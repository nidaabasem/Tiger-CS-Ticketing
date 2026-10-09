# Deploying the reviewed V2 receivables procedures

Status: **written and unit-checked, never executed against SQL Server or PACT** (no SQL Server was available to the author). Run everything on UAT first.

## Where
The `.sql` files in this folder (`docs/Collections/pact-sql/deploy/`) are the only deployment artefacts. They are generated from the reviewed drafts
`docs/Collections/pact-sql/p4AccountReceivablesV2.review.sql` and `p32AccountReceivablesV2.review.sql`; a test (`TheDeployScript_IsTheReviewedDraftBodyExactly`) fails if
a deploy script's body ever differs from its draft. They create **new** procedures `dbo.p4AccountReceivablesV2` and `dbo.p32AccountReceivablesV2` in the database that already
holds `dbo.p4AccountReceivables` / `dbo.p32AccountReceivables` — the database the application's `ConnectionStrings:PACTRPT` points to. Which server and database that is
must be read from the deployed configuration (it is not in the repository).

## Order
| Step | Script | Purpose |
| --- | --- | --- |
| 1 | `00-preflight.sql` | Read-only. Every row must be `OK` (version ≥ 2012, originals present here, access to `pact2c4` and `PACT2C32`, CREATE PROCEDURE right). Lists who may execute the originals. |
| 2 | `10-deploy-p4AccountReceivablesV2.sql` | Creates/updates the company 4 V2 procedure. |
| 3 | `11-deploy-p32AccountReceivablesV2.sql` | Same for company 32 (independent of step 2). |
| 4 | `15-grant-execute.sql` | Replace the placeholder with the grantee from step 1, then run. |
| 5 | `20-smoke-and-compare-p4.sql`, `21-smoke-and-compare-p32.sql` | Read-only. Checks the arithmetic (`original = paid + remaining`), instalment identity, FIFO order, and compares per-tenant totals with the original. Every "expect 0" row must be 0. |
| 6 | Reconciliation | `../reconciliation-checklist.md` with the sign-off evidence. Do not skip. |
| 7 | Application configuration | UAT only first: `CollectionsSource__PactReceivables__ProcedureSuffix=V2`; restart the API; run *Refresh review data*. `Collections__Campaigns__FinancialSourceValidated` stays `false` until step 6 is signed off. |

The application passes only `@StartDate`, `@EndDate` and `@MinAmount`; the V2 defaults apply for `@IncludeSettled = 0` (settled instalments are not returned) and
`@StrictIdentity = 1`. With strict identity a voucher that maps to more than one Tag or unit makes the **whole read fail** (error 51001/51002) instead of guessing:
run reconciliation query R3 first, and expect the refresh to report "The campaign source could not be read" until such vouchers are corrected at source.

## What V2 reports (verified from the procedure text)
`Amount` = remaining; `PlanAmount` = original instalment; `AllocatedAmount` = paid; `Status` = `Paid` only when remaining is 0, otherwise `Installment`.
`PlanAmount = AllocatedAmount + Amount` always (decimal(19,4), no float). The application derives **Unpaid** when `AllocatedAmount = 0`, **PartiallyPaid** when `0 < AllocatedAmount < PlanAmount`,
and **Unknown** when the three figures do not add up or any is missing (the original procedures return neither plan nor paid, so they stay Unknown). **Unknown is never sendable.**

Caveat to confirm in reconciliation: PACT keeps no per-instalment payment. V2 computes the tag-level paid amount (`Σ invoice totals − Σ ledger balances`) and applies it
oldest-first (FIFO by due date, voucher, account). "Paid" and "remaining" per instalment are therefore a convention; only the per-tag total is a ledger fact.

## Rollback
1. Set `CollectionsSource__PactReceivables__ProcedureSuffix` back to empty and restart the API (the originals were never modified, so the application is then exactly as before). Refresh the review data once.
2. Optionally run `90-rollback.sql`, which drops only the two V2 procedures and only if they exist.
