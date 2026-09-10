# Genesys ↔ TigerCS — the three integration contracts

Genesys asked for three capabilities. This is the whole externally consumable
surface: **there is nothing else on `api/genesys`.**

| # | Capability | Route |
|---|---|---|
| 1 | Create Ticket, any channel | `POST /api/genesys/tickets` |
| 2 | Customer lookup on call pickup | `GET /api/genesys/customers/lookup?phoneNumber=` |
| 3 | Update Ticket | `PATCH /api/genesys/tickets/{ticketId}` |

All three are behind the `Genesys:Enabled` feature flag and answer `503` while
it is off.

---

## Authentication (UAT)

**There is no Genesys-specific credential, and none is invented.** No Genesys
API base URL, OAuth client or webhook signing secret exists in this system,
because none has been confirmed by the Genesys team.

Genesys calls TigerCS as an ordinary authenticated **service account**, using
the same JWT bearer authentication every other client of this API uses:

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
touching anything behind it.

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

**A ringing call creates nothing** — `event: "Ringing"` answers `204 No
Content`. The flow starts at `"Answered"` (agent picked up) or `"Started"` (a
text conversation reached an agent).

**A customer-lookup failure never blocks creation.** Nor does a missing phone
number (withheld caller id, a social DM).

### Request

```json
{
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "channel": "WebsiteChat",
  "event": "Started",
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
| `204` | `event: "Ringing"` — accepted, nothing created |
| `400` | Unrecognized `channel`/`event`, or blank `conversationId` |
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

`sender` is one of `Customer`, `Agent`, `VirtualAgent`, `System`. Anything else
is rejected — the sender is never silently attributed.

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

### Idempotency

- Resending an already-stored transcript stores nothing twice —
  `transcriptMessageCount` stays put.
- A redelivered end does not move the recorded end time.
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
