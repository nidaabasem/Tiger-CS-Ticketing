# Tiger Group — CS Ticketing System
## MVP Design Review Findings (Senior-Architecture-Review Pass)

| | |
|---|---|
| **Status** | Design for review — findings and their resolutions, applied directly to the affected documents. **DR-08 (capacity) is now resolved by an approved management decision — see below.** |
| **Scope** | A senior-.NET-solution-architect review of `MVP-ERD.md`, `MVP-Data-Dictionary.md`, `MVP-API-Contracts.md`, `Genesys-Mock-Contract.md`, and `MVP-Implementation-Backlog.md`, cross-checked against `MVP-UI-Wireframes.md` and `MVP-Traceability-Matrix.md` |
| **Explicitly not done here** | No application code, SQL DDL, EF Core migrations, or scaffolding — every resolution below is a design-document change only |
| **Base** | `main` @ `4fe6f19`, reviewing the design package on `design/mvp-erd-api-ui` as of this pass |
| **Related documents** | Every `docs/design/*.md` document; each finding below names exactly which ones it changed |
| **Date** | 2026-08-18; DR-08 updated following management's approved delivery decision (4-week, 1-developer pilot; Genesys feature-flagged and deferred; mock validation not production-ready; no production deployment authorized) |

---

## How to Read This Document

Each finding lists: **Finding ID**, **Severity**, **Documents changed**, **Resolution**, **Remaining decision or dependency**, **Implementation-blocking (Yes/No)**. "Implementation-blocking" means: if Phase 3 had started from the pre-review documents as written, would a developer have hit a contradiction, a missing definition, or a defect serious enough to force a stop? "Yes" does not mean the finding is unresolved — every "Yes" below has already been resolved in this pass; it describes what the *original* documents would have caused, not the current state.

---

## Findings

### DR-01 — Circular requester-verification dependency

- **Severity:** Critical.
- **Documents changed:** `MVP-ERD.md`, `MVP-Data-Dictionary.md`, `MVP-API-Contracts.md`, `MVP-UI-Wireframes.md`, `MVP-Traceability-Matrix.md`, `MVP-Implementation-Backlog.md`.
- **Resolution:** Introduced `VerificationSessions` (`MVP-ERD.md`/`MVP-Data-Dictionary.md` §2.24) — a short-lived, single-agent-owned, single-use, expiring pre-ticket record of the CRM unit/contact lookup and verbal read-back. Ticket creation (`MVP-API-Contracts.md` §3.1) now takes a `VerificationSessionId` and consumes the session, copying its captured fields into the immutable `TicketRequesterSnapshots` row in the same transaction. The original `POST /api/tickets/{ticketId}/requester-confirmation` endpoint — which needed a `TicketId` that ticket creation itself required the confirmation to produce — no longer exists; it is replaced by `POST /api/verification-sessions`, `PATCH .../selection`, `POST .../confirm`, `GET .../{id}` (§2.4.1–§2.4.4). Expiry (`[ASSUMPTION]` 30 minutes), single-use enforcement, single-agent ownership, full audit trail, and CRM-outage handling (a session is only created on the CRM-available path; the outage path still uses `IntakeRecords`, with a session created later at promotion time) are all specified.
- **Remaining decision or dependency:** The 30-minute expiry window and the no-Supervisor+-override, single-agent-ownership rule are both `[ASSUMPTION]` — plausible but not confirmed against real call-handling patterns (e.g., a call transferred mid-verification). Flagged in `MVP-ERD.md` §4.
- **Implementation-blocking:** Yes (as originally specified — a developer could not have satisfied both endpoints' preconditions in any order; now resolved).

### DR-02 — Missing Genesys agent-mapping entity

- **Severity:** High.
- **Documents changed:** `MVP-ERD.md`, `MVP-Data-Dictionary.md`, `MVP-API-Contracts.md`, `MVP-UI-Wireframes.md`, `MVP-Traceability-Matrix.md`, `MVP-Implementation-Backlog.md`.
- **Resolution:** Added `GenesysAgentMappings` (`MVP-ERD.md`/`MVP-Data-Dictionary.md` §2.25) — one row per employee, holding `GenesysAgentId`/`AgentEmailOrExtension` (at least one required), `IsActive`, and audit fields. Uniqueness is enforced among active rows only, so a deactivated mapping doesn't block reassignment of an identifier (e.g., a reassigned extension). `MVP-API-Contracts.md` §6.6 is now split into §6.6.1 (upsert) and §6.6.2 (deactivate), both backed by the new table. A UI home was added on screen 16 (`MVP-UI-Wireframes.md`) — the original design implied this administration lived on screen 20, but screen 20's own spec never actually described it.
- **Remaining decision or dependency:** None — this is a closed structural gap, not a pending business decision.
- **Implementation-blocking:** Yes (the endpoint existed in the original design with nothing to persist to; now resolved).

### DR-03 — Genesys event/idempotency modeled at the wrong grain

- **Severity:** Critical.
- **Documents changed:** `MVP-ERD.md`, `MVP-Data-Dictionary.md`, `MVP-API-Contracts.md`, `Genesys-Mock-Contract.md`, `MVP-UI-Wireframes.md`, `MVP-Traceability-Matrix.md`, `MVP-Implementation-Backlog.md`.
- **Resolution:** Added `GenesysInteractionEvents` (`MVP-ERD.md`/`MVP-Data-Dictionary.md` §2.26) — one row per received webhook delivery, replacing the original direct `GenesysInteractions → IdempotencyRecords` relationship (which bound an entire, progressively-updated conversation to a single dedup record and would have silently dropped every event after the first). The idempotency key **prefers the provider's own `EventId`** once Genesys confirms it is stable/unique (`Genesys-Integration.md` §15 item 1, open); until then it falls back to `ConversationId + EventType + RawPayloadHash + a short time-bucket` — a composite specifically chosen so two genuinely distinct events of the same type on the same call (e.g., two hold events) are never suppressed as duplicates, only near-identical redeliveries within the same short window. Out-of-order, retry, and duplicate handling are documented in `Genesys-Mock-Contract.md` §4. `MVP-API-Contracts.md` §6.1/§6.2/§6.4/§6.5 and `MVP-UI-Wireframes.md` screens 19/20 all updated to operate at the per-event grain.
- **Remaining decision or dependency:** Whether Genesys's real `EventId` is stable/unique — `Genesys-Integration.md` §15 item 1, open, blocking only the *preference* for the primary key, not the fallback's correctness.
- **Implementation-blocking:** Yes (the original model would have caused silent data loss on the second and every subsequent event of a call; now resolved).

### DR-04 — Signature-failure persistence contradiction

- **Severity:** High.
- **Documents changed:** `MVP-Data-Dictionary.md`, `MVP-API-Contracts.md`, `Genesys-Mock-Contract.md`, `MVP-UI-Wireframes.md`, `MVP-Implementation-Backlog.md`.
- **Resolution:** `Genesys-Mock-Contract.md` §3 already said signature failures are "rejected before persistence"; `MVP-Data-Dictionary.md` §2.11 contradicted this by listing `ProcessingStatus = Rejected (signature failure)` as a storable value. **Resolved in favor of never persisting a rejected request**: the `Rejected` value is removed from the data model entirely; a signature failure produces only a minimal security-log entry (timestamp, source IP, byte-length, outcome) — **never the raw payload**, since an unauthenticated request's claimed fields cannot be trusted. This also generalizes to a broader rule applied throughout DR-03's resolution: `RawPayloadHash`, never the raw payload, is what's persisted for any accepted event too.
- **Remaining decision or dependency:** None — this is a closed contradiction, resolved by picking one behavior and removing the other from every document that stated it.
- **Implementation-blocking:** Yes (an implementer following the data dictionary literally would have built persistence logic that the API contract's own text said should never run; now resolved).

### DR-05 — Priority-downgrade approval self-authorization defect

- **Severity:** Critical (security/authorization defect, not merely a design inconsistency).
- **Documents changed:** `MVP-ERD.md`, `MVP-Data-Dictionary.md`, `MVP-API-Contracts.md`, `MVP-UI-Wireframes.md`, `MVP-Traceability-Matrix.md`, `MVP-Implementation-Backlog.md`.
- **Resolution:** The original single endpoint (`POST .../sla/priority-downgrade`) accepted an `ApprovingEmployeeId` field from the requesting Agent's own payload — nothing but a same-call role check stood between an Agent and naming themselves or a compliant colleague as approver. Replaced with `PriorityDowngradeRequests` (`MVP-ERD.md`/`MVP-Data-Dictionary.md` §2.27) and five endpoints (`MVP-API-Contracts.md` §5.6.1–§5.6.5): an Agent creates a `Pending` request naming no approver at all; a Dept Head+ approves or rejects it from their own inbox, with their identity taken **exclusively** from their own authenticated session on that call — never from any request field, on any endpoint. At-most-one-pending-per-ticket and a 24-hour (`[ASSUMPTION]`) expiry prevent duplicate/stale requests. The breach-preservation invariant (ADR-0012) is unchanged — only *who may approve and how* changed. UI redesigned into two screens (10a Agent-facing request, 10b Dept-Head-facing inbox) so an Agent's form never contains an approver field to remove in the first place.
- **Remaining decision or dependency:** The 24-hour expiry window is `[ASSUMPTION]`, not confirmed.
- **Implementation-blocking:** Yes (a self-authorization defect is a security finding that must block sign-off regardless of schedule pressure; now resolved).

### DR-06 — Attachment deletion contradicts the retention policy

- **Severity:** High.
- **Documents changed:** `MVP-ERD.md`, `MVP-Data-Dictionary.md`, `MVP-API-Contracts.md`, `MVP-UI-Wireframes.md`, `MVP-Traceability-Matrix.md`, `MVP-Implementation-Backlog.md`.
- **Resolution:** `DELETE /api/tickets/{ticketId}/attachments/{attachmentId}` was the one hard-delete exception in an otherwise uniformly append-only/retained schema, directly contradicting the 7-year retention requirement (ISSUE-016) that governs every other historical table. Replaced with `POST .../attachments/{attachmentId}/withdraw` (`MVP-API-Contracts.md` §4.6): the metadata row (`IsWithdrawn`, `WithdrawnAtUtc`, `WithdrawnByEmployeeId`, `WithdrawalReason`) is never deleted; access is revoked (excluded from listing, blocked from download) rather than the record destroyed. A separate `BlobStatus` column (`Stored`/`Quarantined`/`Purged`) tracks the underlying binary content's own lifecycle independently — only the blob, never the metadata row, may ever actually be removed, and only via a separately-approved operator action, not this endpoint.
- **Remaining decision or dependency:** The quarantine-to-purge window length is `[ASSUMPTION]`, not specified. No un-withdraw capability exists at MVP — flagged as an open item if reversal is later needed.
- **Implementation-blocking:** Yes (shipping a hard delete against a 7-year-retention requirement is a compliance defect, not a style choice; now resolved).

### DR-07 — Delete-behavior wording conflated DB FK clauses with application operations

- **Severity:** Medium.
- **Documents changed:** `MVP-ERD.md` only.
- **Resolution:** Reviewed every "Set Null"/"Cascade" relationship in `MVP-ERD.md` §2. Found six cases (§2.2 Employees→Tickets, §2.8 TicketRequesterSnapshots, §2.9 IntakeRecords→Tickets, §2.10 Tickets→GenesysInteractions, §2.18 TicketStatusHistory→TicketNotes, §2.20 BusinessCalendars→BusinessCalendarWorkingDays) that stated a delete behavior for a scenario ("if a Ticket/Employee/BusinessCalendar were ever deleted") that structurally cannot occur in this design — worded in a way that could be misread as a sanctioned path, and in one case (§2.20) self-contradictory (claiming a "Cascade if retired" while the same sentence said retired calendars are "kept, not deleted"). Every such case is rewritten to `N/A — <parent row is never deleted in this design>`. Two genuinely real, supported application operations (§2.4 UserDepartmentAssignments join-row removal; §2.1's Identity-framework cascade) are kept, with explicit language distinguishing "this is what happens when this row is directly the target of a real delete" from "this FK's `ON DELETE` clause is never exercised because the parent is never deleted." A new bullet in §3 (Cross-Cutting Referential-Integrity Notes) states this distinction as a general principle for future additions to this ERD.
- **Remaining decision or dependency:** None — this is a documentation-clarity correction; no behavior changed, since every corrected case was already stated as "never happens in practice" before this pass, just worded ambiguously.
- **Implementation-blocking:** No — nothing here would have caused an implementer to build the wrong thing (the parent rows genuinely are never deleted either way), but the original wording could have led someone to configure a real `ON DELETE` clause for a path that should never exist, or to wonder whether "Cascade" quietly sanctioned a delete this document elsewhere forbids.

### DR-08 — Backlog capacity gap (Architecture/Foundation over capacity even after rebalancing) — **RESOLVED by management decision**

- **Severity:** High (planning risk, not a design defect).
- **Status:** **Resolved.** When first raised, this finding presented three options to the sponsor (accept bounded overtime, extend the timeline, or trim scope) without adopting one. **Management has since made that decision** (recorded in `MVP-Implementation-Backlog.md` §0): a **4-week, 1-developer pilot**, with Genesys deferred entirely and kept behind a feature flag whenever it is later attempted, mock validation never described as production-ready, and no production deployment authorized at this stage.
- **Documents changed:** `MVP-Implementation-Backlog.md` (substantially restructured), this document.
- **Original finding (retained for the record):** Added a workload-summary table (`MVP-Implementation-Backlog.md`, then §0.1 of the pre-decision version) totaling every item's estimated effort per role per week — something the pre-review backlog never actually did despite asserting a 90-hour-per-role capacity. Found: Architecture/Foundation's original 3-week/4-person total was already over capacity before this review (Week 1 and Week 3 each independently exceeded 30h/week for that one role), and this review's own findings added a further ~13h to that role. Two real capacity moves and one resequencing narrowed but could not close the gap without handing SLA/escalation-critical logic to a role that plan deliberately didn't assign it to.
- **Decision applied:** The now-approved 1-developer/4-week plan is a **different, smaller scope**, not a compressed version of the 4-person/3-week plan — `MVP-Implementation-Backlog.md` §2 sequences 17 items (S-01–S-17) totaling 129 ideal hours against a ~120-hour (4-week × 30h) budget, itself a disclosed ~8% overage concentrated in Week 4, presented with the same honesty as the original finding rather than force-fit to zero. §0.2 states plainly what was cut to fit (SLA due-date computation, escalation, priority-downgrade approval, attachment withdrawal, and — per management's explicit instruction — all Genesys integration, mock or real) and why each cut is disclosed as a real capability gap, not hidden. The original 4-person/3-week plan is retained in full as §5, explicitly marked superseded/reference-only, so the detailed design and capacity analysis are not lost if the team scales up later.
- **Remaining decision or dependency:** None on the capacity question itself — it is decided. The scope deferred by that decision (SLA/escalation/Genesys/priority-downgrade/attachment-withdrawal) remains real, undelivered functionality that a future phase must still build once capacity increases; that is a schedule fact, not an open question needing another decision right now.
- **Implementation-blocking:** No — the approved plan is internally consistent (verified: every `S-##` dependency resolves, and the workload table's hours were independently recomputed from the source items and match exactly) and ready to be worked from once implementation is authorized.

### DR-09 — Cross-document consistency (`MVP-UI-Wireframes.md`, `MVP-Traceability-Matrix.md`)

- **Severity:** Medium.
- **Documents changed:** `MVP-UI-Wireframes.md`, `MVP-Traceability-Matrix.md`.
- **Resolution:** Both files existed (neither needed to be reported missing) and both contained specific, real inconsistencies with the corrected design, now fixed: screens 4–6 described the old confirm-by-`TicketId` flow rather than `VerificationSessions` (DR-01); screen 10 described the old `ApprovingEmployeeId` co-sign field rather than the two-actor request/approval flow (DR-05) and was split into 10a/10b; screen 12 described attachment "Delete" rather than "Withdraw" (DR-06); screens 19/20 described a per-conversation `ProcessingStatus` including a `Rejected` (signature-failure) value that no longer exists (DR-03/DR-04); screen 16 gained the `GenesysAgentMappings` administration UI that DR-02 added with no prior screen home. In `MVP-Traceability-Matrix.md`, the Entity/Table columns for FR-VER-03, FR-VER-05, FR-SLA-09, and FR-TKT-06 were updated to name the four newly-added entities, and a new table traces `GenesysAgentMappings`/`GenesysInteractionEvents` to ADR-0019 (since Genesys entities were never FR-tagged in the original Solution Analysis). API-Contracts section numbers (§2.4, §5.6, §6.1, §6.4, §6.6) were deliberately kept stable throughout this whole review specifically to avoid a much larger renumbering cascade into these two files — most existing cross-references needed no change at all as a direct result.
- **Remaining decision or dependency:** None.
- **Implementation-blocking:** No — these were consistency corrections following DR-01 through DR-06, not independently-discovered defects.

---

## Summary Table

| Finding | Severity | Status | Implementation-Blocking (as originally specified) |
|---|---|---|---|
| DR-01 — Circular verification dependency | Critical | Resolved | Yes |
| DR-02 — Missing Genesys agent-mapping entity | High | Resolved | Yes |
| DR-03 — Genesys event/idempotency wrong grain | Critical | Resolved | Yes |
| DR-04 — Signature-failure persistence contradiction | High | Resolved | Yes |
| DR-05 — Priority-downgrade self-authorization defect | Critical | Resolved | Yes |
| DR-06 — Attachment deletion vs. retention | High | Resolved | Yes |
| DR-07 — Delete-behavior wording (DB FK vs. app ops) | Medium | Resolved | No |
| DR-08 — Backlog capacity gap | High (planning) | **Resolved by management decision** (4-week, 1-developer pilot) | No for code; the decision removes the "Yes" that previously applied to committing to an undecided timeline |
| DR-09 — Cross-document consistency | Medium | Resolved | No |

**Every finding above is now resolved.** The "Implementation-Blocking" column describes what the pre-review (or, for DR-08, pre-decision) documents would have caused, not a currently open defect. DR-08 was the one finding this document could not resolve unilaterally, since it required a decision only the sponsor could make; that decision has now been made and is recorded in `MVP-Implementation-Backlog.md` §0.

## What This Document Does Not Cover

No application code, SQL DDL, EF Core migrations, or scaffolding was written or implied to resolve any finding above — every resolution is a design-document correction. This document also does not re-run the full 7-point final verification from the original design package's closing pass; that verification is repeated fresh, against the current state of every document, in the commit/PR description for this review pass.
