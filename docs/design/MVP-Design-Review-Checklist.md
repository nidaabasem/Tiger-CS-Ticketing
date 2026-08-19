# Tiger Group — CS Ticketing System
## MVP Design Review Checklist

| | |
|---|---|
| **Status** | Design for review — use this to verify the design package (`MVP-ERD.md` through `MVP-Implementation-Backlog.md`) before it is treated as a stable basis for Phase 3 implementation |
| **Scope** | 17 named check categories spanning architecture consistency through deployment readiness |
| **Explicitly not done here** | This checklist does not itself implement any fix — findings are recorded and referred back to the relevant document |
| **Base** | `main` @ `4fe6f19` |
| **Related documents** | All `docs/design/*.md` documents, `docs/architecture/*` |
| **Date** | 2026-08-18 |

**How to use this:** each category lists specific, checkable questions. A "✓" answer should be traceable to a specific document/section, not asserted from memory. An open item is recorded, not silently resolved.

---

## 1. Architecture Consistency

- [ ] Every entity in `MVP-ERD.md`/`MVP-Data-Dictionary.md` traces to a conceptual entity in `docs/architecture/Domain-Model.md`, or is explicitly flagged as a new structural refinement (`MVP-ERD.md` §0.1).
- [ ] Every module boundary implied by the API contracts (`MVP-API-Contracts.md`'s 6 sections) matches the 12 logical modules in `docs/architecture/Module-Design.md` — no endpoint reaches across a module boundary `Module-Design.md` prohibits.
- [ ] The five independent ticket-state dimensions (ADR-0008) are never collapsed into a single status anywhere in the API contracts or UI wireframes (checked: `MVP-API-Contracts.md` §3.7 changes `TicketStatus` independently of §5.5–§5.9's other dimensions; `MVP-UI-Wireframes.md` screen 7's header strip shows all relevant dimensions as separate badges).
- [ ] Outbox + idempotency (ADR-0013/0014) is used consistently for every cross-boundary effect named in the API contracts (notifications, Genesys webhook, SLA-triggered escalation) — not implemented ad hoc for some and generalized for others.

## 2. Requirement Traceability

- [ ] Every MVP-tagged requirement in `Tiger-CS-Ticketing-Solution-Analysis.md` appears in `MVP-Traceability-Matrix.md` (spot-checked against the FR-CH/VER/TKT/CLS/RTE/SLA/NOT/ESC/RES/ADM lists pulled directly from that document).
- [ ] Every gap identified in `MVP-Traceability-Matrix.md` §9 has an explicit recommendation, not a silent drop.
- [ ] No Phase 2/3-tagged requirement appears in any MVP artifact (`MVP-Traceability-Matrix.md` §10's cross-check table) — reverify this specifically before Phase 3 kickoff, since new documents added between now and then could reintroduce something.

## 3. Entity Naming Consistency

- [ ] Entity names match exactly across `MVP-ERD.md`, `MVP-Data-Dictionary.md`, `MVP-API-Contracts.md`'s DTO field names, and `MVP-UI-Wireframes.md`'s "Fields/data displayed" lists (e.g., `TicketRequesterSnapshots`, `GenesysInteractions.AgentEmailOrExtension`, `TicketSlaInstances.ChangeReason` all appear identically spelled in every document that references them).
- [ ] No document introduces a synonym for an existing entity/column (e.g., no document calls `CurrentOwnerEmployeeId` "AssignedTo" or similar) — a synonym would silently break traceability even if the underlying concept is correct.

## 4. Normalization

- [ ] `UnitReferences`/`ContactReferences` are cache tables, never joined as if they were master data with update authority (`MVP-ERD.md` §2.7's Restrict-not-Update note).
- [ ] `TicketSlaInstances` correctly separates the mutable "current period" from historical periods via `PeriodEndAtUtc IS NULL`, rather than mutating a single row's due dates in place (`MVP-Data-Dictionary.md` §2.15).
- [ ] No column is duplicated across two tables without an explicit reconciliation note (checked: `AgentEmailOrExtension`'s collapse from the Genesys mock's two separate fields is explicitly reconciled in `Genesys-Mock-Contract.md` §5, not silently duplicated).

## 5. CRM Source-of-Truth Compliance

- [ ] No local Unit or Customer master table exists anywhere in `MVP-ERD.md`/`MVP-Data-Dictionary.md` (verified: `UnitReferences`/`ContactReferences` are explicitly a "cache — NOT master data," §2.7).
- [ ] `TicketRequesterSnapshots` is write-once with no update path in the API contracts (`MVP-API-Contracts.md` §2.4 returns `409` on a second confirmation attempt) — confirms the ADR-0007 rule is enforced at the contract level, not just documented as an intention.
- [ ] No API endpoint allows creating, editing, or deleting a unit/contact record directly — every CRM Verification endpoint (`MVP-API-Contracts.md` §2) is read/lookup/cache-refresh only, consistent with ADR-0006.

## 6. Data Privacy

- [ ] `GenesysInteractions.CallerNumber` is documented as masked in logs (`MVP-Data-Dictionary.md` §2.11, `Genesys-Mock-Contract.md` §4) — verify this masking behavior is also called out at the API layer (`MVP-API-Contracts.md` §6.1 references `Security-Architecture.md` §11 but does not itself restate the masking rule — **minor gap**: cross-reference exists but the masking behavior isn't independently re-stated in the API contract's own validation/error section; low severity since the authoritative rule lives in `Security-Architecture.md` and is correctly cross-referenced).
- [ ] Attachment access (`MVP-API-Contracts.md` §4.5) is individually audited (download logged), not just upload — since attachments may contain sensitive photos/documents per ISSUE-007-adjacent concerns.
- [ ] No customer-facing identity or authentication path exists anywhere in the API contracts or UI wireframes (FR-ADM-07/ISSUE-021) — confirmed absent from both documents' full endpoint/screen lists.

## 7. Authorization

- [ ] Every endpoint in `MVP-API-Contracts.md` states an "Auth" line; none are silently left unstated.
- [ ] Role escalation points are consistent between the API contracts and UI wireframes — e.g., `ManualLevel4` escalation requires CS Manager/GM in both `MVP-API-Contracts.md` §5.7 and `MVP-UI-Wireframes.md` screen 11; priority-downgrade approval requires Dept Head+ in both §5.6 and screen 10.
- [ ] The "last System Administrator cannot be deactivated" rule (`MVP-API-Contracts.md` §1.6) is reflected in the UI (`MVP-UI-Wireframes.md` screen 16) as an explicit, named error state, not a generic failure.

## 8. Concurrency

- [ ] Every ticket-mutating endpoint in `MVP-API-Contracts.md` requires `If-Match`/`RowVersion` per §0's convention — spot-checked across §3.4–§3.12, §5.2–§5.7; all comply.
- [ ] `UserDepartmentAssignments.IsPrimary` and `TicketAssignments.IsCurrent`/`TicketResolutions.IsCurrent`'s "exactly one true row" invariants are documented with a recommended filtered-unique-index backstop (`MVP-Data-Dictionary.md` §2.4, §2.12, §2.14) rather than left as an app-only promise with no DB-level safety net suggested.
- [ ] Optimistic concurrency conflicts return a consistent `409` ProblemDetails shape (`MVP-API-Contracts.md` §0) across every endpoint that uses it — no endpoint invents its own concurrency-error shape.

## 9. SLA Correctness

**SLA due-date calculation and breach detection are core, committed pilot features** (`MVP-Implementation-Backlog.md` §0.2), not deferred — these checks apply directly to the pilot plan (S-09/S-10/S-14), not only the reference plan.

- [ ] The breach-flag immutability rule (`FirstResponseBreached`/`ResolutionBreached` never reset once true) is stated in `MVP-ERD.md` §2.15, enforced in the API contract's design note (`MVP-API-Contracts.md` §5.6), and has a dedicated regression-test line item in **both** the committed pilot plan (`MVP-Implementation-Backlog.md` S-10, S-25) **and** the reference plan (W3-03) — traced consistently, not asserted in only one.
- [ ] Priority-downgrade is **hard-disabled** for the pilot (S-14), not merely deferred to an unbuilt approval workflow — verified that no path (API, UI, or otherwise) in the committed plan can produce a `PriorityId` decrease after creation, consistent with "does not remove ADR-0012's protection" rather than silently bypassing it.
- [ ] Priority upgrade (earlier-of-due-dates, ADR-0012) is identical across `MVP-API-Contracts.md` §5.5, `MVP-UI-Wireframes.md` screen 10a, `MVP-Implementation-Backlog.md` S-14, and `MVP-Traceability-Matrix.md`'s FR-SLA-09 row. **SLA pause/resume is the one deferred piece of this area** (`TicketSlaPausePeriods`, §0.2/§2.6) — the Critical-never-pauses rule from the full design is therefore moot for this pilot (nothing pauses at all yet), not silently violated.

## 10. Genesys Idempotency

**Genesys Basic Integration is a core, committed pilot feature, built against the confirmed Tiger/Genesys contract** (`MVP-Implementation-Backlog.md` §0/§3/S-17) — not the provisional mock, and not deferred.

- [ ] The webhook dedup key uses the confirmed contract's own stable event identifier directly (`MVP-Implementation-Backlog.md` S-17's note on why this costs less than the reference plan's fallback-key design, Finding DR-03) — the composite fallback key `Genesys-Mock-Contract.md` §4 describes remains documented for any *other* integration whose contract is still unconfirmed, but is not what S-17 actually builds against, since Genesys's contract is now confirmed.
- [ ] Out-of-order and duplicate-event handling are both explicitly documented (`Genesys-Mock-Contract.md` §4, for the underlying design principle) and have a dedicated test requirement in the committed pilot plan (`MVP-Implementation-Backlog.md` S-17, S-25).
- [ ] The feature flag controlling Genesys is confirmed to default off at first deploy and is an explicit, recorded item in the Week 4 deployment checklist (`MVP-Implementation-Backlog.md` §2's Week 4 reserve) — not an assumption about what state it's left in.
- [ ] The Genesys failed-events retry endpoint/UI is explicitly listed as a stretch item outside the pilot commitment (`MVP-Implementation-Backlog.md` §2.6) — failures are visible via logs/audit for this pilot, a real and disclosed limitation, not a silent gap.

## 11. Auditability

- [ ] Every mutating endpoint category in `MVP-API-Contracts.md` lists its `AuditEntries`/domain-event effects — spot-checked, none silently omit this.
- [ ] `TicketStatusHistory` and `AuditEntries` are confirmed append-only with no update/delete endpoint anywhere in `MVP-API-Contracts.md` (neither table has a PATCH/PUT/DELETE route defined for it).
- [ ] The Timeline endpoint (`MVP-API-Contracts.md` §3.13) and screen (`MVP-UI-Wireframes.md` screen 7) correctly source from all five audit-relevant tables (`TicketStatusHistory`, `TicketAssignments`, `TicketNotes`, `TicketEscalations`, `TicketResolutions`), not a subset.

## 12. Outbox Reliability

- [ ] Every domain event listed across `MVP-API-Contracts.md`'s sections that has a downstream effect (notification, SLA recalculation, Genesys correlation) is explicitly tied to an `OutboxMessages.EventType`, not left as a vague "triggers X."
- [ ] The retry/dead-letter policy (attempts threshold, `MVP-API-Contracts.md` §6.1) is the same mechanism referenced for both Genesys events and general notifications (`MVP-Traceability-Matrix.md` §9 item 1's gap note acknowledges the notification-specific retry *endpoint* is unnamed, but the underlying Outbox mechanism itself is shared and consistent).

## 13. Performance / Indexes

- [ ] Every "exactly one current row" invariant (`TicketAssignments.IsCurrent`, `TicketResolutions.IsCurrent`, `TicketSlaInstances.PeriodEndAtUtc IS NULL`, `UserDepartmentAssignments.IsPrimary`) has an explicit filtered-unique-index recommendation in `MVP-Data-Dictionary.md`, which also serves as the natural query-optimization index for "get the current X" lookups the API contracts perform constantly (`MVP-API-Contracts.md` §3.3, §5.1).
- [ ] `TicketNumber`, `CrmUnitId`, `CrmContactId`, `ConversationId`, and `IdempotencyKey` are all documented as unique (`MVP-Data-Dictionary.md`), which doubles as their natural lookup index — no endpoint that looks up by these fields (`MVP-API-Contracts.md` §2.1, §6.2, etc.) would require a full scan.
- [ ] **Open item, not silently resolved:** no explicit index list beyond the uniqueness constraints above and the filtered-unique-index recommendations was produced in this design pass (e.g., a covering index for the Ticket Queue's common filter/sort combination, `MVP-API-Contracts.md` §3.2). Flagged for Phase 3 schema implementation (`MVP-Implementation-Backlog.md` W1-03) to address with real query-plan data, not guessed here.

## 14. Attachment Security

- [ ] Size cap (25MB, `[ASSUMPTION]`) and content-type allow-list are both enforced client-side (`MVP-UI-Wireframes.md` screen 12) and server-side (`MVP-API-Contracts.md` §4.3) — consistent, not client-only.
- [ ] An attachment with `VirusScanStatus ≠ Clean` is unreachable via both the metadata list (flagged `Downloadable: false`, §4.4) and the direct content endpoint (`403`, §4.5) — the rule is enforced at every read path, not just the obvious one.
- [ ] Attachment deletion policy (uploader window + Supervisor+ override, `MVP-API-Contracts.md` §4.6) is explicitly flagged `[ASSUMPTION]` in that document since the requirement text didn't define the policy — carried through consistently to `MVP-UI-Wireframes.md` screen 12's confirmation dialog, not silently hardened or loosened between the two documents.

## 15. Four-Week, 1-Developer Scope Feasibility

**Updated a second time following management's clarified delivery decision** (`MVP-Implementation-Backlog.md` §0). The first correction over-deferred scope (it dropped SLA, escalation, and all Genesys work) that management did not intend to cut; this checklist entry now verifies the corrected plan, which keeps SLA due-date calculation, basic escalation, and Genesys Basic Integration (against the confirmed contract) core and committed.

- [x] The corrected plan's sequence (`MVP-Implementation-Backlog.md` §2, items S-01–S-25 plus a named 12h reserve) fits within the 120-hour cap with 1 hour of headroom (119h total: 107h feature work + 12h reserve) — verified by independently recomputing every item's hours from the source text and confirming they match the stated workload table exactly (30h/30h/29h/30h across the four weeks, no week exceeding 30h).
- [x] The 10–12 hour integration/regression/UAT/deployment reserve is explicitly named and broken down (§2's Week 4 section: ~5h regression, ~4h UAT, ~3h deployment) — not hidden as unestimated contingency.
- [x] §0.2 names, feature by feature, exactly what stayed core (matching management's must-remain list item-for-item: auth, CRM verification, intake/creation, classification/routing, assignment/transfer, core lifecycle, SLA due-date calc + breach detection, basic escalation, Genesys Basic Integration, email ack, audit trail, queue/detail UI, critical-rule tests, UAT/deployment) and what was trimmed *within* each feature (not removed) to fit — pause/resume, the timed Level 2→3 auto-advance, the Genesys failed-events retry UI, attachment withdrawal, and live admin screens are each named against management's own "may be deferred" list, not invented independently.
- [x] Priority downgrade is **hard-disabled** for the pilot (S-14, `MVP-Implementation-Backlog.md` §0), not merely a deferred, half-built workflow — verified that S-14's acceptance criteria and test requirements describe a rejection producing no partial state (no `PriorityDowngradeRequests` row exists at all in the pilot schema), consistent with "does not contradict ADR-0012" rather than removing its protection.
- [x] The Genesys feature flag is confirmed to be an operational-safety control, not a scope-exclusion mechanism (§3 of that document) — the integration is built against the confirmed contract in this pilot, and the flag's on/off state at deploy time is an explicit, recorded checklist item in the Week 4 reserve, not an assumption.
- [x] Optional stretch items (`MVP-Implementation-Backlog.md` §2.6) are listed with rough hour estimates, explicitly outside the 119h commitment — so nothing is silently included in the committed total, and nothing management asked to defer is silently promised either.

## 16. Testing Readiness

- [ ] Every backend backlog item (`MVP-Implementation-Backlog.md` Weeks 1–3) names a specific test requirement, not a generic "add tests" placeholder.
- [ ] The highest-consequence invariants (breach-flag immutability, snapshot write-once, Critical-never-pauses, no-duplicate-chains) each have a named regression test in the backlog, not just a design-document mention.
- [ ] `MVP-Traceability-Matrix.md`'s "Test Scenario" column gives QA a starting point per requirement — confirmed populated for every MVP-tagged requirement row, not left blank for any of them.

## 17. Deployment Readiness

- [x] The hosting-target open question (ADR-0022) is explicitly re-flagged in the approved plan's Week 4 reserve (`MVP-Implementation-Backlog.md` §2's Week 4 deployment line, and its reference-plan counterpart W3-11) rather than assumed resolved by the time deployment work starts.
- [x] A post-deploy smoke test is a named requirement for the deployment reserve item; a full rollback path is not yet specified for the reduced 1-developer scope — flagged as an open item to define before deployment is actually executed, not assumed away.
- [x] The Genesys feature flag's on/off state at deploy time is an explicit, named checklist item within the Week 4 deployment reserve (`MVP-Implementation-Backlog.md` §2's Week 4 section) — not left to whatever the last build happened to default to.
- [x] The Pilot-Done vs. Production-Ready distinction (`MVP-Implementation-Backlog.md` §4) is explicit enough that go-live sign-off cannot be mistaken for a production-readiness sign-off, and now additionally states — per management's explicit decision — that **no production deployment is authorized at this stage**; only a non-production pilot deployment is in scope. This checklist itself only certifies the design package, not a go-live or production-deployment decision, either of which remains separate and, for production, currently unauthorized.

---

## Summary of Open Items Found During This Review

These are carried forward, not resolved by this checklist:

1. §6 (Data Privacy) — the caller-number masking rule is correctly cross-referenced but not independently restated in the API contract document. Low severity; no action required beyond awareness.
2. §13 (Performance/Indexes) — no query-plan-driven index list beyond uniqueness/filtered-unique-index recommendations exists yet. Deferred to Phase 3 schema implementation by design (real query patterns aren't known until then).
3. All items already listed in `MVP-Traceability-Matrix.md` §9 (notification-retry endpoint naming, FR-ADM-04/05's export/boundary gaps, SLA-policy-tuning admin UI) remain open and are not re-litigated here — this checklist confirms they were reported, not that they were fixed.

## What This Document Does Not Cover

This checklist does not perform the review itself in an automated sense — it is the list a human (or a future review pass) works through against the actual documents. It does not certify code correctness (no code exists yet) or production readiness (see §17's explicit distinction).
