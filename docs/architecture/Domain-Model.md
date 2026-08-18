# Tiger Group — Customer Service Ticketing System
## Domain Model (Conceptual)

| | |
|---|---|
| **Status** | Approved for Architecture Design |
| **Purpose** | Define the conceptual domain model for the 3-week pilot MVP. **No SQL DDL, EF Core mappings, or migrations are produced here** — this is conceptual design only, per explicit instruction. |
| **Related documents** | `Module-Design.md` · `SLA-Architecture.md` · `Genesys-Integration.md` · ADRs 0006–0019 |
| **Date** | 2026-08-17 |

Each entity lists Purpose, Key Attributes (conceptual, not typed columns), Relationships, Invariants, Ownership (which module owns writes), and Lifecycle (how it is created, changed, and — if applicable — retired).

---

## Ticket

- **Purpose:** The central aggregate representing a single customer service request from creation through closure.
- **Key attributes:** Immutable ticket number (`TG-[DEPT]-[YYYYMMDD]-[SEQ]`); originating department (immutable) vs. current department (mutable); current owner; channel; category/sub-category; priority; the five independent state dimensions — `TicketStatus`, `VerificationStatus`, `EscalationLevel`, `SlaState`, `ResolutionOutcome`; reopen count; request summary; resolution note.
- **Relationships:** One `TicketRequesterSnapshot` (embedded/1:1); many `TicketStatusHistory`, `TicketAssignment`, `TicketSlaInstance`, `TicketEscalation`, `TicketNote`, `TicketAttachment`; zero-or-one `GenesysInteraction` link; zero-or-one `TicketResolution`; zero-or-one self-reference (`DuplicateOfTicketId`) when `ResolutionOutcome = Duplicate`.
- **Invariants:** Ticket number never changes after creation (ADR per ISSUE-020). `VerificationStatus` must be `Verified` before the ticket is department-visible. Closure requires `ResolutionOutcome` already set and customer-notification confirmed (ISSUE-022). A `Duplicate` outcome requires a valid `DuplicateOfTicketId`.
- **Ownership:** Ticketing module.
- **Lifecycle:** Created by an agent after CRM verification (or promoted from an `IntakeRecord`); transitions through `TicketStatus` per `System-Architecture.md` §7; never physically deleted — retained per the data-retention requirement (ISSUE-016, pending Legal/Compliance confirmation before go-live).

## TicketRequesterSnapshot

- **Purpose:** An immutable, point-in-time copy of the unit/contact details the agent actually read back and relied on at verification time (ADR-0007) — never re-synced from CRM afterward.
- **Key attributes:** Unit number, property, tower, unit type, contact display name, contact channel (phone/email used for outreach).
- **Relationships:** Owned 1:1 by `Ticket`.
- **Invariants:** Write-once; no update path exists once the parent ticket is created.
- **Ownership:** Ticketing module (written at ticket creation, using data supplied by CRM Verification).
- **Lifecycle:** Created exactly once, at the same moment as its parent `Ticket`. Never modified or deleted independently of the ticket.

## GenesysInteraction

- **Purpose:** Represents one Genesys call/conversation and its link to a ticket (ADR-0019).
- **Key attributes:** Conversation ID, caller number, Genesys agent ID, agent email/extension (when available), channel/media type, interaction start/answer/end timestamps, linked ticket reference, correlation ID, idempotency key, webhook processing status.
- **Relationships:** Zero-or-one link to `Ticket` (a call may arrive before a ticket exists, or never result in one).
- **Invariants:** `ConversationId` is unique per interaction; a duplicate webhook for the same `ConversationId` + event type must not create a second record or re-trigger a side effect (idempotency, ADR-0014). The answer timestamp is immutable once recorded.
- **Ownership:** Genesys Integration module.
- **Lifecycle:** Created on the first webhook event for a conversation (e.g., start); updated as answer/end events arrive; linked to a `Ticket` either automatically (if a ticket already exists for a returning caller — [ASSUMPTION, not yet confirmed as MVP behavior]) or manually by the agent handling the call.

## TicketAssignment

- **Purpose:** Records who currently owns a ticket and the history of ownership changes.
- **Key attributes:** Assigned employee, department at time of assignment, assigned-at timestamp, assigning actor.
- **Relationships:** Many per `Ticket` (history); the ticket's `CurrentOwnerEmployeeId` reflects the latest.
- **Invariants:** A ticket has at most one *current* assignment at a time; every reassignment is recorded, not overwritten.
- **Ownership:** Ticketing module.
- **Lifecycle:** Created on initial assignment; a new row created on every reassignment or transfer — never updated in place.

## TicketStatusHistory

- **Purpose:** The audit trail for every change to any of the five ticket-state dimensions (ADR-0018).
- **Key attributes:** Which dimension changed, old value, new value, actor (or system), correlation ID, timestamp, optional note.
- **Relationships:** Many per `Ticket`.
- **Invariants:** Append-only — no update or delete path exists.
- **Ownership:** Audit module (written on Ticketing's behalf via event subscription — see `Module-Design.md`'s ownership note).
- **Lifecycle:** One row per dimension change, for the life of the ticket and beyond (retained per the retention policy).

## TicketResolution

- **Purpose:** Captures the specific outcome and mandatory note when a ticket is resolved, cancelled, rejected, or marked duplicate.
- **Key attributes:** `ResolutionOutcome` value, resolution note, reason code (for Cancelled/Rejected), `DuplicateOfTicketId` (for Duplicate), resolving actor, resolved-at timestamp.
- **Relationships:** Zero-or-one per `Ticket`; self-referencing link to another `Ticket` when Duplicate.
- **Invariants:** Resolution note is mandatory before this record can exist. `DuplicateOfTicketId` is mandatory when outcome is Duplicate, and must reference an existing ticket.
- **Ownership:** Ticketing module.
- **Lifecycle:** Created once, when a Department Employee/Head performs the Resolve action (or Cancel/Reject); on Reopen, the prior resolution is archived (not deleted) and a new one is created if the ticket is resolved again.

## TicketSlaInstance

- **Purpose:** One immutable row per SLA period a ticket passes through — the original period at creation, plus one new period per priority change (ADR-0012).
- **Key attributes:** Priority tier for this period, period start/end, First Response due timestamp, Resolution due timestamp, First Response breached flag, Resolution breached flag, change reason (initial/upgrade/downgrade), approving employee + timestamp (downgrade only).
- **Relationships:** Many per `Ticket`.
- **Invariants:** Once a breach flag is set to true, it is never cleared or reversed by a later change (ADR-0012). A downgrade-reason row requires an approving employee before its due dates take effect.
- **Ownership:** SLA and Escalation module.
- **Lifecycle:** One row created at ticket creation; a new row created (and the prior row's `PeriodEndAtUtc` set) on every priority change — never updated after its period ends.

## TicketEscalation

- **Purpose:** Records escalation-level changes and who was notified at each point (ADR-0011).
- **Key attributes:** Escalation level, trigger type (auto-breach, auto-window-expired, manual flag, manual Level 4), notified roles (distinct from the level itself), raised-at timestamp, responded-at timestamp, responding employee.
- **Relationships:** Many per `Ticket`.
- **Invariants:** `NotifiedRoles` and `Level` are independent — a notification does not imply a level change, and vice versa. Level 4 only ever originates from a manual trigger type.
- **Ownership:** SLA and Escalation module.
- **Lifecycle:** A new row per escalation event; the ticket's current `EscalationLevel` reflects the latest level-changing row.

## TicketNote

- **Purpose:** Free-text notes recorded during the ticket's lifecycle (e.g., "waiting on customer for a photo," "contractor scheduled for Thursday").
- **Key attributes:** Note text, authoring employee, timestamp, associated `TicketStatus` transition if any.
- **Relationships:** Many per `Ticket`.
- **Invariants:** Immutable once written (edits create a new note, not an in-place change) — consistent with audit-immutability requirements.
- **Ownership:** Ticketing module.
- **Lifecycle:** Created whenever an agent/department adds context; never deleted.

## TicketAttachment

- **Purpose:** Metadata and storage reference for a file attached to a ticket (ADR-0017).
- **Key attributes:** File name, content type, size, storage reference, virus-scan status, uploading employee (or none, for a future customer-submitted channel — not in MVP).
- **Relationships:** Many per `Ticket` (up to 10, enforced at the application layer).
- **Invariants:** Not available/visible until `VirusScanStatus = Clean`. Count per ticket ≤ 10; size per file ≤ [ASSUMPTION] 25MB.
- **Ownership:** Attachments module.
- **Lifecycle:** Created on upload; scan status updated asynchronously; never physically deleted from the database record (the underlying blob's retention follows the same 7-year policy).

## Department

- **Purpose:** Represents Real Estate, Leasing, and Facility Management (and any internal CS/administrative grouping) for routing and ownership.
- **Key attributes:** Name, code (backs the `[DEPT]` segment of the ticket number), active flag.
- **Relationships:** Many `Employee` records; referenced by `Ticket` (originating and current).
- **Invariants:** Code is immutable once tickets reference it (changing it would break existing ticket numbers' meaning).
- **Ownership:** Administration module (reference data), Identity and Access (for `Employee` linkage).
- **Lifecycle:** Rarely changes; seeded at setup, edited only by System Administrator.

## Category

- **Purpose:** Represents the classification taxonomy (Sales Enquiry, Leasing, Facility Management, Complaint, General Information) and FM sub-categories.
- **Key attributes:** Name, parent category (for sub-categories), department-routing mapping.
- **Relationships:** Referenced by `Ticket`.
- **Invariants:** FM sub-category is mandatory when category is Facility Management.
- **Ownership:** Classification and Routing module.
- **Lifecycle:** Reference data, rarely changed.

## Priority

- **Purpose:** Represents Critical/High/Medium/Low and their associated SLA definitions.
- **Key attributes:** Name, definition text (for agent guidance), display order.
- **Relationships:** Referenced by `Ticket` and `TicketSlaInstance`; paired 1:1 with `SlaPolicy` reference data (see `SLA-Architecture.md`).
- **Invariants:** Fixed set of four values for MVP — no dynamic priority creation.
- **Ownership:** SLA and Escalation module.
- **Lifecycle:** Static reference data.

## BusinessCalendar

- **Purpose:** Defines the working-day pattern and business-hour window used for non-Critical SLA calculation (ADR-0010).
- **Key attributes:** Working-days mask, business-day start/end time, effective-from date.
- **Relationships:** Consulted by SLA and Escalation's due-date calculation; not directly linked to any single ticket.
- **Invariants:** Exactly one active configuration at a time (ISSUE-017: approved as Saturday–Thursday, Friday off, but stored as data, not code).
- **Ownership:** SLA and Escalation module (data), Administration (edit access).
- **Lifecycle:** Rarely changes; a new effective-dated row is added rather than editing history if the working week ever changes.

## Holiday

- **Purpose:** Individual UAE public holiday dates that pause non-Critical SLA clocks (ISSUE-012).
- **Key attributes:** Date, description, entering employee (System Administrator), confirming employee (Customer Service/HR).
- **Relationships:** Consulted by SLA and Escalation's due-date calculation.
- **Invariants:** Dates are unique; a holiday entered without a confirming employee is a data-entry gap the Administration module should surface, not silently accept as final [ASSUMPTION — exact confirmation workflow to be detailed in Phase 3].
- **Ownership:** SLA and Escalation module (data), Administration (entry/edit access split per ISSUE-012).
- **Lifecycle:** Added annually/as announced; never deleted (historical accuracy for past SLA calculations).

## Notification

- **Purpose:** Records an outbound notification (acknowledgement, breach/warning alert) and its delivery status.
- **Key attributes:** Notification type, recipient, channel (email at MVP), content reference, delivery status, correlation ID, retry count.
- **Relationships:** Associated with a `Ticket` (and, indirectly, an `OutboxMessage`).
- **Invariants:** A notification's idempotency key prevents a duplicate send for the same underlying event.
- **Ownership:** Notifications module.
- **Lifecycle:** Created when a domain event triggers a notification; updated as delivery attempts occur; moves to a failed/dead-letter state after retries are exhausted.

## AuditEntry

- **Purpose:** The generic, cross-cutting audit record for actions outside the ticket-lifecycle model — exports, admin/config changes, access revocation, Genesys webhook signature failures (ADR-0018).
- **Key attributes:** Actor, action, entity type/ID, before/after value, correlation ID, timestamp.
- **Relationships:** May reference any entity type generically (not a strict foreign key to every possible target).
- **Invariants:** Append-only.
- **Ownership:** Audit module.
- **Lifecycle:** Created on every qualifying administrative action; retained per the data-retention policy.

## OutboxMessage

- **Purpose:** The Transactional Outbox record ensuring a state change and its notification/integration effect commit atomically (ADR-0013).
- **Key attributes:** Event type, payload, correlation ID, idempotency key, status (pending/processed/dead-lettered), attempt count, last error.
- **Relationships:** Not directly tied to a single entity type — generic across all modules that raise domain events requiring cross-boundary effects.
- **Invariants:** Idempotency key is unique; a message is never processed twice with a different observable effect.
- **Ownership:** Infrastructure module.
- **Lifecycle:** Created in the same transaction as the triggering state change; read and dispatched by the Outbox dispatcher job; moves to dead-lettered after retries are exhausted, reviewable by System Administrator.

## User and Role References

- **Purpose:** Represents the internal-staff-only identity model (ADR-0004) that every other entity's "actor" fields reference.
- **Key attributes:** `Employee` (extends `AspNetUsers` 1:1): display name, department, Geyness-staff flag, deactivated-at timestamp. Roles: the ten named roles from Solution Analysis §4, managed via ASP.NET Core Identity's role tables.
- **Relationships:** Referenced as "actor" by `TicketAssignment`, `TicketStatusHistory`, `TicketResolution`, `TicketEscalation`, `TicketNote`, `Notification`, `AuditEntry`.
- **Invariants:** No `Customer` role or account type exists (ISSUE-021). A deactivated employee cannot authenticate, and access is revoked within 24 hours of departure (FR-ADM-02).
- **Ownership:** Identity and Access module.
- **Lifecycle:** Created on staff onboarding; deactivated (never hard-deleted, to preserve historical actor references) on departure.
