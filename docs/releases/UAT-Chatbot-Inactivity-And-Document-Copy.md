# UAT release notes — chatbot inactivity closure and document copies

Branch `claude/admiring-brown-73jnmx`. **Nothing here has been deployed.** What was verified is listed
under [Verification](#verification); what could not be verified is listed under
[Not verified / blocked](#not-verified--blocked).

## 1. Chatbot: close the ticket after customer inactivity

**Rule.** When the chatbot has asked a question and the customer is silent for **more than 5 minutes**
(configurable), a server-side job closes the ticket. Only for an active chatbot exchange; never when
a human is involved.

### How it works

1. Genesys tells TigerCS the chatbot is waiting: `PATCH /api/genesys/tickets/{ticketId}` with
   `{"conversationId": "...", "awaitingCustomerReply": true}` (data action
   `docs/Genesys/data-actions/10-awaiting-customer-reply.json`). TigerCS stores the server time in
   `TicketInteractions.AwaitingCustomerReplySinceUtc`.
2. Repeating `true` while the timer runs changes nothing (it can never be postponed by a retried webhook).
   `awaitingCustomerReply: false` — the customer replied — clears it. The chatbot's next question sends
   `true` again and starts a new timer.
3. The timer is not started (and a running one is cleared) when the conversation has ended, a handoff to a
   human is requested or pending (requested human, AI connection lost, escalation), a person owns the
   ticket, or the ticket is closed.
4. A Hangfire recurring job (`chatbot-inactivity-close`, every minute) lists timers older than the timeout
   and, **per interaction, in its own scope**, re-checks everything (still waiting, still due, not ended, no
   open handoff, no owner, ticket still Open/InProgress/PendingCustomer) before closing.
5. Closure goes through `TicketLifecycleAppService.CloseForCustomerInactivityAsync`: status history
   `→ Resolved → Closed` (system actor), a `TicketResolution` with outcome **Cancelled** and the reason
   note, the audit entry `AutoCloseCustomerInactivity` with the reason
   **"Automatically closed — customer did not respond for more than 5 minutes."** (minutes reflect the
   configured value), and the `TicketClosed` Outbox event (idempotent). **No** "resolved" notification is sent.

**Race with a reply.** `AwaitingCustomerReplySinceUtc` is an EF concurrency token written in the same
`SaveChanges` as the closure; a reply that clears it first makes the closure's write fail and roll back — the
reply wins. Proven against a real relational engine in `InteractionTimerConcurrencyTests`.

**Idempotent.** `InactivityClosedAtUtc` is write-once; repeated or overlapping runs close once and notify once.

### Decisions to confirm in UAT

* The ticket is closed as **Cancelled** (decided: the customer's request was not resolved). **Reopen:** a ticket closed
  this way — and only this — can be reopened to InProgress by CS Agent, CS Supervisor, CS Manager (System Administrator via the
  central override), inside the normal window, with the closure audit preserved. Every other Cancelled stays final.
  Details, configuration and payloads: `docs/Genesys/Chatbot-Inactivity-UAT-Configuration.md`.
* The lifecycle normally cannot resolve an *Open* (unassigned) ticket; the new path is deliberately the one
  exception, and only for unowned tickets. SLA breach finalization is not run on this closure.
* Timing: the job runs each minute, so closure happens within ~1 minute after the deadline passes.

### Configuration

| Setting | Default | Notes |
|---|---|---|
| `Genesys:CustomerInactivityTimeoutMinutes` | `5` | `0` switches automatic closure off (timers are still recorded). |
| `BackgroundJobs:Enabled` | **`false` in the shipped `appsettings.json`** | **Must be `true` in UAT** (as for the SLA jobs) or nothing closes. The API logs a warning at startup when it is false. Needs `ConnectionStrings:TigerCsDatabase` for Hangfire. |
| `Genesys:Enabled` | `true` | Existing flag. |

### Migration

`AddChatbotInactivityAndCrmDocumentCopies` and `AddResolutionClosedForCustomerInactivity` (EF) — standalone idempotent script `AddChatbotInactivityAndCrmDocumentCopies.sql`
at the repo root (same convention as `AddGenesysScreenPopLaunches.sql`). **Additive only**: three nullable columns on
`TicketInteractions`, one filtered index, and the new `CrmDocumentDeliveryRequests` table (section 2). No data change.
Apply before deploying the new build. Rollback: `Down()` drops exactly those objects.

### UAT steps

1. Set `BackgroundJobs:Enabled=true` (and `Genesys:CustomerInactivityTimeoutMinutes=2` to shorten the test); apply the migration.
2. Create a chat ticket (`POST /api/genesys/tickets`, channel `LiveChat`). Note `ticketId`, `conversationId`.
3. **Timeout:** `PATCH` with `awaitingCustomerReply:true`. Response shows `awaitingCustomerReply:true` and an
   `inactivityDeadlineUtc`. Wait past the deadline + 1 min. Expect: ticket **Closed**; Ticket Details timeline shows
   Resolved→Closed by *System*; resolution outcome *Cancelled* with the reason text; audit entry
   `AutoCloseCustomerInactivity` carries the reason; exactly one "ticket closed" customer email (if email is enabled).
4. **Reply cancels:** new ticket, `true`, then within the window `false`. Wait past the deadline. Ticket stays open.
   Send `true` again — a new deadline from now.
5. **Retry does not restart:** `true`, wait 1 min, send `true` again. `inactivityDeadlineUtc` is unchanged.
6. **Human exclusions** (each: send `true`, then do the thing, wait past the deadline, ticket must stay open):
   request a human (`handoff.required:true`, or Request Human data action); assign the ticket to an agent; end the
   conversation (e.g. `ended` with `AiConnectionLost`-style disconnect). With a handoff open, sending `true` returns a
   `awaitingCustomerReplyNote` and no timer.
7. **Idempotent:** after step 3, restart the API and confirm no second resolution/audit/email appears.
8. **Restart survival:** step 3 but restart the API between sending `true` and the deadline; the ticket still closes.

## 2. CRM: send a copy of a document to the customer

Full REST contract, samples and error table: **`docs/Genesys/Document-Copy-API.md`**.
Endpoint: `POST /api/genesys/documents/send-copy`, `Idempotency-Key` header, Genesys→TigerGroupWeb→TigerCS service-account JWT.
Data action: `docs/Genesys/data-actions/11-send-document-copy.json`.

### Configuration / migration

`CrmDocuments:Enabled` ships **false**; set true for UAT. Same migration as above (table `CrmDocumentDeliveryRequests`,
unique `(CallerEmployeeId, IdempotencyKey)`). Email needs `EmailNotifications:Enabled=true` + SMTP credentials.

> ## ⚠ Document copies: PENDING REAL UAT VERIFICATION
> Not complete until a customer has completed verification, selected a unit and a document, and received exactly one email through the public Genesys route. Blocked on CRM: verification-session endpoints and file-route authentication — see `docs/Genesys/CRM-Required-Contracts.md` (exit criteria §8).

### Tiger CRM connection (update)

The document source is now Tiger CRM's `POST {Crm}/TicketingSystem/GetCustomerDocuments` (header `X-SECRET-KEY`, existing `Crm`
configuration) — see `docs/Genesys/Document-Copy-API.md` for the resolution flow, type mapping (Contract→`TigerContract`,
ReservationForm, RegistrationReceipt, UnitLayout→`Layout`), and the CRM-status → answer table. The public API gained only the optional
`crmLeadId` request field and `choiceKind` response field.

**UAT result: not verified on UAT.** The environment this was built in cannot reach the CRM host (the egress proxy resets the
connection) and holds no `Crm:SecretKey`, so no real request was made. What *was* verified is in the Verification section.

### Still open

* **How `fileUrl` is fetched with auth** is not stated in the CRM contract; TigerCS sends `X-SECRET-KEY` to the CRM origin only
  (relative / `~/` paths, or a host in `Crm:DocumentFileHosts`). The first UAT run settles it.
* **The verified contact needs a phone** (CRM identity comes from the buyer lookup by that phone).
* **Verification sessions over real CRM** still need CRM's unit/contact endpoints; OTP issue/check is outside TigerCS.
* **WhatsApp/SMS**: no integration (501). **TigerGroupWeb** must forward the route.

### UAT runbook (to run where CRM is reachable)

Config: `Crm:BaseUrl`, `Crm:SecretKey` (existing), `CrmDocuments:Enabled=true`, `EmailNotifications:Enabled=true` + SMTP credentials,
`Crm:Provider=Http`. Use a real UAT buyer (CRM customer with a phone, at least one unit/lead) and a mailbox you control as that
customer's CRM email.

1. Authenticate as the integration account; create a verification session for the buyer's unit/contact (`Otp`).
2. `POST /api/genesys/documents/send-copy` with `Idempotency-Key: uat-1`, body `{"verificationSessionId":"…","documentType":"ReservationForm"}`.
   Expect `200 Sent`, masked address, one email with the attachment. In the API log: one `GetCustomerDocuments` call, then one file fetch;
   no `CRM_*` warnings.
3. Repeat with the same key → `200`, `duplicate:true`, no second email, no new CRM call.
4. Repeat for `Contract`, `RegistrationReceipt`, `UnitLayout` (Layout returns a record id `LAYOUT-{leadId}`). For a lead with several
   documents expect `SelectionRequired`/`choiceKind:"Document"` first, then the chosen `recordId` with a new key.
5. A customer with several units whose verified unit is not matched: expect `choiceKind:"Unit"`, then `crmLeadId`.
6. Negative: another customer's `crmLeadId` → 403 `RECORD_OWNERSHIP_MISMATCH` and **no** CRM call; wrong `Crm:SecretKey` →
   `502 CRM_AUTHENTICATION_FAILED`; a type with nothing on record → `404 DOCUMENT_NOT_FOUND`.
7. Record the outcome of steps 2–6, especially whether the file fetch in step 2 succeeds (the open `fileUrl` question), in this document.

## Changed files

**Chatbot inactivity** — `TicketInteraction.cs` (timer state), `Ticket.cs` (`CloseForCustomerInactivity`),
`TicketLifecycleAppService.cs` (`CloseForCustomerInactivityAsync`), `GenesysTicketUpdateAppService.cs` + DTOs + `GenesysContracts.cs` +
`GenesysController.cs` (`awaitingCustomerReply`), new `ChatbotInactivityCloseAppService.cs`, `ChatbotInactivityCloseJob.cs`,
`BackgroundJobServiceCollectionExtensions.cs`, `Program.cs`, `GenesysOptions.cs`, `IGenesysRepositories.cs`/`GenesysRepositories.cs`,
`TicketInteractionConfiguration.cs`, DI in `InfrastructureServiceCollectionExtensions.cs`.

**Document copy** — new `GenesysDocumentsController.cs`; `Application/Modules/CrmDocuments/*` (service, DTOs, options, ports, email channel sender);
`CrmDocumentType.cs`, `CrmDocumentDeliveryRequest.cs`; `CrmDocumentDeliveryRequestConfiguration.cs`, `CrmDocumentDeliveryRepository.cs`, `TigerCsDbContext.cs`;
`CrmDocumentHttpGateway.cs`, `MockCrmDocumentGateway.cs`, `IntegrationsServiceCollectionExtensions.cs`; email attachments in `IEmailSender.cs`,
`SmtpEmailSender.cs`, `RecordingEmailSender.cs`.

**Both** — migration `20261007093644_AddChatbotInactivityAndCrmDocumentCopies` (+ snapshot), `AddChatbotInactivityAndCrmDocumentCopies.sql`, `appsettings.json`, docs and two data-action JSON files.

## Verification

See the final section of the hand-off message for the exact commands and results.

## Not verified / blocked

* Never run against SQL Server, Hangfire or a real SMTP server (no SQL Server in the build environment). Concurrency-token and query
  behaviour were verified on SQLite with the real EF model; the migration SQL was generated, not applied.
* The Hangfire recurring registration is not exercised by tests (the test host runs with `BackgroundJobs:Enabled=false`); the job body is.
* Real Tiger CRM, real WhatsApp, the TigerGroupWeb proxy and Genesys Architect flows were not touched.
