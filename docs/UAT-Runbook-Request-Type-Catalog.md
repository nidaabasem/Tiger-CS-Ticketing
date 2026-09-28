# UAT Runbook: First Request-Type Catalog Import

**Goal:** import the 35 catalog rows. Every newly imported request type must end up **inactive**, with an unpublished Draft workflow, `ConfigurationEnforced` off and no approval requirement. The four existing NOC types must stay unchanged, and no request type gets enforcement.

Run the steps in this order and stop on any unexpected result. All commands run from a machine that can reach the UAT database, with UAT's normal configuration (`ConnectionStrings__TigerCsDatabase` and the other UAT settings), **not** `Development`.

## 0. Preparation

1. Build the host **from this branch's commit**. The committed `publish/` folder is older and does not contain the import:
   ```bash
   dotnet publish src/TigerCS.Api -c Release -o out/api
   ```
2. Confirm the connection string in the UAT settings points at the UAT database.
3. Take a UAT database backup (the standard pre-change backup).
4. Run **`VerifyRequestTypeCatalog_PreImport.sql`** (read-only) and **save the output**. Expected:
   - the latest migration is `20260921133507_AddAgentHandoffTriggerAndConcurrency`, and neither catalog migration is listed;
   - the department list shows which workbook departments exist (a MISSING one means its rows will be skipped);
   - it records the four NOC types' rows, SLA rows and approval rows, plus the table counts;
   - it shows the configured business calendar, for reference only. Nothing is assumed from it.

## 1. Pre-migration dry run (writes nothing)

```bash
cd out/api
dotnet TigerCS.Api.dll --import-request-types --report catalog-dry-run.md
```
Expected: exit code 0, and the report headed "DRY RUN" notes the migration is not applied. It should show 0 active, "Existing — left unchanged" for the 4 NOC types, 31 inactive drafts minus any rows for missing departments, and the rest "Skipped". **Review the report before continuing.**

## 2. Migrations, in dependency order

Both scripts are idempotent, additive and transactional. Stop if either fails.
```bash
sqlcmd -S <uat-server> -d <uat-database> -b -i AddRequestTypeCatalogImport.sql       # 20260928085727
sqlcmd -S <uat-server> -d <uat-database> -b -i AddConfiguredRuntimeEnforcement.sql   # 20260928102230
```

## 3. Import: everything new stays inactive

```bash
dotnet TigerCS.Api.dll --import-request-types --apply --keep-all-inactive --report catalog-applied.md
```
Do **not** add `--activate-resolved`, `--link-existing` or `--agent-priority-change`. Expected: exit code 0, report headed "applied", counts equal to the dry run, and "Created — active 0". The command is safe to rerun: a second run reports every created row as "Already imported (unchanged)".

## 4. Read-only verification

Run **`VerifyRequestTypeCatalog_PostImport.sql`** (read-only):
- **Both migrations** are listed.
- **Imported / Existing / Skipped** totals add up to 35 and match the report. The per-code list shows which rows are which.
- **Every "MUST BE EMPTY" query returns no rows:**
  - no imported type is active or enforced, or has an active or published workflow, or has any approval requirement;
  - no request type anywhere has enforcement on;
  - no pre-existing type received a catalog code;
  - no ticket tracks a workflow step.
- **The four NOC types:** their request-type, SLA and approval rows match step 0's saved output exactly.
- **Open decisions** are listed per area; they block activation until the business answers them.

**Rollback** (if ever needed): restore the step-0 backup. Both migrations also have reversible `Down` steps, but a restore is the approved path.
