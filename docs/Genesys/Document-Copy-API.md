# Document copy API — chatbot asks CRM to send a CRM buyer their own document

> ## ⚠ PENDING REAL UAT VERIFICATION — and DISABLED in Production
> Phase 1 (CRM **buyers** requesting **their own** documents) is implemented and passes 3 100+ automated tests, including the
> whole flow through the real host against a stub of Tiger CRM. **It has not been run against real UAT CRM, the real file routes,
> real SMTP, or the public Genesys route.** It is not complete until a real customer completes email-OTP verification, selects a
> unit and a document, and receives exactly one email through the public route (exit criteria: [`CRM-Required-Contracts.md` §8](CRM-Required-Contracts.md#8-status-and-exit-criteria)).
> It ships `CrmDocuments:Enabled=false`, and **a Production host refuses to start with it enabled** unless
> `CrmDocuments:AllowInProduction=true` is also set — a decision to take only after UAT passes.

Scope: a CRM **Buyer** (found by `GetBuyerByPhone`) asking for a Contract, Reservation Form, Unit Layout or Registration Receipt
**of the unit they verified for**. Tenants, representatives, and "Owner" semantics are out of scope (§Not in this phase).

## The flow

```
 phone ──► POST /api/genesys/verification/buyer-lookup     CRM GetBuyerByPhone → units (labels only) + masked email
 unit  ──► POST /api/genesys/verification/otp/send         code emailed to the address CRM returned; challenge bound to
                                                           (integration account, CRM customer, unit/lead)
 code  ──► POST /api/genesys/verification/otp/verify       server checks the code → OTP-verified session
 doc   ──► POST /api/genesys/documents/send-copy           GetCustomerDocuments → choices → file download → one email
```

1. **Lookup** — `GetBuyerByPhone` through the existing buyer-lookup service. The phone number is only the key CRM is searched by; it
   proves nothing. Several CRM customers behind one number → `409 CUSTOMER_AMBIGUOUS` (no one is verified through the chatbot);
   none → `404 CUSTOMER_NOT_FOUND`. A customer with several units gets the list to choose from.
2. **Send** — the code goes to the **email CRM returns for that customer**. No request has a destination field. The unit must be one
   of that customer's (`403 UNIT_NOT_OWNED` otherwise). The buyer's unit and a **unit-scoped contact** are cached from the lookup
   (`UnitReferences.CrmUnitId = String(unitId)`, `ContactReferences.CrmContactId = "{customerId}-{unitId}"`, name/phone from the
   customer, type **`Buyer`** — see below). Limits: code valid 10 min; 3 sends per challenge, ≥ 60 s apart; 5 challenges per CRM
   customer per hour (any caller); 5 wrong tries lock the challenge; a repeated `send` for a live challenge does not email again.
3. **Verify** — the code is checked server-side (salted HMAC, constant time; wrong tries and single use are enforced with database
   concurrency tokens, so parallel guesses cannot out-run the budget). Success creates the OTP-verified session **in the same
   transaction that spends the challenge**. The session records the proof challenge and CRM's customer id and lead id.
4. **Documents** — `send-copy` accepts **only** a session that carries the recorded proof; it re-confirms with CRM (same phone → same
   customer, bound lead still theirs), asks `GetCustomerDocuments` for exactly that CustomerID/LeadID, offers choices when CRM says
   `selectionRequired`, downloads the chosen file with the server-side CRM credential, and emails it once.

**Not bypassable.** `POST /api/verification-sessions` now refuses `verificationMethod: "Otp"` (`400 otp-requires-challenge`);
`confirmed: true` and a method name are never proof. Existing manual-agent verification (`ManualAgentConfirmation`,
`FaceToFaceDocumentCheck`, `Other`, `AuthenticatedDigitalUser`) works exactly as before for tickets — and none of those sessions can
retrieve a document, because they carry no server-recorded proof.

**`customerType = 1` is not read as "Owner".** Its ownership meaning is unconfirmed by CRM, so the cache stores `ContactType.Buyer`
(a new value, 4). The agent-desk tenant/representative/Owner flows and their rows are untouched; an existing unit row keeps its tower
and unit type, and an existing contact row keeps its type.

**What the unit/lead binding means.** The proof covers **one unit**. Another unit of the same customer needs its own challenge
(`crmLeadId` on `send-copy` is accepted only when it equals the verified lead).

**What the lookup discloses before proof:** unit labels (project and unit number) and a masked email — what the existing Genesys
customer lookup already returns to the call flow. Never the customer's name, phone, full email or CRM customer id.

**The chatbot-asserted gap is closed, but one dependency remains: the email itself.** The proof is "the person can read the mailbox
CRM holds". If CRM's email for a customer is wrong or stale, the code goes to the wrong person. CRM must confirm those emails are the
customers' own and current (`CRM-Required-Contracts.md` §7).

## Not in this phase

Tenants and authorised representatives; "Owner" semantics; lookup by unit number; WhatsApp/SMS delivery (`501`); verifying a
different unit without a new challenge. The CRM-side `GetUnit` / `GetUnitContacts` contracts remain specified in
`CRM-Required-Contracts.md` §4 and are **not** needed for this phase.

## Open items for UAT

| # | Item | Effect |
|---|---|---|
| 1 | **Real CRM never exercised from this build.** The sandbox cannot reach the CRM host and holds no `Crm:SecretKey`. | Response shapes and the file-retrieval mechanism are verified against the contract as written and a stub of it, not UAT data. |
| 2 | **File-route authentication is unconfirmed.** TigerCS sends `X-SECRET-KEY` to the CRM origin only; a different file host is fetched **without** the secret. Run [`uat/verify-crm-file-auth.sh`](uat/verify-crm-file-auth.sh) where CRM is reachable. | A 401/403 shows as `CRM_AUTHENTICATION_FAILED`/`CRM_ACCESS_DENIED`; an HTML login page or redirect as `CRM_INVALID_RESPONSE`. |
| 3 | **TigerGroupWeb must forward the new routes.** Probed on the public host with unauthenticated POSTs: `/api/genesys/tickets` → 401 (exists), the new `/api/genesys/verification/*` and `/documents/send-copy` → 404. | Unreachable from Genesys until forwarded (and deployed). |
| 4 | **Email delivery is real only with SMTP.** Needs `EmailNotifications:Enabled=true` + credentials; the OTP and the document both ride it. | Otherwise `OTP_DELIVERY_FAILED` / `DELIVERY_FAILED` — nothing pretends to be sent. |
| 5 | **CRM data quality:** buyer email and `mobileNumber` (E.164, matching `GetBuyerByPhone?phoneNumber=`), `unitNumber` non-empty. | Customers without a usable email cannot be verified (`422 NO_EMAIL_ON_RECORD`). |
| 6 | **Migrations** `AddChatbotInactivityAndCrmDocumentCopies`, `AddCustomerOtpVerification` must be applied before deploy. | |

## The verification contract (`/api/genesys/verification`)

Same authentication, proxy path and `CustomerVerification` policy as every `/api/genesys/*` route; all four are `POST` with a JSON body;
none takes a destination, customer id, lead id, `confirmed` flag or verification method.

| Route | Body | Answers |
|---|---|---|
| `buyer-lookup` | `{ "phoneNumber" }` | `200 Found` + `units[]` + `maskedDestination` · `404 CUSTOMER_NOT_FOUND` · `409 CUSTOMER_AMBIGUOUS` · `400 INVALID_REQUEST` · `502 CRM_AUTHENTICATION_FAILED / CRM_INVALID_RESPONSE` · `503 CRM_UNAVAILABLE` |
| `otp/send` | `{ "phoneNumber", "crmUnitId"? }` | `200 CodeSent` · `200 AlreadySent` (live challenge; nothing emailed) · `200 UnitSelectionRequired` + `units[]` · `403 UNIT_NOT_OWNED` · `422 NO_EMAIL_ON_RECORD` · `429 OTP_RATE_LIMITED` · `502 OTP_DELIVERY_FAILED` · plus the lookup errors |
| `otp/resend` | `{ "challengeId" }` | `200 CodeSent` (the old code stops working) · `429 OTP_RESEND_TOO_SOON` (`Retry-After`) · `429 OTP_RESEND_LIMIT_REACHED` · `410 OTP_EXPIRED` · `423 OTP_LOCKED` · `404 OTP_CHALLENGE_NOT_FOUND` |
| `otp/verify` | `{ "challengeId", "code" }` | `200 Verified` + `session` · `400 OTP_INVALID` + `attemptsRemaining` · `410 OTP_EXPIRED` · `423 OTP_LOCKED` · `409 OTP_ALREADY_USED` · `404 OTP_CHALLENGE_NOT_FOUND` (unknown **or another account's** — identical answers, and a stranger cannot spend the customer's attempts) |

Any route answers `503 DOCUMENT_COPY_DISABLED` while the switch is off. Bodies below are captured from the host (Mock gateways off; real
buyer-lookup and document gateways over a stub CRM); `traceId` is elided.

`buyer-lookup` → `200`:
```json
{"status":"Found","code":null,"message":null,"challengeId":null,"maskedDestination":"a***@e***.test","expiresAtUtc":null,"resendAvailableAtUtc":null,"attemptsRemaining":null,"retryAfterSeconds":null,"units":[{"crmUnitId":"1101","leadId":12345,"unitNumber":"1205","projectName":"Tiger Sky Tower"},{"crmUnitId":"1102","leadId":12346,"unitNumber":"1403","projectName":"Tiger Sky Tower"}],"unit":null,"session":null}
```
`otp/send` without a unit, two units → `200`: `{"status":"UnitSelectionRequired","code":"UNIT_SELECTION_REQUIRED","message":"The customer has more than one unit. Ask which unit, then call again with its crmUnitId. Nothing was sent.", …, "units":[ … ]}`

`otp/send` with `{ "phoneNumber": "+971501234567", "crmUnitId": "1101" }` → `200`:
```json
{"status":"CodeSent","code":null,"message":"A code was emailed to the address CRM holds for this customer.","challengeId":"a7fedbd9-a7bc-4726-bbc7-be4b57693eb3","maskedDestination":"a***@e***.test","expiresAtUtc":"2026-10-07T18:24:02.0217384Z","resendAvailableAtUtc":"2026-10-07T18:15:02.0217384Z","attemptsRemaining":5,"retryAfterSeconds":null,"units":null,"unit":{"crmUnitId":"1101","leadId":12345,"unitNumber":"1205","projectName":"Tiger Sky Tower"},"session":null}
```
`otp/resend` too soon → `429` (+ `Retry-After: 60`):
```json
{"type":"https://tigercs.internal/problems/otp-resend-too-soon","title":"Too many requests","status":429,"detail":"A code was sent moments ago. Wait 60 seconds before asking for another.","traceId":"…","code":"OTP_RESEND_TOO_SOON","outcome":"ResendTooSoon","challengeId":"a7fedbd9-a7bc-4726-bbc7-be4b57693eb3","maskedDestination":"a***@e***.test","resendAvailableAtUtc":"2026-10-07T18:15:02.0217384Z"}
```
`otp/verify` wrong code → `400`:
```json
{"type":"https://tigercs.internal/problems/otp-invalid","title":"The code is not correct","status":400,"detail":"That code is not correct.","traceId":"…","code":"OTP_INVALID","outcome":"InvalidCode","challengeId":"a7fedbd9-a7bc-4726-bbc7-be4b57693eb3","attemptsRemaining":4}
```
`otp/verify` right code → `200` (the `verificationSessionId` is what `send-copy` takes):
```json
{"status":"Verified","code":null,"message":"The customer is verified.","challengeId":"a7fedbd9-a7bc-4726-bbc7-be4b57693eb3","maskedDestination":null,"expiresAtUtc":null,"resendAvailableAtUtc":null,"attemptsRemaining":null,"retryAfterSeconds":null,"units":null,"unit":null,"session":{"verificationSessionId":"ac8900ff-e15c-4e12-bfd8-b592d6c18ef1","agentEmployeeId":"311397ec-9ac5-43e5-82cb-d09329c6e262","unitReferenceId":1,"contactReferenceId":1,"status":"Confirmed","confirmed":true,"verificationMethod":"Otp","createdAtUtc":"2026-10-07T18:14:02.3289915Z","confirmedAtUtc":"2026-10-07T18:14:02.3289915Z","expiresAtUtc":"2026-10-07T18:44:02.3289915Z","snapshotUnitNumber":"1205","snapshotPropertyName":"Tiger Sky Tower","snapshotTowerName":null,"snapshotUnitType":null,"snapshotContactDisplayName":"Ahmed Ali","snapshotContactChannel":"+971501234567"}}
```
`otp/verify` again with the used code → `409`:
```json
{"type":"https://tigercs.internal/problems/otp-already-used","title":"Conflict","status":409,"detail":"This code was already used.","traceId":"…","code":"OTP_ALREADY_USED","outcome":"AlreadyUsed"}
```
`POST /api/verification-sessions` with `"verificationMethod": "Otp"` → `400 …/otp-requires-challenge`.

Genesys data actions: `data-actions/12-buyer-lookup.json`, `13-otp-send.json`, `14-otp-resend.json`, `15-otp-verify.json`, then `11-send-document-copy.json`.

## The document contract

```
POST https://tigergroup.ae/api/genesys/documents/send-copy      (via TigerGroupWeb)
POST {TigerCS API base}/api/genesys/documents/send-copy         (service account, direct)
```

### Authentication

Identical to every `/api/genesys/*` route. Genesys Cloud authenticates to TigerGroupWeb with OAuth 2.0
client credentials (`scope=ticketing.genesys`); TigerGroupWeb forwards the call to TigerCS with
`Authorization: Bearer <JWT>` for the TigerCS integration **service account** (policy
`CustomerVerification`, i.e. CS Agent / CS Supervisor / System Administrator). There is no anonymous
endpoint and no document download endpoint. The same service account must own the verification session.

### Headers

| Header | Required | Notes |
|---|---|---|
| `Authorization: Bearer …` | yes | 401 without it. |
| `Content-Type: application/json` | yes | |
| `Idempotency-Key` | **yes** | 1–128 chars of letters, digits, `. _ : -`. **Reuse the same key when retrying the same request**; use a new key for a new request — including the follow-up call that carries the customer's choice. |

### Body

| Field | Required | Description |
|---|---|---|
| `verificationSessionId` | **yes** | The session returned by `POST /api/genesys/verification/otp/verify` — created by this same service account, method `Otp`, **carrying the server-recorded OTP proof**, not expired (30 min). A session from any other path is refused (`403 VERIFICATION_FAILED`). This — not a phone number or customer id — is the only identity input. |
| `documentType` | **yes** | `Contract`, `ReservationForm`, `UnitLayout` or `RegistrationReceipt` (case-insensitive; `Reservation Form`, `unit-layout` are accepted). |
| `crmUnitId` | no | If sent it must equal the verified unit, otherwise 403 `RECORD_OWNERSHIP_MISMATCH`. Never used to look up another unit. |
| `crmLeadId` | no | Accepted only when it equals the lead the session was verified for; anything else — another customer's lead or the same customer's other unit — is 403 `RECORD_OWNERSHIP_MISMATCH` with no CRM call. Normally omit it. |
| `recordId` | no | The document the customer chose after a `choiceKind: "Document"` answer (CRM's `attachmentId`). Must be one CRM just listed for that customer and lead, otherwise 403 `RECORD_OWNERSHIP_MISMATCH`. |
| `deliveryChannel` | no | `Email` (default). `WhatsApp`/`Sms` → 501 (see above). |

There is deliberately **no** phone, customer-id or destination-address field. The destination is the
customer's email on record in CRM.

### Statuses

| `status` | HTTP | Meaning |
|---|---|---|
| `Sent` | 200 | The email was accepted for delivery. `duplicate: true` means this was a replay and nothing was sent again. |
| `Queued` | 202 | An identical request is still being processed. Nothing new was sent. Repeat the call with the same key to get the result. |
| `SelectionRequired` | 200 | More than one record matches. `choices` lists them. **Nothing was sent.** |
| `DocumentUnavailable` | 404 / 502 / 503 | `DOCUMENT_NOT_FOUND`; `CRM_REQUEST_REJECTED` / `CRM_AUTHENTICATION_FAILED` / `CRM_ACCESS_DENIED` / `CRM_INVALID_RESPONSE` (502); `DOCUMENT_SOURCE_UNAVAILABLE` (503, retryable). |
| `DeliveryFailed` | 502 / 501 / 422 | Found but not delivered: `DELIVERY_FAILED`, `DOCUMENT_TOO_LARGE`, `DELIVERY_CHANNEL_NOT_INTEGRATED`, `DELIVERY_DESTINATION_UNAVAILABLE`. `retryable` says whether to retry with the same key. |
| (errors) | 400 / 403 / 409 / 503 | `INVALID_REQUEST`, `VERIFICATION_FAILED`, `CRM_CUSTOMER_NOT_RESOLVED`, `RECORD_OWNERSHIP_MISMATCH`, `IDEMPOTENCY_KEY_REUSED`, `DOCUMENT_COPY_DISABLED`. |

Success bodies carry only: `status, code, message, documentType, recordId, deliveryChannel,
maskedDestination, deliveryRequestId, duplicate, retryable, choices, choiceKind`. Error bodies are RFC 7807
`ProblemDetails` plus `code`, `outcome` and the relevant identifiers. The full email address, the
phone number and the document are never in a response.

### Sample requests

Contract (a customer with two contracts → selection first):

```http
POST /api/genesys/documents/send-copy
Idempotency-Key: conv-7f3a-contract-1

{ "verificationSessionId": "2f6c0b2e-5d0a-4c1b-9e43-8d6a1c5d7b90", "documentType": "Contract" }
```

Contract, after the customer chose (new key):

```http
Idempotency-Key: conv-7f3a-contract-2

{ "verificationSessionId": "2f6c0b2e-5d0a-4c1b-9e43-8d6a1c5d7b90", "documentType": "Contract",
  "recordId": "5002" }
```

Reservation Form:

```json
{ "verificationSessionId": "2f6c0b2e-5d0a-4c1b-9e43-8d6a1c5d7b90", "documentType": "ReservationForm" }
```

Unit Layout (restating the verified unit is allowed):

```json
{ "verificationSessionId": "2f6c0b2e-5d0a-4c1b-9e43-8d6a1c5d7b90", "documentType": "UnitLayout",
  "crmUnitId": "CRM-UNIT-1001" }
```

Registration Receipt:

```json
{ "verificationSessionId": "9a41d7c3-61b2-4f0e-8a77-0c3f5e21b8d4", "documentType": "RegistrationReceipt",
  "deliveryChannel": "Email" }
```

### Sample responses (captured from the host over a stub CRM)

`200` — selection required (nothing sent):

```json
{"status":"SelectionRequired","code":"SELECTION_REQUIRED","message":"More than one contract matches. Ask the customer which one, then call again with its recordId.","documentType":"Contract","recordId":null,"deliveryChannel":null,"maskedDestination":null,"deliveryRequestId":null,"duplicate":false,"retryable":null,"choices":[{"recordId":"5001","label":"Sale and Purchase Agreement.pdf","unitNumber":"1205","issuedOn":null},{"recordId":"5002","label":"Addendum 1.pdf","unitNumber":"1205","issuedOn":null}],"choiceKind":"Document"}
```

`200` — sent:

```json
{"status":"Sent","code":null,"message":"The document was sent to the customer's email on record.","documentType":"Contract","recordId":"5002","deliveryChannel":"Email","maskedDestination":"a***@e***.com","deliveryRequestId":1,"duplicate":false,"retryable":null,"choices":null,"choiceKind":null}
```

`200` — replay of the same request (Genesys retry); nothing sent again:

```json
{"status":"Sent","code":null,"message":"This document was already sent; nothing was sent again.","documentType":"Contract","recordId":"5002","deliveryChannel":"Email","maskedDestination":"a***@e***.com","deliveryRequestId":1,"duplicate":true,"retryable":null,"choices":null,"choiceKind":null}
```

`202` — identical request still in progress:

```json
{"status":"Queued","code":"REQUEST_IN_PROGRESS","message":"An identical request is already being processed; nothing new was sent. Repeat the call with the same Idempotency-Key to get the result.","documentType":"Contract","recordId":"5002","deliveryChannel":"Email","maskedDestination":"a***@e***.com","deliveryRequestId":1,"duplicate":true,"retryable":null,"choices":null,"choiceKind":null}
```

### Sample error responses

> Bodies below are the shapes asserted by the integration tests (the host exercised over HTTP against a stub of CRM's contract); record ids are CRM `attachmentId`s. `traceId` is elided where shown as "…".

`400` invalid request (missing session; also missing/invalid `Idempotency-Key`, unknown `documentType`/`deliveryChannel`):

```json
{"type":"https://tigercs.internal/problems/invalid-request","title":"Invalid document request","status":400,"detail":"verificationSessionId is required — the document is released only to a verified customer.","traceId":"00-8443bfa42215365d1331d09e8dc7305b-c2304fadc0a627ae-00","code":"INVALID_REQUEST","outcome":"InvalidRequest"}
```

`403` failed verification (unknown, someone else's, unconfirmed, expired, or too-weak-method session — one answer for all):

```json
{"type":"https://tigercs.internal/problems/verification-failed","title":"Customer verification failed","status":403,"detail":"The customer is not verified for this request. Complete verification (one-time code or authenticated user) and send its verificationSessionId.","traceId":"00-c5229f5f24e8863022426026c2d056c2-b92034d93619c3b6-00","code":"VERIFICATION_FAILED","outcome":"VerificationFailed"}
```

`403` ownership mismatch (record or unit is not the verified customer's — also the answer for a guessed id):

```json
{"type":"https://tigercs.internal/problems/record-ownership-mismatch","title":"Record does not belong to the verified customer","status":403,"detail":"That record does not belong to the verified customer.","traceId":"00-ba4a3eea5066ea9195ede2a366cec469-f357fe5807b36527-00","code":"RECORD_OWNERSHIP_MISMATCH","outcome":"OwnershipMismatch","documentType":"Contract"}
```

`200` multiple matches → the `SelectionRequired` body above (this is a normal answer, not an error).

`404` missing document:

```json
{"type":"https://tigercs.internal/problems/document-not-found","title":"Document not found","status":404,"detail":"No registration receipt is on record for this customer and unit.","traceId":"00-c4eae77afbb164a6f9f0702f3483a790-38fa0b27807a55e5-00","code":"DOCUMENT_NOT_FOUND","outcome":"DocumentUnavailable","documentType":"RegistrationReceipt"}
```

`503` document source unavailable (CRM unreachable, timeout, 500 or 503):

```json
{"type":"https://tigercs.internal/problems/document-source-unavailable","title":"Document source unavailable","status":503,"detail":"CRM's document source is unavailable right now. Nothing was sent; retry with the same Idempotency-Key.","traceId":"…","code":"DOCUMENT_SOURCE_UNAVAILABLE","outcome":"DocumentUnavailable","documentType":"Contract","retryable":true}
```

`502` delivery failure (email transport/rejection; `retryable` distinguishes):

```json
{"type":"https://tigercs.internal/problems/delivery-failed","title":"Document could not be delivered","status":502,"detail":"The document could not be delivered right now. Retry with the same Idempotency-Key.","traceId":"…","code":"DELIVERY_FAILED","outcome":"DeliveryFailed","documentType":"Contract","recordId":"5002","deliveryChannel":"Email","deliveryRequestId":1,"retryable":true}
```

`501` channel without an integration:

```json
{"type":"https://tigercs.internal/problems/delivery-channel-not-integrated","title":"Document could not be delivered","status":501,"detail":"No WhatsApp delivery integration exists in TigerCS or CRM. Documents can be sent by Email only.","traceId":"00-e34dee4e224b24d86b7f20632da25217-ce97010939b2e183-00","code":"DELIVERY_CHANNEL_NOT_INTEGRATED","outcome":"DeliveryFailed","documentType":"UnitLayout","deliveryChannel":"WhatsApp","retryable":false}
```

`409` idempotency key reused for a different request:

```json
{"type":"https://tigercs.internal/problems/idempotency-key-reused","title":"Idempotency key already used","status":409,"detail":"This Idempotency-Key was already used for a different request. Use a new key for a new request.","traceId":"00-3e92fabea5104e55e40ca9675bba7de4-fe96a9b9b3b46780-00","code":"IDEMPOTENCY_KEY_REUSED","outcome":"IdempotencyConflict"}
```

Also: `422 DELIVERY_DESTINATION_UNAVAILABLE` (CRM has no valid email for the customer), `502 DOCUMENT_TOO_LARGE`,
`503 DOCUMENT_COPY_DISABLED` (`CrmDocuments:Enabled` is false — the shipped default).

## How the guarantees are met

* **Identity** — only `verificationSessionId`, and only one produced by the OTP flow: owned by the calling account, confirmed,
  unexpired, method `Otp`, **with the challenge and the CRM customer/lead recorded by the server**. Phone/customer-id are not fields.
* **Ownership** — the customer and lead come from the verified contact's CRM buyer lookup, never the caller; a
  `crmLeadId`/`recordId` outside the customer's own leads/just-listed documents is refused before anything is fetched; CRM's
  answer must echo the same customer and lead; the file reference comes only from that listing and is fetched only from the
  CRM origin.
* **Selection** — several matches → `SelectionRequired`; nothing is sent until the customer's choice arrives.
* **Duplicates** — a unique (caller, `Idempotency-Key`) row claims the send before anything is fetched;
  replays answer from it; a concurrent replay answers `Queued`; the same document/session/channel is
  not re-sent within 15 minutes even under a new key (`CrmDocuments:DuplicateSuppressionMinutes`); an
  abandoned in-progress claim can be retaken after 5 minutes.
* **Privacy** — the document is attached to an email to the CRM-held address. No link is created, so
  none can leak or need to expire. Nothing is stored beyond ids, status and a masked address
  (`CrmDocumentDeliveryRequests`). Audit action `CrmDocumentCopySent`.

## Configuration

```json
"CrmDocuments": {
  "Enabled": false,                       // ships OFF; turn on for UAT only
  "AllowInProduction": false,             // Production refuses to start with Enabled=true unless this is also true — only after UAT passes
  "AcceptedVerificationMethods": ["Otp", "AuthenticatedDigitalUser"],
  "MaxAttachmentBytes": 10485760,
  "InProgressStaleAfterMinutes": 5,
  "DuplicateSuppressionMinutes": 15,
  "OtpLifetimeMinutes": 10,
  "OtpMaxAttempts": 5,
  "OtpMaxSendsPerChallenge": 3,
  "OtpMinResendSeconds": 60,
  "OtpMaxChallengesPerCustomerPerHour": 5,
  "OtpCodePepper": null                   // SET a long random secret (user-secrets / env CrmDocuments__OtpCodePepper); never commit it
}
```

`Crm:BaseUrl` and `Crm:SecretKey` (existing) are required. `Crm:DocumentFileHosts` (extra **https** hosts a `fileUrl` may point to) are fetched
**without** the CRM secret — only the CRM origin ever receives `X-SECRET-KEY`; `Crm:MaxDocumentBytes` caps a download (default 10 MB).
A downloaded file must be a recognised document (PDF, PNG, JPEG, GIF, WebP, TIFF, BMP, DOC, DOCX); its extension and media type come from
the file's own signature (corroborated by Content-Type / Content-Disposition / name), so an extension-less CRM "Layout Plan" is mailed as
`Layout Plan.png` or `.pdf` according to what it really is — never assumed to be a PDF — and anything else (an executable, an HTML page,
an unknown blob) is refused.

Email delivery needs `EmailNotifications:Enabled = true` with the `Smtp` provider configured (the OTP and the document both use it).
Without it the answers are `OTP_DELIVERY_FAILED` / `DELIVERY_FAILED` — nothing pretends to be sent.

Migrations: `AddChatbotInactivityAndCrmDocumentCopies`, `AddCustomerOtpVerification` (`AddCustomerOtpVerification.sql` at the repo root).
