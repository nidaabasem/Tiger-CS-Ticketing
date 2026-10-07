# PACT Due & Overdue Customers

TigerCS lists PACT customers with a positive outstanding instalment due today or earlier, even if they have no TigerCS ticket or customer-directory record. The supplied report procedures cover company **4 (Dubai)** and **32 (Sharjah)** only. This feature adds no Genesys route, reminder send, next-payment calculation, database migration or background job.

## UI and API

- Navigation: **Collections**, `/Collections/Receivables`.
- Authenticated staff API: `GET /api/collections/receivables/customers?companyId=4&status=all&search=3001&page=1&pageSize=25`.
- Existing Collections financial-read authorization is enforced before any external read; the central System Administrator override applies. Reporting User alone is insufficient.
- `companyId`: omitted, `4`, or `32`; `status`: `all` (default), `due`, or `overdue`. Search matches name, tenant ID, mobile, email or unit. Page size is 1–100, default 25.
- One customer per `(CompanyID, TenantID)`, with every returned unit/instalment under that customer. Equal tenant IDs across companies are separate accounts; there is no CRM identity guess.
- Dubai business date is provided by the existing Collections clock. **Due** means today; **Overdue** means before today. Future dates and amounts <= 0 are excluded. `all` is the union, not a requirement to have both.
- Count metrics reflect the selected filters before pagination. Pagination follows customer grouping, with overdue customers and oldest dates first.
- Response fields include company, tenant ID, name, mobile, email, units, dates, overdue days, due/overdue amounts, source read time, configured currency and whether legacy exclusions were applied.
- Disabled or failed sources return 503, never an empty/partial success. Invalid filters return 400, unauthorized callers 401/403. The UI has corresponding error/empty/loading states.

## Direct SQL source and amount limitation

`PactSqlReceivablesSource` calls `dbo.p4AccountReceivables` and `dbo.p32AccountReceivables` in the configured `PACTRPT` database. It passes `@StartDate = 2000-01-01` (matching the supplied procedures' internal lower bound), an inclusive SQL datetime `@EndDate` at the end of the Dubai business date, and `@MinAmount = 0`. Positive balances below 1 AED therefore remain visible. Stored-procedure names and parameters are not caller-controlled. There is a shared request deadline and a source-row cap; either failure rejects the complete response.

The supplied definitions calculate `Amount` from their payment allocation as `abs(b.PlanFutureAmount - b.NewFutureAmount - PlanFutureAmount)`. Their final invoice-to-schedule join is by tenant tag alone. Several returned rows on one date may therefore be repeated allocations or legitimate instalments. The application preserves all rows, without `DISTINCT`. A due/overdue bucket containing multiple rows on a date has a **null total** and `AmountStatus = NeedsReview`; the UI displays **Review needed** and exposes the rows. An unambiguous bucket is summed as provided. This is a conservative report of those procedures, not independent proof that the underlying allocation is correct. Unit/voucher associations remain exactly as returned by PACT.

PACT returns no currency field. `Currency` defaults to AED and is marked `CurrencySource = Configured`; confirm this during environment setup.

## Existing EDSM exclusions

With `ApplyLegacyExclusions = true` (default), the supplied function's filters are reproduced:

| Exclusion | Matching rule |
| --- | --- |
| Booking/down-payment rows | Exact `(UnitCode, DueDate)` returned by the supplied CRM query |
| TP121 | DueDate > 2025-12-15 |
| TP122 | DueDate > 2026-02-20 |
| TP123 | DueDate > 2026-03-20 |
| TP103, TP102, TP104, TP101 | Any unit starting with the prefix |
| Optional PDC unit list | Entire UnitCode, matching the supplied optional argument |
| Names containing `*` | Entire row |
| `Helper.SEPCIAL_CASES` | Entire UnitCode; the 50 active values supplied in `Pasted text(6).txt` are loaded in the API configuration |

The supplied `GetHandoverAndDownPaymentPaymentsAsync` method executes only the Booking & Downpayment query: join `tblPayment` to `tblLead` by `LeadID`, require payment type 2 or 3, lead status != 6, and DueDate >= 2025-11-01. The unit key is `TRIM(TRIM(l.ProjectCode) + '-' + TRIM(l.UnitNumber))`. `p.Amount` is returned but does not participate in matching. There is **no upper date limit**: the supplied `TotDate` argument is unused. The Handover/After Handover branches and project filters are commented out and remain inactive. TigerCS executes this fixed query directly with a typed date parameter, so no new CRM stored procedure is needed.

The commented TP119 condition remains inactive. String comparisons are case-sensitive; CRM keys retain the exact timestamp instead of silently reducing it to a date.

Only the active `SEPCIAL_CASES` entries were imported. Commented entries, `DN_SEPCIAL_CASES` and `GULF_CUSTOMERS` are not added to this filter, because the original function does not use them. Individual configured TP119 units are excluded, while the commented TP119 prefix/date rule remains inactive.

## Environment setup

The feature ships disabled. The CRM query and the 50 active special-case units come from the supplied EDSM source; no credentials or exclusion values are invented. Set server-side configuration/secrets:

| Setting | Required value |
| --- | --- |
| `Collections:Enabled` | `true` |
| `CollectionsSource:PactReceivables:Enabled` | `true`, after setup and validation |
| `ConnectionStrings:PACTRPT` | Connection to the report database, using an account permitted to execute the two reports and read their required dependencies |
| `ConnectionStrings:CrmDatabase` | Connection to the same CRM database used by EDSM's `connectionStrings.CRM`, with SELECT access to `tblPayment` and `tblLead`; the connection setting name is configurable |
| `CollectionsSource:PactReceivables:SpecialCaseUnitCodes` | The supplied 50 active `Helper.SEPCIAL_CASES` values are included; maintain the list if the original changes |
| `CollectionsSource:PactReceivables:SpecialCasesConfigured` | Included as `true` after importing the supplied list; a custom deployment with an empty list requires explicit confirmation that it is empty |
| `CollectionsSource:PactReceivables:PdcExcludedUnitCodes` | Optional original PDC unit exclusions; empty reproduces an omitted `pdcPayments` argument |
| `CollectionsSource:PactReceivables:Currency` | Confirmed reporting currency; default AED |

Environment-variable names use double underscores, e.g. `ConnectionStrings__PACTRPT` and `CollectionsSource__PactReceivables__Enabled`.

The supplied CRM query and `Helper.SEPCIAL_CASES` list are now incorporated. Remaining setup is the actual server-side PACT/CRM connections and build/UAT validation before activation. Missing connection/special-case configuration blocks the list and explains the missing source in the API; it does not silently omit the filter.

`ApplyLegacyExclusions = false` explicitly selects the raw PACT list and the UI identifies it. It is not the supplied filtered EDSM function and is not enabled in the shipped configuration.

`CommandTimeoutSeconds` defaults to 60 (clamped 1–300), `MaxSourceRows` to 250000 (clamped 1–1000000). Every request reads both reports before filtering/pagination; large-source performance needs validation in the target environment. There is no cache or synchronization between independently read CRM and PACT databases.

## Validation

Automated fixtures cover no-ticket customers, the Dubai date boundary, positive sub-dirham balances, future/settled exclusion, union selection, company/tenant identity, grouping before paging, formatted mobile search, legacy cutoff boundaries, raw-row preservation, ambiguous totals, authorization before reads and source failures. Endpoint tests use the real API/JWT host with a fake PACT source, not a real SQL server. The route is included in the OpenAPI and protected-endpoint inventories.

**Real PACT/CRM and UAT checks have not been run.** Before activation, compare known customers with PACT: due today, overdue, both, partial-payment remainder, multiple units/invoices, repeated dates, booking/down-payment exclusions, special cases, and optional PDC exclusions. Confirm the complete list, procedure performance and currency. The report procedures' allocation/join issue must be checked against expected accounting figures; the UI's review state does not repair it.

Current verification: the full solution builds (Release, 0 warnings, 0 errors) and the whole test suite passes (2,870/2,870) on .NET SDK 10.0.111. The configured special-case list was compared with the supplied source: all 50 active entries match, with no duplicates and no commented/other-list entries. Real PACT/CRM validation (above) is still outstanding.
