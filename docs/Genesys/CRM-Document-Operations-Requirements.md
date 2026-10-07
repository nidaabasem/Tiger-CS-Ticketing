# Tiger CRM — document list/download operations required for "send a copy of a document"

**Status: BLOCKED for real UAT.** The Tiger CRM source and database were not accessible when this was written, so no
CRM operation could be inspected or implemented, and nothing here is verified against a CRM. What exists in TigerCS
is the port `ICrmDocumentGateway` (`src/TigerCS.Application/Modules/CrmDocuments/Abstractions/`), complete and
tested **against a Mock gateway and a recording email adapter only**. With `Crm:Provider=Http` the only gateway,
`UnimplementedCrmDocumentGateway`, fails closed (503 `DOCUMENT_SOURCE_UNAVAILABLE`). Mock delivery and the
integration account's OTP assertion are **not** evidence of end-to-end delivery.

## What the CRM team must provide (same conventions as `GetBuyerByPhone`: `X-SECRET-KEY`, JSON envelope)

Proposed routes — names are a proposal for the CRM developer to confirm against the CRM schema.

### 1. List

```
GET /TicketingSystem/GetCustomerDocuments?documentType={Contract|ReservationForm|UnitLayout|RegistrationReceipt}&unitId={int}&customerId={int}
```
```json
{ "success": true, "found": true, "customerEmail": "owner@example.com",
  "documents": [ { "recordId": "SPA-2026-0912", "label": "Sale and Purchase Agreement",
                   "unitId": 9200, "customerId": 9001, "unitNumber": "1204", "issuedOn": "2026-03-14" } ] }
```

### 2. Download

```
GET /TicketingSystem/GetCustomerDocumentFile?documentType=...&recordId=...&customerId={int}
```
Response: the file bytes with `Content-Type` and `Content-Disposition: attachment; filename="…"`, plus headers or a JSON
envelope naming the owning `unitId` and `customerId`. `404` when the record no longer exists.

### Rules the CRM implementation must enforce itself (TigerCS re-checks, but must not be the only check)

* **Ownership:** only records whose unit belongs to that customer under CRM's existing Buyer rules (Lead Sold/Contract,
  CustomerType Buyer — the rules behind `GetBuyerByPhone`). A `recordId` for another customer's unit → `404`, same as unknown.
* **Real sources:** each document type must come from the CRM table/blob store that really holds it. If a type is not stored
  anywhere (e.g. registration receipts), say so — it must answer "not found", never a generated placeholder.
* **Email:** `customerEmail` is the customer's own email on record; TigerCS only ever sends to it, never to a caller-supplied address.
* No internal notes, no other customers' data, no other units in either response.

## TigerCS work once CRM publishes them (small)

Replace `UnimplementedCrmDocumentGateway` with an HTTP gateway (pattern: `CrmUnitDetailsHttpGateway`), map `GetAsync`
outcomes to `CrmDocumentSourceUnavailableException` for outages, and bind the routes' real field names. Everything above
the port (verification check, ownership re-check, selection, idempotency, email attachment) is already built.

## Other blockers for real UAT (independent of this feature)

* **Verification sessions over real CRM** need the unit/contact CRM endpoints (`Internal-CRM-API-Contract.md` §1.1–1.3), which
  are unpublished (`UnimplementedCrmHttpGateway`), so a chatbot verification session cannot be created in UAT yet.
* **OTP** issue/check lives outside TigerCS; the session only records that the integration account says it passed.
* **WhatsApp/SMS** are not integrated (501); email only.
* **TigerGroupWeb** must forward the route (`TigerGroupWeb-Proxy-Change.md`).
