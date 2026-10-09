# Genesys tickets: default priority, request-type routing and SLA

## Default priority

A new Genesys ticket is created on the configured default priority instead of a null priority.

* Config: `Genesys:DefaultTicketPriority` (default `Normal`). Resolved at ingestion from the `Priorities` table: a row with exactly that name wins, otherwise the documented alias applies (`Normal` = the **Medium** tier, `PriorityAliases`). No id is hard-coded.
* Unresolvable / blank config: the inquiry is still created (no priority, no SLA, as before) and the ingestion audit entry records `DefaultPriority=(none…)`.
* Re-ingesting the same `conversationId` returns the same ticket (`AlreadyIngested`) and changes nothing. Historical tickets are not updated.
* Changing it: the existing Ticket Details flow (priority downgrade request + Department Head approval). `POST /api/tickets/{id}/classification` on a defaulted ticket keeps the running SLA period; a more urgent priority replaces it under the earlier-of rule (ADR-0012), a less urgent one is refused (`DowngradeRequiresApproval`).

## Request-type routing

`POST /api/genesys/tickets` accepts `requestType: { requestTypeId | name }`; `PATCH /api/genesys/tickets/{id}` accepts the same part when the bot identifies it later. Both go through `GenesysRequestTypeClassificationAppService` → `TicketRequestTypeRoutingService`:

1. **Validate**: exists, active, unambiguous by name, responsible department active, published workflow. Otherwise nothing is written (PATCH: `422`; POST: ticket still created, awaiting classification).
2. **Classify + pin** the published workflow version.
3. **Department**: if the request type's department differs, `Ticket.TransferToDepartment` (the same transfer semantics as the manual transfer: owner cleared, audited `Transfer`). `OriginatingDepartmentId` is never changed.
4. **Assignment**: the existing `TicketAutoAssignmentService` (trigger `DepartmentTransfer`, or `RequestTypeClassified` when the department did not change). No eligible employee ⇒ the responsible department's queue. A ticket a person already owns, and that did not move, keeps its owner.
5. **SLA**: see below.
6. **Audit**: `ClassifyRequestType`, `Transfer`, `AutoAssign`, `ReapplySlaPolicy`.

Idempotency: the same request type again ⇒ `AlreadyClassified`, no writes; a different one ⇒ `409`; ticket closed ⇒ `409`.

**Missing / unresolved request type**: the ticket stays unclassified in its arrival department and a human follow-up item is raised through the existing handoff queue (trigger `RoutingDecision`, reason prefix `Awaiting classification`). When a request type is classified later, that item — and only an item with that reason — is cancelled. `Genesys:HumanQueueForUnclassified=false` disables the queue entry.

Response fields: `classificationStatus` (`Classified` | `AwaitingClassification`), `requestTypeId`, `departmentId`, `assignedEmployeeId` (null = department queue), `classificationDetail`.

## SLA

**Confirmed start-time rule:** the SLA clock starts at **ticket creation** (`Ticket.CreatedAtUtc`, ISSUE-001 Option C), never at the Genesys interaction time, and never backdated. Before this change Genesys tickets were the exception (no priority ⇒ no period ⇒ clock started at classification). They now follow the general rule because they carry the default priority. Only a ticket with no priority at all (created before this change) still starts at classification — the moment a request type or category/priority is applied, not backdated.

* Policy: the request-type SLA row for (request type, ticket priority) when it is active, creation-triggered, has an explicit clock basis and single values (`RequestTypeSlaEnforcement`); otherwise the per-priority policy (Normal ⇒ Medium). Ticket Details states which applied and exactly why a request-type SLA was not applied, or that none is configured.
* On later classification the running period is updated **in place from its original start**: no new clock, elapsed time kept. A deadline already breached, a First Response already answered, or (Resolution) a period with pause history is left exactly as it is. Nothing is written if nothing would change.
* First Response is stopped only by `FirstHumanResponseRecorder` (a `HumanAgent` transcript line, or the explicit record-first-response call). A bot reply, assignment, department transfer, status change or the automated acknowledgement never count.
* Priority-change, approval, pause/resume and business-hours rules are unchanged.

## Configuration / migration

* No schema change and no migration.
* Requires the `Priorities` rows and the Medium `SlaPolicies` row (already seeded everywhere), and request types with a published workflow, a department, optionally an assignment rule and SLA rows.
* Optional keys: `Genesys:DefaultTicketPriority`, `Genesys:HumanQueueForUnclassified`.
* Genesys data actions `02` (create) and `03` (update routing) gained an optional `requestTypeName` input. Existing flows keep working unchanged.
