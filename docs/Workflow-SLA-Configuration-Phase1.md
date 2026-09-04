# Workflow & SLA Configuration — Phase 1

Status: implemented (configuration foundation only). Phases 2–6 (transition
enforcement, approval flows, SLA calculation/pause, escalation, UI) build on
this and are **not** implemented yet.

Source: the Customer Service SLA document (durations for Customer Service,
Collections, Registration, Call Center, Handover) as restated in the phase
task. Where that document is silent, nothing was invented — the gap is
recorded in §5 below.

---

## 1. Audit of the current solution (what phase 1 builds on)

| Area | Current implementation | Reused? |
| --- | --- | --- |
| Ticket statuses | `TicketStatus`: Open, InProgress, PendingCustomer, PendingThirdParty, Resolved, Closed (ADR-0008 five-dimension model) | Yes — untouched. No new statuses added; approval stages will be approval records (phase 3), not statuses. |
| Allowed transitions | `Ticket.ChangeStatus` (Open→InProgress needs owner; InProgress↔Pending*), `Resolve` (from InProgress/Pending*), `Close` (from Resolved, CS layer), `Reopen` (Resolved/Closed→InProgress) | Yes — untouched. The new configuration layer will *narrow* availability per request type in phase 2, never widen it. |
| Assignment model | `TicketAssignment` append-only history; role sets: assign within own department = CS Supervisor + Department Head, cross-department assign/transfer = CS Manager (`TicketRoleSets`) | Yes — untouched. `DepartmentWorkflowSettings` adds per-department narrowing flags for phase 2. |
| Department model | `Department` (Id, Name, Code, IsActive), `UserDepartmentAssignment`, `Employee`, fixed `Roles` | Yes — untouched. New 1:1 optional `DepartmentWorkflowSettings` row; no employee-directory data duplicated. |
| Request type / category | `Category` (name, department, parent, active) — intake classification routing to one department | Kept as-is for intake/routing. New `RequestType` is the workflow/SLA configuration unit (see §3.1). |
| Priority model | Fixed `Priorities` 1=Critical, 2=High, 3=Medium, 4=Low + per-priority `SlaPolicy` (minutes, clock basis, warning threshold) | Yes — reused as the only priority model. Normal/Urgent map onto it (§4.1). |
| SLA model | `SlaPolicy` (per priority), `TicketSlaInstance` periods, `SlaDueDateCalculator` (24/7 + business-hours), `BusinessCalendar` (UAE Sat–Thu 08:00–18:00) | Yes — all kept. New `RequestTypeSlaPolicy` adds the per-(request type, priority) layer; combination rule is a phase-4 decision (§5.10). |
| Pending | Statuses exist; reason only as free-text history note; **no** pause/resume (`TicketSlaPausePeriods` deliberately unbuilt) | Statuses reused. Structured pending reasons = phase 2; pause behavior stays configurable-and-undecided (§5). |
| Resolve/Close/Reopen | Resolve = Department Employee/Head (dept-scoped); Close/Reopen = CS Agent/Supervisor/Manager; `ReopenPolicy` 7-day configurable window | Yes — untouched, matches the phase's target ownership already (ISSUE-022). `RequestType.AllowReopen` can only remove reopen for a type; `ReopenPolicy` remains the final enforcement point. |
| Escalation | `TicketEscalation` rows, auto Level 2 on breach, manual levels, recipient matrix per priority, `EscalationLevel` dimension | Yes — untouched. Request-type warning threshold column seeded for phase 5; recipients stay role-based. |
| Audit | `AuditEntries` + `TicketStatusHistory`, append-only, correlation ids | Yes — the only audit system; phase 2+ workflow actions write through it. |

Genuinely missing before this phase: a request-type concept with workflow/SLA
configuration, reusable workflow templates, per-request-type SLA values
(with ranges and non-creation triggers), and department-level workflow flags.
All added here as **configuration**, with no behavior change to any existing
flow.

## 2. What phase 1 adds

New domain module `TigerCS.Domain.Modules.WorkflowConfiguration`:

- `WorkflowTemplate` + `WorkflowTemplateStep` — the three reusable patterns
  (`STANDARD`, `PENDING`, `APPROVAL`) with displayable, ordered steps.
  Steps map onto the existing lifecycle (`WorkflowStepKind` documents the
  mapping); Review/WaitingForApproval are approval-record concepts, not new
  `TicketStatus` values.
- `RequestType` — belongs to one Department, selects one template, carries
  default priority + business flags (priority change, pending customer,
  pending internal, reopen, required fields JSON, active).
- `RequestTypeSlaPolicy` — SLA per (request type, priority): trigger, unit,
  first-response and resolution values as **ranges** (target + maximum),
  `IsImmediate`, nullable clock basis and pause flags (null = pending
  business decision), optional warning threshold, active.
- `DepartmentWorkflowSettings` — 1:1 optional per Department: allow
  assignment / internal reassignment / transfer out, head **role** name.
- `WorkflowCapabilities.Resolve(template, requestType)` — the single
  combination rule (request type can only narrow its template).

Persistence: EF configurations + migration `AddWorkflowConfiguration`
(tables `WorkflowTemplates`, `WorkflowTemplateSteps`, `RequestTypes`,
`RequestTypeSlaPolicies`, `DepartmentWorkflowSettings`). Seed:
`WorkflowReferenceData` (single source of truth, consumed by the dev seed
and tests). Application: read-only `WorkflowConfigurationQueryService`.

## 3. Design decisions

### 3.1 RequestType vs. Category

`Category` remains the intake classification/routing taxonomy (FR-CLS-01);
`RequestType` is the operational workflow/SLA configuration unit. They are
deliberately separate concerns. **How a ticket acquires its request type**
(a `Tickets.RequestTypeId` column, a Category→RequestType mapping, or
merging the two models) is a phase-2 wiring decision to be made when
transition enforcement is built — recorded here rather than silently decided
by adding speculative columns now.

### 3.2 Approval flows

Workflow C (approval) is expressed as `WorkflowTemplate.RequiresApproval` +
Review/WaitingForApproval **steps**. No `Review`/`WaitingForApproval`/
`Approved` values were added to `TicketStatus`: the audit showed the status
enum is consumed by lifecycle, SLA, dashboards, and history as a closed set,
and the phase instruction prefers approval records over enum explosion.
Approval records (who approved, when, which step) are phase 3.

### 3.3 SLA ranges and units

Durations are stored in the source document's own unit
(`SlaDurationUnit.Days` etc.) with `ResolutionTargetValue` /
`ResolutionMaximumValue` as the range bounds — "10–12 Days" is stored as
10/12 Days, never collapsed to one number and never converted to minutes at
seed time. "Immediately" is `IsImmediate = true`, not a fabricated zero.

### 3.4 SLA triggers

`SlaTriggerType` records where each clock starts. Seeded non-default
triggers: Collections/Send Receipts = `ApprovalReceived` (the 1 day runs
after Accounting approval); Handover Request = `CustomerServiceApproved`
(1–4 days after CS approval); Registration/Register Unit =
`PrerequisitesCompleted` (1–3 days "when everything is OK"). Everything else
is `TicketCreated`, matching today's behavior (ISSUE-001 Option C).

## 4. Seeded configuration

### 4.1 Urgency ↔ priority mapping (documented decision, provisional)

The SLA document's **Normal → Medium (3)** and **URGENT → High (2)**.
Critical stays reserved for genuine emergencies above "Urgent"; Low keeps
its existing meaning. No second priority/urgency model was created, and no
`… URGENT` request types exist — urgency is a priority-level SLA row on the
same request type. This mapping is configuration-level and awaits business
confirmation.

### 4.2 Departments

Existing `CS` reused. Added if absent: Collections (`COL`), Registration
(`REG`), Handover (`HO`), Call Center (`CC`), Accounting (`ACC` — named by
the document only as the Send Receipts approver; seeded so phase 3 has a
real department to route approval to). All six get a
`DepartmentWorkflowSettings` row with provisional defaults (everything
allowed, head role = Department Head).

### 4.3 Request types and SLA rows (all values verbatim from the source)

| Department | Request type | Template | SLA rows (unit: Days) |
| --- | --- | --- | --- |
| Customer Service | NOC for Resale | PENDING | Normal 10–12; Urgent 2–4 |
| Customer Service | NOC for Handover | PENDING | Normal 1–2 |
| Customer Service | NOC for Mortgage | PENDING | Normal 10–12; Urgent 2–4 |
| Customer Service | NOC for Golden Visa | PENDING | Normal 1–2 |
| Customer Service | Complaint Handling | PENDING | Normal 1–3 |
| Customer Service | Ticketing System | STANDARD | Normal 1; Urgent Immediate |
| Customer Service | E-mail | STANDARD | Normal 1–2 |
| Collections | E-mail | STANDARD | Normal 1–2 |
| Collections | Ticketing System | STANDARD | Normal 1; Urgent Immediate |
| Collections | Send Receipts | APPROVAL | Normal 1 — **trigger: ApprovalReceived** |
| Registration | Send SPA Link | STANDARD | Normal 1–2 |
| Registration | Register Unit | PENDING | Normal 1–3 — **trigger: PrerequisitesCompleted** |
| Handover | Handover Request | APPROVAL | Normal 1–4 — **trigger: CustomerServiceApproved** |

Not seeded on purpose: Call Center request types (the document gives it
operational rules but the approved request-type list names none);
a maintenance-completion SLA for Handover (undefined in the source);
Registration's "if something is wrong" duration (issue-dependent);
local/international transaction receipt durations (3–4 days / ~1 week — the
document ties them to Call Center operations, not to a listed request type;
add as configuration once business names the owning request type).

## 5. Pending business decisions (must NOT be silently decided)

Held open as configuration; where a technical default was unavoidable it is
marked provisional:

1. **Business vs. calendar days** — `RequestTypeSlaPolicy.ClockBasis` is
   nullable and seeded null. Phase 4 must not assume.
2. **Weekend treatment** — same; the existing UAE Sat–Thu calendar exists
   but is not yet bound to these rows.
3. **UAE public holiday treatment** — same; the `Holidays` table remains
   manually maintained and empty by default.
4. **Pending Customer pauses SLA?** — `PausesOnPendingCustomer` nullable,
   seeded null (= undecided; treat as no-pause until approved).
5. **Pending Internal pauses SLA?** — `PausesOnPendingInternal` nullable,
   seeded null; internal delays never pause implicitly.
6. **SLA after Reopen** — unchanged from the existing explicit assumption
   flag; nothing in this phase touches SLA-on-reopen.
7. **Escalation recipients/timing** — existing per-priority matrix kept;
   request-type-level recipients are phase 5 configuration.
8. **Final role permissions per department** — existing `TicketRoleSets`
   kept verbatim; `DepartmentWorkflowSettings` flags are provisional
   all-allowed defaults.
9. **Range interpretation** — Target/Maximum store the range bounds; whether
   lower = "target" and upper = "breach" officially is NOT claimed anywhere.
10. **First Response SLA** — the source gives none per request type, so all
    `FirstResponse*` values are null; the existing per-priority
    first-response targets are unaffected.
11. **RequestTypeSlaPolicy ↔ SlaPolicy precedence** (phase 4): how the new
    per-request-type layer overrides/combines with the per-priority
    defaults.
12. **Ticket ↔ RequestType wiring** (phase 2): see §3.1.
13. **Provisional flag choices in seed data**: `AllowAgentPriorityChange`
    is true only where the source documents an urgent variant; pending
    flags follow the documented waits per type; all request types allow
    reopen (ReopenPolicy still governs). All are data edits away from
    changing.
