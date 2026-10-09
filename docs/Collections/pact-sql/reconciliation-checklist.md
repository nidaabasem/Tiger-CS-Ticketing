# PACT reconciliation checklist (sign-off for `FinancialSourceValidated`)

Purpose: show, from actual PACT records, that the amounts the application would quote are correct. **`Collections:Campaigns:FinancialSourceValidated` stays `false` until every
section below has evidence attached and a named person has signed it.** Nothing in the application sets it. When signed, also set
`Collections:Campaigns:ValidatedProcedureSuffix` to the procedure that was checked (`V2`) and `ReconciliationReference` to the evidence location. Switching procedure later invalidates the sign-off.

Do it on **UAT first, then repeat the sample on production data** (read-only scripts only). Use a fixed business date and record it; PACT balances move daily.

## 0. Preconditions
- [ ] `deploy/00-preflight.sql` all OK; V2 deployed (`deploy/README.md`) and `deploy/20-/21-smoke-and-compare-*.sql` every "expect 0" row is 0 (attach output).
- [ ] `receivables-reconciliation.company4.read-only.sql` and `...company32...` run; sections R1–R11 attached. Specific answers needed: **R3** (no voucher on more than one Tag or unit — otherwise V2 refuses to run), **R2** (invoice lines per voucher), **R5** (voucher-number collisions), **R6** (ledger double join), **R7** (plan vs invoice totals), **R8** (plans with no ledger row), **R10** (float residue).

## 1. Sample (a person who knows PACT chooses it; at least 5 of each, more for company 4)
| Case | How to find it | What to compare |
| --- | --- | --- |
| **Unpaid** | tenant with `AllocatedAmount = 0` in V2 | PACT statement of account for the tenant/unit: nothing received against the instalment; remaining = original |
| **Partly paid** | `0 < AllocatedAmount < PlanAmount` | received amount in the ledger/receipts; remaining = original − received; note which instalment the payment was applied to in PACT versus V2's oldest-first rule |
| **Fully paid** | tenant with all instalments settled (use `@IncludeSettled = 1`) | V2 returns remaining 0 / no open row; PACT shows zero balance |
| **Overpaid / advance** | tenant with ledger credit | no open row; confirm V2 does not show a negative or a phantom balance |
| **Float residue** | original-procedure rows with amounts below 0.0001 (the Overdue export had 36 such rows, e.g. `7.27E-11`) | V2 returns no row (decimal), or the application drops it as settled; PACT shows zero |
| **Float noise** | original amounts like `37797.0800000001` (619 rows in the Overdue export) | original value equals the V2 value to the fils (`37797.08`) |
| **Genuine sub-fils** | amounts with a real third decimal (`0.398`, `291906.651` in the export) | decide with finance whether PACT really holds three decimals; they stay `AmountPrecisionNeedsReview` and are never rounded |
| **Multiple instalments** | tenants with 2+ open instalments in one unit | each instalment: date, original, paid, remaining; the unit's quoted amount = sum of the qualifying ones (Overdue: due before one month ago; Legal Notice: previous calendar month; Legal Case: older than three months) |
| **Several units** | tenants with 2+ units (5 in the Overdue export) | each unit's balance separate; no instalment attributed to the wrong unit (R11 unit mapping, name→code) |
| **Same-date instalments** | tenants with two instalments on one date | not double counted (R4); the application flags them `AmbiguousInstalments` regardless |
| **Shared phone** | the 25 phone numbers shared by 2–5 customers in the Overdue export | confirm whether these are real shared numbers (family/company) or placeholder values entered in PACT |

## 2. Evidence to attach per sampled tenant
PACT statement/ledger screenshot or export (with date), V2 row(s) (`TenantID, UnitCode, VoucherNumber, DueDate, PlanAmount, AllocatedAmount, Amount`), the application's review record
(`/Collections/Review`, same business date), and the difference with an explanation. Pass criterion: every sampled difference is zero or explained and corrected at source.

## 3. Totals
- [ ] Per company: Σ remaining of V2 open rows = PACT receivable ledger total for the same accounts at the same instant (finance report), within AED 0.01 × rows.
- [ ] Original-vs-V2 per-tenant differences (smoke script, last query) explained: expected causes are the fan-out (D1), same-date double counting (D3) and repeated paid amount (D4).
- [ ] The FIFO convention (payments applied to the oldest instalment first) is confirmed by Collections as how PACT receipts are applied, or the exceptions are listed.

## 4. Sign-off
Name, role, date, business date used, procedure suffix, evidence location. Then (and only then) configure `FinancialSourceValidated=true` + `ValidatedProcedureSuffix` + `ReconciliationReference` for that environment.

## What is still needed from you (the app cannot produce it)
1. **Access to the PACTRPT database** (read, plus CREATE PROCEDURE on UAT) or a DBA who runs the scripts and returns outputs. The server/database names are not in the repository.
2. **A person who can read the PACT ledger** to judge the sampled tenants (sections 1–2). 
3. **Raw `Mobile` values for the rows whose phone the application shows blank** (738 of 1,077 Overdue records had no usable phone but 640 had an email). The review export contains only the normalized phone, so the cause (empty in PACT vs. a format the normalizer rejects) cannot be separated without raw values; these can be supplied as a masked sample (digits replaced by 9).
4. **The project mapping**: `ProjectCode` is hard-coded empty in `dbo.p4AccountReceivables`/V2 and absent in p32, so the Project filter has nothing to match (all 1,077 rows blank). Unit codes start with `TPnnn` (e.g. `TP126`); decide whether that prefix is the project before any filter or message relies on it.
