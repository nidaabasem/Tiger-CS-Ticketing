# EDSM instalment semantics — what is established, and what is not

Purpose: before TigerCS states a **next payment**, establish what EDSM's rows actually mean. This page records the
answer for each question with its evidence. Source: `EDSM_Collections_Contract.md` (static analysis of the EDSM C#
source; the stored-procedure bodies are **not** in any repository this was written from). **Nothing here was
confirmed against a running EDSM or UAT data.**

Evidence levels: **VERIFIED (code)** = proven by EDSM's C# source as analysed in the contract; **UNVERIFIED** = depends on
SQL, the deployed build or host settings; **NOT ESTABLISHED** = no evidence either way. TigerCS does not fill a gap with
a plausible meaning.

## Conclusion

**The semantics needed to compute a next payment are not established for any company.** `v1/due-installments` is the only
EDSM route with instalment-level dated rows, and four of its five required meanings are unverified. The feature therefore
ships **off**, and even when switched on it returns an explicit `Unavailable` (`SemanticsNotConfirmed`, naming what is
missing) until the EDSM owners confirm each item per company in `CollectionsSource:NextPayment:Companies`
(see `Next-Payment.md`). `CollectionsSource:DueInstallmentsEnabled` stays `false` and is not touched.

## 1. Which records represent unpaid instalments?

| Route / company | What the evidence says | Level |
|---|---|---|
| `due-installments`, companies 4, 32 (`p4DuePayments`, `p32DuePayments`) | Returns rows with a **raw `status` string**; rows with a null/blank status are dropped by EDSM. No field says paid or unpaid. Whether paid rows are included is decided inside the SP (contract §4.4). | **NOT ESTABLISHED** |
| `due-installments`, companies 25, 7 (`p25GetCheques`, `p7GetCheques`) | The SPs are *cheque* procedures, a different procedure from 4/32 "DuePayments"; they "may have different semantics" (§4.4). A cheque is not necessarily an instalment. | **NOT ESTABLISHED** |
| `due-installments`, company 20 | Not supported by the route (400). | VERIFIED (code) |
| payment-summary / payment-transactions, **owned** (4, 32) | A ledger row counts as unpaid when `Debit` and `Credit` are both non-null and `Credit < Debit`; `ChequeDueDate ≤ today` → *due*, `> today` → *outstanding* (§3.4, §5.5). **Rows with NULL `Credit` (never partially paid) are excluded**, and whether the SP returns such rows at all is unverified — so an untouched future instalment may not appear (Collections-Integration §2.3 #4). | classification **VERIFIED (code)**; completeness **UNVERIFIED** |
| payment-summary / payment-transactions, **rented** (7, 25, 20) | There is no unpaid-instalment concept. *Outstanding* = post-dated cheques **received** (`status = pdc`); *due* = bounced cheques + fees + opening-balance net (§3.5). An instalment not yet paid by cheque appears nowhere. | VERIFIED (code) — and it means a next unpaid instalment **cannot be derived for rented companies** from these routes |
| The status value set (all SPs) | "The full set of status values the SPs can emit is UNVERIFIED" (§3.6). | **UNVERIFIED** |

## 2. Which field is the due date?

| Source | Field | What is known | Level |
|---|---|---|---|
| `due-installments` | `chequeDueDate` (ISO date-time, no offset, UAE local) | It is a **cheque** due date. That it equals the *instalment's* contractual due date is not stated. Range inclusivity (`FromDate`/`ToDate`) is decided in the SP. | **UNVERIFIED** |
| `payment-transactions` | `date` (`dd-MMM-yyyy`) | `ChequeDueDate` **if present, else `CreatedDate`**, and `""` for opening-balance/contract rows — one field that can hold either a due date or a creation date, with no flag saying which. **Unusable as a due date.** | VERIFIED (code) |
| `payment-summary` | none | Aggregates only; carries no dates (§3.3). | VERIFIED (code) |
| "today" inside EDSM | `DateTime.Now.Date` on the **EDSM server's local clock** | Whether that host is on Asia/Dubai is unverified, so "due today / overdue" in EDSM's buckets may differ from TigerCS's Dubai business date near midnight. | **UNVERIFIED** |

## 3. Is `amount` the original or the remaining unpaid amount?

| Source | What is known | Level |
|---|---|---|
| `due-installments.amount` | A `double` with no companion field (no scheduled amount, no paid amount, no remaining amount, no currency). | **NOT ESTABLISHED** |
| owned summary arithmetic | `dueAmount`/`outstandingAmount` sum **`Debit − Credit`** per ledger row — i.e. row-level *remaining*. That is the *summary's* arithmetic; it says nothing about the due-installments SP's `amount`. | VERIFIED (code) for the summary only |
| rented `outstandingAmount` | Sum of post-dated cheque **face values** (`AmountValue`), not a remaining balance. | VERIFIED (code) |

## 4. How are partial payments, overdue amounts and currency represented?

* **Partial payments.** Owned ledger rows show them as `0 < Credit < Debit` (VERIFIED, summary/transactions). The
  due-installments row has **no paid-portion field**, so a partial payment cannot be seen or netted there — unless the owners
  confirm `amount` is already the remaining amount (the `AmountRepresents` attestation). **NOT ESTABLISHED.**
* **Overdue.** TigerCS never derives it from instalments. EDSM already reports it in the summary: owned `dueAmount` =
  `Σ(Debit − Credit)` with `ChequeDueDate ≤ today`; rented `dueAmount` = bounced cheques + fee/opening-balance items, and
  **can be negative** (§3.5). The next-payment feature keeps overdue **out**: it considers instalments dated strictly after
  today, so an instalment due today or earlier is never the "next payment" and is never added to it.
* **Currency.** None is returned anywhere; amounts are *implicitly* AED, and the only explicit "AED" is a stray suffix on rented
  Due/Fees `formattedAmount` (§7). TigerCS shows the configured `CollectionsSource:Currency` (`currencySource: "Configured"`) and
  requires the owners to confirm it (`CurrencyConfirmed`). Format/culture of formatted strings is unverified for the host (§7).
* **Other facts that limit what can be promised.**
  * No as-of timestamp, no cache bypass; figures may lag a posted payment by up to ~20 minutes (§8).
  * `tenantID` is 32-bit in due-installments and 64-bit in contracts (§9 #7): larger tenants cannot be matched.
  * The route is **company-wide and unpaged** (all tenants for the date range); TigerCS filters by tenant.
  * Granularity is per `(company, tenant)`; the summary cannot separate a tenant's units (§2.3), while due-installments rows
    carry `unitID`.
  * EDSM answers an unknown tenant with **200 and zeros** (§1.2), so an empty answer proves nothing about existence — which is why
    the tenant/company pair always comes from PACT's own contracts.

## 5. What the EDSM owners must provide (exact)

1. The stored-procedure definitions for `p4DuePayments`, `p32DuePayments`, `p25GetCheques`, `p7GetCheques` (and the
   `p{N}tenantStat` procedures behind the summary) — contract §10.1.
2. For **each company**, written answers to the six attestation items recorded in configuration (`Next-Payment.md` §3):
   unpaid-status values · `amount` = remaining unpaid · `chequeDueDate` = instalment due date · the route lists **every**
   unpaid instalment (including never-partially-paid) · amounts are in the configured currency · who confirmed, when, and
   where it can be checked.
3. Date-range inclusivity for `FromDate`/`ToDate` (TigerCS overlaps windows by one day so it does not depend on it, but it
   should be known), and the EDSM host's time zone.
4. UAT records with **known expected figures** (see `Next-Payment.md` §6): a partially paid instalment, a settled one, an
   overdue one, a next instalment **more than 31 days** out, and a tenant with several units — for owned and, if wanted,
   rented companies. For **rented** companies the owners should say whether an unpaid-instalment schedule exists at all; the
   routes TigerCS reads do not carry one.

## 6. What is NOT done

No semantic above has been confirmed, so **no company is attested** and nothing in this repository returns a computed next
payment for real data. The calculation and search are implemented and tested on fixtures only
(`Next-Payment.md` §7).
