# Receivables snapshot (rebuilt): PACT -> local snapshot every two hours -> Receivables and Campaigns

Companies **Dubai (4)** and **Sharjah (32)** only. Amounts come from PACT, never from CRM. Company 7 (Leasing) is **not** part of this: it exists only in *GetPaymentSummary* by phone number and is never loaded into the snapshot or shown on Receivables / Campaigns.

```
 PACTRPT.dbo.p4AccountReceivables  ─┐                       ┌─ Receivables page   (usp_CollectionsRcv_GetUnits)
 PACTRPT.dbo.p32AccountReceivables ─┴─ usp_CollectionsRcv_Refresh (one call per company) ─ dbo.CollectionsRcvLine ─┼─ Campaigns / Review (usp_CollectionsRcv_GetLines)
        every 2 hours (Hangfire, one job per company)                                                              └─ Payment Summary    (usp_CollectionsRcv_GetUnit)
```

Opening a page or changing a filter reads the last published copy; it never calls PACT. This pipeline shares no table, procedure, run history, lock or job with the previous one (retired: [legacy/Receivables-Snapshot-previous-pipeline.md](legacy/Receivables-Snapshot-previous-pipeline.md)).

## 1. What is stored, and how

| Object | Purpose |
| --- | --- |
| `dbo.CollectionsRcvLine` | The **unpaid** payments (remaining `Amount > 0`), per company and run. Fully paid rows, cancelled apartments (`*` in the unit code) and invalid units (UnitID <= 0, unit code blank or `0`, no customer) are **not stored**; they are counted on the run (with the amount, for invalid / cancelled). The whole window 2000-01-01 .. 2099-12-31 is stored. |
| `dbo.CollectionsRcvCompany` | One row per company: the **published** run, last success, last attempt (`Never / Running / Succeeded / Failed / Abandoned`), real error number + message, consecutive failures. |
| `dbo.CollectionsRcvRun` | One row per attempt of one company: layout, window, raw / published / excluded counts, fetch and publish milliseconds, error. |

* PACT returns only the **remaining** amount and a status text (no original, no paid amount), so *View Details* shows `—` for the original and paid amounts. Nothing is guessed.
* `SourceMinAmount = 0`: the Minimum Total of the pages is **not** sent to PACT; it is applied to the summed payments of an apartment when a page is read.
* **Result layouts are fixed per company** (verified on the live procedures): Dubai = layout 1 (13 columns, with `ProjectCode`), Sharjah = layout 3 (12 columns: `CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status`). There is no trial-and-error: `INSERT ... EXEC` maps by position, and a wrong layout through a linked server can end the SQL session without any error (reproduced on SQL Server 2022; it is exactly what left the old run `Running`). A result that contains a row of another company is refused as a whole.

## 2. Refresh, state and failure (per company)

* `usp_CollectionsRcv_Refresh @CompanyId` fetches into a **session-local temp table**, validates, then publishes in one transaction (lines under a new run id + the company's pointer). The other company, the pages and the previous published run are untouched until that transaction commits. A partial result is never visible.
* **Failure keeps the last good copy.** The attempt is recorded (`Failed`, the real SQL error, start / finish time); the pages keep serving the previous run and show *Could not load data for Tiger Group Sharjah* with **Retry**. A result that would shrink the published rows by more than 60% is refused the same way (override: `@AllowLargeShrink = 1`).
* **Loading is per company and truthful.** A company is *loading* only while a refresh of **that company** really executes. Liveness = the company's application lock (`Collections.Rcv.<company>`, session-owned): `usp_CollectionsRcv_GetStatus` (called by every page read) tries to take it for 0 ms; if it succeeds nobody is refreshing, and any `Running` row is closed as `Abandoned`. The age of a row is never used, and no `VIEW SERVER STATE` is needed. Loading ends when that company's attempt ends; another company's or an older run never makes a finished company *Loading*.
* **Timeout / cancel / restart.** The refresh has its own command timeout (`RefreshCommandTimeoutSeconds`, 1800). On timeout or cancellation the application closes the connection, records the reason on the attempt and tries to close it; if the SQL session is still inside the PACT call (a linked-server call may not honour a cancel until PACT answers) the company stays honestly *Loading* until that session ends, then the next status read closes it as `Failed` with the recorded reason. A killed process leaves a `Running` row without a lock holder: closed as `Abandoned` by the next status read or refresh. Nothing is closed by age.
* **Retry** re-runs **only the named company**. Two refreshes of one company cannot overlap (in-process gate + SQL lock: the second returns `AlreadyRunning`); the two companies refresh independently, in parallel.
* Exports, Review approval and Genesys dispatch stay blocked for missing or stale data (`Freshness`, `RangeCovered`).

## 3. What the pages show (all dates are Asia/Dubai)

* **Due** = remaining amount of payments due **today**; **Overdue** = due **before** today; **Total = Due + Overdue**. Payments due after today are never read, listed, totalled or exported. Zero Due/Overdue prints `—`. The month does not change the classification.
* One row per **apartment of a customer** (company + customer + unit id + unit code, compared exactly); a customer with several apartments has one row each; payments of one apartment are summed; Dubai and Sharjah are never mixed.
* **Status**: *Due + Overdue* (default), *Due*, *Overdue* - applied to the payments, so every figure is computed from the payments that match. **Month / Year** filter the **due date** (a month shows only its payments; the current month stops at today); *All months* = everything due up to today **including previous years**.
* **Minimum Total (AED)**: default 100, changeable and clearable; applied **after** the payments of an apartment are summed (`Total > value`, strictly greater; 60 + 60 shows as 120 at 100; exactly 100 does not), before the count, paging and export; never per payment and never hard-coded.
* **Order**: apartments by their newest matching due date, newest first, then company, customer, unit code, unit id (stable pages); *View Details* newest first, overdue rows in red, sum of the details = the row's Total. Same order in Receivables, Campaigns and the exports.
* Contact: CRM first (Sold / Contract, not cancelled), PACT completes missing fields, different people are never merged; a missing mobile or e-mail never hides a receivable. Sending eligibility and the campaign stage rules are unchanged.
* The last successful update time is shown briefly above the table; when nothing has ever been loaded the page shows *Loading* or *Could not load* with Retry and **no table** (never a zero).
* Campaigns evaluate the stage rules in the application on the same payment rows (`usp_CollectionsRcv_GetLines`), so the stage/eligibility/review logic is the one that was already tested. Campaigns keep their configured start date (`CollectionsSource:PactReceivables:StartDate`, 2026-01-01); set it earlier to let Campaigns reach older years.

## 4. Schedule, runtime requirements and freshness

| Setting | Value | Why |
| --- | --- | --- |
| `Collections:ReceivablesSnapshot:RefreshCron` | `0 */2 * * *` | every two hours, on the hour |
| `...:RefreshTimeZoneId` | `Asia/Dubai` | the cron is evaluated in Dubai time (not the server's zone): 00:00, 02:00 ... 22:00 |
| `...:RefreshOnStartup` | `true` | one refresh per company when the application starts, so a new deployment is not empty |
| `...:MaxAgeMinutes` | **180** | 120 (interval) + 30 (the longest refresh, the 1800 s command timeout) + 30 slack; a snapshot is never *Stale* before its next normal refresh, and exports stay allowed between refreshes |
| `...:RefreshCommandTimeoutSeconds` | 1800 | one company's refresh |
| `...:SourceFromDate / SourceThroughDate / SourceMinAmount` | 2000-01-01 / 2099-12-31 / 0 | the permanent window (not a test window) |
| `Collections:StaleAfterMinutes` | 60 (unchanged) | judges **live** reads. Figures taken from the snapshot (the Review workflow) are judged by `MaxAgeMinutes` instead (`CollectionsClock.IsStale(asOf, snapshotMaxAge)`), never stricter than the snapshot's own limit |

The scheduler is **Hangfire** inside the API process; changing the cron alone does nothing. It runs only when all of these hold in the target environment:

1. `BackgroundJobs__Enabled=true` (the committed `appsettings*.json` ship `false`; with `false` Hangfire is off and the API logs a warning at start-up).
2. The API process is running continuously (IIS: application pool *Start Mode = AlwaysRunning*, *Idle Time-out = 0*, no recycle-on-idle; or a service). A stopped worker process runs nothing.
3. `ConnectionStrings__TigerCsDatabase` can use schema `HangfireSla` (created on first start, `PrepareSchemaIfNecessary`; the login needs DDL rights once, or create the schema beforehand). Hangfire uses that same database.
4. The host knows the time zone id `Asia/Dubai` (an unknown id stops the start-up with an error).
5. No environment override still carries the old values: search the deployment's environment / secret store / `appsettings.Production.json` for `Collections__ReceivablesSnapshot__RefreshCron`, `...__MaxAgeMinutes`, `...__UseLocalSnapshot` (an old `*/30 * * * *` override would win over the new default). The keys `UseLocalSnapshot`, `RetainPaidInstalments`, `SourceProcedureSuffix`, `StrictIdentity` no longer exist and are ignored.
6. The login of `TigerCsDatabase` can `EXECUTE` the `dbo.usp_CollectionsRcv_*` procedures (`GRANT EXECUTE ON SCHEMA::dbo` or per procedure, unless it is `db_owner`) and the linked server `[10.10.10.94]` accepts it (RPC Out + Data Access, a login mapping, EXECUTE on `p4AccountReceivables` / `p32AccountReceivables`). No Linked Server / MSDTC / server setting is changed by this work.

At start the API registers `collections-receivables-refresh-4` and `-32` and **removes** the previous `collections-receivables-refresh` job. Without Hangfire a *Retry* still refreshes in-process, but nothing is scheduled.

## 5. Deployment, migration, cleanup, rollback (in this order)

Server `10.10.10.117`, database `TigerCsTicketing`. SQL scripts are in `database/collections-receivables/`; all are idempotent (`CREATE OR ALTER` / `IF NOT EXISTS`). No credentials in any script.

1. **Full database backup** (DBA).
2. **Before the application**, in this order: `V001__reconcile_CollectionsTowers.sql` (report only), `V003__fn_CollectionsTowerNumber.sql`, `V010__create_crm_unit_owner_tables.sql`, `V011__create_crm_project_map.sql` (these four are already deployed on most servers; re-running is harmless), then **`R001__create_tables.sql`, `R002__usp_CollectionsRcv_Refresh.sql`, `R003__usp_CollectionsRcv_GetStatus.sql`, `R004__read_procedures.sql`**.
3. `migration/M01__backup_legacy_snapshot.sql` - copies the 9 old tables (listed in the script) into `dbo.Bak_<table>_20261011` and verifies the row counts. The old pipeline's tables are *not* modified.
4. Stop the old application, make sure the Hangfire queue has no `CollectionsReceivablesRangeLoadJob` / `CollectionsReceivablesRefreshJob` enqueued, deploy the new package with `BackgroundJobs__Enabled=true`. Start it: both companies load (`RefreshOnStartup`).
5. **Verify** (section 6). Optional diagnosis: `tools/test_pact_fetch.sql` (fetch of one company into a temp table, the full-window run), `tools/diagnose_receivables_refresh.sql`.
6. **Cleanup after success**: `migration/M02__cleanup_legacy_snapshot.sql` - dry run first (`@Execute = 0`), then `@Execute = 1`. It refuses unless both companies are published by the new pipeline, every table has a verified backup and no old refresh runs. It empties exactly the 9 old tables (snapshot, staging, refresh history, unit tables, contact dictionary, company state re-seeded as *never loaded*). Customers, tickets, CRM owners, tower map, campaign / review / dispatch history and PACT are not touched.
7. *Optional, later:* `migration/M03__drop_legacy_objects_OPTIONAL.sql` drops the old (empty) tables and procedures after the retention period you choose. After M03 the rollback below is no longer possible.
8. **Rollback to the previous version**: stop the new application; `migration/M90__rollback_restore_legacy.sql` (restores the 9 tables from the backups and removes the two new Hangfire entries); deploy the previous package; start it (it registers its own job again). The new `dbo.CollectionsRcv*` tables stay and are harmless.

## 6. Verify after deployment (short)

1. `tools/verify_snapshot.sql` - every row `OK` (both companies published, newest attempt succeeded, nothing stuck, refreshed within 3 hours, no `*` / invalid units, only 4 and 32, old job gone, two new jobs registered).
2. Receivables page: both companies appear, *Updated hh:mm (Dubai)* is recent; pick one apartment: Due + Overdue = Total = the sum in *View Details*; clear *Minimum Total* and the list grows.
3. Campaigns: same apartment, same Due / Overdue / Total; units without mobile or e-mail are listed with a reason.
4. Stop PACT access for one company (or wait for a failure): that company shows *Could not load data for ...* + **Retry**, the other company and the old copy keep working.
5. After the next even Dubai hour, `dbo.CollectionsRcvRun` has a new `Hangfire` row per company.

## 7. Verification status - what was tested where

**Real SQL Server 2022, local (Linux), fake PACT with the verified result layouts (not the production server, not real PACT):** `tests/local-sqlserver` builds `PACTRPT` with `p4AccountReceivables` (layout 1) and `p32AccountReceivables` (layout 3) and a loopback linked server named `[10.10.10.94]`. `RcvRealSqlTests` (21 tests) + `RcvSchedulingTests` (3) + `RcvFullVolumeTimings` (`TIGERCS_PERF_SQL`) cover: refresh of both companies with their own layouts and no company mixing; counts of settled / invalid / cancelled rows; apartment grouping, 60 + 60, exactly 100, minimum cleared; Due / Overdue against today; month, previous year, current month, future month; status filter; search; stable paging; newest-first order; details = Total; contactless units; CRM first / ambiguous CRM; Payment Summary by unit key; failure of one company keeping the other and the last good copy; a foreign-company row refused; shrink guard; concurrent refresh refused and the other company refreshing meanwhile; client timeout; abandoned and killed sessions (a wrong layout really ends the session); Hangfire storage (cron, `Asia/Dubai`, next run on an even Dubai hour, old job removed); migration M01 / M02 / M90 (executed by hand: backup, dry run, cleanup, rollback incl. identity values). The real API process with Hangfire enabled ran both company jobs on start-up.

**Full window 2000-2099, synthetic PACT (300,000 / 144,000 raw rows, as large as the last real Dubai snapshot of 324,076 rows):**

| | raw rows | published unpaid payments | fetch | publish |
| --- | --- | --- | --- | --- |
| Dubai (4), via the API's Hangfire job | 300,000 | 136,886 | 9.5 s | 2.9 s |
| Sharjah (32), via the API's Hangfire job (in parallel) | 144,000 | 65,700 | 4.7 s | 1.5 s |

Page reads over that snapshot (warm, ~203 k payments): Receivables page 1 / page 40 about 0.5 s, Overdue-only 0.5 s, one company 0.2-0.5 s, a month 0.3 s, a text search about 2.3 s. Campaigns read 166 k payment rows and evaluate the stage rules in memory: about 3.5 s per preview at this synthetic worst case (the earlier SQL campaign engine is gone; if that latency matters, add a set-based `IPactCampaignSource` later).

**Not verified on the real server (no access from the build environment):** the real `p4` / `p32` full-window run (time and row counts), the publish on `10.10.10.117`, the linked-server login / EXECUTE rights of the production login, `INSERT ... EXEC` behaviour of the production provider (the diagnostic test of the field into a temp table already succeeded for both companies: 51 rows in 543 ms for Sharjah, layout 3, 12 columns, no ProjectCode, rows of one company only), and Hangfire hosting on the target IIS / service. Run section 5 steps 2-5 there and attach `tools/verify_snapshot.sql` + `tools/test_pact_fetch.sql` (company 4 and 32, window 2000-01-01..2099-12-31) results. If `tools/test_pact_fetch.sql` fails on the server, give the DBA that error and the section that produced it - this work changes no Linked Server, MSDTC or server setting.
