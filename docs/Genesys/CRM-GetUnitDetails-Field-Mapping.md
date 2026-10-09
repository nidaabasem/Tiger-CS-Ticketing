# CRM `GetUnitDetails` — field mapping against the supplied CRM source

Source inspected (2026-10-09): `TicketingSystemController.cs` (actions `GetBuyerByPhone`,
`GetCustomerDocuments`). **Nothing else of the CRM was available** — no EF model classes,
no `.edmx`/migrations, no enums file. Only names that appear in that file are used; every
other cell is a stated question, not a guess.

Deliverable: `docs/Genesys/crm-insertion/TicketingSystemController.GetUnitDetails.cs`
(insertion-ready, **not compiled against the real CRM project**) plus
`crm-insertion/harness/` (22 logic tests against stubs; see "What was verified").

## What the supplied source confirms

| Fact | Evidence in the supplied file |
|---|---|
| ASP.NET MVC 5 + EF6 (`System.Web.Mvc`, `System.Data.Entity`), context via `new dbConnection().DBEntity` | usings, `GetBuyerByPhone` |
| Service auth = header `X-SECRET-KEY` vs `AppSettings["TicketingSecretKey"]` | `GetBuyerByPhone`, `GetCustomerDocuments` |
| JSON 401 without login redirect needs `Response.SuppressFormsAuthenticationRedirect = true` and `TrySkipIisCustomErrors = true`, `[AllowAnonymous]`, constant-time compare | `GetCustomerDocuments`, `CustomerDocumentsJson`, `CustomerDocumentsKeyMatches` |
| Buyer ownership = `tblLeadCustomers` (`LeadID`, `CustomerID`, `CustomerType == CustomerType.Buyer`, `UnitID` `long?`) joined to `tblLeads` (`ID`, `Status`), `tblUnits` (`ID`, `ProjectID`), `tblProjects` (`ID`) | `GetBuyerByPhone` query |
| `LeadStatus.Sold`, `LeadStatus.Cancelled` (compiled); `LeadStatus.Contract` appears only in commented-out code | `GetBuyerByPhone` |
| `tblUnits`: `Number`, `Status`, `Type` (int), `FloorNumber`, `SuitesArea`, `BalconyArea`, `NetArea`, `CommonArea`, `GrossArea`, `UnitPlanURL` | `GetBuyerByPhone`, `GetCustomerDocuments` |
| `tblProjects`: `Name`, `ArabicName` | `GetBuyerByPhone` |
| `tblAttachments` (`ParentID`, `ParentType`, `DocumentType`, `Status`, `FileURL`); `DocumentType` 6 = RegistrationReceipt (a **document**) | `GetCustomerDocuments` |

## Public field → CRM source

| Public field | CRM source | Status |
|---|---|---|
| ownership of customer/unit/lead | `tblLeadCustomers` ⨝ `tblLeads` ⨝ `tblUnits` ⨝ `tblProjects`, `CustomerID`, `LeadID`, `UnitID`, `CustomerType == Buyer`, `tblLeads.Status ∈ {Sold, Contract}` | **implemented** (strict; see finding 1) |
| `unitId` / `unitNumber` / `floor` / `unitType.code` / `booking.*` / `project.{id,name,arabicName}` | already returned by `GetBuyerByPhone` (`tblUnits.ID/Number/FloorNumber/Type`, `tblLeads.ID/Status`, `tblProjects.ID/Name/ArabicName`) | connected today (not via this action) |
| `unit.area`, `unit.areaUnit` | five area columns exist; **which one is the customer-facing area, and the unit (sqft/sqm), are not in the source** | **blocked — question A1** |
| `unit.unitTypeName` | `tblUnits.Type` is an int; label source unknown | blocked — A2 |
| `unit.towerName`, `unit.bedrooms`, `unit.parking[]` | no column/relation seen | blocked — A3 |
| `unit.expectedHandoverDate` / `actualHandoverDate` | none seen | blocked — A4 |
| `project.address/status/description/amenities` | none seen | blocked — A5 |
| `project.expectedHandoverDate` / `actualHandoverDate` | none seen | blocked — A4 |
| `project.completionPercentage` | none seen | blocked — A6 |
| `project.expectedCompletionDate` / `actualCompletionDate` (≠ handover) | none seen | blocked — A6 |
| `sale.soldPrice` | none seen on `tblLeads`; **must not** be `tblUnits` list price | blocked — A7 |
| `sale.registrationCost` | none seen; **not** the RegistrationReceipt attachment, **not** a percentage | blocked — A7 |
| `sale.currency` | none seen | blocked — A7 |

Until a row is mapped the action returns that member as explicit `null` (sale: member
omitted), which Ticketing surfaces as `null`. Nothing is defaulted or computed.

## Exact information needed (send these; no full repo required)

* **A1** — the EF class for `tblUnits`; business answer: which area column is "the unit's area" and its unit of measure (or the table that stores the unit).
* **A2** — the enum/lookup that labels `tblUnits.Type`.
* **A3** — property/relation names for tower/building, bedrooms, parking (`tblUnits` and any `tblParking*`/`tblTowers`).
* **A4** — columns holding planned/actual **handover** dates, on `tblUnits`, `tblLeads` or `tblProjects`.
* **A5** — `tblProjects` properties for address/location, status (+enum), customer-facing description (not internal notes), amenities table.
* **A6** — `tblProjects` (or construction-progress table) properties for completion %, planned/actual **completion** dates. Confirm they are distinct from handover.
* **A7** — the table/columns holding the customer's agreed sale price, the registration fee **amount** (and whether it is stored at all), and currency; the screen that shows them (booking/reservation/contract form). Also the type of `tblLeads.Status` (int vs int?) and whether `LeadStatus.Contract` exists.
* **A8** — confirmation that `tblLeads.BuyerID` (used by `GetCustomerDocuments`) and `tblLeadCustomers` (used by `GetBuyerByPhone`) mean the same thing. `GetUnitDetails` uses `tblLeadCustomers` (the confirmed, compiled relationship).

## Findings in the supplied CRM code (not changed by this work)

1. **`GetBuyerByPhone` does not apply Sold/Contract.** The filter is commented out; it excludes only `Cancelled`, and labels any non-`Sold` lead "Contract". So Ticketing's "eligible units" can include e.g. Hot/Reserved leads. `GetUnitDetails` is strict (Sold/Contract only), so such a unit gets `NotAvailable` details. Decide whether to restore the filter in `GetBuyerByPhone`.
2. **Phone matching is `MobileNumber.Contains(phoneNumber)`** (substring): a short or partial number can match another customer. Ticketing's verified-number check depends on this lookup; consider exact/normalised matching (the unused `NormalizePhoneNumber` exists).
3. `GetBuyerByPhone` compares the key with `!=` (not constant-time), returns `HttpStatusCodeResult(401)` (can become an HTML login redirect under Forms auth), and returns `ex.Message` to the caller. `GetCustomerDocuments` already shows the safe pattern; apply it to `GetBuyerByPhone`.
4. `GetCustomerDocuments` is `[HttpPost]` with `DenyGet`; `GetUnitDetails` is a GET, so it has its own `UnitDetailsJson` using `AllowGet`.

## Financial protection

CRM has no verification session of its own. The agreed contract is: **TigerCS releases `includeSale=true` only for a session its own OTP verification produced** (email or SMS code): owned by the calling account, unexpired, carrying the recorded proof and bound to the requested CRM customer, unit and Lead, with the spent challenge on record. An asserted `verificationMethod` never qualifies (implemented and tested in Tiger-CS-Ticketing). In CRM the flag is honoured only for a caller holding `TicketingSecretKey`; it also requires the lead binding above, and it is off unless `AppSettings["TicketingReleaseSale"] = "true"`.

## Configuration

| Where | Key | Value |
|---|---|---|
| CRM `Web.config` appSettings | `TicketingSecretKey` | existing; must equal TigerCS `Crm:SecretKey` |
| CRM `Web.config` appSettings | `TicketingReleaseSale` | new; leave absent/`false` until A7 is mapped and approved |
| TigerCS | `Crm:Provider=Http`, `Crm:BaseUrl`, `Crm:SecretKey` | unchanged |
| TigerGroupWeb | `TicketingOptions` + `TigerCS-NoRedirect` HttpClient (Program.cs, not supplied) | already used by the document routes |

## TigerGroupWeb — nothing to add

Supplied `GenesysController.UnitDetails` (`POST api/genesys/customers/unit-details`, under the
Genesys OAuth policy) → `TicketingGenesysService.UnitDetailsAsync` → `ForwardPostAsync("/api/genesys/customers/unit-details")`
already exists and is complete: the body is forwarded as raw JSON (so `unitId` and
`verificationSessionId` pass), `Cache-Control: no-store`, the service-account bearer and its single
401 refresh are reused, redirects are refused, and TigerCS's status, bytes, content-type and
`Retry-After` are relayed. No second route was added. Not verified here: that Program.cs registers
the `TigerCS-NoRedirect` client and the Genesys policy (not supplied; the document routes rely on the same).
Cosmetic: the 504/502 text mentions retrying "with the same Idempotency-Key", which a read does not use.

## What was verified, and what was not

* Verified: the insertion file compiles (C# against stubs) and its 22 tests pass — auth (missing/wrong/unconfigured key → JSON 401/503, no redirect flags, AllowGet), input validation, own Sold and Contract unit, another customer's unit, nonexistent unit (identical answer), another customer's lead, lead of another unit, Hot and Cancelled leads, non-Buyer relationship, nulls not invented, sale never released without the switch / for another customer, DB failure → JSON 500 without the exception text. Mutation check: removing the lead predicate or the Sold/Contract filter makes tests fail. Ticketing parses the action's exact payload (new gateway test); Ticketing suite for these areas passes (95 tests).
* **Not verified:** compilation against the real CRM project and model types; EF6 SQL translation; IIS/Forms-auth behaviour; any real data; the TigerGroupWeb build; any request through `https://tigergroup.ae/api/genesys/customers/unit-details`. **End-to-end is not achieved.**

## Real integration test procedure (UAT)

1. Apply the file, add `TicketingSecretKey` (same as TigerCS), build, deploy to CRM UAT.
2. `curl -i -H "X-SECRET-KEY: <key>" "https://<crm-uat>/TicketingSystem/GetUnitDetails?customerId=<A>&unitId=<A's unit>&leadId=<A's Sold/Contract lead>"` → `200`, JSON, `found:true`.
3. Same without header and with a wrong key → `401` JSON (not a 302/HTML).
4. Buyer B's unit with A's customer id; A's unit with B's lead; a Hot/Reserved lead; a nonexistent unit → `404` `found:false`, identical bodies.
5. Fill A1–A7 mappings, rebuild, compare each returned value with the CRM unit, project and booking screens (zero and missing cases included).
6. With `TicketingReleaseSale=true` and `includeSale=true`: sale equals the booking screen for **that lead**; for a customer with two units each returns its own sale.
7. Through TigerGroupWeb: lookup → unit selection → OTP verification → `POST https://tigergroup.ae/api/genesys/customers/unit-details` with `unitId` and `verificationSessionId`; confirm sale appears only with the session, `VerificationFailed` with an expired one, and `403 UNIT_NOT_ELIGIBLE` for another customer's unit.
