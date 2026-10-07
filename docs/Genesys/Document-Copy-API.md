# Document copy API — chatbot asks CRM to send the customer a document

> ## ⚠ PENDING REAL UAT VERIFICATION
> Not complete until a real customer has completed verification, selected a unit and a document, and received exactly one email through the public Genesys route. Blocking CRM work and the exact contracts: [`CRM-Required-Contracts.md`](CRM-Required-Contracts.md). Exit criteria: §8 there.

Status: **connected to Tiger CRM's `TicketingSystem/GetCustomerDocuments`; verified in automated tests against a stub
of that contract. Not yet verified against real UAT CRM** — see [Open items](#open-items-for-uat).

The chatbot can ask for a copy of one of four documents — **Contract, Reservation Form, Unit
Layout, Registration Receipt** — to be sent to the customer. TigerCS verifies who the customer is, resolves
their CRM customer and unit, asks CRM for the documents, asks the customer to choose if there is more than one, and
emails the chosen file to the address CRM holds for them. The document is never returned to the caller.

## How a request is resolved

1. **Verification** — `verificationSessionId` (existing flow): owned by the calling account, confirmed (or consumed),
   unexpired, method `Otp`/`AuthenticatedDigitalUser`. Nothing else identifies the customer.
2. **CRM customer** — the verified session's contact (as cached from CRM) supplies the phone number; the existing CRM
   buyer lookup (`GetBuyerByPhone`) turns it into CRM's **`CustomerID`** and the customer's units with their **`LeadID`**s.
   A contact with no phone, a phone that is not exactly one CRM buyer, or an ambiguous match releases nothing
   (`403 CRM_CUSTOMER_NOT_RESOLVED`).
3. **CRM lead (the selected unit)** — the lead of the unit the customer verified on; or `crmLeadId` if the caller names
   one, which **must be one of that customer's own leads** (else `403 RECORD_OWNERSHIP_MISMATCH`, and CRM is not called).
   If the verified unit is not one of the customer's leads and they have several, the answer is `SelectionRequired`
   with `choiceKind: "Unit"`.
4. **CRM documents** — `POST {Crm}/TicketingSystem/GetCustomerDocuments` with `X-SECRET-KEY` (`Crm:SecretKey`, the same
   convention as the buyer lookup) and `{"CustomerID", "LeadID", "DocumentType"}`:

   | Public `documentType` (unchanged) | CRM `DocumentType` | CRM source |
   |---|---|---|
   | `Contract` | `TigerContract` | attachment type 5 |
   | `ReservationForm` | `ReservationForm` | attachment type 4 |
   | `RegistrationReceipt` | `RegistrationReceipt` | attachment type 6 |
   | `UnitLayout` | `Layout` | the unit's `unitplan` field (no `attachmentId`; record id `LAYOUT-{leadId}`) |

   CRM's `customerId`/`leadId` in the answer must equal what was asked, or the answer is refused (`CRM_INVALID_RESPONSE`).
5. **Selection** — `selectionRequired: true` (or more than one attachment) → `SelectionRequired` with `choiceKind: "Document"`;
   nothing is fetched or sent. The chatbot calls again with the chosen `recordId` (the CRM `attachmentId`).
6. **File** — `fileUrl` is a storage reference, never public and never exposed. It is fetched server-side with the CRM
   credential, only from the configured CRM origin (relative and `~/` paths resolve against `Crm:BaseUrl`) or a host in
   `Crm:DocumentFileHosts`; redirects are not followed; an HTML body is rejected.
7. **Delivery** — email attachment to the customer's email **as returned by the CRM buyer lookup**; idempotency, audit
   (`CrmDocumentCopySent`, with CRM customer/lead ids) and the delivery record are unchanged.

## CRM responses and what Genesys sees

| CRM answer | TigerCS answer |
|---|---|
| 200, for the asked customer/lead | listing handled as above |
| 404 | `404 DOCUMENT_NOT_FOUND` (nothing on record) |
| 400 | `502 CRM_REQUEST_REJECTED` — not retryable (a TigerCS/contract defect) |
| 401 | `502 CRM_AUTHENTICATION_FAILED` — not retryable; `Crm:SecretKey` wrong/missing |
| 403 | `502 CRM_ACCESS_DENIED` — not retryable |
| 500, 503, other 5xx, timeout, unreachable | `503 DOCUMENT_SOURCE_UNAVAILABLE` — `retryable: true` |
| 200 but not the contract / other customer / redirect / off-origin `fileUrl` | `502 CRM_INVALID_RESPONSE` |

Nothing is sent in any failure case. A failure after the send was claimed marks the record Failed; a retry with the same
`Idempotency-Key` tries again.

## Open items for UAT

| # | Item | Effect |
|---|---|---|
| 1 | **Real CRM never exercised from this build.** The sandbox that built this cannot reach the CRM host and holds no `Crm:SecretKey`. | Response shapes and the storage-retrieval mechanism are verified against the contract as written, not against UAT data. |
| 2 | **How `fileUrl` is retrieved with auth.** Not yet confirmed by CRM; the exact download contract TigerCS needs is in [`CRM-Required-Contracts.md` §6](CRM-Required-Contracts.md#6-file-download-contract-what-crmdocumenthttpgateway-does-with-each-fileurl). TigerCS sends `X-SECRET-KEY` to the CRM origin. If CRM serves files differently (a different host, a token, or an endpoint by `attachmentId`), set `Crm:DocumentFileHosts` or adapt `CrmDocumentHttpGateway.DownloadAsync`. | First UAT run answers this. A 401/403 on download shows as `CRM_AUTHENTICATION_FAILED` / `CRM_ACCESS_DENIED`; an HTML login page as `CRM_INVALID_RESPONSE`. |
| 3 | **Verified contact must carry a phone.** The CRM identity comes from the phone of the verified contact. | Sessions whose contact channel is an email cannot be resolved (`CRM_CUSTOMER_NOT_RESOLVED`). |
| 4 | **Verification sessions over real CRM are blocked**: `ICrmGateway` is unimplemented for `Crm:Provider=Http`, so no unit/contact can be cached and no session created. In addition TigerCS does not itself check an OTP — a session labelled `Otp` is the caller's assertion. | Exact CRM contracts, the `GetBuyerByPhone` field mapping, the buyers-only alternative and the OTP gap: [`CRM-Required-Contracts.md`](CRM-Required-Contracts.md). |
| 5 | **WhatsApp/SMS**: no integration → `501 DELIVERY_CHANNEL_NOT_INTEGRATED`. Email works. | |
| 6 | **TigerGroupWeb proxy** must forward the route. | |

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
| `crmLeadId` | no | The CRM lead of the unit the customer chose (from a `choiceKind: "Unit"` selection). Must be one of the verified customer's own leads, otherwise 403 `RECORD_OWNERSHIP_MISMATCH`. Omitted → the verified unit. |
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

### Sample responses (captured from the running host against the Mock CRM provider)

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

* **Identity** — only `verificationSessionId`; checked for ownership by the calling account, confirmed
  status, expiry and an accepted method. Phone/customer-id are not request fields.
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
  "Enabled": false,                       // ships OFF; turn on for UAT
  "AcceptedVerificationMethods": ["Otp", "AuthenticatedDigitalUser"],
  "MaxAttachmentBytes": 10485760,
  "InProgressStaleAfterMinutes": 5,
  "DuplicateSuppressionMinutes": 15
}
```

`Crm:BaseUrl` and `Crm:SecretKey` (existing settings) are required; optional `Crm:DocumentFileHosts` (extra https hosts the files may come from) and `Crm:MaxDocumentBytes`.

Email delivery also requires `EmailNotifications:Enabled = true` with the `Smtp` provider configured
(otherwise `DELIVERY_FAILED`, not retryable).

Genesys data action: `docs/Genesys/data-actions/11-send-document-copy.json`.
