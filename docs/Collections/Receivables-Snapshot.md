# Receivables snapshot: tower, date window, background refresh

The **Due & Overdue Customers** page and **Collections Campaigns** read a **local snapshot** of the PACT receivables in
`TigerCsTicketing`. A background job refreshes it; a page view never calls PACT. Filters are **Tower + From + To** (plus status,
stage and search). There is no company selector: the company is resolved from the tower.

Deployment scripts: [`database/collections-receivables/`](../../database/collections-receivables/). Nothing here is deployed
automatically. **Nothing in this change has been run against a real SQL Server or PACT** (see "Verification status").

## 1. How it works

```
Hangfire recurring job (every 30 min, + once at startup)
  -> EXEC dbo.usp_Collections_RefreshReceivables                       (Ticketing DB, application login)
       per company (4, then 32), independently:
         INSERT ... EXEC [10.10.10.94].[PACTRPT].[dbo].[p4|p32AccountReceivables] -> dbo.CollectionsReceivableStaging
         dbo.usp_Collections_PublishReceivablesStaging: validate -> insert under a NEW RunId -> flip CurrentRunId
Pages -> API -> EXEC dbo.usp_Collections_GetReceivables (tower + window + Due/Overdue) -> C# groups per apartment
```

* **Atomic publish, previous snapshot preserved.** New rows are inserted under a new `RunId`; readers only see
  `CollectionsReceivableCompanyState.CurrentRunId`. Validation or fetch failure never moves the pointer or `LastSuccessUtc`.
* **Per-company tracking.** `CollectionsReceivableCompanyState` holds last attempt / last success / consecutive failures / error
  number / row counts / coverage window per company; `CollectionsReceivableRun(Company)` keep the history. A company can be fresh
  while the other is failed.
* **No overlap.** A session-scoped `sp_getapplock` inside the refresh procedure (works across app instances and SSMS runs) plus a
  process semaphore. A concurrent call returns `AlreadyRunning`; the job treats that as a skip.
* **Staging validation (publish is refused, snapshot kept):** zero rows (an empty result is a failure, never "no receivables"),
  more than `MaxRawRows`, wrong/NULL `CompanyID`, NULL `DueDate`/`Amount`, negative `Amount`, or a row count that fell by more than
  `MaxShrinkPercent` (60 %) versus the previous snapshot when that had >= 200 rows (override: `@AllowLargeShrink = 1` when running by hand).
* **Row rules.** `Amount = 0` (paid, float residue rounded to 4 dp) is excluded and counted. `Amount > 0` with `UnitID` NULL/0, or a
  blank customer id, is **excluded but counted with its amount** and shown on the pages ("N instalments, AED x are not listed ...").
  `Status = Paid` with `Amount > 0` and unrecognised statuses are **kept** and counted for review.
* **Amount/status meaning** (from `pact-sql/Receivables-Source-Review.md`): `Amount` is the **remaining unpaid** amount of the instalment;
  `Status` is `Paid` iff it is 0, otherwise `Installment` (does not distinguish unpaid from partially paid). Payment allocation is
  untouched. Duplicate/same-date rows are preserved and flagged exactly as before (`NeedsReview`, `AmbiguousInstalments`, ...).
* **No threshold, no truncation.** The refresh calls the existing procedures with `@MinAmount = 0` (an `int` filter inside PACT: any
  higher value permanently removes small balances; the diagnostic `100` is not used anywhere) and a wide `@StartDate/@EndDate`
  (`2000-01-01`..`2099-12-31`, configurable). Per the source review `@StartDate/@EndDate` only filter the final `DueDate`; the ledger and
  allocation arithmetic is unaffected, so historical accounting is not truncated to 1 January.

## 2. Date semantics (Due, Overdue, window, as-of date)

| Term | Meaning |
| --- | --- |
| **DueDate** | Due date of one instalment (a calendar day; time-of-day ignored). |
| **From / To** | Inclusive window on `DueDate`: `From <= DueDate < To + 1 day` (To covers its whole day, e.g. 23:59:59.997). Both editable. It only chooses *which instalments are listed*; it never changes how an instalment is classified. |
| **As-of / reporting month** | Receivables page: the current Dubai month (optional API `year`+`month` override). Campaigns: the **preview date** (it also drives stage schedule and stage eligibility, exactly as before). |
| **Overdue** (Receivables page) | `DueDate` before the first day of the as-of month. |
| **Due** (Receivables page) | `DueDate` within the as-of month, **including the later days of that month** (existing EDSM monthly rule). |
| **Neither** | `DueDate` after the as-of month end ("Outstanding"): never overdue, never listed on the Receivables page. A future instalment is never classified overdue. |
| **Campaign stages** | Unchanged: Overdue reminder `< preview date - 1 month`; Current month = whole preview month; Follow-up = whole month; Legal notice = previous calendar month (> 1,500); Legal referral `< preview date - 3 months` (> 20,000). The window is an extra reviewer filter on top of these rules and can only remove instalments; `RangeNotes` flag when it does. |

Defaults: **From = 1 January of the preview/report year** (2026-01-01 for 2026). **To** = month end for *Current-month* and *Follow-up*
stages and for the Receivables page (so Due and Overdue are both complete); for the other campaign stages the preview date. The inputs are
pre-filled with the effective values; in Campaigns, unedited defaults follow stage/preview-date changes in the browser and edited dates are kept.
With the default From, instalments due before 1 January of that year are **not listed** (the page says so); lower From to include them.
Customer totals are sums of the *listed* instalments.

## 3. Towers

`dbo.CollectionsTowers (TowerId, TowerNumber, TowerName, CompanyId, IsActive)` is the manually created table; `TowerId` is a local identity and
is **not** a CRM project id. `V001` only reconciles it (see script header): it never drops, truncates, re-seeds or retypes, and reports duplicates
`(CompanyId, TowerNumber)`, non-supported companies, padded/odd numbers, missing Faradis (127) / Al Ghaf (140) on company 32, and inactive towers.

* **Tower number** = unit code without an optional leading `TP`, text before the first hyphen: `TP124-1001`/`124-1001` -> `124`,
  `TP136-C-402`/`136-C-402` -> `136`. SQL `dbo.fn_CollectionsTowerNumber` and C# `CollectionsTowerNumber.Parse` implement the same rule.
  `ProjectCode` (blank in the observed output) is never used.
* **Match** = `CompanyId` **and** `TowerNumber` (trimmed text compare, so the table column may be numeric or text). Same number on two companies = two towers.
* **Unmatched towers** (e.g. `TP119` while 119 is not in the seed): the receivables stay in **All towers**, show the derived number with no name,
  and are listed in the status panel (count and amount, reason `NoMatchingTower` / `InactiveTower` / `NoTowerNumber`). No name is invented; add the
  tower to `dbo.CollectionsTowers` to make it selectable.
* Dropdown: `GET /api/collections/receivables/towers` (active towers, "number - name"), a searchable combobox that degrades to a plain `<select>`.

## 4. Freshness, failures and export

* The status panel shows the **last successful refresh**, per-company data state (Fresh / Stale / Not loaded), last attempt result with failure count and
  error number (no server names or messages), row counts, covered due-date range and exclusions.
* **Not loaded** (never refreshed) is never an empty list: if no company in scope is loaded the page shows "unavailable ... not an empty result"; if one is
  missing the other's rows are shown with a warning and the missing company is flagged.
* **Stale** = last success older than `MaxAgeMinutes` (default **90**). A failed refresh keeps serving the previous snapshot and the age keeps growing.
* **Export requirement (review and Genesys CSV):** every company in scope is loaded **and** no older than `MaxAgeMinutes`; otherwise the preview marks rows
  `NeedsReview/StaleSource`, export links are hidden and the API refuses with 400 "not fresh enough to export". Export re-reads the same snapshot with
  the same tower/window/search as the preview and contains exactly those rows (all pages). A tower of a healthy company can still be exported while the other company is failing.
* Choose `MaxAgeMinutes` > refresh interval + longest refresh duration (30 min cadence -> 90 min leaves room for two missed runs).

## 5. Scheduling (SQL Server Agent is **not** used)

`Agent XPs` is disabled, so the refresh is an application Hangfire recurring job `collections-receivables-refresh` (ADR-0015, same Hangfire server and
`HangfireSla` schema as the other jobs). Requirements:

1. `BackgroundJobs:Enabled = true` and the API process running continuously (Hangfire server). With it off, nothing refreshes and the pages show the data as stale / not loaded.
2. `Collections:ReceivablesSnapshot:RefreshEnabled = true` (default); `RefreshOnStartup = true` triggers one run at start-up.
3. The `TigerCsDatabase` login can `EXECUTE` the `usp_Collections_*` procedures and read/write the snapshot tables.
4. Multiple API instances are safe (application lock). Alternatives if the API cannot host Hangfire: a Windows Task Scheduler / cron job running
   `sqlcmd -S 10.10.10.117 -d TigerCsTicketing -Q "EXEC dbo.usp_Collections_RefreshReceivables @TriggerSource=N'Scheduler'"` every 30 minutes, or enable SQL Agent and add a job with the same statement.
   Set `RefreshEnabled=false` if you use one of those.

## 6. Configuration (no credentials)

`appsettings` section `Collections:ReceivablesSnapshot` (defaults shown):

| Key | Default | Meaning |
| --- | --- | --- |
| `UseLocalSnapshot` | `true` | `false` = legacy direct PACT read (escape hatch; Hangfire job is then removed). |
| `RefreshEnabled` / `RefreshCron` / `RefreshOnStartup` | `true` / `*/30 * * * *` / `true` | Hangfire schedule (server local time). |
| `MaxAgeMinutes` | `90` | Freshness limit (preview warning, export gate). |
| `SourceFromDate` / `SourceThroughDate` | `2000-01-01` / `2099-12-31` | Due-date coverage requested from PACT. Requests outside the loaded coverage are rejected, never truncated. |
| `SourceMinAmount` | `0` | Keep 0. |
| `MaxRawRows`, `MaxShrinkPercent`, `RefreshCommandTimeoutSeconds`, `ReadCommandTimeoutSeconds` | `1000000`, `60`, `1800`, `60` | Guards and timeouts. |
| `ConnectionStringName` | `TigerCsDatabase` | Name only. The connection string itself comes from `ConnectionStrings:TigerCsDatabase` via the deployment secret store / environment variable `ConnectionStrings__TigerCsDatabase`; the PACT linked-server login mapping lives in SQL Server, not in the application. |

`CollectionsSource:PactReceivables:ApplyLegacyExclusions` is **not supported** with the snapshot (the read fails with a clear message); it is `false` in the shipped settings.
The linked server name and PACT database are the two `DECLARE`s at the top of `V005` (`10.10.10.94` / `PACTRPT`).

## 7. Deployment order

1. Back up `TigerCsTicketing`. Run `tests/probe_pact_result_shape.sql` as the application login (**needs V003 first**, so run V001-V003, then the probe).
   It reports which column layout each deployed procedure returns (company 32 has never been run through the linked server), counts of zero / negative / UnitID 0 / blank-tenant rows,
   status values, and tower-number matching. **Do not enable the job before reading it.**
2. Run `V001` ... `V006` in order (idempotent; `CREATE OR ALTER`/`IF NOT EXISTS`). Review `V001`'s reports (duplicates, missing 127/140).
3. `EXEC dbo.usp_Collections_RefreshReceivables @TriggerSource = N'Manual';` in SSMS. Expect two result sets: run summary and per-company outcome. Re-run `@CompanyId = 32` alone if needed.
   If INSERT-EXEC fails with 8501/7391 (distributed transaction): the procedure already sets `REMOTE_PROC_TRANSACTIONS OFF`; otherwise MSDTC or the linked server's
   *Enable Promotion of Distributed Transactions for RPC* option must be adjusted by the DBA.
4. Optional: `tests/smoke_snapshot_publish_and_read.sql` on a development copy (rolls back).
5. Deploy the application with `BackgroundJobs:Enabled=true`; the first refresh runs at start-up.

## 8. Verification status

Verified locally: the .NET solution builds and the full test suite passes (new tests: tower mapping, date boundaries, Due/Overdue rules, partial refresh failure,
freshness gate, export == preview, script contracts, endpoints, render). The tower picker and campaign default-date scripts were exercised in headless Chromium.

**Not verified (needs real SQL Server / PACT):**

* None of the T-SQL has been executed (no SQL Server here); `Msg` names/numbers (213 for shape mismatch) and `INSERT ... EXEC` over the linked server (MSDTC) are untested.
* The deployed procedures' exact result shape, column order and types - especially **company 32** - are unconfirmed; `V005` tries four layouts and fails safely with nothing published.
* Execution time and total row count of the unfiltered (`@MinAmount = 0`, wide window) call, which includes every paid instalment since 2000 (the earlier
  measurement for company 4 was ~58 s with a filtered result). If this is too slow or exceeds `MaxRawRows`, narrow `SourceFromDate` (and accept the coverage limit) or use the V2 procedures.
* That `Amount`/`Status` semantics match the deployed definitions (they are taken from the reviewed older definitions) and the tower seed list vs real unit codes (e.g. 119).
* Real counts of excluded UnitID 0 / blank-identity rows and the unmatched-tower list.
