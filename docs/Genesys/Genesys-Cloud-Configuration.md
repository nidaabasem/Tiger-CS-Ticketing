# Genesys Cloud ↔ TigerCS: configuration guide

This guide covers everything the Genesys Cloud administrator configures. The
TigerCS side is complete: no further TigerCS development is needed for the
flows below. What remains is Genesys configuration, credentials, and three
TigerCS reference-data entries (queue → department, agent → user, and
switching the integration on).

| File | What it is |
|---|---|
| `data-actions/00-custom-auth-request-config.json` | Request configuration for the auto-created **Custom Auth** action |
| `data-actions/01-customer-lookup.json` … `06-cancel-human-request.json` | Data actions in Genesys' import format |
| `TigerCS-Genesys.postman_collection.json` | End-to-end Postman run of every call below |
| `../architecture/Genesys-API-Contracts.md` | The HTTP contracts in full |

---

## 1. How it fits together

```text
Genesys Cloud ──OAuth2 client_credentials──▶ TigerGroupWeb  (https://tigergroup.ae/api/genesys/…)
   Architect / Scripts / Workflows              │  Bearer JWT, scope ticketing.genesys
   call Data Actions                            │  TicketingGenesysService (internal, TigerCS service account)
                                                ▼
                                             TigerCS API  (/api/genesys/…)  ── CRM / PACT / Tasleeh
```

* Genesys only ever holds an **OAuth client id and secret** for TigerGroupWeb.
  It never holds the TigerCS service-account username or password, and never
  uses the old `X-API-Key`. A request that carries only that key gets `401`.
* TigerGroupWeb forwards the three routes to TigerCS unchanged, so the
  request and response bodies below are the TigerCS contracts exactly.
* **One Genesys conversation is one TigerCS ticket.** `conversationId` is the
  idempotency key. Retries, transfers, queue changes, AI → human handoff and
  the end of the conversation all act on that same ticket. Nothing in
  Genesys can create a second ticket for a conversation.

| # | Contract | Route (on `https://tigergroup.ae`) | Data action(s) |
|---|---|---|---|
| 1 | Customer lookup | `GET /api/genesys/customers/lookup?phoneNumber={ANI}` | `TigerCS - Customer Lookup` |
| 2 | Create / reuse interaction ticket | `POST /api/genesys/tickets` | `TigerCS - Create or Reuse Ticket` |
| 3 | Conversation update | `PATCH /api/genesys/tickets/{ticketId}` | `TigerCS - Update Routing`, `TigerCS - End Conversation`, `TigerCS - Request Human Agent`, `TigerCS - Cancel Human Request` |

Contract 3 is **one** endpoint. It has four data actions only because a
Genesys data action has a fixed request template. Each variant sends just
the part of the body it owns.

---

## 2. Credentials and OAuth client

Done by the TigerGroupWeb operator. This is configuration, not code.

1. Generate a signing key of at least 32 random bytes, base64-encoded, and a
   client secret of at least 32 characters. Use **letters and digits only**
   for the secret so it survives form-encoding unchanged.
2. Set both as **environment variables** on the TigerGroupWeb host. Never
   put them in `appsettings*.json`:
   ```text
   GenesysAuth__SigningKey=<base64 key>
   GenesysAuth__Clients__genesys-uat__ClientSecret=<secret>
   ```
   Production uses its own client entry (for example `genesys-prod`) under
   `GenesysAuth:Clients`, with `AllowedScopes: ["ticketing.genesys"]`.
3. Give the Genesys administrator the client id and the secret through a
   secure channel.
4. The TigerCS service account TigerGroupWeb uses internally (`Ticketing:
   BaseUrl / Username / Password`) is a TigerCS **CS Agent** account. Supply
   its password to TigerGroupWeb as an environment variable
   (`Ticketing__Password`), never through Genesys.

Check it with Postman request `0.1`. The token response is:

```json
{ "access_token": "eyJ…", "token_type": "Bearer", "expires_in": 3600, "scope": "ticketing.genesys" }
```

---

## 3. Genesys integration and data actions

### 3.1 Integration

**Admin → Integrations → + Integrations → Web Services Data Actions.**

* Name: `TigerCS Ticketing`
* **Configuration → Credentials → Configure**:
  * Credential Type: **User Defined (OAuth)**
  * Add two fields: `clientId` and `clientSecret`, filled with the values from step 2.
* Save and set the integration to **Active**.

### 3.2 Custom Auth action

Genesys creates an action called **Custom Auth** under the integration. You
cannot rename or delete it, and you cannot edit its contracts. Edit its
**Configuration → Request** so it matches
`data-actions/00-custom-auth-request-config.json`:

| Setting | Value |
|---|---|
| Request URL Template | `https://tigergroup.ae/api/genesys/oauth/token` |
| Request Type | `POST` |
| Headers | `Content-Type: application/x-www-form-urlencoded` |
| Request Template | `grant_type=client_credentials&scope=ticketing.genesys&client_id=$esc.url(${credentials.clientId})&client_secret=$esc.url(${credentials.clientSecret})` |
| Response | leave as passthrough (`${rawResult}`) |

Genesys caches the token, reuses it until it expires or a call using it fails,
then fetches a new one and retries. **Genesys does not attach the token for
you.** Every TigerCS action therefore sets this header, which the provided
JSON files already contain:

```text
Authorization: ${authResponse.token_type} ${authResponse.access_token}
```

### 3.3 Import the data actions

**Admin → Integrations → Actions → Import**, once for each of `01`–`06`.
Pick the `TigerCS Ticketing` integration each time, then **Publish** each
action.

Use **Test** on `TigerCS - Customer Lookup` with `phoneNumber = tel:+971500000002`
to confirm the authentication chain before building the flows.

---

## 4. TigerCS reference data (TigerCS administrator)

| What | Where | Why |
|---|---|---|
| Switch the integration on | `Genesys:Enabled = true` in the TigerCS API configuration | While off, every Genesys endpoint answers `503` and writes nothing |
| **Queue → department mapping**, one per Genesys queue that can receive calls or chats | TigerCS Administration → Genesys routing (`POST /api/admin/genesys/queue-mappings`, with the Genesys **queue id**) | Decides which department's queue the ticket lands in. An unmapped queue with no `departmentCode` is refused `422`, never routed to a guess |
| Agent mapping, for each agent | Set the agent's Genesys **User ID** on their Ticketing user (see `Genesys-API-Contracts.md` §4) | Lets TigerCS record which Ticketing user handled the interaction. An unmapped agent never blocks anything |

Call Center agents stay exactly as they are today: Role **CS Agent**,
Department **Call Center**. This integration changes no permissions.

---

## 5. Voice: inbound call flow

**Architect → Inbound Call Flow** (the flow your DID routes to). Add these
steps at the start of the flow, before the menu or the Transfer to ACD.

```text
Start
 ├─ Call Data Action  "TigerCS - Customer Lookup"
 │     phoneNumber        ← Call.Ani
 │     outputs → Task.TcsFound, Task.TcsCustomerName, Task.TcsVerificationSource,
 │               Task.TcsUnitsText, Task.TcsRecentTicketsText, Task.TcsOpenTicketCount …
 │     Failure / Timeout path → continue (never block the call)
 │
 ├─ (optional) menu / IVR choice → Task.TcsDepartmentCode
 │
 ├─ Call Data Action  "TigerCS - Create or Reuse Ticket"
 │     conversationId     ← Call.ConversationId
 │     channel            ← "Phone"
 │     customerPhone      ← Call.Ani                    (tel:+971… is fine, TigerCS normalizes it)
 │     customerName       ← Task.TcsCustomerName        (empty when unknown; TigerCS fills it from CRM)
 │     departmentCode     ← Task.TcsDepartmentCode      (optional; wins over the queue mapping)
 │     queueId            ← ToString(Task.TargetQueue.id)   the queue this flow is about to transfer to
 │     queueName          ← Task.TargetQueue.name
 │     calledNumber       ← Call.CalledAddressOriginal
 │     direction          ← "Inbound"
 │     startedAtUtc       ← ToString(Flow.StartDateTimeUtc)
 │     outputs → Task.TcsTicketId, Task.TcsTicketNumber
 │     Failure / Timeout path → continue to Transfer to ACD
 │
 ├─ Set Participant Data
 │     TigerCsTicketId          = Task.TcsTicketId
 │     TigerCsTicketNumber      = Task.TcsTicketNumber
 │     TigerCsCustomerName      = Task.TcsCustomerName
 │     TigerCsVerificationSource= Task.TcsVerificationSource
 │     TigerCsUnits             = Task.TcsUnitsText
 │     TigerCsRecentTickets     = Task.TcsRecentTicketsText
 │     TigerCsCustomerFound     = ToString(Task.TcsFound)
 │
 └─ Transfer to ACD  (Task.TargetQueue, script = "TigerCS Agent Script")
```

Notes:

* `Task.TargetQueue` is a **Queue** variable you set, for example with
  `FindQueue("Customer Service")` or from your routing logic. It must be
  the queue you then transfer to, because TigerCS turns that queue id into
  the ticket's department.
* An unknown caller is normal. `found` is `false`, the screen-pop fields are
  empty strings, and the ticket is still created with the caller's number.
  The agent identifies the customer later, in TigerCS.
* A withheld number (`tel:anonymous`) is also normal. The lookup answers
  `found: false` and the ticket is created with no phone.
* If the call is re-routed through a second flow, calling Create again is
  harmless: it answers `AlreadyIngested` with the same ticket.
* Participant data values are strings of up to 500 characters, and the last
  write wins. In voice flows Genesys syncs them when the flow ends, which is
  before the agent is alerted.
* **Consequence to accept:** because the ticket is created before queueing,
  a caller who hangs up in queue still leaves an Unclassified ticket, with
  its conversation ended by the workflow in §8. This is the approved "create
  on ConversationId" rule.

---

## 6. Live Chat: inbound message flow

**Architect → Inbound Message Flow** used by the Web Messaging deployment.

```text
Start
 ├─ Get Participant Data   (optional: values the website set through Messenger custom attributes)
 │     customerName  → Task.TcsName,  customerEmail → Task.TcsEmail,
 │     customerPhone → Task.TcsPhone, department    → Task.TcsDepartmentCode
 │
 ├─ Call Data Action  "TigerCS - Create or Reuse Ticket"
 │     conversationId  ← Message.ConversationId
 │     channel         ← "LiveChat"
 │     customerName    ← Task.TcsName        (empty if not supplied)
 │     customerEmail   ← Task.TcsEmail
 │     customerPhone   ← Task.TcsPhone
 │     departmentCode  ← Task.TcsDepartmentCode
 │     queueId         ← ToString(Task.TargetQueue.id)
 │     queueName       ← Task.TargetQueue.name
 │     direction       ← "Inbound"
 │     startedAtUtc    ← ToString(Flow.StartDateTimeUtc)
 │     outputs → Task.TcsTicketId, Task.TcsTicketNumber
 │     Failure path → continue
 │
 ├─ Set Participant Data  TigerCsTicketId, TigerCsTicketNumber
 │
 ├─ (bot / virtual agent, if used, see §9)
 │
 └─ Transfer to ACD (Task.TargetQueue, script = "TigerCS Agent Script")
```

* `channel` may be `LiveChat` or `WebMessaging`. Both record the TigerCS
  **Live Chat** channel.
* Identity is optional. With no name, email or phone the ticket is still
  created, and the agent enriches it in TigerCS.
* Retries with the same `Message.ConversationId` always return the same
  `ticketId`.

---

## 7. Agent script: screen pop and "agent connected"

Create **Scripts → TigerCS Agent Script** and select it in both flows'
Transfer to ACD.

1. **Script properties → Data Actions: on.** Agents need the permissions
   *Integrations > Action > View* and *Bridge > Actions > Execute*.
2. **Input variables** whose names match the participant data exactly,
   case-sensitive, with **Input** ticked: `TigerCsTicketId`,
   `TigerCsTicketNumber`, `TigerCsCustomerName`, `TigerCsVerificationSource`,
   `TigerCsUnits`, `TigerCsRecentTickets`, `TigerCsCustomerFound`.
3. **Screen pop.** Show those variables, plus a link that opens the ticket in
   TigerCS. The agent never has to search by phone.
4. **Script Load → Execute Data Action `TigerCS - Update Routing`.** This
   call is the "agent connected" signal:

   | Input | Value |
   |---|---|
   | `ticketId` | `{{TigerCsTicketId}}` |
   | `conversationId` | `{{Scripter.Interaction Id}}` |
   | `queueId` | `{{Scripter.Queue ID}}` |
   | `queueName` | `{{Scripter.Queue Name}}` |
   | `agentId` | `{{Scripter.Agent Id}}` |
   | `agentName` | `{{Scripter.Agent Name}}` |

   TigerCS then records the current queue and agent on the interaction. The
   first mapped agent becomes the interaction's handler. If the
   conversation's human work was `WaitingForAgent` (after an AI handoff),
   TigerCS marks it **Assigned** to this agent. The ticket's department,
   owner and status are **not** changed.

   A transferred call loads the script for the new agent, so a transfer
   records the new agent on the **same** ticket with no extra configuration.

Use the script, not the `user.start` trigger, as the "agent connected" signal.
`user.start` fires when the conversation is *offered* to an agent, even one
who never answers.

---

## 8. Workflows: queue changes and conversation end

**Architect → Workflow** plus **Admin → Triggers**. These run server-side,
so the end of a conversation is recorded even when no agent ever connected
(for example, abandoned in queue or the chat closed while waiting).

### 8.1 Workflow `TigerCS - Conversation Event`

Input variables, each with *Input to Flow* ticked and named exactly as in
the event: `Flow.conversationId`, `Flow.mediaType`, `Flow.queueId`,
`Flow.disconnectType`.

```text
Start
 ├─ Update Data  Task.channel = If(Flow.mediaType == "voice", "Phone", "LiveChat")
 ├─ Call Data Action "TigerCS - Create or Reuse Ticket"         ← resolves the ticket id
 │     conversationId ← Flow.conversationId, channel ← Task.channel, queueId ← Flow.queueId
 │     outputs → Task.ticketId      (answers AlreadyIngested with the existing ticket;
 │                                   creates it only if the flow's call never happened)
 ├─ Decision: IsSet(Flow.disconnectType) and Flow.disconnectType != ""
 │    yes → Call Data Action "TigerCS - End Conversation"
 │              ticketId ← Task.ticketId, conversationId ← Flow.conversationId,
 │              endedAtUtc ← ToString(GetCurrentDateTimeUtc()), endReason ← Flow.disconnectType
 │    no  → Call Data Action "TigerCS - Update Routing"
 │              ticketId ← Task.ticketId, conversationId ← Flow.conversationId, queueId ← Flow.queueId
 └─ End
```

Re-calling Create to get the ticket id is intentional. It is idempotent,
needs no Genesys API permission, and it also repairs a conversation whose
flow-time Create call failed.

### 8.2 Triggers

Data format **TopLevelPrimitives**, target the workflow above:

| Trigger | Topic | Match criteria (recommended) |
|---|---|---|
| TigerCS queue change | `v2.detail.events.conversation.{id}.acd.start` | `mediaType` in `voice`, `message` |
| TigerCS conversation end | `v2.detail.events.conversation.{id}.customer.end` | `mediaType` in `voice`, `message` |

`acd.start` fires again on every queue transfer, which is exactly the "queue
changed" update. `customer.end` fires once. Ending never closes the ticket;
the response echoes the unchanged `ticketStatus` to show it.

> Confirm the event field names in your org's trigger schema when you create
> the input variables (`conversationId`, `mediaType`, `queueId`,
> `disconnectType`). Genesys matches them by exact, case-sensitive name.

---

## 9. AI / virtual agent → human handoff (Live Chat)

The TigerCS rules are unchanged:

```text
AI conversation ─▶ customer asks for a human, or the AI connection drops
                ─▶ "TigerCS - Request Human Agent"      → WaitingForAgent (same TicketId)
                ─▶ Transfer to ACD, agent connects
                ─▶ script load "TigerCS - Update Routing" → Assigned (same TicketId)
                ─▶ agent accepts in TigerCS (Pending Customer Interactions → Start)
                     → ticket owner assigned, Open → InProgress
First Human Response is NOT recorded on accept. It is recorded on the first
actual HumanAgent response only.
```

In the message flow, on the bot's escalation / "agent requested" exit, call:

| Input | Value |
|---|---|
| `ticketId` | `Task.TcsTicketId` |
| `conversationId` | `Message.ConversationId` |
| `trigger` | `"CustomerRequestedHuman"`, or `"AiConnectionLost"` on the bot's failure path, or `"AiEscalated"` |
| `reason` | the bot's escalation reason / intent |
| `mode` | leave empty unless it is actually known |

If the AI reconnects and a human is no longer needed, call `TigerCS - Cancel
Human Request` with a `reason`. If the conversation ends while the work is
still waiting, TigerCS keeps it outstanding. It never orphans the ticket.

---

## 10. Variable reference

### Participant data written by the flows

| Attribute | Value |
|---|---|
| `TigerCsTicketId` | TigerCS ticket id (string) |
| `TigerCsTicketNumber` | TigerCS ticket number |
| `TigerCsCustomerName` | Matched customer name, or empty |
| `TigerCsVerificationSource` | `Crm`, `Pact`, `Tasleeh`, or empty |
| `TigerCsUnits` | `"Project - Unit; …"` |
| `TigerCsRecentTickets` | `"TG-… (Status); …"` |
| `TigerCsCustomerFound` | `true` / `false` |

### Genesys built-ins used

| Where | Variable |
|---|---|
| Inbound call flow | `Call.Ani`, `Call.ConversationId`, `Call.CalledAddressOriginal`, `Flow.StartDateTimeUtc` |
| Inbound message flow | `Message.ConversationId`, `Flow.StartDateTimeUtc`, Get Participant Data (Messenger custom attributes) |
| Script | `Scripter.Interaction Id`, `Scripter.Agent Id`, `Scripter.Agent Name`, `Scripter.Queue ID`, `Scripter.Queue Name` |
| Workflow (trigger) | `Flow.conversationId`, `Flow.mediaType`, `Flow.queueId`, `Flow.disconnectType` |

---

## 11. Data action schemas (summary)

The JSON files are the source of truth. This is what each action exchanges
with Architect.

**TigerCS - Customer Lookup**
Input: `phoneNumber` (required).
Output: `found` (bool), `phoneNumber`, `crmStatus`, `customerName`,
`customerEmail`, `verificationSource`, `externalCustomerId`,
`matchedCustomerCount` (int), `units` (string collection), `unitsText`,
`recentTicketNumbers` (collection), `openTicketNumbers` (collection),
`recentTicketsText`, `openTicketCount` (int).

**TigerCS - Create or Reuse Ticket**
Input: `conversationId`\*, `channel`\* (`Phone` | `LiveChat`),
`customerPhone`, `customerName`, `customerEmail`, `departmentCode`,
`queueId`, `queueName`, `agentId`, `agentName`, `calledNumber`, `direction`,
`subject`, `towerName`, `unitNumber`, `startedAtUtc`.
Output: `outcome` (`TicketCreated` | `AlreadyIngested`), `ticketId`, `ticketNumber`.

**TigerCS - Update Routing / End Conversation / Request Human Agent / Cancel Human Request**
Input: `ticketId`\*, `conversationId`\*, plus the variant's own fields.
Output: `outcome`, `ticketId`, `ticketNumber`, `ticketStatus`,
`conversationEnded`. Request Human Agent also returns `handoffStatus`.

Unset inputs are sent as empty strings, and TigerCS treats an empty value as
not supplied.

### Failure handling in flows

Always wire the data action's **Failure** and **Timeout** paths to continue
the interaction (Transfer to ACD / keep the chat going). The customer must
never be dropped because TigerCS is unavailable.

| Status | Meaning | Flow action |
|---|---|---|
| `401` | Token or client problem | Continue; alert the integration owner |
| `422` Create | Queue not mapped to a department | Continue; add the queue mapping in TigerCS |
| `503` | `Genesys:Enabled` is off | Continue |

---

## 12. Go-live checklist

- [ ] TigerGroupWeb: signing key + client secret set as environment variables; `Ticketing__Password` set
- [ ] TigerCS: `Genesys:Enabled = true`; every Genesys queue mapped to a department; agents mapped
- [ ] Genesys: integration active; Custom Auth configured; 6 actions imported and published
- [ ] Voice inbound call flow: lookup → create → participant data → transfer (§5)
- [ ] Inbound message flow: create → participant data → (bot handoff) → transfer (§6, §9)
- [ ] Agent script with input variables and the load-time routing action (§7)
- [ ] Workflow + two triggers (§8)
- [ ] Postman collection run green against UAT
