# EDSM Collections Contract (for TigerCS)

> Public copy of a static analysis of the EDSM source (supplied 2026-10-05). **Sanitized:** server addresses, database and login names, connection-string keys, local paths and security-finding details are removed. The API contract, financial definitions and code references are unchanged. TigerCS code cites its section numbers. The unsanitized original is held by the EDSM owners.

**Source:** static analysis of `external-data-source-manager` (EDSM), .NET 10 / ASP.NET Core, Dapper over SQL Server.
**Date:** 2026-10-05
**Scope:** `GET /v1/reports/payment-summary`, `GET /v1/due-installments`, `GET /v1/reports/payment-transactions`, `GET /v1/late-fines` (plus the upstream `GET /v1/contracts` / `GET /v1/contracts/transactions` they depend on).

> **Verification level.** Everything below comes from reading the C# source. I also checked that the compiled `bin/Debug` (2026-09-09) and `bin/Release` (2026-08-27) assemblies contain the same SP names, format strings and status literals as the source.
> **Not verified:** the stored-procedure SQL bodies, which are **not in this repository** (see §10), and runtime behaviour. The API was not run, partly because one code path writes to the database (§8.3).
> In this document, **VERIFIED** means proven by code. **UNVERIFIED** means it depends on SQL or the hosting environment.

---

## 1. Common transport contract

| Item | Value | Ref |
|---|---|---|
| Method | All four endpoints are `GET` with query-string parameters (`[FromQuery]` complex model) | `EDSM.API/Controllers/v1/*.cs` |
| Auth | Header `X-API-KEY`. Must equal config `ApiKeySettings:Key` (one shared key, ordinal compare). Get the value out-of-band; it is **not** reproduced here. | `EDSM.API/Middlewares/ApiKeyMiddleware.cs:15-31` |
| Missing key | `401`, `text/plain`, body `API Key was not provided.` (no envelope) | `ApiKeyMiddleware.cs:15-20` |
| Wrong key | `403`, `text/plain`, body `Unauthorized client.` (no envelope) | `ApiKeyMiddleware.cs:27-32` |
| JSON casing | ASP.NET Core defaults (System.Text.Json Web defaults) give **camelCase**. Example: `CompanyID` → `companyID`, `TenantID` → `tenantID`. Nulls are serialized. Property order is not guaranteed, so parse by name. | `EDSM.API/Program.cs:26` (no JSON options configured) |
| Query param names | `TenantId`, `CompanyId`, `TransactionTypeId`, `Mobile`, `FromDate`, `ToDate` (case-insensitive binding) | input models below |
| Dates in query | Parsed with invariant culture. Send `yyyy-MM-dd` (or ISO 8601). | ASP.NET Core query binding |
| HTTP status | **HTTP status code = `status` field in the envelope.** Controllers return `StatusCode(result.Status, result)`. | e.g. `ReportsController.cs:23-24` |
| DB timeout | 360 s per SP call. There is no API-level timeout. | `EDSM.DataAccess/DataBaseManager/SqlServerDapperProvider.cs:45-50` |
| Base URL | Not determinable from the repo (`EDSM.API.http` is a stale template; the CORS origin is the placeholder `https://app2.example.com`). See §10. | `Program.cs:18-20` |

### 1.1 Response envelope (VERIFIED)

`EDSM.BusinessLogic/Models/Response/Response.cs:5-55`

```json
{ "title": "", "status": 200, "data": <T or null> }
```

| Field | Type | Meaning |
|---|---|---|
| `title` | string | `""` on success. Error message on business-rule failure. |
| `status` | int | Mirrors the HTTP status. Possible values come from `HttpResponsesEnum` (`Response.cs:60-77`). In these endpoints only `200` and `400` are produced. |
| `data` | T or `null` | `null` whenever `status` = 400 |

### 1.2 Error matrix

| Case | HTTP | Body |
|---|---|---|
| Missing or bad API key | 401 / 403 | plain text (above) |
| Missing or unparseable required query param | 400 | **ASP.NET ProblemDetails**, not the envelope: `{type,title:"One or more validation errors occurred.",status:400,errors:{...},traceId}`. Triggered by `[ApiController]` and C# `required` members. *(Inferred from framework behaviour, not runtime-tested.)* |
| Unsupported `CompanyId` / `TransactionTypeId` | 400 | envelope, `data:null`, title `"Company not supported"` or `"Company or Transaction type not supported"` |
| SQL error, null-deref, or whitespace-only `TenantId` | 500 | No envelope. No exception handler is registered. You get the Developer Exception Page in Development and an empty body in Production. |
| Unknown TenantId | **200** | SPs return no rows, so all amounts are `"0.00"` / `0`. **You cannot distinguish "no such tenant" from "zero balance".** |

---

## 2. Identifier mapping: CompanyId and TenantId

### 2.1 CompanyId (VERIFIED)

`EDSM.BusinessLogic/Enums/CompanyEnum.cs:5-9`

| CompanyId | Enum name | Business model | SP prefix |
|---|---|---|---|
| 4 | TigerGroupDubai | **Owned** (buyer) | `p4…` |
| 32 | TigerGroupSharjah | **Owned** (buyer) | `p32…` |
| 25 | HirmasDubai | **Rented** (tenant) | `p25…` |
| 7 | AlsabeelSharjah | **Rented** (tenant) | `p7…` |
| 20 | AlsabeelSharjahTrio3 | **Rented** (tenant) | `p20…` (shares company 7's connection) |

Connection mapping: `EDSM.DataAccess/Repositories/Accounting/PactRepo.cs:22-89`.
In the analysed config, all five companies resolve to the same reporting database. The company is distinguished only by the SP prefix.
`helper.txt` lists only 4 companies and omits `20`. **The enum is authoritative.**

### 2.2 Company support per endpoint (VERIFIED)

| Endpoint | 4 | 32 | 25 | 7 | 20 | Ref |
|---|---|---|---|---|---|---|
| payment-summary | ✅ owned | ✅ owned | ✅ rented | ✅ rented | ✅ rented | `ReportsService.cs:33,47-54` |
| payment-transactions | ✅ owned | ✅ owned | ✅ rented | ✅ rented | ✅ rented | `ReportsService.cs:62,77-85` |
| due-installments | ✅ | ✅ | ✅ | ✅ | ❌ 400 | `DueInstallmentsService.cs:31` |
| late-fines | ✅ | ✅ | ❌ 400 | ❌ 400 | ❌ 400 | `LateFinesService.cs:30` |
| contracts/transactions (raw) | ✅ | ✅ | ✅ | ✅ | ✅ | other ids give **500**, not 400 (`ContractsService.cs:230-238`) |

### 2.3 TenantId (VERIFIED in code; meaning UNVERIFIED in SQL)

- Type: **string** on input (`TenantId`). It is passed unchanged as SP parameter `@tid` (`PactRepo.cs:56-89`). Values are numeric strings.
- Despite the name, it is the PACT account/tenant ID **for buyers too** (companies 4 and 32).
- **Where TigerCS gets it:** `GET /v1/contracts/{mobile}` returns one item per unit with `tenantID` (long) and `companyID` (int). Those come from SP columns `TenantID` and `CompanyID` (`ContractsService.cs:122-126`, `ContractSpEntity.cs:5,16`). Always send the **pair** (`tenantID`, `companyID`) from the same item.
- **TigerCS DB caveat:** EDSM writes units to the TigerCS customer-service database table `Info.UserUnit` (`CustomerServiceRepo.cs:33-57`). In that table, `RefId = TenantID`, **except Parking units, where `RefId = ContractID`** (`ContractsService.cs:315`). **Do not use `UserUnit.RefId` as TenantId for Parking rows.** `UserUnit.CompanyId` is the CompanyId above.
- Granularity: whether one TenantID covers one unit or all of a customer's units in that company depends on the `p{N}tenantStat` SPs. **UNVERIFIED.** Every endpoint here aggregates per (CompanyId, TenantId), not per unit.

---

## 3. `GET /v1/reports/payment-summary`

### 3.1 Code trace

| Layer | Location |
|---|---|
| Controller | `EDSM.API/Controllers/v1/ReportsController.cs:19-25` → `GetCustomerUnitPaymentsSummary` |
| Input model | `EDSM.BusinessLogic/Models/Reports/PaymentSummary/PaymentsSummaryInputModel.cs:5-6` |
| Output model | `…/PaymentSummary/PaymentsSummaryOutputModel.cs:5-9` |
| Service | `EDSM.BusinessLogic/Services/Reports/ReportsService.cs:30-58` |
| Owned calc (4, 32) | `ReportsService.PaymentSummary.cs:9-43` + `ReportsService.TransactionUtilities.cs:7-29` |
| Rented calc (7, 25, 20) | `ReportsService.PaymentSummary.cs:44-173` |
| Upstream data | `ContractsService.GetCustomerContractTransactionsByCompanyIdAsync` (`ContractsService.cs:217-258`), plus `LateFinesService` for owned (§6) |
| Repository / SQL | `PactRepo.cs:55-68`: SP `p4tenantStat`, `p32tenantStat`, `p7tenantStat`, `p25tenantStat`, `p20tenantStat` with `@tid` |
| Row entity | `EDSM.DataAccess/Models/ContractTransactionSpEntity.cs` |

### 3.2 Request

`GET /v1/reports/payment-summary?TenantId={string}&CompanyId={int}`. Both are required.

### 3.3 Response (`Response<PaymentsSummaryOutputModel>`)

```json
{
  "title": "",
  "status": 200,
  "data": {
    "totalAmount": "1,250,000.00",
    "paidAmount": "900,000.00",
    "dueAmount": "50,000.00",
    "outstandingAmount": "300,000.00",
    "lateFines": "1,200.00"
  }
}
```
*(The values are illustrative; the shape is verified.)*

**All five fields are strings, already formatted.** No numeric equivalents are returned.

### 3.4 Field definitions: Owned (CompanyId 4, 32) (VERIFIED)

First, every row is normalized (`ContractTransactionItemOutputModel.cs:24-38`): status, type and description are trimmed and lowercased, voucher is trimmed and uppercased, and `AmountValue = Credit` if Credit is non-null and non-zero, otherwise `Debit`. "today" means `DateTime.Now.Date` on the **server's local clock**.

| Field | Definition | Ref |
|---|---|---|
| `paidAmount` | Σ `Credit` over rows where `Credit > 0` | `TransactionUtilities.cs:15`, `PaymentSummary.cs:18-21` |
| `dueAmount` | Σ (`Debit − Credit`) over rows where Debit and Credit are non-null, `Credit < Debit`, and `ChequeDueDate ≤ today` | `TransactionUtilities.cs:18-20`, `PaymentSummary.cs:24-27` |
| `outstandingAmount` | Same as `dueAmount` but with `ChequeDueDate > today` | `TransactionUtilities.cs:23-25`, `PaymentSummary.cs:30-33` |
| `lateFines` | Late-fines value (§6), formatted, **only if > 0; otherwise `""`** | `PaymentSummary.cs:36-37` |
| `totalAmount` | `paid + due + outstanding`. **Excludes lateFines.** | `PaymentSummary.cs:40` |

Note: rows where `Credit` is NULL (debit-only installments, not yet partially paid) are **excluded** from due and outstanding. Whether the SP ever returns such rows is UNVERIFIED.

### 3.5 Field definitions: Rented (CompanyId 7, 25, 20) (VERIFIED)

Opening balance (OB) is the first row whose description is `"bf"`. `pob` = Σ `Credit` of rows with status `posted` and type containing `"against ob"`.

| Field | Definition | Ref |
|---|---|---|
| `paidAmount` | **+** Σ `AmountValue` of `Credit≠null`, status `posted`, voucher **not** starting with `JVP`, `JRN`, `JVA` or `PDPV`<br>**−** Σ `AmountValue` of refunds: `Debit≠null` and voucher starting with `IPV` or `PPV` (no status filter)<br>**+** Σ `Credit` of `Credit≠null`, voucher starting with `JRN`, status `posted` (fees paid)<br>**+** if `obCredit > obDebit`: `obCredit − obDebit − pob` | `PaymentSummary.cs:72-124` |
| `dueAmount` | **+** bounced cheques: `Credit≠null`, status `bounced`, `ChequeDueDate ≤ today` give Σ `AmountValue` − Σ `AdjustedAmount`<br>**+** if any `JRN` posted debit rows exist: Σ their `Debit` − Σ `Credit` of posted rows with type containing `"against fees"`<br>**+** if `obDebit > obCredit`: `obDebit − obCredit − pob` | `PaymentSummary.cs:125-163` |
| `outstandingAmount` | Σ `AmountValue` of `Credit≠null`, `ChequeDueDate≠null`, status `pdc` (post-dated cheques) | `PaymentSummary.cs:164-173` |
| `lateFines` | **Always `""`.** Never computed for rented companies. | `PaymentSummary.cs:44-71` (not set) |
| `totalAmount` | `paid + due + outstanding` | `PaymentSummary.cs:65` |

`dueAmount` can be **negative** when fee payments exceed fee debits; it is then formatted as, for example, `"-500.00"`.

### 3.6 Status and type literals used (after lowercasing)

`posted`, `bounced`, `pdc` (status). `against ob`, `against fees` (type). `bf` (description = opening balance). Voucher prefixes: `JVP`, `JRN`, `JVA`, `PDPV`, `IPV`, `PPV`.
The full set of status values the SPs can emit is **UNVERIFIED** (needs the SP source).

---

## 4. `GET /v1/due-installments`

### 4.1 Code trace

| Layer | Location |
|---|---|
| Controller | `EDSM.API/Controllers/v1/DueInstallmentsController.cs:18-24`. The method is misnamed `GetCustomerLateFinesByCompanyId`, which may show up as the Swagger operationId. |
| Input | `EDSM.BusinessLogic/Models/DueInstallments/DueInstallmentsInputModel.cs:5-7` |
| Output | `…/DueInstallments/DueInstallmentsOutputModel.cs:5-12` |
| Service | `EDSM.BusinessLogic/Services/DueInstallments/DueInstallmentsService.cs:28-65` |
| Repository / SQL | `PactRepo.cs:71-81`: 4→`p4DuePayments`, 32→`p32DuePayments`, 25→`p25GetCheques`, 7→`p7GetCheques`, each with `@fromDate`, `@toDate` |
| Row entity | `EDSM.DataAccess/Models/DueInstallmentsSpEntity.cs` |

### 4.2 Request

`GET /v1/due-installments?CompanyId={int}&FromDate={yyyy-MM-dd}&ToDate={yyyy-MM-dd}`. All three are required. **There is no TenantId parameter.**

### 4.3 Response (`Response<IEnumerable<DueInstallmentsOutputModel>>`)

```json
{
  "title": "",
  "status": 200,
  "data": [
    {
      "companyID": 4,
      "tenantID": 12345,
      "unitID": 678,
      "voucherNumber": "PDC-0001",
      "chequeNumber": "000123",
      "chequeDueDate": "2026-10-15T00:00:00",
      "amount": 25000.0,
      "status": "<raw SP value>"
    }
  ]
}
```

| Field | Type | Notes |
|---|---|---|
| `companyID` | int | from SP |
| `tenantID` | **int** (32-bit; it is `long` in `/v1/contracts`) | Join key back to the customer. Overflows if PACT IDs ever exceed 2³¹. |
| `unitID` | int | PACT unit id |
| `voucherNumber` | string | raw, not trimmed |
| `chequeNumber` | string | declared non-null, but becomes `null` if the SP returns NULL |
| `chequeDueDate` | DateTime | ISO 8601 **without offset** (Kind Unspecified), for example `2026-10-15T00:00:00`. Treat it as UAE local date. |
| `amount` | **number (double)**, unformatted | No currency field. Floating-point artefacts are possible (for example `1234.5600000000001`), so round to 2 dp. |
| `status` | string, **raw** | Not trimmed or lowercased. Rows with null or blank status are **dropped** (`DueInstallmentsService.cs:50`). The value set is **UNVERIFIED** (SP source needed). |

### 4.4 Semantics

- **Company-wide, not per customer.** Returns every installment in the date range for all tenants of that company, with no paging. TigerCS must filter by `tenantID`.
- Whether the range is inclusive or exclusive, and whether rows are already paid or not, is decided inside the SPs (**UNVERIFIED**). The 4/32 SPs (`…DuePayments`) and the 25/7 SPs (`…GetCheques`) are different procedures and may have different semantics.
- No amount formatting and no currency.

---

## 5. `GET /v1/reports/payment-transactions`

### 5.1 Code trace

| Layer | Location |
|---|---|
| Controller | `ReportsController.cs:26-32` |
| Input | `…/Reports/PaymentTransaction/PaymentTransactionsIntputModel.cs:5-8` |
| Output | `PaymentTransactionsOutputModel.cs:5-12`, `PaymentTransactionItemOutputModel.cs:5-11` |
| Service | `ReportsService.cs:59-89`; owned `ReportsService.PaymentTransactions.cs:9-121`; rented `:125-402` |
| SQL | same `p{N}tenantStat` as payment-summary (shared cache, §8) |

### 5.2 Request

`GET /v1/reports/payment-transactions?Mobile={string}&TenantId={string}&CompanyId={int}&TransactionTypeId={1|2|3|4}`. All four are required. `Mobile` is only *used* for rented with type 4.

### 5.3 Enums (VERIFIED)

`TransactionTypeEnum` (`Enums/TransactionTypeEnum.cs`): `1` Paid, `2` Due, `3` Outstanding, `4` All.
`PaymentTypeEnum` (`Enums/PaymentTypeEnum.cs`): `1` Cash, `2` Cheque, `3` Fees, `4` OpeningBalance, `5` CurrentContractAmount.

### 5.4 Response

```json
{
  "title": "", "status": 200,
  "data": {
    "totalAmount": 900000.0,
    "formattedTotalAmount": "900,000.00",
    "transactions": [
      { "amount": 450000.0, "formattedAmount": "450,000.00", "date": "15-Mar-2026",
        "chequeNumber": null, "transactionTypeId": 1, "paymentTypeId": null }
    ]
  }
}
```

| Field | Type | Notes |
|---|---|---|
| `totalAmount` | double | `totalNumbericAmount` is `[JsonIgnore]` and not emitted |
| `formattedTotalAmount` | string `#,##0.00` | **`""` (not `"0.00"`) when there are no matching rows** (owned types 1–3, rented type 3) |
| `transactions[].amount` | double | |
| `transactions[].formattedAmount` | string | `#,##0.00`. **One exception: rented Due/Fees rows add `" AED"`** (`PaymentTransactions.cs:311`) |
| `transactions[].date` | string `dd-MMM-yyyy` | `ChequeDueDate` if present, else `CreatedDate`. **`""` for opening-balance and contract-amount rows.** |
| `transactions[].chequeNumber` | string or null | Owned: set only on Due rows. Rented: set on cheque rows. |
| `transactions[].transactionTypeId` | int or null | |
| `transactions[].paymentTypeId` | int or null | **Always null for owned (4, 32).** |

### 5.5 Consistency with payment-summary

| Company | Type | Matches summary field? |
|---|---|---|
| Owned | 1, 2, 3 | ✅ Same classification as summary (`TransactionUtilities.cs`) |
| Owned | 4 All | ✅ Total = paid + due + outstanding = summary `totalAmount` |
| Rented | 1 Paid | ⚠ Total matches summary. But **refund rows appear with positive `amount` and `transactionTypeId=1`**, indistinguishable from payments (`:210-224`). **JRN fee rows use `Debit` for `amount` but `Credit` for the total.** If `Debit` is NULL this throws a 500 (`:227-239`). |
| Rented | 2 Due | ⚠ **Can differ from summary `dueAmount`.** There is a fee-allocation bug at `:320` (`paidFeesAmount + (paidFeesAmount − Debit)`), and the total at `:323` uses the mutated value. The summary uses the original fee total (`PaymentSummary.cs:137-142`). |
| Rented | 3 Outstanding | ✅ matches |
| Rented | 4 All | ❌ **Different concept.** It is OB net (`obDebit − obCredit`) plus the contract value (`ContractNetAmount + ContractServicesNetAmount` of the mobile's contracts with this TenantId) (`:358-401`). It is **not** paid + due + outstanding. |

---

## 6. `GET /v1/late-fines`

| Layer | Location |
|---|---|
| Controller | `EDSM.API/Controllers/v1/LateFinesController.cs:18-24` |
| Input | `Models/LateFines/CustomerLateFinesInputModel.cs:5-6` (`CompanyId` int, `TenantId` string, both required) |
| Output | `Models/LateFines/CustomerLateFinesOutputModel.cs:5`. Only `{ "lateFines": <double> }` |
| Service | `Services/LateFines/LateFinesService.cs:27-56` |
| SQL | `PactRepo.cs:85-89`: 4→**`p4tenantStatTest`**, 32→**`p32tenantStatTest`** with `@tid` |

**Definition (VERIFIED):** `lateFines = Σ Debit − Σ Credit` over rows whose `Description` contains `"fine"` (case-insensitive substring) (`LateFinesService.cs:48-50`).

- This is a raw double: unrounded, **can be 0 or negative**, with no formatting and no currency.
- The payment-summary embeds this same value, but only when it is > 0 (§3.4).
- Production uses SPs whose names end in **`Test`**. Confirm with the DB owner that these are the intended production procedures.
- The `"fine"` substring match also matches words such as "define" or "refined". Accuracy depends on PACT description conventions (**UNVERIFIED**).
- The response `data` is `{"lateFines": 1200.0}`.

---

## 7. Amount formatting and currency

| Aspect | Behaviour | Ref |
|---|---|---|
| Format string | `"#,##0.00"`: thousands separators, exactly 2 decimals, leading `-` for negatives, **no currency symbol** | all `ToString("#,##0.00")` in `ReportsService.*.cs` |
| Culture | **No culture is specified.** Formatting uses the host process `CurrentCulture`, and there is no `UseRequestLocalization`. On `en-US`/`en-GB` you get `1,234.50` and `05-Oct-2026`. Other host cultures change the separators and month names. **The production host culture is UNVERIFIED.** | `Program.cs` (absent) |
| Numeric type | `double` end-to-end (SQL to Dapper to C#). Formatted strings are rounded to 2 dp. Raw numbers may show binary floating-point noise. | entities and models |
| Currency | **No currency field anywhere.** Amounts are implicitly **AED**. The only explicit currency is the stray `" AED"` suffix on rented Due/Fees `formattedAmount`. | `PaymentTransactions.cs:311` |
| `AdjustedAmount` | Mapped as **`int`** (`ContractTransactionSpEntity.cs:16`, `ContractTransactionItemOutputModel.cs:18`). Fractional adjustments may be truncated or fail in Dapper conversion, which affects rented bounced-cheque due. SQL column type is **UNVERIFIED**. | |

**Recommendation for TigerCS:** display the formatted strings from payment-summary. For arithmetic, use numeric fields rounded to 2 dp (`decimal`). Label the currency as AED on the TigerCS side, and strip any `" AED"` suffix before parsing.

---

## 8. Cache behaviour (VERIFIED)

### 8.1 Mechanism

- `IMemoryCache` in-process (`BusinessLogic/Extensions/ServiceCollectionExtensions.cs:14-15`, `CacheManager/InMemoryCacheProvider.cs:20-27`).
- **Absolute** expiration (`AbsoluteExpirationRelativeToNow`), not sliding.
- **Per instance.** Multiple app instances or app-pool recycles each keep their own cache.
- There is no invalidation endpoint, no bypass parameter, and no cache headers. `Remove` exists but is never called.
- Durations are read per request from `CacheDuration` config in **minutes**. Currently every value is `10` (`EDSM.API/appsettings.json:25-32`, `Models/AppSettings/CacheDuration.cs`). If a key is missing it becomes 0, and the cache `Set` would throw, giving a 500.
- 400 results are **not** cached. Exceptions are not cached. **Empty or zero results are cached** for the full duration.

### 8.2 Keys (`CacheManager/CacheKeysManager.cs`)

| Endpoint | Key | Duration key | Ref |
|---|---|---|---|
| payment-summary | `customer_payment_summary_{companyId}_{tenantId}` | `CustomerUnitPaymentSummary` | `CacheKeysManager.cs:25-29`, `ReportsService.cs:40-43,56` |
| payment-transactions | `customer_payment_transactions_{companyId}_{tenantId}_{transactionTypeId}` (**no mobile**) | `CustomerUnitPaymentTransaction` | `:31-35`, `ReportsService.cs:70-73,87` |
| due-installments | `customer_due_installments_{companyId}_{fromDate}_{toDate}` (dates via culture `ToString()`) | `DueInstallments` | `:42-46`, `DueInstallmentsService.cs:36-39,63` |
| late-fines | `customer_late_fines_{companyId}_{tenantId}` | `CustomerLateFines` | `:37-41`, `LateFinesService.cs:35-38,54` |
| upstream transactions | `customer_contract_transactions_{companyId}_{tenantId}` | `CustomerContractTransaction` | `:15-19`, `ContractsService.cs:224-227,256` |

### 8.3 Staleness and side effects

- **Nested caches.** Payment-summary is built from the cached transactions entry (and, for owned, the cached late-fines entry). That data can already be 10 min old, and the summary is then cached another 10 min. **A payment posted in PACT can take up to about 20 minutes to appear in payment-summary.** The same applies to payment-transactions.
- The summary and transactions share the upstream transactions cache, so they are consistent with each other within a window, apart from the rented bugs in §5.5.
- **Side effect:** rented `payment-transactions` with `TransactionTypeId=4` calls `ContractsService.GetCustomerContractsAsync`. That method **writes** to the identity-management database (customer type) and inserts into the customer-service table `Info.UserUnit` (`ContractsService.cs:160, 288-309`). `/v1/contracts/{mobile}` behaves the same way. The other three endpoints are read-only.
- `ContractsService.cs:64-66` contains a hard-coded mobile-number substitution that affects contract lookups for one specific number.

---

## 9. Defects and risks for TigerCS to design around

| # | Severity | Issue | Ref |
|---|---|---|---|
| 1 | High | Rented Due total in payment-transactions can disagree with summary `dueAmount` (fee-allocation arithmetic) | `PaymentTransactions.cs:320,323` |
| 2 | High | Rented Paid JRN-fee rows: `amount` uses `Debit` while the filter is on `Credit`, so a NULL Debit gives a 500 | `PaymentTransactions.cs:227-239` |
| 3 | High | Unknown TenantId returns 200 with zeros, not 404 | §1.2 |
| 4 | Medium | Rented "All" is OB plus contract value, not a ledger total. It also requires `Mobile`, which is not part of the cache key. | `PaymentTransactions.cs:358-401` |
| 5 | Medium | Late fines: `…StatTest` SPs, `"fine"` substring heuristic, negative values possible | `LateFinesService.cs:48-50`, `PactRepo.cs:85-89` |
| 6 | Medium | Culture-dependent number and date strings | §7 |
| 7 | Medium | `due-installments.tenantID` is int32, while contracts use int64 | `DueInstallmentsOutputModel.cs:6` |
| 8 | Medium | `AdjustedAmount` typed `int` | `ContractTransactionSpEntity.cs:16` |
| 9 | Low | Refund rows look like payments in rented Paid list | `PaymentTransactions.cs:210-224` |
| 10 | Low | `UserUnit.RefId` is ContractID for Parking, not TenantID | `ContractsService.cs:315` |
| 11 | Low | Unsupported CompanyId on `/v1/contracts/transactions` gives 500, not 400 | `ContractsService.cs:237` |
| 12 | Low | Public `GET /v1/contracts/{mobile}/customer-type` ignores CompanyId 20 for tenants, while the internal classifier includes it | `ContractsService.cs:265` vs `:353` |
| 13 | Security | A configuration-handling finding was reported to the EDSM owners. Details are intentionally omitted from this public copy. | (withheld) |

---

## 10. Needed from outside this project

| # | What | Where (exact) | Why |
|---|---|---|---|
| 1 | **Stored-procedure definitions** (`sp_helptext` or script) for: `p4tenantStat`, `p32tenantStat`, `p7tenantStat`, `p25tenantStat`, `p20tenantStat`, `p4tenantStatTest`, `p32tenantStatTest`, `p4DuePayments`, `p32DuePayments`, `p25GetCheques`, `p7GetCheques` (and, for TenantId origin, `p4tenantphone2`, `_p4tenantphone2`, `p32tenantphone2`, `p7tenantphone`, `p20tenantphone`, `p25tenantphone`) | EDSM's reporting database (server, database and login from the EDSM owners). Called unqualified, so they live in the EDSM login's default schema (likely `dbo`). | Status value sets, date-range inclusivity, column types (money/decimal vs float), TenantId granularity, and the meaning of the `Test` variants |
| 2 | Deployed build and config | The EDSM publish output (see `EDSM.API/Properties/PublishProfiles/`), plus the production host's configuration and IIS site | Confirm the deployed version matches source (last local Release build is 2026-08-27; source changed 2026-09-09), the real `CacheDuration`, and the base URL |
| 3 | Production host culture | Windows regional settings / app-pool identity culture on the IIS host | Number and date string format (§7) |
| 4 | `Info.UserUnit` schema | TigerCS customer-service database (from its DBA) | TigerCS-side join on `RefId` / `CompanyId` |
