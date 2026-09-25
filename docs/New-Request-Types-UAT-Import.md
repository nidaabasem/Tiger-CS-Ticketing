# New Request Types — UAT Import (Business Review)

**Status: UAT preparation only.** Not approved for Production, not merged
automatically. Every row's *Business Decision* is blank in the source, so
nothing here is Production-approved configuration.

Source of truth: [`business-review/TigerCS_New_Request_Types_Business_Review.xlsx`](business-review/TigerCS_New_Request_Types_Business_Review.xlsx)
(35 proposed NEW request types, sheet *New Request Types*, header row 5, data
rows 6–40). Its cell-for-cell TSV export
[`business-review/TigerCS_New_Request_Types_Business_Review.tsv`](business-review/TigerCS_New_Request_Types_Business_Review.tsv)
is embedded in `TigerCS.Infrastructure` and is what the importer reads; a
test re-reads the `.xlsx` and fails if the two ever differ.

## 1. What was built

| Artifact | Purpose |
|---|---|
| `src/TigerCS.Infrastructure/Modules/WorkflowConfiguration/Seed/NewRequestTypesBusinessReview.cs` | The catalog: reads the 35 rows verbatim, normalizes them (throwing on any unknown value), and holds the explicit structured-workflow translation per Request Code. |
| `src/TigerCS.Infrastructure/Modules/WorkflowConfiguration/Seed/NewRequestTypesImporter.cs` | The idempotent, additive, transactional importer. |
| `DevSeedData.SeedNewRequestTypesForReviewAsync` | Fresh (Development) databases. `DevSeedData` only runs when `IsDevelopment()`, so **Production is never seeded**. |
| `ImportNewRequestTypes_UAT.sql` (repo root) | Existing UAT databases. Same rules as the importer; its data block is rendered from the same catalog and a test fails on any drift. Supports a dry run (`@CommitChanges = 0`). |
| `src/TigerCS.Tests/WorkflowConfiguration/Services/NewRequestTypesImportTests.cs` | 16 tests (fidelity, normalization, publish validation, column limits, outcomes, inactivity, no modification, idempotency, SQLite transaction, SQL drift). |

**No migration. No new table. No new status.** `dotnet ef migrations
has-pending-model-changes` → *No changes have been made to the model since
the last migration.*

## 2. Data-model audit and column mapping

| Excel column | TigerCS representation | Support |
|---|---|---|
| Request Code | `Workflows.Code` (unique, 24 chars) of a per-request-type workflow; its version 1 `WorkflowTemplates.Code` too. `RequestTypes` has no code column, so the code is carried by the workflow the request type points at. This is also the idempotency key. | Supported (without schema change) |
| Department | `RequestTypes.DepartmentId`, resolved by `Departments.Code` then exact `Name` (both unique). Never created. | Supported |
| Request Group | No column on `RequestType`. `Category` is the separate intake taxonomy and its link to request types is an open phase-2 decision, so no categories were created. Stored in the workflow description. | **Gap** (documentation only) |
| New Request Type | `RequestTypes.Name` (unique per department) | Supported |
| Business Description | No column on `RequestType`; stored in the workflow description | **Gap** (documentation only) |
| Proposed Workflow | Structured `WorkflowTemplateSteps` (see §4); verbatim text kept in the workflow description | Supported, with gaps in §4 |
| Needs Approval? / Approval Role | `RequestTypeApprovalRequirements` (ApprovalType + TargetKind Role/Department/Employee) | **Not applied** — see §6 |
| Default Priority | `RequestTypes.DefaultPriorityId`; Normal→Medium (the documented `NormalUrgencyPriority` mapping), High→High, Low→Low | Supported |
| First Response SLA | `RequestTypeSlaPolicies.FirstResponseTargetValue` — but one `Unit` per row | **Not stored** — see §7 |
| Resolution SLA | `RequestTypeSlaPolicies` (Days, `ClockBasis = BusinessHours`, `Trigger = TicketCreated`, at the default priority) | Supported where numeric |
| Required Fields | `RequestTypes.RequiredFieldsJson` (provisional JSON array, nothing enforces it yet) | Stored as labels — see §8 |
| Required Documents | Not modeled (attachments are a later increment); stored in the workflow description | **Gap** |
| Allow Transfer? | Department-level only (`DepartmentWorkflowSettings.AllowTransferToOtherDepartments`); no per-request-type flag. All 35 = **Yes**, which matches the existing default (and "no settings row" = allowed), so nothing is changed. | **Gap** (no effect today) |
| Allow Reopen? | `RequestTypes.AllowReopen` — all 35 = Yes → `true` | Supported |
| Business Decision / Comments | All blank → every row imported **inactive** | — |

Not in the workbook, set conservatively and listed for confirmation:
`AllowAgentPriorityChange = false`, `AllowPendingCustomer = false`
(no translated flow has a Pending Customer step), `AllowPendingInternal =
false` (retired capability).

### UAT activation approach

`RequestType.IsActive` exists and ticket creation/classification already
refuse inactive request types. Every imported request type is therefore
created **inactive**: visible in Administration → Request Types for review,
invisible to intake. An approved row is switched on there (an audited
configuration edit). Administration **refuses to activate a request type
whose workflow has no Published version**, so the Draft-workflow rows (§5)
cannot go live until their destination is decided and the workflow is
published in the Workflow Designer. No Draft status was introduced.

## 3. 35/35 reconciliation

Outcomes are for a database shaped like the reference seed (CS, COL, REG, HO,
CC, ACC, **FM**; no Leasing Customer Services). Verified identically through
the C# importer (Development startup) and the SQL script, both on SQL Server
2022. On a real UAT database the outcome depends on which departments exist
there; the script prints its plan before applying and supports a dry run.

**Summary: 20 created (workflow Published) · 6 created with Draft workflow ·
4 not imported (existing request type) · 5 not imported (owning department
missing) = 35.** Without an FM department, the 4 FM rows are also skipped and
`HO-HND-004` becomes Draft (15 / 7 / 4 / 9).

| # | Request Code | Excel Department → TigerCS dept | Request Group | Request Type | Priority (Excel → TigerCS) | Resolution SLA stored | Outcome (reference UAT-like DB) |
|---|---|---|---|---|---|---|---|
| 1 | `CS-GEN-001` | Customer Service → `CS` | General Inquiries | General Inquiry | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |
| 2 | `CS-GEN-002` | Customer Service → `CS` | General Inquiries | Office Hours / Contact Information | Low → Low | — (decision) | Created (inactive, workflow Published) |
| 3 | `CS-GEN-003` | Customer Service → `CS` | General Inquiries | Construction Update | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |
| 4 | `CS-CMP-001` | Customer Service → `CS` | Complaints & Feedback | Complaint | High → High | 2 business day(s) | Created (inactive, workflow Published) |
| 5 | `CS-CMP-002` | Customer Service → `CS` | Complaints & Feedback | Feedback / Suggestion | Low → Low | 1 business day(s) | Created (inactive, workflow Published) |
| 6 | `REG-NOC-001` | Customer Service → `CS` | NOC | NOC for Resale | Normal → Medium | 2 business day(s) | **Not imported** — same name already exists in dept |
| 7 | `REG-NOC-002` | Customer Service → `CS` | NOC | NOC for Golden Visa | Normal → Medium | 2 business day(s) | **Not imported** — same name already exists in dept |
| 8 | `REG-NOC-003` | Customer Service → `CS` | NOC | NOC for Mortgage | Normal → Medium | 2 business day(s) | **Not imported** — same name already exists in dept |
| 9 | `REG-CON-001` | Registration → `REG` | Contracts / DLD | SPA / Contract Inquiry | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |
| 10 | `REG-DLD-001` | Registration → `REG` | Contracts / DLD | Ownership Transfer / Title Deed Inquiry | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |
| 11 | `COL-PAY-001` | Collections → `COL` | Payments & Collection | Payment / Outstanding Balance Inquiry | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |
| 12 | `COL-PAY-002` | Collections → `COL` | Payments & Collection | Returned Cheque | High → High | 1 business day(s) | Created (inactive, workflow Published) |
| 13 | `COL-PAY-003` | Collections → `COL` | Payments & Collection | Cheque Collection | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |
| 14 | `COL-PAY-004` | Collections → `COL` | Payments & Collection | Payment Cheque Inquiry | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |
| 15 | `HO-NOC-001` | Customer Service → `CS` | Handover | NOC for Handover | Normal → Medium | 2 business day(s) | **Not imported** — same name already exists in dept |
| 16 | `HO-HND-001` | Handover → `HO` | Handover | Coordinate Handover | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |
| 17 | `HO-HND-002` | Handover → `HO` | Handover | Schedule Handover Appointment | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |
| 18 | `HO-HND-003` | Handover → `HO` | Handover | Move-In / Move-Out | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |
| 19 | `HO-HND-004` | Handover → `HO` | Handover | Handover Maintenance Follow-up | Normal → Medium | — (decision) | Created (inactive, workflow Published) |
| 20 | `FM-MNT-001` | Facilities Management → `FM` | Maintenance | Repair / Maintenance Request | Normal → Medium | — (decision) | Created (inactive, workflow Published) |
| 21 | `FM-UTL-001` | Facilities Management → `FM` | Maintenance | Utilities Inquiry | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |
| 22 | `FM-COM-001` | Facilities Management → `FM` | Maintenance | Common Area Maintenance | Normal → Medium | — (decision) | Created (inactive, workflow Published) |
| 23 | `FM-SVC-001` | Facilities Management → `FM` | Service Charge | Service Charge Inquiry | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |
| 24 | `LCS-TEN-001` | Leasing Customer Services → `Leasing Customer Services` | Leasing | Tenancy Contract | Normal → Medium | 2 business day(s) | **Not imported** — owning dept missing |
| 25 | `LCS-EJR-001` | Leasing Customer Services → `Leasing Customer Services` | Leasing | Ejari | Normal → Medium | 2 business day(s) | **Not imported** — owning dept missing |
| 26 | `LCS-BKG-001` | Leasing Customer Services → `Leasing Customer Services` | Leasing | Booking | Normal → Medium | 1 business day(s) | **Not imported** — owning dept missing |
| 27 | `LCS-MOV-001` | Leasing Customer Services → `Leasing Customer Services` | Leasing | Move-In / Move-Out | Normal → Medium | 1 business day(s) | **Not imported** — owning dept missing |
| 28 | `LCS-CHK-001` | Leasing Customer Services → `Leasing Customer Services` | Leasing | Rent / DEWA / AC Cheque Inquiry | Normal → Medium | 1 business day(s) | **Not imported** — owning dept missing |
| 29 | `BRK-COM-001` | Customer Service → `CS` | Broker Inquiries | Broker Commission Inquiry | Normal → Medium | 2 business day(s) | Created (inactive, workflow **Draft**) — unresolved: Admin Sales |
| 30 | `BRK-CHK-001` | Customer Service → `CS` | Broker Inquiries | Broker Cheque Collection | Normal → Medium | 1 business day(s) | Created (inactive, workflow **Draft**) — unresolved: Admin Sales |
| 31 | `SAL-INQ-001` | Customer Service → `CS` | Sales | Property Sales Inquiry | Normal → Medium | 1 business day(s) | Created (inactive, workflow **Draft**) — unresolved: Sales |
| 32 | `LEG-INQ-001` | Customer Service → `CS` | Legal | Legal Notice / Permit / Approval Inquiry | High → High | 2 business day(s) | Created (inactive, workflow **Draft**) — unresolved: Legal |
| 33 | `REC-HR-001` | Customer Service → `CS` | Reception | HR Inquiry | Low → Low | 1 business day(s) | Created (inactive, workflow **Draft**) — unresolved: HR |
| 34 | `REC-MKT-001` | Customer Service → `CS` | Reception | Marketing Inquiry | Low → Low | 1 business day(s) | Created (inactive, workflow **Draft**) — unresolved: Marketing |
| 35 | `REC-OTH-001` | Customer Service → `CS` | Reception | Other Reception Inquiry | Normal → Medium | 1 business day(s) | Created (inactive, workflow Published) |

Re-running creates nothing: every created row is recognised by its Request
Code as `AlreadyImported`, even if renamed or activated in the meantime.

## 4. Structured workflows

Each imported row gets its own workflow (code = Request Code) whose version 1
holds the translated steps, validated with the Workflow Designer's own
publish rules (`WorkflowTemplate.Publish`).

**Translation rules** (no semantics invented beyond the mandatory Start step):

| Excel step | Classification | Step kind |
|---|---|---|
| (implicit) | Start | `Created` — every TigerCS workflow must begin with it |
| "CS Queue", "Collections Queue", "CS Intake", "Reception / CS", … | Queue / Department ownership | `Assigned` |
| "Agent", "CS Agent", "Agent/Technician" | Assignment | `Assigned` |
| hand-off to another department (Accounting, Admin Sales, Sales, Legal, HR, Marketing, back to CS, to Handover) | Transfer / Handoff | `Assigned`, with a destination department |
| "Facilities Management if needed" (Handover) | Transfer / Handoff (conditional) | `MaintenanceDependency` (optional) — the existing Handover maintenance concept |
| review / coordinate / follow-up / confirm / process / in progress | Operational/manual | `InProgress` |
| "Escalate if needed" | Operational/manual (optional) | `InProgress` — the existing manual escalation, **not** an approval |
| Resolve, "CS Resolve", "Acknowledge/Resolve" | Resolve | `Resolved` |
| Close | Close | `Closed` |

No `Approval` (`WaitingForApproval`) step was generated: every Approval step
needs a supported approval type, and none fits (§6).

**Engine gaps (kept as documentation, reported):**

1. **A step cannot name a destination department.** Hand-offs are `Assigned`
   steps named after the destination, and the actual move is the existing
   manual Transfer action. The importer uses the destination only to decide
   whether the workflow can be published.
2. **No conditional steps.** "if needed" becomes `IsOptional = true`.
3. **No "Escalate" step kind.** Escalation stays the existing
   `TicketEscalation` feature; the step documents the point in the flow.
4. `LEG-INQ-001` "CS Resolve" after the Legal hand-off implies a return to
   CS that the workbook does not list; it was not invented as a step.
5. Workbook typos were corrected in step names only ("Acounting",
   "CS Agentt", "Cs Agent", "Admin sales"); the verbatim text is preserved in
   the workflow description.

| Request Code | Proposed Workflow (verbatim) | Structured steps (step kind · classification) |
|---|---|---|
| `CS-GEN-001` | CS Queue → Agent → Resolve → Close | Ticket Created `Created`·Start (mandatory) → CS Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `CS-GEN-002` | CS Queue → Agent → Resolve → Close | Ticket Created `Created`·Start (mandatory) → CS Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `CS-GEN-003` | CS Queue → Agent → Obtain update if needed → Resolve → Close | Ticket Created `Created`·Start (mandatory) → CS Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Obtain update if needed (optional) `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `CS-CMP-001` | CS Queue → Agent → Escalate if needed → Resolve → Close | Ticket Created `Created`·Start (mandatory) → CS Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Escalate if needed (optional) `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `CS-CMP-002` | CS Queue → Agent → Record/route → Resolve → Close | Ticket Created `Created`·Start (mandatory) → CS Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Record / route `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REG-NOC-001` | CS Queue → CS Agent →Acounting→CS Agentt → Resolve → Close | Ticket Created `Created`·Start (mandatory) → CS Queue `Assigned`·Queue / Department ownership → CS Agent `Assigned`·Assignment → Transfer to Accounting `Assigned`·Transfer / Handoff ⇒ Accounting → Return to CS Agent `Assigned`·Transfer / Handoff ⇒ Customer Service → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REG-NOC-002` | CS Queue → CS Agent →Acounting→CS Agentt → Resolve → Close | Ticket Created `Created`·Start (mandatory) → CS Queue `Assigned`·Queue / Department ownership → CS Agent `Assigned`·Assignment → Transfer to Accounting `Assigned`·Transfer / Handoff ⇒ Accounting → Return to CS Agent `Assigned`·Transfer / Handoff ⇒ Customer Service → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REG-NOC-003` | CS Queue → CS Agent →Acounting→CS Agentt → Resolve → Close | Ticket Created `Created`·Start (mandatory) → CS Queue `Assigned`·Queue / Department ownership → CS Agent `Assigned`·Assignment → Transfer to Accounting `Assigned`·Transfer / Handoff ⇒ Accounting → Return to CS Agent `Assigned`·Transfer / Handoff ⇒ Customer Service → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REG-CON-001` | Registration Queue → Agent → Review → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Registration Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Review `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REG-DLD-001` | Registration Queue → Agent → Review DLD/registration status → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Registration Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Review DLD / registration status `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `COL-PAY-001` | Collections Queue → Agent → Review account → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Collections Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Review account `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `COL-PAY-002` | Collections Queue → Agent → Follow-up → Escalate if needed → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Collections Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Follow-up `InProgress`·Operational/manual → Escalate if needed (optional) `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `COL-PAY-003` | Collections Queue → Agent → Confirm cheque/collection → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Collections Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Confirm cheque / collection `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `COL-PAY-004` | Collections Queue → Agent → Review → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Collections Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Review `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `HO-NOC-001` | CS Queue →Agent→Acounting→ CS Agent → Handover Agent → Resolve → Close | Ticket Created `Created`·Start (mandatory) → CS Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Transfer to Accounting `Assigned`·Transfer / Handoff ⇒ Accounting → Return to CS Agent `Assigned`·Transfer / Handoff ⇒ Customer Service → Transfer to Handover Agent `Assigned`·Transfer / Handoff ⇒ Handover → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `HO-HND-001` | Handover Queue → Agent → Coordinate → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Handover Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Coordinate `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `HO-HND-002` | Handover Queue → Agent → Confirm available slot → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Handover Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Confirm available slot `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `HO-HND-003` | Handover Queue → Agent → Coordinate requirements → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Handover Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Coordinate requirements `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `HO-HND-004` | Handover Queue → Facilities Management if needed → Follow-up → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Handover Queue `Assigned`·Queue / Department ownership → Facilities Management if needed (optional) `MaintenanceDependency`·Transfer / Handoff ⇒ Facilities Management → Follow-up `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `FM-MNT-001` | FM Queue → Assign Agent/Technician → In Progress → Resolve → Close | Ticket Created `Created`·Start (mandatory) → FM Queue `Assigned`·Queue / Department ownership → Assign Agent / Technician `Assigned`·Assignment → In Progress `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `FM-UTL-001` | FM Queue → Agent → Review/coordinate → Resolve → Close | Ticket Created `Created`·Start (mandatory) → FM Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Review / coordinate `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `FM-COM-001` | FM Queue → Agent/Technician → In Progress → Resolve → Close | Ticket Created `Created`·Start (mandatory) → FM Queue `Assigned`·Queue / Department ownership → Agent / Technician `Assigned`·Assignment → In Progress `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `FM-SVC-001` | FM/Responsible Finance Queue → Agent → Review → Resolve → Close | Ticket Created `Created`·Start (mandatory) → FM / Responsible Finance Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Review `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `LCS-TEN-001` | Leasing CS Queue → Agent → Process/Review → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Leasing CS Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Process / review `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `LCS-EJR-001` | Leasing CS Queue → Agent → Process/Review → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Leasing CS Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Process / review `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `LCS-BKG-001` | Leasing CS Queue → Agent → Confirm booking/details → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Leasing CS Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Confirm booking / details `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `LCS-MOV-001` | Leasing CS Queue → Agent → Coordinate → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Leasing CS Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Coordinate `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `LCS-CHK-001` | Leasing CS Queue → Agent → Review → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Leasing CS Queue `Assigned`·Queue / Department ownership → Agent `Assigned`·Assignment → Review `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `BRK-COM-001` | CS Queue →Cs Agent  → Admin sales →Review → Resolve → Close | Ticket Created `Created`·Start (mandatory) → CS Queue `Assigned`·Queue / Department ownership → CS Agent `Assigned`·Assignment → Transfer to Admin Sales `Assigned`·Transfer / Handoff ⇒ Admin Sales → Review `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `BRK-CHK-001` | CS Queue →Cs Agent  → Admin sales → Confirm collection → Resolve → Close | Ticket Created `Created`·Start (mandatory) → CS Queue `Assigned`·Queue / Department ownership → CS Agent `Assigned`·Assignment → Transfer to Admin Sales `Assigned`·Transfer / Handoff ⇒ Admin Sales → Confirm collection `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `SAL-INQ-001` | CS / Call Center → Sales Handoff → Follow-up → Resolve / Close | Ticket Created `Created`·Start (mandatory) → CS / Call Center Queue `Assigned`·Queue / Department ownership → Sales Handoff `Assigned`·Transfer / Handoff ⇒ Sales → Follow-up `InProgress`·Operational/manual → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `LEG-INQ-001` | CS Intake → Legal Handoff → Legal Review/Response → CS Resolve → Close | Ticket Created `Created`·Start (mandatory) → CS Intake `Assigned`·Queue / Department ownership → Legal Handoff `Assigned`·Transfer / Handoff ⇒ Legal → Legal Review / Response `InProgress`·Operational/manual → CS Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REC-HR-001` | Reception / CS → HR Handoff → Acknowledge/Resolve → Close | Ticket Created `Created`·Start (mandatory) → Reception / CS `Assigned`·Queue / Department ownership → HR Handoff `Assigned`·Transfer / Handoff ⇒ HR → Acknowledge / Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REC-MKT-001` | Reception / CS → Marketing Handoff → Acknowledge/Resolve → Close | Ticket Created `Created`·Start (mandatory) → Reception / CS `Assigned`·Queue / Department ownership → Marketing Handoff `Assigned`·Transfer / Handoff ⇒ Marketing → Acknowledge / Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REC-OTH-001` | Reception / CS → Route to Responsible Department → Resolve → Close | Ticket Created `Created`·Start (mandatory) → Reception / CS `Assigned`·Queue / Department ownership → Route to Responsible Department `Assigned`·Transfer / Handoff → Resolve `Resolved`·Resolve → Close `Closed`·Close |

## 5. Unresolved workflow destinations and departments

| Department | Referenced by | Exists? | Effect |
|---|---|---|---|
| Customer Service (`CS`), Registration (`REG`), Collections (`COL`), Handover (`HO`) | owning | Yes (reference seed) | — |
| Accounting (`ACC`) | NOC hand-offs | Yes (reference seed) | — (the NOC rows are not imported for another reason, §3) |
| Facilities Management (`FM`) | owning (4 rows); `HO-HND-004` destination | **Only in the development seed** (`DevSeedData`); not in `WorkflowReferenceData` | Confirm it exists in UAT. If missing: FM rows skipped, `HO-HND-004` Draft |
| **Leasing Customer Services** | owning (5 `LCS-*` rows) | **No** — in no seed or script | 5 rows **not imported** |
| **Admin Sales** | `BRK-COM-001`, `BRK-CHK-001` | **No** | Draft workflow |
| **Sales** | `SAL-INQ-001` | **No** | Draft workflow |
| **Legal** | `LEG-INQ-001` (also its approver) | **No** | Draft workflow |
| **HR** | `REC-HR-001` | **No** | Draft workflow |
| **Marketing** | `REC-MKT-001` | **No** | Draft workflow |
| Finance ("Responsible Finance") | `FM-SVC-001` queue wording | `FIN` only in the development seed | Modeled as the FM queue; ownership needs confirming |
| Reception | `REC-*` intake wording | Not a department | Modeled as the CS queue (owning department is CS) |

None of these was created. Departments with no known code resolve by exact
name only (Leasing Customer Services, Sales, Admin Sales, Legal, HR,
Marketing); if UAT already holds one under a different name, tell us the
name/code and the catalog can map it.

## 6. Approval mapping (10 Conditional rows) — **Approval Mapping Required**

The supported model: `ApprovalType` ∈ {AccountingApproval,
CustomerServiceApproval, ReopenApproval}; `TargetKind` ∈ {Role, Department
(optionally narrowed to a role), Employee}; roles are the fixed set
(CS Agent, CS Supervisor, Department Employee, Department Head, CS Manager,
General Manager, Chairman/CEO, System Administrator, Reporting User).
**No approval requirement was created.** `ReopenApproval` was not touched.

| Request Code | Request Type | Approval Role (Excel) | Exact match? | Proposed mapping (needs confirmation) | Blocker |
|---|---|---|---|---|---|
| `CS-CMP-001` | Complaint | CS Supervisor / Manager | No ("/" = either) | `CustomerServiceApproval`, Role = **CS Supervisor** (the existing provisional CS approver) or CS Manager | Which role; "Conditional" has no model (a requirement applies to every ticket) |
| `REG-NOC-001` | NOC for Resale | Registration Supervisor / Authorized Approver | No | Department = `REG`, narrowed to **Department Head** | No approval type fits (not Accounting/CS approval); row not imported (§3) |
| `REG-NOC-002` | NOC for Golden Visa | Registration Supervisor / Authorized Approver | No | as above | as above |
| `REG-NOC-003` | NOC for Mortgage | Registration Supervisor / Authorized Approver | No | as above | as above |
| `COL-PAY-002` | Returned Cheque | Collections Supervisor / Manager | No | Department = `COL`, narrowed to **Department Head** | No approval type fits |
| `HO-NOC-001` | NOC for Handover | Handover Supervisor / Authorized Approver | No | Department = `HO`, narrowed to **Department Head** | No approval type fits; row not imported (§3) |
| `LCS-TEN-001` | Tenancy Contract | Leasing Supervisor / Authorized Approver | No | Department = Leasing, narrowed to Department Head | Department missing; no approval type fits |
| `LCS-EJR-001` | Ejari | Leasing Supervisor / Authorized Approver | No | as above | as above |
| `BRK-COM-001` | Broker Commission Inquiry | Responsible Manager | No | none — which manager? | Unmappable without a named role/department |
| `LEG-INQ-001` | Legal Notice / Permit / Approval Inquiry | Legal / Authorized Approver | No | Department = Legal | Department missing; no approval type fits |

"Supervisor" has no per-department role in TigerCS; departments' head role is
**Department Head** (`DepartmentWorkflowSettings.HeadRoleName`), which is why
it is the proposal. "Authorized Approver" would be an `Employee` target
naming a specific person. Implementing any of the department approvals needs
a new, additive `ApprovalType` value (the model's stated extension path),
which is a code change to agree first.

## 7. SLA mapping

| Excel value | Rows | Normalized | Stored |
|---|---|---|---|
| Resolution "1 business day" | 22 | 1 day, business-hours clock | `Unit = Days`, `ResolutionTargetValue = 1`, `ClockBasis = BusinessHours` |
| Resolution "2 business days" | 9 | 2 days, business-hours clock | `ResolutionTargetValue = 2` |
| Resolution "Same business day" | 1 (`CS-GEN-002`) | **not numeric** | no SLA row — **policy decision** |
| Resolution "Based on severity" / "Based on issue severity" | 3 (`FM-MNT-001`, `FM-COM-001`, `HO-HND-004`) | **not numeric** | no SLA row — **policy decision** |
| First Response "2 business hours" / "4 business hours" | 6 / 29 | 2 h / 4 h | **not stored** — see below |

Rows are keyed by (request type, **default priority**), trigger
`TicketCreated`, pause flags and warning threshold left null (pending
decisions, as in the existing seed). `RequestTypeSlaPolicies` is still
configuration only; due dates keep using the per-priority `SlaPolicies`
until phase 4.

**First Response gap.** A `RequestTypeSlaPolicy` row has a single
`SlaDurationUnit` for both first response and resolution; the workbook
mixes business hours with business days. Resolution is stored exactly;
storing first response would mean converting "1 business day" to 10 hours
under the current Sat–Thu 08:00–18:00 calendar, which ties configuration to
the calendar's day length. Options for the business: (a) accept resolution in
business hours (1 day = 10 h, 2 days = 20 h) and store both; (b) add
per-deadline units (schema change); (c) leave first response to the
per-priority policy.

| Request Code | First Response (Excel) | Resolution (Excel) | Stored |
| `CS-GEN-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `CS-GEN-002` | 4 business hours | Same business day | no SLA row |
| `CS-GEN-003` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `CS-CMP-001` | 2 business hours | 2 business days | Days=2, BusinessHours, TicketCreated, priority High |
| `CS-CMP-002` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Low |
| `REG-NOC-001` | 4 business hours | 2 business days | Days=2, BusinessHours, TicketCreated, priority Medium |
| `REG-NOC-002` | 4 business hours | 2 business days | Days=2, BusinessHours, TicketCreated, priority Medium |
| `REG-NOC-003` | 4 business hours | 2 business days | Days=2, BusinessHours, TicketCreated, priority Medium |
| `REG-CON-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `REG-DLD-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `COL-PAY-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `COL-PAY-002` | 2 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority High |
| `COL-PAY-003` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `COL-PAY-004` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `HO-NOC-001` | 4 business hours | 2 business days | Days=2, BusinessHours, TicketCreated, priority Medium |
| `HO-HND-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `HO-HND-002` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `HO-HND-003` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `HO-HND-004` | 4 business hours | Based on issue severity | no SLA row |
| `FM-MNT-001` | 2 business hours | Based on severity | no SLA row |
| `FM-UTL-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `FM-COM-001` | 2 business hours | Based on severity | no SLA row |
| `FM-SVC-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `LCS-TEN-001` | 4 business hours | 2 business days | Days=2, BusinessHours, TicketCreated, priority Medium |
| `LCS-EJR-001` | 4 business hours | 2 business days | Days=2, BusinessHours, TicketCreated, priority Medium |
| `LCS-BKG-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `LCS-MOV-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `LCS-CHK-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `BRK-COM-001` | 4 business hours | 2 business days | Days=2, BusinessHours, TicketCreated, priority Medium |
| `BRK-CHK-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `SAL-INQ-001` | 2 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |
| `LEG-INQ-001` | 2 business hours | 2 business days | Days=2, BusinessHours, TicketCreated, priority High |
| `REC-HR-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Low |
| `REC-MKT-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Low |
| `REC-OTH-001` | 4 business hours | 1 business day | Days=1, BusinessHours, TicketCreated, priority Medium |

## 8. Required Fields / Documents

* **Required Fields — partially supported.** `RequiredFieldsJson` exists as a
  *provisional* JSON array of intake field keys; nothing validates or
  enforces it yet and there is no field-key catalog. The workbook's labels
  are stored verbatim as the array (e.g. `["Customer","Project/Unit if applicable","Description"]`),
  including conditional wording ("if applicable"). When the required-fields
  feature defines field keys, these labels need mapping to them.
* **Required Documents — not supported.** No per-request-type document
  model exists (attachments are a later increment). Kept in the workflow
  description and below; no documents engine was added.

| Request Code | Required Fields (Excel) → RequiredFieldsJson | Required Documents (Excel; not modeled) |
| `CS-GEN-001` | Customer, Project/Unit if applicable, Description → `["Customer","Project/Unit if applicable","Description"]` | None |
| `CS-GEN-002` | Customer/Contact, Description → `["Customer/Contact","Description"]` | None |
| `CS-GEN-003` | Customer, Project, Unit if applicable → `["Customer","Project","Unit if applicable"]` | None |
| `CS-CMP-001` | Customer, Project/Unit, Complaint details → `["Customer","Project/Unit","Complaint details"]` | Supporting evidence if available |
| `CS-CMP-002` | Customer, Feedback details → `["Customer","Feedback details"]` | None |
| `REG-NOC-001` | Customer, Project, Unit, Request details → `["Customer","Project","Unit","Request details"]` | Required resale NOC documents |
| `REG-NOC-002` | Customer, Project, Unit → `["Customer","Project","Unit"]` | Required Golden Visa/NOC documents |
| `REG-NOC-003` | Customer, Project, Unit → `["Customer","Project","Unit"]` | Required mortgage/NOC documents |
| `REG-CON-001` | Customer, Project, Unit, Contract reference → `["Customer","Project","Unit","Contract reference"]` | Contract if needed |
| `REG-DLD-001` | Customer, Project, Unit → `["Customer","Project","Unit"]` | Relevant ownership/title documents if needed |
| `COL-PAY-001` | Customer, Project, Unit → `["Customer","Project","Unit"]` | Payment proof if applicable |
| `COL-PAY-002` | Customer, Project, Unit, Cheque details → `["Customer","Project","Unit","Cheque details"]` | Cheque copy / bank return document |
| `COL-PAY-003` | Customer, Project, Unit, Cheque details → `["Customer","Project","Unit","Cheque details"]` | Cheque details if applicable |
| `COL-PAY-004` | Customer, Project, Unit, Cheque details → `["Customer","Project","Unit","Cheque details"]` | Cheque copy if required |
| `HO-NOC-001` | Customer, Project, Unit → `["Customer","Project","Unit"]` | Required clearance/NOC documents |
| `HO-HND-001` | Customer, Project, Unit → `["Customer","Project","Unit"]` | Handover documents if applicable |
| `HO-HND-002` | Customer, Project, Unit, Preferred date/time → `["Customer","Project","Unit","Preferred date/time"]` | None |
| `HO-HND-003` | Customer, Project, Unit, Move date → `["Customer","Project","Unit","Move date"]` | Required move-in/out documents if applicable |
| `HO-HND-004` | Customer, Project, Unit, Issue details → `["Customer","Project","Unit","Issue details"]` | Photos/supporting evidence if available |
| `FM-MNT-001` | Customer, Project, Unit, Issue type, Description → `["Customer","Project","Unit","Issue type","Description"]` | Photos if available |
| `FM-UTL-001` | Customer, Project, Unit, Utility type → `["Customer","Project","Unit","Utility type"]` | Supporting document if applicable |
| `FM-COM-001` | Project/Building, Location, Issue details → `["Project/Building","Location","Issue details"]` | Photos if available |
| `FM-SVC-001` | Customer, Project, Unit → `["Customer","Project","Unit"]` | Statement/invoice if applicable |
| `LCS-TEN-001` | Customer/Tenant, Property/Unit → `["Customer/Tenant","Property/Unit"]` | Required tenancy documents |
| `LCS-EJR-001` | Customer/Tenant, Property/Unit → `["Customer/Tenant","Property/Unit"]` | Required Ejari documents |
| `LCS-BKG-001` | Customer/Tenant, Property/Unit → `["Customer/Tenant","Property/Unit"]` | Booking document if applicable |
| `LCS-MOV-001` | Customer/Tenant, Property/Unit, Move date → `["Customer/Tenant","Property/Unit","Move date"]` | Required move-in/out documents if applicable |
| `LCS-CHK-001` | Customer/Tenant, Property/Unit, Cheque type/details → `["Customer/Tenant","Property/Unit","Cheque type/details"]` | Cheque copy if applicable |
| `BRK-COM-001` | Broker, Project/Unit, Deal reference → `["Broker","Project/Unit","Deal reference"]` | Supporting commission documents if needed |
| `BRK-CHK-001` | Broker, Deal reference, Cheque details → `["Broker","Deal reference","Cheque details"]` | Supporting document if needed |
| `SAL-INQ-001` | Customer, Contact details, Interested project/unit if known → `["Customer","Contact details","Interested project/unit if known"]` | None |
| `LEG-INQ-001` | Customer, Project/Unit, Legal request details → `["Customer","Project/Unit","Legal request details"]` | Legal notice / relevant documents |
| `REC-HR-001` | Requester details, Description → `["Requester details","Description"]` | None |
| `REC-MKT-001` | Requester details, Description → `["Requester details","Description"]` | None |
| `REC-OTH-001` | Requester details, Description → `["Requester details","Description"]` | None |

## 9. Business decisions required

1. **Business Decision for all 35 rows** (blank) — only approved rows get activated.
2. **NOC rows `REG-NOC-001/002/003`, `HO-NOC-001`**: Customer Service already has
   *NOC for Resale / Golden Visa / Mortgage / Handover* (seeded, active, with
   their own SLA rows). Are these the same request types (then they are not
   new, and any change to the existing ones is a separate, deliberate edit),
   or distinct ones needing different names?
3. **`CS-CMP-001` Complaint** vs existing **Complaint Handling** (CS) — duplicate?
4. **Leasing Customer Services** — create the department (name/code)? Its 5 rows wait on it.
5. **Destinations Sales, Admin Sales, Legal, HR, Marketing** — departments in
   TigerCS, or hand-offs outside the system? Their 6 workflows stay Draft until decided.
6. **Facilities Management** — confirm it exists in UAT (code `FM` / name).
7. **`FM-SVC-001`** — owned by FM or by Finance ("FM/Responsible Finance Queue")?
8. **Reception** — confirm it is CS intake, not a department.
9. **Approvals (§6)** — roles, "Conditional" semantics, and whether a new approval type may be added.
10. **SLA (§7)** — "Same business day", "Based on severity", and first-response storage.
11. **Flags not in the workbook** — `AllowAgentPriorityChange` (relevant to the severity-based rows) and `AllowPendingCustomer` (rows that wait on customer documents) are set to `false`.
12. **ReopenApproval** — `ConfigureReopenApprovalRequirements.sql` covers *active*
    reopenable request types; re-run it after activation if the approved
    rule should apply to these too. This import does not touch ReopenApproval.
13. **Request Group** — should the groups become intake Categories?

## 10. Running it

* **Fresh / Development database:** automatic on startup (Development only).
* **Existing UAT database:** apply migrations, then
  `sqlcmd -S <server> -d <database> -U <user> -P <password> -C -b -I -i ImportNewRequestTypes_UAT.sql`
  (first with `@CommitChanges = 0` for a dry run). Re-running is safe.
* **Production:** do not run.
