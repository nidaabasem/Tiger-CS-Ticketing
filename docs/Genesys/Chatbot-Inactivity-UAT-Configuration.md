# Chatbot inactivity closure — UAT configuration, PATCH payloads, reopen policy

Outcome stays **Cancelled** ("the customer's request was not resolved"). Behaviour of the timer is in
`docs/releases/UAT-Chatbot-Inactivity-And-Document-Copy.md`; this page is what to change and what to send.

## 1. Deployment order

1. Database (idempotent; run in this order, each is safe to re-run):
   1. `AddChatbotInactivityAndCrmDocumentCopies.sql` — timer columns on `TicketInteractions`, `CrmDocumentDeliveryRequests`.
   2. `AddResolutionClosedForCustomerInactivity.sql` — `TicketResolutions.ClosedForCustomerInactivity bit NOT NULL DEFAULT 0`.
   Check first: `SELECT TOP 1 MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC` should be
   `20261005072511_AddCollectionsReminders` (or later if an earlier script was already run).
2. Deploy the API build (the new column is read by every Ticket Details/Customer History call, so the SQL must come first).
3. Set the configuration below, restart the API, confirm the startup log has **no**
   "BackgroundJobs:Enabled is false" warning.

## 2. Configuration changes (API host environment variables)

| Setting (env var) | UAT value | Why |
|---|---|---|
| `BackgroundJobs__Enabled` | `true` | **Ships `false`.** Without it Hangfire does not run and nothing is ever closed (timers are still recorded). |
| `ConnectionStrings__TigerCsDatabase` | existing UAT connection string | Hangfire uses the same database. |
| `Genesys__Enabled` | `true` | Existing flag; every Genesys route answers 503 when off. |
| `Genesys__CustomerInactivityTimeoutMinutes` | `5` for acceptance; `2` while testing | Closure fires when the wait is **strictly more than** this many minutes, within ~1 minute after (job runs every minute). `0` switches automatic closure off. |
| `Ticketing__ReopenWindowDays` | unchanged (`7`) | A reopen of an inactivity closure uses the same window, measured from closure. |
| `CrmDocuments__Enabled` | `false` until document copy is unblocked | Document copy is **not UAT-ready** (see `CRM-Document-Operations-Requirements.md`). |

No new configuration key is introduced by the reopen change.

## 3. Genesys PATCH payloads

All go to `PATCH https://tigergroup.ae/api/genesys/tickets/{ticketId}` (Genesys → TigerGroupWeb → TigerCS,
`Content-Type: application/json`). `ticketId` and `conversationId` come from the `POST /api/genesys/tickets`
response / the conversation. The bot sends these from the Architect flow — data action
`10-awaiting-customer-reply.json`. Send only what changed.

**Start the timer** — the chatbot has just asked the customer a question and is waiting:

```json
{ "conversationId": "5f1b0c2e-8d3a-4f6e-9a77-1c2d3e4f5a6b", "awaitingCustomerReply": true }
```

Response (shape from the code; not captured against a live host):

```json
{
  "outcome": "Applied", "conversationId": "5f1b0c2e-8d3a-4f6e-9a77-1c2d3e4f5a6b",
  "ticketId": 1042, "ticketNumber": "TG-CS-20261008-0007", "ticketStatus": "Open",
  "conversationEnded": false, "transcriptMessageCount": 0, "handoffStatus": null, "ticketAgentHandoffId": null,
  "awaitingCustomerReply": true, "inactivityDeadlineUtc": "2026-10-08T09:35:00Z", "awaitingCustomerReplyNote": null
}
```

* Sending `true` again while the timer runs changes nothing and returns the same `inactivityDeadlineUtc`
  (a retried webhook can never postpone the deadline).
* If a person owns the ticket, a human handoff is requested/pending, the conversation ended, or the ticket is
  already closed, the response has `"awaitingCustomerReply": false` and an `awaitingCustomerReplyNote` saying why.

**Cancel the timer** — the customer replied:

```json
{ "conversationId": "5f1b0c2e-8d3a-4f6e-9a77-1c2d3e4f5a6b", "awaitingCustomerReply": false }
```

Response: `"awaitingCustomerReply": false`, `"inactivityDeadlineUtc": null`. The chatbot's next question sends `true`
again and starts a new timer measured from then.

**Omitting the field** leaves a running timer untouched (other PATCH parts — transcript, handoff, end — are unaffected).

## 4. Reopen policy (what changed)

* A ticket closed by the timer (`Closed`, outcome `Cancelled`, resolution flagged `ClosedForCustomerInactivity`)
  can be reopened to **InProgress** with `POST /api/tickets/{ticketId}/reopen` by **CS Agent, CS Supervisor, CS Manager**;
  **System Administrator** via the central override. Reason, target department and the current `rowVersion` are required, as for any reopen.
* **Only this closure.** A Cancelled set by a person, and every Rejected/Duplicate, stay final. The flag is set
  by the server alone (the system closure path); no request field can set it.
* **Original closure audit is kept.** The resolution is archived (not edited or deleted), the `AutoCloseCustomerInactivity`
  audit entry and the system status-history rows remain, and the `Reopen` audit entry records
  `ResolutionOutcome=Cancelled;ClosedForCustomerInactivity=True`.
* The 7-day window and the request type's Reopen switch still apply. The **Reopen Approval** path is unchanged
  (Resolved only). The ticket shows the Reopen button (`isReopenEligible`) to the same roles for this closure.
* The conversation cannot close the ticket again: the interaction's closure marker is write-once, so a later
  `awaitingCustomerReply: true` for that conversation is refused ("already closed") and the job has nothing to find.
  Exactly one `TicketClosed` and one `TicketReopened` customer event result.

## 5. UAT steps for the reopen change

1. Close a chat ticket by timeout (steps in the release note). Open Ticket Details as a CS Agent: **Reopen** is offered.
2. Reopen with a reason and a department → status InProgress; timeline shows Closed → InProgress by the agent,
   the earlier Resolved/Closed rows by *System* are still there.
3. Repeat as CS Supervisor and CS Manager on other timed-out tickets; as Department Employee / Department Head / GM:
   no button, and `POST …/reopen` answers 403.
4. Cancel a ticket by hand (agent resolve as Cancelled, then close): **no** Reopen offered; the API answers 422
   `resolution-outcome-not-reopenable`.
5. After step 2, send `awaitingCustomerReply: true` for the same conversation: `awaitingCustomerReply:false` with the
   "already closed" note; wait past the deadline: ticket stays InProgress; one closed and one reopened email in total.
