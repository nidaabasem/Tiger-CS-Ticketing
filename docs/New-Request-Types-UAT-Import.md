# Customer Service Request Types — UAT Baseline

The Customer Service workbook
[`business-review/TigerCS_New_Request_Types_Business_Review.xlsx`](business-review/TigerCS_New_Request_Types_Business_Review.xlsx)
is the **first approved UAT baseline**. Its 35 request types are imported
as-is, active, with a deliberately basic configuration: use the system in
UAT, gather feedback, then refine SLA, approvals and workflow from the
Administration screens.

The workbook's cell-for-cell TSV export
([`business-review/TigerCS_New_Request_Types_Business_Review.tsv`](business-review/TigerCS_New_Request_Types_Business_Review.tsv))
is embedded in `TigerCS.Infrastructure` and is what the importer reads; a test
re-reads the `.xlsx` and fails if the two ever differ.

## How to load it

| Database | Path |
|---|---|
| Fresh development database | Automatic — the development seed runs `NewRequestTypesImporter`. |
| Existing UAT database | `sqlcmd -S <server> -d <database> -U <user> -P <password> -C -b -I -i ImportNewRequestTypes_UAT.sql` (set `@CommitChanges = 0` for a dry run). |

Both paths apply the same rules; the SQL script's data block is rendered from
the C# catalog and a test fails if they drift. Both are idempotent and
additive: a re-run creates nothing, and nothing existing is updated or
deleted. No migration is needed.

## What each row gets

| Workbook column | Configuration |
|---|---|
| Department | Owning department, found by code then exact name. **Facilities Management** (`FM`) and **Leasing Customer Services** (`LCS`) are created if missing. |
| New Request Type | Request type name, **active**. |
| Default Priority | High → High, Normal → Medium, Low → Low. |
| Proposed Workflow | A workflow (code = Request Code), version 1 **Published**, one step per arrow. Hand-offs to teams that are not TigerCS departments (Admin Sales, Sales, Legal, HR, Marketing, Responsible Finance) are *"Manual handoff to …"* work steps — the ticket stays with its owning department. "Reception / CS" is Customer Service intake. |
| Resolution SLA | Request-type SLA row in business days. "Same business day" = 1 day. "Based on severity" (3 rows) gets no row, so the standard per-priority SLA applies. |
| First Response SLA | Not stored — see below. |
| Needs Approval? | "No" → no approval. "Conditional" (10 rows) → no approval requirement is created; the workbook gives no rule to configure. |
| Required Fields | Stored on the request type as a list of the workbook's labels. |
| Allow Reopen? | Stored (all Yes). |
| Request Group, Business Description, Required Documents | Kept in the workflow description. |

## Duplicates

A request type with the **exact same name** in the owning department is reused
as-is, not duplicated. In the standard seed that is `REG-NOC-001/002/003` (NOC
for Resale / Golden Visa / Mortgage) and `HO-NOC-001` (NOC for Handover).
`CS-CMP-001` "Complaint" is not an exact match for the existing "Complaint
Handling", so it is imported and the similarity is reported.

## Result on the standard seed

31 created + 4 reused = **all 35 workbook request types available**. With the
13 request types already seeded, that is **44 active request types**.

## Not configured

| Item | Why | How to add later |
|---|---|---|
| First Response SLA (all 35) | A request-type SLA row has one time unit for both deadlines; the workbook gives First Response in hours and Resolution in days. | Administration → Request Type → SLA. |
| Conditional approvals (10 rows) | No approval rule is specified, and no existing approval type matches supervisor/manager approval. | Administration → Request Type → Approvals, once the rule is defined. |
