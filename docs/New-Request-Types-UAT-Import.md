# New Request Types — UAT Import (Business Review)

**Status: UAT business-review configuration only.** Not Production-approved,
not deployed, not merged. Every row's *Business Decision* in the source is
blank, and **every imported request type is INACTIVE**.

Source of truth: [`business-review/TigerCS_New_Request_Types_Business_Review.xlsx`](business-review/TigerCS_New_Request_Types_Business_Review.xlsx)
(35 proposed NEW request types, sheet *New Request Types*, header row 5, data
rows 6–40). Its cell-for-cell TSV export
[`business-review/TigerCS_New_Request_Types_Business_Review.tsv`](business-review/TigerCS_New_Request_Types_Business_Review.tsv)
is embedded in `TigerCS.Infrastructure` and is what the importer reads; a
test re-reads the `.xlsx` and fails if the two ever differ.

## 1. UAT decisions applied

| # | Decision | How it is implemented |
|---|---|---|
| 1 | Facilities Management and Leasing Customer Services are valid TigerCS departments | Resolved against existing data first — by code (`FM`, `LCS`; overridable in the UAT script), then exact name. **UAT script:** if genuinely missing it is **reported** and its rows blocked; it is created only on a re-run with `@CreateMissingOwningDepartments = 1`, and **never** while a similarly named department exists (reported as a near match — map it via the code variable instead). **Development seed:** created when genuinely missing, so their rows are not skipped because the sample seed lacks them. No duplicates in either path. |
| 2 | Reception is not a department | "Reception / CS" and "CS Intake" are the step *Customer Service intake (Reception / CS)* on the Customer Service queue. |
| 3 | Admin Sales, Sales, Legal, HR, Marketing: no departments yet; documented manual hand-offs; Draft allowed | Each is a step *Manual handoff to X (documented; no system transfer)* of kind **Department Work** on the owning department — deliberately **not** a queue/assignment step, so nothing implies the engine transfers the ticket. These destinations never resolve (even if a same-named department appears), so their workflows stay **Draft** until the business decides. |
| 4 | FM-SVC-001 stays owned by Facilities Management; "Responsible Finance" is a dependency | Owner FM; *Manual handoff to Responsible Finance* (optional — the workbook's "FM/…" is an alternative) is a pending dependency, so the workflow is **Draft**. |
| 5 | Keep all imported request types inactive | `IsActive = false` on every created row; nothing re-activates or modifies them on re-run. |
| 6 | Do not guess the 10 Conditional approvals; do not touch ReopenApproval | No approval requirement created; all 10 flagged in §5; ReopenApproval untouched. |
| 7 | Do not invent SLA conversions | "Same business day", "Based on severity" → no SLA row; First Response not stored for any row; all flagged in §6. |
| 8 | Do not merge/replace similarly named request types | `REG-NOC-001/002/003`, `HO-NOC-001`: exact same name exists → **not imported** (the unique department+name index allows only one; creating a renamed copy would invent a name); existing rows untouched. `CS-CMP-001` *Complaint*: imported inactive **alongside** the existing *Complaint Handling*, flagged. |
| 9 | Request Groups are not intake Categories | No Category created or linked; the group is kept in the workflow description. The model keeps them separate (`Category` = intake routing taxonomy; its link to `RequestType` is an open phase-2 decision). |

## 2. Artifacts

| Artifact | Purpose |
|---|---|
| `src/TigerCS.Infrastructure/Modules/WorkflowConfiguration/Seed/NewRequestTypesBusinessReview.cs` | Catalog: reads the 35 rows verbatim, normalizes them (throws on any unknown value), holds the structured-workflow translation per Request Code, the confirmed owning departments, the pending hand-off destinations and the similar-name list. |
| `src/TigerCS.Infrastructure/Modules/WorkflowConfiguration/Seed/NewRequestTypesImporter.cs` | Idempotent, additive, transactional importer; per-row UAT review flags and an owning-department report. |
| `DevSeedData.SeedNewRequestTypesForReviewAsync` | Fresh (Development) databases; `DevSeedData` only runs when `IsDevelopment()` — **Production is never seeded**. |
| `ImportNewRequestTypes_UAT.sql` (repo root) | Existing UAT databases: same rules; data block rendered from the catalog (a test fails on drift); `@CommitChanges` (dry run), `@CreateMissingOwningDepartments`, FM/LCS code overrides. |
| `src/TigerCS.Tests/WorkflowConfiguration/Services/NewRequestTypesImportTests.cs` | 19 tests. |

**No migration. No new table. No new status.**

## 3. Data-model audit and column mapping

| Excel column | TigerCS representation | Support |
|---|---|---|
| Request Code | `Workflows.Code` (unique) of the request type's own workflow, and its version 1 code. `RequestTypes` has no code column, so the code is carried by the workflow; it is also the idempotency key. | Supported without schema change |
| Department | `RequestTypes.DepartmentId`, resolved by `Departments.Code`, then exact `Name` (both unique) | Supported |
| Request Group | No column on `RequestType`; not a Category (decision 9). Kept in the workflow description. | Gap (documentation only) |
| New Request Type | `RequestTypes.Name` (unique per department) | Supported |
| Business Description | No column on `RequestType`; kept in the workflow description | Gap (documentation only) |
| Proposed Workflow | Structured `WorkflowTemplateSteps` (§4); verbatim text in the workflow description | Supported, with engine gaps (§4) |
| Needs Approval? / Approval Role | `RequestTypeApprovalRequirements` | **Not applied** — §5 |
| Default Priority | `DefaultPriorityId`: Normal→Medium (documented `NormalUrgencyPriority`), High→High, Low→Low | Supported |
| First Response SLA | `RequestTypeSlaPolicies.FirstResponseTargetValue` — one unit per row | **Not stored** — §6 |
| Resolution SLA | `RequestTypeSlaPolicies`: Days, `ClockBasis = BusinessHours`, `TicketCreated`, default priority | Supported where numeric |
| Required Fields | `RequiredFieldsJson` (provisional; nothing enforces it) | Stored as labels — §7 |
| Required Documents | Not modeled | Gap — §7 |
| Allow Transfer? | Department-level only (`DepartmentWorkflowSettings`); all 35 = Yes = existing default; no per-type flag | Gap (no effect today) |
| Allow Reopen? | `RequestTypes.AllowReopen` — all 35 = Yes | Supported |
| Business Decision / Comments | All blank → imported **inactive** | — |

Not in the workbook, set conservatively: `AllowAgentPriorityChange = false`,
`AllowPendingCustomer = false`, `AllowPendingInternal = false`.

**Activation (when the business approves a row):** Administration → Request
Types. Administration refuses to activate a request type whose workflow has no
Published version, so Draft rows cannot go live until their dependency is
decided and the workflow published in the Workflow Designer.

## 4. 35/35 UAT review status

Scenario verified on SQL Server 2022 against a database seeded from `main`
(Facilities Management present, Leasing Customer Services absent — what the
UAT script is expected to meet):

* **Step 1 — default run:** Leasing Customer Services reported *genuinely
  missing*; its 5 rows **blocked**; 19 imported Published + 7 imported Draft;
  4 exact-name conflicts.
* **Step 2 — after the business confirms, re-run with
  `@CreateMissingOwningDepartments = 1`:** department created once; its 5
  rows imported; everything else `AlreadyImported`.
* **Step 3 — re-run:** nothing created.

**Final accounting (35):** **31 imported inactive** (24 with the
workflow Published, 7 Draft because of an unresolved dependency) +
**4 exact-name conflicts not imported** = 35. Before step 2, 5
of the 31 are blocked on the missing owning department. Cross-cutting flags:
**10** need an approval decision; **4** need a Resolution SLA
decision; **all 35** need the First Response decision; **5** carry an
existing-name conflict (4 exact + `CS-CMP-001` similar).

| # | Request Code | Department | Request Type | Imported inactive | Draft — unresolved workflow dependency | Blocked — owning dept missing | Existing-name conflict | Approval decision required | SLA decision required |
|---|---|---|---|---|---|---|---|---|---|
| 1 | `CS-GEN-001` | Customer Service (`CS`) | General Inquiry | Yes | — | — | — | — | First Response only |
| 2 | `CS-GEN-002` | Customer Service (`CS`) | Office Hours / Contact Information | Yes | — | — | — | — | Resolution ("Same business day") + First Response |
| 3 | `CS-GEN-003` | Customer Service (`CS`) | Construction Update | Yes | — | — | — | — | First Response only |
| 4 | `CS-CMP-001` | Customer Service (`CS`) | Complaint | Yes | — | — | **Similar** — "Complaint Handling" exists; imported alongside | Yes — CS Supervisor / Manager | First Response only |
| 5 | `CS-CMP-002` | Customer Service (`CS`) | Feedback / Suggestion | Yes | — | — | — | — | First Response only |
| 6 | `REG-NOC-001` | Customer Service (`CS`) | NOC for Resale | No | — | — | **Exact** — "NOC for Resale" exists; not imported | Yes — Registration Supervisor / Authorized Approver | First Response only |
| 7 | `REG-NOC-002` | Customer Service (`CS`) | NOC for Golden Visa | No | — | — | **Exact** — "NOC for Golden Visa" exists; not imported | Yes — Registration Supervisor / Authorized Approver | First Response only |
| 8 | `REG-NOC-003` | Customer Service (`CS`) | NOC for Mortgage | No | — | — | **Exact** — "NOC for Mortgage" exists; not imported | Yes — Registration Supervisor / Authorized Approver | First Response only |
| 9 | `REG-CON-001` | Registration (`REG`) | SPA / Contract Inquiry | Yes | — | — | — | — | First Response only |
| 10 | `REG-DLD-001` | Registration (`REG`) | Ownership Transfer / Title Deed Inquiry | Yes | — | — | — | — | First Response only |
| 11 | `COL-PAY-001` | Collections (`COL`) | Payment / Outstanding Balance Inquiry | Yes | — | — | — | — | First Response only |
| 12 | `COL-PAY-002` | Collections (`COL`) | Returned Cheque | Yes | — | — | — | Yes — Collections Supervisor / Manager | First Response only |
| 13 | `COL-PAY-003` | Collections (`COL`) | Cheque Collection | Yes | — | — | — | — | First Response only |
| 14 | `COL-PAY-004` | Collections (`COL`) | Payment Cheque Inquiry | Yes | — | — | — | — | First Response only |
| 15 | `HO-NOC-001` | Customer Service (`CS`) | NOC for Handover | No | — | — | **Exact** — "NOC for Handover" exists; not imported | Yes — Handover Supervisor / Authorized Approver | First Response only |
| 16 | `HO-HND-001` | Handover (`HO`) | Coordinate Handover | Yes | — | — | — | — | First Response only |
| 17 | `HO-HND-002` | Handover (`HO`) | Schedule Handover Appointment | Yes | — | — | — | — | First Response only |
| 18 | `HO-HND-003` | Handover (`HO`) | Move-In / Move-Out | Yes | — | — | — | — | First Response only |
| 19 | `HO-HND-004` | Handover (`HO`) | Handover Maintenance Follow-up | Yes | — | — | — | — | Resolution ("Based on issue severity") + First Response |
| 20 | `FM-MNT-001` | Facilities Management (`FM`) | Repair / Maintenance Request | Yes | — | — | — | — | Resolution ("Based on severity") + First Response |
| 21 | `FM-UTL-001` | Facilities Management (`FM`) | Utilities Inquiry | Yes | — | — | — | — | First Response only |
| 22 | `FM-COM-001` | Facilities Management (`FM`) | Common Area Maintenance | Yes | — | — | — | — | Resolution ("Based on severity") + First Response |
| 23 | `FM-SVC-001` | Facilities Management (`FM`) | Service Charge Inquiry | Yes | Draft: Responsible Finance | — | — | — | First Response only |
| 24 | `LCS-TEN-001` | Leasing Customer Services (`LCS`) | Tenancy Contract | Yes — once LCS exists (step 2) | — | Until Leasing Customer Services is confirmed & created | — | Yes — Leasing Supervisor / Authorized Approver | First Response only |
| 25 | `LCS-EJR-001` | Leasing Customer Services (`LCS`) | Ejari | Yes — once LCS exists (step 2) | — | Until Leasing Customer Services is confirmed & created | — | Yes — Leasing Supervisor / Authorized Approver | First Response only |
| 26 | `LCS-BKG-001` | Leasing Customer Services (`LCS`) | Booking | Yes — once LCS exists (step 2) | — | Until Leasing Customer Services is confirmed & created | — | — | First Response only |
| 27 | `LCS-MOV-001` | Leasing Customer Services (`LCS`) | Move-In / Move-Out | Yes — once LCS exists (step 2) | — | Until Leasing Customer Services is confirmed & created | — | — | First Response only |
| 28 | `LCS-CHK-001` | Leasing Customer Services (`LCS`) | Rent / DEWA / AC Cheque Inquiry | Yes — once LCS exists (step 2) | — | Until Leasing Customer Services is confirmed & created | — | — | First Response only |
| 29 | `BRK-COM-001` | Customer Service (`CS`) | Broker Commission Inquiry | Yes | Draft: Admin Sales | — | — | Yes — Responsible Manager | First Response only |
| 30 | `BRK-CHK-001` | Customer Service (`CS`) | Broker Cheque Collection | Yes | Draft: Admin Sales | — | — | — | First Response only |
| 31 | `SAL-INQ-001` | Customer Service (`CS`) | Property Sales Inquiry | Yes | Draft: Sales | — | — | — | First Response only |
| 32 | `LEG-INQ-001` | Customer Service (`CS`) | Legal Notice / Permit / Approval Inquiry | Yes | Draft: Legal | — | — | Yes — Legal / Authorized Approver | First Response only |
| 33 | `REC-HR-001` | Customer Service (`CS`) | HR Inquiry | Yes | Draft: HR | — | — | — | First Response only |
| 34 | `REC-MKT-001` | Customer Service (`CS`) | Marketing Inquiry | Yes | Draft: Marketing | — | — | — | First Response only |
| 35 | `REC-OTH-001` | Customer Service (`CS`) | Other Reception Inquiry | Yes | — | — | — | — | First Response only |

## 5. Approval Mapping Required (10 Conditional rows)

Supported model: `ApprovalType` ∈ {AccountingApproval, CustomerServiceApproval,
ReopenApproval}; `TargetKind` ∈ {Role, Department (optionally narrowed to a
role), Employee}; fixed roles: CS Agent, CS Supervisor, Department Employee,
Department Head, CS Manager, General Manager, Chairman/CEO, System
Administrator, Reporting User. **Nothing applied.** ReopenApproval untouched.

| Request Code | Request Type | Approval Role (Excel) | Maps exactly? | Possible mapping — for confirmation only | Blocker |
|---|---|---|---|---|---|
| `CS-CMP-001` | Complaint | CS Supervisor / Manager | No | `CustomerServiceApproval` → Role CS Supervisor *or* CS Manager | Which role; "Conditional" has no model (a requirement applies to every ticket) |
| `REG-NOC-001` | NOC for Resale | Registration Supervisor / Authorized Approver | No | Department `REG` narrowed to Department Head | No approval type fits; also an exact-name conflict |
| `REG-NOC-002` | NOC for Golden Visa | Registration Supervisor / Authorized Approver | No | as above | as above |
| `REG-NOC-003` | NOC for Mortgage | Registration Supervisor / Authorized Approver | No | as above | as above |
| `COL-PAY-002` | Returned Cheque | Collections Supervisor / Manager | No | Department `COL` narrowed to Department Head | No approval type fits |
| `HO-NOC-001` | NOC for Handover | Handover Supervisor / Authorized Approver | No | Department `HO` narrowed to Department Head | No approval type fits; also an exact-name conflict |
| `LCS-TEN-001` | Tenancy Contract | Leasing Supervisor / Authorized Approver | No | Department Leasing Customer Services narrowed to Department Head | No approval type fits |
| `LCS-EJR-001` | Ejari | Leasing Supervisor / Authorized Approver | No | as above | as above |
| `BRK-COM-001` | Broker Commission Inquiry | Responsible Manager | No | none — which manager? | Unmappable without a named role/department |
| `LEG-INQ-001` | Legal Notice / Permit / Approval Inquiry | Legal / Authorized Approver | No | none until Legal is represented (decision 3) | No department; no approval type fits |

"Supervisor" has no per-department role in TigerCS (a department's head role
is Department Head); "Authorized Approver" would be an `Employee` target
naming a person. Department approvals would also need a new additive
`ApprovalType` — a code change to agree first.

## 6. SLA mapping

| Excel value | Rows | Stored | Decision |
|---|---|---|---|
| Resolution "1 business day" | 22 | `Days`, 1, `BusinessHours`, `TicketCreated`, default priority | — |
| Resolution "2 business days" | 9 | `Days`, 2, as above | — |
| Resolution "Same business day" | 1 (`CS-GEN-002`) | **no SLA row** | **SLA policy decision** — no numeric value is invented |
| Resolution "Based on severity" / "Based on issue severity" | 3 (`HO-HND-004`, `FM-MNT-001`, `FM-COM-001`) | **no SLA row** | **SLA policy decision** — severity rules undefined |
| First Response "2 business hours" / "4 business hours" | 6 / 29 | **not stored** | **Technical/business decision** (below) |

**First Response representation.** A `RequestTypeSlaPolicy` row has one
`SlaDurationUnit` for both deadlines; the workbook mixes business hours with
business days. Resolution is stored exactly. Storing First Response would
require converting days to hours (1 business day = 10 h only under the
current Sat–Thu 08:00–18:00 calendar) — not done. Options: (a) express
resolution in business hours and store both; (b) per-deadline units (schema
change); (c) leave First Response to the per-priority `SlaPolicies`.
`RequestTypeSlaPolicies` is configuration only today; due dates still use the
per-priority policy until phase 4.

| Request Code | First Response (Excel) | Resolution (Excel) | Stored in TigerCS | Decision |
|---|---|---|---|---|
| `CS-GEN-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `CS-GEN-002` | 4 business hours | Same business day | no SLA row | Resolution value + First Response |
| `CS-GEN-003` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `CS-CMP-001` | 2 business hours | 2 business days | Resolution 2 business day(s) · Days · BusinessHours · TicketCreated · High | First Response |
| `CS-CMP-002` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Low | First Response |
| `REG-NOC-001` | 4 business hours | 2 business days | Resolution 2 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `REG-NOC-002` | 4 business hours | 2 business days | Resolution 2 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `REG-NOC-003` | 4 business hours | 2 business days | Resolution 2 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `REG-CON-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `REG-DLD-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `COL-PAY-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `COL-PAY-002` | 2 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · High | First Response |
| `COL-PAY-003` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `COL-PAY-004` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `HO-NOC-001` | 4 business hours | 2 business days | Resolution 2 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `HO-HND-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `HO-HND-002` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `HO-HND-003` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `HO-HND-004` | 4 business hours | Based on issue severity | no SLA row | Resolution value + First Response |
| `FM-MNT-001` | 2 business hours | Based on severity | no SLA row | Resolution value + First Response |
| `FM-UTL-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `FM-COM-001` | 2 business hours | Based on severity | no SLA row | Resolution value + First Response |
| `FM-SVC-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `LCS-TEN-001` | 4 business hours | 2 business days | Resolution 2 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `LCS-EJR-001` | 4 business hours | 2 business days | Resolution 2 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `LCS-BKG-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `LCS-MOV-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `LCS-CHK-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `BRK-COM-001` | 4 business hours | 2 business days | Resolution 2 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `BRK-CHK-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `SAL-INQ-001` | 2 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |
| `LEG-INQ-001` | 2 business hours | 2 business days | Resolution 2 business day(s) · Days · BusinessHours · TicketCreated · High | First Response |
| `REC-HR-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Low | First Response |
| `REC-MKT-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Low | First Response |
| `REC-OTH-001` | 4 business hours | 1 business day | Resolution 1 business day(s) · Days · BusinessHours · TicketCreated · Medium | First Response |

## 7. Required Fields / Documents

* **Required Fields — partially supported.** `RequiredFieldsJson` is a
  *provisional* JSON array of intake field keys; nothing validates or enforces
  it and no field-key catalog exists. The workbook's labels are stored
  verbatim (including "if applicable"); they will need mapping to real field
  keys when that feature is built.
* **Required Documents — not supported.** No per-request-type document model
  (attachments are a later increment). Preserved in the workflow description
  and below; no documents engine added.

| Request Code | Required Fields (Excel) → `RequiredFieldsJson` | Required Documents (Excel — not modeled) |
|---|---|---|
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

## 8. Structured workflows

Every imported row has its own workflow (code = Request Code), version 1
validated with the Workflow Designer's publish rules.

| Excel step | Classification | Step kind |
|---|---|---|
| (implicit) | Start | `Created` — mandatory first step |
| "CS Queue", "Collections Queue", "FM Queue", "Leasing CS Queue", … | Queue / Department ownership | `Assigned` |
| "Reception / CS", "CS Intake" | Queue / Department ownership (Customer Service intake) | `Assigned` |
| "Agent", "CS Agent", "Agent/Technician" | Assignment | `Assigned` |
| hand-off to an **existing** department (Accounting, back to CS, Handover) | Transfer / Handoff | `Assigned` — the move itself is the agent's existing Transfer action |
| hand-off to **Admin Sales, Sales, Legal, HR, Marketing, Responsible Finance** | Transfer / Handoff — **manual, documented** | `InProgress` on the owning department; no system transfer; workflow Draft |
| "Facilities Management if needed" (Handover) | Transfer / Handoff (conditional) | `MaintenanceDependency` (optional) |
| review / coordinate / follow-up / confirm / process / in progress | Operational/manual | `InProgress` |
| "Escalate if needed" | Operational/manual (optional) | `InProgress` — existing manual escalation, **not** an approval |
| Resolve, "CS Resolve", "Acknowledge/Resolve" | Resolve | `Resolved` |
| Close | Close | `Closed` |

No Approval step was generated (no supported approval type fits — §5).

**Engine gaps (documentation kept, not worked around):** a step cannot name a
destination department and the engine performs no transfer; no conditional
steps ("if needed" → optional); no escalation step kind; `LEG-INQ-001`'s
"CS Resolve" implies a return from Legal that the workbook does not list (not
invented). Workbook typos corrected in step names only ("Acounting", "CS
Agentt", "Cs Agent", "Admin sales").

| Request Code | Proposed Workflow (verbatim) | Structured steps — name `kind` · classification |
|---|---|---|
| `CS-GEN-001` | CS Queue → Agent → Resolve → Close | Ticket Created `Created`·Start → CS Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `CS-GEN-002` | CS Queue → Agent → Resolve → Close | Ticket Created `Created`·Start → CS Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `CS-GEN-003` | CS Queue → Agent → Obtain update if needed → Resolve → Close | Ticket Created `Created`·Start → CS Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Obtain update if needed *(optional)* `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `CS-CMP-001` | CS Queue → Agent → Escalate if needed → Resolve → Close | Ticket Created `Created`·Start → CS Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Escalate if needed *(optional)* `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `CS-CMP-002` | CS Queue → Agent → Record/route → Resolve → Close | Ticket Created `Created`·Start → CS Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Record / route `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REG-NOC-001` | CS Queue → CS Agent →Acounting→CS Agentt → Resolve → Close | Ticket Created `Created`·Start → CS Queue `Assigned`·Queue/ownership → CS Agent `Assigned`·Assignment → Transfer to Accounting `Assigned`·Handoff → Return to CS Agent `Assigned`·Handoff → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REG-NOC-002` | CS Queue → CS Agent →Acounting→CS Agentt → Resolve → Close | Ticket Created `Created`·Start → CS Queue `Assigned`·Queue/ownership → CS Agent `Assigned`·Assignment → Transfer to Accounting `Assigned`·Handoff → Return to CS Agent `Assigned`·Handoff → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REG-NOC-003` | CS Queue → CS Agent →Acounting→CS Agentt → Resolve → Close | Ticket Created `Created`·Start → CS Queue `Assigned`·Queue/ownership → CS Agent `Assigned`·Assignment → Transfer to Accounting `Assigned`·Handoff → Return to CS Agent `Assigned`·Handoff → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REG-CON-001` | Registration Queue → Agent → Review → Resolve → Close | Ticket Created `Created`·Start → Registration Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Review `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REG-DLD-001` | Registration Queue → Agent → Review DLD/registration status → Resolve → Close | Ticket Created `Created`·Start → Registration Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Review DLD / registration status `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `COL-PAY-001` | Collections Queue → Agent → Review account → Resolve → Close | Ticket Created `Created`·Start → Collections Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Review account `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `COL-PAY-002` | Collections Queue → Agent → Follow-up → Escalate if needed → Resolve → Close | Ticket Created `Created`·Start → Collections Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Follow-up `InProgress`·Operational → Escalate if needed *(optional)* `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `COL-PAY-003` | Collections Queue → Agent → Confirm cheque/collection → Resolve → Close | Ticket Created `Created`·Start → Collections Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Confirm cheque / collection `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `COL-PAY-004` | Collections Queue → Agent → Review → Resolve → Close | Ticket Created `Created`·Start → Collections Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Review `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `HO-NOC-001` | CS Queue →Agent→Acounting→ CS Agent → Handover Agent → Resolve → Close | Ticket Created `Created`·Start → CS Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Transfer to Accounting `Assigned`·Handoff → Return to CS Agent `Assigned`·Handoff → Transfer to Handover Agent `Assigned`·Handoff → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `HO-HND-001` | Handover Queue → Agent → Coordinate → Resolve → Close | Ticket Created `Created`·Start → Handover Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Coordinate `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `HO-HND-002` | Handover Queue → Agent → Confirm available slot → Resolve → Close | Ticket Created `Created`·Start → Handover Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Confirm available slot `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `HO-HND-003` | Handover Queue → Agent → Coordinate requirements → Resolve → Close | Ticket Created `Created`·Start → Handover Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Coordinate requirements `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `HO-HND-004` | Handover Queue → Facilities Management if needed → Follow-up → Resolve → Close | Ticket Created `Created`·Start → Handover Queue `Assigned`·Queue/ownership → Facilities Management if needed *(optional)* `MaintenanceDependency`·Handoff → Follow-up `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `FM-MNT-001` | FM Queue → Assign Agent/Technician → In Progress → Resolve → Close | Ticket Created `Created`·Start → FM Queue `Assigned`·Queue/ownership → Assign Agent / Technician `Assigned`·Assignment → In Progress `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `FM-UTL-001` | FM Queue → Agent → Review/coordinate → Resolve → Close | Ticket Created `Created`·Start → FM Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Review / coordinate `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `FM-COM-001` | FM Queue → Agent/Technician → In Progress → Resolve → Close | Ticket Created `Created`·Start → FM Queue `Assigned`·Queue/ownership → Agent / Technician `Assigned`·Assignment → In Progress `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `FM-SVC-001` | FM/Responsible Finance Queue → Agent → Review → Resolve → Close | Ticket Created `Created`·Start → FM Queue `Assigned`·Queue/ownership → Manual handoff to Responsible Finance (documented; no system transfer) *(optional)* `InProgress`·Handoff → Agent `Assigned`·Assignment → Review `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `LCS-TEN-001` | Leasing CS Queue → Agent → Process/Review → Resolve → Close | Ticket Created `Created`·Start → Leasing CS Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Process / review `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `LCS-EJR-001` | Leasing CS Queue → Agent → Process/Review → Resolve → Close | Ticket Created `Created`·Start → Leasing CS Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Process / review `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `LCS-BKG-001` | Leasing CS Queue → Agent → Confirm booking/details → Resolve → Close | Ticket Created `Created`·Start → Leasing CS Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Confirm booking / details `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `LCS-MOV-001` | Leasing CS Queue → Agent → Coordinate → Resolve → Close | Ticket Created `Created`·Start → Leasing CS Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Coordinate `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `LCS-CHK-001` | Leasing CS Queue → Agent → Review → Resolve → Close | Ticket Created `Created`·Start → Leasing CS Queue `Assigned`·Queue/ownership → Agent `Assigned`·Assignment → Review `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `BRK-COM-001` | CS Queue →Cs Agent  → Admin sales →Review → Resolve → Close | Ticket Created `Created`·Start → CS Queue `Assigned`·Queue/ownership → CS Agent `Assigned`·Assignment → Manual handoff to Admin Sales (documented; no system transfer) `InProgress`·Handoff → Review `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `BRK-CHK-001` | CS Queue →Cs Agent  → Admin sales → Confirm collection → Resolve → Close | Ticket Created `Created`·Start → CS Queue `Assigned`·Queue/ownership → CS Agent `Assigned`·Assignment → Manual handoff to Admin Sales (documented; no system transfer) `InProgress`·Handoff → Confirm collection `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `SAL-INQ-001` | CS / Call Center → Sales Handoff → Follow-up → Resolve / Close | Ticket Created `Created`·Start → CS / Call Center Queue `Assigned`·Queue/ownership → Manual handoff to Sales (documented; no system transfer) `InProgress`·Handoff → Follow-up `InProgress`·Operational → Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `LEG-INQ-001` | CS Intake → Legal Handoff → Legal Review/Response → CS Resolve → Close | Ticket Created `Created`·Start → Customer Service intake `Assigned`·Queue/ownership → Manual handoff to Legal (documented; no system transfer) `InProgress`·Handoff → Legal Review / Response `InProgress`·Operational → CS Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REC-HR-001` | Reception / CS → HR Handoff → Acknowledge/Resolve → Close | Ticket Created `Created`·Start → Customer Service intake (Reception / CS) `Assigned`·Queue/ownership → Manual handoff to HR (documented; no system transfer) `InProgress`·Handoff → Acknowledge / Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REC-MKT-001` | Reception / CS → Marketing Handoff → Acknowledge/Resolve → Close | Ticket Created `Created`·Start → Customer Service intake (Reception / CS) `Assigned`·Queue/ownership → Manual handoff to Marketing (documented; no system transfer) `InProgress`·Handoff → Acknowledge / Resolve `Resolved`·Resolve → Close `Closed`·Close |
| `REC-OTH-001` | Reception / CS → Route to Responsible Department → Resolve → Close | Ticket Created `Created`·Start → Customer Service intake (Reception / CS) `Assigned`·Queue/ownership → Route to Responsible Department `Assigned`·Handoff → Resolve `Resolved`·Resolve → Close `Closed`·Close |

## 9. Departments and workflow dependencies

| Department | Role in workbook | Resolution | Effect |
|---|---|---|---|
| Customer Service `CS`, Registration `REG`, Collections `COL`, Handover `HO`, Accounting `ACC` | owning / hand-off | Existing (reference seed) | — |
| Facilities Management `FM` | owning (4) + `HO-HND-004` hand-off | Resolved by code/name; UAT script reports if missing and creates only on request | Confirm presence in UAT (script step 1 shows it) |
| Leasing Customer Services `LCS` | owning (5) | Not in any seed — expected **genuinely missing** in UAT: reported, then created on request; code `LCS` unless UAT has it under another code | 5 rows blocked until created |
| Admin Sales, Sales, Legal, HR, Marketing | manual hand-offs | **Representation pending — never created, never resolved** | 6 workflows Draft |
| Responsible Finance | `FM-SVC-001` hand-off | **Representation pending** (Finance? Accounting?) | Workflow Draft |
| Reception | intake wording | Not a department — Customer Service intake | — |

## 10. Running it on UAT

1. Apply migrations (none new). Optionally dry run: `@CommitChanges = 0`.
2. `sqlcmd -S <server> -d <database> -U <user> -P <password> -C -b -I -i ImportNewRequestTypes_UAT.sql`
3. Read section 2 of the output (*OWNING DEPARTMENTS*):
   * **Missing** — genuinely missing; after confirmation, re-run with `@CreateMissingOwningDepartments = 1`.
   * **NearMatch** — a similarly named department exists; nothing created. If it is the same department, set `@FacilitiesManagementCode` / `@LeasingCustomerServicesCode` to its code and re-run.
4. Review the reconciliation. Re-running is always safe. **Do not run on Production.**

## 11. Validation

* Release build: 0 warnings, 0 errors. Full suite: **2,310 passed** (2,291 existing + 19 new), 0 failed.
* `dotnet ef migrations has-pending-model-changes`: *No changes have been made to the model since the last migration.*
* SQL Server 2022 (container), database seeded from `main`: step 1/2/3 above
  exactly as described; pre-existing request types, approvals (ReopenApproval
  included), departments, department settings, SLA rows and baseline
  workflows byte-identical afterwards; no imported request type active.
  Near-match database (extra "Leasing" department): Leasing rows blocked,
  nothing created; with the code override set, rows import into the existing
  department (dry run rolled back cleanly).
* Development startup (C# path) on a fresh database: Leasing created, 24
  Published + 7 Draft + 4 conflicts; restart creates nothing; configuration
  **identical** to the SQL path for all 31 imported rows.

## 12. Business decisions still required

1. **Business Decision** for each of the 35 rows (all remain inactive).
2. **Exact-name conflicts** `REG-NOC-001/002/003`, `HO-NOC-001` vs the existing CS NOC request types — same or different? (If different, they need distinct names.)
3. **Similar-name conflict** `CS-CMP-001` *Complaint* vs *Complaint Handling*.
4. **Leasing Customer Services** — confirm it is missing in UAT (the script reports it) before it is created; confirm the code (`LCS`).
5. **Representation** of Admin Sales, Sales, Legal, HR, Marketing and *Responsible Finance* (7 Draft workflows).
6. **Approval mappings** for the 10 Conditional rows (§5), "Conditional" semantics, and whether a new approval type may be added.
7. **SLA**: "Same business day" (1), "Based on severity" (3), First Response representation (35).
8. Flags not in the workbook: `AllowAgentPriorityChange`, `AllowPendingCustomer` (both off).
9. ReopenApproval for these rows after activation (not touched here).
