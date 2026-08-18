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
| UnitReferenceId, ContactReferenceId | int | No | FK → §2.7. **As of this review pass (Finding DR-01), populated by copying from the consumed `VerificationSessions` row (§2.24) at creation time — the create-ticket request supplies a `VerificationSessionId`, not these fields directly.** |
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
| AnsweredAtUtc, EndedAtUtc | datetime2 | Yes | Populated as later webhooks arrive, apply-if-absent (never overwritten once set) — see `MVP-ERD.md` §2.26 |
| LinkedTicketId | bigint | Yes | FK → Tickets |
| CorrelationId | uniqueidentifier | No | ADR-0014 |
| ProcessingStatus | tinyint | No | **Corrected in this review pass (Finding DR-04):** `Active` / `Completed` — the conversation-level state. **No longer includes a signature-failure value.** A request that fails signature validation is rejected before any row is written to this table at all (see `Genesys-Mock-Contract.md` §3) — it never reaches `ProcessingStatus`, `GenesysInteractions`, or `GenesysInteractionEvents`. Per-event acceptance/failure tracking (including dead-lettering) now lives on `GenesysInteractionEvents.ProcessingStatus` (§2.26), not here. |

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
| ApprovedByEmployeeId | uniqueidentifier | Yes | Required if `ChangeReason = Downgrade`. **As of this review pass (Finding DR-05), always copied from `PriorityDowngradeRequests.DecidedByEmployeeId` (§2.27) — never a directly client-supplied field on any endpoint.** |

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
| IsWithdrawn | bit | No | **Added in this review pass (Finding DR-06).** Default `false`. Replaces the physical `DELETE` endpoint — see `MVP-ERD.md` §2.19 |
| WithdrawnAtUtc | datetime2 | Yes | Null unless withdrawn |
| WithdrawnByEmployeeId | uniqueidentifier | Yes | FK → Employees; null unless withdrawn |
| WithdrawalReason | nvarchar(500) | Yes | Required (app-level) at the moment of withdrawal; null beforehand |
| BlobStatus | tinyint | No | **Added in this review pass (Finding DR-06).** `Stored` / `Quarantined` / `Purged` — tracks the underlying binary content's lifecycle independently of this metadata row, which is never deleted regardless of `BlobStatus` |

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

### 2.24 VerificationSessions

**Added in this review pass (Finding DR-01).** See `MVP-ERD.md` §2.24 for the full rationale.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| VerificationSessionId | uniqueidentifier | No | PK |
| AgentEmployeeId | uniqueidentifier | No | FK → Employees; the sole owner, per §2.24's single-agent-ownership rule |
| UnitReferenceId | int | Yes | FK → UnitReferences; null until selected |
| ContactReferenceId | int | Yes | FK → ContactReferences; null until selected |
| SnapshotUnitNumber, SnapshotPropertyName, SnapshotTowerName, SnapshotUnitType | nvarchar | Yes | Captured at confirmation time from the cache read-back — copied verbatim into `TicketRequesterSnapshots` on consumption, not re-fetched |
| SnapshotContactDisplayName, SnapshotContactChannel | nvarchar | Yes | Same as above |
| ConfirmedVerbally | bit | No | Default `false`; must be `true` before `Status` can advance to `Confirmed` |
| Status | tinyint | No | 1=InProgress, 2=Confirmed, 3=Consumed, 4=Expired, 5=Abandoned |
| CreatedAtUtc | datetime2 | No | |
| ConfirmedAtUtc | datetime2 | Yes | Null until `Status = Confirmed` |
| ExpiresAtUtc | datetime2 | No | `[ASSUMPTION]` `CreatedAtUtc` + 30 minutes |
| ConsumedAtUtc | datetime2 | Yes | Null until `Status = Consumed` |
| ConsumedByTicketId | bigint | Yes | FK → Tickets; set exactly once, at consumption |

*(See `MVP-ERD.md` §2.24 for relationships, including expiry/single-use/ownership/audit/CRM-outage behavior.)*

### 2.25 GenesysAgentMappings

**Added in this review pass (Finding DR-02).** Backs `MVP-API-Contracts.md` §6.6.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| GenesysAgentMappingId | int | No | PK |
| EmployeeId | uniqueidentifier | No | FK → Employees |
| GenesysAgentId | nvarchar(100) | Yes | At least one of this and `AgentEmailOrExtension` must be non-null (app-enforced) |
| AgentEmailOrExtension | nvarchar(200) | Yes | Same identifier format as `GenesysInteractions.AgentEmailOrExtension`, for direct matching |
| IsActive | bit | No | Default `true`; deactivation, not deletion, is the only "removal" path |
| CreatedAtUtc | datetime2 | No | |
| CreatedByEmployeeId | uniqueidentifier | No | System Administrator who created the mapping |
| DeactivatedAtUtc | datetime2 | Yes | Null while active |
| DeactivatedByEmployeeId | uniqueidentifier | Yes | Null while active |

*(See `MVP-ERD.md` §2.25 for relationships, including uniqueness rules.)*

### 2.26 GenesysInteractionEvents

**Added in this review pass (Finding DR-03).** See `MVP-ERD.md` §2.26 and `Genesys-Mock-Contract.md` §4 for the full idempotency-key and out-of-order/duplicate/retry behavior this table enables.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| GenesysInteractionEventId | bigint | No | PK |
| GenesysInteractionId | bigint | No | FK → GenesysInteractions (the conversation this event belongs to) |
| ProviderEventId | nvarchar(100) | Yes | The provider's own per-event identifier, when present. **Preferred idempotency-key input when confirmed reliable** (`Genesys-Integration.md` §15 item 1, open) |
| FallbackDedupKey | nvarchar(300) | Yes | Computed as `ConversationId + EventType + RawPayloadHash + time-bucket` when `ProviderEventId` is absent or not yet confirmed reliable — see `MVP-ERD.md` §2.26 |
| RawPayloadHash | nvarchar(128) | No | SHA-256 (or equivalent) hash of the canonicalized inbound payload. **The raw payload itself is never persisted here or anywhere else** (Finding DR-04) |
| EventType | nvarchar(100) | No | As received (mock names in `Genesys-Mock-Contract.md` §1; real enum values unconfirmed) |
| ReceivedAtUtc | datetime2 | No | |
| ProcessingStatus | tinyint | No | 1=Received, 2=Applied, 3=DeadLettered. **Never a signature-failure value — see §2.11's note and Finding DR-04; a signature failure never produces a row here** |
| Attempts | int | No | Default 0; mirrors the retry bookkeeping pattern used on `OutboxMessages` (§2.23) |
| LastError | nvarchar(2000) | Yes | |
| IdempotencyRecordId | bigint | No | FK → IdempotencyRecords (1:1 — see `MVP-ERD.md` §2.26) |
| OutboxMessageId | uniqueidentifier | Yes | FK → OutboxMessages; null briefly between acceptance and dispatch |
| CorrelationId | uniqueidentifier | No | |

*(See `MVP-ERD.md` §2.26 for relationships.)*

### 2.27 PriorityDowngradeRequests

**Added in this review pass (Finding DR-05).** See `MVP-ERD.md` §2.27 for the full rationale and the self-authorization defect this closes.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| PriorityDowngradeRequestId | bigint | No | PK |
| TicketId | bigint | No | FK → Tickets |
| CurrentPriorityId | tinyint | No | Snapshot of the ticket's priority at request time |
| RequestedPriorityId | tinyint | No | FK → Priorities; must be a genuine decrease |
| Reason | nvarchar(1000) | No | Required |
| RequestedByEmployeeId | uniqueidentifier | No | FK → Employees; from the authenticated caller, never client-supplied |
| RequestedAtUtc | datetime2 | No | |
| Status | tinyint | No | 1=Pending, 2=Approved, 3=Rejected, 4=Expired, 5=Superseded |
| DecidedByEmployeeId | uniqueidentifier | Yes | FK → Employees; null while `Pending`. **Populated exclusively from the authenticated approver's identity at the moment of approve/reject — never accepted as an input field on any endpoint** |
| DecidedAtUtc | datetime2 | Yes | Null while `Pending` |
| DecisionNote | nvarchar(1000) | Yes | Required (app-level) when `Status = Rejected` |
| ExpiresAtUtc | datetime2 | No | `[ASSUMPTION]` `RequestedAtUtc` + 24 hours |
| RowVersion | rowversion | No | Optimistic concurrency on the approve/reject action, independent of the ticket's own `RowVersion` |

*(See `MVP-ERD.md` §2.27 for relationships, including the at-most-one-pending-per-ticket rule and expiry behavior.)*

---

## What This Document Does Not Cover

Per instruction, this document stops at conceptual/logical column definitions. **Not included:** SQL DDL/CREATE TABLE statements, EF Core entity classes or Fluent API configuration, migrations, or any connection to a real database. Those remain Phase 3 ("Project Foundation") deliverables. Relationship cardinality, ownership, delete behavior, and referential-integrity notes are in `MVP-ERD.md`, not repeated here.
