# PACT receivables procedures – source review and proposed correction

Reviewed: `dbo.p4AccountReceivables` (PACT2C4 database `pact2c4`, account group 2841) and `dbo.p32AccountReceivables`
(`PACT2C32`, group 4010), as supplied. The two procedures are identical except for the database, the account group, the
`CompanyID` literal, `ProjectCode` (p4 only returns an empty one) and the final `ORDER BY`. **Nothing here changes a deployed
procedure.** Files in this folder:

| File | Purpose |
| --- | --- |
| `p4AccountReceivablesV2.review.sql`, `p32AccountReceivablesV2.review.sql` | Proposed **new, versioned** procedures (not deployed; `CREATE PROCEDURE` only, no ALTER/DROP). |
| `allocation-core.sql` | The allocation algorithm shared verbatim by both V2 procedures and unit-tested (`PactAllocationCoreTests`). |
| `receivables-reconciliation.company4.read-only.sql`, `…company32…` | Read-only queries (SELECT on PACT; temp tables only) to measure each risk on real records. |

**Not executed:** no SQL Server is available in this environment, so none of the T-SQL has been run. The ledger block of the V2
files was verified to be token-for-token identical to the originals (comments/whitespace/redundant parentheses ignored). The
allocation arithmetic is tested on SQLite (portable SQL). Run the reconciliation scripts on a copy/UAT before trusting V2.

## 1. Confirmed from the SQL

* `Amount = ABS(b.PlanFutureAmount - b.NewFutureAmount - PlanFutureAmount)`; the unqualified `PlanFutureAmount` can only bind to
  `b` (only derived table `b` has that column), so `Amount = NewFutureAmount` (never negative). *(p4 l.596, p32 l.165)*
* `Status` = `Paid` when that amount is 0, else `Installment`. It says nothing about unpaid vs partially paid.
* `Amount >= @MinAmount` is the only threshold (p4 l.644, p32 l.176); `@MinAmount` is `int`.
* `@StartDate/@EndDate` filter only the final `b.DueDate`. The ledger uses fixed `@From = 2000-01-01`, `@To = 2099-01-01`;
  `@DueDtFrom/@DueDtTo` (2000–2099) filter nothing in practice. So a "month" is a due-date window over **today's** balances.
* `ABS()` hides a sign error: any negative `PlanFutureAmount − NewFutureAmount` cannot occur (New ≤ Plan by construction), so no
  information is lost, but it makes the formula look more complicated than `NewFutureAmount`.
* The `OverDueAmount` (`GETDATE()`) and `NewFutureAmt` joins (`c`, `f`) are never selected.

## 2. Issues demonstrated by the SQL itself

| # | Issue | Evidence | Effect |
| --- | --- | --- | --- |
| D1 | **Instalments are repeated accounts × invoices times.** `#tab` has one row per (account, tag) × (invoice of that tag) because the ledger derived table `a` is joined to `#tabInv` on tag only; the final query then joins `#tab a` to `@tabpaynew b` on `a.Tag=b.Tag` only. | p4 l.449-451, 603, 610-618; p32 l.106, 168, 170 | Every instalment of a tag is returned `A × I` times (A = ledger account rows of the tag, I = invoices of the tag). `VoucherNumber` (`a.InvNo`) and the unit (`a.FlatUnit`) are those of an arbitrary invoice row, not of the instalment, so the same remaining amount is printed under each invoice/unit. |
| D2 | **`@tabpaynew` drops identity.** It selects only `Tag, DueDate, PlanFutureAmount` from `@tabpay`; `VoucherNo` and `AccountId` are lost. | p4 l.506-513, 522-571 | The returned voucher cannot come from the payment-term row; it comes from `#tab` (D1). No instalment identity survives to the output. |
| D3 | **Same-date double counting in the allocation.** `AccAmount` = sum of `PlanFutureAmount` of all rows with `DueDate <= o.DueDate` for the tag, evaluated per row. Two rows of 100 on one date, 150 paid: `AccAmount = 200` for both, each `100 − 200 + 150 = 50` remaining → **100 returned instead of 50**. Reproduced in `PactAllocationCoreTests.SameDateInstalments_OriginalDoubleCounts_CoreAllocatesExactly` by running the original expression on SQLite. | p4 l.545-564; p32 l.136-143 | Over-reports the remainder whenever two instalments of a tag share a due date and the payment falls inside them. Distinct dates are correct (FIFO by date). |
| D4 | **`PaidAmount` is repeated by the fan-out.** `#tab.PaidAmount = InvAmount_invoice − Balance_account-tag`; the balance is account/tag level, `InvAmount` invoice level. `@tabpaynew` uses `SUM(PaidAmount) GROUP BY tag` over all `#tab` rows, so it is `A·ΣInv − I·ΣBal` instead of `ΣInv − ΣBal`. With two invoices (100, 200) and one account balance 150: original `(100−150)+(200−150) = 0`, correct `300−150 = 150`. Verified arithmetically in `RepeatedBalanceAcrossAccountsAndInvoices_…`. Correct only when A = I = 1. | p4 l.112-113, 567; p32 l.44, 145 | Under-/over-states paid amounts for tags with several invoices or several ledger accounts, which then feeds D3's allocation. (The `#tabInv` totals are *all* invoices of the tag, not only `Installment` ones, while only installment plans are allocated – see Q1.) |
| D5 | **Invoice lines can multiply the plan.** `@tabpay` joins `Inv_DocDetails` (line level) to `COM_DocPayTerms` on `VoucherNo` only and then `SUM(Amount)`. If a voucher has *n* `Inv_DocDetails` lines, each pay-term amount is summed *n* times. (`#tabInv` itself sums `dcNum6` across lines, which suggests several lines per voucher can exist.) | p4 l.473-501; p32 l.115-124 | Plan amounts × *n* for such vouchers. **Whether any voucher has n > 1 needs data** (R2). |
| D6 | **Float arithmetic.** Balances, plan, allocation and result are `float`; the remainder is a float subtraction. | p4 l.79-81, 462, 510-512 | A fully paid instalment can come out as a tiny positive (e.g. 1e-12) instead of 0, appearing as a positive balance and shifting `Status` from `Paid` to `Installment`. **Whether it occurs needs data** (R10). |
| D7 | **Ledger rows may be counted by two branches.** The ledger is built from `ACC_DocDetails` joined to `COM_DocCCData` once on `AccDocDetailsID` and once on `InvDocDetailsID` (8 `UNION ALL` branches). A ledger row that has cost-centre data under both ids is counted twice. | p4 l.176-185 vs 297-306 | Possible double counting of balances. **Needs data** (R6). Kept unchanged in V2. |
| D8 | **Unit code is resolved by name→code.** `UnitCode` = `'TP' + Code` of the `COM_CC50010` node whose **Code equals the invoice unit's Name** (`f.Name`). p4 joins all matches (more than one node with that code multiplies rows); p32 takes `TOP 1` with no `ORDER BY`. | p4 l.604-609; p32 l.154, 159 | Wrong/NULL unit when name ≠ code; multiplication when codes repeat. **Needs data** (R11). |

## 3. Needs actual-record reconciliation (not provable from the SQL)

| Q | Question | Query |
| --- | --- | --- |
| Q1 | Which tags have A·I > 1, and how large is the repeated `PaidAmount` error? Is `InvAmount` (all pay modes) the right basis for "paid" of installment plans? | R1, R1b |
| Q2 | Do any installment vouchers join to > 1 invoice line (D5)? | R2 |
| Q3 | Do vouchers map to > 1 tag or > 1 unit? (V2 refuses to guess; with `@StrictIdentity = 1` it raises 51001/51002.) | R3a, R3b |
| Q4 | How many same-date ties exist and what is their effect (D3)? | R4 |
| Q5 | `COM_DocPayTerms` is joined by `VoucherNo` alone: do voucher numbers repeat across cost centres/document types? (Other join columns cannot be assumed from the supplied SQL.) | R5 |
| Q6 | Ledger double-join (D7). | R6 |
| Q7 | Do plan totals equal invoice totals? | R7 |
| Q8 | Plans whose tag has no ledger row (omitted by the original and by V2) and negative plan amounts (the allocation assumes non-negative plans). | R8a, R8b |
| Q9 | Original vs V2 totals per tag. | R9 |
| Q10 | Zero rows, float residue and the `@MinAmount = 0` effect. | R10 |
| Q11 | Unit mapping. | R11 |

## 4. Proposed correction (V2)

Explicit identities: **instalment** = (`Tag`, `VoucherNo` of the pay-term row, `AccountId`, `DueDate`); **receivable (invoice)** =
(`Tag`, invoice `VoucherNo`, unit); **allocation scope** = the tag (the ledger balance is only known per account/tag, never per
invoice, so payments are allocated across the tag's instalments oldest-first).

1. Collapse `Inv_DocDetails` lines to one row per (VoucherNo, Tag) **before** joining `COM_DocPayTerms` (fixes D5).
2. Keep `VoucherNo`/`AccountId` through allocation and return one row per instalment; the unit comes from that voucher's own
   invoice row (fixes D1, D2). Account × invoice fan-out disappears.
3. Tag paid = `Σ invoice totals − Σ account balances`, each counted once (fixes D4).
4. Allocation: running total per tag ordered by (`DueDate`, `VoucherNo`, `AccountId`) with a *unique* order;
   `Allocated = clamp(Paid − CumulativeBefore, 0, Plan)`, `Remaining = Plan − Allocated` (fixes D3). Per-tag remainder
   always equals `max(ΣPlan − Paid, 0)`; the split among same-date rows is a deterministic convention (voucher order) that the
   business should confirm – it does not change any total.
5. `decimal(19,4)` throughout, `@MinAmount decimal(19,4)`, and `@IncludeSettled bit = 0` so zero rows are not produced at all
   (D6). The application still applies `Amount > 0`; **no positive balance, including below 1, is dropped.**
6. Output columns are a superset of the original, so the application needs no change except the configuration switch
   `CollectionsSource:PactReceivables:ProcedureSuffix = "V2"` (allow-listed, alphanumeric; default empty = originals).

Intentionally **unchanged**: the ledger block, the account tree, `StatusId`/cost-centre filters, the `Installment` pay-mode
filter, the omission of tags with no ledger row, and the name→code unit mapping (now deterministic with `TOP 1 … ORDER BY NodeID`).

## 5. `@MinAmount = 0`

* The ledger scans (`#tab`, `@tabpay`, the allocation) run in full regardless of `@MinAmount`; it is applied only in the final
  `WHERE`. So it changes **result size and transfer**, not the expensive work. Not measured here (no SQL Server).
* With `@MinAmount = 0` the output contains **every fully paid instalment ever due up to `@EndDate`** (all history from 2000,
  `Status = 'Paid'`), each repeated `A × I` times (D1). Because the application's source-row cap (`MaxSourceRows`, default 250 000)
  rejects the whole read when exceeded, zero rows (and their duplicates) can also cause a failure rather than slowness.
* `@MinAmount` is an `int`: `1` would drop positive balances below 1, which must not happen. V2 removes the need: positive
  remainders are always returned and settled ones are not (`@IncludeSettled = 0`).
* Measure it: R10 reports the zero/positive/sub-1/float-residue composition; the application now logs, per procedure, rows,
  zero-amount rows and elapsed time (see below). Compare `EXEC … 0` and `EXEC … 1` with `SET STATISTICS TIME ON`.

## 6. Local `TaskCanceledException` from `ExecuteReaderAsync`

*No screenshot reached this session and no PACT/SQL Server is reachable from it, so nothing below was measured here; the cause is
established from the code and from the timings you reported. The new logging (below) will confirm it on your machine.*

**Reported measurement:** direct `p4AccountReceivables`, `@StartDate = 2026-01-01`, `@MinAmount = 1000`: **58 s, 2,132 rows**.
`@StartDate`/`@EndDate`/`@MinAmount` are applied only in the final `WHERE` (section 1), so the 58 s is essentially the fixed
ledger work and the real application call (`2000-01-01`, `@MinAmount = 0`) costs **at least that** for p4, plus the larger result.

**Timers on the path (code before this change):**

| Layer | Setting | Value |
| --- | --- | --- |
| Web → API | `HttpClient.Timeout` for `CollectionsApiClient` | 100 s (framework default; none configured) |
| API request | caller token (`HttpContext.RequestAborted`) | fires only if the Web request is abandoned |
| API service | `CancelAfter(CommandTimeoutSeconds)` linked token, **shared by both procedures** | **60 s** |
| SQL | `SqlCommand.CommandTimeout`, **per procedure** | 60 s |
| Source | one connection, `p4` then `p32` sequentially | – |

**Confirmed cause (by elimination):** the shared 60 s API budget. p4 alone needs ≥ 58 s, so the budget token fired about 2 s
into p32 (or earlier if p4 ran longer), cancelling the pending `ExecuteReaderAsync` with `TaskCanceledException`.
* A SQL command timeout would be a `SqlException` (number −2), not `TaskCanceledException`.
* The Web `HttpClient` (100 s) is later than the 60 s budget, so it cannot be first; its expiry would also surface in the Web
  layer, not at `ExecuteReaderAsync`.
* Debugger pauses only make it worse (the budget is wall-clock) but are not required to reproduce it: **p4 + p32 simply cannot
  fit in 60 s**. Raising only the SQL `CommandTimeout` would not have helped – the budget token would still fire first.
* p4 at 58 s is also within 2 s of its own 60 s command timeout, so even a single procedure was one slow run from a SQL timeout.

**Code version.** A visible `endDate` computed from `businessDate`'s end of day is the code *before* commit `3355237`
(monthly alignment); that checkout is behind `claude/dreamy-rubin-fa0yu7`. Check `git log -1 --oneline`; every read now logs
`build=<informational version>`.

**Fix implemented**
1. **Parallel reads.** p4 and p32 hit different databases, so they now run concurrently on separate connections; request time
   becomes the slower procedure instead of the sum (if each takes ~60 s: ~60 s instead of ~120 s). If either fails the other
   is cancelled and the original failure is reported; no partial list is returned.
2. **Budget ordering.** `CommandTimeoutSeconds` default 60 → **120**, and the API budget is now `CommandTimeoutSeconds + 30`
   (150 s), so a slow procedure ends in a diagnosable SQL timeout rather than being cut off by the budget token. The Web client
   timeout for the receivables API is **180 s** (> 150 s). Invariant: SQL command timeout < API budget < Web HTTP timeout.
3. **Logging** (no credentials, no row content): per procedure – name, company, rows, zero-amount rows, elapsed ms, dates,
   build; on abort – elapsed, rows so far, `callerCancelled`, `tokenCancelled`, command timeout, kind (SQL timeout vs
   cancelled); overall – total rows, elapsed, budget. The service logs when the budget elapses. A raw SQL timeout still
   surfaces as the generic 503 "could not be read".

**Still open (needs measurement on your side)**
* Real p4/p32 timings with the application parameters (`2000-01-01`, `MinAmount = 0`) – the log lines above record them; or run
  `receivables-reconciliation.company*.read-only.sql` (R0 reports rows, zero rows and elapsed ms).
* **Every page or filter change re-runs both procedures** (no cache), i.e. ~1 min per click. That is the largest remaining
  usability cost. A short in-memory snapshot cache (seconds–minutes) would fix it but trades freshness of a financial balance
  for speed, so it was not added without your decision. The V2 procedures (section 4) remove the duplicate rows and the
  zero-balance output, which are the only parts of the cost that depend on the parameters.
* The 120 s default is sized from one 58 s observation; if p4 or p32 exceed it with the real parameters, the proper fix is the
  procedure (V2 / indexing / a pre-computed table), not a larger timeout.
