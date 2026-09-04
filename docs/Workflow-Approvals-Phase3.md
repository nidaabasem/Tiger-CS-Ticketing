# Workflow & Automation — Phase 3: Approvals & Dependencies

Status: implemented on top of Phases 1–2 (all preserved). Phase 4 (SLA
calculation, pause/resume, trigger consumption) and Phase 5 (escalation)
are NOT started — this phase only produces correct, typed trigger
events/timestamps.

## 1. Model

- **`TicketApprovals`** — one row per approval cycle, the *authoritative*
  approval state, fully independent of `TicketStatus` (no new status
  values). Small dedicated `ApprovalStatus`: Pending / Approved / Rejected /
  Cancelled. Decisions are write-once; rejection requires a reason; a
  re-request (after rejection/cancellation) opens a new cycle and flips the
  old row's `IsCurrent` — history is never overwritten or deleted. Filtered
  unique indexes guarantee at most one Pending and one current cycle per
  (ticket, approval type). Target columns snapshot the requirement's target
  at request time. An approved/pending cycle can never be silently
  superseded by a new request.
- **`ApprovalType`** (controlled enum, never free text): only the two the
  SLA document supports — `AccountingApproval` (Send Receipts),
  `CustomerServiceApproval` (Handover). Nothing invented.
- **`RequestTypeApprovalRequirements`** — configuration linking approval
  requirements to request types (never `if (name == "Send Receipts")`
  logic): approval type, target (`ApprovalTargetKind` Department / Role /
  Employee + nullable columns), `BlocksWorkUntilApproved`, active flag; one
  per (request type, approval type).
- **`TicketWorkflowEvents`** — the typed, append-only, machine-queryable
  event store phase 4 reads (never parsed from audit text):
  ApprovalRequested, ApprovalReceived, ApprovalRejected,
  CustomerServiceApproved, PrerequisitesCompleted, MaintenanceRequired,
  MaintenanceNotRequired, MaintenanceCompleted — each with
  `OccurredAtUtc`, actor, optional approval link, correlation id. Existing
  `AuditEntries`/`TicketStatusHistory` were inspected first: audit is
  human-text before/after values and status history records only the five
  lifecycle dimensions, so neither is a safe trigger source — this small
  typed table is genuinely needed, and approval records alone would not
  cover prerequisites/maintenance.

## 2. Source-supported behavior

- **Collections / Send Receipts**: the request type requires
  `AccountingApproval` targeting the (provisional) Accounting department.
  The ticket waits operationally via the existing structured
  Pending Internal (reason e.g. "Waiting for Accounting Approval") — a
  separate record from the approval, which stays authoritative. On
  approval, the typed `ApprovalReceived` event carries the decision
  timestamp the 1-day SLA will start from in phase 4 — **nothing starts at
  ticket creation, and no deadline is computed in this phase**. On
  rejection (reason mandatory) the ticket is neither resolved nor closed;
  the next action stays explicit.
- **Handover**: requires `CustomerServiceApproval` (role target). Approval
  emits the typed `CustomerServiceApproved` event — the 1–4-day phase-4
  trigger. The maintenance dependency is represented by typed events, no
  duration anywhere: `MaintenanceRequired` → (waits, Pending Internal
  available, never customer-caused) → `MaintenanceCompleted`;
  `MaintenanceNotRequired` records the direct path. Completed requires a
  prior Required; nothing changes after Completed.
- **Registration**: NO approval invented. `PrerequisitesCompleted` is an
  explicit, authorized, once-only recorded event (its first timestamp is
  the phase-4 trigger); until recorded, no trigger exists, and the ticket
  may wait via the existing Pending Customer/Internal.

## 3. Actions, authorization, audit

`TicketApprovalAppService`: request / approve / reject / cancel / record
event / view. Requesting, cancelling, and event-recording use the existing
operational-actor shape (current owner, cross-department supervisory roles,
department-scoped Department Head) — nothing broader. Deciding is gated by
the cycle's target snapshot: Role target → holds the role; Employee target →
is that employee; Department target → active member of the target
department AND a department-side role (`DepartmentEmployee`/`DepartmentHead`
by default, or the requirement's narrowing role). The ADR-0024 System
Administrator override applies through `AuthorizationGate` exactly as
everywhere else. The approvals view uses the ticket's own visibility rule
plus one narrow extension: the configured approver of a *pending* cycle may
see the view (they are otherwise not members of the ticket's department).

Every action writes through the existing audit infrastructure (no second
audit system): actor, timestamps, previous → new approval state,
reason/comment, target context, and a correlation id shared across the
approval row, the typed event, and the audit entry of the same action.

## 4. UX (minimal, no redesign)

Ticket Details gains one concise **Approvals & Dependencies** card:
current cycle per type (status, requested/decided timestamps, comment,
human-readable "decided by" target — no technical ids), Approve/Reject
with a comment box shown **only** to callers the API flags as authorized
(no disabled buttons), Request buttons for configured-but-unrequested
approvals, Maintenance state ("Required — Pending" / "Not Required" /
"Completed"), Prerequisites state, and a small record-event control for
operational actors. The Change Status form also gained the pending-reason
field the phase-2 API requires. New Ticket is untouched — approvals are
discoverable from the request type after creation, never forced during it.

## 5. API

`GET/POST /api/tickets/{id}/approvals`,
`POST /api/tickets/{id}/approvals/{approvalId}/decision`,
`POST /api/tickets/{id}/approvals/{approvalId}/cancellation`,
`POST /api/tickets/{id}/workflow-events` — documented under the new
"Approvals and Dependencies" OpenAPI tag; approval behavior is identical
for Genesys-originated and Face-to-Face tickets (nothing interaction-
source-specific anywhere in it).

## 6. Open business decisions (unchanged + phase-3 additions)

All prior pending decisions stand. Newly documented as provisional:

1. **Accounting's nature** — still provisional (department vs. approval
   role vs. external/internal provider). The requirement targets the
   provisionally seeded Accounting department; re-pointing it is a data
   edit, and an external provider would be an additive target kind.
2. **Exact Accounting approver role** — provisional: active Accounting
   membership + Department Employee/Head
   (`TicketApprovalAppService.DepartmentTargetDefaultApproverRoles`).
3. **Exact CS approver role for Handover** — provisional: CS Supervisor
   (`WorkflowReferenceData.ProvisionalCustomerServiceApproverRole`), the
   narrowest CS supervisory role, chosen fail-safe.
4. **Auto-resume on approval** — NOT implemented: approval never resumes a
   Pending ticket; resume stays an explicit, audited action until business
   approves an automatic (configurable) variant.
5. **Behavior after rejection** — nothing automatic; explicitly open.
6. **Maintenance SLA** — none exists in the source; none modeled.
7. **SLA pause during approval waiting** — untouched; phase 4 + the
   existing tri-state pause configuration decide.
