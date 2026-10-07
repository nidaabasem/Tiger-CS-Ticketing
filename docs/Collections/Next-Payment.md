# Next payment on the PACT/EDSM payment-summary

**Status: implemented and fixture-tested in TigerCS; OFF by default; NOT verified against EDSM or UAT.** It returns a
computed date only for a company whose EDSM semantics the EDSM owners have confirmed in configuration. No company is
confirmed today, so every real response says `Unavailable` and explains why. Why the meanings cannot be assumed:
`EDSM-Instalment-Semantics.md`.

## 1. Contract

The existing routes are extended, not duplicated — same authentication, authorization and deadline as today:

```
GET /api/genesys/collections/customers/by-key/{customerKey}/payment-summary      (Genesys, via TigerGroupWeb)
GET /api/collections/customers/by-key/{customerKey}/payment-summary                (TigerCS Web)
```

The response gains `nextPayment` (always present on these routes). Data Action `08-collections-payment-summary.json`
maps it to `nextPaymentStatus`, `nextPaymentReasons`, `nextPaymentDetail`, `nextPaymentIsComplete`,
`nextPaymentSearchedThrough`, `nextPaymentDate`, `nextPaymentAmountRaw`, `nextPaymentCompanyId`.

| `nextPayment.status` | Meaning | May the bot state a date? |
|---|---|---|
| `Available` | An upcoming unpaid instalment was found. `earliest` has the date, amount and unit lines. If `isComplete` is false, say it is the earliest among the companies that could be read. | Yes (see `isComplete`) |
| `NoneWithinHorizon` | Semantics confirmed and EDSM read; no unpaid instalment falls after today **through `searchedThrough`**. It does not say none exist later. | No date. Say nothing is due before `searchedThrough`. |
| `Unavailable` | A next payment cannot be stated. `reasons` says why. **Never a zero and never "no payment".** | No |

`reasons`: `FeatureDisabled` · `MappingNotAvailable` (CRM customer or no PACT contracts: no tenant or company is guessed) ·
`SemanticsNotConfirmed` (see `companies[].missingSemantics`) · `CompanyNotSupported` (company 20) · `TenantIdNotMatchable`
(PACT tenant id above EDSM's 32-bit field) · `SourceUnavailable` · `DeadlineExceeded` · `SearchIncomplete` (an answer exists but a
mapped company could not be determined).

### Samples (illustrative shapes; the dates and amounts are not real data)

Default (what every real environment returns today):

```json
"nextPayment": {
  "status": "Unavailable", "reasons": ["FeatureDisabled"],
  "detail": "Next payment is switched off (CollectionsSource:NextPayment:Enabled).",
  "isComplete": false, "searchedFrom": null, "searchedThrough": null, "earliest": null, "companies": [],
  "overdueTreatment": "Instalments due today or earlier are never returned here; EDSM reports them in the company's dueAmount."
}
```

Enabled, but the owners have not confirmed company 4:

```json
"nextPayment": {
  "status": "Unavailable", "reasons": ["SemanticsNotConfirmed"], "isComplete": false, "earliest": null,
  "companies": [ { "companyId": 4, "status": "Unavailable", "reasons": ["SemanticsNotConfirmed"],
                   "missingSemantics": ["UnpaidStatusValues", "AmountRepresents=RemainingUnpaid", "ChequeDueDateIsInstalmentDueDate",
                                        "ListsEveryUnpaidInstalment", "CurrencyConfirmed", "ConfirmedBy/ConfirmedOn/EvidenceReference"],
                   "searchedThrough": null, "next": null } ]
}
```

Confirmed, found 120 days out in the second search window, partly paid instalment (amount = what is still unpaid):

```json
"nextPayment": {
  "status": "Available", "reasons": [], "detail": null, "isComplete": true,
  "searchedFrom": "2026-10-08", "searchedThrough": "2027-04-10",
  "earliest": { "companyId": 4, "tenantId": 3001, "dueDate": "2027-02-05", "amount": 7500.25, "amountRaw": "7500.25", "currency": "AED",
                "units": [ { "unitId": 678, "amount": 7500.25, "instalmentCount": 1 } ] },
  "companies": [ { "companyId": 4, "status": "Available", "reasons": [], "missingSemantics": [], "searchedThrough": "2027-04-10", "next": { "...": "same item as earliest" } } ]
}
```

Nothing through the horizon (explicitly not "no payment"):

```json
"nextPayment": { "status": "NoneWithinHorizon", "isComplete": true, "searchedThrough": "2028-10-07",
                 "detail": "No unpaid instalment falls after today through 2028-10-07. This does not say none exist later.", "earliest": null }
```

CRM customer (no verified CRM → PACT mapping; `mappingStatus` is `NotMapped`):

```json
"nextPayment": { "status": "Unavailable", "reasons": ["MappingNotAvailable"], "isComplete": false, "companies": [],
                 "detail": "No verified mapping to a PACT tenant exists for this customer, so no company or tenant is asked about." }
```

## 2. Selection rules (deterministic)

1. **Which companies:** exactly the companies PACT confirmed for the tenant (never a guessed one), in ascending id order.
2. **Gate:** a company is read only if `NextPayment.Enabled` **and** its attestation is complete. Otherwise it is
   `Unavailable` and EDSM is **not** called for it.
3. **Rows:** a due-installments row is a candidate only if its `companyID` and `tenantID` match, `chequeDueDate` is present,
   `amount` is present and positive, and its raw `status` (trimmed, case-insensitive) is in the attested unpaid set.
   Duplicates (same unit, voucher, cheque, date, amount, status) count once.
4. **Upcoming = strictly after today** (Asia/Dubai business date). Due today or earlier is overdue/due: EDSM's summary
   `dueAmount` already reports it, so it is excluded here and never added to the next payment.
5. **Date:** the earliest candidate date. **Amount:** the sum still unpaid on that date (all units), rounded once to 2 dp;
   `units[]` lists each unit's share (a row without a unit id is its own null-unit line). Nothing is silently picked between units.
6. **Several companies:** the earliest date wins, ties go to the lowest company id; every company's own answer stays in
   `companies[]`. `isComplete` is true only if every mapped company was answered (`Available` or `NoneWithinHorizon`).
7. **Search:** forward from today in `WindowDays` windows (default 92) through `SearchHorizonDays` (default 730), **not a fixed
   31-day look-ahead**. Windows overlap by one day, so an inclusive/exclusive range boundary cannot hide a row. The search stops
   at the first window holding a candidate (windows are visited in date order, so that is the earliest overall). Nothing found
   through the horizon is `NoneWithinHorizon` with `searchedThrough`.
8. **Failures:** EDSM error → `SourceUnavailable`; the request's overall deadline (shared with PACT discovery and the other EDSM
   calls) → `DeadlineExceeded`. A caller's own cancellation propagates.
9. **Missing schedule / multiple units** are explicit: no rows through the horizon is `NoneWithinHorizon` (never "no schedule");
   the scope is the earliest date across *all* units of the tenant, and later instalments of other units are not listed.

## 3. Configuration (`CollectionsSource:NextPayment`, all off by default)

```jsonc
"CollectionsSource": {
  "DueInstallmentsEnabled": false,            // UNRELATED and stays false: it governs the Payment tab's due-installments table
  "NextPayment": {
    "Enabled": false,                         // master switch
    "SearchHorizonDays": 730,                 // clamped 31-1830
    "WindowDays": 92,                         // clamped 7-366
    "Companies": {                            // absent = not confirmed = Unavailable. Fill ONLY from the EDSM owners' written answers.
      "4": {
        "UnpaidStatusValues": [ "<raw values EDSM confirmed mean unpaid>" ],
        "AmountRepresents": "RemainingUnpaid",            // the only accepted value; "OriginalScheduled" cannot be netted for partial payments
        "ChequeDueDateIsInstalmentDueDate": true,
        "ListsEveryUnpaidInstalment": true,
        "CurrencyConfirmed": true,
        "ConfirmedBy": "<name / team>", "ConfirmedOn": "2026-MM-DD", "EvidenceReference": "<SP definition, ticket or UAT record>"
      }
    }
  }
}
```

Environment variables: `CollectionsSource__NextPayment__Enabled=true`, `CollectionsSource__NextPayment__Companies__4__UnpaidStatusValues__0=…` etc.
Unchanged prerequisites: `CollectionsSource:EdsmProvider=Pact`, `PactApi:BaseUrl`, `PactApi__ApiKey`, `Pact:Provider=Http`,
`Collections:Enabled=true`, the service account in `Collections:Authorization:IntegrationEmployeeIds`, a verified
`CollectionsSource:EdsmNumberCulture`. Do **not** enable `DueInstallmentsEnabled` to make this populate.

## 4. CRM customers

A CRM-identified customer (`crm:{id}`) is `mappingStatus: NotMapped` and `nextPayment` is `Unavailable` /
`MappingNotAvailable`. No tenant or company is guessed from a phone number or name. A verified CRM → PACT
crosswalk does not exist (Collections-Integration §2.3 #8) — **dependency: CRM/PACT owners**. When it does, it plugs in
at the PACT mapping step; the next-payment logic needs no change.

## 5. Unresolved dependencies (the feature stays Unavailable until these are answered)

| # | Dependency | Owner |
|---|---|---|
| 1 | The six attestation items per company (`EDSM-Instalment-Semantics.md` §5) | EDSM owners |
| 2 | SP definitions for `p4DuePayments`, `p32DuePayments`, `p25GetCheques`, `p7GetCheques` | EDSM DB owner |
| 3 | Whether any unpaid-instalment schedule exists for **rented** companies (the routes read carry none) | EDSM owners / Leasing |
| 4 | Verified CRM → PACT crosswalk | CRM / PACT owners |
| 5 | Latency of company-wide due-installments windows against the 22 s Genesys deadline (up to 8 windows per company) | UAT measurement |
| 6 | UAT records with known figures (§6) | Collections / UAT |
| 7 | Host culture/time zone of EDSM (affects "today") | EDSM host owner |

## 6. UAT validation protocol — **NOT RUN**

This session had no network path to EDSM/PACT/UAT and no UAT records, so **none of this has been executed**. Run it after the
owners confirm the semantics and the company is attested. Fill the expected column from the EDSM/Collections records *before*
calling the API; a mismatch is a finding about the semantics, not about the test.

| # | Case (known tenant) | Expected (from the record) | Actual | Result |
|---|---|---|---|---|
| 1 | Next instalment ≤ 31 days out | date / amount | | |
| 2 | Next instalment **> 31 days** out (e.g. 120 days) | date / amount; `searchedThrough` ≥ date | | |
| 3 | **Partially paid** next instalment | amount = unpaid remainder, not the scheduled amount | | |
| 4 | Fully paid earlier instalment + unpaid later one | earlier one skipped | | |
| 5 | **Overdue** instalment plus a later upcoming one | next = the upcoming one; overdue only in `dueAmount` | | |
| 6 | Tenant with several units, different dates | earliest date; `units[]` lines; later units not listed | | |
| 7 | Two companies for one tenant | earliest across both; both in `companies[]` | | |
| 8 | Tenant with nothing unpaid | `NoneWithinHorizon` with `searchedThrough` | | |
| 9 | CRM-identified customer | `Unavailable` / `MappingNotAvailable`; no PACT/EDSM call | | |
| 10 | Company without attestation | `Unavailable` / `SemanticsNotConfirmed`; EDSM not called | | |
| 11 | Response time through TigerGroupWeb with the Genesys deadline | within 22 s, or `DeadlineExceeded` reported | | |

## 7. What has actually been tested

* **Fixture tests (TigerCS code against fake EDSM rows):** `NextPaymentCalculatorTests` (selection, overdue exclusion, same-date
  units, rounding, order independence), `CollectionsNextPaymentServiceTests` (the semantics gate for every missing item,
  no EDSM call when unconfirmed, search windows including a **120-day** instalment and a row on a window boundary, partial-payment
  remainder as attested, status/tenant filtering, ties, `NoneWithinHorizon`, source failure, deadline vs caller cancellation, clamping,
  the committed settings staying off) and `CollectionsPaymentSummaryAppServiceTests` (CRM customer, default off, enabled-unconfirmed).
* **Real EDSM / UAT verification: none.**

## 8. Deployment

* TigerCS API build only: **no migration**, no new secret. Behaviour is unchanged until `NextPayment:Enabled` is set *and* a company is attested.
* Re-import Data Action `08` in Genesys to receive the new outputs (existing outputs are unchanged).
* TigerGroupWeb: the two `by-key` routes already pass bodies through unchanged (Genesys-Collections-API.md §9), so the new members
  need no code change **provided** its summary proxy is a pass-through — to be confirmed in that repository (not accessible here).
  Its deployment status is "implemented and tested, not deployed" per that document.
