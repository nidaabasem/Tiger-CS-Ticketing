# Request-Type Catalog Import

Imports the 35 request types in `docs/imports/TigerCS_New_Request_Types.xlsx`
(Sheet2) into the existing **Department → Request Type → Workflow → SLA**
configuration. Workbook values are either mapped to something that already
exists in TigerCS or recorded as a business decision. The import does not
guess a value, and it does not add a ticket status, step kind, approval type,
role or department.

## Pieces

| Piece | Where |
|---|---|
| Source rows (verbatim JSON extract of Sheet2, embedded resource) | `src/TigerCS.Infrastructure/Modules/WorkflowConfiguration/Import/Data/TigerCS_New_Request_Types.json` |
| Mapping rules (pure, no database) | `RequestTypeCatalogMapper` |
| Idempotent import + report | `RequestTypeCatalogImporter`, `RequestTypeCatalogImportReport` |
| Hand-run command | `RequestTypeCatalogCommand` (wired into `TigerCS.Api` behind `--import-request-types`) |
| Schema migration | `20260928085727_AddRequestTypeCatalogImport` (idempotent SQL: `AddRequestTypeCatalogImport.sql`) |
| Tests | `src/TigerCS.Tests/WorkflowConfiguration/Import/*`, `.../Domain/RequestTypeCatalogDomainTests.cs` |

## Schema change (additive only, reversible)

* `RequestTypes.Code` stores the workbook's **Request Code**. It is nullable
  and unique when present, through a filtered index. Every re-import keys on it.
* `RequestTypes.RequestGroup`, `Description` and `RequiredDocumentsJson` hold
  the workbook's own wording. They are informational only, because
  attachments are not modeled yet.
* `RequestTypeSlaPolicies.FirstResponseUnit` covers "4 business *hours*"
  first response beside a "1 business *day*" resolution. Null (every existing
  row) means the row's `Unit`, exactly as before.
* `WorkflowTemplateSteps.DepartmentId` is the department a Department Queue /
  Assignment step routes to (a handoff or a return to Customer Service). It
  is null when the target is unconfirmed. It is a stored definition only;
  see "Stored definitions vs executable routing" below.

## Stored definitions vs executable routing

| | Stored workflow definition (what the import writes) | Executable routing (what moves a ticket) |
|---|---|---|
| What | Workflow version, its steps, each handoff step's `DepartmentId` | The existing **Transfer** action (`POST /api/tickets/{id}/transfer`) |
| Who | Administration / the import | **CS Manager only** (`TicketRoleSets.Transfer`, unchanged) |
| Limits | None at runtime | The source department's `AllowTransferToOtherDepartments` |
| Read at runtime? | **No.** No application service or controller reads workflow steps or their departments. `CatalogHandoffRuntimeTests.No_runtime_code_reads_a_steps_department` checks this by scanning the IL, with a positive control. | Yes |

At runtime a workflow version contributes only its capability flags (Pending
Customer, approval) through `WorkflowCapabilities`. The same holds for
imported **request-type SLA rows**: live due dates still come from the
per-priority `SlaPolicies`. `CatalogHandoffRuntimeTests` demonstrates this
end to end through the real Api:
- The ticket stays in Customer Service even though its pinned version names Handover.
- CS Agent, CS Supervisor, Department Head and Department Employee get 403.
- A CS Manager hands the ticket to Handover and then returns it to Customer Service, with both moves audited as `Transfer`.
- A transfer to a department the definition never names also succeeds.
- The department transfer setting blocks the return even though the definition lists it.

## Mapping rules

| Workbook column | Maps to |
|---|---|
| Request Code | `RequestTypes.Code` (the identity) and workflow code `RT-<code>` |
| Department | Existing department **by name**. A missing department is never created; its rows are skipped and a later re-run picks them up. |
| Default Priority | Low → Low, **Normal → Medium** (the documented provisional mapping), High → High |
| First Response / Resolution SLA | `n business hours/days` → one SLA row at the default priority, trigger TicketCreated, clock basis BusinessHours. Anything else creates no SLA row and becomes a decision. |
| Required Fields / Documents | JSON array of the workbook's wording. "None" is stored as null. Neither is enforced yet. |
| Needs Approval? / Approval Role | "No" creates nothing. "Conditional" becomes a decision, and no approval requirement is created. |
| Allow Transfer? | Checked against the department's existing transfer setting. "No" becomes a decision. |
| Allow Reopen? | `AllowReopen`, plus the approved Reopen Approval rule (CS Manager, non-blocking) |
| Proposed Workflow | Stored steps (see above). Unconfirmed handoff targets keep the step with no department and become a decision. |
| Business Decision / Comments | Any value becomes a decision to review. The column is never interpreted. |

### Settings the workbook does not give

A missing column never switches existing behaviour off.

| Setting | Created request types get | Why |
|---|---|---|
| Pending Customer | **Allowed** (request type and workflow) | Inherits the established behaviour: `TicketLifecycleAppService` allows Pending Customer unless a request type restricts it. |
| Agent priority change | Stored as **allowed** and **flagged** (Configuration decision) | No default exists to inherit: it is a required value set individually on every existing type. Nothing enforces it today, so agents choose any priority. Once the business answers, `--agent-priority-change allow\|deny` records it. |
| Assignment rule | None, so the department queue is used | The existing fallback |
| SLA clock start / pause | TicketCreated / "not decided" | The existing defaults |

## Outcomes

* **Created: inactive draft.** This is the default. The request type and workflow are inactive and v1 is an unpublished Draft.
* **Created: active.** This happens only with `--activate-resolved`, and only for a row with no open decision, including the priority-change one.
* **Existing: left unchanged.** A request type with the same department and name exists. Nothing about it is written, and the report shows a current-vs-workbook comparison (SLAs, approvals, workflow steps, Pending Customer, priority change, reopen).
* **Existing: code linked only.** This happens only with `--link-existing`. The code is attached and nothing else changes.
* **Already imported.** The code already exists, so the row is left as it is. Administration edits are never overwritten, even by a run that asks for activation.
* **Skipped.** The department does not exist.

## Running it

The import never runs at startup. It uses the environment's normal
configuration (`ConnectionStrings:TigerCsDatabase`), so check which database
that points at first. The command must be run from a machine that can reach
that database.

```bash
# 1. Dry run. This works BEFORE the migration: it reads existing columns only and writes nothing.
dotnet TigerCS.Api.dll --import-request-types --report catalog-dry-run.md
# 2. Schema: review AddRequestTypeCatalogImport.sql, then apply it the usual way.
# 3. First import: every created type stays inactive, and existing types are untouched.
dotnet TigerCS.Api.dll --import-request-types --apply --keep-all-inactive --report catalog-applied.md
```

`--apply` is refused unless it names an activation choice
(`--keep-all-inactive` or `--activate-resolved`), and it is refused before the
migration. It writes in a single transaction. Re-running with the same
switches creates and changes nothing.

## Dry-run snapshot (pre-migration)

This was generated before the migration, against the reference seed
(Customer Service, Collections, Registration, Handover, Call Center,
provisional Accounting and their request types) **plus** Facilities
Management and Leasing Customer Services. Without those two departments,
their 9 rows are "Skipped" (22 created, 4 existing, 9 skipped). The
existing-type comparison shows reference-seed values. **The dry run against
UAT is the authoritative report.**

The AddRequestTypeCatalogImport migration is **not applied** to this database: this dry run read existing columns only, so no row can show as already imported.

- Activation: **off** — every created request type and workflow is an inactive draft.
- Existing request types: **left completely unchanged** (not even the catalog code is written).
- Agent priority change on created types: open decision (stored as allowed, matching today's unenforced behaviour).

| Outcome | Rows |
|---|---|
| Created — active | 0 |
| Created — inactive draft | 31 |
| Existing — left unchanged | 4 |
| Existing — code linked only | 0 |
| Already imported (unchanged) | 0 |
| Skipped | 0 |
| **Total** | **35** |

### Rows

| Code | Request type | Department | Priority | SLA (first response / resolution) | Outcome | Workbook mapping | Open decisions |
|---|---|---|---|---|---|---|---|
| CS-GEN-001 | General Inquiry | Customer Service | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| CS-GEN-002 | Office Hours / Contact Information | Customer Service | Low | — | Created — inactive draft | open | 2 |
| CS-GEN-003 | Construction Update | Customer Service | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| CS-CMP-001 | Complaint | Customer Service | High | 2 bh / 2 bd | Created — inactive draft | open | 2 |
| CS-CMP-002 | Feedback / Suggestion | Customer Service | Low | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| REG-NOC-001 | NOC for Resale | Customer Service | Medium | 4 bh / 2 bd | Existing — left unchanged | open | 3 |
| REG-NOC-002 | NOC for Golden Visa | Customer Service | Medium | 4 bh / 2 bd | Existing — left unchanged | open | 3 |
| REG-NOC-003 | NOC for Mortgage | Customer Service | Medium | 4 bh / 2 bd | Existing — left unchanged | open | 3 |
| REG-CON-001 | SPA / Contract Inquiry | Registration | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| REG-DLD-001 | Ownership Transfer / Title Deed Inquiry | Registration | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| COL-PAY-001 | Payment / Outstanding Balance Inquiry | Collections | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| COL-PAY-002 | Returned Cheque | Collections | High | 2 bh / 1 bd | Created — inactive draft | open | 2 |
| COL-PAY-003 | Cheque Collection | Collections | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| COL-PAY-004 | Payment Cheque Inquiry | Collections | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| HO-NOC-001 | NOC for Handover | Customer Service | Medium | 4 bh / 2 bd | Existing — left unchanged | open | 3 |
| HO-HND-001 | Coordinate Handover | Handover | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| HO-HND-002 | Schedule Handover Appointment | Handover | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| HO-HND-003 | Move-In / Move-Out | Handover | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| HO-HND-004 | Handover Maintenance Follow-up | Handover | Medium | — | Created — inactive draft | open | 2 |
| FM-MNT-001 | Repair / Maintenance Request | Facilities Management | Medium | — | Created — inactive draft | open | 2 |
| FM-UTL-001 | Utilities Inquiry | Facilities Management | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| FM-COM-001 | Common Area Maintenance | Facilities Management | Medium | — | Created — inactive draft | open | 2 |
| FM-SVC-001 | Service Charge Inquiry | Facilities Management | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| LCS-TEN-001 | Tenancy Contract | Leasing Customer Services | Medium | 4 bh / 2 bd | Created — inactive draft | open | 2 |
| LCS-EJR-001 | Ejari | Leasing Customer Services | Medium | 4 bh / 2 bd | Created — inactive draft | open | 2 |
| LCS-BKG-001 | Booking | Leasing Customer Services | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| LCS-MOV-001 | Move-In / Move-Out | Leasing Customer Services | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| LCS-CHK-001 | Rent / DEWA / AC Cheque Inquiry | Leasing Customer Services | Medium | 4 bh / 1 bd | Created — inactive draft | resolved | 1 |
| BRK-COM-001 | Broker Commission Inquiry | Customer Service | Medium | 4 bh / 2 bd | Created — inactive draft | open | 3 |
| BRK-CHK-001 | Broker Cheque Collection | Customer Service | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| SAL-INQ-001 | Property Sales Inquiry | Customer Service | Medium | 2 bh / 1 bd | Created — inactive draft | open | 2 |
| LEG-INQ-001 | Legal Notice / Permit / Approval Inquiry | Customer Service | High | 2 bh / 2 bd | Created — inactive draft | open | 3 |
| REC-HR-001 | HR Inquiry | Customer Service | Low | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| REC-MKT-001 | Marketing Inquiry | Customer Service | Low | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| REC-OTH-001 | Other Reception Inquiry | Customer Service | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |

### Settings the workbook does not give

- **Pending Customer**: inherited — allowed on the request type and its workflow, the established behaviour whenever a request type does not restrict it.
- **Agent priority change**: no default exists to inherit (a required per-request-type value) — see the Configuration decision below.
- **Assignment**: no rule is created, so tickets use the department queue — the existing fallback.
- **SLA clock start**: TicketCreated, the existing default; pause-on-Pending stays "not decided" as on every other type.

### Workflow definitions (stored, not executed)

Steps — including each handoff's department — are stored configuration. No runtime code reads them: a ticket changes department only through the existing Transfer action (CS Manager only, subject to the source department's transfer setting), and nothing moves a ticket automatically.

- **CS-GEN-001**: Ticket Created → Customer Service Queue → Customer Service Agent → Resolve → Close
- **CS-GEN-002**: Ticket Created → Customer Service Queue → Customer Service Agent → Resolve → Close
- **CS-GEN-003**: Ticket Created → Customer Service Queue → Customer Service Agent — Obtain update if needed → Resolve → Close
- **CS-CMP-001**: Ticket Created → Customer Service Queue → Customer Service Agent — Escalate if needed → Resolve → Close
- **CS-CMP-002**: Ticket Created → Customer Service Queue → Customer Service Agent — Record/route → Resolve → Close
- **REG-NOC-001**: Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Accounting [target unconfirmed] → Return to Customer Service → Customer Service Agent → Resolve → Close
- **REG-NOC-002**: Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Accounting [target unconfirmed] → Return to Customer Service → Customer Service Agent → Resolve → Close
- **REG-NOC-003**: Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Accounting [target unconfirmed] → Return to Customer Service → Customer Service Agent → Resolve → Close
- **REG-CON-001**: Ticket Created → Registration Queue → Registration Agent — Review → Resolve → Close
- **REG-DLD-001**: Ticket Created → Registration Queue → Registration Agent — Review DLD/registration status → Resolve → Close
- **COL-PAY-001**: Ticket Created → Collections Queue → Collections Agent — Review account → Resolve → Close
- **COL-PAY-002**: Ticket Created → Collections Queue → Collections Agent — Follow-up; Escalate if needed → Resolve → Close
- **COL-PAY-003**: Ticket Created → Collections Queue → Collections Agent — Confirm cheque/collection → Resolve → Close
- **COL-PAY-004**: Ticket Created → Collections Queue → Collections Agent — Review → Resolve → Close
- **HO-NOC-001**: Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Accounting [target unconfirmed] → Return to Customer Service → Customer Service Agent → Handoff to Handover → Handover Agent → Resolve → Close
- **HO-HND-001**: Ticket Created → Handover Queue → Handover Agent — Coordinate → Resolve → Close
- **HO-HND-002**: Ticket Created → Handover Queue → Handover Agent — Confirm available slot → Resolve → Close
- **HO-HND-003**: Ticket Created → Handover Queue → Handover Agent — Coordinate requirements → Resolve → Close
- **HO-HND-004**: Ticket Created → Handover Queue → Handoff to Facilities Management (if needed) → Handover — Follow-up → Resolve → Close
- **FM-MNT-001**: Ticket Created → Facilities Management Queue → Facilities Management Agent — Technician; In Progress → Resolve → Close
- **FM-UTL-001**: Ticket Created → Facilities Management Queue → Facilities Management Agent — Review/coordinate → Resolve → Close
- **FM-COM-001**: Ticket Created → Facilities Management Queue → Facilities Management Agent — Technician; In Progress → Resolve → Close
- **FM-SVC-001**: Ticket Created → FM/Responsible Finance Queue → Facilities Management Agent — Review → Resolve → Close
- **LCS-TEN-001**: Ticket Created → Leasing Customer Services Queue → Leasing Customer Services Agent — Process/Review → Resolve → Close
- **LCS-EJR-001**: Ticket Created → Leasing Customer Services Queue → Leasing Customer Services Agent — Process/Review → Resolve → Close
- **LCS-BKG-001**: Ticket Created → Leasing Customer Services Queue → Leasing Customer Services Agent — Confirm booking/details → Resolve → Close
- **LCS-MOV-001**: Ticket Created → Leasing Customer Services Queue → Leasing Customer Services Agent — Coordinate → Resolve → Close
- **LCS-CHK-001**: Ticket Created → Leasing Customer Services Queue → Leasing Customer Services Agent — Review → Resolve → Close
- **BRK-COM-001**: Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Admin Sales [target unconfirmed] → Admin Sales — Review → Resolve → Close
- **BRK-CHK-001**: Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Admin Sales [target unconfirmed] → Admin Sales — Confirm collection → Resolve → Close
- **SAL-INQ-001**: Ticket Created → Customer Service Queue → Handoff to Sales [target unconfirmed] → Sales — Follow-up → Resolve → Close
- **LEG-INQ-001**: Ticket Created → Customer Service Queue → Handoff to Legal [target unconfirmed] → Legal — Legal Review/Response → Return to Customer Service → Resolve → Close
- **REC-HR-001**: Ticket Created → Customer Service Queue → Handoff to HR [target unconfirmed] → HR — Acknowledge → Resolve → Close
- **REC-MKT-001**: Ticket Created → Customer Service Queue → Handoff to Marketing [target unconfirmed] → Marketing — Acknowledge → Resolve → Close
- **REC-OTH-001**: Ticket Created → Customer Service Queue → Handoff to the responsible department [target unconfirmed] → Resolve → Close

### Existing request types — current vs workbook

#### REG-NOC-001 — NOC for Resale (Customer Service)

| Aspect | Current (this database) | Workbook |
|---|---|---|
| Status | Active | — |
| Default priority | Medium | Normal → Medium |
| SLA | High: first response not set, resolution 2–4 days, clock starts TicketCreated, basis not decided; Medium: first response not set, resolution 10–12 days, clock starts TicketCreated, basis not decided | Medium: first response 4 business hours, resolution 2 business days |
| Approvals | ReopenApproval by role CS Manager, does not block work | Conditional: Registration Supervisor / Authorized Approver; plus Reopen Approval (CS Manager) if Allow Reopen is Yes |
| Workflow | Request With Pending v1: Ticket Created → Assigned → In Progress → Pending Customer (optional) → Resolved → Closed | Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Accounting [target unconfirmed] → Return to Customer Service → Customer Service Agent → Resolve → Close |
| Pending Customer | allowed | not stated |
| Agent priority change | allowed | not stated |
| Reopen | allowed | Yes |
| Transfer | per department setting | Yes |

#### REG-NOC-002 — NOC for Golden Visa (Customer Service)

| Aspect | Current (this database) | Workbook |
|---|---|---|
| Status | Active | — |
| Default priority | Medium | Normal → Medium |
| SLA | Medium: first response not set, resolution 1–2 days, clock starts TicketCreated, basis not decided | Medium: first response 4 business hours, resolution 2 business days |
| Approvals | ReopenApproval by role CS Manager, does not block work | Conditional: Registration Supervisor / Authorized Approver; plus Reopen Approval (CS Manager) if Allow Reopen is Yes |
| Workflow | Request With Pending v1: Ticket Created → Assigned → In Progress → Pending Customer (optional) → Resolved → Closed | Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Accounting [target unconfirmed] → Return to Customer Service → Customer Service Agent → Resolve → Close |
| Pending Customer | allowed | not stated |
| Agent priority change | not allowed | not stated |
| Reopen | allowed | Yes |
| Transfer | per department setting | Yes |

#### REG-NOC-003 — NOC for Mortgage (Customer Service)

| Aspect | Current (this database) | Workbook |
|---|---|---|
| Status | Active | — |
| Default priority | Medium | Normal → Medium |
| SLA | High: first response not set, resolution 2–4 days, clock starts TicketCreated, basis not decided; Medium: first response not set, resolution 10–12 days, clock starts TicketCreated, basis not decided | Medium: first response 4 business hours, resolution 2 business days |
| Approvals | ReopenApproval by role CS Manager, does not block work | Conditional: Registration Supervisor / Authorized Approver; plus Reopen Approval (CS Manager) if Allow Reopen is Yes |
| Workflow | Request With Pending v1: Ticket Created → Assigned → In Progress → Pending Customer (optional) → Resolved → Closed | Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Accounting [target unconfirmed] → Return to Customer Service → Customer Service Agent → Resolve → Close |
| Pending Customer | allowed | not stated |
| Agent priority change | allowed | not stated |
| Reopen | allowed | Yes |
| Transfer | per department setting | Yes |

#### HO-NOC-001 — NOC for Handover (Customer Service)

| Aspect | Current (this database) | Workbook |
|---|---|---|
| Status | Active | — |
| Default priority | Medium | Normal → Medium |
| SLA | Medium: first response not set, resolution 1–2 days, clock starts TicketCreated, basis not decided | Medium: first response 4 business hours, resolution 2 business days |
| Approvals | ReopenApproval by role CS Manager, does not block work | Conditional: Handover Supervisor / Authorized Approver; plus Reopen Approval (CS Manager) if Allow Reopen is Yes |
| Workflow | Request With Pending v1: Ticket Created → Assigned → In Progress → Pending Customer (optional) → Resolved → Closed | Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Accounting [target unconfirmed] → Return to Customer Service → Customer Service Agent → Handoff to Handover → Handover Agent → Resolve → Close |
| Pending Customer | allowed | not stated |
| Agent priority change | not allowed | not stated |
| Reopen | allowed | Yes |
| Transfer | per department setting | Yes |

### Decisions needed from the business

#### SLA

- Resolution SLA 'Same business day' is not a duration (expected e.g. '2 business days'). Give a number of business hours or days. — *CS-GEN-002*
- Resolution SLA 'Based on severity' is not a duration (expected e.g. '2 business days'). Give a number of business hours or days, per severity level if it varies (severity is not modeled today — only priority is). — *FM-MNT-001, FM-COM-001*
- Resolution SLA 'Based on issue severity' is not a duration (expected e.g. '2 business days'). Give a number of business hours or days, per severity level if it varies (severity is not modeled today — only priority is). — *HO-HND-004*

#### Approval

- Approval is 'Conditional' with approver 'Responsible Manager'. Confirm when approval is required, which approval type applies (today only Accounting Approval or Customer Service Approval can gate work) and which existing role, department or employee decides — 'Responsible Manager' is not one of the fixed roles. — *BRK-COM-001*
- Approval is 'Conditional' with approver 'Collections Supervisor / Manager'. Confirm when approval is required, which approval type applies (today only Accounting Approval or Customer Service Approval can gate work) and which existing role, department or employee decides — 'Collections Supervisor / Manager' is not one of the fixed roles. — *COL-PAY-002*
- Approval is 'Conditional' with approver 'CS Supervisor / Manager'. Confirm when approval is required, which approval type applies (today only Accounting Approval or Customer Service Approval can gate work) and which existing role, department or employee decides — 'CS Supervisor / Manager' is not one of the fixed roles. — *CS-CMP-001*
- Approval is 'Conditional' with approver 'Handover Supervisor / Authorized Approver'. Confirm when approval is required, which approval type applies (today only Accounting Approval or Customer Service Approval can gate work) and which existing role, department or employee decides — 'Handover Supervisor / Authorized Approver' is not one of the fixed roles. — *HO-NOC-001*
- Approval is 'Conditional' with approver 'Leasing Supervisor / Authorized Approver'. Confirm when approval is required, which approval type applies (today only Accounting Approval or Customer Service Approval can gate work) and which existing role, department or employee decides — 'Leasing Supervisor / Authorized Approver' is not one of the fixed roles. — *LCS-TEN-001, LCS-EJR-001*
- Approval is 'Conditional' with approver 'Legal / Authorized Approver'. Confirm when approval is required, which approval type applies (today only Accounting Approval or Customer Service Approval can gate work) and which existing role, department or employee decides — 'Legal / Authorized Approver' is not one of the fixed roles. — *LEG-INQ-001*
- Approval is 'Conditional' with approver 'Registration Supervisor / Authorized Approver'. Confirm when approval is required, which approval type applies (today only Accounting Approval or Customer Service Approval can gate work) and which existing role, department or employee decides — 'Registration Supervisor / Authorized Approver' is not one of the fixed roles. — *REG-NOC-001, REG-NOC-002, REG-NOC-003*

#### Handoff

- Confirm the handoff to Admin Sales: which existing TigerCS department (or other mechanism) receives it. — *BRK-COM-001, BRK-CHK-001*
- Confirm the handoff to Legal: which existing TigerCS department (or other mechanism) receives it. — *LEG-INQ-001*
- Confirm the handoff to HR: which existing TigerCS department (or other mechanism) receives it. — *REC-HR-001*
- Confirm the handoff to Marketing: which existing TigerCS department (or other mechanism) receives it. — *REC-MKT-001*
- Confirm the handoff to the responsible department: which existing TigerCS department (or other mechanism) receives it. — *REC-OTH-001*
- Confirm the handoff to Accounting: which existing TigerCS department (or other mechanism) receives it. — *REG-NOC-001, REG-NOC-002, REG-NOC-003, HO-NOC-001*
- Confirm the handoff to Sales: which existing TigerCS department (or other mechanism) receives it. — *SAL-INQ-001*

#### Workflow

- The workflow's intake 'FM/Responsible Finance Queue' does not name one department queue. Confirm which department's queue receives these tickets. — *FM-SVC-001*

#### Existing request types

- 'NOC for Handover' already exists in Customer Service and was left unchanged. Decide what, if anything, the workbook should change on it — see the current-vs-workbook comparison. — *HO-NOC-001*
- 'NOC for Resale' already exists in Customer Service and was left unchanged. Decide what, if anything, the workbook should change on it — see the current-vs-workbook comparison. — *REG-NOC-001*
- 'NOC for Golden Visa' already exists in Customer Service and was left unchanged. Decide what, if anything, the workbook should change on it — see the current-vs-workbook comparison. — *REG-NOC-002*
- 'NOC for Mortgage' already exists in Customer Service and was left unchanged. Decide what, if anything, the workbook should change on it — see the current-vs-workbook comparison. — *REG-NOC-003*

#### Configuration with no default to inherit

- Agent priority change is not in the workbook and has no default to inherit (it is set per request type). Nothing enforces it today — agents choose any priority — so it is stored as allowed to match that. Confirm allow or deny for the new request types. — *CS-GEN-001, CS-GEN-002, CS-GEN-003, CS-CMP-001, CS-CMP-002, REG-CON-001, REG-DLD-001, COL-PAY-001, COL-PAY-002, COL-PAY-003, COL-PAY-004, HO-HND-001, HO-HND-002, HO-HND-003, HO-HND-004, FM-MNT-001, FM-UTL-001, FM-COM-001, FM-SVC-001, LCS-TEN-001, LCS-EJR-001, LCS-BKG-001, LCS-MOV-001, LCS-CHK-001, BRK-COM-001, BRK-CHK-001, SAL-INQ-001, LEG-INQ-001, REC-HR-001, REC-MKT-001, REC-OTH-001*
