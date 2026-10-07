# Document copy API — chatbot asks CRM to send the customer a document

Status: **implemented and tested in TigerCS; not usable end-to-end against real Tiger CRM yet** —
see [Missing dependencies](#missing-dependencies-read-this-first).

The chatbot can ask for a copy of one of four documents — **Contract, Reservation Form, Unit
Layout, Registration Receipt** — to be sent to the customer. TigerCS verifies who the customer is,
confirms the document is theirs, asks them to choose if there is more than one, and sends it to the
email address CRM holds for them. The document is never returned to the caller.

## Missing dependencies (read this first)

| # | What is missing | Effect today | Who provides it |
|---|---|---|---|
| 1 | **Tiger CRM document operations.** Nothing in this repository or in the CRM contracts it integrates (`TicketingSystem/GetBuyerByPhone`, `Internal-CRM-API-Contract.md`) stores, generates or returns a contract, reservation form, unit layout or registration receipt. | With `Crm:Provider = Http` (UAT/Production) every request answers **503 `DOCUMENT_SOURCE_UNAVAILABLE`**. Nothing is sent, nothing is invented. | Tiger CRM: for each of the four types, (a) *list records for unit U and contact C* returning a record id, label, unit id, owner contact id, issue date, and the contact's email on record; (b) *get the file for record R* returning bytes, content type, file name and the owning unit/contact. TigerCS side: implement `ICrmDocumentGateway` (replace `UnimplementedCrmDocumentGateway`, `src/TigerCS.Integrations/Modules/CrmIntegration/`). |
| 2 | **WhatsApp (and SMS) delivery.** TigerCS has an SMTP email sender only; Genesys is never called by TigerCS, so no WhatsApp/SMS send integration exists. | A request for `deliveryChannel: WhatsApp` answers **501 `DELIVERY_CHANNEL_NOT_INTEGRATED`**. Email works. | A WhatsApp send integration (Genesys Cloud outbound messaging or a WhatsApp Business provider) plus an `IDocumentDeliveryChannelSender` for it. If WhatsApp needs a *link* rather than an attachment, an authorized expiring-link download endpoint is also needed (not built — email attachment needs none). |
| 3 | **OTP issue/verification.** The existing verification flow records that a method (`Otp`, `AuthenticatedDigitalUser`) was used; TigerCS does not itself send or check a one-time code. | The identity guarantee is exactly as strong as the integration account's assertion that the OTP passed. Mitigation built in: documents only ever go to the email CRM holds for the verified contact, never to a caller-supplied address. | Genesys Architect flow / OTP service performing the check before it creates the session. |
| 4 | **Unit/contact lookup over real CRM.** `POST /api/verification-sessions` needs the unit and contact cached through `GET /api/crm/units/{id}/contacts`, which with `Crm:Provider = Http` also fails closed (`UnimplementedCrmHttpGateway`, "no published endpoint"). | In UAT/Production a verification session for the chatbot cannot be created yet, independent of this feature. | Tiger CRM: the unit and contact endpoints named in `Internal-CRM-API-Contract.md` §1.1–1.3. |
| 5 | **TigerGroupWeb proxy route.** Genesys never calls TigerCS directly; TigerGroupWeb forwards `/api/genesys/*` routes. | The new route is unreachable from Genesys until TigerGroupWeb forwards it. | TigerGroupWeb owners (`TicketingGenesysService`). |

Until 1, 4 and 5 exist the feature **can be exercised locally and in automated tests** (Mock CRM provider,
recording email adapter) but **not against real UAT data**. Nothing has been deployed.

## The contract

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
| `verificationSessionId` | **yes** | Id of a **Confirmed** session from `POST /api/verification-sessions`, created by this same service account, method `Otp` or `AuthenticatedDigitalUser` (configurable: `CrmDocuments:AcceptedVerificationMethods`), not expired. This — not a phone number or customer id — is the only identity input. A session already consumed by ticket creation is still accepted until it expires. |
| `documentType` | **yes** | `Contract`, `ReservationForm`, `UnitLayout` or `RegistrationReceipt` (case-insensitive; `Reservation Form`, `unit-layout` are accepted). |
| `crmUnitId` | no | If sent it must equal the verified unit, otherwise 403 `RECORD_OWNERSHIP_MISMATCH`. Never used to look up another unit. |
| `recordId` | no | The CRM record the customer chose after a `SelectionRequired` answer. Must be one of that customer's own records, otherwise 403 `RECORD_OWNERSHIP_MISMATCH`. |
| `deliveryChannel` | no | `Email` (default). `WhatsApp`/`Sms` → 501 (see above). |

There is deliberately **no** phone, customer-id or destination-address field. The destination is the
customer's email on record in CRM.

### Statuses

| `status` | HTTP | Meaning |
|---|---|---|
| `Sent` | 200 | The email was accepted for delivery. `duplicate: true` means this was a replay and nothing was sent again. |
| `Queued` | 202 | An identical request is still being processed. Nothing new was sent. Repeat the call with the same key to get the result. |
| `SelectionRequired` | 200 | More than one record matches. `choices` lists them. **Nothing was sent.** |
| `DocumentUnavailable` | 404 / 503 | `DOCUMENT_NOT_FOUND` (customer has none) or `DOCUMENT_SOURCE_UNAVAILABLE` (CRM source missing/down). |
| `DeliveryFailed` | 502 / 501 / 422 | Found but not delivered: `DELIVERY_FAILED`, `DOCUMENT_TOO_LARGE`, `DELIVERY_CHANNEL_NOT_INTEGRATED`, `DELIVERY_DESTINATION_UNAVAILABLE`. `retryable` says whether to retry with the same key. |
| (errors) | 400 / 403 / 409 / 503 | `INVALID_REQUEST`, `VERIFICATION_FAILED`, `RECORD_OWNERSHIP_MISMATCH`, `IDEMPOTENCY_KEY_REUSED`, `DOCUMENT_COPY_DISABLED`. |

Success bodies carry only: `status, code, message, documentType, recordId, deliveryChannel,
maskedDestination, deliveryRequestId, duplicate, retryable, choices`. Error bodies are RFC 7807
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
  "recordId": "MOCK-CONTRACT-2" }
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

### Sample responses (captured from the running host against the Mock CRM provider)

`200` — selection required (nothing sent):

```json
{"status":"SelectionRequired","code":"SELECTION_REQUIRED","message":"More than one contract matches. Ask the customer which one, then call again with its recordId.","documentType":"Contract","recordId":null,"deliveryChannel":null,"maskedDestination":null,"deliveryRequestId":null,"duplicate":false,"retryable":null,"choices":[{"recordId":"MOCK-CONTRACT-1","label":"Sale and Purchase Agreement","unitNumber":"1204","issuedOn":"2025-03-01T00:00:00"},{"recordId":"MOCK-CONTRACT-2","label":"Addendum 1 to the Sale and Purchase Agreement","unitNumber":"1204","issuedOn":"2025-09-15T00:00:00"}]}
```

`200` — sent:

```json
{"status":"Sent","code":null,"message":"The document was sent to the customer's email on record.","documentType":"Contract","recordId":"MOCK-CONTRACT-2","deliveryChannel":"Email","maskedDestination":"a***@e***.com","deliveryRequestId":1,"duplicate":false,"retryable":null,"choices":null}
```

`200` — replay of the same request (Genesys retry); nothing sent again:

```json
{"status":"Sent","code":null,"message":"This document was already sent; nothing was sent again.","documentType":"Contract","recordId":"MOCK-CONTRACT-2","deliveryChannel":"Email","maskedDestination":"a***@e***.com","deliveryRequestId":1,"duplicate":true,"retryable":null,"choices":null}
```

`202` — identical request still in progress:

```json
{"status":"Queued","code":"REQUEST_IN_PROGRESS","message":"An identical request is already being processed; nothing new was sent. Repeat the call with the same Idempotency-Key to get the result.","documentType":"Contract","recordId":"MOCK-CONTRACT-2","deliveryChannel":"Email","maskedDestination":"a***@e***.com","deliveryRequestId":1,"duplicate":true,"retryable":null,"choices":null}
```

### Sample error responses

> The 400, 403, 404, 409 and 501 bodies below were captured from the running host; the 202, 502 and 503 bodies are built by the same code path and are asserted in the unit tests, but were not captured on the wire (their `traceId` is elided).

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

`503` document source unavailable (today's answer in UAT/Production — dependency 1):

```json
{"type":"https://tigercs.internal/problems/document-source-unavailable","title":"Document source unavailable","status":503,"detail":"CRM's document source is not available (no Tiger CRM document endpoint is integrated, or CRM cannot be reached). Nothing was sent.","traceId":"…","code":"DOCUMENT_SOURCE_UNAVAILABLE","outcome":"DocumentUnavailable","documentType":"Contract","retryable":true}
```

`502` delivery failure (email transport/rejection; `retryable` distinguishes):

```json
{"type":"https://tigercs.internal/problems/delivery-failed","title":"Document could not be delivered","status":502,"detail":"The document could not be delivered right now. Retry with the same Idempotency-Key.","traceId":"…","code":"DELIVERY_FAILED","outcome":"DeliveryFailed","documentType":"Contract","recordId":"MOCK-CONTRACT-2","deliveryChannel":"Email","deliveryRequestId":1,"retryable":true}
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

* **Identity** — only `verificationSessionId`; checked for ownership by the calling account, confirmed
  status, expiry and an accepted method. Phone/customer-id are not request fields.
* **Ownership** — CRM is asked for the verified unit + contact's records; the result is filtered again;
  the fetched content is checked against the verified unit/contact before anything is sent. A named
  `recordId` outside the customer's own list is never fetched.
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
  "Enabled": false,                       // ships OFF; turn on for UAT
  "AcceptedVerificationMethods": ["Otp", "AuthenticatedDigitalUser"],
  "MaxAttachmentBytes": 10485760,
  "InProgressStaleAfterMinutes": 5,
  "DuplicateSuppressionMinutes": 15
}
```

Email delivery also requires `EmailNotifications:Enabled = true` with the `Smtp` provider configured
(otherwise `DELIVERY_FAILED`, not retryable).

Genesys data action: `docs/Genesys/data-actions/11-send-document-copy.json`.
