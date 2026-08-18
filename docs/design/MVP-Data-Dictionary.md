# Tiger Group — CS Ticketing System
## MVP Data Dictionary

| | |
|---|---|
| **Status** | Design for review — conceptual/logical design only |
| **Scope** | Column-level detail (type, nullability, notes) for every entity in `docs/design/MVP-ERD.md`, using the same §2.1–2.23 numbering for cross-reference |
| **Explicitly not done here** | No SQL DDL, no EF Core entity classes or migrations, no connection to a real database, no application code |
| **Base** | `main` @ `4fe6f19` (post PR #1/#3/#4 merge — the full architecture package) |
| **Related documents** | `docs/design/MVP-ERD.md` (Mermaid ER diagram, relationship cardinalities, ownership, delete behavior, and integrity notes for the same entities) · `docs/architecture/Domain-Model.md` |
| **Date** | 2026-08-18 |

**Companion document:** This file holds only column-by-column type/nullability/notes detail. Relationship cardinality, ownership, delete behavior, and referential-integrity notes for these same entities are in `docs/design/MVP-ERD.md` §2, under matching section numbers.

---

## 2. Data Dictionary

### 2.1 AspNetUsers / AspNetRoles / AspNetUserRoles (Identity, framework-owned)

**Purpose:** ASP.NET Core Identity's own tables (ADR-0004). Not redefined here beyond noting the extension point.

| Column (AspNetUsers) | Type | Notes |
|---|---|---|
| Id | uniqueidentifier | PK |
| UserName, Email, PasswordHash, LockoutEnd, AccessFailedCount, ... | framework-defined | Standard Identity columns |

*(See `MVP-ERD.md` §2.1 for relationships.)*

### 2.2 Employees

**Purpose:** Domain extension of `AspNetUsers`, carrying staff attributes not in Identity's own schema (ADR-0004).

| Column | Type | Nullable | Notes |
|---|---|---|---|
| EmployeeId | uniqueidentifier | No | PK, FK → AspNetUsers.Id |
| DisplayName | nvarchar(200) | No | |
| IsGeynessStaff | bit | No | Distinguishes Tiger vs. Geyness employment for reporting |
| DeactivatedAtUtc | datetime2 | Yes | Null = active. Set on departure; never hard-deleted (FR-ADM-02) |
| CreatedAtUtc | datetime2 | No | |

**Note:** `DepartmentId` is deliberately **not** a column here — see `MVP-ERD.md` §0.1 and `UserDepartmentAssignments` below.

*(See `MVP-ERD.md` §2.2 for relationships.)*

### 2.3 Departments

| Column | Type | Nullable | Notes |
|---|---|---|---|
| DepartmentId | int | No | PK |
| Name | nvarchar(100) | No | Unique |
| Code | varchar(10) | No | Unique; backs the `[DEPT]` segment of `TicketNumber` (ADR-0004) |
| IsActive | bit | No | |

*(See `MVP-ERD.md` §2.3 for relationships.)*

### 2.4 UserDepartmentAssignments

**Purpose:** Many-to-many Employee↔Department membership, with one primary department per employee (`MVP-ERD.md` §0.1).

| Column | Type | Nullable | Notes |
|---|---|---|---|
| UserDepartmentAssignmentId | int | No | PK |
| EmployeeId | uniqueidentifier | No | FK → Employees |
| DepartmentId | int | No | FK → Departments |
| IsPrimary | bit | No | Exactly one `true` row per `EmployeeId` (app-enforced; a filtered unique index on `(EmployeeId)` where `IsPrimary = 1` is the recommended DB-level backstop) |
| AssignedAtUtc | datetime2 | No | |
| AssignedByEmployeeId | uniqueidentifier | Yes | Null for the initial/seed assignment |

*(See `MVP-ERD.md` §2.4 for relationships.)*

### 2.5 Categories

| Column | Type | Nullable | Notes |
|---|---|---|---|
| CategoryId | int | No | PK |
| Name | nvarchar(100) | No | e.g., "Facility Management", "Corrective Maintenance" |
| ParentCategoryId | int | Yes | Self-ref; non-null only for FM sub-categories |
| DepartmentId | int | No | Routing target |
| IsActive | bit | No | |

*(See `MVP-ERD.md` §2.5 for relationships.)*

### 2.6 Priorities and SlaPolicies

| Column (Priorities) | Type | Nullable | Notes |
|---|---|---|---|
| PriorityId | tinyint | No | PK — fixed set: 1=Critical, 2=High, 3=Medium, 4=Low |
| Name | nvarchar(20) | No | |
| DisplayOrder | tinyint | No | |

| Column (SlaPolicies) | Type | Nullable | Notes |
|---|---|---|---|
| PriorityId | tinyint | No | PK **and** FK → Priorities (1:1, identifying) |
| FirstResponseTargetMinutes | int | No | |
| ResolutionTargetMinutes | int | No | |
| ClockBasis | tinyint | No | 1=24/7, 2=BusinessHours |
| WarningThresholdPercent | decimal(5,2) | No | Approved defaults: Critical 50.00; others 75.00 |
| Level2ToGmWindowValue | int | No | Approved defaults: Critical 30; High 2; Medium 1; Low 2 |
| Level2ToGmWindowUnit | tinyint | No | 1=Minutes, 2=Hours, 3=BusinessDays |
| IsActive | bit | No | |

*(See `MVP-ERD.md` §2.6 for relationships.)*

### 2.7 UnitReferences and ContactReferences (CRM cache — NOT master data)

**Purpose:** Store only the CRM-issued identifier plus a refreshable display cache (ADR-0006). **Never the authoritative record.**

| Column (UnitReferences) | Type | Nullable | Notes |
|---|---|---|---|
| UnitReferenceId | int | No | PK |
| CrmUnitId | nvarchar(64) | No | Unique — the actual key; this system never invents unit identity |
| UnitNumber, PropertyName, TowerName, UnitType | nvarchar | Varies | Display cache only, refreshed from CRM |
| LastSyncedAtUtc | datetime2 | No | |

| Column (ContactReferences) | Type | Nullable | Notes |
|---|---|---|---|
| ContactReferenceId | int | No | PK |
| CrmContactId | nvarchar(64) | No | Unique |
| UnitReferenceId | int | No | FK — a unit has many contact rows (joint owners/tenants) |
| DisplayName, ContactChannel | nvarchar | Yes | Cache only |
| ContactType | tinyint | No | 1=Owner, 2=Tenant, 3=Representative |
| AuthorizedRepresentativeOf | int | Yes | Self-ref; supports ISSUE-007's CRM-recorded-authorization requirement |
| LastSyncedAtUtc | datetime2 | No | |

*(See `MVP-ERD.md` §2.7 for relationships.)*

### 2.8 TicketRequesterSnapshots

**Purpose:** The immutable, write-once record of what the agent actually read back (ADR-0007).

| Column | Type | Nullable | Notes |
|---|---|---|---|
| TicketId | bigint | No | PK **and** FK → Tickets (1:1, identifying) |
| SnapshotUnitNumber | nvarchar(50) | No | |
| SnapshotPropertyName, SnapshotTowerName, SnapshotUnitType | nvarchar | Yes | |
| SnapshotContactDisplayName, SnapshotContactChannel | nvarchar | Yes | |
| CapturedAtUtc | datetime2 | No | |

*(See `MVP-ERD.md` §2.8 for relationships.)*

### 2.9 IntakeRecords

| Column | Type | Nullable | Notes |
|---|---|---|---|
| IntakeRecordId | bigint | No | PK |
| ChannelId | tinyint | No | MVP: Phone only (per scope) |
| ReceivedAtUtc | datetime2 | No | |
| RawUnitNumberEntered | nvarchar(50) | Yes | As spoken, pre-CRM-match — **not** a FK to UnitReferences |
| PriorityHint | tinyint | Yes | FK → Priorities; agent's initial read, pre-classification |
| CrmVerificationStatus | tinyint | No | 1=Unverified, 2=PendingCrmVerification, 3=Verified |
| CreatedByEmployeeId | uniqueidentifier | No at MVP | Always an agent, since MVP is manual-creation-only |
| LinkedTicketId | bigint | Yes | Set once promoted |

*(See `MVP-ERD.md` §2.9 for relationships.)*

### 2.10 Tickets

*(Column list per the ERD diagram in `MVP-ERD.md` §1; repeated here for the data dictionary.)*

| Column | Type | Nullable | Notes |
|---|---|---|---|
| TicketId | bigint | No | PK |
| TicketNumber | varchar(40) | No | Unique, **immutable** (ADR-0004) |
| OriginatingDepartmentId | int | No | FK → Departments; write-once |
| CurrentDepartmentId | int | No | FK → Departments; mutable on transfer |
| CurrentOwnerEmployeeId | uniqueidentifier | Yes | FK → Employees |
| UnitReferenceId, ContactReferenceId | int | No | FK → §2.7 |
| CategoryId | int | No | FK → Categories |
| PriorityId | tinyint | No | FK → Priorities (current tier) |
| TicketStatus, VerificationStatus, EscalationLevel, SlaState | tinyint | No | Independent dimensions (ADR-0008) |
| ResolutionOutcome | tinyint | Yes | Null until Resolved/Closed |
| DuplicateOfTicketId | bigint | Yes | Self-ref FK; required (app-level) when `ResolutionOutcome = Duplicate` |
| RequestSummary | nvarchar(2000) | No | |
| FirstHumanResponseAtUtc | datetime2 | Yes | Satisfies ISSUE-019; ticket-level (not per SLA period) |
| AcknowledgementSentAtUtc | datetime2 | Yes | Automated ack — never satisfies First Response |
| ReopenCount | int | No | Default 0 |
| CreatedAtUtc | datetime2 | No | |
| RowVersion | rowversion | No | Optimistic concurrency |

**Design note:** `FirstResponseDueAtUtc`/`ResolutionDueAtUtc` are **not** columns on `Tickets` — they live only on the current (open-ended) `TicketSlaInstances` row, to avoid two sources of truth for the active due timestamps. Every read of "what's this ticket's current SLA deadline" joins to `TicketSlaInstances` where `PeriodEndAtUtc IS NULL`.

*(See `MVP-ERD.md` §2.10 for relationships.)*

### 2.11 GenesysInteractions

| Column | Type | Nullable | Notes |
|---|---|---|---|
| GenesysInteractionId | bigint | No | PK |
| ConversationId | nvarchar(100) | No | Unique — Genesys's own identifier |
| CallerNumber | nvarchar(30) | Yes | Masked in logs (`Security-Architecture.md` §11); **not** a FK to any contact record |
| GenesysAgentId | nvarchar(100) | No | |
| AgentEmailOrExtension | nvarchar(200) | Yes | Reliability unconfirmed — open question, `Genesys-Integration.md` §15 item 4 |
| ChannelMediaType | nvarchar(30) | No | MVP: voice only |
| StartedAtUtc | datetime2 | No | |
| AnsweredAtUtc, EndedAtUtc | datetime2 | Yes | Populated as later webhooks arrive |
| LinkedTicketId | bigint | Yes | FK → Tickets |
| CorrelationId | uniqueidentifier | No | ADR-0014 |
| ProcessingStatus | tinyint | No | Received / Validated / Rejected (signature failure) |

*(See `MVP-ERD.md` §2.11 for relationships.)*

### 2.12 TicketAssignments

| Column | Type | Nullable | Notes |
|---|---|---|---|
| TicketAssignmentId | bigint | No | PK |
| TicketId | bigint | No | FK |
| AssignedEmployeeId | uniqueidentifier | No | FK |
| AssignedDepartmentId | int | No | FK — department at time of assignment (historical, may differ from `Tickets.CurrentDepartmentId` for old rows) |
| AssignedAtUtc | datetime2 | No | |
| AssigningActorEmployeeId | uniqueidentifier | Yes | Null if system-assigned |
| IsCurrent | bit | No | Exactly one `true` row per `TicketId` (app-enforced; filtered unique index recommended) |

*(See `MVP-ERD.md` §2.12 for relationships.)*

### 2.13 TicketStatusHistory

| Column | Type | Nullable | Notes |
|---|---|---|---|
| TicketStatusHistoryId | bigint | No | PK |
| TicketId | bigint | No | FK |
| Dimension | tinyint | No | 1=TicketStatus, 2=VerificationStatus, 3=EscalationLevel, 4=SlaState, 5=ResolutionOutcome |
| OldValue | tinyint | Yes | Null for the first-ever row per dimension |
| NewValue | tinyint | No | |
| ActorEmployeeId | uniqueidentifier | Yes | Null when `ActorIsSystem = true` |
| ActorIsSystem | bit | No | |
| Note | nvarchar(1000) | Yes | |
| CorrelationId | uniqueidentifier | No | |
| OccurredAtUtc | datetime2 | No | |

*(See `MVP-ERD.md` §2.13 for relationships.)*

### 2.14 TicketResolutions

| Column | Type | Nullable | Notes |
|---|---|---|---|
| TicketResolutionId | bigint | No | PK |
| TicketId | bigint | No | FK |
| ResolutionOutcome | tinyint | No | Resolved/Cancelled/Rejected/Duplicate |
| ResolutionNote | nvarchar(4000) | No | Mandatory (BR-011) |
| ReasonCode | tinyint | Yes | Required (app-level) for Cancelled/Rejected |
| DuplicateOfTicketId | bigint | Yes | Required (app-level) for Duplicate |
| ResolvingEmployeeId | uniqueidentifier | No | |
| ResolvedAtUtc | datetime2 | No | |
| IsCurrent | bit | No | Set false on Reopen, preserving history |

*(See `MVP-ERD.md` §2.14 for relationships.)*

### 2.15 TicketSlaInstances

*(Columns per the ERD diagram in `MVP-ERD.md` §1.)*

| Column | Type | Nullable | Notes |
|---|---|---|---|
| TicketSlaInstanceId | bigint | No | PK |
| TicketId | bigint | No | FK |
| PriorityId | tinyint | No | FK → Priorities |
| PeriodStartAtUtc | datetime2 | No | |
| PeriodEndAtUtc | datetime2 | Yes | Null = current period |
| FirstResponseDueAtUtc | datetime2 | No | |
| ResolutionDueAtUtc | datetime2 | No | |
| FirstResponseBreached | bit | No | Immutable once true |
| ResolutionBreached | bit | No | Immutable once true |
| ChangeReason | tinyint | No | e.g., InitialCreation/Upgrade/Downgrade |
| ApprovedByEmployeeId | uniqueidentifier | Yes | Required if `ChangeReason = Downgrade` |

*(See `MVP-ERD.md` §2.15 for relationships, including the immutability rule for the breach flags.)*

### 2.16 TicketSlaPausePeriods

| Column | Type | Nullable | Notes |
|---|---|---|---|
| TicketSlaPausePeriodId | bigint | No | PK |
| TicketSlaInstanceId | bigint | No | FK |
| PauseReason | tinyint | No | 1=PendingCustomer, 2=PendingThirdParty (never Critical — see below) |
| PausedAtUtc | datetime2 | No | |
| ResumedAtUtc | datetime2 | Yes | Null = still paused |
| PausedDurationMinutes | int | Yes | Computed and written on resume |

*(See `MVP-ERD.md` §2.16 for relationships, including the Critical-never-pauses invariant.)*

### 2.17 TicketEscalations

| Column | Type | Nullable | Notes |
|---|---|---|---|
| TicketEscalationId | bigint | No | PK |
| TicketId | bigint | No | FK |
| Level | tinyint | No | |
| TriggerType | tinyint | No | AutoBreach/AutoWindowExpired/ManualFlag/ManualLevel4 |
| NotifiedRoles | nvarchar(200) | Yes | e.g. "DeptHead,GM" — **distinct from `Level`** (ADR-0011) |
| RaisedAtUtc | datetime2 | No | |
| RespondedAtUtc | datetime2 | Yes | |
| RespondingEmployeeId | uniqueidentifier | Yes | |

*(See `MVP-ERD.md` §2.17 for relationships.)*

### 2.18 TicketNotes

| Column | Type | Nullable | Notes |
|---|---|---|---|
| TicketNoteId | bigint | No | PK |
| TicketId | bigint | No | FK |
| NoteText | nvarchar(2000) | No | |
| AuthorEmployeeId | uniqueidentifier | No | |
| CreatedAtUtc | datetime2 | No | |
| RelatedStatusChangeId | bigint | Yes | FK → TicketStatusHistory, if the note accompanied a status change |

*(See `MVP-ERD.md` §2.18 for relationships.)*

### 2.19 TicketAttachments

| Column | Type | Nullable | Notes |
|---|---|---|---|
| TicketAttachmentId | bigint | No | PK |
| TicketId | bigint | No | FK |
| FileName, ContentType | nvarchar | No | |
| SizeBytes | bigint | No | `[ASSUMPTION]` ≤25MB, app-enforced |
| StorageReference | nvarchar(500) | No | Opaque blob key (ADR-0017) |
| VirusScanStatus | tinyint | No | Pending/Clean/Rejected |
| UploadedByEmployeeId | uniqueidentifier | Yes | Null only if a future non-agent channel submits one (not applicable at MVP — always populated in this pilot) |
| UploadedAtUtc | datetime2 | No | |

*(See `MVP-ERD.md` §2.19 for relationships.)*

### 2.20 BusinessCalendars, BusinessCalendarWorkingDays, Holidays

| Column (BusinessCalendars) | Type | Nullable | Notes |
|---|---|---|---|
| BusinessCalendarId | int | No | PK |
| Name | nvarchar(100) | No | e.g. "Default Pilot Calendar" |
| BusinessDayStartLocal, BusinessDayEndLocal | time | No | 08:00 / 18:00 |
| TimeZone | nvarchar(50) | No | |
| EffectiveFromUtc | datetime2 | No | |
| IsActive | bit | No | |

| Column (BusinessCalendarWorkingDays) | Type | Nullable | Notes |
|---|---|---|---|
| BusinessCalendarWorkingDayId | int | No | PK |
| BusinessCalendarId | int | No | FK |
| DayOfWeek | tinyint | No | 0–6 |
| IsWorkingDay | bit | No | Seeded per ISSUE-017 (approved: Sat–Thu working, Friday off) |

| Column (Holidays) | Type | Nullable | Notes |
|---|---|---|---|
| HolidayId | int | No | PK |
| BusinessCalendarId | int | No | FK |
| HolidayDate | date | No | Unique per calendar |
| Description | nvarchar(200) | Yes | |
| EnteredByEmployeeId | uniqueidentifier | No | Technical administrator (System Administrator), ISSUE-012 |
| ConfirmedByEmployeeId | uniqueidentifier | Yes | Business owner (Customer Service/HR); **flagged `[ASSUMPTION]`** — nullable here because the exact confirmation workflow (must it be confirmed before it takes effect?) is not yet specified, per `Domain-Model.md`'s existing flag on this entity |

*(See `MVP-ERD.md` §2.20 for relationships.)*

### 2.21 Notifications

| Column | Type | Nullable | Notes |
|---|---|---|---|
| NotificationId | bigint | No | PK |
| TicketId | bigint | Yes | Null only for a non-ticket-specific notice `[ASSUMPTION — at MVP, every notification is ticket-scoped; nullable kept for forward compatibility, not because MVP needs it]` |
| NotificationType | tinyint | No | Acknowledgement/Warning/Breach/Escalation |
| RecipientEmployeeId | uniqueidentifier | Yes | Null for an external recipient (the customer, via email) |
| RecipientAddress | nvarchar(320) | Yes | Populated for external/email recipients |
| Channel | tinyint | No | MVP: Email only |
| DeliveryStatus | tinyint | No | Pending/Sent/Failed/DeadLettered |
| CorrelationId | uniqueidentifier | No | |
| OutboxMessageId | uniqueidentifier | Yes | FK → OutboxMessages |
| RetryCount | int | No | Default 0 |
| CreatedAtUtc | datetime2 | No | |

*(See `MVP-ERD.md` §2.21 for relationships.)*

### 2.22 AuditEntries

| Column | Type | Nullable | Notes |
|---|---|---|---|
| AuditEntryId | bigint | No | PK |
| ActorEmployeeId | uniqueidentifier | Yes | Null for a system action |
| Action | nvarchar(100) | No | |
| EntityType | nvarchar(100) | No | e.g. "Ticket", "SlaPolicy" |
| EntityId | nvarchar(100) | Yes | Generic string, not a typed FK (see `MVP-ERD.md` §2.22) |
| BeforeValue, AfterValue | nvarchar(max) | Yes | JSON |
| CorrelationId | uniqueidentifier | No | |
| OccurredAtUtc | datetime2 | No | |

*(See `MVP-ERD.md` §2.22 for relationships, including why no FK is enforced on `EntityId`.)*

### 2.23 OutboxMessages and IdempotencyRecords

| Column (OutboxMessages) | Type | Nullable | Notes |
|---|---|---|---|
| OutboxMessageId | uniqueidentifier | No | PK |
| EventType | nvarchar(200) | No | |
| Payload | nvarchar(max) | No | JSON |
| CorrelationId | uniqueidentifier | No | |
| IdempotencyRecordId | bigint | No | FK → IdempotencyRecords (replaces a bare `IdempotencyKey` string column — see below) |
| Status | tinyint | No | Pending/Processed/DeadLettered |
| Attempts | int | No | |
| LastError | nvarchar(2000) | Yes | |
| OccurredAtUtc, ProcessedAtUtc | datetime2 | ProcessedAtUtc nullable | |

| Column (IdempotencyRecords) | Type | Nullable | Notes |
|---|---|---|---|
| IdempotencyRecordId | bigint | No | PK |
| IdempotencyKey | nvarchar(300) | No | Unique; e.g. `TicketId+EventType+EventVersion` or `ConversationId+eventType` |
| Scope | nvarchar(50) | No | "OutboxDispatch" / "GenesysWebhook" / "SlaBreachCheck" — generalizes idempotency beyond just Outbox (ADR-0014) |
| FirstSeenAtUtc, LastSeenAtUtc | datetime2 | No | |
| ResultReference | nvarchar(200) | Yes | Lets a duplicate request return the same prior result rather than recomputing |
| ExpiresAtUtc | datetime2 | Yes | Housekeeping — old records may be purged after a retention window `[ASSUMPTION — window not yet specified]` |

*(See `MVP-ERD.md` §2.23 for relationships, including why idempotency was generalized beyond a bare column on `OutboxMessage`.)*

---

## What This Document Does Not Cover

Per instruction, this document stops at conceptual/logical column definitions. **Not included:** SQL DDL/CREATE TABLE statements, EF Core entity classes or Fluent API configuration, migrations, or any connection to a real database. Those remain Phase 3 ("Project Foundation") deliverables. Relationship cardinality, ownership, delete behavior, and referential-integrity notes are in `MVP-ERD.md`, not repeated here.
