# Payment tab - Payment Summary of one unit

The customer card's **Payment** tab shows, for the selected unit (a customer with several units picks one), five cards and a payment schedule.
The unit is found by company + tower + unit code (CRM first for the contact; PACT completes it) - see `Customer-Unit-Linking.md`.

## Figures and where each comes from

| Card | Definition | Source |
|---|---|---|
| **Due This Month** | remaining amount of the unit's unpaid instalments due **anywhere in the current calendar month, Asia/Dubai** (past days, today and the days still to come) | PACT receivables snapshot |
| **Overdue** | remaining amount of unpaid instalments due **before the first day of the current month** | PACT receivables snapshot |
| **Remaining Balance** | remaining amount of **every** unpaid instalment of the unit, **future months included**. It is **not** Due + Overdue | PACT receivables snapshot |
| **Total Amount** | - | **Unavailable** (see below) |
| **Paid Amount** | - | **Unavailable** (see below) |

Due / Overdue use `CollectionsDueMonth` - the same rule as the Receivables and Campaigns pages, so one unit shows the same Due and Overdue on all three.
Instalments of later months appear in the schedule as **Not yet due** and are in neither Due nor Overdue.
Every card sum is computed from the schedule list itself (`CollectionsUnitPaymentFiguresDto`), so the cards and the table cannot disagree
(`PaymentFigures_CardsAndTableAreTheSameNumbers_ForOneUnitAndScope`, `TheCardsAndTheScheduleTable_ShowTheSameNumbers_...`).
A value a source did not provide is shown as **Unavailable**, never as 0. The summary and the schedule stay visible when Due and Overdue are 0
("No due or overdue payments this month").

## Why Total Amount and Paid Amount are unavailable

Investigated sources (nothing is inferred from incomplete data; no customer/company total is ever attributed to one unit):

* **PACT `p4AccountReceivables` / `p32AccountReceivables`** (the snapshot's source): returns only instalments with a **remaining** amount > 0 and **no original and no paid amount**
  (`R002__usp_CollectionsRcv_Refresh.sql`). A fully paid instalment is not returned at all, so neither the contract total nor the paid sum can be rebuilt from it.
* **EDSM payment summary** (`paidAmount`, `totalAmount`, ...): resolved per PACT **tenant + company**, i.e. across all units of the customer in that company - not per unit.
* **EDSM payment transactions**: no unit identifier on a transaction row. **EDSM due-installments**: has a unit id but only a +-31 day window, and its paid/unpaid status is documented as unverified (off by default).
* **Per-account finance source (`ICollectionsFinancialSource`)**: `Provider = "Unavailable"` in real environments.

### What is needed to complete the summary

Any ONE of:

1. A PACTRPT procedure/view (like `p4/p32AccountReceivables`) that returns **all** instalments of a unit - paid, partly paid and unpaid - with **original (scheduled) amount, paid amount, remaining amount, due date, voucher and status**, keyed by company + tenant + unit id / unit code. With it the snapshot can store original and paid amounts; Total = sum of original amounts, Paid = sum of paid amounts, per unit. (The same columns are already requested for company 7 in `CRM-GetUnitOwners-Contract.md`.)
2. A real per-unit `ICollectionsFinancialSource` returning the unit's contract total and payments.

Until then the tab shows "Total and paid amounts are currently unavailable", and the Amount / Paid columns of the schedule show Unavailable.

## SQL

Only `database/collections-receivables/R004__read_procedures.sql` changed for this feature (idempotent `CREATE OR ALTER`; no table, data or other procedure change):
`usp_CollectionsRcv_GetUnit` returns all unpaid instalments (no due-date cut-off); the application classifies them. New optional `@Only = 3` returns only that result set (for the verification script); the application passes nothing, so its reads are unchanged.

## UAT verification

1. Deploy `R004__read_procedures.sql` to `TigerCsTicketing` (together with the current-month rule it already contains).
2. `tools/verify_month_rule.sql` - every row `OK`.
3. `tools/verify_unit_payment_summary.sql` - `Fail = 0`; `UnitsWithFutureInstalments > 0` and `UnitsDueThisMonth > 0` show the sample exercised every class. Set `@UnitKey` to check one specific unit.
4. Open the Payment tab of a customer: for a sample unit compare the five cards with the script's line for that unit - Remaining Balance, Due This Month, Overdue equal; Total and Paid read Unavailable. Add up the **Remaining** column of the table: it equals the Remaining Balance card; the *Due* rows equal Due This Month; the *Overdue* rows equal Overdue.
5. The same unit on **Receivables** and **Campaigns**: Due and Overdue equal the Payment tab's cards (Remaining Balance is intentionally larger when later months exist).
6. A unit whose Due and Overdue are 0 but with later instalments: cards and schedule stay, with "No due or overdue payments this month".
7. A customer with several units: choose each in the selector; each shows only its own figures.

## Testing status (be explicit)

* Unit / render tests (`dotnet test`) pass. The Razor render tests use a fake API with test data.
* The real-SQL tests run only with `TIGERCS_PERF_SQL`; they were executed on a **local SQL Server 2022 (Docker) with synthetic data**, not on UAT and not with real PACT data.
* Screenshots are rendered from the real Razor pipeline with **test data**.
