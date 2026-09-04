# Workflow & Automation — Phase 2

Status: implemented on top of the Phase 1 configuration foundation
(docs/Workflow-SLA-Configuration-Phase1.md — everything there is preserved:
departments, request types, templates, request-type SLA policies with
triggers/ranges, department workflow settings, and every provisional
business decision). Phases 3+ (approval flows, SLA calculation/pause,
escalation, UI/timeline polish) remain open.

## 1. Architectural boundary: interaction routing vs. ticket workflow

Interaction routing (channel handling, Called Number → Queue, queue/agent
selection) is **Genesys's** responsibility. Tiger-CS Ticketing owns the
**ticket business workflow** (department, request type, assignment rules,
lifecycle, SLA, escalation). Nothing in this phase re-implements routing:

- No Genesys API client, endpoint URL, or payload assumption exists — the
  exact Genesys contracts are not finalized.
- The boundary is a DTO on Ticketing's side:
  `GenesysInteractionContextDto` (Application/Modules/GenesysIntegration)
  — conversation id (required), called number, queue id/name, agent
  id/name, interaction start, direction (all optional). Its XML doc records
  what Genesys is expected to provide later.
- Ticketing persists the context verbatim in `TicketInteractionContexts`
  (one optional row per ticket) for audit/history/reporting and
  Ticket ↔ Genesys conversation traceability — indexed by conversation id,
  external identifiers stored as strings, never foreign keys, and not for
  prominent CS-UI display.
- No `Phone Number → Channel → Queue` table exists in Ticketing, and none
  will be added unless business explicitly asks for a fallback.
- The future reverse direction (Ticketing → Genesys: ticket number/id,
  department, request type, status) is documented on the DTO only; nothing
  is implemented.

## 2. Interaction context and Face-to-Face

`TicketInteractionContext.Source` (`InteractionContextSource`) makes the
distinction explicit: `Genesys` (context provided by Genesys; conversation
id mandatory, everything else optional) vs. `Ticketing` (created locally —
today's Face-to-Face / walk-in exception; Genesys fields are null **by
construction**, the factory does not even accept them).

Face-to-Face uses the existing `Channel.FaceToFaceKiosk` value and the
existing intake flow: the agent captures Channel, Customer Phone (still
mandatory — it is the CRM/PACT/Tasleeh verification identity input),
Department, then Request Type. There is **no separate lifecycle**: after
Department + Request Type, the identical workflow/assignment automation
applies. Customer Phone (identity) is kept distinct from Called Number
(Genesys-side destination datum).

Customer verification is untouched: CRM behavior, PACT
normalization/mapping, Tasleeh, external verification identity, manual
verification, and the New Ticket wizard UX are all unchanged (regression
suite green). Verified identity remains the stable ids, never display
names.

## 3. What Phase 2 added

| Concern | Implementation |
| --- | --- |
| Ticket ↔ Request Type | `Tickets.RequestTypeId` (nullable FK, write-once via `Ticket.ClassifyRequestType`; null = legacy behavior, nothing guessed). Creation validates active + same-department. |
| Assignment configuration | `RequestTypeAssignmentRule` (one per request type): `AssignmentMode` = DepartmentQueue / SpecificEmployee / Team, primary employee id, optional team name, member list (existing employee ids only). Round Robin / Least Workload / project-based are future additive modes — deliberately not implemented. |
| Automatic assignment | `TicketAutoAssignmentService`, run inside ticket creation when Department + Request Type are known. Assigns the rule's primary; every other case (no rule, inactive rule, queue rule, assignee no longer in department, settings disable assignment) falls back to the **department queue** — unassigned, supervisor assigns manually; never a random employee. |
| System vs. human actions | Automatic assignments write `TicketAssignments.AssigningActorEmployeeId = null` and audit entries with a null actor ("AutoAssign", naming the rule and mode); queue fallbacks are audited the same way with the reason. Manual assignment keeps the human actor ("Assign"). History can never show automation as a person. |
| Structured pending | `TicketPendingRecords`: kind (`PendingKind.Customer` / `InternalOrThirdParty`, mirroring the two existing statuses 1:1 — no new TicketStatus), required reason, previous status, started by/at, resumed by/at, correlation id shared with the status-history row. Filtered unique index guarantees at most one open record per ticket. Resolve out of Pending closes the open record too. |
| Transition enforcement | `TicketLifecycleAppService` now requires a `PendingReason` for any move to Pending (all tickets), and enforces the request type's `WorkflowCapabilities` (template ∧ request type) for Pending kinds and Reopen — **narrowing only**; a ticket without a request type behaves exactly as before. |
| Reopen | Request type may disable Reopen (`NotAllowedForRequestType`); where allowed, the existing `ReopenPolicy` (window + CS-layer roles) remains the final, unchanged enforcement point. SLA-on-reopen untouched. |
| Supervisor | `DepartmentWorkflowSettings.SupervisorRoleName` (role name from the fixed set; provisional default Department Head — no dedicated "Department Supervisor" role exists in the approved role set, introducing one is a business decision). Supervisor visibility rides the existing department-scoped authorization; nothing was widened. |
| Department settings enforcement | `AllowAssignment` / `AllowInternalReassignment` / `AllowTransferToOtherDepartments` are now consulted by manual assignment/transfer and by the automation — as **narrowing** gates only (`DisabledByDepartmentSettings`). |

## 4. Fail-safe review of the Phase 1 provisional settings (§30)

The Phase 1 `DepartmentWorkflowSettings` rows are provisional all-allowed
defaults. They are used only to *remove* capability, never to grant it:
every operation still passes the existing approved role-set authorization
first (`TicketRoleSets` — unchanged), and a department with **no** settings
row behaves exactly as before the phase. Provisional permissions currently
in the seed: all six participating departments allow assignment, internal
reassignment, and transfer out; head and supervisor roles both default to
Department Head. Each is a data edit away from changing and is pending
business confirmation.

## 5. Open business decisions (unchanged + new)

All ten Phase 1 pending decisions stand (business vs. calendar days,
weekends, UAE holidays, pending-pause behavior for both kinds, SLA after
reopen, escalation recipients/timing, per-department permissions, range
interpretation, first-response SLAs). Additionally:

1. **Urgency mapping** — Normal→Medium / Urgent→High remains provisional;
   whether Urgent = High or Critical needs business confirmation.
2. **Accounting** — still provisional (department vs. approval role vs.
   external provider); no Accounting workflow was built. Phase 3 decides.
3. **Genesys contract** — field guarantees per channel, queue→department
   mapping (deliberately not invented), and any Ticketing→Genesys
   write-back await the finalized integration contract.
4. **Department Supervisor role** — whether a dedicated role is added to
   the fixed set, and final supervisor permissions.
5. **Team collaboration surface** — team members are configuration today;
   whether/how non-primary members appear on the ticket (participants UI,
   notifications) is a later phase.
6. **Wizard preload** — the New Ticket wizard was not touched; preloading
   Genesys context/channel/phone into it is a later, contract-dependent
   step (the API already accepts the context).

## 6. Explicitly not implemented (per scope)

Full Genesys API integration; hard-coded Genesys endpoints; a queue-routing
engine or phone-routing table inside Ticketing; Round Robin / Least
Workload; workflow designer UI; final SLA pause/resume calculation; SLA
after Reopen; final escalation timing; Accounting/Handover approval
workflows; speculative employee mappings (no assignment rules are seeded —
seeding real people is an operational configuration step, not code).
