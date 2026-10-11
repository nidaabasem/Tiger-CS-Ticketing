# Collections receivables snapshot - SQL (TigerCsTicketing)

Run on `10.10.10.117` / `TigerCsTicketing`. Everything is idempotent (`CREATE OR ALTER`, `IF NOT EXISTS`), contains no credentials and nothing destructive except the clearly named, dry-run-by-default `migration/M02`, `M03`, `M90`.
Full design, semantics, schedule, deployment order and verification: [`docs/Collections/Receivables-Snapshot.md`](../../docs/Collections/Receivables-Snapshot.md).

## Execution order (before the application is deployed)

`V001` -> `V003` -> `V010` -> `V011` -> **`R001` -> `R002` -> `R003` -> `R004`** -> `migration/M01` (backup) ... deploy the application ... verify ... `migration/M02` (cleanup).
(V001, V003, V010, V011 are usually already deployed; re-running them is harmless. V010 no longer needs the old snapshot objects.)

| Script | Purpose |
| --- | --- |
| `V001__reconcile_CollectionsTowers.sql` | Reconcile the manually created `dbo.CollectionsTowers` (report only; creates an empty table only if missing). |
| `V003__fn_CollectionsTowerNumber.sql` | Tower number from a unit code. |
| `V010__create_crm_unit_owner_tables.sql` | CRM unit owners (Sold / Contract sales per tower + unit), `fn_CollectionsUnitKey`, `fn_Collections_CrmUnitLinks` and its load procedures. |
| `V011__create_crm_project_map.sql` | CRM project -> PACT tower mapping (`dbo.CollectionsCrmProjectMap`, empty; **no mapping is invented**). |
| `R001__create_tables.sql` | **New snapshot:** `CollectionsRcvRun`, `CollectionsRcvCompany`, `CollectionsRcvLine`. |
| `R002__usp_CollectionsRcv_Refresh.sql` | `usp_CollectionsRcv_Refresh @CompanyId` (PACT -> temp table -> validated atomic publish, per-company lock), `usp_CollectionsRcv_ReapDeadRuns`, `usp_CollectionsRcv_NoteClientAbort`. |
| `R003__usp_CollectionsRcv_GetStatus.sql` | Per-company state for the pages (closes orphaned attempts first; `IsLoading` is true only while that company's refresh really runs). |
| `R004__read_procedures.sql` | `usp_CollectionsRcv_GetUnits` (Receivables), `_GetLines` (Campaigns / Review), `_GetUnit` (Payment Summary by unit key), `_GetTowers`. |
| `migration/M01__backup_legacy_snapshot.sql` | Copies the 9 old tables (named in the script) into `dbo.Bak_<table>_<tag>` and verifies the row counts. |
| `migration/M02__cleanup_legacy_snapshot.sql` | **Dry run by default.** Empties those 9 old tables after checking the new pipeline is healthy and the backups match. |
| `migration/M03__drop_legacy_objects_OPTIONAL.sql` | Optional, later: drops the old (empty) tables and procedures. Dry run by default. |
| `migration/M90__rollback_restore_legacy.sql` | Rollback: restores the 9 tables from the backups and removes the new Hangfire entries. |
| `tools/test_pact_fetch.sql` | Fetch of one company from PACT into a **temp table** with its fixed layout (diagnostic window or the full 2000-2099 window); rows, time, foreign-company check. Changes nothing persistent. |
| `tools/diagnose_receivables_refresh.sql` | **Read-only**: linked-server configuration, company state, last attempts, whether a refresh really runs (lock probe), published volume. |
| `tools/verify_snapshot.sql` | **Read-only** post-deployment checklist (every row must say `OK`). |
| `tools/crm_project_map_discovery.sql` | **Read-only** helper for the CRM project -> tower map. |
| `legacy/` | The previous pipeline's scripts, kept for the rollback and for reference (not deployed any more). |
| `tests/local-sqlserver/` | Local harness: fake PACT with the verified layouts + a loopback linked server, for `RcvRealSqlTests`. Development servers only. |
