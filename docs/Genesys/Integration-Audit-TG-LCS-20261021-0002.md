# Genesys AI ↔ Tiger-CS-Ticketing — integration audit and fix

Trigger: ticket **TG-LCS-20261021-0002** — a customer chatting normally with Tiger Bot about the handover process saw
"System — asked for a human agent", "Waiting for agent", category *Unclassified*, handoff reason
"Awaiting classification: no request type was supplied", and the ticket in "Leasing Customer Services Queue".

> **Status.** The Ticketing-side fixes below are implemented and covered by automated tests. **End-to-end behaviour with
> Genesys is not verified**: no real Genesys events were available to this audit. Section 5 ("Who changes what") lists what Genesys must send and
> section 8 how to prove it in UAT.

## 1. Root cause of TG-LCS-20261021-0002

The customer never asked for a human. Reading the code path (`GenesysInquiryIngestionAppService.ClassifyOrQueueAsync`):

1. Genesys created the ticket with `POST /api/genesys/tickets` and **no `requestType`** (the AI had not classified yet — normal at conversation start).
2. Ingestion treated "no request type" as work for a human: it called `GenesysRequestTypeClassificationAppService.AwaitClassificationAsync`, which
   called the real handoff service with `trigger = RoutingDecision` and `reason = "Awaiting classification: no request type was supplied."` (config
   `Genesys:HumanQueueForUnclassified`, default **true**).
3. That wrote a `TicketAgentHandoff` row in status **WaitingForAgent** and a `HandoffRequested` workflow event with a null actor.
4. Ticket Details rendered every `HandoffRequested` event as the fixed sentence **"asked for a human agent"** with actor **System**, the Interactions tab
   showed the row as **"Waiting for agent"** with the reason text, and Pending Interactions listed it as human work.
5. "Leasing Customer Services Queue" is not a bug: no owner is set, so the UI labels the ticket's *department queue* (`{department} Queue`).
6. The integration test host set `Genesys:HumanQueueForUnclassified=false`, so no test ever exercised the production default.

A second path with the same defect: `GenesysConversationEndAppService.TryAutoRaiseHandoffAsync` raised an `AiConnectionLost` handoff for *any* AI
conversation that ended without a human line — including a conversation the AI completed successfully, or one the customer simply left.

## 2. Findings

### Confirmed bugs (fixed)

| # | Bug | Evidence | Fix |
|---|---|---|---|
| B1 | A missing request type created a human handoff ("Waiting for agent"). | `AwaitClassificationAsync` → `agentHandoffAppService.RequestAsync`, default-on option | No handoff for a missing type; ticket stays Unclassified. Option `HumanQueueForUnclassified` removed. |
| B2 | Ending a conversation raised a human handoff on Ticketing's own initiative. | `TryAutoRaiseHandoffAsync` | Off by default; opt-in lever `Genesys:AutoRaiseHandoffOnConversationEnd`. |
| B3 | UI always said "asked for a human agent" (actor "System") whatever the trigger, even when no trigger was stated. | `TicketDetails.cshtml.cs` `DescribeApprovalEvent` | Wording follows the stated trigger; unstated → "a human agent was requested"; actor "Genesys". |
| B4 | Interactions tab could not show *who* initiated a handoff, whether one happened at all, or whether a human or the AI is handling; an agent name was shown for any interaction that had one. | `AgentHandoffSummaryDto` had no trigger; no handling state | `AgentHandoffSummaryDto.Trigger`, `TicketInteractionDto.HandlingState` (`Ai`/`WaitingForHuman`/`Human`), new rows (Started, Ended, Handled by, Handoff requested, Initiated by, Handoff reason). |
| B5 | Classification could never be refined: any different request type → 409, even before anyone had touched the ticket. | `ClassifyAsync` | AI may replace its own classification until a person has worked the ticket (rules in §4); history kept. |
| B6 | An update refused for an unrecognised handoff mode/trigger had already committed its classification (partial write). | `GenesysTicketUpdateAppService` ordering | Handoff vocabulary validated before anything is written. |
| B7 | Overview hid the Request Type row when unclassified. | `TicketDetails.cshtml` | Always shown ("Unclassified"); added "Handled by". |

### Defensive hardening (no failure reproduced)

* Customer name enrichment now requires exactly one distinct customer across CRM + external sources. CRM buyer lookup already answers `AmbiguousMatch` for a shared
  number (verified by test), so this only guards external (PACT/Tasleeh) multi-matches.

### Reviewed and found correct (no change)

* **One conversation → one ticket**: unique index on `TicketInteractions.GenesysConversationId` + read-before-write + `DuplicateWriteException` handling; retries return the same ticket.
* **Handoff idempotency**: external work-item id, then filtered unique index (one open handoff per interaction).
* **Conversation end never closes/resolves a ticket**; end time/reason write-once; transcript de-duplicated by `externalMessageId`, else sender+time+body; transcript validated before any write.
* **First Response**: only the first `HumanAgent` line records it (`FirstHumanResponseRecorder`); `VirtualAgent`/`System`/`Customer` lines and accepting a handoff do not.
* **OriginatingDepartmentId** is write-once; request-type routing moves only `CurrentDepartmentId` (`Ticket.TransferToDepartment`) and re-runs automatic assignment.
* **Department** is chosen explicit id/code → queue mapping; never a fallback department, never from channel alone.
* **Lookup is not verification**: ingestion creates the ticket `Unverified`, links no unit/contact, never auto-selects a match; OTP / CRM ownership checks live in the separate verification and unit-details endpoints and are untouched.
* **SLA / lifecycle / auto-close / reopen / priority-downgrade approval** — unchanged.

### Suspected / needs a decision or Genesys data (not changed)

| # | Item | Why not changed |
|---|---|---|
| S1 | **Transcript is stored only when the conversation ends** (`ended.transcript`). While a chat is live, Ticket Details shows "No transcript yet", and a human who takes over live cannot see the bot's messages; First Response from a live human message is only recorded at end. | Fix needs a new mid-conversation transcript field (contract extension) and a Genesys-side data action. Recommended follow-up. |
| S2 | **No dedicated "Unclassified" work queue.** Previously the (wrong) handoff made Unclassified tickets appear in Pending Interactions. They now sit in the department queue and are classified from Ticket Details (existing Classify action). | A dedicated filter is a UI feature; recommended follow-up. |
| S3 | Whether Genesys sends `agentId` for the bot participant. If it does and maps to a Ticketing user, an AI-only chat would read as human-handled. | Needs a real event sample. The Interactions/Overview "Handled by" relies on `HandledByUserId`, a human transcript line or an accepted handoff — not on the raw agent name. |
| S4 | Handoff `trigger` is optional in the contract. If Genesys omits it, Ticketing shows "Not stated by Genesys" — never "customer". | Making it required would break existing flows. Genesys should always send it. |
| S5 | Mode/trigger/`endReason` vocabularies other than those in the DTO docs are unconfirmed. | — |

## 3. Handoff rules as implemented

| Situation | Ticketing behaviour |
|---|---|
| Conversation created, AI/customer messages, no request type | Ticket Open, Unclassified, default priority, **no handoff**, handling = `Ai`. |
| AI completes the conversation / customer leaves / browser closes (`ended`, no `handoff`) | Interaction ended, transcript stored, ticket status untouched, **no handoff**. |
| `handoff.required=true, trigger=CustomerRequestedHuman` | Handoff `WaitingForAgent`; feed: "Genesys — customer asked for a human agent"; handling = `WaitingForHuman`. |
| `trigger=AiEscalated` | Same; feed "AI escalated the conversation…"; "Initiated by: AI escalated". |
| `trigger=RoutingDecision` | Same; "Business rule / routing decision". |
| `trigger=AiConnectionLost` (explicit) | Same; "AI conversation ended without a human taking it". |
| `handoff.assignedAgentId` / agent connects while waiting | Handoff `Assigned`; handling = `Human`; agent name shown. |
| Agent accepts in Ticketing | `InProgress`, ticket `InProgress`, owner set (existing). |
| `handoff.required=false` + reason | Handoff `Cancelled` (existing). |
| Legacy: `Genesys:AutoRaiseHandoffOnConversationEnd=true` | Old safety net restored (trigger `AiConnectionLost`). |

States: requested (`WaitingForAgent`) → accepted/assigned (`Assigned`/`InProgress`) → `Completed`/`Cancelled`, each with timestamps, workflow event and audit entry. "Queued" is not a Genesys signal TigerCS receives and is not invented.

## 4. Classification rules as implemented

* The AI sends `requestType` (`requestTypeId` preferred, or exact `name`) on create or on `PATCH`. Ticketing validates it against configured Request Types: must exist, be active, have an active responsible department and a Published workflow; a name matching several departments is refused as ambiguous. **No free text, no keyword matching** — Genesys decides intent, Ticketing only accepts a configured type.
* Missing/blank → Unclassified, **no handoff**. Explicitly invalid → **422**, nothing written (create) / nothing applied (update; handoff vocabulary also pre-validated).
* Valid → department from the Request Type (`TransferToDepartment`, originating department untouched), automatic assignment, SLA re-policy without restarting the clock.
* **Changing** classification (`PATCH` with a different valid type) is allowed only while: status `Open`; no Category set by a person; no first human response; no human has accepted/been assigned the interaction; no approval raised. It re-pins the workflow, transfers/re-assigns as needed and records `RequestTypeReclassified` (workflow event) + `ReclassifyRequestType` (audit, before/after). Otherwise **409** and nothing changes. Same type again → idempotent no-op.

If Genesys cannot supply a request type (it has no intent mapping), tickets will stay Unclassified and agents classify them manually — Ticketing cannot infer it.

## 5. API contract matrix

All routes require a bearer JWT of a TigerCS **service account** (policy `CustomerVerification`: CS Agent, CS Supervisor, System Administrator) obtained from `POST /api/auth/login`. Genesys never holds the credentials: the **TigerGroupWeb forwarding layer** (route table in `docs/Genesys/TigerGroupWeb-Forwarding-Implementation.md`) authenticates and forwards unchanged. When `Genesys:Enabled=false` every route answers 503.

| Endpoint | Method | Genesys event / flow | Required | Optional | Ticketing behaviour | Errors |
|---|---|---|---|---|---|---|
| `/api/genesys/tickets` | POST | Conversation/interaction starts (chat started, call answered) | `conversationId`, `channel` | `customerPhone` (`tel:+971…` accepted), `customerName`, `customerEmail`, `queueId`/`queueName`, `departmentId` or `departmentCode`, `towerName`, `unitNumber`, `subject`, `startedAtUtc`, `agentId`, `agentName`, `requestType{requestTypeId\|name}`, ids/direction/calledNumber | Idempotent per `conversationId` (201 first, 200 repeat); department = explicit id/code → queue mapping; ticket Unclassified unless valid `requestType`; **never a handoff** | 400 (missing conversation/validation), 422 (invalid request type, unresolved department, channel not configured), 503 |
| `/api/genesys/tickets/{ticketId}` | PATCH | Any later fact: classification, handoff, routing/agent connect, transcript+end, closedByAi, customer inactivity/confirmation | `conversationId` (must belong to `ticketId`) | `requestType`, `handoff{required,trigger,reason,mode,agentAvailable,workItemId,assignedAgentId}`, `routing{queueId,agentId,…}`, `ended{endedAtUtc,endReason,transcript[]}`, `agentId/agentName`, `awaitingCustomerReply`, `customerActive`, `customerConfirmation`, `closedByAi` | Applies each part idempotently; response echoes unchanged `ticketStatus`; handoff only for `handoff.required=true` | 400 (bad transcript/mode/trigger), 404, 409 (classification locked / ticket closed / concurrency), 422 (invalid request type) |
| `/api/genesys/customers/lookup` | GET | Call pickup / chat start | `phoneNumber` (query) | — | Normalised-phone lookup; **not verification** | 503 CRM down |
| `/api/genesys/customers/unit-details` | POST | After verification | verified identity + unit | — | Ownership-validated unit details; `CUSTOMER_NOT_VERIFIED`, `UNIT_NOT_ELIGIBLE`, `CUSTOMER_AMBIGUOUS` | 4xx/503 |
| `/api/genesys/verification/*` (`buyer-lookup`, `otp/send|resend|verify`) | POST | Verification sub-flow | per route | — | OTP sessions, CRM buyer ownership | 410/423/429 etc. |
| `/api/genesys/documents/send-copy` | POST | Document copy | verified session | — | Unchanged | — |
| `/api/genesys/agent-context`, `/screen-pop` | POST | Agent desktop | agent id | — | Unchanged | — |
| `/api/admin/genesys/queue-mappings` | GET/POST/PUT | Admin (System Administrator) | — | — | Queue→department mapping | — |

No endpoint was added or renamed. New response fields (all additive): `handlingState` on interactions, `trigger` on the handoff summary.

### Payload examples

Create (AI conversation, type not yet known):
```json
POST /api/genesys/tickets
{ "conversationId": "c5a1…", "channel": "LiveChat", "customerPhone": "+971500000001",
  "customerName": "Ahmed Ali", "departmentCode": "LCS", "startedAtUtc": "2026-10-21T09:00:00Z" }
→ 201 { "outcome":"TicketCreated","ticketId":123,"ticketNumber":"TG-LCS-20261021-0002","classificationStatus":"AwaitingClassification" }
```
The AI identifies *Handover*:
```json
PATCH /api/genesys/tickets/123
{ "conversationId": "c5a1…", "requestType": { "requestTypeId": 17 } }
→ 200 { "outcome":"Applied","ticketStatus":"Open","classification":{"status":"Classified","requestTypeId":17,"departmentId":4},"handoffStatus":null }
```
Customer asks for a person:
```json
PATCH /api/genesys/tickets/123
{ "conversationId": "c5a1…", "handoff": { "required": true, "trigger": "CustomerRequestedHuman",
  "reason": "Customer asked to speak to an agent", "mode": "HumanTakeover" } }
→ 200 { "handoffStatus":"WaitingForAgent","ticketAgentHandoffId":55,… }
```
Bot cannot resolve it: same with `"trigger":"AiEscalated"`. A business rule: `"trigger":"RoutingDecision"`.
A human connects: `{"conversationId":"c5a1…","agentId":"<genesys user id>","agentName":"Layla","routing":{"agentId":"<genesys user id>","agentName":"Layla"}}` (or `handoff.assignedAgentId`).
Conversation ends (AI done / customer left — **no handoff block**):
```json
PATCH /api/genesys/tickets/123
{ "conversationId":"c5a1…","ended":{"endedAtUtc":"2026-10-21T09:12:00Z","endReason":"CustomerDisconnect",
  "transcript":[{"sender":"Customer","sentAtUtc":"…","body":"…","externalMessageId":"m-1"},
                {"sender":"VirtualAgent","senderName":"Tiger Bot","sentAtUtc":"…","body":"…","externalMessageId":"m-2"},
                {"sender":"HumanAgent","senderName":"Layla","senderId":"ga-7","sentAtUtc":"…","body":"…","externalMessageId":"m-3"}]}}
```
AI finished and may close: add `"closedByAi": true` (only honoured while no human is involved).
Invalid type → `422 {"title":…,"detail":"No request type is named 'Foo'."}` and nothing is written.

### Who changes what

1. **Tiger-CS-Ticketing** (this repository): everything in §2 "fixed". No schema migration.
2. **TigerGroupWeb forwarding layer**: *no code change required* — routes, auth and bodies are unchanged and forwarded as-is. Confirm it does not synthesise a `handoff` block or a default `requestType` (docs: `TigerGroupWeb-Forwarding-Implementation.md`).
3. **Genesys Architect / AI flow** (requires real configuration and testing):
   * Do **not** call the handoff/PATCH-with-`handoff` action on conversation start, on each bot message, when no request type is known, or on conversation end. Remove any such call.
   * Call `PATCH … { handoff: { required:true, trigger, reason } }` **only** on the explicit escalation exits, and always set `trigger` truthfully (`CustomerRequestedHuman` / `AiEscalated` / `RoutingDecision`).
   * Have the AI classify intent and map it to a configured Request Type, then `PATCH { requestType:{requestTypeId} }` as soon as it is known (and again if it changes). The mapping intent → Request Type **id** lives in Genesys (a data table keyed to `GET /api/request-types`); Ticketing will not guess. Until that exists tickets remain Unclassified.
   * Send the transcript (with `sender` = `Customer` / `VirtualAgent` / `HumanAgent`, `senderName`, `externalMessageId`, real timestamps) in `ended.transcript`. Never send the bot as `HumanAgent`, and do not put a bot id in `agentId`/`routing.agentId`.
   * On the human leg send the connected agent (`routing.agentId`/`agentName`) so Ticketing shows the real person.
   * Send `closedByAi:true` only when the AI really finished.

## 6. Tests

Added `GenesysHandoffAndClassificationRegressionTests` (AI-only conversation; normal question; Handover classified; missing type; invalid type; update refused without partial write; classification change with history + department transfer + OriginatingDepartment untouched; change locked after human response / manual classification; explicit customer/AI/routing handoff; human acceptance; no handoff signal; conversation ended without handoff; legacy lever; duplicate events; bot messages vs First Response; shared phone number; legacy "Awaiting classification" row stand-down), `HandoffWordingTests` (UI wording), and updated the existing tests that encoded the old behaviour (routing tests, endpoint tests, pending-interaction tests now raise waiting work with an explicit `handoff` signal). The integration host no longer disables the previous queue switch, so production defaults are what tests run.

Results are recorded in the pull-request/session summary. The wider suite has pre-existing flaky integration/Web tests that fail intermittently under parallel load on a clean checkout; they were compared against a clean baseline.

## 7. Deployment order and rollback

No database migration, no new column or index (`WorkflowEventType.RequestTypeReclassified = 16` is a value in an unconstrained `tinyint`).

1. Deploy **Tiger-CS-Ticketing** (API + Web together — the Web reads the new DTO fields; older Web with new API is safe, the fields are additive).
2. Remove the `Genesys:HumanQueueForUnclassified` key from environment config if present (ignored if left).
3. Run `CancelLegacyAwaitingClassificationHandoffs.sql` (preview `SELECT` first) to stand down legacy "Awaiting classification" rows that are still Unclassified. Optional but recommended; classified tickets stand theirs down automatically.
4. Change the **Genesys flow** (section 5, "Who changes what") and test in UAT. Order matters little: Ticketing is safe with the old flow (missing type/ended conversations simply stop creating handoffs). If the old flow relied on the end-of-conversation safety net for escalations, send explicit handoffs *before* go-live, or set `Genesys:AutoRaiseHandoffOnConversationEnd=true` temporarily.

Rollback: redeploy the previous build. Rows written by the new build remain valid for the old one (no schema change; the extra workflow event type is ignored by the old UI feed). The SQL cleanup is not automatically reversible — export `TicketAgentHandoffs` rows it touches (the preview `SELECT`) first.

## 8. UAT checklist

| # | Step | Expected |
|---|---|---|
| 1 | Start a website chat; ask "how does handover work?"; do not ask for a person. | Ticket created Open/Unclassified (or *Handover* if the AI classified); Interactions: Handled by *AI*, **Handoff requested: No**; no "Waiting for agent"; no "asked for a human agent" in Activity. |
| 2 | Same, AI sends `requestType` Handover. | Request Type *Handover*, department Leasing, `Originating` department unchanged; still no handoff. |
| 3 | AI changes its mind to another type before any human reply. | Request type replaced; `RequestTypeReclassified` in Activity; audit has before/after; owner/department re-evaluated. |
| 4 | Agent replies, then AI sends a different type. | 409; nothing changes. |
| 5 | Send an unknown/inactive `requestType`. | 422; no ticket (create) / unchanged ticket (update). |
| 6 | Customer says "I want a human". | Handoff Waiting for agent; Initiated by *Customer asked for a human*; Activity "Genesys — customer asked for a human agent"; Handled by *AI — a human was requested…*. |
| 7 | Bot escalates (`AiEscalated`) / rule (`RoutingDecision`). | Initiator shown accordingly. |
| 8 | Agent accepts / Genesys connects an agent. | Agent name shown; Handled by *Human agent — name*; ticket InProgress on accept. First Response only after the first human message. |
| 9 | End an AI-only chat without handoff. | Interaction Ended with timestamps; ticket status unchanged; no handoff; no First Response. |
| 10 | Re-send create/handoff/end events. | Same ticket; one handoff; no duplicate messages. |
| 11 | Chat from a number shared by two CRM customers. | Ticket Unverified, no unit linked, no customer named from CRM. |
| 12 | Open TG-LCS-20261021-0002 after the SQL cleanup. | Handoff Cancelled with the cleanup note; ticket status/owner/department unchanged. |
| 13 | Run the Genesys-side checks from section 5 ("Who changes what") with real events. | **Only this proves end-to-end behaviour.** |
