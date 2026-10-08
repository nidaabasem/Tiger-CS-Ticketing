# Audit findings - Genesys integration, customer identity, data actions

> Baseline: working tree `31878f4` (main `a1cba71` + CRM documents / email-OTP commits). Read-only code audit; **nothing was executed against Genesys, TigerGroupWeb, CRM, PACT or EDSM**, and no tests were run for this audit.
> "Confirmed" = demonstrated by reading the cited lines. Items that depend on Genesys/TigerGroupWeb runtime behaviour are labelled **(ext, unverified)** and are not counted as confirmed.
> Related: [05](05-customer-identity-and-reconciliation.md), [08](08-genesys-integration-guide.md), [09](09-data-action-inventory.md).

## Summary

| ID | Severity | Title | Status |
|---|---|---|---|
| F-01 | Medium | `POST /api/tickets` accepts CRM unit id `0` / any external unit id; wizard comment claims revalidation | Confirmed |
| F-02 | High | Data action 08 success template renders invalid JSON | Confirmed; FIXED in the data-action file, guarded by `GenesysDataActionContractTests` |
| F-03 | Medium | Data action 12: unquoted string substitutions and `0` defaults for "not recorded" | 0-defaults confirmed and FIXED (sentinels -999 / -1 / -1); quoting was already correct |
| F-04 | Medium | Screen Pop `customerPhone` not normalised | Confirmed |
| F-05 | Low | PATCH is not "validated before anything is written" | Confirmed |
| F-06 | Medium | Transcript `sender` contract text says `Agent`; code rejects it | Confirmed |
| F-07 | Medium | Malformed CRM buyer payload -> unhandled `NullReferenceException` (500) in lookups | Confirmed (by reading) |
| F-08 | Medium | Tasleeh has only a fixture gateway, defaults to it, and has no startup guard | Confirmed |
| F-09 | Medium | OTP endpoints reject `tel:` and mangle national numbers | Confirmed |
| F-10 | Low | Genesys lookup does not reconcile CRM+PACT; ordering differs from wizard | Confirmed (design gap) |
| F-11 | Low | Genesys flat screen pop lists expired PACT contract units unflagged | Confirmed |
| F-12 | Low | PACT `unitCode`/`unitNumber` used as unit id; EDSM `unitID` unchecked narrowing / no `>0` | Confirmed |
| F-13 | Low | Shipped `Genesys:Enabled=true` vs documented default `false`; background jobs off | Confirmed (config) |
| F-14 | Medium | Proxy change document omits the OTP/verification routes (and agent-context/screen-pop) | Confirmed (doc); runtime ext |
| F-15 | Low | Stale/incorrect wording in data action 11 and API remarks | Confirmed |

---

## F-01 CRM unit id `0` (and any external unit id) accepted at ticket creation - Medium

* **Where:** `TigerCS.Application/Modules/Ticketing/Services/TicketCreationAppService.cs:108-116` (only "all four present or none"), `:236-240` -> `Ticket.CreateVerifiedFromCrmBuyer` (`Ticket.cs:333-342`) stores them; `ExternalUnitId` stored untouched (`:241-250`, `Ticket.cs:400`). Web side: `NewTicket.cshtml.cs:843-846` (`int.Parse` on posted text) while the remarks at `NewTicket.cshtml.cs:92-94` and `:1229` state "POST /api/tickets re-validates everything server-side".
* **Scenario:** a crafted or stale request `{CrmBuyerCustomerId:1,CrmBuyerLeadId:1,CrmBuyerUnitId:0,CrmBuyerProjectId:1}` creates a "CRM-verified" ticket (`VerificationStatus=Verified`, SLA running) against a non-existent unit; the Customers directory then lists a unit with `CrmBuyerUnitId = 0` (`CustomerDirectoryRepository.cs:353`). A malformed packed value gives `FormatException` (HTTP 500) in the wizard. The lookup layers already drop unit 0 (`CrmBuyerLookupAppService.cs:119-120`, `PactCustomerHttpGateway.cs:241`), so this is the only unprotected persistence path.
* **Minimal fix:** after the all-or-none check add `crmBuyerIds.All(id => id > 0)` -> `CrmBuyerReferenceMismatch` (422); reject `ExternalUnitId` that is blank/zero-only; optionally re-run `CrmBuyerLookupAppService` for the intake phone and compare. Replace `int.Parse` with `TryParse`. Correct the two misleading comments.

## F-02 Data action `08-collections-payment-summary.json` produces invalid JSON - High

* **Where:** `docs/Genesys/data-actions/08-collections-payment-summary.json:64` - the last eight members are written `\\\"nextPaymentStatus\\\": ...` (backslash + quote survive JSON decoding), so the rendered template contains `\"nextPaymentStatus\":`.
* **Scenario:** every Collections payment summary call that succeeds in TigerCS still yields an unparsable body in Genesys (success template is applied to 200 only), the flow takes its failure path; next-payment fields never reach the bot. TigerCS output (`CollectionsPaymentSummaryResponseDto.NextPayment`) is correct.
* **Minimal fix:** replace the eight `\\\"` pairs by `\"` (file is otherwise consistent with the DTO); re-import the action.

## F-03 Data action `12-customer-unit-details.json` - Medium

* **Where:** `:15` `"customerReference": ${input.customerReference}, "phoneNumber": ${input.phoneNumber}` (no quotes, no `$esc.jsonString`; all other files quote). `:46-48` `floor`, `bedrooms`, `areaValue` default to `0`.
* **Scenario A (ext, unverified):** if Genesys substitutes raw text, the body is `{"customerReference": crm:1001, ...}` -> 400 at proxy/TigerCS. **Scenario B (confirmed):** CRM did not record bedrooms/area/floor -> TigerCS returns `null` (`GenesysCustomerUnitDetailsAppService.cs:173-181`, doc: "never defaulted"), but the action substitutes `0`; a bot may state "0 bedrooms" or "floor 0".
* **Minimal fix:** `\"$esc.jsonString(${input.customerReference})\"` / `\"$esc.jsonString(${input.phoneNumber})\"`; use sentinel defaults the flow understands (e.g. `-1`) or drop numeric defaults and branch on `detailsStatus`.

## F-04 Screen Pop `customerPhone` is not normalised - Medium

* **Where:** `GenesysScreenPopAppService.cs:244` builds `/Customers/Lookup?phoneNumber=<raw trimmed>`; every other Genesys entry point calls `CustomerPhoneNumber.FromTelephonyAddress` (`GenesysCustomerLookupAppService.cs:69`, `GenesysInquiryIngestionAppService.cs:139`). `CustomerLookup.cshtml.cs:81` and `CustomerHistoryController.cs:59` pass it on unchanged.
* **Scenario:** Genesys passes `Call.Ani` = `tel:+971501234567`; CRM is queried with `tel:+971...`, PACT with `tel:971...` -> "no match" on the agent's screen although the number exists.
* **Minimal fix:** `var searched = CustomerPhoneNumber.FromTelephonyAddress(request.CustomerPhone)`; fall through to `/Tickets` when null.

## F-05 PATCH is not "refused before anything is written" - Low

* **Where:** `GenesysTicketUpdateAppService.cs:123-184` (handoff request/cancel/assign committed), `:189-215` (routing), then `:222-236` (`ended` + transcript validated inside `EndAsync`, `GenesysConversationEndAppService.cs:103-107`). Claims: class remarks `:45-50` and `GenesysController.cs:194-196`.
* **Scenario:** `{handoff:{required:true}, ended:{transcript:[{sender:"Agent",...}]}}` -> 400 `genesys-invalid-transcript` ("Nothing was stored") but the handoff work item and routing are already committed. Re-sending is idempotent, so impact is limited to misleading text.
* **Minimal fix:** validate the transcript (call `TryNormalizeTranscript`) before the handoff block, or correct the wording.

## F-06 Transcript `sender` contract text is wrong - Medium

* **Where:** `GenesysContracts.cs:82` and `:88` say `"Customer", "Agent" or "System"`; accepted values are `Customer, HumanAgent, VirtualAgent, System` (`InteractionMessageSender`, `TicketInteractionMessage.cs:4-`; parse at `GenesysConversationEndAppService.cs:434`). No shipped data action sends a transcript, so the XML doc/OpenAPI is the only guidance.
* **Scenario:** an integrator following the documented vocabulary sends `Agent` -> whole PATCH 400, end/transcript lost (and see F-05).
* **Minimal fix:** fix the XML doc (and `docs/architecture/Genesys-API-Contracts.md` if it repeats it); optionally accept `Agent` as an alias of `HumanAgent`.

## F-07 Malformed CRM buyer payload -> 500 - Medium

* **Where:** `CrmBuyerHttpGateway.cs:138` + `MapBuyer :142-162` dereference `buyer.Customer` and `buyer.Units`; the wire records (`CrmBuyerLookupHttpContracts.cs:14-17`) are non-nullable but System.Text.Json leaves a missing member `null`. Only deserialisation exceptions are caught (`:120`). `CustomerSearchAppService.cs:35` awaits `Task.WhenAll` without a catch; the Genesys lookup (`GenesysCustomerLookupAppService.cs:76`) does not catch either (ingestion does, `:406`).
* **Scenario:** CRM returns `{"success":true,"found":true,"buyers":[{"customer":null,...}]}` (or omits `units`) -> `NullReferenceException` -> `GET /api/genesys/customers/lookup` and `/api/crm/buyers` return 500, contradicting "never throws for an expected CRM response" (`CrmBuyerHttpGateway.cs:19-24`). Same family: CRM `customerId`/`projectId` default to `0` when omitted and are not validated.
* **Minimal fix:** in `ParseSuccessResponseAsync`, treat `buyer.Customer is null || buyer.Customer.CustomerId <= 0 || buyer.Units is null` as `InvalidResponse` (or skip that buyer).

## F-08 Tasleeh fixture is the only provider and is unguarded - Medium

* **Where:** `IntegrationsServiceCollectionExtensions.cs:274-290` (only `"Mock"`, any other value throws `NotSupportedException`), `TasleehGatewayOptions.cs:14` default `"Mock"`, no `Tasleeh` key in `TigerCS.Api/appsettings.json`, and `Program.cs:120-190` guards CRM, Collections and e-mail fixtures but **not** Tasleeh.
* **Scenario:** production serves `MockTasleehGateway` (a fake "Omar Khalid", `TSL-CUST-4001`, for `+971500000003`; `OUTAGE` in the number simulates failure) as a real verification source; if someone sets `Tasleeh:Provider=Http` the `ITasleehGateway` resolution throws and, because `CustomerLookupAppService` depends on it, customer lookup **and Genesys ticket ingestion** fail to resolve (500).
* **Minimal fix:** startup guard like `CrmGatewaySafety`; add a `Disabled`/`Unavailable` provider that returns `Failed`/empty, and make it the non-dev default.

## F-09 OTP endpoints reject telephony addresses and mangle national numbers - Medium

* **Where:** `CustomerOtpAppService.cs:521-535` (`LooksLikeNumber` then `"+" + digits`), used by lookup/send. Data actions `13`/`14` describe the input as `Call.Ani`. Document copy re-resolves with `"+" + Normalize(contact.ContactChannel)` (`CrmDocumentCopyAppService.cs`, step 4).
* **Scenario:** `tel:+971501234567` -> 400 `INVALID_REQUEST` (letters fail `LooksLikeNumber`); `0501234567` -> `+0501234567` -> CRM `NotFound`; `00971...` -> `+00971...`. All other Genesys routes accept these forms.
* **Minimal fix:** use `CustomerPhoneNumber.FromTelephonyAddress` in `TryNormalizePhone` (keep the 7-15 digit check).

## F-10 Genesys lookup does not reconcile CRM and PACT - Low

* **Where:** `GenesysCustomerLookupAppService.cs:76-112, 124-165` never calls `CustomerIdentityLinker.FindPactMatch` (used by wizard, lookup page, history, collections pre-ticket). Ordering is CRM-first here vs PACT-first in the wizard (`NewTicket.cshtml.cs:747-752`).
* **Scenario:** a person known to both counts as `matchedCustomerCount = 2` ("agent must confirm"); data action 08 documents "only when `matchedCustomerCount` is 1", so such customers can never use the Collections actions.
* **Minimal fix:** reuse the linker to merge a verified pair into one screen-pop customer (keep both ids), or add `pactCustomerKey` to the response.

## F-11 Expired PACT units shown unflagged in the screen pop - Low

* **Where:** `GenesysCustomerLookupAppService.cs:139-146` builds `units`/`unitsText` from all PACT units; `IsContractExpired` is dropped. Nested `externalSources` keeps it.
* **Scenario:** a bot/agent reads a long-expired contract as one of the customer's current units.
* **Minimal fix:** skip `IsContractExpired` units in the flat projection (or suffix "(expired)").

## F-12 Unit-id fallbacks and narrowing - Low

* **Where:** `PactCustomerHttpGateway.cs:251-254` (`unitID` absent -> `unitCode` -> `unitNumber`; a value `"0"` is accepted and a display code is stored as `ExternalUnitId` and on tickets); `EdsmCollectionsHttpGateway.cs:260` `(int?)Long(row,"unitID")` unchecked cast, no `>0` test (unit `0` or an overflowed value is shown in `nextPayment...units[].unitId`).
* **Scenario:** mixed id spaces under `ExternalUnitId`; wrong/zero unit ids in EDSM next-payment unit breakdown.
* **Minimal fix:** reject numeric-zero fallbacks (`FirstNonBlank` result `"0"`), and map EDSM `unitID <= 0` or `> int.MaxValue` to `null`.

## F-13 Shipped configuration vs documentation - Low

* `TigerCS.Api/appsettings.json:139-140` `"Genesys": {"Enabled": true}` while `GenesysOptions.cs` documents the integration as dark by default; `BackgroundJobs.Enabled=false` means the inactivity-closure job, reminders and outbox dispatch do not run unless enabled per environment; `CrmDocuments.Enabled=false` and `EmailNotifications.Enabled=false` make OTP/document routes answer 503 / `OTP_DELIVERY_FAILED`. Not a code defect; verify per environment.

## F-14 Proxy change specification does not cover the verification routes - Medium

* `docs/Genesys/TigerGroupWeb-Proxy-Change.md` sec. 1 lists 11 routes; it omits `POST /api/genesys/verification/{buyer-lookup,otp/send,otp/resend,otp/verify}`, `POST /api/genesys/agent-context` and `POST /api/genesys/screen-pop`. Data actions 13-16 call `{TG}/api/genesys/verification/*`. Rule 4 (forward bodies unchanged) and rule 6 (pass `Retry-After` and `problem+json`) also apply to them. Whether TigerGroupWeb forwards them is **unknown (ext)**.
* **Fix:** extend the proxy document (and the TigerGroupWeb change) with these routes; pass `Retry-After`.

## F-15 Wording - Low

* Data action 11 `verificationSessionId` description ("Otp or AuthenticatedDigitalUser") - only OTP-proven sessions work now (`CrmDocumentCopyAppService.cs` step 3); `GenesysDocumentsController` remark still says the session comes from `POST /api/verification-sessions` while that endpoint now refuses `Otp` (`VerificationSessionsController.cs:90-95`).

---

## Checked and found correct (no action)

* CRM unit `0` filtered at the single choke point `CrmBuyerLookupAppService.IsValidBuyerUnit` (also covers a missing `unitId`, which deserialises to `0`); PACT `unitID <= 0` dropped; Genesys unit-details rejects `unitId <= 0`; OTP and documents derive units from the filtered buyer; campaigns flag `MissingUnitIdentity`.
* CRM ambiguity (two customers) never auto-selects anywhere (409 / `AmbiguousMatch` / `CUSTOMER_AMBIGUOUS`).
* PACT-only person with no agent-verified ticket -> `404 AccountNotFound` on Genesys Collections is documented, deliberate and tested.
* `Otp` can no longer be asserted: `POST /api/verification-sessions` returns 400 `otp-requires-challenge`; documents require a server-side proof.
* Ticket creation idempotency by `conversationId` (unique index + `DuplicateWriteException` recovery), screen-pop token hashing / one-time use / re-check at redeem.
