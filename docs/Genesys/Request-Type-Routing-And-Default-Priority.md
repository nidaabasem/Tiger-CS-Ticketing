# Genesys tickets: default priority, request-type routing and SLA

## Default priority

A new Genesys ticket is created on the configured default priority instead of a null priority.

* Config: `Genesys:DefaultTicketPriority` (default `Normal`). Resolved at ingestion from the `Priorities` table: a row with exactly that name wins, otherwise the documented alias applies (`Normal` = the **Medium** tier, `PriorityAliases`). No id is hard-coded.
* Unresolvable / blank config: the inquiry is still created (no priority, no SLA, as before) and the ingestion audit entry records `DefaultPriority=(none…)`.
* Re-ingesting the same `conversationId` returns the same ticket (`AlreadyIngested`) and changes nothing. Historical tickets are not updated.
* Changing it: the existing Ticket Details flow (priority downgrade request + Department Head approval). `POST /api/tickets/{id}/classification` on a defaulted ticket keeps the running SLA period; a more urgent priority replaces it under the earlier-of rule (ADR-0012), a less urgent one is refused (`DowngradeRequiresApproval`).

## Request-type routing

`POST /api/genesys/tickets` accepts `requestType: { requestTypeId | name }`; `PATCH /api/genesys/tickets/{id}` accepts the same part when the bot identifies it later. Both go through `GenesysRequestTypeClassificationAppService` → `TicketRequestTypeRoutingService`:

1. **Validate**: exists, active, unambiguous by name, responsible department active, published workflow. Otherwise nothing is written: PATCH and POST both return `422` (POST creates no ticket, intake record, interaction or SLA; a retry without the type, or with a valid one, succeeds).
2. **Classify + pin** the published workflow version.
3. **Department**: if the request type's department differs, `Ticket.TransferToDepartment` (the same transfer semantics as the manual transfer: owner cleared, audited `Transfer`). `OriginatingDepartmentId` is never changed.
4. **Assignment**: the existing `TicketAutoAssignmentService` (trigger `DepartmentTransfer`, or `RequestTypeClassified` when the department did not change). No eligible employee ⇒ the responsible department's queue. A ticket a person already owns, and that did not move, keeps its owner.
5. **SLA**: see below.
6. **Audit**: `ClassifyRequestType`, `Transfer`, `AutoAssign`, `ReapplySlaPolicy`.

Idempotency: the same request type again ⇒ `AlreadyClassified`, no writes; ticket closed ⇒ `409`.

**Changing the classification.** A different valid request type *replaces* the current one (the AI refining its own classification) only while no person has worked the ticket: status `Open`, no Category set by hand, no first human response, no human agent has accepted the interaction, and no approval has been raised. The change runs the same transfer / auto-assignment / SLA re-policy path (clock never restarted; `OriginatingDepartmentId` untouched), re-pins the workflow version, and keeps history as a `RequestTypeReclassified` workflow event plus a `ReclassifyRequestType` audit entry. Once a person has worked the ticket, a different request type is refused with `409` and nothing changes.

**Missing request type** (none supplied; an explicitly invalid one is a `422`, see Validate above): the ticket is created with Normal priority and stays **Unclassified** in its arrival department, for the existing manual classification workflow (Classify Ticket). **No human handoff is recorded**: a missing field says nothing about whether the customer wants a person, and only an explicit Genesys handoff signal may do that. (Earlier versions raised a `RoutingDecision` handoff with reason `Awaiting classification…` and a `Genesys:HumanQueueForUnclassified` switch; both are gone. Rows that earlier version already wrote are stood down automatically when the ticket is classified, and can be cleaned up in bulk with `CancelLegacyAwaitingClassificationHandoffs.sql`.)

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
* Optional keys: `Genesys:DefaultTicketPriority`.
* Genesys data actions `02` (create) and `03` (update routing) gained an optional `requestTypeName` input. Existing flows keep working unchanged.
