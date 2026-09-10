# Genesys Integration — Phase 1 (as built)

| | |
|---|---|
| **Status** | Implemented. Ships **switched off** (`Genesys:Enabled = false`). |
| **Scope** | The foundation: normalized inquiry ingestion, idempotent ticket creation, department resolution, ticket ↔ conversation linking, conversation end, transcript persistence, and the Ticket Details Conversation History. |
| **Supersedes in practice** | The *conceptual* contract in `Genesys-Integration.md` and the `[MOCK]` payload in `Genesys-Mock-Contract.md`. Neither was implemented, and neither is assumed by anything below — see §5. |
| **Related** | ADR-0019, `Genesys-Integration.md` §15 (the open questions), `Genesys-Mock-Contract.md` |

---

## 1. The rule this phase implements

> **Any new customer inquiry received through Genesys results in exactly one
> ticket.** Retries and duplicate events never create a second one.

Genesys owns every communication channel (phone, website live chat, WhatsApp,
social media). Ticketing does not orchestrate channels — it **records** the
originating channel for audit and reporting, and turns each inquiry into one
ticket.

## 2. One flow, every channel

```
        Call answered ─┐
     Website chat ─────┤
         WhatsApp ─────┼──► normalized inquiry ──► TigerCS ingestion
     Social media ─────┘                                  │
                                                          ▼
                              customer lookup (existing services)
                                                          │
                                                          ▼
                                             department resolution
                                                          │
                                                          ▼
                                    ticket creation (existing service)
                                                          │
                                                          ▼
                                          conversation linked to ticket
```

There is **no per-channel ticket-creation code**. Every channel posts the same
`POST /api/genesys/inquiries` body and runs the same
`GenesysInquiryIngestionAppService`.

### The phone rule

A ringing phone is **not** an inquiry anyone has taken on.

| `event` | Result |
|---|---|
| `Ringing` | Accepted, `204 No Content`. **Nothing is created** — not even an intake record. |
| `Answered` | The agent picked up → the ticket flow starts. |
| `Started` | A text conversation reached an agent → the ticket flow starts. |

## 3. Idempotency

`conversationId` is the identity of an inquiry.

| Delivery | Response |
|---|---|
| First accepted event | `201 Created`, outcome `TicketCreated` |
| Every retry / duplicate | `200 OK`, outcome `AlreadyIngested`, **the same ticket** |

This is enforced at **two** levels, deliberately:

1. **Application** — ingestion looks the conversation up before writing anything.
2. **Database** — `UX_TicketInteractions_GenesysConversationId`, a *unique*
   filtered index on `TicketInteractions.GenesysConversationId`. Two concurrent
   deliveries can both pass the read; the index makes the loser fail its
   insert, and ingestion answers that loser with the winner's ticket.

So `abc123 → TCK-1001` always, and never `abc123 → TCK-1002`.

## 4. Department resolution

In priority order, with **no hard-coded department names or queue ids anywhere**:

1. **The customer's explicit selection** — the website chat's Leasing /
   Customer Service / Maintenance choice, sent as `departmentId` or
   `departmentCode`.
2. **The Genesys queue mapping** — `GenesysQueueMappings` (queue id →
   department), configured under `/api/admin/genesys/queue-mappings`.

An unmapped queue resolves to **nothing** and the inquiry is refused with a
`422` naming the queue. There is no fallback department: an inquiry routed
somewhere nobody configured is a configuration gap that must be visible.

`OriginatingDepartmentId` and `CurrentDepartmentId` are both set to the
resolved department, following the existing write-once rules. Transfer and
workflow behaviour are unchanged.

### What is *not* inferred

The website's three options are **departments** — not Request Types, not
Categories, not Priorities. A Genesys ticket is created **Unclassified**:

- `CategoryId` = **null**
- `RequestTypeId` = **null**
- `PriorityId` = **null**
- `SlaState` = **`NotApplicable`** — no SLA period exists

That is the honest state at pick-up: the department is known, and nothing else
about the request is, because nobody has read it yet. See §4a.

## 4a. Unclassified tickets, and when the SLA clock starts

### Why both fields are null

`Tickets.CategoryId` and `Tickets.PriorityId` are both **nullable**. Both were
`NOT NULL` before this phase.

An earlier draft defaulted the category (per department) and the priority (to
Medium). Both were rejected, for the same reason:

- A **placeholder category** is a real business classification. Reporting,
  queue filters and the agent's screen all read it as true, so it makes a
  ticket look triaged when it is not. Inferring one from the department is the
  same mistake wearing a different hat.
- A **placeholder priority** is no more harmless, even though it starts no SLA
  clock. Priority drives the dashboard's *Critical/High* KPI count, the queue's
  priority sort, the *Tickets Requiring Attention* ranking, and the badge on
  every list row. A defaulted Medium would rank an inquiry nobody has read
  among tickets whose urgency a human actually judged.

NULL is the truth in both cases, so NULL is what is stored — and every reader
handles it explicitly (see *Reading a null priority* below).

### The lifecycle

```
10:00  Genesys inquiry arrives, agent picks up
       → Ticket created:  Department known
                          Category = NULL, RequestType = NULL, Priority = NULL
                          SlaState = NotApplicable  (no SLA period)

10:03  Agent replies to the customer
       → FirstHumanResponseAtUtc = 10:03   (3 minutes — measured, see below)

10:15  Agent has understood the inquiry and selects
       Category = NOC, RequestType = NOC for Resale, Priority = Normal
       → the SAME ticket becomes Classified
       → business/resolution SLA period opens, starting 10:15
```

`POST /api/tickets/{ticketId}/classification` performs the transition, on the
same ticket, under its `RowVersion` so two agents cannot both win. **No second
ticket is ever created.** A real `PriorityId` is required alongside the
category; the request type is optional and pins its published workflow version
exactly as classifying at creation would.

### When the business SLA starts — and why not at creation

SLA policy selection is keyed on **`PriorityId` alone**
(`SlaDueDateService.ComputeDueDatesAsync` →
`SlaPolicyRepository.GetByPriorityIdAsync`). Category has never taken part in
it, and this phase does not change that rule.

An SLA policy is a commitment to resolve a *known* request within a target.
While the ticket is Unclassified nobody knows the request, the priority, or
therefore the policy — so there is nothing to commit to and **no period is
opened**. The clock starts at the **classification timestamp** and runs from
there. It is deliberately *not* backdated to `CreatedAtUtc`: doing so would
charge the department for a stretch of time during which no target existed,
against a policy chosen after the fact.

`SlaState` moves `NotApplicable → Running` at that moment, recorded on the
ticket's status history like any other dimension change.

### First Response stays measurable throughout

`Ticket.FirstHumanResponseAtUtc` lives on the **ticket**, not on the SLA
period. Recording an agent's first reply needs no SLA instance, so "how
quickly did this Genesys customer reach a human" is captured with its real
timestamp from the moment the inquiry arrived — whether or not anyone has
classified the ticket yet. `SlaBreachProcessor` already has a
`NoSlaPeriod` outcome for a ticket without a current period, and
`SlaQueryAppService.ToSummaryDto` already renders a null instance, so this
needed no new machinery.

The distinction, stated plainly:

| | Depends on classification? | Starts at |
|---|---|---|
| **First Response** (human responsiveness) | No | Ticket creation; recorded on the ticket |
| **Business / Resolution SLA** (target for a known request) | Yes | Classification |

The First Response *deadline* is part of the SLA period and so exists only
once classified — but the *measurement* of when a human first responded does
not wait for it.

### Reading a null priority

Both SQL Server and LINQ order `NULL` **first** ascending, and ascending is
"most urgent first" here (1 = Critical). Left alone, an unclassified ticket
would outrank every Critical one. Each reader therefore says what it does:

| Reader | Behaviour with `PriorityId = NULL` |
|---|---|
| Queue sort by priority (asc **and** desc) | Sorts **last**, via a leading `PriorityId == null` key. Unjudged is not "most urgent", nor "least" |
| Queue filter `?priorityId=` | Never matches — an unclassified ticket has no tier to match |
| Dashboard *Critical/High* KPI | Not counted: `PriorityId != null && PriorityId <= 2`, written explicitly rather than relying on SQL's `NULL <= 2` |
| *Tickets Requiring Attention* | Still listed (it is unassigned), but tie-breaks **below** every judged tier |
| Escalation on breach | Unreachable — a breach needs an SLA period, which needs classification |
| `TicketSlaInstance.PriorityId` | Stays **NOT NULL**: a period only ever exists for a classified ticket |
| Ticket Details / queue rows / customer history | Renders **Not set** with its own neutral badge — never coalesced to *Medium* |

Nothing else changed. Workflow, approvals and assignment never read
`CategoryId` or `PriorityId` at all.

### Reclassification is deferred, not forbidden

`Ticket.Classify` performs a ticket's **initial** classification; a second
attempt is refused with `AlreadyClassified`. **This is a Phase 1 safeguard,
not a confirmed final business rule.** Reclassification is deferred because
its semantics are undefined: whether the SLA period is recomputed, restarted
or left alone; what happens to a workflow version already pinned and
executing; what happens to approvals raised under the previous request type.
Until those are decided, refusing keeps a ticket's business meaning from
moving underneath machinery that has already acted on it.

This is not a new restriction. `Ticket.ClassifyRequestType` has guarded its
own field the same way since the Workflow/Automation phase
(`TicketRequestTypeAlreadySetException`), and TigerCS has **no** existing
operation that changes a ticket's category, request type or priority after
creation — there is no update-ticket endpoint. Note that
`RequestType.AllowAgentPriorityChange` (surfaced as
`WorkflowCapabilities.CanChangePriority`) already exists in configuration with
no consuming operation, so the intent to allow priority changes predates this
phase and is waiting on exactly the semantics above.

## 4b. Pending human work — on every channel

### Why this is not "callbacks"

Genesys carries phone, website chat, chatbot/virtual agent, WhatsApp, social
media, and whatever it adds next. On every one of them the same thing can
happen: routing or a virtual agent decides a **human agent** is needed, and no
human is immediately available.

The business rule is identical across all of them:

> Any Genesys interaction that requires a human agent, but cannot immediately
> reach one, must remain as pending human work and must be visible to agents.

Only the *continuation* differs — and a callback is one of those, not the
concept. Modelling this as a `CallbackRequest` would have been phone-shaped
thinking that hid most of the work: a chat, a WhatsApp thread and a
social-media message never involve dialling anyone.

### The flow

```
Genesys AI / bot / routing decides a human is needed
    │
    ├── an agent IS available
    │       → Genesys live-transfers
    │       → SAME ticket, SAME conversation where supported
    │       → work item recorded as Assigned; never appears as waiting
    │
    └── NO agent available
            → SAME ticket stays open
            → work item recorded as WaitingForAgent
            → it appears in the agent work list
```

**No second ticket, ever.** The conversation already has one from ingestion.
A handoff attaches work to that ticket and its interaction — including the
AI-first journey, which is one ticket from customer → bot → human.

### The model: `TicketAgentHandoff`

A **separate entity**, not columns on `TicketInteraction`. The deciding
reason: §11's rule that a customer disconnecting must not cancel the business
case. `TicketInteraction.End` is write-once precisely so an ended conversation
is a settled audit record — putting mutable, still-outstanding work on a
settled row would make "ended" and "still pending" the same row saying two
contradictory things. The work genuinely outlives the interaction, and it
needs its own indexed status for a cross-ticket query the interaction table
should not serve. `TicketPendingRecord` (an open/resolved pending period per
ticket) is the existing precedent this follows.

| Field | Notes |
|---|---|
| `TicketId`, `TicketInteractionId` | The ticket and the conversation it arose from — via the interaction, the `GenesysConversationId`. |
| `DepartmentId`, `ChannelId` | Copied at request time from the ticket's **current** department, so work follows a transferred ticket. |
| `Status` | `WaitingForAgent` / `Assigned` / `InProgress` / `Completed` / `Cancelled`. |
| `Mode` | `Callback` / `ContinueChat` / `ReplyInChannel` / `HumanTakeover`, or **null**. |
| `RequestReason` | Why a human was needed, as reported. Free text — no vocabulary is confirmed. |
| `RequestedAtUtc` | What "waiting since" is measured from. |
| `AssignedEmployeeId`, `GenesysAgentId` | The TigerCS employee where known, and Genesys' own agent id verbatim where that is all we get. |
| `AssignedAtUtc`, `StartedAtUtc`, `CompletedAtUtc` | The work's own timeline. |
| `ResolvedAtUtc`, `ResolutionNote` | Null exactly while outstanding. A cancellation **requires** a reason. |
| `ExternalWorkItemId` | Genesys' routing-task/work-item id **if it supplies one**. Never invented. |

`NotRequired` exists in the status enum but **no row ever stores it** — a work
item exists only once a human has been asked for. It is what the ticket-level
view reports when no handoff exists, so "does this need a human?" has an
answer either way.

### The mode is never inferred from the channel

Which continuation applies on which channel is Genesys' behaviour to state,
and **it has not stated it**. So the mode is whatever the caller supplies,
normalized into the four TigerCS-owned values, and stays `null` when nothing
was supplied — displayed as *Not specified*, never defaulted to Callback. An
unrecognized value is refused at the boundary rather than stored as Genesys
vocabulary.

### Genesys still owns the channel

TigerCS implements **no** dialling, chat transport, WhatsApp or social sending,
queue scheduling, agent-availability logic, or routing. Genesys owns all of it.
The agent-facing actions are therefore only *Open Ticket*, *Start Handling*,
*Complete* and *Cancel* — recording what a human is doing. There is
deliberately no "Call" or "Reply on WhatsApp" button, because offering one
would be a promise this system cannot keep.

Nor is there a second assignment algorithm. If Genesys assigns an agent, TigerCS
reflects it (`POST /api/genesys/conversations/handoff/assignment`). Until then
`AssignedEmployeeId` is null and the status is `WaitingForAgent`. A Genesys
agent id is **not** mapped to a TigerCS employee — no such mapping has been
confirmed — so it is recorded as the external string it is.

### Idempotency

Genesys events get retried. Two database-level guarantees, both using only
identifiers that actually exist:

| Index | Guarantee |
|---|---|
| `UX_TicketAgentHandoffs_OpenPerInteraction` — unique on interaction `WHERE ResolvedAtUtc IS NULL` | At most one **outstanding** work item per conversation. A retried "human required" event answers `AlreadyRequested` with the same item. |
| `UX_TicketAgentHandoffs_ExternalWorkItemId` — unique `WHERE NOT NULL` | The stronger key, when Genesys supplies its own id. Costs nothing while absent. |

Filtering on *unresolved* rather than on the interaction is deliberate: a
customer who needs a human again after the first piece of work was finished is
a **new work item on the same ticket**, never a new ticket. Assignment and
completion updates are idempotent in the domain itself — re-reporting the same
agent does not move `AssignedAtUtc`, and starting work already in progress does
not move `StartedAtUtc`.

### Two statuses, deliberately separate

| | Example |
|---|---|
| **Ticket** | Open → In Progress → … (the existing workflow) |
| **Human follow-up** | WaitingForAgent → InProgress → Completed |

**Completing the human work never closes, resolves or otherwise touches the
ticket.** The customer whose chat was answered may still have an NOC workflow
running for days. Cancelling does not touch it either. The two are rendered
side by side in Ticket Details and never conflated.

### The customer disconnects

A customer closing the browser while waiting ends the *interaction*: the
transcript is stored and `EndedAtUtc`/`EndReason` recorded, exactly as before.
The ticket stays open, and the work item **stays outstanding and actionable** —
the work list shows it with a "session ended" marker rather than dropping it.
The live session ended; the business case did not.

### The agent must not start from zero

Opening the work item's ticket shows what was already collected: the customer,
the units, the department, the channel, the full AI/bot transcript, the
previous interactions, the previous-ticket history, and the classification
state. The Conversation History section now also carries, beside the transcript
that led to it, **why a human was asked for**, the follow-up mode, the work's
status and any outcome note.

`InteractionMessageSender` gained **`VirtualAgent`**, distinct from both `Agent`
and `System`: an agent taking over a bot conversation must be able to tell what
a human said from what the bot said, and a bot asking "Are you asking about an
NOC for resale?" is conversation, not platform noise. A TigerCS-owned
normalized value — no Genesys vocabulary is assumed.

### Unclassified tickets still qualify

A ticket can need a human while it is still Unclassified (§4a) — no category,
no request type, no priority, no SLA period. **Classification is not a
prerequisite for entering the work list**; frequently the agent classifies the
ticket *because* they picked it up from there.

### The agent work list

`GET /api/pending-customer-interactions`, and the **Pending Interactions** page
(deliberately not "Callback List"). Longest wait first, scoped by exactly the
same visible-department rule as the ticket queue. Columns: status, ticket
number, customer, mobile, channel, follow-up mode, department, waiting since,
agent, action.


## 5. What is deliberately **not** built

No Genesys API endpoint, OAuth client, webhook signature scheme, event-topic
vocabulary or payload schema has been confirmed to this repository. Therefore
**none is implemented and none is invented**:

- There is **no outbound Genesys client** and no Genesys base URL/credential in configuration.
- There is **no webhook signature verification** (`X-Genesys-Signature` or otherwise).
- There are **no `GenesysInteractions` / `GenesysInteractionEvents` / `GenesysAgentMappings` tables** — those belonged to the conceptual design in `Genesys-Mock-Contract.md`, which assumed a contract that is still open.

Instead, Ticketing exposes **its own** inbound contract
(`TigerCS.Api/Controllers/GenesysContracts.cs`) and Genesys calls it as an
ordinary authenticated TigerCS service account over the system's existing JWT
authentication — the same mechanism every other API client uses. When the real
Genesys mechanism is confirmed, the adapter for it lives at that boundary; the
application and domain behind it do not move.

## 6. Conversation end and transcripts

`POST /api/genesys/conversations/end` is called when a conversation ends for
**any** reason — agent ended it, customer closed the browser, connection
dropped, Genesys timed it out.

It records `EndedAtUtc` / `EndReason` on the interaction and stores the
transcript as **structured messages** (`TicketInteractionMessages`: one row per
message with sender, sender name, timestamp and body) — not one display blob —
so Ticket Details can render the exchange in order.

**Chat end ≠ ticket closed.** This is the phase's load-bearing rule: ending a
conversation writes nothing to the ticket. A customer who asked for an NOC has
an open ticket for days after the chat window closed. The end response returns
the ticket's unchanged status to make that visible.

A duplicate end event answers `AlreadyEnded`, does not move the recorded end
time, and does not duplicate the transcript. A malformed transcript is rejected
**before anything is written**, so a conversation is never half-stored.

## 7. Customer lookup

When a mobile number is available, ingestion calls the **existing**
`CustomerSearchAppService` — the same CRM Buyer + PACT/Tasleeh lookup the New
Ticket wizard uses. Genesys never reaches Tiger CRM, PACT or Tasleeh directly,
and no second CRM integration exists for it.

Lookup is **enrichment, never a gate**:

- No match → the ticket is still created, as an unverified customer inquiry.
- A source is down → the ticket is still created; the failure is recorded on the audit entry.
- No phone at all (withheld caller id, a social DM) → the ticket is still created.

A customer match is **never auto-selected** — that stays the agent's explicit
act, exactly as everywhere else in the system.

## 8. Feature flag

```jsonc
"Genesys": { "Enabled": false }   // the shipped default
```

With the flag off, both Genesys endpoints answer `503` and write nothing, and
**every existing flow is untouched** — nothing in the normal ticketing path
reads the flag. `GenesysDisabledEndpointsTests` proves both halves against a
real host.

## 9. Database changes

| Change | Purpose |
|---|---|
| `TicketInteractions` + `EndedAtUtc`, `EndReason`, `CustomerName`, `CustomerEmail` | Conversation lifecycle and what the channel collected. All nullable — existing rows stay valid. |
| `IX_TicketInteractions_GenesysConversationId` → **`UX_…` (unique)** | The database-level one-ticket-per-conversation guarantee. |
| **`TicketInteractionMessages`** (new) | The structured transcript. |
| **`GenesysQueueMappings`** (new) | Queue → Department configuration. **No rows seeded.** |
| `Tickets.CategoryId` → **nullable** | The Unclassified phase (§4a). Existing rows are unaffected — every ticket created before this phase keeps its category. |
| `Tickets.PriorityId` → **nullable** | Same reason (§4a): an unread inquiry has no judged urgency. Existing rows keep their priority. `TicketSlaInstances.PriorityId` stays NOT NULL. |
| **`TicketAgentHandoffs`** (new) | Pending human work on any channel (§4b), with two filtered unique indexes for idempotency. |

Migration: `20260910080219_AddGenesysIntegration`. No existing interaction
model was replaced or duplicated — `TicketInteraction` was extended.

---

## 10. Still required from the Genesys team

Nothing below blocks what is built; each one decides how the boundary adapter
is finished.

1. **Delivery mechanism** — will Genesys POST to our endpoint, or must TigerCS subscribe/poll? Everything built assumes Genesys calls us; a polling model needs an outbound client that does not exist yet.
2. **Authentication** — the real scheme for inbound calls (HMAC signature, OAuth client credentials, mutual TLS, IP allow-list). Today it is a TigerCS service-account JWT.
3. **Event vocabulary** — the actual values for "ringing", "answered" and "conversation started/ended", per channel, so the edge mapper can translate them.
4. **Field names and payload shape**, including how the conversation id, interaction id, participant id and communication id relate.
5. **Delivery guarantees** — at-least-once confirmed? Is ordering guaranteed? (The build assumes at-least-once and out-of-order-tolerant, which is the safe assumption.)
6. **Transcript delivery** — is the full transcript available at conversation end, or must it be collected incrementally during the chat? (The message model supports both; only the end-of-conversation path is wired.)
7. **Queue ids** — the real Genesys queue identifiers, so an administrator can map them to departments. **None are invented.**
8. **Agent identity** — is an agent email/extension reliably present, and should Genesys agents be mapped to TigerCS employees? (Today agent id/name are recorded as external strings only.)
9. **Website chat form** — confirmation that the department choice, name, phone, email, tower and unit are all delivered with the conversation start.
10. **Sandbox availability** for integration testing.
11. **Human-handoff events** — does Genesys emit an event when routing or a virtual agent decides a human is needed, and another when an agent takes it? Both endpoints exist; the trigger on Genesys' side is unconfirmed.
12. **Follow-up behaviour per channel** — what continuation Genesys actually performs on each channel (callback, chat resume, reply in thread, live takeover), and whether it can resume a chat at all after the customer disconnected. Nothing is assumed; the mode is stored only when stated.
13. **Routing-task / work-item identifiers** — does Genesys expose a stable id for a queued piece of human work? If so it becomes the stronger idempotency key. **None is invented.**
14. **Agent identity mapping** — should Genesys agent ids map to TigerCS employees? Today a Genesys agent id is recorded as an external string and never resolved to an employee.
15. **Escalation reason vocabulary** — does the virtual agent supply a structured reason for handing over, or only free text? Stored as free text until a vocabulary is confirmed.
16. **Bot-authored transcript lines** — how Genesys labels a virtual agent's messages, so they map to the `VirtualAgent` sender rather than being flattened into `System`.
