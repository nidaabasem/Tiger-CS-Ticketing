# Collections stabilisation – findings, fixes, open decisions (October 2026)

Scope: Receivables, Campaigns, Review & Approve, financial reconciliation with PACT, Genesys dispatch.
Honesty note: this work was done from the repository only. **No live SQL Server, PACT, CRM or Genesys was reachable**, so every statement about live data below is either
(a) proven by code and a test in this repository, or (b) explicitly marked *unverified – needs the evidence query named next to it*.

## 1. Receivables repeated polling and "Loading data for Tiger Group Dubai…"

| What was observed | Root cause (code evidence) | Fix |
| --- | --- | --- |
| `Receivables?handler=Results` (~68 KB) repeated every few seconds | `receivables.js` re-requested the **whole results fragment** as its poll (`load()` from `afterRender`), so each poll re-ran the full SQL page read and re-rendered the table. The poll was re-armed by the *presence of the "Loading data" notice in the response*. | Polling now uses a separate tiny JSON handler `?handler=Status` (≈ 300 bytes, one stored-procedure call `usp_CollectionsRcv_GetStatus`, no receivables). The list is re-read **once**, when the server says nothing is loading. |
| Permanent "Loading data…" | After `MAX_POLLS` (180 × 10 s) the loop silently stopped **but left the notice on screen**. Also "Loading" was true for any run row whose SQL session still held the company lock, however long. | Bounded loop (≤ 60 polls / 10 min, back-off 4→15 s) that ends with a visible "stopped checking" message + *Check again*. Server side: a refresh older than its command timeout + 5 min is state **Stuck**, not Loading, so the page stops waiting for it. |
| Possible overlapping requests | Each `load()` aborted the previous one, but the poll timer was a second path into `load()`. | `receivables-load-monitor.js`: one chained loop (next poll scheduled only after the previous answer), generation token (late answers from an older loop are discarded and aborted), cleared on filter change / navigation / `pagehide`, paused while the tab is hidden. |
| Retry showed the failure again immediately | The retry POST enqueues a background job; the immediate re-read saw "Failed" (job not started) and stopped. | After Retry the monitor runs with `expectActive` (30 s grace) so it waits for the job to start. |
| A stale company looked like a failure | `FailedCompanies` lumped *never loaded*, *stale*, *failed* and *coverage gap* under "Could not load data". | Per-company state `Ready / Loading / Stuck / Failed / Stale / NeverLoaded`. Each failed company gets **its own Retry for that company only**; healthy companies show nothing; the last valid data stays on screen with its timestamp. |
| Approval-page polls (`collections-campaign-approve.js`) | Unbounded (`setTimeout` forever, also on errors). | Bounded (20 min refresh / 60 min send, 3 consecutive errors) with a visible stop message. |

Distinct user-visible states now: loading (with start time), stuck, failed (phase + kind + last valid data time), stale (age vs limit), never loaded ("not an empty result"), no data (unchanged list message).

## 2. "Could not load data for Tiger Group Dubai" – where it fails

What the code proves:
* The page can only say *failed* when `dbo.CollectionsRcvCompany.LastStatus` is `Failed`/`Abandoned` for company 4; the real reason is stored in `LastErrorNumber/LastError` and the run row (`CollectionsRcvRun`: `FetchMs`, `RawRows`, `ErrorNumber`, `ErrorMessage`).
* A refresh is one call of `usp_CollectionsRcv_Refresh` (PACT linked-server `INSERT…EXEC` of `p4AccountReceivables`, validate, publish in one transaction). A failure **before the fetch finished** (`FetchMs IS NULL`) is in the linked-server read; error 50010-50013 are our own validation / shrink guard / publish checks; anything else after the fetch is the publish.
* The previous snapshot is always kept (publish is atomic); a failed run never updates `CurrentRunId`, so partial data can never be marked complete. Verified by existing tests plus the new state tests.
* `BackgroundJobs:Enabled` is **false** in the committed `appsettings.json`. With it false nothing is *scheduled*; only the Retry button works (in-process task). If that is also the production value, the snapshot ages past 180 min and every company turns *Stale* until someone presses Retry. *Unverified for production.*

What the code cannot tell (needs the live DB) – run `database/collections-receivables/tools/diagnose_receivables_refresh.sql` (read-only) and read section 3 (last 20 attempts: `Status`, `Seconds`, `FetchMs`, `ErrorNumber`, `ErrorMessage`) and section 4 (is a session really inside the refresh):
* `ErrorNumber = -2` or `Seconds ≈ 1800` → the Dubai PACT procedure exceeds `RefreshCommandTimeoutSeconds` (1800 s) over the full 2000-2099 window. Narrowing `SourceFromDate`, or fixing the procedure/index on PACT, is the remedy; raising the timeout alone is not.
* `ErrorNumber 7399/7302/7303/18456` → linked-server/login problem for `[10.10.10.94]`.
* `ErrorNumber 213/8152/245` → the procedure's result no longer matches layout 1 (13 columns) for company 4.
* `50012` → shrink guard refused a much smaller result.

Changes made (diagnostics, no behaviour change to refresh itself):
* `usp_CollectionsRcv_GetStatus` (R003) now also returns `LastFetchMs`, `LastRawRows` of the last attempt (backward compatible: extra columns; the app tolerates the old procedure).
* Status DTO carries `LastFinishedUtc`, sanitised failure text, `RefreshOverdue`; `SnapshotFailureText` turns number + progress into "failed while reading from PACT: the linked server to PACT is not usable" without server names/addresses/SQL text (tested).
* Structured logs per company: `CompanyId`, `RunId`, trigger, duration, outcome, phase, SQL error number/message (success, skipped, failed, client-side SQL errors).

**Deploy order:** re-run `R003__usp_CollectionsRcv_GetStatus.sql` (idempotent `CREATE OR ALTER`) before or after the application; either order works.

## 3. Receivables accuracy

Already implemented and covered by existing tests (not re-implemented): By-unit default, Due/Overdue against the current Dubai month, Minimum Total (default AED 100, strictly greater, on the unit's summed remaining), installment details adding up to the total, tower/search filters, `UnitId <= 0`, `*` cancelled codes, multi-company separation, December→January boundary.
Documented finding: **PACT returns only the remaining amount**, so partial payments are represented by the reduced remaining amount (original/paid are not available) – tested through the reconciler (`AmountMismatch`).
Duplicates: the refresh does **not** remove exact repeats (same voucher, unit, due day). They are now **counted and shown** by the reconciliation (`Repeated instalments`). Whether to de-duplicate is a business decision (see §9).

## 4. Financial reconciliation with PACT

Root cause of the banner *"The financial source has not been reconciled against PACT for the configured procedure…"*:
`Collections:Campaigns:FinancialSourceValidated` is a **manual configuration flag, committed as `false`**, and no reconciliation process existed anywhere. Every record therefore got `SourceReconciliationRequired` (the "25 on the displayed page" blockers). This is *missing reconciliation*, not a detected financial mismatch. It was also checked in two inconsistent ways (Campaigns: flag only; Review: flag + procedure suffix).

What was built (fail-closed; nothing is bypassed or auto-approved):
* One gate definition `SourceGate` used by Campaigns preview, stored review data and approval: **sign-off recorded + procedure unchanged + recent passing automated reconciliation for both companies** (`Collections:Campaigns:RequireAutomatedReconciliation`, default true; evidence max age 24 h).
* `FinancialReconciliationService`: compares the local snapshot with a **fresh direct read of the same configured PACT procedure** (`PactSqlReceivablesSource`), per unit and per instalment, same validity rules, same window, Dubai-month Due/Overdue buckets, tolerance AED 0.01. Differences: `MissingInSnapshot`, `MissingInSource`, `AmountMismatch`, `DueDateMismatch` (flags instalments that are Due on one side and Overdue on the other).
* States: `Passed`, `Failed` (real differences, units listed), `Error` (could not complete: PACT unreachable, snapshot not fresh (>30 min), refresh running, snapshot republished meanwhile – **missing evidence, not a finding**), `NotRun`, `Expired`.
* Started only by an explicit POST (`/api/collections/reconciliation/run`, permission *send reminders*), single-flight plus 2-minute cooldown; page views and polls never read PACT. UI: `/Collections/Reconciliation` (gate reason, per-company result, unit table), linked from the Campaigns banner.
* History is in memory by design: after a restart there is no evidence and the gate stays closed until a run is made.

Limits that remain (cannot be closed from this repository):
* The comparison proves *snapshot = what the configured procedure returns now*. It does **not** prove the procedure agrees with PACT's own ledger tables; that is what the human sign-off (`FinancialSourceValidated` + `ReconciliationReference`) attests and the code still requires it.
* Direct PACT read is bounded by `CollectionsSource:PactReceivables:CommandTimeoutSeconds` (clamped to 300 s). If the Dubai procedure needs longer the run ends as `Error` (timeout) – never as a pass.
* Therefore **the 58-customer UAT list will stay "Needs review" until**: (1) someone runs the reconciliation on a fresh snapshot and it passes (or its listed differences are explained), and (2) an authorised person records the sign-off in configuration. I did not and will not set the flag.

## 5. CRM vs PACT contacts
Existing rules kept (CRM first; PACT completes missing fields; several CRM customers → nobody chosen; different phones → conflict, CRM alone, PACT not mixed in; e-mails conflict only when phones cannot confirm). New tests prove formatting differences (`0501234567`, `050 123 4567`, `+971-50-123-4567`, `00971…`, stray `0` after 971, Arabic-Indic digits) are the *same* person and do not conflict; a different number is a flagged identity conflict. One unit's conflict only flags that unit.
**Open decision:** today PACT fills a missing CRM field whenever nothing contradicts it. Requiring a *positive* match (equal normalised phone or e-mail) before PACT may fill a field would make contactability stricter; not changed without approval.

## 6–9. Campaign filters, due/overdue rules, preview totals, Review consistency
Verified present (existing tests): Stage, Project/Tower (`towerId`), Payment status (default Both), Period incl. *Last 6 months*, Minimum Total, search, Preview button, legacy `month/year` bookmarks, server-side filtering before paging, approval uses an immutable `FilterJson` snapshot with fingerprint + revalidation (`ReviewProjectFilterTests`, `CampaignReviewDispatchConsistencyTests`).
Business rules as implemented (documented, tested in `CampaignDateRangeTests`):
* **Due** = due inside the *current Dubai month* (any day, including days still to come); **Overdue** = due before its first day; due from next month on = excluded. Always measured against **today**, never against the Preview date.
* From/To only choose which instalments are read. To is clamped to the end of the current month (the page shows the effective To); a range wholly in the future is refused; an explicit From earlier than a unit's old overdue instalments does exclude them (it is an explicit range).
* Both = Due + Overdue without overlap; Minimum Total is on the selected status's amounts.
* Total qualifying amount = **stage amount** of eligible customers (what Genesys receives), over the whole filtered list; outstanding balance = Due + Overdue of those customers. They differ by design; the page now says so and shows the difference.
**Known difference (pre-existing, pinned by a test, needs a business decision):** an instalment due later *this month* is eligible in Review but not in Campaigns (eligibility ends at today).

## 10. "The preview date is not today's scheduled send date: preview only"
Expected. `Current month reminder - day 14` is sendable only on the 14th (`CollectionsCampaignPolicy.ScheduledDates`); any other day is preview-only. The message now names the stage, its allowed day(s), today's date and the reason, and says the list cannot be sent outside the schedule. Nothing overrides the schedule; no unscheduled dispatch path was added.

## 11. Genesys campaigns
Verified by code and tests (`ReviewCampaignNameTests`, 20 tests): campaign name = stage display name exactly; lookup by name with pagination and a **client-side exact match** (so Genesys's prefix/contains semantics of `name=` cannot cause a wrong reuse); reuse only if the campaign dials the stage's contact list; case-variant / several matches / different list → fail safe before any upload; creation payload has `campaignStatus: "off"`, nothing ever activates a campaign; unconfirmed creation → next send re-looks-up and reuses; Id, name and Created/Reused persisted.
Fixed: two sends of the same stage at the same moment could both find no campaign and both create one. Lookup+create is now serialised per campaign name in-process (mutation-checked: the new test fails without it). Multi-instance deployments rely on Genesys's own unique-name rule as the backstop.
*Unverified:* the exact Genesys API contract (`filterType` default for `name`, required fields of `POST /api/v2/outbound/campaigns`). The official reference page could not be read from this environment (the developer-centre page is a JS app); a community answer says the `Contains` filter matches whole words case-insensitively. Check the payload against the API Explorer before enabling `Campaign:Enabled/AllowCreate`. Both stay **off** in committed config (test-enforced).

## 12. CSV exports
Review CSV and Genesys CSV are produced from the same `PreviewAsync` result (same filters, eligibility, amounts, no pagination truncation: over the `MaxExportRows` limit the export is refused, never cut); the Genesys file is refused unless every eligible row is Ready; the dispatch contact list is generated from the approved, revalidated items. Existing tests: `CampaignMinimumAndPaidTests`, `CampaignPaymentStatusTests.BothCsvExports_…`, `CampaignReviewDispatchConsistencyTests`.

## 13. Tests added
* JS (`tools/ui-tests`): `receivables-load-monitor.test.mjs` (14): termination, no overlap, bounded polls/duration, error give-up, stale-answer discard + abort, stop, hidden tab, retry grace, page-level: status-only polling, single final list read, give-up notice keeps list, failed company polls nothing, filter change cancels.
* .NET: `SnapshotLoadStateTests`, `ReceivablesLoadStateRenderTests`, `FinancialReconcilerTests`, `FinancialReconciliationServiceTests`, `ReconciliationRenderTests` (+ `StageScheduleMessageTests`), `CampaignDateRangeTests`, `ContactFormattingVsIdentityTests`, concurrency test in `ReviewCampaignNameTests`.

## 14. Open decisions (need approval; nothing below was changed)
1. Positive-identification rule before PACT fills missing CRM fields (§5).
2. De-duplication of exact repeated PACT instalments (§3).
3. Eligible-later-this-month: Review vs Campaigns (§6–9).
4. Whether `BackgroundJobs:Enabled` must be true in production so the 2-hourly refresh actually runs.
5. Whether the reconciliation should also be scheduled (daily) – currently manual by design.

## 15. UAT verification steps
1. Deploy SQL: re-run `R003` (idempotent). Deploy app. Run `tools/diagnose_receivables_refresh.sql`; record sections 2-4 for company 4 and 32.
2. Receivables: open the page while a refresh runs → one "Loading data for X…" notice, DevTools shows `?handler=Status` (≈ 300 B) every 4-15 s and **no** repeated `handler=Results`; when it finishes the list loads once. With a failed company: no polling at all, a *Retry* button for that company, last valid data + time shown.
3. Press Retry on a failed company: only that company's refresh runs; the other company's rows stay.
4. Campaigns: Preview Customers for `Current month reminder - day 14`; the banner shows the specific gate reason with a link to `/Collections/Reconciliation`.
5. Refresh Dubai, then within 30 min press *Run reconciliation*. Expected outcomes: *Passed* (gate then needs the sign-off), *Failed* (unit list = the differences to explain), or *Error* (message says why; run again).
6. Only after Passed and an authorised sign-off (`FinancialSourceValidated=true`, `ValidatedProcedureSuffix`, `ReconciliationReference`) does Ready/Approve become possible, and only on the stage's scheduled day.
7. Genesys: keep `Campaign.Enabled/AllowCreate` false until the payload is checked in the API Explorer; then test with a throw-away contact list.
