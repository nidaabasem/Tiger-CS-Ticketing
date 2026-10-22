# Live transcript, Unclassified tickets and AI request-type routing

Follow-up to [Integration-Audit-TG-LCS-20261021-0002.md](Integration-Audit-TG-LCS-20261021-0002.md). Everything here is **Ticketing-side and tested**;
nothing is verified against real Genesys events until the Architect changes in section 4 are made and the UAT in section 6 is run.

## 1. Live conversation transcript

### Contract (no new endpoint)

`PATCH /api/genesys/tickets/{ticketId}` gains one optional top-level field, **`transcript`**. Everything existing is unchanged (a request without it behaves exactly as before).

| Field | Type | Notes |
|---|---|---|
| `conversationId` | string, required | Must belong to `ticketId` (409 `genesys-conversation-ticket-mismatch` otherwise) |
| `transcript[]` | array, optional | One or many messages, in the order they happened |
| `transcript[].sender` | `Customer` \| `VirtualAgent` \| `HumanAgent` (\| `System`) | Anything else → 400, **nothing** in the request is applied |
| `transcript[].sentAtUtc` | ISO-8601 UTC | The real time of the message. Stored verbatim |
| `transcript[].body` | string, required, non-blank | Stored verbatim |
| `transcript[].senderName`, `senderId` | string, optional | Display name / Genesys participant or user id. Stored verbatim |
| `transcript[].externalMessageId` | string ≤ 64, **strongly recommended** | Stable Genesys message id — the de-duplication key |

Rules:

* **Additive and ordered.** Messages are appended in delivered order and never edited or removed. The conversation is **not** ended and no ticket status, owner, department or SLA moves because messages arrived.
* **Idempotent.** A message is already stored when its `externalMessageId` matches, or — when it has none — sender + `sentAtUtc` + body match. A retry, an overlapping batch, or the same message twice in one request stores it once.
* **Final transcript reconciles.** `ended.transcript` may repeat everything already sent live: only messages not yet stored are added, the live rows are untouched, and the end time/reason are still write-once. A line first sent without an id and later repeated with one is recognised as the same line. Messages delivered after `ended` are still appended.
* **Not a handoff signal.** A transcript — bot or human lines — never creates, assigns or cancels human work. Only `handoff` does.
* **First Response.** Only a `HumanAgent` line records First Response, at that line's `sentAtUtc`, write-once. `VirtualAgent`, `Customer` and `System` lines never do.
* **A human sees the AI conversation first.** Ticket Details → Interactions shows messages as soon as they are stored (no more "not ended yet" gap), so an agent who accepts the work reads everything said so far before replying.
* **Validation first.** The whole transcript (live and final) is validated before anything — including a classification or handoff in the same request — is written.
* **Concurrency.** Two deliveries racing for the same position: one wins; the other answers `409 concurrency-conflict` with "send it again" and stored nothing. Retrying is always safe.
* The response's `transcriptMessageCount` is the **total** now stored for the conversation.

### Examples

Request (customer + bot, no ids needed for the call to work, but send them):
```json
PATCH /api/genesys/tickets/123
Authorization: Bearer <service-account token>

{ "conversationId": "c5a1b2…",
  "transcript": [
    { "sender": "Customer",     "senderName": "Ahmed Ali", "sentAtUtc": "2026-10-21T09:00:03Z",
      "body": "How does the handover process work?", "externalMessageId": "msg-001" },
    { "sender": "VirtualAgent", "senderName": "Tiger Bot", "senderId": "bot-1", "sentAtUtc": "2026-10-21T09:00:05Z",
      "body": "Handover happens after the final payment. Shall I send the checklist?", "externalMessageId": "msg-002" } ] }
```
Response `200`:
```json
{ "outcome": "Applied", "conversationId": "c5a1b2…", "ticketId": 123, "ticketNumber": "TG-LCS-20261021-0002",
  "ticketStatus": "Open", "conversationEnded": false, "transcriptMessageCount": 2,
  "handoffStatus": null, "ticketAgentHandoffId": null }
```
Human joins and speaks (First Response is recorded at `09:03:10Z`):
```json
{ "conversationId": "c5a1b2…",
  "routing": { "agentId": "<genesys user id>", "agentName": "Layla" },
  "transcript": [ { "sender": "HumanAgent", "senderName": "Layla", "senderId": "<genesys user id>",
                    "sentAtUtc": "2026-10-21T09:03:10Z", "body": "Hello, this is Layla.", "externalMessageId": "msg-005" } ] }
```
Conversation ends — repeating earlier messages is fine:
```json
{ "conversationId": "c5a1b2…",
  "ended": { "endedAtUtc": "2026-10-21T09:12:00Z", "endReason": "CustomerDisconnect",
             "transcript": [ { "sender": "Customer", "sentAtUtc": "2026-10-21T09:00:03Z", "body": "How does…", "externalMessageId": "msg-001" },
                             { "sender": "VirtualAgent", "sentAtUtc": "2026-10-21T09:00:05Z", "body": "Handover happens…", "externalMessageId": "msg-002" } ] } }
```
Errors: `400` unknown sender / blank body (nothing stored); `404` unknown conversation; `409` ticket mismatch or concurrent delivery; `401` no token; `403` role outside the Genesys service-account policy.

## 2. Unclassified tickets

* **Tickets → Classification: Unclassified** (query `GET /api/tickets?unclassified=true`) lists tickets that have **no Request Type**. It is an ordinary filter on the queue: the caller's existing department visibility applies unchanged, it combines with every other filter, and it survives paging and the remembered view.
* **Ticket Details → Classify** (shown for a ticket with no Category that is not Closed, to roles that are not read-only) posts the existing `POST /api/tickets/{id}/classification`. It offers the ticket's **own department's** active categories, request types that have a **published workflow**, and the current priority or a more urgent one (lowering it needs the existing approved downgrade). The API still decides: department membership / role, concurrency (`rowVersion`), already-classified, closed — and answers 403/404/409/422, which the page shows.
* If the Genesys AI already set the Request Type, the form only adds Category and Priority and does not send a second Request Type.
* **No handoff.** Being unclassified, appearing in this filter, or being classified never creates, requests or cancels human work. Human work exists only from an explicit Genesys `handoff`.

## 3. AI request-type integration (unchanged contract, verified)

The existing `requestType` on `POST /api/genesys/tickets` and `PATCH /api/genesys/tickets/{id}` is the only classification input; Ticketing contains **no** keyword or free-text classification. Verified by tests: the type is validated (exists, active, department active, workflow published; ambiguous names refused), the ticket moves to the type's department (`OriginatingDepartmentId` untouched), the department's automatic assignment runs, and the type's SLA policy is applied to the running period **without restarting the clock**; the same type again is a no-op; a different type replaces it only while no person has worked the ticket (otherwise 409); an invalid type is 422 with nothing written. Identifiers per environment: [RequestType-Mapping-For-Genesys.md](RequestType-Mapping-For-Genesys.md).

## 4. Required Genesys Architect / AI-flow changes

1. **Send messages live.** After each customer message and each bot/agent reply, call the existing ticket-update data action with the new `transcript` array (one message per call is fine; batching is fine). Always include `externalMessageId` (the Genesys message id), the real `sentAtUtc`, and the correct `sender`. Add the `transcript` field to the data action's request template/contract (the field is optional, so existing actions keep working).
2. **Sender mapping.** Bot → `VirtualAgent` (never `HumanAgent`); customer → `Customer`; live agent → `HumanAgent` with `senderName`. Do not put the bot id in `agentId` or `routing.agentId`.
3. **Keep the final transcript** in `ended.transcript`; it may be the full conversation — duplicates are ignored.
4. **Request types.** Build the intent → `requestTypeId` data table from the environment export ([mapping doc](RequestType-Mapping-For-Genesys.md)); send `requestType` as soon as the intent is known and again if it changes. If there is no mapping, send nothing — the ticket stays Unclassified for an employee.
5. **Handoff only on explicit exits** (unchanged, see the audit document). Do not infer a handoff from a missing request type or from the conversation ending.
6. Retry on `409 concurrency-conflict` / network errors with the same body; it is idempotent.

## 5. Deployment

No database migration, no new table/column/index (`WorkflowEventType.RequestTypeReclassified` already shipped; the unclassified filter is a `RequestTypeId IS NULL` predicate on existing columns).

1. Deploy the API and Web together (the Web sends `unclassified=true`; an older API would ignore it and show every ticket, so deploy the API first or together).
2. Smoke test: `GET /api/tickets?unclassified=true`; `PATCH` a test ticket with a one-message `transcript`.
3. Export the request-type ids for the environment and configure the Genesys data table.
4. Change the Genesys flow (section 4); Ticketing is safe before and after (the field is optional).

Rollback: redeploy the previous build. Transcript rows written by the new build are ordinary `TicketInteractionMessages` rows the old build reads; the only new audit action is `GenesysTranscriptAppended`.

## 6. UAT

| # | Step | Expected |
|---|---|---|
| 1 | Chat with the bot; each message is pushed with `transcript`. Open Ticket Details → Interactions while the chat is live. | Messages appear (customer / Virtual agent) with real times; conversation shown as Active; Handled by *AI*; Handoff requested *No*. |
| 2 | Re-send the same push (simulate a retry). | Count unchanged; no duplicate lines; no extra audit entry. |
| 3 | Ask for a person (`handoff` CustomerRequestedHuman), then have an agent accept in Pending Interactions. | The agent sees the earlier AI conversation before replying; First Response still empty. |
| 4 | Agent sends the first reply (pushed as `HumanAgent`). | First Response recorded at that message's time; bot lines never did. |
| 5 | End the chat with the full `ended.transcript`. | No duplicates; live rows unchanged; end time recorded; ticket status unchanged. |
| 6 | Send a message with `sender":"Robot"`. | 400; nothing stored; any `requestType`/`handoff` in the same call is not applied. |
| 7 | Create a ticket without `requestType` (department A) and one with a valid type; sign in as a member of department A, then of department B. | A's member: *Classification: Unclassified* lists only the first ticket; B's member does not see it. |
| 8 | Open the unclassified ticket as a department member → *Classify* (category, request type, priority). | Ticket classified in place; leaves the filter; no handoff row; SLA clock not restarted; request type's SLA applied. |
| 9 | Read-only role (Reporting User / Chairman-CEO) opens an unclassified ticket. | No Classify control; the API refuses a direct call. |
| 10 | AI sends `requestType` for an intent. | Ticket moves to the type's department, auto-assignment runs, `OriginatingDepartmentId` unchanged, SLA policy applied without restart. |
| 11 | Real Genesys events through the configured flow. | **Only this proves end-to-end behaviour** — repeat 1–5 and 10 with live Genesys. |

## 7. Remaining external dependencies

* Genesys Architect changes in section 4 (not done, not verifiable from this repository).
* The intent → Request Type data table (needs the per-environment export and a business decision on which intents map where).
* Real Genesys event samples to confirm: message ids are stable across retries, the bot is never sent as an agent, and the `trigger` values on handoff.
