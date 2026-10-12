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
4. **Financials = the PACT account of that company + unit** (the same snapshot read as the Receivables page and the unit Payment Summary, `usp_Collections_GetUnitReceivables`): every unpaid instalment of that unit, classified with the one current-Dubai-month rule (`CollectionsDueMonth`): Overdue before the month, Due inside it, later months *Not yet due*. The Payment tab's figures are described in [Unit-Payment-Summary.md](Unit-Payment-Summary.md). It does not matter whether the CRM phone exists in PACT.
5. **Contact.** CRM name/mobile/email first; PACT fills a missing field; two different people are never merged (`ContactSourceConflict` -> review); several CRM owners of the unit -> `CrmCustomerAmbiguous`; CRM owner data naming a different customer -> `CrmOwnershipConflict`. A missing mobile never blocks the match.
6. **Only when CRM holds no eligible customer** is PACT searched by phone (`v1/contracts/{mobile}`): companies 4/32 go through the same unit read, restricted to that contract's tenant (a former tenant of the same unit is not mixed in); **company 7 (Leasing)** is listed per contract. Ended contracts (`contractEndDate` < today) and cancelled apartment codes (`*`) are excluded.
7. **More than one customer / unit / contract = a list with `selectionRequired`.** The first is never taken. `selectionId` is `crm:{crmUnitId}` or `pact:{company}:{tenant}:{unitId}:{contract}`.

## Financial states (per unit)

| `financialStatus` | Meaning |
| --- | --- |
| `Available` | Amounts due before the end of the current Dubai month (Overdue: before its first day; Due: inside it); `due`, `overdue`, `total`. |
| `NoDues` | **Confirmed zero**: PACT holds the unit and nothing is due. |
| `NoFinancialData` | Nothing usable (`PactHoldsNoRecord`, `CompanySnapshotNotLoaded`, `LeasingReceivablesSourceMissing`, `CompanyNotSupported`). **Not zero**: amounts are null. |
| `MatchFailed` | Failed or ambiguous match (`ProjectMappingMissing/Ambiguous`, `CompanyMappingAmbiguous`, `UnitCode…`, `CompanyMissing`, several PACT accounts). No amounts. |
| `SourceError` | A source could not be read (`PactUnavailable`, `ProjectMappingUnreadable`). Retry; no amounts. |

### Comparing the screens (what must match and what need not)

For the **same unit and the same scope** (all months, current Dubai month rule), **Due and Overdue must be identical** on Receivables, Campaigns and the Payment tab (`due` / `overdue`, and the cards *Due This Month* / *Overdue*). **Total** in Collections is `Due + Overdue`.
The Payment tab's **Remaining Balance is every unpaid payment, future ones included** (`payments.remainingBalance`, from the schedule table), so it is **not** required to equal Due + Overdue; it equals them only when nothing is due after this month. Cards and the schedule table are computed from the same rows (Due card = sum of the *Due* rows, Overdue card = sum of the *Overdue* rows, Remaining = sum of all rows). *Total Amount* and *Paid Amount* are `Unavailable` (the snapshot holds only unpaid remainders). Example: Due 4,000, Overdue 1,000, Remaining 16,500 is consistent (11,500 is not yet due).

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


## CRM contact and tower names (V012) - what was wrong and what the rules are now

Traced by reading the code path (the real CRM / PACT / production database cannot be reached from the build environment; `database/collections-receivables/tools/diagnose_crm_unit.sql` tells which of these causes hit a given unit).

**Why a tower showed only its number (TP119 -> "119").** Every screen took a tower's name from `dbo.CollectionsTowers` alone. A tower PACT holds that has no row there (V001: "Tower 119 is intentionally NOT seeded"), or whose row only repeats the number, rendered its number. The real name is what CRM calls the tower's project (`projectName` of the owner feed). It is now resolved in ONE place, `dbo.fn_Collections_TowerNames` (V012): `CollectionsTowers` when its name is a real name (not blank, not just `119` / `TP119`), else the single project name CRM holds for **that company's** tower; two different CRM names = no name. Nothing is typed into the application. Tables, the picker, the unit details and the review export all read it. `tools/repair_tower_names.sql` (dry run by default, re-runnable) writes the missing rows / names into `CollectionsTowers` so the tower can also be chosen in the picker.

**Why a CRM owner's contact did not reach a unit.** An owner got its company only from `CollectionsTowers`, and only when the tower number belonged to exactly ONE company. A tower missing from that table or present under both companies left the owner **without a company**, and an owner without a company is never joined to a unit: the page silently showed the (possibly empty) PACT contact. Other ways the contact can be lost, all now visible: the feed row had no PACT project code (counted as `NoProjectCode`, filled from the verified `CollectionsCrmProjectMap` when it has exactly one code for the project), a mobile that was valid but written in a form the normaliser did not accept (`966508650505` / Arabic-Indic digits were rejected; +966 is now kept as +966), and a CRM service problem that was shown as "No mobile".

**The company of an owner (V012).** In this order: (1) the verified project map names the company (`Map`); (2) `CollectionsTowers` has the tower in exactly one company (`Tower`); (3) exactly ONE company's PACT snapshot holds this **exact unit code** (`Snapshot`, recomputed after every receivables refresh by `usp_Collections_ResolveCrmOwnerCompanies`); (4) both companies hold it = **`Ambiguous`**: the sale is tied to neither, the unit is flagged `CrmCompanyAmbiguous` (needs review) and no CRM contact is used. A tower number alone never decides when it exists in two companies.

**Contact rules (unchanged in meaning, now applied everywhere through `CollectionsUnitLinkService` / `CollectionsContactLinker`).** CRM name / mobile / e-mail first; PACT completes only a field CRM lacks, and only when nothing contradicts CRM (different phones, or different e-mails when no phone confirms) - otherwise `ContactSourceConflict` and CRM alone is shown; several CRM buyers = `CrmCustomerAmbiguous`, nobody chosen. The same service feeds Receivables, Campaigns, the Payment tab / Payment Summary, both exports and the review records behind the Genesys lists (`ReviewRecordBuilder`); the contacts are re-linked from the CURRENT CRM data when a dispatch revalidates, so a stale approval copy is never trusted. CRM is read in bulk (one statement per request), never per row.

**A CRM service problem is not "CRM has no mobile".** `CrmAvailability`: `Disabled` (linking switched off: PACT contacts only, nothing flagged), `Ready`, `NotLoaded`, `Stale` (last successful load older than `CollectionsSource:CrmOwners:MaxAgeMinutes`, default 360) and `ReadFailed`. The last three block: every unit gets the review reason `CrmUnavailable`, the pages show a banner, **no Genesys list can be exported or dispatched** until CRM is back (the internal review CSV stays available). A failed latest refresh with recent data is a warning only.

**Order.** Receivables, Campaigns and the tower picker list the newest tower first: the tower **number as an integer, descending** (TP140, TP132, TP131 ... TP119; never the text, never the name; a non-numeric tower after the numeric ones). Inside a tower: unit number ascending (numeric before lettered), then the unit code, company, customer, unit id. The order is decided over the WHOLE filtered result before paging (SQL `ROW_NUMBER` for Receivables, the full in-memory result for Campaigns), so pages never repeat or skip a unit. The application (`CollectionsTowerOrder`) and SQL implement the same order; tests compare them.
