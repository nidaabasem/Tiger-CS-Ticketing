# Genesys ↔ TigerCS — the three integration contracts

Genesys asked for three capabilities. This is the whole externally consumable
surface: **there is nothing else on `api/genesys`.**

| # | Capability | Route |
|---|---|---|
| 1 | Create Ticket, any channel | `POST /api/genesys/tickets` |
| 2 | Customer lookup on call pickup | `GET /api/genesys/customers/lookup?phoneNumber=` |
| 3 | Update Ticket | `PATCH /api/genesys/tickets/{ticketId}` |
| 4 | Agent context (agent identity mapping) | `POST /api/genesys/agent-context` |

All four are behind the `Genesys:Enabled` feature flag and answer `503` while
it is off. Contract 4 is the agent-identity addition described under
*Agent identity mapping* below; it changes nothing about contracts 1–3.

---

## Authentication — **UAT only, not the final design**

> **This is the UAT mechanism, not the confirmed final Genesys authentication
> design.** A CS Agent service account + JWT is what UAT runs on so integration
> testing can start now. The production scheme is still open — see item 1 under
> *Still required from the Genesys team*. No new authentication scheme has been
> built, and none should be assumed from this section.

**There is no Genesys-specific credential, and none is invented.** No Genesys
API base URL, OAuth client or webhook signing secret exists in this system,
because none has been confirmed by the Genesys team.

For UAT, Genesys calls TigerCS as an ordinary authenticated **service
account**, using the same JWT bearer authentication every other client of this
API uses:

1. An administrator creates a staff account for Genesys under Administration
   (`POST /api/admin/users`) with the **CS Agent** role — the same role that
   may create tickets, which is exactly what these endpoints do.
2. Genesys signs in:

```http
POST /api/auth/login
Content-Type: application/json

{ "username": "genesys.integration", "password": "…" }
```

```json
{ "accessToken": "eyJhbGciOi…", "expiresAtUtc": "2026-09-10T12:00:00Z" }
```

3. Every subsequent call carries `Authorization: Bearer {accessToken}`.

When the real mechanism is confirmed (HMAC signature, OAuth client
credentials, mutual TLS, IP allow-list), it is added at this boundary without
touching anything behind it. **Until then, treat the JWT service account as a
UAT convenience with a known expiry date, not as the integration's security
design.**

---

## 1. Create Ticket

`POST /api/genesys/tickets`

One endpoint for **every** channel — `Phone`, `WebsiteChat`, `WhatsApp`,
`SocialMedia`. There is no per-channel endpoint and no per-channel
ticket-creation code.

**Idempotent on `conversationId`.** The first accepted event creates the
ticket; every retry returns *that same ticket* with `outcome:
"AlreadyIngested"` and creates nothing. A unique database index makes this hold
under concurrent delivery, not only under sequential retries.

**This endpoint means exactly one thing:** create or reuse the ticket for this
conversation. **There is no `event` field.** It is not an event receiver for
call progress — a ringing call never reaches TigerCS at all:

```
Incoming call → Ringing  → TigerCS receives nothing
Agent picks up           → GET  /api/genesys/customers/lookup?phoneNumber=…
                         → POST /api/genesys/tickets
```

Digital channels (website chat, chatbot, WhatsApp, social) post here directly
when the conversation starts.

**Only `conversationId` and `channel` are required.** Everything else is
optional context, because Genesys' per-channel guarantees are not confirmed —
absent values are stored as null, never guessed.

**A customer-lookup failure never blocks creation.** Nor does a missing phone
number (withheld caller id, a social DM).

### Request

```json
{
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "channel": "WebsiteChat",
  "customerPhone": "+971501234567",
  "customerName": "Ahmed Ali",
  "customerEmail": "ahmed@example.com",
  "departmentId": 3,
  "departmentCode": "CS",
  "towerName": "Tiger Tower A",
  "unitNumber": "1204",
  "queueId": "genesys-queue-cs",
  "queueName": "Customer Service",
  "agentId": "ga-7",
  "agentName": "Layla",
  "calledNumber": "+97140000000",
  "direction": "Inbound",
  "startedAtUtc": "2026-09-10T09:31:00Z",
  "subject": "Asking about handover"
}
```

Department resolution: `departmentId`/`departmentCode` is the customer's own
website-chat choice (Leasing / Customer Service / Maintenance) where there is
one; otherwise the configured `queueId` → department mapping. An unmapped
queue is refused with `422` naming the queue — never routed to a guess.

### Response — `201 Created`

```json
{
  "outcome": "TicketCreated",
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "ticketId": 10432,
  "ticketNumber": "TG-CS-20260910-0007"
}
```

### Response — `200 OK` (the retry answer)

```json
{
  "outcome": "AlreadyIngested",
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "ticketId": 10432,
  "ticketNumber": "TG-CS-20260910-0007"
}
```

Same body, same ticket, nothing created. `outcome` is how the caller tells the
two apart.

### The ticket that is created

It is **Unclassified**: `categoryId`, `requestTypeId` and `priorityId` are all
`null`, and no SLA period is opened. Nobody has read the request yet, so
nothing about it is guessed. A TigerCS agent classifies it afterwards through
`POST /api/tickets/{ticketId}/classification`, on the same ticket, and that is
when the business SLA starts. **Genesys is not involved in classification.**

### Other responses

| Code | When |
|---|---|
| `400` | Unrecognized `channel`, or blank `conversationId` |
| `422` | No department could be resolved, or ticket creation was refused |
| `503` | `Genesys:Enabled` is false |

---

## 2. Customer lookup on call pickup

`GET /api/genesys/customers/lookup?phoneNumber=%2B971501234567`

Called when an agent **answers** — never while ringing.

Reuses the same CRM Buyer Lookup and PACT/Tasleeh services the New Ticket
wizard uses. **No second CRM integration exists**, and Genesys never reaches
those systems directly. Read-only: nothing is created or persisted.

### Response — `200 OK`, customer found

```json
{
  "phoneNumber": "+971501234567",
  "found": true,
  "crmStatus": "Found",
  "crmBuyers": [
    {
      "customer": {
        "customerId": 4001,
        "customerName": "Ahmed Al-Farsi",
        "mobile": "+971501234567",
        "email": "ahmed@example.com"
      },
      "units": [
        {
          "unitId": 9001,
          "unitNumber": "1204",
          "projectName": "Tiger Tower A",
          "unitStatus": "Buyer"
        }
      ]
    }
  ],
  "externalSources": [
    { "source": "Pact", "status": "NotFound", "customers": [] },
    { "source": "Tasleeh", "status": "NotFound", "customers": [] }
  ],
  "tickets": [
    {
      "ticketId": 10310,
      "ticketNumber": "TG-CS-20260901-0044",
      "ticketStatus": "InProgress",
      "isOpen": true,
      "requestSummary": "NOC for resale",
      "currentDepartmentId": 3,
      "createdAtUtc": "2026-09-01T08:12:00Z"
    }
  ],
  "openTicketCount": 1
}
```

`tickets` are the tickets that already arrived from this number, **open ones
first** — so the agent knows the caller has a live case before they speak.

### Response — `200 OK`, nobody found

```json
{
  "phoneNumber": "+971509999999",
  "found": false,
  "crmStatus": "NotFound",
  "crmBuyers": [],
  "externalSources": [
    { "source": "Pact", "status": "NotFound", "customers": [] },
    { "source": "Tasleeh", "status": "NotFound", "customers": [] }
  ],
  "tickets": [],
  "openTicketCount": 0
}
```

**`found: false` is a `200`, not a `404`** — and it never stops the Create
Ticket call that follows. A source being down reports `"crmStatus": "Failed"`
(or the source's own `"Failed"`) alongside whatever the others returned, and is
equally not a blocker.

| Code | When |
|---|---|
| `400` | `phoneNumber` missing or blank |
| `503` | `Genesys:Enabled` is false |

---

## 3. Update Ticket

`PATCH /api/genesys/tickets/{ticketId}`

Everything Genesys owns about a ticket after creating it, in one call. Every
part of the body is optional and independently idempotent — send only what
changed, resend freely.

`conversationId` is **required** and must resolve to an interaction on the
ticket in the route. That cross-check is what stops a transcript being applied
to the wrong ticket.

### What Genesys can update

| Field | Meaning |
|---|---|
| `agentId` / `agentName` | Who is handling the conversation. Apply-if-absent — a later, different agent never overwrites the one who took it |
| `ended.endedAtUtc` / `ended.endReason` | The conversation finished, for any reason |
| `ended.transcript[]` | The conversation, in order |
| `handoff.*` | Human-agent state, on any channel |

### What Genesys **cannot** update

There is no field for `categoryId`, `requestTypeId`, `priorityId`,
`ticketStatus`, owner, department, resolution or closure. Those move only
through their own TigerCS operations, with their own authorization, workflow
and SLA consequences. **A field absent from this contract is a field Genesys
cannot reach.**

### Request — conversation ended, with transcript

```json
{
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "agentId": "ga-7",
  "agentName": "Layla",
  "ended": {
    "endedAtUtc": "2026-09-10T09:47:00Z",
    "endReason": "CustomerDisconnect",
    "transcript": [
      { "sender": "Customer",     "sentAtUtc": "2026-09-10T09:33:00Z", "body": "I want to sell my apartment." },
      { "sender": "VirtualAgent", "sentAtUtc": "2026-09-10T09:33:20Z", "body": "Are you asking about an NOC for resale?" },
      { "sender": "Customer",     "sentAtUtc": "2026-09-10T09:33:45Z", "body": "Yes." }
    ]
  }
}
```

`sender` is one of **`Customer`**, **`VirtualAgent`**, **`HumanAgent`**,
**`System`**. Anything else fails the whole update — the sender is never
silently attributed, and nothing is stored, so the caller simply resends.

Each message preserves, where supplied: `messageId`, sender type, `sentAtUtc`,
`body` (verbatim), plus `senderName` / `senderId`. Ticket Details renders the
whole conversation in delivered order.

**Supplying `messageId` is strongly preferred.** It is the deduplication key,
and the only one that survives a retry whose timestamps were regenerated.
Without it the fallback is sender + timestamp + body.

### Request — a human agent is needed

```json
{
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "handoff": {
    "required": true,
    "agentAvailable": false,
    "mode": "ContinueChat",
    "reason": "Customer asked about an NOC for resale",
    "workItemId": "genesys-workitem-8871"
  }
}
```

`mode` is one of `Callback`, `ContinueChat`, `ReplyInChannel`,
`HumanTakeover`. **Omit it unless Genesys actually states it** — TigerCS never
derives it from the channel, and a callback is one mode among several rather
than the concept. `workItemId` is optional and used as a stronger idempotency
key when supplied; omit it if Genesys has none.

### Request — an agent took the pending work

```json
{
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "agentName": "Noor",
  "handoff": { "assignedAgentId": "ga-9" }
}
```

### Response — `200 OK`

```json
{
  "outcome": "Applied",
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "ticketId": 10432,
  "ticketNumber": "TG-CS-20260910-0007",
  "ticketStatus": "Open",
  "conversationEnded": true,
  "transcriptMessageCount": 3,
  "handoffStatus": "WaitingForAgent",
  "ticketAgentHandoffId": 55
}
```

`ticketStatus` is echoed on **every** response deliberately: it is the standing
proof that **ending a conversation did not close the ticket**. A customer who
asked for an NOC has an open ticket for days after the chat window closed.

`handoffStatus` is reported on every update too, not only on one that touched
it — so an end event shows that the pending human work is *still outstanding*.
That is exactly the customer-disconnected case: the live session ended, the
business case did not.

### Idempotency — no duplicates, and no truncation

- Resending an already-stored transcript stores nothing twice —
  `transcriptMessageCount` stays put.
- A redelivered end does **not** move the recorded `endedAtUtc`/`endReason`;
  those are write-once.
- But its transcript **is** still read, and any message not already stored is
  appended. A conversation stored from a truncated first delivery is completed
  by the retry rather than staying permanently short — "the complete
  transcript is persisted" is the requirement, so a redelivery is not simply
  discarded.
- A redelivered handoff returns the same `ticketAgentHandoffId`; there is never
  a second work item for a conversation whose work is still outstanding.
- Re-reporting the same assigned agent changes nothing.

### Other responses

| Code | When |
|---|---|
| `400` | Blank `conversationId`, a transcript message with an unrecognized sender or empty body, or an unrecognized `handoff.mode` |
| `404` | No such ticket, or no interaction exists for this conversation |
| `409` | The conversation belongs to a different ticket than the one in the route |
| `422` | `handoff.assignedAgentId` supplied but no outstanding human work exists |
| `503` | `Genesys:Enabled` is false |

A malformed transcript is refused **before anything is written**, so a bad
update never leaves a half-stored conversation.

---

## 4. Agent context — Genesys agent → Ticketing user

`POST /api/genesys/agent-context`

When an agent opens or works with Ticketing from Genesys, Ticketing must know
**exactly which authenticated Ticketing user that agent is**. The mapping is:

```text
Genesys User ID  →  AspNetUsers.GenesysUserId  →  Ticketing user  →  user id / roles / departments
```

The immutable **Genesys User ID** is the only key. The agent's display name
and email are never used to identify an agent: `agentEmail` is informational
and is recorded on the audit trail only.

### Request

```json
{
  "genesysUserId": "6f1d2c3b-0a9e-4b8d-9c7f-1e2d3c4b5a69",
  "agentEmail": "agent@tigerproperties.ae",
  "conversationId": "e2c4a1b0-7d3f-4c9e-9a1b-2f3e4d5c6b7a"
}
```

| Field | Required | Meaning |
|---|---|---|
| `genesysUserId` | **yes** | The agent's Genesys User ID. |
| `agentEmail` | no | Informational only. Never trusted as identity. |
| `conversationId` | no | The conversation the agent is working. When present, its interaction records the resolved user as the handler. |

There is deliberately **no Ticketing user id** in this body. Which user
handled the interaction is resolved on the server from `genesysUserId` alone;
a `handledByUserId` (or any similar property) sent by a client is ignored.

### Response — `200 OK`

```json
{
  "outcome": "Resolved",
  "genesysUserId": "6f1d2c3b-0a9e-4b8d-9c7f-1e2d3c4b5a69",
  "userId": "0a1b2c3d-…",
  "userName": "layla.agent",
  "displayName": "Layla",
  "roles": ["CS Agent"],
  "departmentIds": [3],
  "conversationId": "e2c4a1b0-…",
  "ticketId": 1042,
  "ticketNumber": "TG-CS-260911-0007",
  "ticketInteractionId": 2210,
  "handledByUserId": "0a1b2c3d-…"
}
```

`ticketId`, `ticketNumber`, `ticketInteractionId` and `handledByUserId` are
null when no `conversationId` was supplied. `handledByUserId` is the user
recorded on the interaction — the **first** agent resolved on it; a later,
different agent (a transfer) does not overwrite it.

### What it records — and what it does not

The resolved user is stored on the conversation's `TicketInteractions` row:

```text
TicketInteractions.GenesysAgentUserId = the incoming Genesys User ID
TicketInteractions.HandledByUserId    = the resolved Ticketing user (FK → AspNetUsers.Id)
```

This is *who handled the interaction*. It is **not** the ticket's assignee:
the ticket's owner, department, queue, status and workflow are untouched, and
move only through their own TigerCS operations exactly as before.

### Other responses

| Code | Body | When |
|---|---|---|
| `400` | standard validation problem, `errors.GenesysUserId` | `genesysUserId` missing or blank |
| `403` | ProblemDetails with `"code": "GENESYS_AGENT_NOT_MAPPED"` | No Ticketing user carries this Genesys User ID. **No user is created.** |
| `403` | ProblemDetails with `"code": "GENESYS_AGENT_INACTIVE"` | The mapped Ticketing user is deactivated |
| `404` | ProblemDetails | `conversationId` supplied but never ingested |
| `503` | ProblemDetails | `Genesys:Enabled` is false |

The `403` body is the API's standard ProblemDetails shape plus the stable
`code` member:

```json
{
  "type": "https://tigercs.internal/problems/genesys-agent-not-mapped",
  "title": "Genesys agent is not mapped to a Ticketing user",
  "status": 403,
  "detail": "Genesys agent is not mapped to a Ticketing user.",
  "code": "GENESYS_AGENT_NOT_MAPPED"
}
```

### The same mapping on contracts 1 and 3

`agentId` on Create Ticket and Update Ticket **is the Genesys User ID** (it
always was the handling agent's identifier; `agentName` is display-only).
When it is supplied and maps to an active Ticketing user, that user is
recorded as the interaction's handler exactly as above, apply-if-absent.
Those two contracts are integration events — a queue or system may send them
before any agent exists — so an absent or unmapped `agentId` there is **not**
refused: the ticket is still created/updated, the verbatim `agentId` is kept,
ownership stays null, and the ingestion audit entry names the outcome
(`AgentMapping=NotMapped(...)`). Only contract 4 — an agent acting — refuses
an unmapped agent.

### Setting up the mapping (UAT)

There is no administration screen for this yet. For UAT the mapping is set
directly on the Ticketing user. Each Genesys User ID may be mapped to at most
one Ticketing user (`UX_AspNetUsers_GenesysUserId`, filtered unique); users
that are not Genesys agents simply keep `NULL`.

```sql
-- @TicketingUserId : AspNetUsers.Id of the existing Ticketing user (uniqueidentifier)
-- @GenesysUserId   : the agent's immutable Genesys User ID
-- @GenesysEmail    : informational; may be NULL
UPDATE AspNetUsers
SET
    GenesysUserId = @GenesysUserId,
    GenesysEmail  = @GenesysEmail
WHERE Id = @TicketingUserId;
```

To remove a mapping, set both columns back to `NULL`. Never create a user
this way — the Ticketing account must already exist (Administration →
Users), with its role and department assignments, before it is mapped.

---

## The digital / chatbot conversation flow

Mandatory for chatbot, website chat, WhatsApp and social conversations.

```
Chat starts                → POST  /api/genesys/tickets          → ticket created
Conversation runs          → Customer / VirtualAgent / HumanAgent / System messages
Chat closes or disconnects → PATCH /api/genesys/tickets/{id}
                             ├─ every transcript message stored
                             ├─ endedAtUtc + endReason recorded
                             ├─ the interaction marked Ended
                             └─ the TICKET stays open
```

The whole conversation is then visible in Ticket Details → Conversation
History, in order, with each line attributed to the customer, the virtual
agent, the human agent or the platform. A human agent taking over later reads
exactly what the bot and the customer said.

---

## Still required from the Genesys team

Nothing above is blocked, but each of these decides how the boundary adapter is
finished. See `Genesys-Integration-Phase1.md` §10 for the full list — the ones
that touch these three contracts directly:

1. **Authentication** — the real scheme for inbound calls. Today it is a
   TigerCS service-account JWT.
2. **Event vocabulary** — the actual values Genesys sends for ringing,
   answered and conversation started/ended, per channel.
3. **Queue ids** — the real Genesys queue identifiers, so an administrator can
   map them to departments. **None are invented.**
4. **Follow-up behaviour per channel** — what continuation Genesys performs on
   each channel, so `handoff.mode` is stated rather than guessed.
5. **Routing-task / work-item ids** — whether Genesys exposes a stable id for
   queued human work.
6. **Transcript delivery** — whether the full transcript is available at
   conversation end or must be collected incrementally. Both are supported;
   only the end-of-conversation path is wired.
7. **Bot-authored lines** — how Genesys labels a virtual agent's messages, so
   they map to `VirtualAgent` rather than `System`.
8. **Agent identity** — confirmation that the `agentId` Genesys sends (and
   `genesysUserId` on the agent-context call) is the agent's immutable Genesys
   User ID, and the list of agent Genesys User IDs (+ emails) to map to the
   UAT Ticketing accounts.
