# Customer and unit linking (Payment tab, phone lookup, New Ticket)

One service, `CustomerUnitLinkService` (`src/TigerCS.Application/Modules/Collections/Services/`), links a customer to their units and to the PACT figures of **each unit**.
A phone number only *finds the customer*; money is never looked up by phone.

## Flow

1. **Normalise the phone** (`CollectionsContactNormalizer.NormalizePhone`, E.164; `0501234567`, `971501234567`, `+971501234567` are one number). CRM is asked with the number as typed (as the New Ticket lookup always did), PACT with the normalised number.
2. **CRM first.** `GetBuyerByPhone` gives the customer and their units. Only a **buyer's Sold or Contract lead that is not cancelled** counts (`CrmSaleEligibility`; the status *name* decides - see "Status definitions" below). Several CRM customers on one number are returned as separate candidates, never collapsed to the first.
3. **Each CRM unit -> PACT.** CRM sends only `projectId` + unit number. The verified `dbo.CollectionsCrmProjectMap` (V011) gives the PACT project code (`TP140`), so the PACT unit code is `TP140-909`. The company comes from the mapping row, else from `dbo.CollectionsTowers` by tower number. Never from a display name.
   * no mapping row -> `MatchFailed / ProjectMappingMissing` (no amounts are looked up, none are implied)
   * several codes for one CRM project -> `ProjectMappingAmbiguous`
   * the tower exists in both companies and the mapping does not name one -> `CompanyMappingAmbiguous` (nothing is chosen)
4. **Financials = the PACT account of that company + unit** (the same snapshot read as the Receivables page and the unit Payment Summary, `usp_Collections_GetUnitReceivables`): only unpaid instalments due **today or earlier (Asia/Dubai)**, `Total = Due + Overdue`. It does not matter whether the CRM phone exists in PACT.
5. **Contact.** CRM name/mobile/email first; PACT fills a missing field; two different people are never merged (`ContactSourceConflict` -> review); several CRM owners of the unit -> `CrmCustomerAmbiguous`; CRM owner data naming a different customer -> `CrmOwnershipConflict`. A missing mobile never blocks the match.
6. **Only when CRM holds no eligible customer** is PACT searched by phone (`v1/contracts/{mobile}`): companies 4/32 go through the same unit read, restricted to that contract's tenant (a former tenant of the same unit is not mixed in); **company 7 (Leasing)** is listed per contract. Ended contracts (`contractEndDate` < today) and cancelled apartment codes (`*`) are excluded.
7. **More than one customer / unit / contract = a list with `selectionRequired`.** The first is never taken. `selectionId` is `crm:{crmUnitId}` or `pact:{company}:{tenant}:{unitId}:{contract}`.

## Financial states (per unit)

| `financialStatus` | Meaning |
| --- | --- |
| `Available` | Amounts due today or earlier; `due`, `overdue`, `total`. |
| `NoDues` | **Confirmed zero**: PACT holds the unit and nothing is due. |
| `NoFinancialData` | Nothing usable (`PactHoldsNoRecord`, `CompanySnapshotNotLoaded`, `LeasingReceivablesSourceMissing`, `CompanyNotSupported`). **Not zero**: amounts are null. |
| `MatchFailed` | Failed or ambiguous match (`ProjectMappingMissing/Ambiguous`, `CompanyMappingAmbiguous`, `UnitCode…`, `CompanyMissing`, several PACT accounts). No amounts. |
| `SourceError` | A source could not be read (`PactUnavailable`, `ProjectMappingUnreadable`). Retry; no amounts. |

## Entry points (all reuse the service)

| Caller | Route / code |
| --- | --- |
| Customer Details -> **Payment tab** | `CustomerPaymentPanelLoader.LoadAsync` -> `GET /api/collections/customers/crm/{crmCustomerId}/units` first; the per-account / EDSM views are used only when they show real figures, otherwise the unit diagnosis replaces the generic "No payment figures" message. |
| `/Customers/Payments` (payments before a ticket) | `LoadLookupAsync` -> same route (CRM keys); PACT keys fall back to `GET /api/collections/customers/lookup` filtered to that tenant when EDSM shows no figures (Leasing). |
| **New Ticket** (property + review steps) | `PaymentPanelOptions.PreferredCrmUnitId` = the selected CRM unit; typed phone as `CrmLookupPhone`. The panel shows name, mobile, email, tower and apartment of the selected unit. |
| Phone search / API | `GET /api/collections/customers/lookup?phone=&selection=` |
| Unit code (unchanged) | `GET /api/collections/units/payment-summary`, `…/leasing/payment-summary`, all Genesys routes - not modified. |

## Root cause of the reported "No payment figures for this customer" (CRM 498397, Al Ghaf Tower 909)

Traced through the code (the real CRM/PACT/API could not be reached from this environment, so the trace is by reading the code path, not by observing the live response):

1. `CustomerProfile` -> `CustomerPaymentPanelLoader.LoadAsync("crm:498397", 498397)` -> `GET /api/collections/customers/498397/outstanding`. The per-account source is `UnavailableCollectionsFinancialSource` unless `CollectionsSource:Provider` is set (default `Unavailable`; `Fixture` is refused outside Development/Testing) -> 503 `FinanceUnavailable` -> panel state `Unavailable`.
2. The loader then asks EDSM: `GET /api/collections/customers/by-key/crm:498397/payment-summary` -> `CollectionsPaymentSummaryAppService.ResolveAsync`. EDSM is keyed by **PACT companyID + tenantID**. A `crm:` customer has no `ExternalSource = Pact` / tenant id, so the method returns at once, **before any PACT call**, with `NotMappedDetail = "…identified by Tiger CRM… no verified mapping from a CRM customer to a PACT tenant exists"` -> `MappingStatus = NotMapped` -> `_CustomerPaymentTab.cshtml` renders `No payment figures for this customer.`

So the verified cause is **a missing CRM-customer -> PACT identity mapping in the only route the Payment tab used** (it is not phone discovery - the phone is never even sent to PACT for a `crm:` key - and it is not a missing snapshot: the unit's PACT receivables were never consulted). The old tab could therefore *never* show figures for a CRM-only customer unless a ticket had linked them to a PACT tenant. After this change the tab asks for the **unit** first. Whether Al Ghaf Tower 909 then shows figures depends on two facts that must be checked against the real systems: (a) the CRM project "Al Ghaf Tower" has a row in `CollectionsCrmProjectMap` (or the CRM owner feed supplies it) and (b) PACT holds `TP<tower>-909` in the loaded snapshot. Missing (a) now reads `ProjectMappingMissing`, missing (b) `PactHoldsNoRecord`, a confirmed zero `NoDues` - all distinguishable.

## Status definitions

The numeric CRM lead statuses are **not verified** in this repository. Evidence: production responses observed with `4 = Contract`, `8 = Sold`; the harness stub `Contract = 4, Sold = 8, Cancelled = 9` (a test stub, not CRM's enum); `docs/Genesys/CRM-GetUnitDetails-Field-Mapping.md` says `LeadStatus.Contract` appears only in commented-out CRM code and `GetBuyerByPhone` excludes only Cancelled (Hot / Reserved leads can come back). Therefore eligibility uses the **status name** CRM sends (Sold / Contract, not containing "cancel"); `CollectionsSource:CrmOwners:EligibleLeadStatuses` (4, 8) is a fallback only for a row with no name.

## Leasing (company 7) - what exists

* Discovery by phone: yes (`v1/contracts/{mobile}`, `companyID = 7`), current contracts only.
* Financial source: **none**. The snapshot, its tables and refresh procedures (`p4AccountReceivablesV2`, `p32AccountReceivablesV2` on PACTRPT via `[10.10.10.94]`) are hard-wired to companies 4 and 32; the repository contains no company-7 procedure, so none is invented. Leasing contracts are returned as `NoFinancialData / LeasingReceivablesSourceMissing` with that exact text. Needed: a PACTRPT procedure for company 7 with the same columns (tenant, unit, contract, voucher, due date, remaining/original/paid amount, status) plus snapshot support.
* Cancelled contracts: PACT's contract lookup carries no cancellation or status field (`unitStatus` is not interpreted - its meaning is unpublished). Only an ended contract and a cancelled apartment code (`*`) can be excluded today.
