# Customer Email Notifications

**Status: implemented (MVP).** Customer-facing email for the ticket lifecycle,
delivered through the Microsoft 365 SMTP account and the existing
transactional Outbox / Hangfire dispatcher (ADR-0013, ADR-0015). Ships
**switched off**; UAT/Production enable it by configuration only.

## 1. What is sent, and when

| Event | Trigger | Subject | Notification type |
|---|---|---|---|
| Ticket created | `POST /api/tickets` and `POST /api/genesys/tickets` (both go through `TicketCreationAppService`) | `Your request has been received – Ticket {TicketNumber}` | `Acknowledgement` |
| Ticket resolved | `POST /api/tickets/{id}/resolution` | `Your request has been resolved – Ticket {TicketNumber}` | `Resolved` |
| Ticket closed | `POST /api/tickets/{id}/close` | `Your request has been closed – Ticket {TicketNumber}` | `Closed` |
| Ticket reopened | `POST /api/tickets/{id}/reopen` | `Your request has been reopened – Ticket {TicketNumber}` | `Reopened` |

Every email carries the ticket number (the existing `TG-{DEPT}-{yyyyMMdd}-{NNNN}`
reference), the customer's name when one is known, the event date, the
Tiger Properties header/footer/signature, a plain-text body and a responsive
HTML body. The "created" email adds the request type (or category) name; the
"resolved" email adds a customer-friendly status (`Resolved`, `Cancelled`,
`Reviewed – no further action required`, `Merged with an existing request`).

**Never rendered:** database ids, employee ids, queue/workflow ids, SLA state,
escalation level, the free-text request summary, internal ticket notes. The
agent's resolution note is included **only** when
`EmailNotifications:IncludeResolutionNote` is `true` (default `false`).

### Events intentionally *not* emailed

- **Assignment, auto-assignment and department transfer** — internal routing.
  Nothing is sent, by design; see §7 for how to add them later.
- **Pending Customer / Pending Third Party status changes** and any other
  `POST /api/tickets/{id}/status` change.
- **Classification, priority changes, approvals, notes, attachments, SLA
  warnings/breaches/escalations** (`NotificationType.Warning/Breach/Escalation`
  exist in the enum from the data dictionary but have no producer).
- **Genesys `PATCH /api/genesys/tickets/{id}`** (handoff, agent, conversation
  end) — internal.

## 2. Architecture (how it fits the existing code)

```
TicketCreationAppService ─┐                       ┌─ TicketAcknowledgementHandler
TicketLifecycleAppService ─┤ IOutboxWriter        │  TicketResolvedNotificationHandler
  Resolve / Close / Reopen │ (same transaction)   │  TicketClosedNotificationHandler
                           ▼                      │  TicketReopenedNotificationHandler
                    OutboxMessages ── Hangfire ───┤        (CustomerTicketEmailHandler base)
                    (Pending)   OutboxDispatchJob │              │
                                                  │   CustomerContactResolver  (name + email from ticket data)
                                                  │   CustomerEmailTemplates   (subject / text / HTML)
                                                  │              ▼
                                                  │   ICustomerEmailSender  →  CustomerEmailSender
                                                  │        (Enabled? valid recipient? never throws; logs outcome)
                                                  │              ▼
                                                  │   IEmailSender  →  SmtpEmailSender (Microsoft 365 SMTP)
                                                  └                 →  RecordingEmailSender (tests / dev only)
```

- **Reliability / transaction behaviour.** The lifecycle services write the
  Outbox row inside the same database transaction as the state change. A
  rolled-back create/resolve/close/reopen leaves no event, so a customer can
  never receive an email for an operation that failed. Delivery happens later
  in the Outbox dispatch job, so an SMTP outage can never fail or roll back a
  ticket operation. Transient SMTP failures are retried with backoff
  (`Notifications:Outbox`, default 5 attempts); permanent ones are
  dead-lettered and audited.
- **Idempotency.** Each occurrence has one idempotency key
  (`Ticket:{id}:{EventType}:v1:cycle{ReopenCount}`), one `Notifications` row
  per `(OutboxMessageId, NotificationType)` (unique index), and the
  acknowledgement additionally guards on `Ticket.AcknowledgementSentAtUtc`.
  A redelivered message never sends twice.
- **Recipient trust.** The address comes only from persisted ticket data —
  never from a request parameter, so no API caller can direct a notification
  to an arbitrary mailbox.

### Where the customer email comes from

No new column was added. Two existing fields are consulted, in order:

1. `TicketRequesterSnapshot.SnapshotContactChannel` — the CRM-verified
   contact captured at creation. It is an untyped phone-or-email value and is
   used only when it is unambiguously a single bare email address.
2. `TicketInteraction.CustomerEmail` — the email Genesys supplied with the
   inquiry (originating interaction first, then the latest interaction that
   carries one).

The greeting name comes from `SnapshotContactDisplayName`, then
`Ticket.CrmBuyerCustomerName`, then the interaction's `CustomerName`.

## 3. Skip / failure behaviour

| Situation | Ticket operation | Notification row | Outbox message | Audit action |
|---|---|---|---|---|
| `EmailNotifications:Enabled = false` | succeeds | `Skipped` (`NotificationsDisabled`) | `Processed` | `NotificationSkipped` |
| No email on the ticket (phone-only customer, unverified ticket) | succeeds | `Skipped` (`NoCustomerEmail`) | `Processed` | `NotificationSkipped` |
| Value present but not a valid address | succeeds | `Skipped` (`InvalidCustomerEmail`) | `Processed` | `NotificationSkipped` |
| Event older than `MaxNotificationAgeHours` when first handled | succeeds | `Skipped` (`EventTooOld`) | `Processed` | `NotificationSkipped` |
| SMTP transient failure (service unavailable, busy, socket/TLS) | succeeds | `Failed`, retried | `Pending`, backoff | `NotificationDeliveryFailed` / `NotificationRetryScheduled` |
| SMTP permanent failure (mailbox rejected, auth refused) | succeeds | `DeadLettered` | `DeadLettered` | `NotificationDeadLettered` |
| Sent | succeeds | `Sent` | `Processed` | `NotificationDeliverySucceeded` |

`Skipped` is deliberately distinct from `DeadLettered`: most phone-only
tickets have no email, and that expected outcome must not inflate the
dead-letter count operators are alerted on.

**Logging.** One line per attempt from `CustomerEmailSender`: notification
type, ticket number, masked recipient (`a***@example.com`), outcome, reason,
correlation id. `SmtpEmailSender` logs the exception type and SMTP status
code. Never logged or persisted: the SMTP password, the subject, the body,
or an unmasked address.

## 4. Configuration keys

Section `EmailNotifications` (bound to `EmailNotificationOptions`):

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `false` | Master switch. `false` = nothing is sent; operations unchanged. |
| `Provider` | `Smtp` | `Smtp` (Microsoft 365) or `Recording` (in-memory; Development/Testing only). |
| `SmtpHost` | `smtp.office365.com` | |
| `SmtpPort` | `587` | |
| `EnableSsl` | `true` | STARTTLS on 587. |
| `Username` | — | `no_reply_tiger@tigergroup.ae` |
| `Password` | — | **Secret. Never in a committed file.** |
| `FromEmail` | — | `no_reply_tiger@tigergroup.ae` |
| `FromName` | `Tiger Properties` | |
| `TimeoutSeconds` | `30` | SMTP send timeout (minimum 5). |
| `MaxNotificationAgeHours` | `24` | Stale-backlog guard; `0` disables. |
| `IncludeResolutionNote` | `false` | Quote the agent's resolution note in the "resolved" email. |

The dispatcher itself is configured by the existing `BackgroundJobs:Enabled`
and `Notifications:Outbox` (`MaxAttempts`, `BaseRetryDelay`, `BatchSize`,
`PollIntervalMinutes`) sections — **customer emails are only delivered while
`BackgroundJobs:Enabled` is `true`** (docs/DEV-SETUP.md §9). With it `false`,
Outbox rows accumulate as `Pending`; the `MaxNotificationAgeHours` guard then
skips anything older than a day once the job is switched on.

Shipped `src/TigerCS.Api/appsettings.json`:

```json
"EmailNotifications": {
  "Enabled": false,
  "Provider": "Smtp",
  "SmtpHost": "smtp.office365.com",
  "SmtpPort": 587,
  "EnableSsl": true,
  "Username": "no_reply_tiger@tigergroup.ae",
  "Password": "",
  "FromEmail": "no_reply_tiger@tigergroup.ae",
  "FromName": "Tiger Properties",
  "MaxNotificationAgeHours": 24,
  "IncludeResolutionNote": false
}
```

## 5. Supplying the SMTP secret (UAT / Production)

The password is read through ordinary configuration binding, so any standard
ASP.NET Core secret source works. Use **environment variables** on the host
(or the platform's secret store mapped to them), with `__` as the section
separator:

```bash
EmailNotifications__Enabled=true
EmailNotifications__Password='<the no_reply_tiger mailbox password>'
# Only if they differ from appsettings.json:
# EmailNotifications__Username=...
# EmailNotifications__FromEmail=...
BackgroundJobs__Enabled=true          # the dispatcher must run
```

Startup refuses to run (with a message naming the missing *key*, never a
value) when `Enabled = true` with `Provider = Smtp` and any of `SmtpHost`,
`Username`, `Password` or `FromEmail` is missing, or when `Provider =
Recording` is enabled outside Development/Testing.

Local development: `dotnet user-secrets set "EmailNotifications:Password" "..."`
from `src/TigerCS.Api`, or keep the shipped Development override
(`Provider = Recording`, `Enabled = true`) which logs deliveries and sends
nothing.

Requirements on the Microsoft 365 side (outside this repository): the
`no_reply_tiger@tigergroup.ae` mailbox must have **Authenticated SMTP
(SMTP AUTH)** enabled and, if the tenant enforces MFA/security defaults, an
app password or a mailbox exempt from that policy. An authentication
refusal surfaces as a permanent failure (`SMTP ClientNotPermitted`) in
`OutboxMessages.LastError`.

## 6. Disabling customer notifications

Set `EmailNotifications__Enabled=false` (or omit the section — `false` is the
default) and restart. Every ticket operation continues unchanged; queued
events are recorded as `Skipped` with reason `NotificationsDisabled` and are
never sent later. To stop delivery *and* stop the Outbox from being drained,
set `BackgroundJobs__Enabled=false` as well.

## 7. Extending later (assignment / transfer, other channels)

- **A new customer email** = a new `OutboxEventTypes` constant written by the
  owning app service, a template method on `CustomerEmailTemplates`, and a
  ~20-line subclass of `CustomerTicketEmailHandler` registered as an
  `IOutboxEventHandler`. No change to the sender, the SMTP adapter, the
  resolver or the dispatcher. Assignment/transfer would follow this exact
  pattern from `TicketAssignmentAppService`.
- **Another provider** (Graph API, a relay) = another `IEmailSender`
  implementation and a `Provider` value.
- **Another channel** (SMS) = a sibling of `ICustomerEmailSender`;
  `NotificationChannel` already reserves the enum.

## 8. UAT verification steps

1. Configure UAT as in §5 (`Enabled=true`, password via environment,
   `BackgroundJobs__Enabled=true`). Confirm the API starts; a missing key
   fails startup with an explicit message.
2. Create a ticket for a CRM contact whose contact channel is an email
   address you control (or via `POST /api/genesys/tickets` with
   `customerEmail`). Within `Notifications:Outbox:PollIntervalMinutes`
   (default 1) the acknowledgement arrives with the ticket number, name,
   request type and date; `Tickets.AcknowledgementSentAtUtc` is set.
3. Assign, start work and resolve the ticket → "resolved" email with the
   status line. Close → "closed" email. Reopen → "reopened" email. Exactly
   one email per transition.
4. Create a ticket for a phone-only contact → no email;
   `Notifications.DeliveryStatus = 5 (Skipped)`, `AuditEntries.Action =
   NotificationSkipped` with `Reason=NoCustomerEmail`; the ticket operations
   all succeed.
5. Negative path: temporarily set a wrong password → ticket operations still
   succeed; the notification dead-letters with `SMTP ClientNotPermitted` in
   `OutboxMessages.LastError` and no secret in any log line. Restore the
   password.
6. SQL spot-checks:
   ```sql
   SELECT EventType, Status, Attempts, LastError FROM OutboxMessages ORDER BY OccurredAtUtc DESC;
   SELECT TicketId, NotificationType, DeliveryStatus, RecipientAddress, RetryCount FROM Notifications ORDER BY CreatedAtUtc DESC;
   SELECT Action, EntityId, AfterValue FROM AuditEntries WHERE Action LIKE 'Notification%' ORDER BY OccurredAtUtc DESC;
   ```
   `NotificationType`: 1 Acknowledgement, 5 Resolved, 6 Closed, 7 Reopened.
   `DeliveryStatus`: 1 Pending, 2 Sent, 3 Failed, 4 DeadLettered, 5 Skipped.

No database migration is required by this feature: the new enum values map
onto the existing `tinyint` columns and EF reports no pending model changes.
