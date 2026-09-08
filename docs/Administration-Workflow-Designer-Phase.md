# Administration + Request Type Workflow Designer

Status: implemented on top of Workflow phases 1–3 (all preserved). SLA
Phase 4 is NOT started. Dynamic runtime enforcement of designed steps is
NOT started — see §9.

## 1. Architecture before → after

| Area | Before | After |
| --- | --- | --- |
| Administration UI | None (only `PATCH /api/users/{id}/activation` and `GET /api/roles`) | `/Admin` area in TigerCS.Web: Users, Departments, Request Types, Workflows (+ Designer), System Administrator only |
| Workflow model | `WorkflowTemplates` (3 seeded, shared) + `WorkflowTemplateSteps`; `RequestTypes.WorkflowTemplateId` | `Workflows` (logical) → `WorkflowTemplates` = numbered VERSIONS (Draft / Published / Historical) → `WorkflowTemplateSteps` (+ `ApprovalType`) → `WorkflowStepTransitions` (Approved/Rejected branches); `RequestTypes.WorkflowId` |
| Ticket ↔ workflow | Capabilities resolved live from the request type's single template | `Tickets.WorkflowTemplateId` pins the Published version at creation; capabilities resolve from the pinned version |
| Identity admin | Activation only | Create user (Identity), profile, roles (fixed set), department membership (existing model), activation |

Nothing was added to `TicketStatus`. `TicketApprovals`, `TicketPendingRecords`
and `TicketWorkflowEvents` are unchanged. All Phase 1–3 invariants hold
(department queue = `CurrentOwnerEmployeeId` null, transfer re-runs
assignment, ReopenPolicy authoritative, system actions actor = null).

## 2. Database changes (migration `AddWorkflowVersioning`, 20260908054944)

Migration-safe for existing databases; nothing is dropped except the
FK/column that is renamed and remapped. Order in `Up()`:

1. **`Workflows`** created; one row inserted per existing `WorkflowTemplates`
   row (same Code/Name/Description/IsActive, `CreatedAtUtc = SYSUTCDATETIME()`).
2. **`WorkflowTemplates`** gains `WorkflowId`, `VersionNumber`, `Status`,
   `CreatedAtUtc`, `CreatedByEmployeeId` (null), `PublishedAtUtc`,
   `PublishedByEmployeeId` (null). Added NULLABLE, backfilled — every
   existing template becomes **version 1, Published (Status 2)** of the
   workflow with the same Code — then tightened to NOT NULL. No lingering
   DEFAULT constraints. Indexes: unique (WorkflowId, VersionNumber);
   filtered unique `UX_WorkflowTemplates_OnePublishedPerWorkflow`
   (`[Status] = 2`) and `UX_WorkflowTemplates_OneDraftPerWorkflow`
   (`[Status] = 1`).
3. **`RequestTypes.WorkflowTemplateId` → `RequestTypes.WorkflowId`**:
   renamed, then values REMAPPED from template id to logical workflow id
   (`UPDATE … JOIN WorkflowTemplates ON WorkflowTemplateId = rt.WorkflowId`),
   FK re-pointed to `Workflows` (Restrict).
4. **`Tickets.WorkflowTemplateId`** (nullable, FK Restrict) added and
   **backfilled for tickets that already had a RequestTypeId** with the one
   template that governed them (there was exactly one per request type
   before versioning — factual, not fabricated). Tickets without a request
   type stay NULL.
5. `WorkflowTemplateSteps.ApprovalType` (tinyint, null) added; the
   (WorkflowTemplateId, Sequence) index is now NON-unique (the designer
   swaps two sequences in one save; uniqueness is enforced by the aggregate
   and publish validation). `WorkflowStepTransitions` created
   (step FK cascade, target FK restrict, unique (step, outcome)).

`Down()` reverses everything, remapping request types back to the
workflow's Published (else version 1) template. Generated SQL was produced
with `dotnet ef migrations script` and inspected; `dotnet ef migrations
has-pending-model-changes` reports none.

**Left nullable for legacy data:** `Tickets.WorkflowTemplateId` (tickets
with no request type), `WorkflowTemplates.CreatedByEmployeeId` /
`PublishedByEmployeeId` (system-created baseline versions),
`WorkflowTemplateSteps.ApprovalType` on the pre-versioning "With Approval"
step (the approval type still comes from the request type's approval
requirement at runtime, as in phase 3).

## 3. Versioning rules implemented

- Lifecycle: **Draft → Published → Historical**, strictly forward. Every
  mutator on `WorkflowTemplate` calls `EnsureDraft()`; a Published or
  Historical version answers every edit with 409 at the API.
- At most one Draft and one Published version per workflow (DB filtered
  unique indexes + service checks).
- **Create New Version** copies the Published version's steps and branches
  into Draft V(n+1) (falls back to the latest version before any
  publication). Refused while a Draft exists.
- **Publish** = validate (§4) → Published; the previously Published version
  becomes Historical; capability flags are widened to match steps present.
  **Existing tickets are never migrated.**
- **Delete** only a Draft with no pinned tickets and not the only version of
  a workflow still referenced by request types. Published/Historical
  versions are never deleted (FK Restrict from Tickets + service rule).
- Workflows, request types, departments, users: Active/Inactive only —
  no physical delete endpoint exists for any of them.

## 4. Publish validation (`WorkflowVersionValidator`)

Errors (block publication): no steps; no Start step / Start not first /
more than one Start; no Close step / Close not last / more than one Close;
no Resolve step / Resolve after Close; workflow beginning with
Resolve/Close; approval step without a controlled approval type; branches on
a non-approval step; duplicate outcome on a step; branch to a missing step,
to itself, or Approved back to Start; any step unreachable from Start
(Approved branch replaces the implicit "next", Rejected is an additional
edge); Start/Close marked optional; duplicate positions; version not a
Draft. Warning (non-blocking): approval step without a Rejected branch (the
phase-3 decision "after rejection the next action stays explicit").

## 5. Supported step types (controlled, `WorkflowStepKinds` catalog)

Start (Created), Department Queue / Assignment, Review, **Approval**
(WaitingForApproval — requires `AccountingApproval` or
`CustomerServiceApproval`; Approved/Rejected branches), Department Work
(InProgress), Pending Customer, Pending Internal / Third Party,
**Prerequisite** (new enum value 10 — anchors the phase-3
`PrerequisitesCompleted` event), **Maintenance Dependency** (new enum value
11 — anchors `MaintenanceRequired/NotRequired/Completed`), Resolve, Close.
No free-text step types, expressions, scripts or JSON editors.

## 6. RequestType → Workflow → Version

`RequestType.WorkflowId` names the logical workflow. The version in force is
the workflow's single Published version, resolved at ticket creation
(`TicketCreationAppService`) and pinned via `Ticket.PinWorkflowVersion`. A
request type cannot be created/re-pointed/activated onto a workflow with no
Published version; ticket creation on such a request type returns 422
`request-type-workflow-not-published`. The Request Type admin page shows
Workflow / Active Version / Draft with **View Workflow**, **All Versions**
and **Create New Version**.

## 7. Ticket pinning

`Tickets.WorkflowTemplateId` is write-once (`PinWorkflowVersion`, requires a
request type). `TicketLifecycleAppService.ResolveCapabilitiesAsync` uses the
pinned version; a legacy ticket with a request type but no pinned version
falls back to the workflow's currently Published version (the same
resolution it always had). `WorkflowCapabilities.Resolve` no longer
requires the version to belong to the request type's current workflow, so
re-pointing a request type never breaks tickets pinned earlier. Ticket
Details shows Request Type and "Workflow · Vn".

## 8. New Ticket

The wizard's "Request Type *" dropdown is (and remains) the Category. A new
optional **Handling Process** dropdown (active request types of the selected
department, from `GET /api/request-types?departmentId=`) sends
`RequestTypeId`; the Api re-validates the department match. Without a
selection the ticket is created exactly as before (no workflow pinned).

## 9. Left for the runtime-engine increment (explicitly NOT implemented)

- Dynamic transition enforcement from the designed steps (e.g. blocking
  Resolve until the Approval step's Approved outcome, following a Rejected
  branch automatically). Phase-3 approval/pending/typed-event mechanisms are
  reused unchanged; the persisted steps + transitions are the structured
  input the engine will read (`WorkflowTemplateSteps`, `WorkflowStepTransitions`).
- "Current step" tracking on a ticket.
- Auto-resume on approval, behavior after rejection (phase-3 open decisions).
- Enforcing that an Approval step's type matches an active
  `RequestTypeApprovalRequirement` (shown on the Request Type page; not
  enforced — a workflow may be shared by several request types).
- Password reset / lockout management (Identity create-with-password only,
  as the dev seed does).
- Department Head semantics, SLA Phase 4, escalation, calendars.

## 10. Authorization

Every `api/admin/*` controller carries `[Authorize(Policy =
PolicyNames.SystemAdministrator)]`; `GET /api/request-types` is
AuthenticatedStaff (read-only directory). TigerCS.Web gates the `/Admin`
folder with a `SystemAdministrator` role policy and shows the navigation
link only to that role — display only; the Api is the enforcement point.
No existing ticket permission changed. Tests: every admin endpoint × every
non-administrator role → 403; anonymous → 401.

## 11. Deployment

API: yes (new controllers, pinning). Web: yes (Admin area, New Ticket
picker, Ticket Details facts). Database: yes — apply
`AddWorkflowVersioning`. UAT steps: back up DB → `dotnet ef database update`
(or run the generated script) → deploy Api → deploy Web → verify
`/Admin/Workflows` shows Standard Request / Request With Pending / Request
With Approval each as **V1 Active** with their original steps, and existing
tickets with a request type show "Workflow · V1" on Ticket Details. No IIS
configuration change.
