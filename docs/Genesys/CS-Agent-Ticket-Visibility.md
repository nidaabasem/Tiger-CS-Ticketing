# CS Agent ticket visibility — assignment and Genesys handling

A **CS Agent** no longer sees every ticket. They see a ticket only when **either**

1. the ticket is **currently assigned to them** (`Tickets.CurrentOwnerEmployeeId`), **or**
2. they **actually handled a Genesys interaction** on it — a call they answered, a chat or other supported interaction they were connected to — recorded in `TicketInteractionHandlers`.

Belonging to the ticket's department, or to the Genesys queue it arrived on, grants nothing. An unassigned ticket nobody handled is visible to no CS Agent. A ticket handled by several agents (a transfer) is visible to each of them. Reassigning a ticket takes away the previous assignee's *assignment* access but never the access of an agent who *handled* the interaction.

CS Supervisor, CS Manager, General Manager, Chairman/CEO and System Administrator are **unchanged**: they still see every ticket. A person who holds CS Agent *and* one of those roles keeps the wider view. A CS Agent who also holds Department Employee/Head keeps that role's own department scope, on top of their assigned/handled tickets. The Genesys integration account (a "CS Agent" by permission set, listed as a service identity) is not a person and is not restricted — it stays confined to the integration surface exactly as before.

## What the Genesys integration records (and what it does not)

Before this change, `TicketInteractions.HandledByUserId` held **one** user per interaction: the first mapped agent Genesys named, never overwritten. A call transferred from agent A to agent B only ever named A, so B could not be recognised as having handled it.

`TicketInteractionHandlers` now holds **one row per Ticketing user per interaction** (unique on interaction + user), written only when Genesys reports an agent as **connected**, and only when that agent's Genesys User ID resolves — server-side, through `AspNetUsers.GenesysUserId` — to an **active** Ticketing user. `HandledByUserId` is unchanged ("who took it first").

| Genesys event | Records a handler? | Why |
|---|---|---|
| `POST /api/genesys/tickets` naming `agentId` | Yes (`Inquiry`) | The ticket is created when the call is answered / chat accepted; the contract states that ringing never reaches TigerCS |
| `PATCH …` `routing.agentId` | **Yes** (`Routing`), with optional `routing.connectedAtUtc` | Documented as "the agent now **connected**"; this is the event a transfer arrives as |
| `PATCH …` top-level `agentId` | Yes (`Update`, or `ConversationEnd` when it carries `ended`) | Names the handling agent |
| `handoff.assignedAgentId` | **No** | Records that work was *assigned/offered*; an assignment is not proof of answering |
| `POST /api/genesys/agent-context` (screen pop) | **No** | A screen pop can fire while the interaction is still alerting. It still sets the legacy `HandledByUserId`, as before |
| Queue / department membership, queue change with no agent | **No** | Never proof of handling |
| An agent id that is not mapped (a bot, an unknown agent) or is deactivated | **No** | Resolves to no active user; the verbatim id stays on the interaction as before |
| A redelivered event | No second row | Unique per interaction + user |

Every recorded handler writes a `GenesysInteractionHandlerAdded` audit entry (conversation, ticket, Genesys User ID, resolved user, source, connected time).

### What the Genesys flow must send

* On a **transfer**, send a routing update naming the **receiving** agent's Genesys User ID **when they connect** (not when the call is offered to them). The agent who transferred away keeps access because they were already recorded.
* Never put a bot / virtual-agent id in `agentId` or `routing.agentId`. It would not map to a Ticketing user and is ignored, but it is wrong data.
* `routing.connectedAtUtc` (new, optional) is Genesys' connected time for that agent; send it if the flow has it.

### Existing data

The migration `AddTicketInteractionHandlers` copies every existing `TicketInteractions.HandledByUserId` into the new table (`Source = Legacy`), so agents keep access to tickets the integration had already attributed to them. That column could also have been set by the screen pop, which cannot be told apart afterwards. Transfers that happened **before** this change were never recorded beyond the first agent and cannot be reconstructed from the database; the audit trail's `GenesysRoutingChanged` entries hold the agents each conversation passed through if a back-fill is ever wanted.

An agent id that was **unmapped** when the call happened is not recorded, so mapping that agent later does not retroactively grant them the tickets of earlier calls.

## Where it is enforced

All server-side; the web UI only reflects it.

| Surface | How |
|---|---|
| Ticket list and search, filters, paging, totals | `TicketQueryFilters.VisibleTo` — one SQL predicate (assigned **or** `EXISTS` handler row); department/owner/search filters only narrow it |
| Ticket details, history, interactions, notes, reviews, approvals, SLA, customer profile/history | Ticket-level check (`TicketVisibilityRule.CanViewTicketAsync`) — 403 for a ticket the agent may not see |
| Dashboard summary and overview (KPIs, breakdowns, ageing, recent/attention lists, human-wait tile) | Same predicate over the agent's scope |
| Customer directory, customer history, documents | Only customers with at least one visible ticket |
| Pending human work (Awaiting Human Agent) | A work item is listed/actionable only when its ticket is visible to the agent |
| Every endpoint addressed by ticket id (`api/tickets/{id}/…`, SLA, downgrade requests) | `[RequireTicketAccess]` resource filter: a restricted CS Agent gets **403 before the request body is read**; a nonexistent ticket is still 404 |
| Close, reopen, classify, request a priority downgrade | The role gate is unchanged; a CS Agent additionally needs the ticket to be visible |

The set of department ids a restricted agent is resolved to is **empty, never "no restriction"**, so any future query that forgets the agent rule shows them nothing rather than everything.

## Consequences to be aware of

* **The Awaiting Human Agent list no longer lets a CS Agent browse and claim waiting work.** A waiting item has, by definition, no handler yet; it reaches an agent when Genesys connects them (then it appears and they can start it). Supervisors/managers still see and can act on all of it.
* **A ticket a CS Agent creates by hand in the New Ticket wizard** (no Genesys interaction, not assigned to them) is not visible to them afterwards unless it is assigned to them. Only Genesys-handled or assigned tickets qualify, as specified.
* Reports: the Team Performance report is limited to the CS Manager tier and has no export, so it is unaffected.
