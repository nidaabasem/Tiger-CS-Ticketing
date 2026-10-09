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
1 January is **only the default From, never a minimum**: any From/To within 2000-2100 can be selected (e.g. across a year boundary). With the default From, instalments due before 1 January of that year are **not listed** (the page says so); lower From to include them.
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

## 4a. Custom ranges, "Last 6 months" and snapshot coverage

* **Last 6 months** (button on both pages): From = six calendar months before the preview date, To = the preview date, both included
  (`2026-03-15` -> `2025-09-15`..`2026-03-15`; a shorter target month clamps, `2026-08-31` -> `2026-02-28`). It is a plain link with explicit
  dates, so it also overrides the stage default; the preview date is today (Dubai) on the Receivables page and the selected preview date on Campaigns.
* **Coverage** = the due-date range each company's snapshot holds (`CoverageFromDate`..`CoverageThroughDate`, default 2000-01-01..2099-12-31). The page compares the
  *requested* From/To with it. A part outside coverage is a **gap**: the page shows "Additional data needs loading" with the exact missing dates per company,
  marks the list **incomplete** (the count is shown as `n+`; missing instalments are never presented as zero receivables), and nothing is truncated silently.
* **Load missing data** (button, `POST /api/collections/receivables/coverage/load?dateFrom&dateTo`): starts a background job (Hangfire; in-process fallback when Hangfire is
  off) that calls `usp_Collections_RefreshReceivables` for the requested range. The procedure *extends* each company's coverage (union with what is stored,
  `@ExtendCoverage = 1`), so later scheduled refreshes keep it. It is single-flight (application lock); while it runs the pages show "a load is running"
  (`RefreshInProgress`) instead of the button. Same permission as reading the list. If it fails the previous snapshot is unchanged and the failure shows in the status panel.
* **Export** (review and Genesys) is blocked until the **whole requested range is covered AND every company in scope is fresh**; preview rows are `NeedsReview`
  with `CoverageIncomplete` meanwhile. Due/Overdue rules, campaign stage eligibility and PACT's accounting are unchanged: only which instalments are fetched changes.
* Cost: loading a very wide range means a long PACT call (the earlier measurement for company 4 was ~58 s for a filtered result). Narrow `SourceFromDate`
  in configuration if the default 2000-2099 load is too heavy; any range outside it is then loaded on demand through the button.

## 4b. Instalment list, payment status, month/year and the minimum amount

The **Receivables page** lists **instalments** (one row each): Tower, Unit, Customer, Voucher, Due date, **Original instalment**, **Paid**, **Remaining**, **Payment status**
and **Due / Overdue**. It reads `GET /api/collections/receivables/instalments` (local snapshot only). The older apartment-level `.../receivables/customers` API is unchanged for its consumers.

* **Month + Year** selectors set From/To to the first and last day of the month (leap February, year changes: `2028-02-01..2028-02-29`, `2026-12-01..2027-01-31` are separate months).
  A custom From/To range and **Last 6 months** stay available; editing a date returns the selectors to "Custom range". Month/Year are not a separate filter - they are only a shortcut for From/To.
* **Payment status** (separate from Due/Overdue, so an instalment can be *partially paid and overdue*):

  | Choice | Rows | Needs |
  | --- | --- | --- |
  | **Outstanding** (default) | remaining balance > 0: unpaid + partially paid + rows whose status the source cannot tell | nothing |
  | Unpaid / Partially paid | verified `Unpaid` / `PartiallyPaid` | the source returned **original and paid amounts** (companion procedure) |
  | Fully paid | remaining = 0 and verified paid | the snapshot **retains paid instalments** (`RetainPaidInstalments`, default on) |
  | All | every retained instalment | same as Fully paid |

  A choice the loaded data cannot back is **disabled in the UI and refused by the API** (400 with the reason) - never shown as an empty list.
* **Minimum outstanding amount (AED)**, default **100**, any non-negative decimal (1000, 250.5, 0 ...): an instalment is shown/counted only if its **remaining unpaid amount >= the value** (inclusive).
  It is applied to the instalment rows *before* counting, summing, paging and exporting, so rows, counts, totals, pages and both campaign CSVs agree, and it is carried by every URL (tower, dates, page, export).
  For **Fully paid** and **All** the field is **disabled and ignored** (the comparison is on the *remaining* balance, which is 0 for a paid row - it would silently hide them); the text beside the field says so.
  Campaigns apply it per instalment too, *before* the stage rules (a unit's qualifying balance is the sum of its instalments that pass the minimum; the legal-notice/referral thresholds then apply to that sum).
  The snapshot always holds every positive balance (the refresh uses `MinAmount = 0`), so lowering the minimum never needs a PACT reload.
* **Due / Overdue** is unchanged and date based: Overdue = due before the current (Dubai) month, Due = inside it, **Not yet due** = after it (never overdue; previously such rows were simply not listed).
  Fully paid rows show "—" (not applicable) and are never counted as due/overdue. Totals sum the *remaining* balance of the filtered rows.
* **Campaigns** use outstanding balances only. **Fully paid instalments are never reminder candidates** (also enforced in the application). The campaign date column is labelled **Earliest unpaid due date**
  and is the earliest due date among the *qualifying instalments that still have a remaining balance* (the CSV keeps its `DueDate` column name - the Genesys import contract is unchanged).

### Payment status: what the deployed procedures can and cannot tell

Verified from the reviewed definitions (`pact-sql/Receivables-Source-Review.md`) - **not** from the live procedures, which were not accessible: `p4/p32AccountReceivables` compute the instalment plan amount
(`PlanFutureAmount`) and the allocated payment internally but return only the **remaining** `Amount` and `Status` (`Paid` when remaining is 0, otherwise `Installment`).
Therefore with the deployed output:

* **Fully paid** is verifiable (remaining 0 and `Status = Paid`);
* every positive remainder is stored as **Unknown - shown as "Needs verification"**: `Installment` does not distinguish unpaid from partially paid, and **the original instalment amount and paid amount are not returned, so they are shown as "—" (not provided), never estimated or taken from the contract value**.

To get verified Unpaid / Partially paid / original / paid values the source must return them. The reviewed companion drafts `p4AccountReceivablesV2` / `p32AccountReceivablesV2` (new procedure names; the deployed ones are never altered, existing consumers are unaffected)
add `PaymentTermAccountId, PlanAmount, AllocatedAmount` and `@IncludeSettled`. **They are not deployed or reconciled, and they correct the allocation arithmetic (defects D1-D5), i.e. their numbers can differ from today's.**
Deploy them only after the reconciliation scripts (`receivables-reconciliation.company*.read-only.sql`) are reviewed, then set `Collections:ReceivablesSnapshot:SourceProcedureSuffix` to `V2`.
The refresh stores a status only when `Plan - Allocated = Remaining` (+-0.01) for that row (otherwise Unknown) and exposes `BreakdownAvailable` per company; the UI enables Unpaid/Partially paid only when every company in scope has it.

### Paid instalments in the snapshot

With `RetainPaidInstalments = true` (default) the refresh keeps `Amount = 0` instalments as `FullyPaid` (the deployed procedures return them when called with `MinAmount = 0`; the V2 draft with `@IncludeSettled = 1`).
`PaidRetained` is recorded per company **only if the source really returned paid rows** and no minimum hid them. This roughly doubles the stored rows (local test: 202k -> 442k) and the first-refresh time;
set it to `false` for an outstanding-only snapshot - Fully paid / All are then disabled, not faked. `usp_Collections_GetInstalmentsPage` returns `Unavailable` instead of rows when the requested view needs data the snapshot does not hold.

## 4c. Performance and responsiveness

* **Page GET never waits for data or PACT.** The page (filters + tower list, a small cached local read) renders at once; the results area is then fetched as an HTML fragment (`?handler=Results`) by `receivables.js`.
  Only the results area shows loading, errors ("not an empty result" + Retry) and progress; the submit button is always restored (success, failure, superseded request, 120 s watchdog). `?render=full` is the no-JavaScript fallback.
  `Server-Timing` on the fragment reports `web`, `api`, `sql`, `map` milliseconds; the API DTOs carry `Timings`; the refresh logs `fetch from PACT / validate / publish` milliseconds per company (and stores them in `CollectionsReceivableRunCompany`).
* **No snapshot yet?** The page still renders its filters; the results area says "No receivables data has been loaded yet" with a working **Load data** button (background load, progress polled every 10 s, no page reload).
  A partly missing range or company offers **Load missing data**. Before the first successful snapshot nothing is presented as zero receivables, export is blocked and Fully paid / All stay disabled.
* **SQL does the work.** Filtering, per-apartment aggregation (Receivables apartment API), instalment totals and `OFFSET/FETCH` paging run in `usp_Collections_GetInstalmentsPage` / `usp_Collections_GetReceivablesPage`; only one page (<= 100 rows) reaches the application.
  Indexes: clustered `(CompanyId, RunId, DueDate)`, `(CompanyId, RunId, TowerNumber)`, `(CompanyId, RunId, TenantId, UnitId)`; the unmatched-tower report reads a per-run summary table instead of scanning the snapshot.
  **Campaigns** still read the window's instalment rows (now already limited by tower, window and minimum) because their per-unit review flags (ambiguity, contact validity, stage thresholds) run in the application; they are loaded asynchronously, but this is the one remaining path that materialises a large set.
* **Publishing does not block reads, without dirty reads.** The refresh inserts a *new run* in committed batches of 1,500 rows (below lock escalation), flips `CurrentRunId` in a short transaction (a few single-row updates), and keeps the replaced run for one more cycle before deleting older runs in batches.
  Readers only touch `CurrentRunId` rows by index seek. Measured: see below. `NOLOCK`/`READ UNCOMMITTED` are not used anywhere (a contract test enforces it). A **reader/cleanup deadlock** found during this work (cleanup deleting the run an in-flight reader was still scanning) is fixed by keeping the replaced run.

### Measurements (local SQL Server 2022 + synthetic data: ~444k raw rows -> ~441k retained, ~189k outstanding instalments in the default window; 4 vCPU sandbox)

| Scenario (service call incl. SQL, no HTTP/Razor) | Before | After |
| --- | --- | --- |
| Receivables, all towers, default window - apartments API | 2.6-4.2 s, **322 MB allocated**, 200k rows pulled into memory | **0.60-0.65 s, <1 MB**, one page |
| same, tower 5 | 0.12 s | 0.08-0.12 s (unchanged: already small) |
| Instalment list, all towers, outstanding (189k rows) | n/a (new) | 0.50 s (count + totals + page) |
| Instalment list, **one month** (typical use) | n/a | **0.05 s** |
| Instalment list, tower 5 | n/a | 0.11 s |
| Instalment list, "All" (368k rows) / "Fully paid" (169k) | n/a | 0.78 s / 0.43 s |

| During a refresh (reader in a tight loop on the same 4 vCPUs) | Before | After |
| --- | --- | --- |
| Narrow reads | up to **1.7 s** stalls (old publish: one 137k-row INSERT, unbatched cleanup) | max 0.5-0.6 s, lock waits <= 45 ms |
| Deadlocks | an intermediate version (immediate cleanup of the replaced run) produced a reader **deadlock** in 1 of 3 runs - root-caused with a deadlock graph and fixed | **none in the 11 contention runs after the fix** |
| Instalment month read | n/a | median 59 ms (idle 54 ms), max 0.76 s, lock waits 0 ms |
| Instalment list all towers | n/a | median 556 ms (idle 501 ms), lock waits 13 ms |
| Reads while PACT is artificially slow (25 s per procedure) | n/a | median 62 ms: a read never waits for the remote fetch |

Refresh itself (loopback "PACT", so remote time excludes the real ledger work): ~45 s with paid rows retained (was ~20 s without), of which fetch 7 s / validate 0.1-0.3 s / publish 5-10 s per company when idle.

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
| `SourceMinAmount` | `0` | Keep 0 (a higher value permanently hides small balances AND paid rows). |
| `RetainPaidInstalments` | `true` | Keep fully paid instalments so Fully paid / All can be offered (doubles the stored rows). |
| `SourceProcedureSuffix` / `StrictIdentity` | `""` / `false` | `""` = deployed procedures; `"V2"` = a separately deployed companion that returns original + allocated amounts. |
| `MaxRawRows`, `MaxShrinkPercent`, `RefreshCommandTimeoutSeconds`, `ReadCommandTimeoutSeconds` | `1000000`, `60`, `1800`, `60` | Guards and timeouts. |
| `ConnectionStringName` | `TigerCsDatabase` | Name only. The connection string itself comes from `ConnectionStrings:TigerCsDatabase` via the deployment secret store / environment variable `ConnectionStrings__TigerCsDatabase`; the PACT linked-server login mapping lives in SQL Server, not in the application. |

`CollectionsSource:PactReceivables:ApplyLegacyExclusions` is **not supported** with the snapshot (the read fails with a clear message); it is `false` in the shipped settings.
The linked server name and PACT database are the two `DECLARE`s at the top of `V005` (`10.10.10.94` / `PACTRPT`).

## 7. Deployment order

1. Back up `TigerCsTicketing`. Run `tests/probe_pact_result_shape.sql` as the application login (**needs V003 first**, so run V001-V003, then the probe).
   It reports which column layout each deployed procedure returns (company 32 has never been run through the linked server), counts of zero / negative / UnitID 0 / blank-tenant rows,
   status values, and tower-number matching. **Do not enable the job before reading it.**
2. Run `V001` ... `V006` in order (idempotent; `CREATE OR ALTER` / `IF NOT EXISTS` / guarded `ALTER TABLE ... ADD`). **Re-running V002 is required on a database that already has an earlier revision of these scripts**: it adds
   `Snapshot.OriginalAmount/PaidAmount/PaymentStatus`, `CompanyState.PaidRetained/BreakdownAvailable/UnclassifiedRows`, the staging columns, the `RunCompany` timing columns, `IX_..._Apartment` and `CollectionsReceivableTowerSummary`;
   then V004, V005, V006 (procedures replaced). Review `V001`'s reports (duplicates, missing 127/140).
3. `EXEC dbo.usp_Collections_RefreshReceivables @TriggerSource = N'Manual';` in SSMS. Expect two result sets: run summary and per-company outcome (now incl. FetchMs / ValidateMs / PublishMs). Re-run `@CompanyId = 32` alone if needed.
   **The first refresh after upgrading rewrites the snapshot with paid rows and statuses**; until it completes the pages show the previous data (rows without a verified status read "Needs verification"; Fully paid / All stay disabled because `PaidRetained` is still 0).
   If INSERT-EXEC fails with 8501/7391 (distributed transaction): the procedure already sets `REMOTE_PROC_TRANSACTIONS OFF`; otherwise MSDTC or the linked server's
   *Enable Promotion of Distributed Transactions for RPC* option must be adjusted by the DBA.
4. Optional: `tests/smoke_snapshot_publish_and_read.sql` on a development copy (rolls back). For the unpaid/partial breakdown: deploy the reviewed companion procedures separately (after reconciliation), then
   `EXEC dbo.usp_Collections_RefreshReceivables @ProcedureSuffix = N'V2'` once to prove the layout, and set `SourceProcedureSuffix` = `V2` in configuration.
5. Deploy the application with `BackgroundJobs:Enabled=true`; the first refresh runs at start-up. New settings (all optional): `RetainPaidInstalments`, `SourceProcedureSuffix`, `StrictIdentity`, and `CollectionsSource:PactReceivables:DefaultMinOutstandingAmount` (100).

## 8. Verification status

**Real SQL Server (local SQL Server 2022 Developer on Linux, synthetic PACT data behind a loopback linked server named `[10.10.10.94]`)** - run during development, repeatable with `database/collections-receivables/tests/local-sqlserver`:

* V001-V006 deploy cleanly; the real `usp_Collections_RefreshReceivables` runs end to end (`INSERT ... EXEC` through a linked server, both shapes: deployed layout and companion layout), validation, batched publish, pointer flip and cleanup.
* 28 .NET tests (`RealSqlSnapshotTests`) pass against that server: SQL-paged apartment lists equal the application-side rules; the instalment-page procedure equals the in-memory reference model for 12 scenarios (month, range, tower,
  outstanding/unpaid/partial/paid/all, minimum, search, paging, "Unavailable" for views the data cannot back) in **both** data shapes; the stored payment status equals the domain classifier for every row.
* Bugs found only by running it (fixed): `SUM(int)` vs bigint reader mismatch, `MAX(bit)`, a reader/cleanup deadlock, lock behaviour of the publish.
* Timings and contention above.

**Fakes only (also run in CI, no SQL Server needed):** service rules (month boundaries, leap February, year change, payment classification, minimum amounts, preview/export consistency, partial refresh failure, freshness gate), page rendering and
the Load/progress flow, API authorization and parameter pass-through, static contract tests of the SQL scripts.

**NOT verified - needs the real PACT / production-like SQL Server:**

* The **deployed p4/p32 result shape and column order** (company 32 never run through the linked server), whether `@MinAmount = 0` really returns paid rows, and that `Status` is `Paid`/`Installment` - everything about the live procedures is taken from the reviewed definitions. Run `tests/probe_pact_result_shape.sql` first.
* `INSERT ... EXEC` across a **real** linked server (MSDTC / distributed transaction behaviour; the loopback test cannot reproduce it) and the **real** PACT ledger time and row volume (paid rows since 2000, fan-out duplicates).
* The companion `V2` procedures against real data, and whether their corrected allocation matches the business's expectation (until then Unpaid/Partially paid stay disabled and positive balances read "Needs verification").
* Production-scale latency: the numbers above are a 4-vCPU sandbox with synthetic data; CPU, IO, memory and the real data distribution will differ. Re-run the harness against a copy of the production snapshot.
* Real counts of excluded UnitID 0 / blank-identity rows and the unmatched-tower list (e.g. tower 119).
