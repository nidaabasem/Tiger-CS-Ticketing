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

## Two deliberate exceptions

### 1. Awaiting Human Agent — view and claim waiting work in your own department

A CS Agent still sees the Awaiting Human Agent list, but only **unresolved work in departments they belong to** (plus work on tickets they can already see). The dashboard "awaiting" tile counts the same set. This exposes the *work item* only (ticket number, summary, customer, channel, wait time): `GET /api/tickets/{id}` and every other ticket endpoint still answer 403 until the claim.

Claiming (`POST /api/pending-customer-interactions/{id}/start`) is enforced server-side by the existing claim:

* the agent must belong to the work item's department **and** to the ticket's current department — otherwise 403 / `AgentNotInTicketDepartment`, and nothing changes;
* the claim is exclusive: a second agent is told who holds it (409), the handoff `RowVersion` and the ticket's concurrency token close the interleaved-commit window (`ConcurrencyConflict`);
* a successful claim assigns the ticket to the claimer, supersedes the previous assignment row and moves Open → InProgress — the existing workflow, unchanged. From then on the ticket is theirs by assignment;
* the exception is for **claiming only**: an agent cannot complete or cancel work they have not claimed, and read-only roles cannot claim.

### 2. Tickets a CS Agent creates by hand are assigned to them

`POST /api/tickets` (the New Ticket wizard) now makes the creating CS Agent the owner when **all** of these hold:

* the caller is a restricted CS Agent (not a Supervisor/Manager/Admin/service identity) and the ticket is not Genesys-created;
* they did not choose the Department Queue (`assignToDepartmentQueue: true`; the wizard has a "Leave in the department queue" checkbox);
* nothing has already decided: no owner yet, and the request type's rule is not an assignment (`AssignedToPrimary`) or an explicit Department Queue rule, and the department's workflow settings do not disable assignment;
* the creator is an active member of the ticket's current department (the same invariant every assignment honours).

A request type with no rule, no request type, or a rule whose configured assignee went stale fall back to the queue today — those are the cases self-assignment fills. It is written as the creator's own act (`TicketAssignments.AssigningActorEmployeeId` = creator, audit action `SelfAssign`), unlike rule assignment which is a system action. An agent who is not a member of the ticket's department (for example a Call Center agent raising a Facilities ticket) is not self-assigned and cannot open that ticket afterwards.

## Other consequences

* Reports: the Team Performance report is limited to the CS Manager tier and has no export, so it is unaffected.
