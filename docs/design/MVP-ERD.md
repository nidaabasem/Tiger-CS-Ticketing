# Tiger Group — CS Ticketing System
## MVP Detailed ERD

| | |
|---|---|
| **Status** | Design for review — conceptual/logical design only |
| **Scope** | Detailed entity-relationship design for the 3-week internal pilot MVP, refining `docs/architecture/Domain-Model.md` to implementation-ready detail |
| **Explicitly not done here** | No SQL DDL, no EF Core entity classes or migrations, no connection to a real database, no application code |
| **Base** | `main` @ `4fe6f19` (post PR #1/#3/#4 merge — the full architecture package) |
| **Related documents** | `docs/design/MVP-Data-Dictionary.md` (column-level detail for every entity below) · `docs/architecture/Domain-Model.md` (conceptual model this refines) · `docs/architecture/System-Architecture.md` · `docs/architecture/SLA-Architecture.md` · `docs/architecture/Genesys-Integration.md` · `docs/architecture/Security-Architecture.md` · `docs/architecture/adr/0003, 0006, 0007, 0008, 0009-0012, 0013-0014, 0017-0019` |
| **Date** | 2026-08-18 |

**Companion document:** This file holds the Mermaid ER diagram and, for every entity, its relationship cardinalities, ownership, delete behavior, and referential-integrity notes. Column-by-column type/nullability detail lives in `docs/design/MVP-Data-Dictionary.md` so this file stays focused on structure and relationships.

---

## 0. Design Principles Carried Forward (Not Re-Decided Here)

These are restated, not re-opened — every one traces to an already-accepted ADR, and this document does not silently invent a different answer:

1. **The CRM remains the sole source of truth for units and contacts (ADR-0006).** This design creates **no local master Unit or Customer table.** Store CRM identifiers and immutable ticket-time snapshots only. The CRM remains the source of truth for unit, owner, tenant, and contact master data. Do not create local Unit or Customer master tables. `UnitReferences`/`ContactReferences` below store only the CRM-issued identifier plus a refreshable, non-authoritative display cache — never a locally-owned record of who owns or lives in a unit.
2. **Each ticket carries an immutable, write-once snapshot (ADR-0007)** — `TicketRequesterSnapshots` — captured at verification time, never re-synced from the CRM or the cache afterward.
3. **No customer-facing identity exists** (ISSUE-021) — `AspNetUsers` contains internal staff only.
4. **Five independent ticket-state dimensions** (ADR-0008) — `TicketStatus`, `VerificationStatus`, `EscalationLevel`, `SlaState`, `ResolutionOutcome` — are tracked as separate concerns, never collapsed into one status column.
5. **Append-only audit** (ADR-0018) — `TicketStatusHistory` and `AuditEntries` have no update or delete path in this design; corrections are new rows, not edits to old ones.
6. **Transactional Outbox + idempotency** (ADR-0013/0014) — every cross-boundary effect is mediated by `OutboxMessages`, and idempotency is a first-class, generalized concern (`IdempotencyRecords`), not bolted on per-feature.

## 0.1 Two Structural Refinements Flagged, Not Silently Assumed

Going from the conceptual `Domain-Model.md` to a detailed ERD required two structural decisions the conceptual model left implicit. Both are refinements of already-approved concepts, not new business rules — but they're called out explicitly rather than silently baked in:

- **`UserDepartmentAssignments` (many-to-many) replaces a single `DepartmentId` on `Employee`.** The conceptual model implied one department per employee. This detailed design allows an employee (e.g., a Supervisor) to be assigned to more than one department, with exactly one marked primary. **[ASSUMPTION — multi-department staff was not an explicit requirement; flagged for confirmation. If every MVP pilot agent genuinely belongs to exactly one department, this table still works correctly (one row per employee) and costs nothing — it is a safe superset, not a scope increase.]**
- **`TicketSlaPausePeriods` makes pause/resume an explicit, timestamped history** rather than inferring pause duration from `SlaState` transitions alone. This is required to correctly extend a due timestamp by the exact paused duration (`SLA-Architecture.md` §8) and to support an audit question like "how long, in total, was this ticket waiting on the customer." This does not change any approved SLA rule — it is the storage needed to implement the rule already approved.

No other business decision is invented in this document. Where a data-type or constraint choice is genuinely arbitrary (e.g., a string length), it is marked `[ASSUMPTION]` inline (see `MVP-Data-Dictionary.md`).

---

## 1. Entity-Relationship Diagram

```mermaid
erDiagram
    ASPNET_USERS ||--|| EMPLOYEES : "extends 1:1"
    ASPNET_USERS ||--o{ ASPNET_USER_ROLES : has
    ASPNET_ROLES ||--o{ ASPNET_USER_ROLES : has

    EMPLOYEES ||--o{ USER_DEPARTMENT_ASSIGNMENTS : "assigned to"
    DEPARTMENTS ||--o{ USER_DEPARTMENT_ASSIGNMENTS : "has members"
    DEPARTMENTS ||--o{ CATEGORIES : "routes to"
    DEPARTMENTS ||--o{ TICKETS : "originates / currently owns"

    CATEGORIES ||--o{ CATEGORIES : "sub-category of"
    CATEGORIES ||--o{ TICKETS : classifies

    PRIORITIES ||--|| SLA_POLICIES : "governs (1:1)"
    PRIORITIES ||--o{ TICKETS : "current priority"
    PRIORITIES ||--o{ TICKET_SLA_INSTANCES : "tier for period"

    UNIT_REFERENCES ||--o{ CONTACT_REFERENCES : "has contacts"
    CONTACT_REFERENCES ||--o{ CONTACT_REFERENCES : "represents"
    UNIT_REFERENCES ||--o{ TICKETS : "raised against"
    CONTACT_REFERENCES ||--o{ TICKETS : "raised by"

    EMPLOYEES ||--o{ INTAKE_RECORDS : "creates (manual)"
    INTAKE_RECORDS }o--o| TICKETS : "promotes to"

    TICKETS ||--|| TICKET_REQUESTER_SNAPSHOTS : "has (1:1)"
    TICKETS }o--o| GENESYS_INTERACTIONS : "linked to"
    EMPLOYEES ||--o{ TICKETS : "currently owns"

    TICKETS ||--o{ TICKET_ASSIGNMENTS : logs
    EMPLOYEES ||--o{ TICKET_ASSIGNMENTS : "assigned by/to"
    DEPARTMENTS ||--o{ TICKET_ASSIGNMENTS : "at time of assignment"

    TICKETS ||--o{ TICKET_STATUS_HISTORY : logs
    EMPLOYEES ||--o{ TICKET_STATUS_HISTORY : "acted (nullable)"

    TICKETS ||--o{ TICKET_RESOLUTIONS : logs
    TICKETS }o--o| TICKETS : "duplicate of"
    EMPLOYEES ||--o{ TICKET_RESOLUTIONS : resolves

    TICKETS ||--o{ TICKET_SLA_INSTANCES : logs
    TICKET_SLA_INSTANCES ||--o{ TICKET_SLA_PAUSE_PERIODS : logs
    EMPLOYEES ||--o{ TICKET_SLA_INSTANCES : "approves downgrade"

    TICKETS ||--o{ TICKET_ESCALATIONS : logs
    EMPLOYEES ||--o{ TICKET_ESCALATIONS : responds

    TICKETS ||--o{ TICKET_NOTES : has
    EMPLOYEES ||--o{ TICKET_NOTES : authors
    TICKET_STATUS_HISTORY ||--o| TICKET_NOTES : "accompanies (nullable)"

    TICKETS ||--o{ TICKET_ATTACHMENTS : has
    EMPLOYEES ||--o{ TICKET_ATTACHMENTS : uploads

    BUSINESS_CALENDARS ||--o{ BUSINESS_CALENDAR_WORKING_DAYS : defines
    BUSINESS_CALENDARS ||--o{ HOLIDAYS : defines
    EMPLOYEES ||--o{ HOLIDAYS : "enters / confirms"

    TICKETS ||--o{ NOTIFICATIONS : triggers
    NOTIFICATIONS }o--o| OUTBOX_MESSAGES : "dispatched via"

    OUTBOX_MESSAGES }o--o| IDEMPOTENCY_RECORDS : "keyed by"
    GENESYS_INTERACTIONS }o--o| IDEMPOTENCY_RECORDS : "keyed by"

    EMPLOYEES ||--o{ AUDIT_ENTRIES : "acts (nullable)"

    EMPLOYEES {
        uniqueidentifier EmployeeId PK
        int DepartmentId FK "removed - see UserDepartmentAssignments"
        nvarchar DisplayName
        bit IsGeynessStaff
        datetime2 DeactivatedAtUtc
    }
    TICKETS {
        bigint TicketId PK
        varchar TicketNumber "unique, immutable"
        int OriginatingDepartmentId FK "immutable"
        int CurrentDepartmentId FK "mutable"
        uniqueidentifier CurrentOwnerEmployeeId FK
        int UnitReferenceId FK
        int ContactReferenceId FK
        int CategoryId FK
        tinyint PriorityId FK
        tinyint TicketStatus
        tinyint VerificationStatus
        tinyint EscalationLevel
        tinyint SlaState
        tinyint ResolutionOutcome
        bigint DuplicateOfTicketId FK
        datetime2 FirstHumanResponseAtUtc
        datetime2 AcknowledgementSentAtUtc
        int ReopenCount
    }
    TICKET_SLA_INSTANCES {
        bigint TicketSlaInstanceId PK
        bigint TicketId FK
        tinyint PriorityId FK
        datetime2 PeriodStartAtUtc
        datetime2 PeriodEndAtUtc "null = current"
        datetime2 FirstResponseDueAtUtc
        datetime2 ResolutionDueAtUtc
        bit FirstResponseBreached "immutable once true"
        bit ResolutionBreached "immutable once true"
        tinyint ChangeReason
        uniqueidentifier ApprovedByEmployeeId FK "required if Downgrade"
    }
```

*(Reference-only tables with no ticket-specific foreign keys — `AspNetRoles`, `AspNetUserRoles` beyond what's shown — are described in `MVP-Data-Dictionary.md` but omitted from the diagram for readability.)*

---

## 2. Entity Relationships and Integrity Rules

For every entity: a one-line purpose reminder, then its relationships table — **Cardinality, Required/Optional, Delete Behavior, Ownership, Referential-Integrity Expectation**. Column-level detail (types, nullability, constraints) for the same entities is in `MVP-Data-Dictionary.md` §2.1–2.23, using the same section numbers for cross-reference.

### 2.1 AspNetUsers / AspNetRoles / AspNetUserRoles (Identity, framework-owned)

**Purpose:** ASP.NET Core Identity's own tables (ADR-0004). Not redefined here beyond noting the extension point.

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| AspNetUsers ↔ Employees | 1:1 | Required (every Employee has exactly one AspNetUsers row; not every AspNetUsers row need have an Employee, though at MVP it always will since no customer identity exists) | **Restrict** — an AspNetUsers row is never hard-deleted while an Employee references it; deactivation (`DeactivatedAtUtc`) is the only "removal" path | Identity and Access | `Employees.EmployeeId` is both PK and FK to `AspNetUsers.Id` (identifying relationship) |
| AspNetUsers ↔ AspNetUserRoles ↔ AspNetRoles | many-to-many | Required (every staff account has ≥1 role) | Cascade (Identity framework default) | Identity and Access | Framework-enforced |

### 2.2 Employees

**Purpose:** Domain extension of `AspNetUsers`, carrying staff attributes not in Identity's own schema (ADR-0004). `DepartmentId` is deliberately **not** a column here — see §0.1 and `UserDepartmentAssignments` below.

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Employees → UserDepartmentAssignments | 1:many | An active Employee should have ≥1 assignment (app-enforced, not a DB constraint, since an employee could theoretically be between assignments) | **Restrict** on Employee "deletion" — deactivation only, never a hard delete, so this is moot in practice | Identity and Access | — |
| Employees → Tickets (`CurrentOwnerEmployeeId`) | 1:many | Optional (a ticket may be unassigned) | **Set Null** if an employee is deactivated mid-ticket — ownership must be explicitly reassigned, not silently orphaned to a dangling reference | Ticketing | App layer must reassign on deactivation, not merely null the FK and move on |

### 2.3 Departments

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Departments → UserDepartmentAssignments | 1:many | Optional (a new department starts with none) | **Restrict** — cannot delete a Department with active assignments; deactivate (`IsActive = 0`) instead | Identity and Access / Administration | |
| Departments → Categories | 1:many | Required (every Category routes to exactly one Department) | **Restrict** | Classification and Routing | |
| Departments → Tickets (Originating, Current) | 1:many (×2 relationships) | Required (every ticket has both) | **Restrict** — `Code` and department rows are never deleted once a ticket references them, consistent with `TicketNumber` immutability (ADR-0004) | Ticketing | `OriginatingDepartmentId` is **write-once** at the application layer — no update path exists for it after creation, enforced in code, not just by convention |

### 2.4 UserDepartmentAssignments

**Purpose:** Many-to-many Employee↔Department membership, with one primary department per employee (§0.1).

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| → Employees | many:1 | Required | **Cascade** (deleting the join row, not the Employee — Employees are never hard-deleted) | Identity and Access | |
| → Departments | many:1 | Required | **Restrict** (see §2.3) | Identity and Access | |

### 2.5 Categories

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| → Categories (self, parent) | many:1 | Optional | **Restrict** | Classification and Routing | A sub-category's parent must itself have `ParentCategoryId IS NULL` (app-enforced — no two-level-deep nesting) |
| → Departments | many:1 | Required | **Restrict** | Classification and Routing | |
| → Tickets | 1:many | Required (every ticket has a category) | **Restrict** — a Category referenced by any ticket is deactivated (`IsActive=0`), never deleted | Classification and Routing | |

### 2.6 Priorities and SlaPolicies

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Priorities ↔ SlaPolicies | 1:1 | Required — every Priority has exactly one SlaPolicy row, seeded at setup | **Restrict** (neither is ever deleted; both are fixed reference data for MVP) | SLA and Escalation | |
| Priorities → Tickets (current) | 1:many | Required | **Restrict** | SLA and Escalation | |
| Priorities → TicketSlaInstances (tier per period) | 1:many | Required | **Restrict** | SLA and Escalation | |

### 2.7 UnitReferences and ContactReferences (CRM cache — NOT master data)

**Purpose:** Store only the CRM-issued identifier plus a refreshable display cache (ADR-0006). **Never the authoritative record.**

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| UnitReferences → ContactReferences | 1:many | Optional (a unit may briefly have zero cached contacts before first CRM sync) | **Restrict** — a cached row is refreshed, not deleted, while any ticket references it | CRM Verification | |
| ContactReferences → ContactReferences (representative) | many:1 | Optional | **Restrict** | CRM Verification | Enforces ISSUE-007: an agent may only disclose to a representative with this link populated **and** a CRM-recorded authorization (the authorization record itself lives in the CRM, not duplicated here — this FK only records that the local cache believes a link exists, refreshed from CRM) |
| UnitReferences/ContactReferences → Tickets | 1:many (each) | Required (every ticket has both) | **Restrict** | Ticketing | A ticket's FK here is a pointer to the cache row **at verification time** — it is not a substitute for the immutable snapshot (§2.8), which is what actually protects the historical record if the cache later changes |

### 2.8 TicketRequesterSnapshots

**Purpose:** The immutable, write-once record of what the agent actually read back (ADR-0007).

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Tickets ↔ TicketRequesterSnapshots | 1:1 | Required — created in the same transaction as the ticket | **Cascade** (only if the ticket itself were ever deleted, which it never is per the retention policy — in practice this relationship is never exercised) | Ticketing | **No update path exists in the application layer** for this table after insert — this is enforced in code, not the database, since a database-level immutability constraint on an otherwise-normal table is unusual; code review must treat any proposed update path here as a defect |

### 2.9 IntakeRecords

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Employees → IntakeRecords | 1:many | Required at MVP | **Restrict** | Ticketing | |
| IntakeRecords → Tickets | 0/1:0/1 | Optional both ways (many intake attempts never become a ticket — e.g., a wrong-number call) | **Set Null** on the intake side if a ticket is later deleted (never happens in practice) | Ticketing | An `IntakeRecord` is never required to have a `LinkedTicketId` — a call that resolves without needing a ticket (e.g., a simple information request answered verbally) is a valid terminal state **[ASSUMPTION — MVP does not mandate 100% intake-to-ticket conversion; flag if this is wrong]** |

### 2.10 Tickets

**Design note:** `FirstResponseDueAtUtc`/`ResolutionDueAtUtc` are **not** columns on `Tickets` — they live only on the current (open-ended) `TicketSlaInstances` row, to avoid two sources of truth for the active due timestamps. Every read of "what's this ticket's current SLA deadline" joins to `TicketSlaInstances` where `PeriodEndAtUtc IS NULL`.

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Tickets → Tickets (Duplicate) | many:1 | Optional; required only when `ResolutionOutcome = Duplicate` | **Restrict** | Ticketing | App-level check: `DuplicateOfTicketId` must reference an existing, non-duplicate-of-itself ticket (no chains of duplicates pointing to duplicates — must resolve to a genuine, non-duplicate original) |
| Tickets → GenesysInteractions | 0/1:0/1 | Optional both ways | **Set Null** on the interaction side if the ticket is deleted (never happens in practice) | Genesys Integration | A `GenesysInteraction` may exist with no linked ticket (call never converted); a `Ticket` may exist with no Genesys link (manual, non-Genesys-originated) |
| Tickets never physically deleted | — | — | **No delete path exists in the application layer for any Ticket**, consistent with the 7-year retention requirement (ISSUE-016) | Ticketing | Enforced in code; there is no "delete ticket" use case anywhere in this design |

### 2.11 GenesysInteractions

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| → Tickets | 0/1:0/1 | Optional | **Restrict** | Genesys Integration | See §2.10 |
| → IdempotencyRecords | many:1 (conceptually — see §2.23) | Required | **Restrict** | Genesys Integration | Idempotency key = `ConversationId + eventType`, per ADR-0014 |
| Employees (via AgentEmailOrExtension) | *soft* — no enforced FK | Optional | N/A | Genesys Integration | **Deliberately not a hard FK** — matching a Genesys agent to an `Employee` is a lookup performed by application logic, not a database constraint, since the mapping's reliability is itself an open question (`Genesys-Integration.md` §15 item 4). Forcing a FK here would make webhook ingestion fail whenever the mapping can't be resolved, which is the opposite of the required "never lose an interaction" behavior. |

### 2.12 TicketAssignments

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Tickets → TicketAssignments | 1:many | Required (every ticket beyond `Open` has ≥1 row) | **Cascade** is never exercised — assignment history is retained for the ticket's full life | Ticketing | Append-only; a reassignment inserts a new row and flips the prior row's `IsCurrent` to false, it never updates the prior row's `AssignedEmployeeId` |

### 2.13 TicketStatusHistory

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Tickets → TicketStatusHistory | 1:many | Required | **Restrict/never deleted** — append-only (ADR-0018) | Audit (written on Ticketing's behalf — see `Module-Design.md`'s ownership note) | No update or delete path in the application layer, at any privilege level, including System Administrator |

### 2.14 TicketResolutions

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Tickets → TicketResolutions | 1:many (one per resolve/re-resolve cycle) | Optional until first resolution; required before `Closed` | **Restrict/never deleted** | Ticketing | On Reopen (FR-RES-04), the current row's `IsCurrent` flips to false; a new row is created on the next resolution, never overwriting the old one |

### 2.15 TicketSlaInstances

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Tickets → TicketSlaInstances | 1:many | Required (≥1 from creation) | **Restrict/never deleted** — full history retained (ISSUE-023) | SLA and Escalation | Exactly one row per `TicketId` has `PeriodEndAtUtc IS NULL` (the current period) — app-enforced, recommended as a filtered unique index |
| Employees → TicketSlaInstances (`ApprovedByEmployeeId`) | optional many:1 | Required **only** when `ChangeReason = Downgrade` | **Restrict** | SLA and Escalation | App-level check blocks a Downgrade-reason row from taking effect (i.e., from ever becoming the current period) without this FK populated |
| **Immutability rule** | — | — | `FirstResponseBreached`/`ResolutionBreached`, once set to `true`, are never reset to `false` by any code path, including a later downgrade (ADR-0012/ISSUE-023) | SLA and Escalation | Enforced in application code; flagged as the single highest-consequence integrity rule in this schema (`Architecture-Review-Checklist.md`) |

### 2.16 TicketSlaPausePeriods

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| TicketSlaInstances → TicketSlaPausePeriods | 1:many | Optional (a period may never pause) | **Restrict/never deleted** | SLA and Escalation | **App-level invariant:** no row in this table may reference a `TicketSlaInstance` whose `PriorityId = Critical` — Critical never pauses, as a fixed rule (`SLA-Architecture.md` §6), not a configurable one. This is enforced in the pause-initiation code path, not by a database CHECK constraint (which would need a cross-table lookup SQL Server CHECK constraints cannot express directly) |

### 2.17 TicketEscalations

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Tickets → TicketEscalations | 1:many | Optional (many tickets never escalate) | **Restrict/never deleted** | SLA and Escalation | `TriggerType = ManualLevel4` rows may only be inserted by a CS Manager or GM actor — app-level check, not a DB constraint (the DB has no concept of "role," only `ActorEmployeeId`) |

### 2.18 TicketNotes

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Tickets → TicketNotes | 1:many | Optional | **Restrict/never deleted** — immutable once written; a correction is a new note | Ticketing | |
| TicketStatusHistory → TicketNotes | 0/1:0/many | Optional | **Set Null** if the referenced history row were ever removed (never happens — history is append-only) | Ticketing/Audit | |

### 2.19 TicketAttachments

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Tickets → TicketAttachments | 1:many, ≤10 (app-enforced) | Optional | **Restrict/never deleted** (retention policy) | Attachments | An attachment is never surfaced to any UI/API consumer while `VirusScanStatus ≠ Clean` — app-level filter on every read path, not a database constraint |

### 2.20 BusinessCalendars, BusinessCalendarWorkingDays, Holidays

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| BusinessCalendars → BusinessCalendarWorkingDays | 1:7 (exactly) | Required | **Cascade** if a calendar is ever retired (superseded by a new effective-dated calendar, per ADR-0010 — old calendars are kept, not deleted, for historical SLA recalculation) | SLA and Escalation (data) / Administration (edit) | App-enforced: exactly 7 rows (one per `DayOfWeek`) per calendar |
| BusinessCalendars → Holidays | 1:many | Optional | **Restrict/never deleted** (historical accuracy) | SLA and Escalation (data) / Administration (edit) | |

### 2.21 Notifications

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| Tickets → Notifications | 1:many | Optional | **Restrict/never deleted** | Notifications | |
| Notifications → OutboxMessages | many:1 | Required for any notification actually dispatched (a `Pending` row created before dispatch may briefly have no Outbox link yet) | **Restrict** | Notifications / Infrastructure | |

### 2.22 AuditEntries

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| (generic, no enforced FK to `EntityId`) | — | — | **Restrict/never deleted** | Audit | **Deliberately no foreign key** — `AuditEntries` must be able to record an action against any current or future entity type without a schema change per new type; referential integrity here is a **soft** expectation (the `EntityType`+`EntityId` pair *should* resolve to a real row) verified by application logic and covered by tests (ADR-0021), not by the database |

### 2.23 OutboxMessages and IdempotencyRecords

| Relationship | Cardinality | Required/Optional | Delete Behavior | Ownership | Referential Integrity |
|---|---|---|---|---|---|
| OutboxMessages → IdempotencyRecords | many:1 | Required | **Restrict** | Infrastructure | This is a **refinement beyond `Architecture-Design.md`'s original schema sketch**, which put a bare `IdempotencyKey` column directly on `OutboxMessage`. Generalizing it into its own table lets the *same* idempotency mechanism also cover Genesys webhook dedup and the SLA sweep/scheduled-job overlap (ADR-0014), which the original sketch handled with separate, ad hoc keys per feature. **Flagged as a detailed-design refinement, not a new business decision** — the underlying idempotency requirement (ADR-0014) is unchanged. |
| GenesysInteractions → IdempotencyRecords | many:1 | Required | **Restrict** | Genesys Integration | Key = `ConversationId + eventType`, per `Genesys-Integration.md` §10 |

---

## 3. Cross-Cutting Referential-Integrity Notes

- **No cascading delete of any ticket-related row is ever exercised in practice**, because no code path deletes a `Ticket`. Cascade delete behaviors noted above (e.g., `TicketRequesterSnapshots`) exist only for schema completeness in the event a ticket were ever purged under a future, separately-approved data-lifecycle policy — not something this MVP design implements.
- **Every "Restrict" above means the database (or, where noted, the application layer) refuses an operation that would orphan a historical record** — consistent with the audit-immutability and 7-year retention requirements running through every prior architecture document.
- **Soft references** (`AuditEntries.EntityId`, `GenesysInteractions.AgentEmailOrExtension`) are called out explicitly wherever a hard FK is deliberately not used, so a future reader doesn't mistake the omission for an oversight.

## 4. Open Items Carried From Prior Documents (Not Re-Litigated, Just Flagged Again Here)

- `Genesys-Integration.md` §15's 8 open technical questions still govern `GenesysInteractions`' exact field reliability — this ERD is built to be resilient to those answers changing (e.g., `AgentEmailOrExtension` is nullable and un-FK'd specifically because of question #4), not to require them resolved first.
- ISSUE-016 (retention regulation) governs *how long* every "never deleted" table above is actually retained — this ERD assumes "at least 7 years, uniform," per the existing interim default, not a final answer.

---

## 5. What This Document Does Not Cover

Per instruction, this document stops at conceptual/logical ERD and relationship design. **Not included:** column-level types/nullability (see `MVP-Data-Dictionary.md`), SQL DDL/CREATE TABLE statements, EF Core entity classes or Fluent API configuration, migrations, or any connection to a real database. Those remain Phase 3 ("Project Foundation") deliverables.
