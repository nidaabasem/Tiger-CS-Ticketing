# CRM bulk owner feed – `GET /TicketingSystem/GetUnitOwners`

Collections (Receivables, Campaigns) and the unit Payment Summary link a unit to its customer **by tower + unit**, CRM first, PACT completing.
The repository holds no bulk by-unit CRM source (only `GetBuyerByPhone`, `GetUnitDetails`, `GetCustomerDocuments`), and the CRM entity column names are not in this repository, so
linking is **not complete end-to-end until CRM publishes this endpoint** and `CollectionsSource:CrmOwners:Enabled` is switched on. Until then Collections runs on PACT contact data only
(`CrmStatus = Unavailable`), nothing breaks, and no unit is hidden.

## Request
`GET {Crm:BaseUrl}/TicketingSystem/GetUnitOwners?page=1&pageSize=1000` with header `X-SECRET-KEY: {Crm:SecretKey}` (same auth as the other TicketingSystem actions). Paged, stable order (e.g. by LeadId), one request per page.

## Response
```json
{ "success": true, "message": null, "total": 12345,
  "owners": [ { "leadId": 1, "leadStatus": 8, "leadStatusName": "Sold", "customerType": 1, "customerId": 99,
                "fullNameEnglish": "…", "fullNameArabic": "…", "mobileNumber": "…", "email": "…",
                "unitId": 5001, "unitNumber": "101", "projectId": 7, "projectCode": "TP140", "projectName": "…" } ] }
```
A page shorter than `pageSize` ends the load.

## What CRM must guarantee
| Field | Requirement |
|---|---|
| `projectCode` + `unitNumber` | Joined as `projectCode-unitNumber` they equal PACT's unit code (`TP140-101`). **Confirm the CRM project column that holds `TP###`.** |
| `leadStatus` | Only Sold / Contract leads. Observed: 4 = Contract, 8 = Sold (**confirm against CRM's enum**; configurable in `EligibleLeadStatuses`). |
| `leadStatusName` | Sent so a cancelled lead is refused by name too (`cancel`). Cancelled leads should not be sent at all. |
| `customerType` | 1 = buyer. Tenants/representatives are ignored by Ticketing. |
| One row per (lead, customer) | A unit with several qualifying customers is flagged *needs review*; Ticketing never picks the first. A former owner whose lead was cancelled must not appear. |
| Contact | `mobileNumber` / `email` as stored; Ticketing normalises (E.164 / lower-case). Empty values are allowed. |

## Ticketing side
* Background job `collections-crm-owners-refresh` (`CollectionsSource:CrmOwners:RefreshCron`, default hourly) pages the endpoint and publishes a complete run into `CollectionsCrmUnitOwner` (V010) atomically. A failed or empty load keeps the previous data and is recorded (`GET /api/collections/crm-owners/status`).
* Pages read the local copy with ONE set-based query (`fn_Collections_CrmUnitLinks`); nothing calls CRM or PACT per row.
* Settings: `Crm:BaseUrl`, `Crm:SecretKey`, `CollectionsSource:CrmOwners:*` (disabled by default).
* The company of a CRM row is resolved through `CollectionsTowers`; a tower that belongs to both companies stays unlinked until CRM/PACT tells which (visible as `UnlinkedRows` in the status).

## Contact precedence
CRM value first; PACT fills a missing name / mobile / email, never replacing a valid value with an empty one. Different normalised phones (or different e-mails when phones cannot confirm the same person) = `ContactSourceConflict` (256): CRM alone is shown and the unit is marked for review; two people are never merged. Several CRM customers = `CrmCustomerAmbiguous` (128).

## Leasing (PACT company 7) fallback
For a customer with no unit tied to an eligible CRM record, `GET /api/collections/leasing/payment-summary?mobile=` searches PACT by the **normalised** mobile (`+971…`, `971…`, `00971…`, `05…` are one number) via the existing `GET v1/contracts/{mobile}` lookup and keeps only `companyID = 7`. Each contract/unit is its own entry (company, tenant, unit, contract); ended contracts (`contractEndDate` before today = former tenant) are excluded.

Not complete, and stated plainly:
* **Amounts:** no company-7 receivables source exists (the snapshot, its tables and procedures are hard-wired to companies 4/32). Leasing amounts are `NoFinancialData`, never zero. Needed: a bulk `p7AccountReceivables`-style procedure on PACTRPT with the same columns as `p4/p32AccountReceivables` (tenant, unit, contract, voucher, due date, remaining/original/paid amount, status).
* **Cancelled contracts:** the contracts lookup carries no cancellation flag (only `contractEndDate`); PACT must expose a contract status (or only return non-cancelled contracts) before "cancelled" can be excluded by status.
* **Collections lists:** they stay bulk and never call PACT per row, so the by-mobile Leasing path is a detail (Payment Summary) read only; listing Leasing units needs the company-7 bulk source above.
