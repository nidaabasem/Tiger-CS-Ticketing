# Customer Details · Documents tab

Reservation Form (CRM type 4), Tiger Contract (5) and Registration Receipt (6) for the selected unit, with **Open**, **Download**
and **Send to customer**. Built on the existing CRM document integration — no second integration:

| Piece | Reused |
|---|---|
| Listing + file fetch | `ICrmDocumentGateway` → `POST {Crm}/TicketingSystem/GetCustomerDocuments` (`CustomerID`, `LeadID`, `DocumentType`) and the credentialed `fileUrl` fetch (`CrmDocumentHttpGateway`) |
| Customer and unit (lead) | `CrmBuyerLookupAppService` (`GetBuyerByPhone`) — the CRM lead of a unit comes from here, never from the browser |
| Sending | `CrmDocumentCopyAppService` — the service behind `POST /api/genesys/documents/send-copy` (new entry point `SendToCustomerAsync`, same delivery core) |

## Routes (TigerCS.Api, `AuthenticatedStaff`)

| Route | Purpose |
|---|---|
| `GET /api/customers/{customerKey}/documents?crmLeadId=` | Units + the three document groups for the unit. Several units and none chosen → `UnitSelectionRequired`, no documents. |
| `GET /api/customers/{customerKey}/documents/{crmLeadId}/{documentType}/{recordId}/file?download=` | The file (inline, or attachment with `download=true`). |
| `POST /api/customers/{customerKey}/documents/send` + `Idempotency-Key` | `{ crmLeadId, documentType, recordId }` → emailed as an attachment. Same HTTP/problem shapes as `send-copy`. |

The Web page (`/Customers/{key}`) adds `?handler=DocumentsPanel` (lazy tab fragment), `?handler=DocumentFile` (file relay) and
`?handler=SendDocument` (POST, redirect back to `?tab=documents`).

## Ownership and access (every call, in order)

1. The customer must be **visible to the signed-in agent** (same department-scoped check as the Customer Profile). Not visible = not found.
2. Documents exist only for a **CRM customer**; CRM must resolve one of the phones TigerCS holds to **that same CRM customer**.
3. `crmLeadId` must be one of that customer's CRM units (else 403, CRM is not asked).
4. A file/send names a record only if CRM **just listed it** for that customer + unit + type (a guessed or other-unit id → 403, nothing fetched).
5. CRM's `fileUrl` and credential never reach the browser; the gateway still refuses a listing that echoes another customer/lead and a file reference outside the CRM origin.
6. Read-only roles (Chairman/CEO, Reporting User) can open files but not send (the existing POST guard; the button says why).

## Sending: what is and is not true

* **Channel:** email only, with the document as an **attachment**, to the **email CRM holds for the customer**. The agent cannot pick or type an address.
  There is no WhatsApp/SMS/Genesys-conversation attachment integration in TigerCS or CRM (`send-copy` answers `501` for them), so the tab does not offer one.
* **Why `send-copy` itself is not called from the page:** it accepts only an OTP-proven verification session owned by the Genesys service account.
  An agent on Customer Details has none. The agent entry point uses the **same delivery core** (record selection from CRM's listing, idempotency
  claim, server-side download, size/type checks, one email, audit) but takes its proof from the agent's access to the customer (rules 1–4 above).
* **"Accepted" is not "delivered".** `Sent` means the SMTP server accepted the message. TigerCS has no delivery receipt, bounce feed or read receipt, so
  the UI says *"Accepted for sending … Accepted is not delivered"* and never "delivered". A repeat within 15 minutes says *"Not sent again"*.
* **Prerequisites (otherwise Send is disabled with the reason shown):** `CrmDocuments:Enabled=true` (ships `false`; a Production host refuses to start with it
  on unless `CrmDocuments:AllowInProduction=true`), `EmailNotifications:Enabled=true` with the `Smtp` provider (the `Recording` provider records in memory and
  sends nothing), a valid email in CRM, and at least one document. Listing/Open/Download do **not** depend on `CrmDocuments:Enabled`.
* **Audit/idempotency:** one `CrmDocumentDeliveryRequests` row and a `CrmDocumentCopySent` audit entry per send (`Origin=AgentDesk`). There is no verification
  session on this path, so the row's `VerificationSessionId` holds a stable surrogate (SHA-256 of customer + lead), which also lets the 15-minute duplicate
  suppression stop two agents emailing the same file. No migration was needed.

## UAT (needs real CRM, real SMTP and a mailbox you can read)

Pre-reqs: `Crm:Provider=Http` with `Crm:BaseUrl`/`Crm:SecretKey`; `CrmDocuments:Enabled=true`; `EmailNotifications:Enabled=true`, `Provider=Smtp`.
Use a CRM customer you control whose CRM email is a mailbox you can read, with two units that have different documents.

1. **Retrieval** — Customers → open the customer → **Documents**. With two units the tab asks you to pick one; pick unit A. Check the three groups against CRM
   (type 4/5/6 attachments of that lead). Pick unit B: A's documents disappear immediately and only B's show.
2. **Open / Download** — Open a contract (new tab shows it); Download saves it with the CRM attachment name. Compare the bytes/page count with the file in CRM.
   Try a hand-edited link with another unit's record id → a 403 page, no file.
3. **Attachment** — Click **Send to customer** on one document. Expect *"Accepted for sending … to x***@y***.com"*. Open the mailbox: one email, subject
   "Your … — Tiger Properties", the file attached and identical to what Open showed. Click again → *"Not sent again"*; the mailbox still has one email.
4. **Unavailable reasons** — Turn `CrmDocuments:Enabled` off → button disabled with the reason; remove the customer's CRM email → disabled with the reason.

**Not verified in this repository's sandbox:** nothing here could reach Tiger CRM, a real SMTP relay or a real mailbox, so receipt of a real attachment (step 3)
and the real CRM file-route authentication (`docs/Genesys/uat/verify-crm-file-auth.sh`) are unverified. Automated tests prove what TigerCS requested of CRM
and handed to the mail sender, against a stub CRM and the in-memory recording sender.
