# Genesys tickets routed to Finance on UAT — investigation and correction

## Finding (code review of the deployed build and this release)

The deployed UAT build is commit `004dff9` (the `publish/` API assembly's
informational version). In that build and in this release the department of
a Genesys-created ticket is resolved by `GenesysInquiryIngestionAppService`
**only** from the request the Genesys Data Action sent, in this order:

1. `departmentId` — explicit (website chat picker). A wrong/inactive id is
   refused `422`; it never falls through.
2. `departmentCode` — explicit (IVR choice `Task.TcsDepartmentCode`, or the
   Messenger custom attribute `department` in the live-chat flow). Matched
   case-insensitively against active department codes; no match → `422`.
3. The active `GenesysQueueMappings` row for `queueId` → its department.
   No mapping → `422` "Genesys queue '…' has no active department mapping".

There is **no default department, no Finance fallback and no `Genesys:*`
configuration key** that names a department (`GenesysOptions` has only
`Enabled` and `ScreenPopWebBaseUrl`; `appsettings*.json` in `src/` and
`publish/` are identical on this point). No SQL script, migration or seed
in the repository inserts a queue mapping, and no UAT script creates the
Finance department at all (it exists only in the Development seed, where it
is **DepartmentId 3**). The ticket number prefix is simply
`TG-{Department.Code}-` of the resolved department, fixed at creation.

Therefore a `TG-FIN` ticket created from Genesys means one of these UAT
environment facts, none of which is in the code:

| Cause | How it shows up | Fix (configuration, not code) |
|---|---|---|
| A queue mapping points at Finance (e.g. every test queue mapped to one department while setting up) | Query 1 lists a mapping whose `DepartmentCode = FIN`; Query 2 shows `QueueMappedToToday = FIN` for the tickets | Re-point the mapping: Admin → Genesys routing, `PUT /api/admin/genesys/queue-mappings/{id}` to the intended department (CS / FM / LCS) |
| The flow sends `departmentCode = "FIN"` (IVR default, or the website Messenger `department` attribute) | Query 3 "Decision" = explicit, or (new build) `DepartmentSource=ExplicitDepartmentCode;ReceivedDepartmentCode=FIN` | Fix the Architect flow / website attribute to send the intended code (`CS`, `FM`, `LCS`) or nothing |
| The flow sends `departmentId` = Finance's id on UAT (a picker built against Development ids, where Leasing/Maintenance/CS ≠ UAT ids) | `DepartmentSource=ExplicitDepartmentId;ReceivedDepartmentId=<Finance id>` | Send `departmentCode` instead of numeric ids — ids differ per environment; the shipped data action already supports `departmentCode` |

The reported subject `TH_CS_Leasing_WM` is whatever the flow passed as
`subject`; if it equals the queue name, the flow maps `subject ← queue name`
and the ticket's intended department is Leasing Customer Services (`LCS`).

## What this release changes in code

Nothing in the routing rule. `Finance must not become a generic fallback`
and `missing routing returns the existing validation error` already hold
and are pinned by tests (`GenesysCreateTicketOptionalFieldsTests`,
`GenesysInquiryIngestionAppServiceTests`). What is added is traceability:
the `GenesysInquiryIngested` audit entry now records
`DepartmentSource=ExplicitDepartmentId|ExplicitDepartmentCode|QueueMapping`,
`ReceivedDepartmentId`, `ReceivedDepartmentCode`, `QueueId`, `QueueName` and
the resolved `DepartmentCode`, so the next misrouted ticket names its cause
without guessing.

## Steps on UAT

1. Run `01_Investigate_Finance_Routing_UAT.sql` (read-only) and keep the
   output with the release notes.
2. Fix the cause found (table above). Confirm with one new test chat/call
   that the audit entry shows the intended `DepartmentSource` and department.
3. Build the reviewed correction list: from Query 2, one row per ticket that
   is genuinely misrouted, with the intended `TargetDepartmentId` and a
   reason. Leave intentional Finance tickets out.
4. Dry run: `02_Correct_Misrouted_Tickets_UAT.ps1 -ApiBaseUrl … -Username <CS Manager> -CsvPath reviewed.csv -WhatIf`.
5. Run it without `-WhatIf`. Each ticket is moved through the standard
   transfer (audited, concurrency-checked, `OriginatingDepartmentId`
   untouched, history preserved, ticket number unchanged). Closed tickets are
   refused by the API and listed in the result file.
