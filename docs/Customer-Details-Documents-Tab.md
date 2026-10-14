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
* **Explicit send permission.** Seeing a customer or opening their files does not allow sending. Sending needs a role in
  `CrmDocuments:AgentSendRoles` (default: CS Agent, CS Supervisor, CS Manager; System Administrator via the standard override). A configured list
  **replaces** the default. Checked first on every send, before the customer is resolved or CRM is called; refused with `403 DOCUMENT_SEND_NOT_PERMITTED`,
  and the tab shows the reason with the buttons disabled. The destination is always CRM's email for the customer; the request has no address field.
* **Recording mail provider.** `EmailNotifications:Provider=Recording` keeps messages in memory and reports "Sent" without contacting anyone. The agent
  desk checks `IEmailSender.SendsRealMail` and, when false, shows "only records messages in memory… Use the Smtp provider", disables Send, and refuses a
  request with `503 EMAIL_TRANSPORT_NOT_REAL` — before any download, claim or row. It can therefore never show "Accepted for sending".
* **Other prerequisites (otherwise Send is disabled with the reason shown):** `CrmDocuments:Enabled=true` (ships `false`; a Production host refuses to start
  with it on unless `CrmDocuments:AllowInProduction=true`), `EmailNotifications:Enabled=true` with the `Smtp` provider, a valid email in CRM, and at least one
  document. Listing/Open/Download do **not** depend on `CrmDocuments:Enabled` or on the send grant.

## Delivery persistence (`CrmDocumentDeliveryRequests`)

Migration `AddAgentDeskDocumentDelivery` (script: `AddAgentDeskDocumentDelivery.sql`, idempotent). The table has no foreign key to verification sessions (only
`CallerEmployeeId → AspNetUsers`); consumers of `VerificationSessionId` are the Genesys replay check, the 15-minute duplicate suppression and the
`IX_..._SessionDocument` index. The origin is now modelled explicitly:

| Column | Genesys send | Agent-desk send |
|---|---|---|
| `Origin` (tinyint, default 1 for every existing row) | `1` GenesysVerifiedSession | `2` AgentDesk |
| `VerificationSessionId` (now nullable) | the real verified session | **NULL** |
| `CrmCustomerId`, `CrmLeadId` (new, nullable) | NULL | the CRM customer and unit |

A check constraint `CK_CrmDocumentDeliveryRequests_Origin` allows only those two shapes. Idempotency stays the unique `(CallerEmployeeId, IdempotencyKey)`;
a replay must match its row's origin and scope (session, or customer + lead), so a key used by one door conflicts at the other. Duplicate suppression is
per scope (`IX_..._SessionDocument` for Genesys, new `IX_..._AgentDocument` for agents): an agent send never suppresses a Genesys send or vice versa; two
agents sending the same document to the same customer + unit within 15 minutes send one email. Rollback (`Down`) deletes agent-desk rows (they cannot exist
under the old NOT NULL column). The Genesys flow, its OTP requirements and its fingerprint are unchanged.

## Live UAT — minimal configuration and steps (ALL UNVERIFIED until performed)

Minimal configuration of the **API** host (Web only needs its existing API base URL). Secrets via user-secrets / environment, never committed:

```
Crm__Provider=Http
Crm__BaseUrl=https://<crm-host>/            # where GetCustomerDocuments and the file routes live
Crm__SecretKey=<X-SECRET-KEY value>
CrmDocuments__Enabled=true                  # (Production also needs CrmDocuments__AllowInProduction=true)
EmailNotifications__Enabled=true
EmailNotifications__Provider=Smtp           # NOT Recording
EmailNotifications__SmtpHost=<host>  EmailNotifications__SmtpPort=587  (+ the sender/credentials keys the Smtp provider requires)
# optional: CrmDocuments__AgentSendRoles__0="CS Agent"  (default already grants CS Agent / Supervisor / Manager)
```

Apply the schema first: `AddAgentDeskDocumentDelivery.sql` (or `dotnet ef database update`). Test data: a CRM customer you control whose **CRM email is a
mailbox you can read**, two units with different documents, and a TigerCS ticket for that customer (so it appears in Customers).

1. **CRM download authentication** — from a machine that reaches CRM run `docs/Genesys/uat/verify-crm-file-auth.sh` (checks the `fileUrl` route with and without
   `X-SECRET-KEY`). Then, signed in as a CS Agent, open the customer → Documents → pick a unit → **Open** a document. Pass = the real file renders; a
   `CRM_AUTHENTICATION_FAILED` / `CRM_ACCESS_DENIED` / `CRM_INVALID_RESPONSE` error naming the cause means the file route rejects the credential or serves HTML.
   Compare the file with the one in CRM (bytes/page count). Repeat with a hand-edited link carrying another unit's record id → must be a 403 page.
2. **One real attachment over SMTP** — click **Send to customer** once on one document. Expect "Accepted for sending … to x***@y***" (the SMTP server accepted it).
   Then, in the customer's mailbox, find exactly one email "Your … — Tiger Properties" with the file attached and identical to step 1's file. That mailbox check is the only
   evidence of receipt; "accepted" is not "delivered". Click again within 15 minutes → "Not sent again", still one email.
3. **Guard rails (optional, quick):** with `Provider=Recording` the tab says nothing can be sent; a role outside `AgentSendRoles` sees the reason and disabled buttons.

**Unverified here:** steps 1 and 2 (no CRM, SMTP relay or mailbox were reachable from the build sandbox), and the new migration against a real SQL Server (the script was
generated by EF Core but not executed; tests use the in-memory provider, so the check constraint is not exercised by them).
