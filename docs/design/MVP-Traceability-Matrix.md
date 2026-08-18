# Tiger Group — CS Ticketing System
## MVP Traceability Matrix

| | |
|---|---|
| **Status** | Design for review |
| **Scope** | Maps every MVP-scoped Functional/Business Requirement to its Management Decision/Issue, ADR, Entity/Table, API endpoint, UI screen, and test scenario. Identifies gaps and confirms no Phase 2/3 requirement leaked into MVP artifacts. |
| **Explicitly not done here** | No new requirements, decisions, or design content — this document only cross-references what already exists in prior documents. Any gap found is reported, not silently filled. |
| **Base** | `main` @ `4fe6f19`; cross-references `docs/Tiger-CS-Ticketing-Solution-Analysis.md`, `docs/Tiger-CS-Ticketing-Management-Decisions.md`, `docs/architecture/adr/`, `docs/design/MVP-ERD.md`, `docs/design/MVP-Data-Dictionary.md`, `docs/design/MVP-API-Contracts.md`, `docs/design/MVP-UI-Wireframes.md` |
| **Date** | 2026-08-18 |

**Legend:** Requirement IDs (`FR-xxx-nn`, `BR-nnn`, `ISSUE-nnn`) are as defined in `Tiger-CS-Ticketing-Solution-Analysis.md`. "—" means genuinely not applicable (e.g., a pure business rule with no dedicated UI screen). "**GAP**" means applicable but missing, and is separately listed in §5.

---

## 1. Intake and Verification (FR-CH, FR-VER)

| Requirement | Decision/Issue | ADR | Entity/Table | API Endpoint | UI Screen | Test Scenario |
|---|---|---|---|---|---|---|
| FR-CH-01 (manual phone intake, verify-then-create) | — | ADR-0019 | `IntakeRecords`, `Tickets` | §2.5, §3.1 (`MVP-API-Contracts.md`) | 4, 5, 6 | Create ticket only succeeds after verification + confirmation steps complete |
| FR-VER-01 (unit number is sole lookup key) | — | ADR-0006 | `UnitReferences` | §2.1/§2.2 | 4 | Search UI offers no name/phone-only path |
| FR-VER-02 (confirm CRM match before proceeding) | — | ADR-0006 | `UnitReferences` | §2.1 | 4 | Cannot advance to screen 5 without a selected unit |
| FR-VER-03 (read back name/property/tower/unit type) | — | ADR-0007 | `VerificationSessions` (capture), `TicketRequesterSnapshots` (final immutable copy) — `VerificationSessions` added in the senior-architecture-review pass, Finding DR-01 | §2.4 (§2.4.1–§2.4.4) | 5 | Snapshot fields match what was displayed for read-back |
| FR-VER-04 (identify specific contact, not just unit) | ISSUE-007 | ADR-0006 | `ContactReferences` | §2.3 | 5 | Ticket's `ContactReferenceId` is required, non-null |
| FR-VER-05 (no local mastering; CRM-issued IDs + immutable snapshot) | — | ADR-0006, ADR-0007 | `UnitReferences`, `ContactReferences`, `VerificationSessions`, `TicketRequesterSnapshots` | §2.1–§2.4, §3.1 | 4, 5 | No update path exists for `TicketRequesterSnapshots` after insert (code review check); ticket creation now sources the snapshot from a consumed `VerificationSessions` row, not a fresh cache read, per Finding DR-01 |
| FR-VER-07 (CRM downtime doesn't block Critical/High intake) | ISSUE-006 | ADR-0006 | `IntakeRecords` | §2.5, §2.6 | 4 (outage banner) | Intake Record created and later promoted when CRM returns |

## 2. Ticketing Core (FR-TKT)

| Requirement | Decision/Issue | ADR | Entity/Table | API Endpoint | UI Screen | Test Scenario |
|---|---|---|---|---|---|---|
| FR-TKT-01 (`TG-[DEPT]-[YYYYMMDD]-[SEQ]` ID, server-generated) | — | ADR-0004 | `Tickets.TicketNumber` | §3.1 | 6, 7 | Client-supplied `TicketNumber` is ignored/rejected; format matches on create |
| FR-TKT-02 (unit reference mandatory before `Open`) | — | ADR-0006 | `Tickets.UnitReferenceId` | §3.1 | 6 | Create request without a verified unit reference returns `400`/`422` |
| FR-TKT-03 (creation timestamp non-editable) | — | — | `Tickets.CreatedAtUtc` | §3.1, §3.4 (no field for it) | 7 | No PATCH field exists for `CreatedAtUtc` |
| FR-TKT-04 (channel from fixed enum) | — | — | `IntakeRecords.ChannelId` | §2.5 | 4, 6 | Invalid channel value rejected |
| FR-TKT-05 (agent ID from auth context) | — | ADR-0004 | `TicketAssignments.AssigningActorEmployeeId` | §3.1 (implicit from JWT) | 6 | Creator/actor fields never accept a client-supplied employee ID different from the authenticated user |
| FR-TKT-06 (summary + ≤10 attachments, virus-scanned) | BR-010, ISSUE-INT-09 | ADR-0017 | `Tickets.RequestSummary`, `TicketAttachments` (now with `IsWithdrawn`/`BlobStatus`, per Finding DR-06) | §4.3–§4.6 | 6, 12 | 11th upload rejected `422`; unscanned/rejected/withdrawn file never downloadable; a withdrawn attachment's row is retained (never physically deleted), verified by a regression test per `MVP-Implementation-Backlog.md` W2-06 |
| FR-TKT-07 (every state change audited across 5 dimensions) | — | ADR-0008, ADR-0018 | `TicketStatusHistory`, `AuditEntries` | all mutating endpoints in §3, §5 | 7 (Timeline tab) | Every mutating call produces a matching history/audit row with actor + before/after |
| FR-TKT-08 (five independent state dimensions) | — | ADR-0008 | `Tickets.TicketStatus/VerificationStatus/EscalationLevel/SlaState/ResolutionOutcome` | §3.7, §5.x | 7, 11 | A ticket can be `InProgress` + `Level2` simultaneously |
| FR-TKT-09 (unverified ticket invisible to queues, no SLA start) | ISSUE-002 | ADR-0006, ADR-0008 | `Tickets.VerificationStatus` | §3.2 (filter excludes unverified) | 3 | Unverified ticket excluded from queue results and has no `TicketSlaInstances` row yet |
| FR-TKT-10 (mandatory note before resolution) | BR-011 | — | `TicketResolutions.ResolutionNote` | §3.9 | 13 | Resolve rejected `400` with empty note |
| FR-TKT-11 (`[DEPT]` segment immutable; `CurrentDepartment` separate/mutable) | ISSUE-020 | ADR-0004 | `Tickets.OriginatingDepartmentId` (write-once) vs. `CurrentDepartmentId` (mutable) | §3.6 | 7, 9 | Transfer changes `CurrentDepartmentId`; `TicketNumber`/`OriginatingDepartmentId` unchanged |

## 3. Classification and Routing (FR-CLS, FR-RTE)

| Requirement | Decision/Issue | ADR | Entity/Table | API Endpoint | UI Screen | Test Scenario |
|---|---|---|---|---|---|---|
| FR-CLS-01 (single primary category) | — | — | `Tickets.CategoryId` | §3.1 | 6 | Category is single-select, mandatory |
| FR-CLS-02 (FM sub-category mandatory) | — | — | `Categories.ParentCategoryId` | §3.1 (category picker logic) | 6 | Sub-category required when parent = Facility Management |
| FR-CLS-03 (priority selection, mandatory) | — | ADR-0008 | `Tickets.PriorityId` | §3.1 | 6 | Priority mandatory, single-select |
| FR-RTE-01 (auto-route by category/sub-category, data-driven) | — | — | `Categories.DepartmentId` | §3.1 (derives `OriginatingDepartmentId`) | 6 | Ticket routes to the department the selected category maps to |
| FR-RTE-02 (verbal confirmation + read back ticket number) | — | — | — | 6 (success confirmation) | 6 | UI surfaces routed department + ticket number post-create |
| FR-RTE-03 (named current owner; SLA start per ISSUE-001) | ISSUE-001 | ADR-0009 | `Tickets.CurrentOwnerEmployeeId`, `TicketAssignments` | §3.5 | 8 | Ticket has exactly one current owner once assigned |
| FR-RTE-04 (transfer between departments; ID stays immutable) | ISSUE-010, ISSUE-020 | ADR-0004 | `Tickets.CurrentDepartmentId` | §3.6 | 9 | Same as FR-TKT-11 |
| FR-RTE-05 (reassign within same department) | — | — | `TicketAssignments` | §3.5 | 8 | Reassignment logged, department unchanged |

## 4. SLA and Escalation (FR-SLA, FR-ESC)

| Requirement | Decision/Issue | ADR | Entity/Table | API Endpoint | UI Screen | Test Scenario |
|---|---|---|---|---|---|---|
| FR-SLA-01 (per-priority SLA targets, configuration not code) | — | ADR-0009 | `SlaPolicies` | §5.1 (read) | 11 | Changing a `SlaPolicies` row changes future due-date computation without a deploy |
| FR-SLA-02 (Critical runs 24/7) | — | ADR-0010 | `SlaPolicies.ClockBasis` | §5.1 | 11 | Critical due-date math ignores business calendar |
| FR-SLA-03 (High/Medium/Low run business-hours only) | ISSUE-017 | ADR-0010 | `BusinessCalendars`, `BusinessCalendarWorkingDays`, `Holidays` | §5.10–§5.12 | 18 | Non-Critical due date excludes weekends/holidays |
| FR-SLA-04 (explicit `Due*At`, recalculated on priority change; no server tick broadcast) | — | ADR-0009, ADR-0016 | `TicketSlaInstances.FirstResponseDueAtUtc/ResolutionDueAtUtc` | §5.1, §5.5, §5.6 | 11 | SignalR payload carries a due timestamp, not a countdown |
| FR-SLA-05 (First Response ≠ automated ack) | ISSUE-019 | ADR-0009 | `Tickets.FirstHumanResponseAtUtc` vs. `AcknowledgementSentAtUtc` | §5.2 | 11 | Automated ack alone never sets `FirstHumanResponseAtUtc` |
| FR-SLA-06 (warning before breach) | — | ADR-0009 | `SlaPolicies.WarningThresholdPercent` | §5.1 (`SlaState` derivation) | 2, 11 | `SlaState = Warning` fires at the configured threshold, visually distinct from `Breached` |
| FR-SLA-07 (breach triggers priority-specific alert via Outbox) | — | ADR-0011, ADR-0013 | `Notifications`, `OutboxMessages` | (system-triggered; no client endpoint) | 2 (dashboard reflects it) | Breach produces a `Notifications` row and an `OutboxMessages` entry, retryable |
| FR-SLA-08 (reassignment/transfer doesn't reset SLA clock by default) | — | ADR-0009 | `TicketSlaInstances` (period unaffected by §3.5/§3.6) | §3.5, §3.6 | 8, 9 | Reassign/transfer leaves the current `TicketSlaInstances` period's due dates unchanged |
| FR-SLA-09 (priority-change policy: upgrade=earlier-of, downgrade=approval+breach-preserved) | ISSUE-023 | ADR-0012 | `TicketSlaInstances.ChangeReason/ApprovedByEmployeeId`, `PriorityDowngradeRequests` (added in the senior-architecture-review pass, Finding DR-05 — separates the requesting Agent from the approving Dept Head+, closing a self-authorization defect) | §5.5, §5.6 (§5.6.1–§5.6.5) | 10 (now 10a/10b) | Upgrade due date = min(old, new); downgrade blocked without a separately-authenticated Dept-Head+ approval action; prior breach flag never clears; an explicit test confirms the approver identity is never accepted as a request-body field |
| FR-ESC-01 (Agent manual flag) | — | ADR-0011 | `TicketEscalations` | §5.7 | 11 | Agent can create a `ManualFlag` escalation with a reason |
| FR-ESC-02 (Level 2 auto/flag-triggered, 2h response clock) | — | ADR-0011 | `TicketEscalations.Level/NotifiedRoles` | (system-triggered for auto; §5.7 for flag) | 11 | Level 2 escalation carries its own response-window tracking |
| FR-ESC-03 (Level 3 auto if Level 2 doesn't resolve within window) | ISSUE-013 | ADR-0011 | `TicketEscalations` | (system-triggered, Hangfire job per ADR-0015) | 11 | Level 3 fires automatically after the configured window elapses unresolved |
| FR-ESC-04 (Level 4 manual-only) | — | ADR-0011 | `TicketEscalations.TriggerType = ManualLevel4` | §5.7 | 11 | Only CS Manager/GM can create a `ManualLevel4` row; never system-triggered |
| FR-ESC-05 (full audit trail of escalation changes) | — | ADR-0018 | `TicketEscalations`, `AuditEntries` | §5.7–§5.9 | 11 | Escalation history queryable per ticket |
| FR-ESC-06 (`EscalationLevel` independent of `TicketStatus`) | — | ADR-0008 | `Tickets.EscalationLevel` vs. `TicketStatus` | §3.7 vs. §5.7 | 7, 11 | Ticket can be `InProgress` + `Level2` |
| FR-ESC-07 (time/priority-based Level 2→3 progression, not retry-count) | ISSUE-013 | ADR-0011 | `SlaPolicies.Level2ToGmWindowValue/Unit` | §5.10 (config, not a direct endpoint) | — | Escalation window is configurable per priority tier |

## 5. Notifications (FR-NOT — MVP-scoped items only)

| Requirement | Decision/Issue | ADR | Entity/Table | API Endpoint | UI Screen | Test Scenario |
|---|---|---|---|---|---|---|
| FR-NOT-01 (automated email ack on creation; does not satisfy FR-SLA-05) | — | ADR-0009, ADR-0013 | `Notifications`, `Tickets.AcknowledgementSentAtUtc` | (system-triggered from §3.1's `TicketCreated` event) | 7 (shows `AcknowledgementSentAtUtc`) | Ack email attempted for every ticket; does not set `FirstHumanResponseAtUtc` |
| FR-NOT-02 (SLA breach notifications, email/in-app for MVP) | — | ADR-0011, ADR-0013 | `Notifications` | (system-triggered) | 2 | Breach notification recipients match Section 7's matrix |
| FR-NOT-05 (all sends logged, correlation-tracked, retryable via Outbox) | — | ADR-0013, ADR-0014 | `Notifications.OutboxMessageId`, `OutboxMessages` | §6.4/§6.5 pattern applies analogously (no dedicated notification-retry endpoint enumerated — **GAP**, see §5 below) | 20 (Outbox/failed-events screen, Genesys-scoped by name but same underlying mechanism) | Failed send visible in an operational queue with dead-letter path |

## 6. Resolution (FR-RES)

| Requirement | Decision/Issue | ADR | Entity/Table | API Endpoint | UI Screen | Test Scenario |
|---|---|---|---|---|---|---|
| FR-RES-01 (Dept Employee/Head sets Resolved; does not close) | — | ADR-0008 | `TicketResolutions` | §3.9 | 13 | After resolve, `TicketStatus` is not yet `Closed` |
| FR-RES-02 (only Agent/Supervisor/CS Manager can Close, after notification confirmed) | — | ADR-0008 | `Tickets.TicketStatus` | §3.10 | 14 | Close blocked without a current resolution; role-gated |
| FR-RES-03 (resolution note permanently retained, even after archival) | — | ADR-0018 | `TicketResolutions.ResolutionNote` | §3.9 (read via §3.13) | 7, 14 | Note remains queryable via timeline after retention window changes (no delete path exists) |
| FR-RES-04 (Reopen is a domain event; increments `ReopenCount`; preserves prior outcome) | ISSUE-011 | ADR-0008 | `Tickets.ReopenCount`, `TicketResolutions.IsCurrent` | §3.11 | 15 | Reopen flips `IsCurrent` on the old resolution row rather than deleting it |
| FR-RES-05 (Cancelled/Rejected outcomes, reason code required) | — | ADR-0008 | `TicketResolutions.ReasonCode` | §3.9 | 13 | Cancel/Reject rejected without a reason code |
| FR-RES-06 (Duplicate requires `DuplicateOfTicketId`) | — | ADR-0008 | `Tickets.DuplicateOfTicketId`, `TicketResolutions.DuplicateOfTicketId` | §3.9, §3.12 | 13, 15 | Duplicate outcome rejected without a valid, non-duplicate target ticket |
| FR-RES-07 (`Pending Third-Party` status, distinct from `Pending Customer`) | — | ADR-0008 | `Tickets.TicketStatus`, `TicketSlaPausePeriods.PauseReason` | §3.7, §5.3 | 7, 11 | Pause reason distinguishes `PendingCustomer` from `PendingThirdParty` |

## 7. Administration and Security (FR-ADM)

| Requirement | Decision/Issue | ADR | Entity/Table | API Endpoint | UI Screen | Test Scenario |
|---|---|---|---|---|---|---|
| FR-ADM-01 (RBAC via ASP.NET Core Identity) | — | ADR-0004 | `AspNetUsers/Roles/UserRoles` | all endpoints (role gates per §0 of `MVP-API-Contracts.md`) | 16 | Every permission-matrix cell enforced server-side, not just hidden in UI |
| FR-ADM-02 (access revoked within 24h of departure) | — | ADR-0004 | `Employees.DeactivatedAtUtc` | §1.6 | 16 | Deactivation available and effective immediately; SLA-tracked operationally (outside system scope) |
| FR-ADM-03 (full audit trail: 5 dimensions, notes, escalations, exports, admin actions) | — | ADR-0018, ADR-0021 | `AuditEntries`, `TicketStatusHistory` | all mutating endpoints | 7, 16–20 | Every listed action category produces a queryable audit/history row |
| FR-ADM-04 (Tiger Group data exclusivity; technical boundary, not just contract) | — | ADR-0006 (no local mastering) | — | — (an infrastructure/deployment concern, not a single endpoint) | — | **GAP** — see §8 |
| FR-ADM-05 (full data export within 24h of request) | — | — | — | Not enumerated in `MVP-API-Contracts.md` | — | **GAP** — see §8 |
| FR-ADM-06 (downtime detected/reported within 15 min) | — | `Security-Architecture.md` (health checks) | — | — (ops/monitoring concern, not an API/UI screen) | — | Health-check endpoint exists per `System-Architecture.md`; alerting pipeline is an ops concern outside this API/UI matrix |
| FR-ADM-07 (no customer-facing auth/portal unless ISSUE-021 approved) | ISSUE-021 | ADR-0004 | — (absence is the point) | — | — | No customer-facing login endpoint exists anywhere in `MVP-API-Contracts.md` |

## 8. Cross-Cutting Business Rules (selected BRs not already covered above)

| Requirement | Entity/Table | API Endpoint | UI Screen | Test Scenario |
|---|---|---|---|---|
| BR-009 (email ack mandatory; SMS/WhatsApp Phase 2) | `Notifications.Channel` | (system-triggered) | 7 | `Channel` enum has only `Email` populated at MVP |
| BR-010 (≤10 attachments/ticket) | `TicketAttachments` | §4.3 | 12 | 11th upload rejected |
| BR-011 (mandatory resolution note) | `TicketResolutions.ResolutionNote` | §3.9 | 13 | Same as FR-TKT-10 |

**Entities added by the senior-architecture-review pass, traced to ADR/Finding rather than an FR ID (structural corrections, not new requirements — consistent with how `MVP-ERD.md` §0.1 already frames refinements of this kind):**

| Entity | Traces to | API Endpoint | UI Screen | Test Scenario |
|---|---|---|---|---|
| `GenesysAgentMappings` (Finding DR-02) | ADR-0019 (Genesys Basic Integration), `Genesys-Integration.md` §15 item 4 | §6.6.1/§6.6.2 | 16 | Upserting an already-active identifier for a different employee returns `409` |
| `GenesysInteractionEvents` (Finding DR-03) | ADR-0014 (idempotency), ADR-0019 | §6.1, §6.2, §6.4, §6.5 | 19, 20 | Two distinct events of the same type on one call are both retained, not collapsed (`MVP-Implementation-Backlog.md` W3-05's test requirements) |

---

## 9. Gaps Identified (Reported, Not Silently Filled)

These requirements are MVP-scoped per `Tiger-CS-Ticketing-Solution-Analysis.md` but have **no dedicated endpoint or screen** in the current design package. Each is flagged here rather than invented on the spot:

1. **FR-NOT-05's retry/dead-letter visibility for general notifications** — `MVP-API-Contracts.md` §6.4/§6.5 (failed-events retry) is scoped to Genesys interactions only; there is no explicitly-named `GET /api/notifications/failed` endpoint. **Recommendation:** either extend screen 20/§6.4 to be a general Outbox-failure view (since the underlying `OutboxMessages` table is already shared infrastructure per `MVP-ERD.md` §2.23), or add a dedicated notification-retry endpoint in Phase 3. Not resolved silently here since it changes an API surface.
2. **FR-ADM-04 (technical data-boundary enforcement)** — this is substantially satisfied by ADR-0006's no-local-mastering design and `Security-Architecture.md`'s access-boundary controls, but no single entity/endpoint/screen "proves" it the way other requirements do; it is an emergent property of the whole architecture rather than a feature. Flagged so it isn't mistaken for an oversight.
3. **FR-ADM-05 (full data export within 24h)** — no export endpoint appears in `MVP-API-Contracts.md`. **Recommendation:** add a `POST /api/exports/tickets` (background job, per the requirement's own "background export job for large exports" note) in Phase 3 API contract refinement; not added here because it wasn't part of the six-module list this pass was scoped to, and inventing its shape now would be exactly the kind of silent scope addition this engagement has been avoiding.
4. **FR-ESC-07's escalation-window configuration** has a data column (`SlaPolicies.Level2ToGmWindowValue/Unit`) but no dedicated admin UI screen for editing it — screen 18 (Business Calendar) doesn't cover SLA policy tuning, and no "SLA Policy Administration" screen was in the requested 20. **Recommendation:** flagged for Phase 3; at MVP pilot scale, seeding these values directly (already the approved defaults per `MVP-Data-Dictionary.md` §2.6) without a live-editing UI is an acceptable interim gap, not a blocker.

## 10. Confirmation: No Phase 2/3 Requirement Present in MVP Artifacts

Cross-checked every requirement tagged **Phase 2** or **Phase 3** in `Tiger-CS-Ticketing-Solution-Analysis.md` against `MVP-API-Contracts.md`, `MVP-UI-Wireframes.md`, and `MVP-ERD.md`/`MVP-Data-Dictionary.md`:

| Excluded item | Tag | Confirmed absent from |
|---|---|---|
| FR-CH-03/05/06 (App/Website, WhatsApp auto-route, Social Media DM) | Phase 2/3 | No such channel/endpoint/screen exists |
| FR-CH-04 (Kiosk) | Phase 3 | No kiosk screen/endpoint |
| FR-VER-06 (auto-ticket channel CRM resolution) | Phase 2 | No auto-ticket-creation endpoint exists — creation is always agent-initiated (§3.1) |
| FR-CLS-04 (AI priority auto-suggestion) | AI-assisted/Phase 3 | No suggestion field/endpoint; §3.4/§6.1 of `MVP-API-Contracts.md` explicitly lists AI features as a non-goal |
| FR-NOT-03/04 (CSAT survey, low-CSAT alert) | Phase 2 | No CSAT entity, endpoint, or screen anywhere in the package |
| FR-CSAT-01/02/03 (CSAT survey schema, storage, threshold alert) | Phase 2 | Same as above |
| FR-RPT-01–05 (Daily/Weekly/Monthly/Ad Hoc formatted reports) | Phase 2 | Only the five basic Dashboard endpoints (§7 of `MVP-API-Contracts.md`) exist; no report-generation endpoint |
| FR-KPI-01/02 (full 10-metric KPI dashboard, threshold flagging) | Phase 2 | Dashboard module (§7) is limited to counts/backlog/breaches/escalation counts — not the full KPI set |
| FR-KPI-03 (Repeat Contact Rate) | Phase 3 | Not present |
| FR-KPI-04 (advanced KPI/root-cause analytics) | Phase 3 | Not present |
| FR-AI-01–04 (AI classifier, breach-risk scoring, chatbot, root-cause clustering) | Phase 3 | Explicitly listed as a non-goal in `MVP-API-Contracts.md` §8 and absent from every entity/endpoint/screen |
| Notification SMS/WhatsApp channel | Phase 2 | `Notifications.Channel` is Email-only at MVP (`MVP-Data-Dictionary.md` §2.21) |

**Result: no Phase 2/3 requirement was found embedded in any MVP design artifact.** Every exclusion above is a deliberate absence, not an oversight — each is either explicitly named in a "does not cover"/"non-goals" section of the relevant document, or has no trace in any entity, endpoint, or screen.

---

## 11. What This Document Does Not Cover

This document does not introduce any new requirement, entity, endpoint, or screen — it only cross-references what already exists across the prior six documents. Where a gap is identified (§9), no design decision is made to close it; that remains a Phase 3 (or earlier, if urgent) design task, flagged here so it is not lost.
