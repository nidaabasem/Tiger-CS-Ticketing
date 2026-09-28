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
| Schema migrations | `20260928085727_AddRequestTypeCatalogImport` and `20260928102230_AddConfiguredRuntimeEnforcement` (idempotent SQL: `AddRequestTypeCatalogImport.sql`, `AddConfiguredRuntimeEnforcement.sql`) |
| Runtime enforcement | `ConfiguredWorkflowRuntime` (steps), `SlaDueDateService.ComputeAsync` (SLA precedence), `ConfiguredRuntimeReadiness` / `WorkflowProgression` (rules) |
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

## Runtime enforcement (opt-in per request type)

Configuration enforcement is off for every request type, including all existing ones and every imported draft, so their behaviour is unchanged. A System Administrator turns it on per type with `PUT /api/admin/request-types/{id}/configuration-enforcement`. It is refused (409, listing every issue) while any of these remain:

- An unresolved catalog decision. The import stores each open question with its request type. `POST …/catalog-decisions/{decisionId}/resolution` records the business's answer; it changes no configuration by itself.
- No published workflow version, or a step the runtime cannot track (only Start, Department Queue, Work, optional Pending Customer, Resolve and Close are supported). A queue step without a department (an unconfirmed handoff) also blocks it, as does an entry step that isn't the request type's own department queue.
- An active gating approval (Accounting or Customer Service approval). An enforced workflow has no approval stage. Reopen Approval is unaffected.
- No SLA at the default priority with both targets. An SLA row that is a range, "Immediately", starts at a trigger other than TicketCreated, or has an undecided clock basis also blocks it.

The same rules block activating an enforced type, publishing a new version of its workflow, and editing its SLA or approvals into a state that breaks them. Activating any type that still has open catalog decisions is refused too.

### What enforcement does

| | Behaviour |
|---|---|
| Step tracking | A new ticket starts at its department's queue step (`Tickets.CurrentWorkflowStepId`, shown as `currentWorkflowStepName` on `GET /api/tickets/{id}`). Every move is audited as `WorkflowStep` under the triggering action's correlation id. |
| Assign | At a queue step, assigning an owner (manually or automatically) moves the ticket to the following work step. |
| Transfer (handoff or return) | Still the existing Transfer action: CS Manager only, subject to the source department's transfer setting, and audited as `Transfer`. It must also target the department of the next queue step, otherwise 422 `workflow-step-not-allowed` naming the expected step. |
| Resolve / Close | Outcome Resolved and Close must each be the next step (422 otherwise). Cancelled, Rejected and Duplicate end the request from any step. |
| Reopen | The existing Reopen rule is unchanged (CS layer, window, outcome). The ticket resumes at the target department's last queue step before Resolve; a department with no queue step gets 422. |
| SLA | The type's active SLA row for the ticket's priority replaces the per-priority policy, deadline by deadline. A deadline the row leaves empty falls back to the policy. Units come from the row (the first response may have its own unit). Minutes and hours on BusinessHours are walked on the **configured** active business calendar (its own work week, window and holidays, with nothing assumed). With 24/7, 1 day = 24 hours. **Business days are not applied:** what "N business days" means is an open business decision, so such a row is inapplicable (the per-priority policy governs) and it blocks enforcement. The clock still starts at creation, and Reopen still restarts Resolution only while carrying First Response. The audit records each deadline's source (`RequestTypeSla:<id>` or `PriorityPolicy`). |

Authorization is always checked first, so a caller the existing rules refuse still gets 403. The workflow can only refuse an action; it never grants one. A ticket created before enforcement was switched on stays untracked.

### Stored but not enforced
- SLA values in **business days**: every workbook row. Their meaning is an open decision.

- Required fields (`RequiredFieldsJson`) and required documents: no validation at intake.
- Agent priority change: not checked anywhere (agents choose any priority), as before.
- Request group and description: informational only.
- On request-type SLA rows: pause flags, warning threshold and trigger. Only TicketCreated is applied, and enforced types can't have another trigger.
- Everything above for request types **without** enforcement: steps, step departments and request-type SLA rows are stored only. That covers the four NOC types and every imported draft.
- The Web UI has no enforcement toggle or decision list (API only). Ticket Details doesn't show the current step or pre-hide out-of-order actions; the API refuses them with a 422 explaining the expected next step.
- Transfer is still configured per department, not per request type.

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
| Allow Reopen? | `AllowReopen` only. The approved rule is direct Reopen by CS Agent, CS Supervisor or CS Manager, and by the System Administrator through the central override. The import creates **no** approval requirement of any kind, including no Reopen Approval, so no reopen-request path is added for other roles. |
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

**For UAT, follow `docs/UAT-Runbook-Request-Type-Catalog.md` exactly.** The import never runs at startup. It uses the environment's normal
configuration (`ConnectionStrings:TigerCsDatabase`), so check which database
that points at first. The command must be run from a machine that can reach
that database.

```bash
# 1. Dry run. This works BEFORE the migration: it reads existing columns only and writes nothing.
dotnet TigerCS.Api.dll --import-request-types --report catalog-dry-run.md
# 2. Schema: review AddRequestTypeCatalogImport.sql and AddConfiguredRuntimeEnforcement.sql, then apply them the usual way.
# 3. First import: every created type stays inactive, and existing types are untouched.
dotnet TigerCS.Api.dll --import-request-types --apply --keep-all-inactive --report catalog-applied.md
```

`--apply` is refused unless it names an activation choice
(`--keep-all-inactive` or `--activate-resolved`), and it is refused before the
migration. It writes in a single transaction. Re-running with the same
switches creates and changes nothing.

## Dry-run snapshot (pre-migration)

This was generated before the migration, against the development reference
seed (Customer Service, Collections, Registration, Handover, Call Center,
provisional Accounting and their request types) **plus** Facilities
Management and Leasing Customer Services. No UAT calendar or department list
is assumed from it. Without those two departments,
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
| CS-GEN-001 | General Inquiry | Customer Service | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| CS-GEN-002 | Office Hours / Contact Information | Customer Service | Low | — | Created — inactive draft | open | 2 |
| CS-GEN-003 | Construction Update | Customer Service | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| CS-CMP-001 | Complaint | Customer Service | High | 2 bh / 2 bd | Created — inactive draft | open | 3 |
| CS-CMP-002 | Feedback / Suggestion | Customer Service | Low | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| REG-NOC-001 | NOC for Resale | Customer Service | Medium | 4 bh / 2 bd | Existing — left unchanged | open | 4 |
| REG-NOC-002 | NOC for Golden Visa | Customer Service | Medium | 4 bh / 2 bd | Existing — left unchanged | open | 4 |
| REG-NOC-003 | NOC for Mortgage | Customer Service | Medium | 4 bh / 2 bd | Existing — left unchanged | open | 4 |
| REG-CON-001 | SPA / Contract Inquiry | Registration | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| REG-DLD-001 | Ownership Transfer / Title Deed Inquiry | Registration | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| COL-PAY-001 | Payment / Outstanding Balance Inquiry | Collections | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| COL-PAY-002 | Returned Cheque | Collections | High | 2 bh / 1 bd | Created — inactive draft | open | 3 |
| COL-PAY-003 | Cheque Collection | Collections | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| COL-PAY-004 | Payment Cheque Inquiry | Collections | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| HO-NOC-001 | NOC for Handover | Customer Service | Medium | 4 bh / 2 bd | Existing — left unchanged | open | 4 |
| HO-HND-001 | Coordinate Handover | Handover | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| HO-HND-002 | Schedule Handover Appointment | Handover | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| HO-HND-003 | Move-In / Move-Out | Handover | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| HO-HND-004 | Handover Maintenance Follow-up | Handover | Medium | — | Created — inactive draft | open | 2 |
| FM-MNT-001 | Repair / Maintenance Request | Facilities Management | Medium | — | Created — inactive draft | open | 2 |
| FM-UTL-001 | Utilities Inquiry | Facilities Management | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| FM-COM-001 | Common Area Maintenance | Facilities Management | Medium | — | Created — inactive draft | open | 2 |
| FM-SVC-001 | Service Charge Inquiry | Facilities Management | Medium | 4 bh / 1 bd | Created — inactive draft | open | 3 |
| LCS-TEN-001 | Tenancy Contract | Leasing Customer Services | Medium | 4 bh / 2 bd | Created — inactive draft | open | 3 |
| LCS-EJR-001 | Ejari | Leasing Customer Services | Medium | 4 bh / 2 bd | Created — inactive draft | open | 3 |
| LCS-BKG-001 | Booking | Leasing Customer Services | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| LCS-MOV-001 | Move-In / Move-Out | Leasing Customer Services | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| LCS-CHK-001 | Rent / DEWA / AC Cheque Inquiry | Leasing Customer Services | Medium | 4 bh / 1 bd | Created — inactive draft | open | 2 |
| BRK-COM-001 | Broker Commission Inquiry | Customer Service | Medium | 4 bh / 2 bd | Created — inactive draft | open | 4 |
| BRK-CHK-001 | Broker Cheque Collection | Customer Service | Medium | 4 bh / 1 bd | Created — inactive draft | open | 3 |
| SAL-INQ-001 | Property Sales Inquiry | Customer Service | Medium | 2 bh / 1 bd | Created — inactive draft | open | 3 |
| LEG-INQ-001 | Legal Notice / Permit / Approval Inquiry | Customer Service | High | 2 bh / 2 bd | Created — inactive draft | open | 4 |
| REC-HR-001 | HR Inquiry | Customer Service | Low | 4 bh / 1 bd | Created — inactive draft | open | 3 |
| REC-MKT-001 | Marketing Inquiry | Customer Service | Low | 4 bh / 1 bd | Created — inactive draft | open | 3 |
| REC-OTH-001 | Other Reception Inquiry | Customer Service | Medium | 4 bh / 1 bd | Created — inactive draft | open | 3 |

### Settings the workbook does not give

- **Pending Customer**: inherited — allowed on the request type and its workflow, the established behaviour whenever a request type does not restrict it.
- **Agent priority change**: no default exists to inherit (a required per-request-type value) — see the Configuration decision below.
- **Assignment**: no rule is created, so tickets use the department queue — the existing fallback.
- **SLA clock start**: TicketCreated, the existing default; pause-on-Pending stays "not decided" as on every other type.

### Workflow definitions

Steps — including each handoff's department — are enforced at runtime only once an administrator turns on configuration enforcement for the request type, which is refused while any decision below is open. Even then a ticket changes department only through the existing Transfer action (CS Manager only, subject to the source department's transfer setting); the workflow only decides whether that transfer is the expected next step.

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
| Runtime | not enforced: steps are not tracked and due dates come from the per-priority SLA policy — the SLA rows below are stored only | — |
| Default priority | Medium | Normal → Medium |
| SLA | High: first response not set, resolution 2–4 days, clock starts TicketCreated, basis not decided; Medium: first response not set, resolution 10–12 days, clock starts TicketCreated, basis not decided | Medium: first response 4 business hours, resolution 2 business days |
| Approvals gating the work | none | Conditional: Registration Supervisor / Authorized Approver |
| Workflow | Request With Pending v1: Ticket Created → Assigned → In Progress → Pending Customer (optional) → Resolved → Closed | Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Accounting [target unconfirmed] → Return to Customer Service → Customer Service Agent → Resolve → Close |
| Pending Customer | allowed | not stated |
| Agent priority change | allowed | not stated |
| Reopen | allowed — direct Reopen by CS Agent, CS Supervisor or CS Manager; System Administrator through the central override. Note: pre-existing ReopenApproval requirement row (role CS Manager, active) — left unchanged; not part of the approved direct-Reopen rule | Yes — direct Reopen by CS Agent, CS Supervisor or CS Manager; System Administrator through the central override |
| Transfer | per department setting | Yes |

#### REG-NOC-002 — NOC for Golden Visa (Customer Service)

| Aspect | Current (this database) | Workbook |
|---|---|---|
| Status | Active | — |
| Runtime | not enforced: steps are not tracked and due dates come from the per-priority SLA policy — the SLA rows below are stored only | — |
| Default priority | Medium | Normal → Medium |
| SLA | Medium: first response not set, resolution 1–2 days, clock starts TicketCreated, basis not decided | Medium: first response 4 business hours, resolution 2 business days |
| Approvals gating the work | none | Conditional: Registration Supervisor / Authorized Approver |
| Workflow | Request With Pending v1: Ticket Created → Assigned → In Progress → Pending Customer (optional) → Resolved → Closed | Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Accounting [target unconfirmed] → Return to Customer Service → Customer Service Agent → Resolve → Close |
| Pending Customer | allowed | not stated |
| Agent priority change | not allowed | not stated |
| Reopen | allowed — direct Reopen by CS Agent, CS Supervisor or CS Manager; System Administrator through the central override. Note: pre-existing ReopenApproval requirement row (role CS Manager, active) — left unchanged; not part of the approved direct-Reopen rule | Yes — direct Reopen by CS Agent, CS Supervisor or CS Manager; System Administrator through the central override |
| Transfer | per department setting | Yes |

#### REG-NOC-003 — NOC for Mortgage (Customer Service)

| Aspect | Current (this database) | Workbook |
|---|---|---|
| Status | Active | — |
| Runtime | not enforced: steps are not tracked and due dates come from the per-priority SLA policy — the SLA rows below are stored only | — |
| Default priority | Medium | Normal → Medium |
| SLA | High: first response not set, resolution 2–4 days, clock starts TicketCreated, basis not decided; Medium: first response not set, resolution 10–12 days, clock starts TicketCreated, basis not decided | Medium: first response 4 business hours, resolution 2 business days |
| Approvals gating the work | none | Conditional: Registration Supervisor / Authorized Approver |
| Workflow | Request With Pending v1: Ticket Created → Assigned → In Progress → Pending Customer (optional) → Resolved → Closed | Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Accounting [target unconfirmed] → Return to Customer Service → Customer Service Agent → Resolve → Close |
| Pending Customer | allowed | not stated |
| Agent priority change | allowed | not stated |
| Reopen | allowed — direct Reopen by CS Agent, CS Supervisor or CS Manager; System Administrator through the central override. Note: pre-existing ReopenApproval requirement row (role CS Manager, active) — left unchanged; not part of the approved direct-Reopen rule | Yes — direct Reopen by CS Agent, CS Supervisor or CS Manager; System Administrator through the central override |
| Transfer | per department setting | Yes |

#### HO-NOC-001 — NOC for Handover (Customer Service)

| Aspect | Current (this database) | Workbook |
|---|---|---|
| Status | Active | — |
| Runtime | not enforced: steps are not tracked and due dates come from the per-priority SLA policy — the SLA rows below are stored only | — |
| Default priority | Medium | Normal → Medium |
| SLA | Medium: first response not set, resolution 1–2 days, clock starts TicketCreated, basis not decided | Medium: first response 4 business hours, resolution 2 business days |
| Approvals gating the work | none | Conditional: Handover Supervisor / Authorized Approver |
| Workflow | Request With Pending v1: Ticket Created → Assigned → In Progress → Pending Customer (optional) → Resolved → Closed | Ticket Created → Customer Service Queue → Customer Service Agent → Handoff to Accounting [target unconfirmed] → Return to Customer Service → Customer Service Agent → Handoff to Handover → Handover Agent → Resolve → Close |
| Pending Customer | allowed | not stated |
| Agent priority change | not allowed | not stated |
| Reopen | allowed — direct Reopen by CS Agent, CS Supervisor or CS Manager; System Administrator through the central override. Note: pre-existing ReopenApproval requirement row (role CS Manager, active) — left unchanged; not part of the approved direct-Reopen rule | Yes — direct Reopen by CS Agent, CS Supervisor or CS Manager; System Administrator through the central override |
| Transfer | per department setting | Yes |

### Decisions needed from the business

#### SLA

- Confirm what 'N business day(s)' means on the configured business calendar — for example N full working-day windows of business time, or by the end of the Nth working day. Until confirmed, day-based SLAs are stored but not applied. — *CS-GEN-001, CS-GEN-003, CS-CMP-001, CS-CMP-002, REG-NOC-001, REG-NOC-002, REG-NOC-003, REG-CON-001, REG-DLD-001, COL-PAY-001, COL-PAY-002, COL-PAY-003, COL-PAY-004, HO-NOC-001, HO-HND-001, HO-HND-002, HO-HND-003, FM-UTL-001, FM-SVC-001, LCS-TEN-001, LCS-EJR-001, LCS-BKG-001, LCS-MOV-001, LCS-CHK-001, BRK-COM-001, BRK-CHK-001, SAL-INQ-001, LEG-INQ-001, REC-HR-001, REC-MKT-001, REC-OTH-001*
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
