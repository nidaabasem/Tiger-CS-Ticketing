# Receivables snapshot: tower, date window, background refresh

The **Due & Overdue Customers** page and **Collections Campaigns** read a **local snapshot** of the PACT receivables in
`TigerCsTicketing`. A background job refreshes it; a page view never calls PACT. Filters are **Tower + From + To** (plus status,
stage and search). There is no company selector: the company is resolved from the tower.

Deployment scripts: [`database/collections-receivables/`](../../database/collections-receivables/). Nothing here is deployed
automatically. **Nothing has been run against the real PACT server or the production Ticketing database**; everything was exercised on a local SQL Server 2022 with synthetic data (see "Verification status").

## 1. How it works

```
Hangfire recurring job (every 30 min, + once at startup)
  -> EXEC dbo.usp_Collections_RefreshReceivables                       (Ticketing DB, application login)
       per company (4, then 32), independently:
         INSERT ... EXEC [10.10.10.94].[PACTRPT].[dbo].[p4|p32AccountReceivables] -> dbo.CollectionsReceivableStaging
         dbo.usp_Collections_PublishReceivablesStaging: validate -> insert under a NEW RunId -> flip CurrentRunId
Pages -> API -> dbo.usp_Collections_GetReceivablesPage / GetInstalmentsPage / GetCampaignUnits (tower + window + filters + aggregation + paging in SQL)
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

Defaults: **From = the configured receivables start date** (`CollectionsSource:PactReceivables:StartDate`, 2026-01-01 by default; merged with the review workflow, which found that a calendar-year floor silently empties the overdue and legal stages from January onwards). **To** = month end for *Current-month* and *Follow-up*
stages and for the Receivables page (so Due and Overdue are both complete); for the other campaign stages the preview date. The inputs are
pre-filled with the effective values; in Campaigns, unedited defaults follow stage/preview-date changes in the browser and edited dates are kept.
The start date is **only the default From, never a minimum**: any From/To within 2000-2100 can be selected (e.g. across a year boundary). With the default From, instalments due before the start date are **not listed**; lower From to include them.
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

### Receivables views: By unit (default) and By instalment

`/Collections/Receivables?view=units|instalments` (kept in every filter, pagination and "Last 6 months" link; By unit is the default). Both views apply **identical filters** (tower, due-date window or month, payment status, minimum amount, search).
**By unit** shows one row per unit - tower, unit, customer, instalment count, total remaining, oldest due date - and expands in place to the unit's instalments. Units are grouped **in SQL before paging**
(`usp_Collections_GetInstalmentUnitsPage`, V008), so all matching instalments of a unit are on the same page, and a unit is (company, customer id, unit id, unit code) compared binary-exactly: other companies, other customers (even ids differing by case) are never merged.
Unit counts/amounts always add up to the instalment view's totals (tested against the real procedure over 12 filter scenarios and a full page walk). The API default stays `view=instalments`; the page sends `units`.

**Month overview.** Above the results, one card per due month (e.g. "Sep 2026": overdue instalment count and total remaining) plus "All months". Clicking a card adds `dueMonth=yyyy-MM`, which narrows the list and its totals to that month in both views; the selected card is highlighted and "All months" clears it. The cards come from `usp_Collections_GetInstalmentMonths` (V009) over the **whole** filtered set (tower, dates, payment status, minimum, search) - before paging and ignoring the month selection - so a card's count and remaining always equal the filtered list's totals for that month. Overdue is unchanged: remaining > 0 and due before the first day of the current Dubai month.

## 4c. Performance and responsiveness

* **Page GET never waits for data or PACT.** The page (filters + tower list, a small cached local read) renders at once; the results area is then fetched as an HTML fragment (`?handler=Results`) by `receivables.js`.
  Only the results area shows loading, errors ("not an empty result" + Retry) and progress; the submit button is always restored (success, failure, superseded request, 120 s watchdog). `?render=full` is the no-JavaScript fallback.
  `Server-Timing` on the fragment reports `web`, `api`, `sql`, `map` milliseconds; the API DTOs carry `Timings`; the refresh logs `fetch from PACT / validate / publish` milliseconds per company (and stores them in `CollectionsReceivableRunCompany`).
* **No snapshot yet?** The page still renders its filters; the results area says "No receivables data has been loaded yet" with a working **Load data** button (background load, progress polled every 10 s, no page reload).
  A partly missing range or company offers **Load missing data**. Before the first successful snapshot nothing is presented as zero receivables, export is blocked and Fully paid / All stay disabled.
* **SQL does the work.** Filtering, per-apartment aggregation (Receivables apartment API), instalment totals and `OFFSET/FETCH` paging run in `usp_Collections_GetInstalmentsPage` / `usp_Collections_GetReceivablesPage`; only one page (<= 100 rows) reaches the application.
  Indexes: clustered `(CompanyId, RunId, DueDate)`, `(CompanyId, RunId, TowerNumber)`, `(CompanyId, RunId, TenantId, UnitId)`; the unmatched-tower report reads a per-run summary table instead of scanning the snapshot.
  **Campaigns** are evaluated in SQL too (`usp_Collections_GetCampaignUnits`, section 4d); the application no longer loads the window's instalments for them.
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

## 4d. Campaigns: filtering, per-unit evaluation, flags, totals and paging in SQL

**Problem.** The Campaigns preview read *every instalment of the date window* (157 k rows in the default window of the 441 k-row synthetic snapshot), grouped them per unit in C# and only then paged:
3.1 s idle, **8.4 s median / 14.9 s worst while a refresh published**, 475 MB allocated per request, 660 MB peak working set. A loading indicator alone could not fix that.

**What runs where now.** `usp_Collections_GetCampaignUnits` (V007) does, for the requested tower / company / window / minimum amount / stage:

1. window scan of the *current* run only, from a narrow covering index (`IX_CollectionsReceivableSnapshot_Campaign`), aggregated per (unit, due day) on **integer keys**;
2. per-unit stage amount: stage rows = due day in the stage range, `SUM(remaining)`, ambiguous when two stage rows share a due day (amount `NULL`, still a candidate, needs review), otherwise `SUM > threshold` (0 / 1,500 / 20,000);
3. **all unit-level review flags over the whole result set, before paging**: ambiguity, amount precision, missing unit identity, conflicting contact details, tenant-level unit-allocation ambiguity, contradictory `Paid` status, no valid contact;
4. search (name, customer id, unit code, normalised phone/e-mail, phone digits), totals (`TotalUnits`, `CleanUnits`, `ReviewUnits`) and `OFFSET/FETCH` paging in the order company, tenant (binary), unit code (binary), unit id.

The application only adds the *global* gates that do not depend on the unit (stale source, incomplete coverage, currency, financial-source validation, outside-schedule, legal-notice release, internal legal referral) and derives
Ready / Needs review / Preview only / Internal review. Because every unit-level reason makes a unit "Needs review" regardless of the global gates, the Ready and Review **counts are exact over the whole filtered set** without materialising it.
An **export** asks for the bounded full result (`MaxExportRows + 1` units; more than the limit is refused with no file, as before); the CSV contract is unchanged and both export modes use the same gates.

**Review flags that need application code (bounded, precomputed).** Phone and e-mail validity is the application's rule (`CollectionsContactNormalizer`: E.164 for UAE numbers, `System.Net.Mail` for e-mail) and is *not* re-implemented in T-SQL.
Each run's publish registers every distinct phone / e-mail text in `dbo.CollectionsContactNorm` and records, per unit, whether it is valid once normalised. The application normalises what is missing (typically only the new customers of the last refresh, done right after the refresh by the job and otherwise lazily on first read) and
`usp_Collections_SyncUnitContacts` records the result on the units. The dictionary is a pure function of the text and versioned (`CollectionsContactNormalizer.Version`), so stored values stay valid until a rule change.
Units whose rows all carry one identical (name, phone, e-mail, project) - nearly all - need no per-row work; the few "Variants" units are resolved exactly from their own rows (first row by due date, voucher, row id; distinct normalised tuples).

**What falls back to the old in-memory evaluation (never to an empty list):** a snapshot published before V007 (`ExoticTextRows IS NULL`), or one holding tenant / unit / name / status text with leading/trailing whitespace that .NET trims but T-SQL cannot (`ExoticTextRows > 0`), or any other `IPactReceivablesSource`.
The in-memory evaluation stays in the code as the **reference implementation** the SQL engine is tested against.

**Independent of PACT refresh.** Preview and export only read the local snapshot (`CurrentRunId` rows by index range). The service has no dependency on the refresher or the range loader (a unit test pins that); there are no dirty-read hints.
The publish step prepares the *new* run before the pointer flip (batches of 1,500 rows, same as the snapshot rows), so readers never see an unprepared run.

### Measurements (same 441 k-row synthetic snapshot, 4 vCPU sandbox, service call incl. mapping; stage Overdue, default window, minimum 100, page 1)

| | Before (in memory) | After (SQL engine) |
| --- | --- | --- |
| Idle: p50 / p95 / max | 3,095 / 3,297 / 3,433 ms | **421 / 476 / 542 ms** |
| **During a refresh** (same service call in a loop while the real refresh procedure publishes): p50 / p95 / max | 8,357 / 9,963 / 14,860 ms (16 calls) | **725 / 1,270 / 1,634 ms** (92 calls) |
| Allocated per request | 475 MB | 0.1 MB |
| Peak working set of the test process | 660 MB | 267 MB (runtime baseline included) |
| Reader lock waits during the refresh | n/a | 0 ms (0 waits) |
| Stored-procedure time only (`bench_campaign.py`): idle p50 / p95; during refresh p50 / p95 / max | n/a | 485 / 522 ms; 887 / 1,537 / 2,028 ms |

Target "preview p95 < 2 s" is met idle and during the refresh in this environment (p95 1.27 s through the service, 1.54 s for the procedure alone; the worst single call was 2.03 s for the procedure and 1.63 s through the service - there is little margin on a 4-vCPU box shared with the refresh).
Where the idle ~0.42 s goes (procedure, warm): window scan + per-(unit, day) aggregation 160 ms, per-unit aggregation 85 ms, candidate join with the unit table 75 ms, tenant-ambiguity check ~35 ms, flags 35 ms, page ~5 ms. Further reductions would need a columnstore index or
a precomputed per-month aggregate, both rejected for now (operational cost, minimum-amount semantics).
The first call after a deployment or plan-cache eviction pays ~1.4 s of plan compilation.

**Plans, indexes, locks.** The window scan is an `Index Seek` on `IX_CollectionsReceivableSnapshot_Campaign` (covering: no key lookups; 67 MB against 264 MB for the clustered index over 884 k rows), then a parallel `Hash Match (Aggregate)` (captured with `SET STATISTICS XML`).
Publishing with the extra preparation stayed in the range seen before it (company 4: 16-22 s, company 32: 5-8 s per publish across runs; whole refresh 75-120 s here with paid rows retained, dominated by the loopback fetch and the batched inserts).
The reader waited **0 ms on locks** in each of the three measured publications and there were no deadlocks or errors; the preparation uses the same 1,500-row committed batches as the snapshot rows, and `usp_Collections_SetContactNorms` / `usp_Collections_SyncUnitContacts` stay below lock escalation.
Dictionary growth: one row per distinct phone / e-mail text ever seen (50 k rows here); old entries are not pruned (they are tiny and a pure function of the text).

**Result equivalence (real SQL Server).** `RealSqlCampaignEquivalenceTests` run the same service over the same snapshot twice - SQL engine and in-memory reference - for 90 scenarios (5 stages x 18: default / minimum 0 / 100 / 1000 / 99.99, company 4 / 32, towers 103 / 127, month and wide windows, searches incl. LIKE wildcards, brackets, apostrophes,
Arabic text, phone and unit-code terms, legal-notice release on/off) and compare **every field of every row in order on first / second / last page, the totals, the snapshot status and both CSV exports byte for byte** (or the identical refusal). Hand-written edge cases (`tests/local-sqlserver/03_edge_cases.sql`: ambiguous same-day instalments, first-row tie-breaks,
contradictory status in any case, tenant allocation ambiguity (all three kinds), phones that normalise equal, name case / spacing, project trailing space, invalid / valid e-mails, threshold boundaries 1,500 / 1,500.01 / 1,499.99 / 20,000 / 20,000.01, minimum boundaries 99.99 / 100 / 100.01,
amount precision, blank and `0` unit codes, tenant id case) are loaded; a test asserts every review flag occurs in the results. Further tests: lazy normalisation (dictionary and units reset), fallback when the snapshot is unprepared or contains edge-whitespace text, stored normalisations equal the C# functions. 98 tests, all passing.
A first run found a real bug (SQL three-valued logic made a search that matched nothing return rows); it is fixed and covered. After the merge the apartment-list procedure was also made binary-exact (tenant ids that differ only by letter case are different customers, as in the application).

## 4e. Review, approval and Genesys dispatch on the snapshot (merge of #80)

The review refresh, the dispatch revalidation and the paid-after-upload sweep read the **same local snapshot** as the Campaigns preview, so the three flows agree:

* **Same filters.** All of them ask the source for the configured start date and the **same minimum outstanding amount** (`DefaultMinOutstandingAmount`, 100; remaining >= value per instalment) - a 99.99 instalment is nobody's candidate, a 100.00 one is everybody's.
  The stage rule, the amount rule, the phone rule (`Review.PhoneNormalizer`, also used by the campaign engine's contact dictionary) and the money normalisation are single definitions shared by preview, review and the SQL engine. A test pins that preview, review records, export and the contacts uploaded to the (scripted) Genesys client carry identical units and amounts.
* **Payment status.** `usp_Collections_GetReceivables` now also returns `OriginalAmount` / `PaidAmount` (NULL unless the source returned and reconciled them), which become the rows' `PlanAmount` / `AllocatedAmount`; with the deployed procedures they stay NULL and every open balance stays "Unknown" (unsendable), exactly as with a live read.
* **Freshness is a dispatch gate, not a hint.** Revalidation requires a snapshot that is fresh (`MaxAgeMinutes`) and covers the window; otherwise it throws and the dispatch ends *Failed* with nothing sent (previously a missing record meant "paid or settled"; with a stale local snapshot that would have excluded everything). The review refresh refuses a snapshot that is not loaded or does not cover the window; a merely stale snapshot is still flagged on every record through its read time. The approval check (exact count + fingerprint), idempotency, lease, revalidation of amounts and per-type batching are untouched.
* **Cost.** Review and revalidation no longer call PACT per run; they read the snapshot (one read per company). Revalidation therefore sees data up to one refresh interval old, bounded by the freshness gate.
* Customer data: nothing was sent anywhere in these tests; the Genesys client is a scripted fake and all contacts are synthetic.

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
2. Run `V001` ... `V009` in order (idempotent; `CREATE OR ALTER` / `IF NOT EXISTS` / guarded `ALTER TABLE ... ADD`). **Re-running V002 is required on a database that already has an earlier revision of these scripts**: it adds
   `Snapshot.OriginalAmount/PaidAmount/PaymentStatus`, `CompanyState.PaidRetained/BreakdownAvailable/UnclassifiedRows`, the staging columns, the `RunCompany` timing columns, `IX_..._Apartment` and `CollectionsReceivableTowerSummary`;
   `V002` also adds `Snapshot.UnitSeq/TenantSeq/RowFlags`, `IX_..._Campaign` (**creating it reads the whole table once**; do it outside peak hours), `CompanyState.ExoticTextRows`, `CollectionsContactNorm`, `CollectionsReceivableUnit(Text)`.
   then V004, V005, V006, V007 (procedures replaced). **Until the first refresh after this upgrade the Campaigns still work but use the slower in-memory evaluation** (the current run is not prepared); the refresh prepares the new run and the job normalises the contact values right after it. Review `V001`'s reports (duplicates, missing 127/140).
3. `EXEC dbo.usp_Collections_RefreshReceivables @TriggerSource = N'Manual';` in SSMS. Expect two result sets: run summary and per-company outcome (now incl. FetchMs / ValidateMs / PublishMs). Re-run `@CompanyId = 32` alone if needed.
   **The first refresh after upgrading rewrites the snapshot with paid rows, statuses and the campaign preparation**; until it completes the pages show the previous data (rows without a verified status read "Needs verification"; Fully paid / All stay disabled because `PaidRetained` is still 0).
   If INSERT-EXEC fails with 8501/7391 (distributed transaction): the procedure already sets `REMOTE_PROC_TRANSACTIONS OFF`; otherwise MSDTC or the linked server's
   *Enable Promotion of Distributed Transactions for RPC* option must be adjusted by the DBA.
4. Optional: `tests/smoke_snapshot_publish_and_read.sql` on a development copy (rolls back). For the unpaid/partial breakdown: deploy the reviewed companion procedures separately (after reconciliation), then
   `EXEC dbo.usp_Collections_RefreshReceivables @ProcedureSuffix = N'V2'` once to prove the layout, and set `SourceProcedureSuffix` = `V2` in configuration.
5. Deploy the application with `BackgroundJobs:Enabled=true`; the first refresh runs at start-up. New settings (all optional): `RetainPaidInstalments`, `SourceProcedureSuffix`, `StrictIdentity`, and `CollectionsSource:PactReceivables:DefaultMinOutstandingAmount` (100).

## 8. Verification status

**Real SQL Server (local SQL Server 2022 Developer on Linux, synthetic PACT data behind a loopback linked server named `[10.10.10.94]`)** - run during development, repeatable with `database/collections-receivables/tests/local-sqlserver`:

* V001-V007 deploy cleanly - **also on an empty database** (a clean install initially failed in V002: the staging-table upgrades ran before the table existed; fixed, and the rolled-back smoke test now also checks the campaign preparation); the real `usp_Collections_RefreshReceivables` runs end to end (`INSERT ... EXEC` through a linked server, both shapes: deployed layout and companion layout), validation, batched publish, pointer flip and cleanup.
* 98 more .NET tests (`RealSqlCampaignEquivalenceTests`, section 4d) pass against that server, besides the 28 `RealSqlSnapshotTests`: SQL-paged apartment lists equal the application-side rules; the instalment-page procedure equals the in-memory reference model for 12 scenarios (month, range, tower,
  outstanding/unpaid/partial/paid/all, minimum, search, paging, "Unavailable" for views the data cannot back) in **both** data shapes; the stored payment status equals the domain classifier for every row.
* Bugs found only by running it (fixed): `SUM(int)` vs bigint reader mismatch, `MAX(bit)`, a reader/cleanup deadlock, lock behaviour of the publish.
* Timings and contention above (synthetic data, 4 vCPU, loopback "PACT"). **Not real-PACT or production measurements.**

**Fakes only (also run in CI, no SQL Server needed):** service rules (month boundaries, leap February, year change, payment classification, minimum amounts, preview/export consistency, partial refresh failure, freshness gate), page rendering and
the Load/progress flow, API authorization and parameter pass-through, static contract tests of the SQL scripts.

**NOT verified - needs the real PACT / production-like SQL Server:**

* The **deployed p4/p32 result shape and column order** (company 32 never run through the linked server), whether `@MinAmount = 0` really returns paid rows, and that `Status` is `Paid`/`Installment` - everything about the live procedures is taken from the reviewed definitions. Run `tests/probe_pact_result_shape.sql` first.
* `INSERT ... EXEC` across a **real** linked server (MSDTC / distributed transaction behaviour; the loopback test cannot reproduce it) and the **real** PACT ledger time and row volume (paid rows since 2000, fan-out duplicates).
* The companion `V2` procedures against real data, and whether their corrected allocation matches the business's expectation (until then Unpaid/Partially paid stay disabled and positive balances read "Needs verification").
* Production-scale latency: the numbers above are a 4-vCPU sandbox with synthetic data; CPU, IO, memory and the real data distribution will differ. Re-run the harness against a copy of the production snapshot.
* Real counts of excluded UnitID 0 / blank-identity rows and the unmatched-tower list (e.g. tower 119).
