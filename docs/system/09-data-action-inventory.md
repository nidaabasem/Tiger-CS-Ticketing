# 9. Genesys Data Action inventory (`docs/Genesys/data-actions/*.json`)

> Baseline: working tree `31878f4`; files as named after the coordinator's renumbering (`13-buyer-lookup`, `14-otp-send`, `15-otp-resend`, `16-otp-verify`).
> Each action was compared with the **controller route, DTO, headers and status codes in code**. TigerGroupWeb and Genesys Cloud runtime behaviour (Velocity rendering, JSONPath null handling) is **`[ext]` unverified** - nothing here was executed against Genesys.
> `{TG}` = `https://tigergroup.ae` (public proxy `[ext]`), `{TCS}` = TigerCS.Api base URL, `$T` = bearer token.

## 9.1 Import instructions (PROPOSED - Genesys admin UI wording not verified)

1. In Genesys Cloud: *Admin > Integrations*, add a **Web Services Data Actions** integration, credential type **User Defined (OAuth)** with fields `clientId`, `clientSecret`. Activate it.
2. Open the auto-created **Custom Auth** action and paste `config.request` from `00-custom-auth-request-config.json`; leave `config.response` as Genesys created it (the file says its contracts cannot be edited).
3. *Admin > Integrations > Actions > Import*: import `01` ... `16` one by one into that integration. Files declare `"integrationType":"custom-rest-actions"`, `"category":"TigerCS"`, `"secure": false` (some omit `secure`).
4. Publish each action; assign to the Architect flows with *Call Data Action*. Set each flow's Data Action timeout to **>= 30 s for the Collections actions** (TigerGroupWeb answers within the flow timeout - 2 s `[ext]`); pass `flowTimeoutSeconds` to actions `08`/`09`.
5. Store `ticketId` in participant data `TigerCsTicketId` (action `02` output) and pass it to actions `03-07, 10`.
6. Before go-live verify against TigerCS directly with the sample calls below; then through `{TG}`. The TigerGroupWeb forwarding for `12`, `11`, `13-16`, the collections routes and the PATCH body is **pending** per [TigerGroupWeb-Proxy-Change.md](../Genesys/TigerGroupWeb-Proxy-Change.md) (it does not list the `/api/genesys/verification/*` routes at all - F-14).

Common assumptions: header `Authorization: ${authResponse.token_type} ${authResponse.access_token}` (the token is the **Genesys** token; the proxy swaps it `[ext]`); responses with 4xx/5xx go to the action's failure path (the shipped files define success schemas only).

## 9.2 Summary

| # | File | Method + URL in file | Real TigerCS route (code) | Result |
|---|---|---|---|---|
| 00 | custom-auth-request-config | `POST {TG}/api/genesys/oauth/token` | **not in TigerCS** (proxy `[ext]`) | cannot verify |
| 01 | customer-lookup | `GET /api/genesys/customers/lookup?phoneNumber=` | `GenesysController.cs:342` | matches |
| 02 | create-reuse-ticket | `POST /api/genesys/tickets` | `:100` | matches; limited field set |
| 03 | update-routing | `PATCH /api/genesys/tickets/${ticketId}` | `:207` | matches |
| 04 | end-conversation | same PATCH | `:207` | matches; **no transcript** |
| 05 | request-human-agent | same PATCH | `:207` | matches; live transfer not expressible |
| 06 | cancel-human-request | same PATCH | `:207` | matches |
| 07 | customer-confirmed-resolved | same PATCH | `:207` | matches |
| 08 | collections-payment-summary | `GET .../collections/customers/by-key/{key}/payment-summary?includeTransactions=false` | `GenesysCollectionsController.cs:107` | success template fixed (was F-02); validated by `GenesysDataActionContractTests` |
| 09 | collections-payment-transactions | `GET .../by-key/{key}/payment-transactions?companyId&type` | `:135` | matches |
| 10 | awaiting-customer-reply | PATCH | `:207` | matches; null-field defaults |
| 11 | send-document-copy | `POST /api/genesys/documents/send-copy` + `Idempotency-Key` | `GenesysDocumentsController.cs:69` | matches; stale description |
| 12 | customer-unit-details | `POST /api/genesys/customers/unit-details` | `GenesysController.cs:397` | quoting already correct; not-recorded defaults changed from `0` to sentinels `-999` / `-1` / `-1` (F-03 closed) |
| 13 | buyer-lookup | `POST /api/genesys/verification/buyer-lookup` | `GenesysVerificationController.cs` `buyer-lookup` | matches; `tel:` rejected (F-09) |
| 14 | otp-send | `POST .../verification/otp/send` | `otp/send` | same |
| 15 | otp-resend | `POST .../verification/otp/resend` | `otp/resend` | matches |
| 16 | otp-verify | `POST .../verification/otp/verify` | `otp/verify` | matches |

Not covered by any action (routes exist in code): `agent-context`, `screen-pop`, `GET/POST` collections reminders/candidates/outcomes/outstanding/payments, `POST /api/verification-sessions`, pending-interaction work list, transcript delivery.

## 9.3 Per-action detail, mismatches and sample calls

### 00 Custom Auth
* External. Posts `application/x-www-form-urlencoded` `grant_type=client_credentials&scope=ticketing.genesys&client_id=...&client_secret=...`. TigerCS has no such endpoint; TigerCS only accepts JWTs from `POST /api/auth/login`. **Mismatch risk: none inside this repo; entirely `[ext]`.**
* Sample (proxy `[ext]`): `curl -X POST {TG}/api/genesys/oauth/token -d 'grant_type=client_credentials&scope=ticketing.genesys&client_id=ID&client_secret=SECRET'`.
* Sample TigerCS service login: `curl -X POST {TCS}/api/auth/login -H 'Content-Type: application/json' -d '{"username":"svc","password":"..."}'`.

### 01 Customer Lookup
* Request: GET, `phoneNumber` URL-escaped, `Accept` - matches. `requestTemplate: ${input.rawRequest}` on a GET is meaningless but harmless.
* Response mapping vs `GenesysCustomerLookupResultDto`: `$.found, $.phoneNumber, $.crmStatus, $.openTicketCount, $.screenPop.{customerName,customerEmail,verificationSource,externalCustomerId,matchedCustomerCount,units,unitsText,recentTicketNumbers,openTicketNumbers,recentTicketsText}` all exist (camelCase). Success 200 only.
* Gaps: output omits the CRM `customerId`/PACT tenant distinction (use `verificationSource`), omits `crmBuyers[]`, `externalSources[]`, `tickets[]`; there is no `customerKey`, so the flow must assemble `ext:Pact:{externalCustomerId}`. `units`/`unitsText` include expired PACT contracts unflagged (F-11).
* Sample: `curl -H "Authorization: Bearer $T" "{TCS}/api/genesys/customers/lookup?phoneNumber=tel%3A%2B971501234567"` -> `200 {"phoneNumber":"+971501234567","found":true,"crmStatus":"Found",...,"screenPop":{...}}`.

### 02 Create or Reuse Ticket
* Matches body names exactly (`conversationId, channel, customerPhone, customerName, customerEmail, departmentCode, queueId, queueName, agentId, agentName, calledNumber, direction, subject, towerName, unitNumber, startedAtUtc`). Blank values become absent (`GenesysContracts.cs:395`). `startedAtUtc` rendered as `null` when blank.
* Success codes: **201 and 200** both 2xx (`outcome` distinguishes). Required by code: `conversationId` (400 `genesys-conversation-id-required` if blank) and a recognised `channel` (400 validation). Not exposed by the action: `departmentId, interactionId, participantId, communicationId`.
* Output `ticketId` declared `string`; server sends a number; the template re-quotes it - fine.
* Sample: `curl -X POST {TCS}/api/genesys/tickets -H "Authorization: Bearer $T" -H 'Content-Type: application/json' -d '{"conversationId":"c-1","channel":"Phone","customerPhone":"tel:+971501234567","queueId":"Q1"}'` -> `201 {"outcome":"TicketCreated","conversationId":"c-1","ticketId":42,"ticketNumber":"..."}`; repeat -> `200 ... "AlreadyIngested"`.

### 03 / 04 / 05 / 06 / 07 / 10 PATCH actions
Common: URL `/api/genesys/tickets/$esc.url(${input.ticketId})` (escaped so a flow value cannot leave the path) - `{ticketId:long}`; an empty/non-numeric `ticketId` gives 404/405 before TigerCS logic. Body part names (`routing`, `ended`, `handoff`, `customerConfirmation`, `awaitingCustomerReply`) match `GenesysTicketUpdateRequest`. Outputs `outcome, ticketId, ticketNumber, ticketStatus, conversationEnded, handoffStatus, awaitingCustomerReply, inactivityDeadlineUtc, awaitingCustomerReplyNote` exist.
* **03**: always sends a `routing` object (blank fields ignored). OK.
* **04**: sends only `endedAtUtc`, `endReason`; **no `transcript`** although the API stores one. A conversation ended by this action has `transcriptMessageCount 0`; a bot-only conversation then auto-raises a human-handoff (`AiConnectionLost`).
* **05**: hard-codes `required:true, agentAvailable:false`; `trigger`, `mode`, `reason` are free input and must be one of the documented words (else 400 `genesys-invalid-handoff-trigger|mode`). `workItemId`, `assignedAgentId`, live transfer (`agentAvailable:true`) are not expressible.
* **06**: `reason` required by both action and code (400 `genesys-handoff-reason-required`). "Nothing outstanding" is success.
* **07**: `confirmedResolved:true` fixed; matches 400 rule. Record-only.
* **10**: output field `note` is mapped from `awaitingCustomerReplyNote`; `translationMapDefaults` is `{}`, so for `null` values (`note`, `inactivityDeadlineUtc`) rendering of `${note}` depends on Genesys' null handling `[ext]` - give them defaults or quote in a revision.
* Samples: 
  `curl -X PATCH {TCS}/api/genesys/tickets/42 -H "Authorization: Bearer $T" -H 'Content-Type: application/json' -d '{"conversationId":"c-1","ended":{"endReason":"CustomerDisconnect"}}'` -> `200 {"outcome":"Applied",...,"conversationEnded":true}`;
  handoff: `-d '{"conversationId":"c-1","handoff":{"required":true,"agentAvailable":false,"trigger":"CustomerRequestedHuman"}}'` -> `handoffStatus:"WaitingForAgent"`;
  timer: `-d '{"conversationId":"c-1","awaitingCustomerReply":true}'` -> `inactivityDeadlineUtc` = now + 5 min.

### 08 Collections Payment Summary
* URL/method/headers match; `customerKey` is `$esc.url`-encoded (`ext%3APact%3A3001`), ASP.NET decodes it. `includeTransactions=false` matches the query binding. Header `X-Genesys-Flow-Timeout-Seconds` is for the proxy `[ext]`; TigerCS reads `X-Collections-Deadline-Seconds` (`CollectionsEdsmOptions.cs`), so calling TigerCS directly with this action gives the default 22 s deadline. When `flowTimeoutSeconds` is empty the header is sent empty.
* Response JSONPaths vs `CollectionsPaymentSummaryResponseDto`: `mappingStatus, completeness, incompleteReasons, currency, maxSourceDelayMinutes, companies[].{companyId,companyName,businessModel,status,fields[].{key,raw}}, nextPayment.{status,reasons,detail,isComplete,searchedThrough,earliest.{dueDate,amountRaw,companyId}}` all exist.
* **FIXED (was F-02) - historical description:** `config.response.successTemplate` (file line 64) contains `\\\"nextPaymentStatus\\\"` ... i.e. literal backslash-quote for the last eight keys, so the rendered body is not valid JSON; the flow receives a parse error exactly when all else succeeded.
* Defaults: `nextPaymentStatus` defaults to `"Unavailable"`, `completeness` `"NoFigures"` - safe wording; `nextPaymentCompanyId` defaults to `0`.
* Contract note: `customerKey` description says use it only when `verificationSource=Pact` and `matchedCustomerCount=1`; because the Genesys lookup does not reconcile CRM+PACT (F-10) a person in both systems never qualifies. A PACT person with no agent-verified ticket returns `404 AccountNotFound` (by design).
* Sample: `curl -H "Authorization: Bearer $T" -H 'X-Collections-Deadline-Seconds: 20' "{TCS}/api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-summary?includeTransactions=false"`. Errors `{"code":"AccountNotFound"|"FinanceUnavailable"|"Forbidden"|"InvalidRequest",...}`.

### 09 Collections Payment Transactions
* Query `companyId` (int) and `type` (`Paid|Due|Outstanding`; `All`/`4` -> 400 `InvalidRequest`) match. Mappings `companyId, companyName, businessModel, transactionType, caveat, currency, items[*].{formattedRaw,dateRaw,chequeNumber,paymentType}` exist in `CollectionsPaymentTransactionsResponseDto`/`CollectionsEdsmTransactionDto`. Errors incl. `422 CustomerNotMapped`, `404 AccountNotFound`.
* Sample: `curl -H "Authorization: Bearer $T" "{TCS}/api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-transactions?companyId=4&type=Paid"`.

### 11 Send Document Copy
* Route, header `Idempotency-Key`, body names (`verificationSessionId, documentType, crmUnitId, recordId, crmLeadId, deliveryChannel`) match `CrmDocumentCopyRequestDto` (`crmLeadId` uses a lenient int converter that accepts `""`, `CrmDocumentCopyDtos.cs`). Output `status, code, message, recordId, maskedDestination, duplicate, choices, choiceKind` exist; `deliveryRequestId`, `retryable`, `documentType` are not mapped.
* **Stale description:** `verificationSessionId` is described as "Otp or AuthenticatedDigitalUser"; the code additionally requires a server-side OTP proof (`ProofChallengeId`, customer and lead) - only a session from action `16` qualifies (`CrmDocumentCopyAppService.cs` step 3).
* `verificationSessionId` is `Guid?`: an empty or malformed string fails model binding with an *uncoded* 400 validation problem, not `INVALID_REQUEST`.
* `successTemplate` is rendered for 200 only; **202 `Queued`** has the same body (`status:"Queued"`).
* Sample: `curl -X POST {TCS}/api/genesys/documents/send-copy -H "Authorization: Bearer $T" -H 'Idempotency-Key: c-1:Contract:1' -H 'Content-Type: application/json' -d '{"verificationSessionId":"<guid>","documentType":"Contract"}'` -> `200 {"status":"Sent","maskedDestination":"a***@g***.com","duplicate":false,...}` or `200 {"status":"SelectionRequired","choiceKind":"Document","choices":[{"recordId":"..","label":".."}]}`.

### 12 Customer Unit Details
* Route/method/body names match `GenesysCustomerUnitDetailsRequest`; output paths match `GenesysCustomerUnitDetailsResponse` (`mode, customerReference, eligibleUnits, unit.*, project.*, handoverDateSource, detailsStatus`).
* **FIXED (was F-03) - historical description:** `requestTemplate` renders `"customerReference": ${input.customerReference}` and `"phoneNumber": ${input.phoneNumber}` **without quotes** (every other file quotes strings with `\"$esc.jsonString(...)\"`). If Genesys substitutes the raw string (as it does elsewhere in these files) the body is invalid JSON (`crm:123`) -> 400 from the proxy/TigerCS. `[ext]` Genesys rendering unverified, but inconsistent with the other 15 files and with the documented request.
* **Defaults contradict the API contract:** `floor`, `bedrooms`, `areaValue` default to `0` (file lines 46-48) whereas TigerCS returns `null` for "not recorded" and the API doc forbids defaulting; a bot can read "0 bedrooms / ground floor".
* `unitId` is `integer`: `#if($input.unitId)` omits the field when unset; if a flow passes `""` the number binder returns 400 (uncoded). `0` is rejected: 400 `unitId must be a positive CRM unit id`.
* Only a CRM buyer works: `customerReference` must be a CRM customer id (lookup `externalCustomerId` when `verificationSource=Crm`); a PACT tenant id gives 403 `CUSTOMER_NOT_VERIFIED`.
* Errors (`code` extension): 403 `CUSTOMER_NOT_VERIFIED`, 403 `UNIT_NOT_ELIGIBLE`, 409 `CUSTOMER_AMBIGUOUS`, 502 `CRM_UNAVAILABLE`, 503.
* Sample: `curl -X POST {TCS}/api/genesys/customers/unit-details -H "Authorization: Bearer $T" -H 'Content-Type: application/json' -d '{"customerReference":"crm:1001","phoneNumber":"tel:+971501234567","unitId":41230}'`.

### 13 Buyer Lookup, 14 Send Verification Code, 15 Resend, 16 Verify
* Routes, body names (`phoneNumber`, `crmUnitId`, `challengeId`, `code`) and the output fields (`status, code, message, challengeId, maskedDestination, units`; `session.verificationSessionId`, `session.snapshotUnitNumber`) match `CustomerOtpResult` / `VerificationSessionResponseDto`. `challengeId` is a `Guid` - blank gives an uncoded 400.
* **Mismatch (F-09):** the descriptions say `phoneNumber` is `Call.Ani`, but the controller accepts only digits and separators (`CustomerPhoneNumber.LooksLikeNumber`) - `tel:+971...` returns **400 `INVALID_REQUEST`**; national `0501234567` becomes `+0501234567`. Strip `tel:` in Architect or fix server-side.
* Error bodies (`otp/verify` 400 `OTP_INVALID`, `attemptsRemaining`; 429 with `Retry-After`) are not part of the success schema; the flow must use the failure path to read `code`, `attemptsRemaining`, `resendAvailableAtUtc`.
* `units` is `[{crmUnitId, leadId, unitNumber, projectName}]` - `crmUnitId` is the CRM **unitId as text** (always >0, unit 0 is filtered).
* Samples:
  `curl -X POST {TCS}/api/genesys/verification/buyer-lookup -H "Authorization: Bearer $T" -H 'Content-Type: application/json' -d '{"phoneNumber":"+971501234567"}'`;
  `.../otp/send -d '{"phoneNumber":"+971501234567","crmUnitId":"41230"}'` -> `{"status":"CodeSent","challengeId":"<guid>","maskedDestination":"a***@g***.com","expiresAtUtc":"..."}`;
  `.../otp/resend -d '{"challengeId":"<guid>"}'`;
  `.../otp/verify -d '{"challengeId":"<guid>","code":"123456"}'` -> `{"status":"Verified","session":{"verificationSessionId":"<guid>",...}}`.

## 9.4 Journey -> action coverage

| Journey | Action(s) | Missing |
|---|---|---|
| auth | 00 | - |
| lookup | 01 | customer key |
| create/reuse | 02 | `departmentId` |
| routing, end, handoff, cancel, confirm, timer | 03, 04, 05, 06, 07, 10 | transcript, live transfer, `assignedAgentId` |
| unit details | 12 | - |
| OTP + documents | 13-16, 11 | - |
| collections summary / transactions | 08, 09 | reminders, candidates, outcomes, outstanding, payments |
| agent context / screen pop | none | both |

## Changes after the contract-test pass
* Actions 03–07 and 10: `ticketId` in the URL is now escaped with `$esc.url`.
* Action 11: `choiceKind` default renders empty (was the literal `""`).
* Action 12 (unit details): numeric fields that TigerCS returns as null are exposed to Architect as sentinels — floor `-999`, bedrooms `-1`, area `-1` — because a Genesys integer cannot be null and `0` is a valid floor. Flows must test `detailsStatus` / the sentinel before speaking a value.
* Actions 13–16 (buyer lookup, OTP send/resend/verify): numbering fixed; references updated.
* Every file is rendered by `GenesysDataActionContractTests` (see [16](16-uat-checklist.md)); this proves the templates against a simulator, not against Genesys Cloud itself.
