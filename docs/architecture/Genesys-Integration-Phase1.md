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
Categories. A Genesys ticket is created **Unclassified**:

- `CategoryId` = **null**
- `RequestTypeId` = **null**
- `SlaState` = **`NotApplicable`** — no SLA period is opened
- `PriorityId` = **Medium**, provisional and documented, never derived from
  channel/queue/department — and while the ticket is Unclassified it selects
  nothing, because no SLA period exists to select a policy for

That is the honest state at pick-up: the department is known, the request is
not, because nobody has read it yet. See §4a.

## 4a. Unclassified tickets, and when the SLA clock starts

`Tickets.CategoryId` is **nullable**. It was `NOT NULL` before this phase, and
the alternative — a per-department default category, which an earlier draft of
this design used — was rejected because it is not merely cosmetic: a
placeholder category is a real business classification that reporting, queues
and the agent's screen all read as true, and it would have made a ticket look
triaged when it is not. Inferring a category from a department is the same
mistake wearing a different hat. NULL is the truth, so NULL is what is stored.

The lifecycle:

```
inquiry received → ticket created (Unclassified) → agent reads it
    → agent picks Category + Priority (+ optional Request Type)
    → ticket becomes Classified          ← the SAME ticket, updated in place
```

`POST /api/tickets/{ticketId}/classification` performs the transition.
`Ticket.Classify` is **write-once**: a second attempt is refused with
`AlreadyClassified` rather than silently re-categorising a ticket someone is
already working. The call takes the ticket's `RowVersion`, so two agents
classifying at once cannot both win. **No second ticket is ever created.**

**When the SLA clock starts.** SLA policy selection is keyed on
`PriorityId` alone (`SlaDueDateService.ComputeDueDatesAsync` →
`SlaPolicyRepository.GetByPriorityIdAsync`); Category has never taken part in
it, and this phase does not change that rule. But Priority is *also* part of
classification, so an Unclassified ticket's Medium is provisional — opening a
period against it would measure the department against a target nobody chose.
So an Unclassified ticket opens **no SLA period at all** and sits at
`SlaState.NotApplicable`. Classification opens the initial period, backdated
to `Ticket.CreatedAtUtc`, and moves the state `NotApplicable → Running` with a
history row. Backdating is deliberate: classifying an hour late must not buy
an hour of extra SLA.

Nothing else changed. Workflow, approvals, assignment and escalation never
read `CategoryId`; the four places that do are display and reporting
projections, which now render *Unclassified*.

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

Migration: `20260910064613_AddGenesysIntegration`. No existing interaction
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
