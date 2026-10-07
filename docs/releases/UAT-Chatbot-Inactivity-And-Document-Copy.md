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

> ## ⚠ Document copies: PENDING REAL UAT VERIFICATION — disabled in Production
> Phase 1 (CRM buyers requesting their own documents) is built and passes 3 100+ automated tests, including the whole
> lookup → unit → email OTP → session → document → download → one-email flow through the real host against a stub CRM.
> **It has not been run against real CRM, the real file routes, real SMTP or the public Genesys route.** Complete only when a real
> customer completes verification, selects a unit and a document and receives exactly one email through the public route
> (exit criteria: `docs/Genesys/CRM-Required-Contracts.md` §8). `CrmDocuments:Enabled=false` ships; a Production host refuses to
> start with it enabled unless `CrmDocuments:AllowInProduction=true` is also set.

### What Phase 1 delivers (see `docs/Genesys/Document-Copy-API.md`)

* **Buyer lookup → cache:** the verification unit/contact cache is filled from the real `GetBuyerByPhone` (unit id as `CrmUnitId`,
  contact id `"{customerId}-{unitId}"`, type `Buyer` — `customerType=1` is not read as Owner). Ambiguous customers and multiple units
  are explicit results. Agent-desk, tenant and representative flows are unchanged.
* **Email OTP:** code to the address CRM returns (no destination field anywhere); bound to integration account + CRM customer + unit/lead;
  10-minute expiry, 5 attempts, 3 sends ≥ 60 s apart, 5 challenges/customer/hour, single use; stored only as a salted HMAC. `Otp` can no
  longer be asserted on `POST /api/verification-sessions`; `send-copy` accepts only a session carrying the recorded proof.
* **Delivery:** CRM `GetCustomerDocuments` → choices when `selectionRequired` → download with the CRM secret (CRM origin only; other
  hosts fetched without it) → type/extension from the file's own bytes (an extension-less "Layout Plan" becomes `.png`/`.pdf` as it really is)
  → one email; idempotency and delivery audit unchanged.

### UAT result (this session)

| Check | Result |
|---|---|
| Automated: full suite | **3 113 passed, 0 failed** (Release build, 0 warnings) |
| Automated: whole flow through the real host over a stub CRM (happy path, wrong/locked/reused OTP, another account, another customer's lead, other unit, multiple documents, download 401/403/404/5xx/HTML/executable/off-host, idempotent retries) | passed |
| Concurrency rules (single-use code, attempt budget, resend race, one session per challenge) on a real relational engine (SQLite, real EF model) | passed |
| Real CRM `GetBuyerByPhone` / `GetCustomerDocuments` / file download | **not run** — `tigercrm.tigergroup.ae:8014` is unreachable from this environment and no `Crm:SecretKey` is available |
| Real SMTP delivery of the code and the document | **not run** |
| Public Genesys route | **not reachable**: unauthenticated POSTs to `tigergroup.ae` return `401` for the existing `/api/genesys/tickets` but `404` for `/api/genesys/verification/*` and `/documents/send-copy` (not deployed and/or not forwarded by TigerGroupWeb — this probe cannot tell which) |
| File-route authentication | **unconfirmed** — run `docs/Genesys/uat/verify-crm-file-auth.sh` where CRM is reachable |

### Remaining dependencies

1. **TigerGroupWeb** must forward `/api/genesys/verification/*` and `/api/genesys/documents/send-copy` (and the new build must be deployed).
2. **File storage:** how the `Uploads` routes authenticate (public / cookie / secret header) — unknown; cookie auth cannot work. A separate file host is possible only as https in `Crm:DocumentFileHosts` and is fetched **without** the CRM secret.
3. **CRM data:** buyer email must be the customer's own and current; `mobileNumber` must be findable by `GetBuyerByPhone?phoneNumber=+971…`; `unitNumber` non-empty.
4. **Config:** `CrmDocuments:OtpCodePepper` (secret), `EmailNotifications:Enabled=true` + SMTP credentials, `Crm:SecretKey`, `BackgroundJobs:Enabled` (inactivity feature), both migrations applied.

### UAT runbook (where CRM, SMTP and the public route are reachable)

1. Apply `AddChatbotInactivityAndCrmDocumentCopies.sql` and `AddCustomerOtpVerification.sql`; set the §4 config; `CrmDocuments:Enabled=true` (UAT only).
2. `uat/verify-crm-file-auth.sh Contract` (and `UnitLayout`) with a real buyer's `CRM_CUSTOMER_ID` / `CRM_LEAD_ID`: record the file-route behaviour with and without the secret.
3. Through the **public** route as the integration account: `buyer-lookup` (the buyer's phone) → expect the real units and a masked email; `otp/send` with the chosen `crmUnitId` → the code arrives in the buyer's mailbox; `otp/verify` → `Verified` + `verificationSessionId`.
4. `send-copy` `Contract` → if CRM lists several: `SelectionRequired` → choose → `Sent`; check the mailbox has **exactly one** document email with the right file and extension; repeat the identical call (same key and a new key) → `duplicate:true`, no second email.
5. `UnitLayout` for a lead whose plan has no extension in its name → attachment gets the true extension.
6. Negatives: wrong code (`OTP_INVALID`, attempts fall), 5 wrong codes (`OTP_LOCKED`), reuse (`OTP_ALREADY_USED`), expired code (wait 10 min), another customer's `crmLeadId` and the same customer's other unit (`RECORD_OWNERSHIP_MISMATCH`, no CRM document call in the log), wrong `Crm:SecretKey` (`CRM_AUTHENTICATION_FAILED`), a type with nothing on record (`DOCUMENT_NOT_FOUND`).
7. Record every result here. **Only then** consider `CrmDocuments:AllowInProduction`.

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
