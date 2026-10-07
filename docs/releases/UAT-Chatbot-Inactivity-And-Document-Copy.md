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

### Missing document source / delivery integration — flagged

* **No document source exists.** Neither TigerCS nor the Tiger CRM contracts it uses store or generate contracts,
  reservation forms, unit layouts or registration receipts. The port `ICrmDocumentGateway` is complete and tested; its only
  `Crm:Provider=Http` implementation fails closed (503 `DOCUMENT_SOURCE_UNAVAILABLE`). **Against real CRM nothing can be sent
  until Tiger CRM publishes the list/download operations** (exact shape in the contract doc).
* **WhatsApp/SMS: no integration.** Answers 501 `DELIVERY_CHANNEL_NOT_INTEGRATED`. Email attachment works.
* **Verification sessions over real CRM are also blocked** by the unpublished CRM unit/contact endpoints
  (`UnimplementedCrmHttpGateway`), and OTP issuance/checking lives outside TigerCS.
* **TigerGroupWeb must forward the new route** (and `PATCH` field `awaitingCustomerReply` is part of the existing route).

### UAT steps (local/Mock only until the dependencies above exist)

1. Run the API with `Crm:Provider=Mock` (Development), `CrmDocuments:Enabled=true`, `EmailNotifications:Provider=Recording`.
2. As a CS Agent: `GET /api/crm/units/CRM-UNIT-1001`, `GET /api/crm/units/CRM-UNIT-1001/contacts`,
   `POST /api/verification-sessions` (contact `CRM-CONTACT-2001`, `verificationMethod: "Otp"`).
3. `send-copy` `ReservationForm` → `Sent`, masked address `a***@e***.com`. Repeat the identical request → `duplicate:true`, no second mail.
4. `Contract` → `SelectionRequired` (two choices); repeat with `recordId` (new key) → `Sent`.
5. `RegistrationReceipt` on unit 1001 → 404 `DOCUMENT_NOT_FOUND`. `recordId: "MOCK-CONTRACT-3"` → 403 `RECORD_OWNERSHIP_MISMATCH`.
6. Session with `ManualAgentConfirmation` → 403 `VERIFICATION_FAILED`. `deliveryChannel: "WhatsApp"` → 501.
7. Real UAT: with `Crm:Provider=Http` expect 503 `DOCUMENT_SOURCE_UNAVAILABLE` — that confirms the fail-closed behaviour.

## Changed files

**Chatbot inactivity** — `TicketInteraction.cs` (timer state), `Ticket.cs` (`CloseForCustomerInactivity`),
`TicketLifecycleAppService.cs` (`CloseForCustomerInactivityAsync`), `GenesysTicketUpdateAppService.cs` + DTOs + `GenesysContracts.cs` +
`GenesysController.cs` (`awaitingCustomerReply`), new `ChatbotInactivityCloseAppService.cs`, `ChatbotInactivityCloseJob.cs`,
`BackgroundJobServiceCollectionExtensions.cs`, `Program.cs`, `GenesysOptions.cs`, `IGenesysRepositories.cs`/`GenesysRepositories.cs`,
`TicketInteractionConfiguration.cs`, DI in `InfrastructureServiceCollectionExtensions.cs`.

**Document copy** — new `GenesysDocumentsController.cs`; `Application/Modules/CrmDocuments/*` (service, DTOs, options, ports, email channel sender);
`CrmDocumentType.cs`, `CrmDocumentDeliveryRequest.cs`; `CrmDocumentDeliveryRequestConfiguration.cs`, `CrmDocumentDeliveryRepository.cs`, `TigerCsDbContext.cs`;
`UnimplementedCrmDocumentGateway.cs`, `MockCrmDocumentGateway.cs`, `IntegrationsServiceCollectionExtensions.cs`; email attachments in `IEmailSender.cs`,
`SmtpEmailSender.cs`, `RecordingEmailSender.cs`.

**Both** — migration `20261007093644_AddChatbotInactivityAndCrmDocumentCopies` (+ snapshot), `AddChatbotInactivityAndCrmDocumentCopies.sql`, `appsettings.json`, docs and two data-action JSON files.

## Verification

See the final section of the hand-off message for the exact commands and results.

## Not verified / blocked

* Never run against SQL Server, Hangfire or a real SMTP server (no SQL Server in the build environment). Concurrency-token and query
  behaviour were verified on SQLite with the real EF model; the migration SQL was generated, not applied.
* The Hangfire recurring registration is not exercised by tests (the test host runs with `BackgroundJobs:Enabled=false`); the job body is.
* Real Tiger CRM, real WhatsApp, the TigerGroupWeb proxy and Genesys Architect flows were not touched.
