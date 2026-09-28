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
  is null when the target is unconfirmed. It is configuration only: the
  existing **Transfer** action still moves the ticket, under its existing
  role rule (CS Manager) and each department's transfer setting.

## Mapping rules

| Workbook column | Maps to |
|---|---|
| Request Code | `RequestTypes.Code` (the identity) and workflow code `RT-<code>` |
| Department | Existing department **by name**. A missing department is never created; its rows are skipped and a later re-run picks them up once the department exists. |
| Default Priority | Low → Low, **Normal → Medium** (the documented provisional mapping), High → High |
| First Response / Resolution SLA | `n business hours/days` → one SLA row at the default priority, trigger TicketCreated, clock basis BusinessHours. Anything else (e.g. "Same business day", "Based on severity") creates no SLA row and becomes a decision. |
| Required Fields / Documents | JSON array of the workbook's wording. "None" is stored as null. |
| Needs Approval? / Approval Role | "No" creates nothing. "Conditional" becomes a decision, because none of the approver roles are in the fixed role set and the approval type cannot be derived. No approval requirement is created. |
| Allow Transfer? | Checked against the department's existing `AllowTransferToOtherDepartments`. "No" becomes a decision, because there is no per-request-type transfer gate. |
| Allow Reopen? | `AllowReopen`, plus the approved Reopen Approval rule (CS Manager, non-blocking) |
| Proposed Workflow | The text is split on "→". The intake queue becomes an Assignment step for the request type's department. Agent tokens and activity text ("Review account", "Escalate if needed") become the responsible team's single work step, with the activities in its name. A handoff or return becomes an Assignment step naming the target department. Resolve and Close map to the existing Resolve and Close steps. Unconfirmed handoff targets (Accounting, Admin Sales, Sales, Legal, HR, Marketing, "responsible department") keep the step with no department and become a decision. |
| Business Decision / Comments | Any value becomes a decision to review. The column is never interpreted. |

Anything the workbook does not state stays **off** rather than being assumed:
agent priority change and Pending Customer.

## Outcomes

* **Created: active.** Every value resolved. The request type is active and workflow v1 is published after passing the normal publish validation.
* **Created: inactive draft.** At least one decision is open. The request type and workflow are inactive and v1 stays an unpublished Draft that an administrator can finish in the Workflow Designer.
* **Linked to existing.** A request type with the same department and name already existed without a code. The code is attached and **nothing else changes**. The differences are listed as a decision.
* **Already imported.** The code already exists, so the row is left as it is. Administration edits are never overwritten.
* **Skipped.** The department does not exist.

## Running it

The import never runs at startup. It uses the environment's normal
configuration (`ConnectionStrings:TigerCsDatabase`), so check which database
that points at first.

```bash
# 1. Schema (review AddRequestTypeCatalogImport.sql, then apply it the usual way)
# 2. Dry run: prints the report and writes nothing
dotnet TigerCS.Api.dll --import-request-types --report catalog-dry-run.md
# 3. After the report has been reviewed and approved:
dotnet TigerCS.Api.dll --import-request-types --apply
#    or, if nothing may go live before business sign-off:
dotnet TigerCS.Api.dll --import-request-types --apply --keep-all-inactive
```

The import refuses to run until the migration is applied, and `--apply` writes
in a single transaction.

## Dry-run snapshot

Generated against the reference seed (Customer Service, Collections,
Registration, Handover, Call Center, provisional Accounting and their request
types) **plus** Facilities Management and Leasing Customer Services. A
department missing from the target environment turns its rows into "Skipped".
The dry run against that environment is the authoritative report.

| Outcome | Rows |
|---|---|
| Created — active | 15 |
| Created — inactive draft | 16 |
| Linked to existing (unchanged) | 4 |
| Already imported (unchanged) | 0 |
| Skipped | 0 |
| **Total** | **35** |

### Rows

| Code | Request type | Department | Priority | SLA (first response / resolution) | Outcome | Open decisions |
|---|---|---|---|---|---|---|
| CS-GEN-001 | General Inquiry | Customer Service | Medium | 4 bh / 1 bd | Created — active | 0 |
| CS-GEN-002 | Office Hours / Contact Information | Customer Service | Low | — | Created — inactive draft | 1 |
| CS-GEN-003 | Construction Update | Customer Service | Medium | 4 bh / 1 bd | Created — active | 0 |
| CS-CMP-001 | Complaint | Customer Service | High | 2 bh / 2 bd | Created — inactive draft | 1 |
| CS-CMP-002 | Feedback / Suggestion | Customer Service | Low | 4 bh / 1 bd | Created — active | 0 |
| REG-NOC-001 | NOC for Resale | Customer Service | Medium | 4 bh / 2 bd | Linked to existing (unchanged) | 3 |
| REG-NOC-002 | NOC for Golden Visa | Customer Service | Medium | 4 bh / 2 bd | Linked to existing (unchanged) | 3 |
| REG-NOC-003 | NOC for Mortgage | Customer Service | Medium | 4 bh / 2 bd | Linked to existing (unchanged) | 3 |
| REG-CON-001 | SPA / Contract Inquiry | Registration | Medium | 4 bh / 1 bd | Created — active | 0 |
| REG-DLD-001 | Ownership Transfer / Title Deed Inquiry | Registration | Medium | 4 bh / 1 bd | Created — active | 0 |
| COL-PAY-001 | Payment / Outstanding Balance Inquiry | Collections | Medium | 4 bh / 1 bd | Created — active | 0 |
| COL-PAY-002 | Returned Cheque | Collections | High | 2 bh / 1 bd | Created — inactive draft | 1 |
| COL-PAY-003 | Cheque Collection | Collections | Medium | 4 bh / 1 bd | Created — active | 0 |
| COL-PAY-004 | Payment Cheque Inquiry | Collections | Medium | 4 bh / 1 bd | Created — active | 0 |
| HO-NOC-001 | NOC for Handover | Customer Service | Medium | 4 bh / 2 bd | Linked to existing (unchanged) | 3 |
| HO-HND-001 | Coordinate Handover | Handover | Medium | 4 bh / 1 bd | Created — active | 0 |
| HO-HND-002 | Schedule Handover Appointment | Handover | Medium | 4 bh / 1 bd | Created — active | 0 |
| HO-HND-003 | Move-In / Move-Out | Handover | Medium | 4 bh / 1 bd | Created — active | 0 |
| HO-HND-004 | Handover Maintenance Follow-up | Handover | Medium | — | Created — inactive draft | 1 |
| FM-MNT-001 | Repair / Maintenance Request | Facilities Management | Medium | — | Created — inactive draft | 1 |
| FM-UTL-001 | Utilities Inquiry | Facilities Management | Medium | 4 bh / 1 bd | Created — active | 0 |
| FM-COM-001 | Common Area Maintenance | Facilities Management | Medium | — | Created — inactive draft | 1 |
| FM-SVC-001 | Service Charge Inquiry | Facilities Management | Medium | 4 bh / 1 bd | Created — inactive draft | 1 |
| LCS-TEN-001 | Tenancy Contract | Leasing Customer Services | Medium | 4 bh / 2 bd | Created — inactive draft | 1 |
| LCS-EJR-001 | Ejari | Leasing Customer Services | Medium | 4 bh / 2 bd | Created — inactive draft | 1 |
| LCS-BKG-001 | Booking | Leasing Customer Services | Medium | 4 bh / 1 bd | Created — active | 0 |
| LCS-MOV-001 | Move-In / Move-Out | Leasing Customer Services | Medium | 4 bh / 1 bd | Created — active | 0 |
| LCS-CHK-001 | Rent / DEWA / AC Cheque Inquiry | Leasing Customer Services | Medium | 4 bh / 1 bd | Created — active | 0 |
| BRK-COM-001 | Broker Commission Inquiry | Customer Service | Medium | 4 bh / 2 bd | Created — inactive draft | 2 |
| BRK-CHK-001 | Broker Cheque Collection | Customer Service | Medium | 4 bh / 1 bd | Created — inactive draft | 1 |
| SAL-INQ-001 | Property Sales Inquiry | Customer Service | Medium | 2 bh / 1 bd | Created — inactive draft | 1 |
| LEG-INQ-001 | Legal Notice / Permit / Approval Inquiry | Customer Service | High | 2 bh / 2 bd | Created — inactive draft | 2 |
| REC-HR-001 | HR Inquiry | Customer Service | Low | 4 bh / 1 bd | Created — inactive draft | 1 |
| REC-MKT-001 | Marketing Inquiry | Customer Service | Low | 4 bh / 1 bd | Created — inactive draft | 1 |
| REC-OTH-001 | Other Reception Inquiry | Customer Service | Medium | 4 bh / 1 bd | Created — inactive draft | 1 |

### Workflows

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

- 'NOC for Handover' already exists in Customer Service and is active; the import linked it to HO-NOC-001 and changed nothing else. Confirm whether the catalog should replace its configuration: SLA today first response not set, resolution 1–2 days vs catalog first response 4 hours, resolution 2 days (business time); its current workflow vs the catalog's 'CS Queue →Agent→Acounting→ CS Agent → Handover Agent → Resolve → Close'. — *HO-NOC-001*
- 'NOC for Resale' already exists in Customer Service and is active; the import linked it to REG-NOC-001 and changed nothing else. Confirm whether the catalog should replace its configuration: SLA today first response not set, resolution 10–12 days vs catalog first response 4 hours, resolution 2 days (business time); its current workflow vs the catalog's 'CS Queue → CS Agent →Acounting→CS Agentt → Resolve → Close'. — *REG-NOC-001*
- 'NOC for Golden Visa' already exists in Customer Service and is active; the import linked it to REG-NOC-002 and changed nothing else. Confirm whether the catalog should replace its configuration: SLA today first response not set, resolution 1–2 days vs catalog first response 4 hours, resolution 2 days (business time); its current workflow vs the catalog's 'CS Queue → CS Agent →Acounting→CS Agentt → Resolve → Close'. — *REG-NOC-002*
- 'NOC for Mortgage' already exists in Customer Service and is active; the import linked it to REG-NOC-003 and changed nothing else. Confirm whether the catalog should replace its configuration: SLA today first response not set, resolution 10–12 days vs catalog first response 4 hours, resolution 2 days (business time); its current workflow vs the catalog's 'CS Queue → CS Agent →Acounting→CS Agentt → Resolve → Close'. — *REG-NOC-003*
