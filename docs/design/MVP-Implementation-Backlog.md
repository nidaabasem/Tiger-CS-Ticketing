# Tiger Group — CS Ticketing System
## MVP Implementation Backlog

| | |
|---|---|
| **Status** | Design for review — planning artifact only. **Corrected following management's clarified delivery decision** (see §0) — the prior revision's scope reduction did not match what management actually approved and has been fixed. |
| **Scope** | The approved plan is a **4-week, 1-developer, AI-assisted-development functional pilot** (§2), committed to **≤120 ideal hours**, built from `MVP-ERD.md`, `MVP-Data-Dictionary.md`, `MVP-API-Contracts.md`, the confirmed Tiger/Genesys integration contract, and `MVP-UI-Wireframes.md`. **SLA due-date calculation, basic escalation, and Genesys Basic Integration are core, committed pilot features — not deferred.** The original 4-person/3-week plan is retained as a reference appendix (§5) for a future team scale-up, not as an active plan. |
| **Explicitly not done here** | No application code, no project scaffolding, no actual sprint/task-tracker tickets created in any external tool — this is the plan those would be created from. **No production deployment is authorized at this stage — see §4.** |
| **Base** | `main` @ `4fe6f19` |
| **Related documents** | All preceding `docs/design/*.md` documents, including `MVP-UI-Wireframes.md` and `MVP-Traceability-Matrix.md`; `docs/architecture/System-Architecture.md`, `Module-Design.md`, `SLA-Architecture.md` (§7's pilot-scope note on priority downgrade), `docs/architecture/adr/0012-priority-change-sla-policy.md` (pilot-scope note); `MVP-Design-Review-Findings.md` (Finding DR-08, resolved by the decision recorded in §0) |
| **Date** | 2026-08-18; revised in the senior-architecture-review pass; revised again to record management's first delivery decision; **corrected in this revision to match management's clarified intended pilot scope** |

---

## 0. Team-Capacity Decision (Governs Everything Below) — **Approved, Corrected to Match Management's Intent**

**Management has approved the following delivery decision, resolving Finding DR-08 (`MVP-Design-Review-Findings.md`). This revision corrects the prior draft, which over-deferred scope management did not intend to cut:**

1. **The delivery plan is four weeks with one developer using AI-assisted development.**
2. **Genesys Basic Integration is ready and remains in the pilot** — built against the confirmed Tiger/Genesys integration contract (not the provisional `Genesys-Mock-Contract.md`), and kept behind a feature flag **for operational safety, not because it is out of scope**. See §3.
3. **SLA due-date calculation and basic escalation are core requirements and remain in the pilot** — they are central to the approved ticketing workflow and are not removable for capacity reasons.
4. **Priority-downgrade protection is not removed.** For this pilot, **priority downgrades are disabled completely after ticket creation** — this safely defers the approval workflow (Finding DR-05) without permitting an unauthorized downgrade and without contradicting ADR-0012. Priority **upgrades** remain in scope, since they can be implemented safely (an upgrade can only tighten a deadline, per ADR-0012, and needs no approval gate). The exact restriction statement, used consistently across every affected document:

   > **"Priority is fixed after ticket creation during the pilot. Downgrades are not permitted. The approved downgrade-request and approval design remains documented for the post-pilot phase."**

- **Mock validation must never be described as production-ready**, in any status update, pilot readout, or go-live communication — see §4. (This principle stands even though Genesys itself is no longer mock-only for this pilot — it still governs how any remaining unconfirmed detail is described, and how future mock-contract work on other integrations must be described.)
- **No production deployment is authorized at this stage** — see §4. Only a pilot deployment to a non-production environment is in scope.

**Capacity rules governing §2 below, all satisfied and verified in §0.1:**
- The committed plan does not exceed **120 ideal hours**.
- No week exceeds **30 ideal hours**.
- At least **10–12 hours** inside the 120-hour total are reserved for integration, regression testing, UAT fixes, and pilot deployment — named and estimated, not hidden as unestimated work.
- Anything not fitting in the 120-hour commitment is listed as an explicitly-labeled **optional stretch item** (§2.6), outside the commitment, not silently dropped and not silently included.

This is a decision, recorded here as the source of truth for every hour estimate and scope choice below. The original 4-person/3-week plan (§5) is **superseded as the active plan** and retained only as a reference for a future team scale-up; nothing in §5 should be treated as current.

### 0.1 Workload Summary — Hours per Week (1 Developer, 4 Weeks, AI-Assisted)

A single developer at ~30 ideal-hours/week gives a **120-hour budget** across 4 weeks. The committed plan in §2 totals exactly **119 ideal hours** — **107h of feature work plus a 12-hour reserve** for integration/regression/UAT/deployment — fitting within the 120-hour cap with 1 hour of headroom, and with no week exceeding 30 hours:

| Week | Feature hours | Reserve | Total | vs. 30h/week budget |
|---|---|---|---|---|
| Week 1 | 30h | — | 30h | On budget |
| Week 2 | 30h | — | 30h | On budget |
| Week 3 | 29h | — | 29h | −1h (within budget) |
| Week 4 | 18h | 12h | 30h | On budget |
| **Total** | **107h** | **12h** | **119h** | **−1h (within the 120h cap)** |

**Why this is different from the prior draft:** the prior revision fit within capacity by deferring SLA, escalation, and Genesys entirely — which management has now clarified is not the intended cut. This revision keeps those three core, and instead trims **depth and richness within each feature**, plus leans on AI-assisted development to reduce the mechanical/boilerplate share of the estimate (schema generation from an already-fully-specified data dictionary, CRUD scaffolding, DTO mapping) more than the correctness-critical share (SLA due-date math, breach-flag immutability, idempotency correctness, escalation trigger timing) — those keep estimates close to what careful implementation actually requires, AI-assisted or not. §0.2 states exactly what depth was trimmed per feature; §2.6 lists what didn't fit at all, explicitly as stretch, not silently dropped.

### 0.2 What's Committed, What's Trimmed Within Each Feature, and Why

**Committed (core, built in this pilot) — matches management's must-remain list exactly:**

| Feature | What ships | What's trimmed within it (not removed, just less deep) |
|---|---|---|
| Authentication and authorization | Login, logout, current-user, role/department checks on every endpoint | Lockout-policy edge cases get simpler handling than a full multi-attempt-window implementation |
| CRM unit/customer verification | Unit/contact lookup and confirmation, immutable snapshot capture | Single-step combined lookup-and-confirm instead of the full 4-endpoint session flow (§5's reference design) — still session-based, so Finding DR-01's circular-dependency fix is not reintroduced |
| Intake and ticket creation | Full ticket creation from a confirmed verification session | — |
| Classification, priority, and department routing | Category-to-department routing, priority assignment at creation | — |
| Assignment and transfer | Department-membership-checked assignment, transfer preserving the immutable `OriginatingDepartmentId` | — |
| Core ticket lifecycle | Status changes, resolve, close, notes, attachments (upload/list/download) | Attachment withdrawal (Finding DR-06) not built — upload-only, per management's "may be deferred" list; duplicate-flag recommend/confirm sub-flow not built — resolve-as-duplicate directly still works |
| **SLA due-date calculation and breach detection** | Business-calendar-aware due dates (Critical 24/7, others business-hours), breach-flag detection, immutable once set | **Pause/resume (`TicketSlaPausePeriods`) not built** — a paused-equivalent status exists via `TicketStatus`, but the SLA clock does not stop for it in this pilot; this is a real, disclosed limitation, not a silent one |
| **Basic automatic/manual escalation** | Manual escalation flag, Level 4 manual-only (role-gated), automatic Level 2 trigger on breach | The timed Level 2→3 auto-advance (a scheduled-job feature) is not built — Level 2 triggers automatically on breach, but does not automatically advance to Level 3 on a timer; GM/Level-3 escalation in this pilot is manual only |
| **Genesys Basic Integration** | Webhook ingestion against the **confirmed** contract, per-event idempotency (preferring the provider's confirmed stable `EventId` directly, since the contract is now confirmed — no fallback-key complexity needed), agent mapping, First-Human-Response satisfaction, manual call-to-ticket linking — **shipped behind a feature flag for operational safety** | The failed-events retry endpoint/UI is not built for pilot — failures are visible in logs/audit, manual re-processing is a direct-to-database operator action if it's ever needed, not a UI feature |
| Priority changes | **Upgrade only** — earlier-of-due-dates, reuses the SLA calculation engine's own logic | **Downgrade is completely disabled after ticket creation** — see the restriction statement in §0 |
| Email acknowledgement | Automated ack on ticket creation via the Outbox | — |
| Audit trail | Append-only `AuditEntries` for every mutating action, on every feature above | — |
| Ticket queue and ticket-details UI | Queue list, ticket detail, timeline, SLA/escalation panel | Queue filter/sort is basic (fewer simultaneous filter fields than the full design); no dashboard beyond the queue itself |
| Automated tests for critical business rules | A dedicated regression pass covering breach-flag immutability, verification-session single-use, Genesys event idempotency, escalation-trigger timing, priority-upgrade correctness, and priority-downgrade hard-block | In addition to (not instead of) the per-item contract tests each feature item already carries |
| UAT and pilot deployment | Full regression pass, UAT-fix window, non-production pilot deployment | Part of the explicit 12-hour reserve (§0.1) — see S-25/S-26 in §2 |

**May be deferred or simplified (per management's explicit list) — not committed, and where attempted at all, listed as stretch in §2.6, not counted in the 119h total:**
- SLA pause/resume
- Priority downgrade of any kind (hard-disabled, not merely deferred — see §0's restriction statement)
- The priority-downgrade request/approval UI and workflow (the full design remains documented in `MVP-API-Contracts.md` §5.6.1–§5.6.5 and `MVP-ERD.md`/`MVP-Data-Dictionary.md` §2.27 for the post-pilot phase — **not deleted**, per explicit instruction)
- Attachment withdrawal (upload-only is committed; withdrawal is stretch)
- Advanced administration screens (departments/categories/business-calendar live-editing UI) — seeded configuration is committed instead
- Advanced dashboards and reports
- Non-core UI polish
- Any integration other than CRM, Genesys, and email

---

## 1. Backlog Item Fields

Every item below carries: **Backlog ID**, user story/task, business value, acceptance criteria, dependencies, estimated effort (ideal dev-hours), Risk (High/Medium/Low), test requirements, Definition of Done.

Since there is exactly one developer, there is no "can run in parallel" field and no "workstream" field in this section — every item is sequential on one person's calendar, and the **critical path is the sequence itself** (§2.5). (The reference plan in §5 retains parallel-workstream fields, since they were meaningful there.)

---

## 2. Approved 4-Week, 1-Developer Pilot Plan (Corrected)

Items are sequential; "Week N" markers show where each item falls by cumulative hours. Each item names the reference-plan item(s) it's adapted from, so nothing here is invented independent of the detailed design work already done. **AI-assisted development is assumed throughout** — estimates reflect that boilerplate-heavy items (scaffolding, schema, CRUD, DTO mapping, UI layout) benefit more from AI assistance than correctness-critical items (SLA math, breach immutability, idempotency, escalation timing), which keep estimates close to what careful implementation requires regardless of tooling.

### Week 1 (target ≤30h; actual 30h)

**S-01 — Solution scaffolding** *(adapted from W1-01)*
- **Story/value:** the physical solution structure (Domain/Application/Infrastructure/Api/Web/Tests) in place before feature work starts.
- **Acceptance criteria:** solution builds; module boundaries match `Module-Design.md`'s dependency rules; a placeholder health-check endpoint responds.
- **Dependencies:** none. **Estimated effort:** 2h (AI-assisted scaffolding from `Module-Design.md`'s already-specified boundaries). **Risk:** Low.
- **Test requirements:** a build-verification check that fails on a prohibited project reference.
- **Definition of Done:** solution builds clean.

**S-02 — Minimal CI** *(adapted from W1-08, reduced)*
- **Story/value:** build + unit tests run automatically on every push.
- **Acceptance criteria:** CI runs build + unit tests on push; a containerized integration-test harness is deferred as unnecessary overhead at this scale.
- **Dependencies:** S-01. **Estimated effort:** 2h. **Risk:** Low.
- **Test requirements:** N/A (this item builds the test infrastructure).
- **Definition of Done:** a deliberately-broken test fails the pipeline.

**S-03 — Authentication and authorization** *(adapted from W1-02)*
- **Story/value:** login and role/department-based access control (FR-ADM-01).
- **Acceptance criteria:** `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/users/me` per `MVP-API-Contracts.md` §1.1–1.3; role checks enforced on every endpoint as it's built.
- **Dependencies:** S-01. **Estimated effort:** 4h (lockout-policy edge cases simplified, per §0.2). **Risk:** Medium.
- **Test requirements:** login success/failure unit tests; a smoke test on a protected endpoint with/without a token.
- **Definition of Done:** a seeded test user logs in and receives a JWT with correct roles.

**S-04 — Database schema (25 of 27 entity groups)** *(adapted from W1-03, narrowed only by the two features fully out of scope)*
- **Story/value:** the schema for every committed feature — **including SLA, escalation, and Genesys entities**, since those are core in this corrected plan.
- **Acceptance criteria:** implements every group in `MVP-Data-Dictionary.md` §2.1–2.27 **except** `TicketSlaPausePeriods` (§0.2 — pause/resume not built) and `PriorityDowngradeRequests` (§0 — downgrades hard-disabled) — 25 of 27 groups, including `SlaPolicies`, `TicketSlaInstances`, `TicketEscalations`, `GenesysInteractions`, `GenesysInteractionEvents`, `GenesysAgentMappings`, `IdempotencyRecords`, `BusinessCalendars`/`BusinessCalendarWorkingDays`/`Holidays`.
- **Dependencies:** S-01. **Estimated effort:** 9h (AI-assisted migration generation from the already-complete Data Dictionary specification — mechanical work, trimmed the most). **Risk:** Medium (schema mistakes are still expensive to fix later, even solo, even AI-assisted).
- **Test requirements:** a schema-verification check that every FK in scope is present.
- **Definition of Done:** migrations apply cleanly; seed data for `Priorities`/`SlaPolicies`/`Departments`/default `BusinessCalendars` loads.

**S-05 — Audit trail, Outbox, and idempotency foundation** *(adapted from W1-04)*
- **Story/value:** every mutating action produces an audit record (FR-TKT-07, FR-ADM-03 — never descoped, per the immutable/append-only invariant carried through every revision of this plan); a generalized idempotency mechanism, since **Genesys webhook dedup needs it in this corrected plan**, unlike the prior (incorrect) draft.
- **Acceptance criteria:** an audit-writing mechanism used by every later feature; an Outbox dispatch loop; `IdempotencyRecords` used by both the email-ack path and the Genesys webhook path (S-17).
- **Dependencies:** S-04. **Estimated effort:** 6h. **Risk:** High — still the highest-leverage item in this plan; getting this wrong is expensive to retrofit, and it's now load-bearing for Genesys too.
- **Test requirements:** a round-trip test — write an Outbox message, confirm dispatch; a duplicate `IdempotencyKey` short-circuits correctly.
- **Definition of Done:** a sample event flows end-to-end through audit + Outbox + idempotency.

**S-06 — CRM gateway interface and test double** *(adapted from W1-05)*
- **Story/value:** an `ICrmGateway` abstraction with a fake implementation, so verification can be built and tested before real CRM access exists.
- **Acceptance criteria:** covers unit lookup, unit search, contact lookup; the test double simulates an outage for fallback-path testing.
- **Dependencies:** S-01. **Estimated effort:** 3h. **Risk:** Low.
- **Test requirements:** happy-path and simulated-outage unit tests.
- **Definition of Done:** the test double is wired into DI; a real implementation can be swapped in later.

**S-07 — Verification flow, simplified single-step** *(adapted from W2-01, reduced — Finding DR-01's lesson preserved)*
- **Story/value:** verify a unit/contact and capture the immutable read-back snapshot, without reintroducing the circular dependency Finding DR-01 fixed.
- **Acceptance criteria:** one endpoint combines what the full design (§5, `MVP-API-Contracts.md` §2.4.1–§2.4.4) splits into four — lookup, select, and confirm happen in a single call against a short-lived, single-use `VerificationSessions` row, consumed at ticket creation (S-08).
- **Dependencies:** S-04, S-06. **Estimated effort:** 4h. **Risk:** Medium (the single-use/write-once rules still need a real test, even simplified).
- **Test requirements:** a second consumption attempt on the same session returns `409`.
- **Definition of Done:** the single verification endpoint passes its contract test.

**Week 1 total: 2+2+4+9+6+3+4 = 30h.**

### Week 2 (target ≤30h; actual 30h)

**S-08 — Ticket creation, with SLA instance opened** *(adapted from W2-03, corrected — the prior draft incorrectly skipped opening an SLA instance)*
- **Story/value:** create a ticket from a confirmed verification session, **and open its first `TicketSlaInstances` row with a computed due date** — SLA tracking starts at creation in this corrected plan, since SLA is core (FR-TKT-01–06, FR-CLS-01–03, FR-RTE-01, FR-SLA-01).
- **Acceptance criteria:** correct `TicketNumber` format; routes to department from category; consumes the session and writes `TicketRequesterSnapshots` in the same transaction; seeds `TicketStatusHistory`; opens the initial `TicketSlaInstances` row (due dates computed once S-09 exists — sequenced so S-08 and S-09 are built together in practice, even though listed separately for traceability to the reference plan).
- **Dependencies:** S-07. **Estimated effort:** 4h. **Risk:** Medium.
- **Test requirements:** idempotency-key replay test (no duplicate ticket); category-to-department routing test.
- **Definition of Done:** contract test passes, including idempotency and SLA-instance creation.

**S-09 — SLA calculation engine (business-calendar-aware due dates)** *(adapted from W3-01 — corrected from "not built" to core, per management's decision)*
- **Story/value:** the due-date math behind `MVP-API-Contracts.md` §5.1 (FR-SLA-01–04) — **committed, not deferred.**
- **Acceptance criteria:** Critical due dates ignore the business calendar (24/7); other tiers correctly exclude non-working hours/days/holidays, using seeded `BusinessCalendars`/`Holidays` data (live holiday-administration UI is stretch, per §0.2 — the calendar itself is not).
- **Dependencies:** S-04, S-08. **Estimated effort:** 9h — kept close to the reference plan's own 14h estimate proportionally, since this is explicitly one of the correctness-critical items AI assistance trims least (flagged High risk in every prior revision of this plan; business-calendar-with-holidays math is the single most error-prone calculation in the whole system). **Risk:** High.
- **Test requirements:** worked-example tests matching `SLA-Architecture.md`'s examples, including a holiday-spanning case.
- **Definition of Done:** due-date computation matches `SLA-Architecture.md` §8's worked examples.

**S-10 — SLA breach detection** *(adapted from W3-01/W3-02's breach-detection half, without pause/resume)*
- **Story/value:** mark `FirstResponseBreached`/`ResolutionBreached` immutably once a due date passes — the other core half of FR-SLA-01–04's requirement, without the pause/resume mechanism (§0.2 — deferred).
- **Acceptance criteria:** a scheduled check (reuses the Outbox/scheduled-job pattern from S-05) marks breach flags; once `true`, never reset by any code path, including a later priority upgrade (S-14).
- **Dependencies:** S-09. **Estimated effort:** 4h. **Risk:** Medium-High (the breach-flag-immutability rule is the highest-consequence invariant in this schema, per `MVP-ERD.md` §2.15 — kept correctness-first despite the tight budget).
- **Test requirements:** an explicit "breach flag stays true" regression test (also re-verified in S-25).
- **Definition of Done:** breach detection fires at the correct due-date boundary and never un-sets a flag.

**S-11 — Basic escalation (manual + automatic Level 2-on-breach)** *(adapted from W3-04, reduced — the timed Level 2→3 auto-advance is stretch, per §0.2)*
- **Story/value:** FR-ESC-01, FR-ESC-04, and the core of FR-ESC-02 — manual flag, Level 4 manual-only (role-gated), and an automatic Level 2 trigger the moment a breach is detected (S-10).
- **Acceptance criteria:** `ManualFlag` and `ManualLevel4` role-gated correctly; a breach automatically raises a Level 2 `TicketEscalations` row. **The timed window-based Level 2→3 auto-advance (a separate scheduled job) is not built in this pilot** — Level 3/GM escalation is manual-only here, listed as stretch in §2.6.
- **Dependencies:** S-10. **Estimated effort:** 4h. **Risk:** Medium.
- **Test requirements:** a test that breach detection (S-10) produces exactly one Level 2 escalation, not a repeating one on every subsequent check.
- **Definition of Done:** manual and auto-Level-2 escalation paths pass their tests.

**S-12 — Ticket read/list/detail/timeline** *(adapted from W2-04)*
- **Story/value:** queue and detail visibility (FR-TKT, FR-ADM-03's auditability via timeline) — now including SLA state and escalation level, since both exist in this corrected plan.
- **Acceptance criteria:** list filters/sorts; detail returns the full shape including `SlaState`/`EscalationLevel`; timeline merges status history, assignments, notes, resolutions, and escalations in order.
- **Dependencies:** S-08. **Estimated effort:** 4h. **Risk:** Low.
- **Test requirements:** timeline ordering test across mixed event types, including escalation entries.
- **Definition of Done:** endpoints pass contract tests.

**S-13 — Assignment, transfer, status change** *(adapted from W2-05)*
- **Story/value:** ownership and status management (FR-RTE-03–05, FR-TKT-11).
- **Acceptance criteria:** assignment enforces department membership; transfer preserves the immutable `OriginatingDepartmentId`; status-change enforces the transition table.
- **Dependencies:** S-08. **Estimated effort:** 5h. **Risk:** Medium.
- **Test requirements:** invalid-transition rejection test; transfer-preserves-immutable-ID test.
- **Definition of Done:** endpoints pass contract tests.

**Week 2 total: 4+9+4+4+4+5 = 30h.**

### Week 3 (target ≤30h; actual 29h)

**S-14 — Priority upgrade only; downgrade hard-blocked** *(adapted from W3-03's upgrade half; downgrade replaced entirely per §0)*
- **Story/value:** priority upgrade (ADR-0012's earlier-of-due-dates rule, FR-SLA-09's upgrade half) — the only priority-change path in this pilot.
- **Acceptance criteria:** `PATCH /api/tickets/{ticketId}` (or the dedicated upgrade endpoint, `MVP-API-Contracts.md` §5.5) computes the new due date as the earlier of the existing and freshly-computed due date; **any attempt to submit a lower-urgency `PriorityId` after creation is rejected outright with a fixed error** (`type: .../priority-downgrade-disabled-in-pilot`) — no request is created, no approval path exists, nothing is left `Pending`. This is a hard block, not a deferred workflow.
- **Dependencies:** S-09, S-13. **Estimated effort:** 3h. **Risk:** Low (the logic reuses S-09's due-date computation; the hard-block path is simple to implement and simple to verify).
- **Test requirements:** an upgrade test (earlier-of-due-dates correctness); an explicit test that a downgrade attempt is rejected and produces no `TicketSlaInstances` row, no `PriorityDowngradeRequests` row (none exists), and no partial state of any kind.
- **Definition of Done:** upgrade passes its contract test; downgrade is proven to be a no-op rejection, not a partially-built workflow.

**S-15 — Notes and attachments, upload only** *(adapted from W2-06, reduced per §0.2)*
- **Story/value:** internal notes and file attachments (FR-TKT-06, BR-010).
- **Acceptance criteria:** note creation; attachment upload with size/type validation and virus-scan status; download blocked while not `Clean`. **No withdrawal** (Finding DR-06 — stretch, §2.6).
- **Dependencies:** S-08. **Estimated effort:** 4h. **Risk:** Medium.
- **Test requirements:** 11th-attachment rejection; download-while-pending rejection.
- **Definition of Done:** endpoints pass contract tests.

**S-16 — Resolve/close workflow** *(adapted from W2-07, reduced)*
- **Story/value:** the resolution lifecycle (FR-RES-01–06, FR-TKT-10).
- **Acceptance criteria:** resolve requires a non-empty note and conditional fields per outcome; close blocked without a current resolution. Duplicate-flag recommend/confirm sub-flow deferred (stretch, §2.6) — resolve-as-duplicate directly (§3.9) still works.
- **Dependencies:** S-08. **Estimated effort:** 4h. **Risk:** Medium.
- **Test requirements:** empty-note rejection; close-without-resolution `409`.
- **Definition of Done:** endpoints pass contract tests.

**S-17 — Genesys Basic Integration** *(adapted from W1-06+W3-05, corrected from "not built at all" to core, per management's decision — built against the confirmed contract, not the provisional mock)*
- **Story/value:** the MVP's confirmed Genesys scope (ADR-0019) — **committed for this pilot.** Kept behind a feature flag, defaulted off at first deploy, **for operational safety** (a controlled rollout, not a scope exclusion) — see §3.
- **Acceptance criteria:** webhook ingestion against the confirmed Tiger/Genesys contract; per-event idempotency using the contract's confirmed stable event identifier directly as the dedup key (`IdempotencyRecords`, via S-05) — the fallback composite-key logic designed for an *unconfirmed* contract (Finding DR-03) is not needed here, since the contract is now confirmed, which is exactly why this item costs less than the reference plan's combined 37h mock-then-real estimate; agent-to-employee mapping (`GenesysAgentMappings`); First-Human-Response satisfaction on call-answer; manual call-to-ticket linking. **The failed-events retry endpoint/UI is not built** (stretch, §2.6) — failures are visible in logs and the audit trail, not a dedicated operator screen, for this pilot.
- **Dependencies:** S-04, S-05, S-06 (agent-mapping reuses the same DI/gateway patterns), S-10 (First-Human-Response ties into breach/SLA state). **Estimated effort:** 10h. **Risk:** Medium — real contract, real signature validation, still new integration code; kept a meaningful budget because correctness here (idempotency, signature validation) is not an area to compress.
- **Test requirements:** duplicate-event idempotency test (using the real contract's event identifier); missing-optional-field acceptance test; signature-failure rejection test (rejected before persistence, per Finding DR-04 — unchanged by the mock-to-real switch); a feature-flag-off smoke test proving the webhook endpoint safely no-ops (or queues without side effects) while the flag is off.
- **Definition of Done:** the integration passes its contract tests behind the flag; the flag itself, and the specific conditions for turning it on, are documented in §3, not assumed.

**S-18 — Basic email acknowledgement** *(adapted from W3-06)*
- **Story/value:** the automated acknowledgement email on ticket creation (FR-NOT-01).
- **Acceptance criteria:** email attempted via the Outbox (S-05) for every ticket; content matches the required fields; does not set `FirstHumanResponseAtUtc` (that field is now live and meaningful in this corrected plan, per S-17 — the ack path must still never touch it).
- **Dependencies:** S-05, S-08. **Estimated effort:** 2h. **Risk:** Low.
- **Test requirements:** a test asserting the ack path never touches `FirstHumanResponseAtUtc`.
- **Definition of Done:** ack email fires and retries via Outbox on transient failure.

**S-19 — UI: login and shell** *(adapted from W1-07, reduced)*
- **Story/value:** screen 1 and the authenticated shell.
- **Acceptance criteria:** login and shell render; route guards redirect unauthenticated users.
- **Dependencies:** S-03. **Estimated effort:** 2h. **Risk:** Low.
- **Test requirements:** an end-to-end smoke test.
- **Definition of Done:** a user can log in, see the shell, and log out.

**S-20 — UI: verification and ticket-creation flow** *(adapted from W2-08, reduced)*
- **Story/value:** the single-screen verification-and-create flow (S-07), plus the create-ticket form (screen 6).
- **Acceptance criteria:** an agent can look up and confirm a unit/contact in one screen and create a ticket, with priority upgrade available post-creation from the detail screen (S-23) but no downgrade path anywhere in the UI.
- **Dependencies:** S-07, S-08, S-19. **Estimated effort:** 4h. **Risk:** Medium.
- **Test requirements:** a scripted walkthrough of the full create-ticket happy path.
- **Definition of Done:** an agent can verify and create a ticket entirely through the UI.

**Week 3 total: 3+4+4+10+2+2+4 = 29h.**

### Week 4 (target ≤30h; actual 30h — 18h feature work + 12h reserve)

**S-21 — UI: ticket detail and timeline** *(adapted from W2-08's detail half)*
- **Story/value:** the ticket detail screen (screen 7), now showing SLA due dates, `SlaState`, escalation level, and Genesys linkage where present.
- **Acceptance criteria:** detail view matches `MVP-UI-Wireframes.md` §7's spec, extended with the SLA/escalation/Genesys fields this corrected plan actually builds.
- **Dependencies:** S-12, S-20. **Estimated effort:** 3h. **Risk:** Low.
- **Test requirements:** a scripted walkthrough of viewing a ticket's full detail including its SLA panel.
- **Definition of Done:** detail screen functional against real data.

**S-22 — UI: ticket queue** *(adapted from W2-10, reduced)*
- **Story/value:** the queue list (screen 3), including `SlaState` and `EscalationLevel` badges.
- **Acceptance criteria:** a basic filterable/sortable list — fewer simultaneous filter fields than the full design (§0.2), but includes SLA/escalation visibility since those are core here.
- **Dependencies:** S-12. **Estimated effort:** 2h. **Risk:** Low.
- **Test requirements:** filter/sort manual test pass.
- **Definition of Done:** queue renders against real data, including SLA/escalation badges.

**S-23 — UI: assignment/transfer, notes/attachments, resolve/close, priority upgrade** *(adapted from W2-09, reduced)*
- **Story/value:** completes the agent-facing ticket lifecycle UI.
- **Acceptance criteria:** assign/transfer/status-change actions; notes/attachments (upload/list/download, no withdrawal per S-15); resolve/close; a priority-upgrade action on the detail screen. **No downgrade action anywhere in the UI** — not a disabled button, simply absent, since the capability doesn't exist server-side either.
- **Dependencies:** S-13, S-14, S-15, S-16, S-21. **Estimated effort:** 6h. **Risk:** Medium.
- **Test requirements:** scripted walkthrough of assign → note → attach → resolve → close, and separately, upgrade.
- **Definition of Done:** all screens functional against real backend endpoints.

**S-24 — UI: SLA and escalation panel** *(adapted from W3-07, reduced — no downgrade UI at all, per §0)*
- **Story/value:** makes the SLA/escalation engine usable, not just API-correct (screen 11).
- **Acceptance criteria:** shows due dates, `SlaState` badge, breach status, escalation history, and a manual-escalate action. **Explicitly shows "Priority is fixed after ticket creation during the pilot. Downgrades are not permitted." as visible text wherever a priority-change action would otherwise appear** — not a tooltip-only disclosure, given its compliance relevance (mirroring how this plan has always treated compliance-relevant notices).
- **Dependencies:** S-10, S-11, S-14, S-21. **Estimated effort:** 3h. **Risk:** Medium.
- **Test requirements:** scripted walkthrough of viewing SLA/escalation state and performing a manual escalation and a priority upgrade.
- **Definition of Done:** screen functional against real backend, including the pilot-restriction notice.

**S-25 — Automated tests for critical business rules** *(consolidated regression, beyond each item's own per-item tests)*
- **Story/value:** explicit, dedicated coverage of the rules whose failure would be a correctness or compliance defect, not just a missing feature — the "automated tests for critical business rules" line item management named as a must-remain category in its own right, not merely implied by other items' test requirements.
- **Acceptance criteria:** dedicated regression tests for: breach-flag immutability (S-10) surviving a priority upgrade (S-14); verification-session single-use (S-07); Genesys webhook idempotency using the real contract's event identifier (S-17); automatic Level 2 escalation firing exactly once per breach (S-11); priority-downgrade hard-block producing no partial state (S-14).
- **Dependencies:** S-09 through S-17. **Estimated effort:** 4h. **Risk:** Medium.
- **Test requirements:** N/A (this item is the test requirement).
- **Definition of Done:** all five regression tests above exist, pass, and are named clearly enough that a future contributor understands which invariant each one protects.

**Week 4 feature total: 3+2+6+3+4 = 18h.**

**Reserve — Integration, regression testing, UAT fixes, and pilot deployment: 12h**, explicitly named and estimated, not hidden as unestimated contingency:

- **Integration/regression pass (≈5h):** exercise the full lifecycle end-to-end — verification → create → SLA due-date/breach → escalation → Genesys linking (flag on, in a controlled test) → assign/transfer → notes/attachments → resolve/close → priority upgrade → downgrade-rejected — as one continuous run, spot-checked against `MVP-Traceability-Matrix.md`'s in-scope rows.
- **UAT support and fixes (≈4h):** a defect triage list; High-severity defects fixed within this window; Medium/Low triaged to post-pilot if the window is exhausted.
- **Pilot deployment, non-production (≈3h):** repeatable deployment; **this is explicitly not a production deployment — no production deployment is authorized at this stage** (§0); the Genesys feature flag's off/on state at deploy time is an explicit, recorded deployment-checklist item, not an afterthought; a post-deploy smoke test (login, create ticket, verify SLA due date appears, view detail).

**Week 4 total: 18h feature + 12h reserve = 30h.**

**Pilot go-live gate:** the full committed lifecycle above (§0.2's "Committed" table) works end-to-end, with no open High-severity defect, in a non-production environment, with the Genesys feature flag's state explicitly decided (on or off) rather than left as whatever the last deploy happened to leave it.

### 2.5 Recalculated Critical Path

Since there is exactly one developer, the critical path **is** the sequence — every item blocks the next unless explicitly parallel-safe, and there is no second person to run a parallel branch on. The one real branch point: **S-17 (Genesys) does not depend on S-14/S-15/S-16** (priority upgrade, notes/attachments, resolve/close) and could be resequenced earlier or later within Week 3 without changing the total — it is placed after S-16 above only because it shares Week 3 with the other backend items, not because of a hard dependency beyond S-04/S-05/S-06/S-10. If Week 3 slips, **S-17 is the item most defensible to shift into the Week 4 reserve's slack first**, since it has the most independent acceptance criteria of any single item.

The unbroken dependency chain determining minimum duration: **S-01 → S-04 → S-05 → S-07 → S-08 → S-09 → S-10 → S-14 (upgrade logic depends on S-09) → S-25 (regression depends on everything) → Reserve → deploy.** S-11 (escalation), S-17 (Genesys), and the UI items (S-19–S-24) branch off this chain but must all complete before S-25's regression pass, since that pass covers the whole committed feature set.

### 2.6 Optional Stretch Items — Outside the Committed 120-Hour Plan

**These are not committed. They are not counted in the 119h total above. They are built only if time remains after S-01–S-25 and the 12h reserve are genuinely complete and verified — never by quietly extending the reserve or compressing a committed item's test requirements to make room:**

| Stretch item | If attempted, rough added effort | Why it's stretch, not committed |
|---|---|---|
| SLA pause/resume (`TicketSlaPausePeriods`) | ~5h | Management's explicit "may be deferred" list |
| Timed Level 2→3 auto-escalation advance | ~4h | "Basic" escalation was the committed bar; the scheduled-job timing logic is the richest, least-essential part |
| Genesys failed-events retry endpoint/UI | ~3h | Logs/audit suffice for a pilot's failure volume |
| Attachment withdrawal (Finding DR-06) | ~4h | Management's explicit "keep upload only if capacity permits" |
| Advanced administration screens (live dept/category/calendar editing) | ~8h | Seeded configuration is the committed bar |
| Advanced dashboards and reports | ~6h | Not in the must-remain list |
| Duplicate-flag recommend/confirm sub-flow | ~2h | Resolve-as-duplicate directly already covers the capability |
| Non-core UI polish (deeper accessibility pass, animation, richer empty/loading states beyond the baseline each committed screen already includes) | Not estimated by design — explicitly excluded from any hour commitment | Management's explicit "non-core UI polish" deferral |

---

## 3. Genesys Policy for This Pilot — Feature-Flagged for Operational Safety, Not Deferred

**Corrected in this revision.** The prior draft deferred Genesys entirely, reasoning that it should not be built against an unconfirmed mock contract. **Management has clarified that the Tiger/Genesys integration contract is now confirmed, and Genesys Basic Integration is ready and must remain in the pilot** (§0, decision 2). The feature flag is **not** a scope-exclusion mechanism here — it is an **operational-safety control**, the same purpose a flag serves on any newly-built integration regardless of confidence in its contract: it lets the pilot go live with Genesys code shipped but not yet live-traffic-exposed, and lets it be turned on deliberately once the team has verified the integration against the real environment, rather than the moment the code merges.

1. **Genesys Basic Integration is built in this pilot (S-17), against the confirmed contract** — not the provisional `Genesys-Mock-Contract.md`, which remains relevant only for any future integration whose contract is not yet confirmed, not for this one.
2. **It ships behind a feature flag, defaulted off at first deploy.** Turning it on is a deliberate, recorded operational decision (part of the Week 4 reserve's deployment checklist, §2's Week 4 section) — not an automatic consequence of the code existing.
3. **This is different from the prior revision's "BLOCKED until Genesys confirms sandbox/schema/authentication" framing**, which correctly described the situation *before* the contract was confirmed. That framing is now retired for Genesys specifically — the open questions it referenced (`Genesys-Integration.md` §15 items 1, 2, 3, 8) are resolved by the confirmed contract this pilot builds against. **This does not retroactively validate the provisional `Genesys-Mock-Contract.md`'s guesses** — S-17 is built directly from the real contract, not from the mock, and `Genesys-Mock-Contract.md` remains labeled provisional for any other integration that might still need a placeholder.
4. **Mock validation must never be described as production-ready**, whenever it does apply to some other, still-unconfirmed integration in the future — this principle is unchanged by Genesys's status here; it just no longer describes Genesys.

---

## 4. Pilot-Done vs. Production-Ready — An Explicit Distinction

**Pilot-Done** (the bar this 4-week, 1-developer plan is built to clear) means:
- Every requirement in §0.2's "Committed" table works for the internal pilot's actual usage pattern, including SLA due-date calculation/breach detection, basic escalation, and Genesys Basic Integration behind its feature flag.
- No known High-severity defect is open.
- The system has been smoke-tested post-deployment, **in a non-production environment**.
- **Priority is fixed after ticket creation during the pilot. Downgrades are not permitted. The approved downgrade-request and approval design remains documented for the post-pilot phase** (`MVP-API-Contracts.md` §5.6.1–§5.6.5, `MVP-ERD.md`/`MVP-Data-Dictionary.md` §2.27 — retained in full, not deleted).
- The Genesys feature flag's state at go-live is an explicit, recorded decision, not an assumption.

**Production-Ready** (explicitly **not** this plan's target, and **not claimed at pilot go-live, and no production deployment is authorized at this stage** — management's explicit decision, §0) would additionally require, at minimum:
- SLA pause/resume, the timed escalation auto-advance, the priority-downgrade request/approval workflow, and attachment withdrawal — everything in §0.2's "may be deferred" list — actually built, per the full design in `MVP-ERD.md`/`MVP-API-Contracts.md`.
- The Genesys feature flag turned on under real production traffic conditions, with the failed-events retry capability (§2.6) built, not just logs/audit.
- Confirmed retention/regulatory policy (ISSUE-016) rather than the interim 7-year default.
- Load/performance testing beyond pilot-scale usage.
- Full security review sign-off per `Security-Architecture.md` §14's testing section.
- A confirmed hosting target (ADR-0022) with production-grade infrastructure — and, separately from any technical readiness, **explicit authorization to deploy to production, which does not exist at this stage** and is a decision outside this document's authority to grant.
- Team capacity beyond 1 developer, sufficient to build everything in §2.6's stretch list and named in §5's reference plan.

This plan deliberately targets the first bar, not the second, and must not be read as a production launch plan under any circumstance.

---

## 5. Reference Plan — Full 4-Person / 3-Week Scope (Superseded; Retained for a Future Team Scale-Up)

**This section is historical/reference only. It is not the currently approved plan (§2 is).** It is retained because the detailed design and capacity analysis in it remain valid and directly reusable the moment team capacity increases beyond 1 developer — none of the entities, endpoints, or findings it references have changed; only *who builds what, on what timeline* has. Every effort estimate below predates management's approved decision in §0 and should not be read as current guidance.

### 5.1 Reference Team-Capacity Model (Superseded)

This reference plan assumed a **team of 4 people** for 3 weeks, each holding one workstream:

- **1 Backend/Architecture developer** — Foundation, Domain, SLA engine, Outbox/idempotency.
- **1 Backend/Integration developer** — CRM gateway, Genesys adapter, notifications.
- **1 Frontend developer** — all 20 UI screens.
- **1 QA/DevOps generalist** — environment setup, CI, test authoring/execution, deployment.

A 3-week pilot at ~30 productive hours/person/week gave each role roughly **90 ideal hours**.

### 5.2 Reference Workload Summary (Superseded — Found Over Capacity Even Before This Decision)

The senior-architecture-review pass found this reference plan's own Architecture/Foundation role already over capacity before the team-size decision in §0 was made — this finding (DR-08) is what led to management's decision, not a contradiction of it:

| Role | Week 1 | Week 2 | Week 3 | **3-week total** | **vs. 90h capacity** |
|---|---|---|---|---|---|
| Architecture/Foundation | 47h | 34h | 54h | **135h** | **+45h (+50%)** |
| Integration | 25h | 33h | 20h | **78h** | −12h (slack) |
| Frontend | 27h | 44h | 39h | **110h** | +20h (+22%) |
| QA/DevOps | 8h | 26h | 30h (+ W3-10 contingency) | **64h** | −26h (slack) |

*(The full accounting of what added hours to this reference plan during the review pass, and which two items were rebalanced to QA/DevOps plus which 15 hours of frontend scaffolding were front-loaded into Week 1, is preserved below in §5.5–§5.7 exactly as originally written, since it remains accurate reference material.)*

### 5.3 Reference Backlog Item Fields

Every item below carries: **Backlog ID**, user story/task, business value, acceptance criteria, dependencies, estimated effort (ideal dev-hours), Risk (High/Medium/Low), test requirements, Definition of Done, can-run-in-parallel (Yes/No), assigned workstream.

### 5.4 Week 1 — Project Foundation (Reference)

#### W1-01 — Solution scaffolding and module boundaries
- **Story:** As the team, we need the physical solution structure (Domain/Application/Infrastructure/Integrations/Reporting/Api/Web/Tests) in place before any feature work starts.
- **Business value:** Everything else depends on this; prevents structural rework mid-pilot.
- **Acceptance criteria:** solution builds; project references match `Module-Design.md`'s dependency rules (no prohibited dependency direction); a placeholder health-check endpoint responds.
- **Dependencies:** none.
- **Estimated effort:** 6h.
- **Risk:** Low.
- **Test requirements:** a build-verification test/CI step that fails if a prohibited project reference is introduced.
- **Definition of Done:** solution builds clean in CI; module boundaries documented match `Module-Design.md`.
- **Can run in parallel:** No (blocks everything).
- **Workstream:** Architecture/Foundation.

#### W1-02 — Authentication and authorization (Identity)
- **Story:** As any staff member, I need to log in and have my role/department determine what I can do.
- **Business value:** Implements FR-ADM-01; gates every other feature's access control.
- **Acceptance criteria:** `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/users/me` implemented per `MVP-API-Contracts.md` §1.1–1.3; role-based policy checks scaffolded (even if most policies are stubs until their owning feature lands).
- **Dependencies:** W1-01.
- **Estimated effort:** 10h.
- **Risk:** Medium (lockout/session-timeout behavior needs care).
- **Test requirements:** login success/failure/lockout unit tests; a smoke test hitting a protected endpoint with/without a token.
- **Definition of Done:** a seeded test user can log in and receive a JWT with correct roles/departments; unauthenticated requests to protected routes return `401`.
- **Can run in parallel:** No (most other backend work needs auth to test against).
- **Workstream:** Architecture/Foundation.

#### W1-03 — Database schema implementation (all 27 entity groups)
- **Story:** As the team, we need the schema from `MVP-ERD.md`/`MVP-Data-Dictionary.md` realized in the database.
- **Business value:** Every feature needs its tables to exist.
- **Acceptance criteria:** all entities in `MVP-Data-Dictionary.md` §2.1–2.27 exist with correct types/nullability; relationships/cardinalities from `MVP-ERD.md` §2 enforced where DB-enforceable (FKs), documented where app-enforced (filtered unique indexes, etc.).
- **Dependencies:** W1-01.
- **Estimated effort:** 17h (+3h vs. the pre-review estimate — covers `VerificationSessions`, `GenesysAgentMappings`, `GenesysInteractionEvents`, `PriorityDowngradeRequests`, §2.24–2.27, added by the senior-architecture-review pass).
- **Risk:** Medium (this is the largest single mechanical task; errors here are expensive to fix later).
- **Test requirements:** a schema-verification test that every FK/relationship from the ERD's relationship tables is actually present.
- **Definition of Done:** migrations apply cleanly to a fresh database; seed data for `Priorities`/`SlaPolicies`/`Departments`/default `BusinessCalendars` loads without error.
- **Can run in parallel:** Partially — with W1-02 (different developer), not with W1-01.
- **Workstream:** Architecture/Foundation.

#### W1-04 — Audit and Outbox/Idempotency foundations
- **Story:** As the system, every mutating action must produce an audit trail and every cross-boundary effect must be idempotent and retryable.
- **Business value:** Implements FR-TKT-07, FR-ADM-03, FR-NOT-05; foundational per ADR-0013/0014/0018.
- **Acceptance criteria:** a reusable audit-writing mechanism (interceptor/middleware) and a reusable Outbox-dispatch background job exist and are exercised by at least one round-trip test (write an Outbox message, confirm it dispatches and marks `Processed`).
- **Dependencies:** W1-03.
- **Estimated effort:** 14h (+2h vs. the pre-review estimate — extends the idempotency pattern to the event-level dedup used by `GenesysInteractionEvents` and to the generic expiry-sweep pattern reused by `VerificationSessions`/`PriorityDowngradeRequests`).
- **Risk:** High — getting this wrong is expensive to retrofit across every later feature; this is explicitly the highest-leverage item in Week 1.
- **Test requirements:** integration test proving a failed dispatch retries and eventually dead-letters per policy; a duplicate `IdempotencyKey` is rejected/short-circuited correctly.
- **Definition of Done:** a sample domain event flows end-to-end through Outbox dispatch with idempotency protection, observably in logs/test assertions.
- **Can run in parallel:** No (everything from Week 2 onward depends on this being solid).
- **Workstream:** Architecture/Foundation.

#### W1-05 — CRM gateway interface and test double
- **Story:** As a developer, I need an `ICrmGateway` abstraction and a working fake/mock implementation so CRM-dependent features can be built and tested before real CRM connectivity exists.
- **Business value:** Unblocks CRM Verification feature work (Week 2) without waiting on real CRM access; supports ADR-0006's swappable-gateway design.
- **Acceptance criteria:** `ICrmGateway` interface covers unit lookup, unit search, contact lookup per `MVP-API-Contracts.md` §2.1–2.3; a test-double implementation returns deterministic fixture data and can simulate a timeout/unavailable response for fallback-path testing.
- **Dependencies:** W1-01.
- **Estimated effort:** 8h.
- **Risk:** Low.
- **Test requirements:** unit tests for both the happy path and the simulated-outage path of the test double.
- **Definition of Done:** the test double is wired into the DI container for local/pilot use; a real implementation can be swapped in later without touching calling code.
- **Can run in parallel:** Yes, with W1-02/W1-03 (different developer — Integration workstream).
- **Workstream:** Integration.

#### W1-06 — Genesys webhook foundation and mock contract wiring
- **Story:** As a developer, I need the webhook receiving endpoint and signature-validation scaffold in place, built against `Genesys-Mock-Contract.md`'s placeholder shape.
- **Business value:** De-risks Week 3's Genesys integration work by getting the plumbing (endpoint, signature placeholder, idempotency dedup, Outbox write) built and tested against the mock now, so Week 3 is substitution, not first-time construction.
- **Acceptance criteria:** `POST /api/genesys/webhook` accepts the mock payload shape from `Genesys-Mock-Contract.md` §1, validates a placeholder signature header (rejecting failures before any persistence, per Finding DR-04 — no signature-failure row is ever written), writes a `GenesysInteractionEvents` row per accepted event (not a single row per conversation, per Finding DR-03) with its `RawPayloadHash`/fallback-key computation, and creates/updates the parent `GenesysInteractions` row on an apply-if-absent basis. **Must ship behind a feature flag, defaulted off — see §3.**
- **Dependencies:** W1-03, W1-04.
- **Estimated effort:** 17h (+7h vs. the pre-review estimate — this item absorbs most of the Finding DR-03/DR-04 rework: per-event ingestion instead of per-conversation, fallback-key/hash computation, and the signature-rejection-is-security-log-only behavior).
- **Risk:** Medium — explicitly built against a mock, so **must be flagged for re-verification once real Genesys details arrive** (this is not a hidden risk; it's the entire premise of `Genesys-Mock-Contract.md`). **This item, and its Week 3 continuation (W3-05), remain BLOCKED from ever being claimed as production-integration-tested until Genesys supplies sandbox access, a confirmed payload schema, and a confirmed signature scheme — see §3.**
- **Test requirements:** duplicate-event idempotency test; missing-optional-field acceptance test; signature-failure rejection test.
- **Definition of Done:** posting a mock event twice results in exactly one `GenesysInteractions` row; posting an event missing `agentEmail`/`agentExtension` succeeds.
- **Can run in parallel:** Yes, with W1-05 (same Integration developer, sequenced after it) or independently if a second integration resource exists.
- **Workstream:** Integration.

#### W1-07 — Basic UI shell and routing, plus front-loaded screen scaffolding
- **Story:** As a staff member, I need a login screen and an authenticated shell (nav, current-user display, route guards) to exist before any feature screen can be built into it.
- **Business value:** Every UI screen (2–20) depends on this shell existing.
- **Acceptance criteria:** screen 1 (Login) and the authenticated shell around screens 2+ exist; route guards redirect unauthenticated users to Login; role-based route guarding scaffold exists (even if most routes aren't built yet).
- **Dependencies:** W1-02 (needs a working login endpoint to integrate against).
- **Estimated effort:** 27h (+15h vs. the pre-review estimate). **Added in the senior-architecture-review capacity rebalance: static layout/component scaffolding for screens 8, 9, 12, 13, 14, 15 (per `MVP-UI-Wireframes.md`'s structural specs, which don't require a working backend endpoint to lay out) is front-loaded into Week 1, since Week 2's frontend workload (originally 56h in a single week against a ~30h/week capacity) was the single worst per-week overload found in this review.** This is scaffolding only (layout regions, static fields, no live data wiring); W2-09 finishes wiring these screens to real endpoints once they exist.
- **Risk:** Low.
- **Test requirements:** an end-to-end smoke test: login succeeds, shell renders, logout returns to Login.
- **Definition of Done:** a user can log in, see the shell with their name/roles, log out, and every front-loaded screen's static layout matches its wireframe spec (no live data required yet).
- **Can run in parallel:** Yes, once W1-02 has a working (even partial) login endpoint — Frontend workstream, parallel to W1-03 through W1-06.
- **Workstream:** Frontend.

#### W1-08 — CI pipeline and test infrastructure
- **Story:** As the team, we need automated build/test on every change from day one, not bolted on later.
- **Business value:** Prevents regressions accumulating silently across a compressed 3-week timeline.
- **Acceptance criteria:** CI runs build + unit tests on every push; a basic integration-test harness (in-memory or containerized test database) is available for W1-03 onward's tests to run against.
- **Dependencies:** W1-01.
- **Estimated effort:** 8h.
- **Risk:** Low.
- **Test requirements:** N/A (this item builds the test infrastructure itself).
- **Definition of Done:** a deliberately-broken test fails the pipeline; a passing suite goes green.
- **Can run in parallel:** Yes, from day one — QA/DevOps workstream, independent of the others.
- **Workstream:** QA/DevOps.

**Week 1 daily milestones (illustrative, not a rigid schedule):**
- Day 1–2: W1-01, W1-08 start.
- Day 2–3: W1-02, W1-03 start once W1-01 lands.
- Day 3–4: W1-05 starts (Integration); W1-07 starts once W1-02 has a working login stub.
- Day 4–5: W1-04 (after W1-03); W1-06 (after W1-04).
- **End-of-week-1 gate:** auth works, schema exists, audit/Outbox foundation proven, CRM test double exists, Genesys mock endpoint accepts events, login UI shell works. If this gate isn't met, Week 2's CRM Verification/Ticket Creation work has nothing to build on — this is the single most important checkpoint in the whole pilot.

### 5.5 Week 2 — Core Ticketing Workflow (Reference)

#### W2-01 — CRM verification flow, including Verification Sessions (backend)
- **Story:** Implements `MVP-API-Contracts.md` §2.1–2.4 against the test double from W1-05.
- **Business value:** FR-VER-01–05.
- **Acceptance criteria:** unit search/lookup, contact retrieval all function against the test double; `VerificationSessions` create/select-target/confirm/get (§2.4.1–2.4.4) enforce single-agent ownership, expiry, and the confirmed-before-consumable state machine.
- **Dependencies:** W1-05, W1-03.
- **Estimated effort:** 15h (+5h vs. the pre-review estimate — covers the `VerificationSessions` endpoints that replace the old, circular requester-confirmation-by-`TicketId` design, per Finding DR-01). **Risk:** Medium (the immutable-snapshot write-once rule and the session's single-use/expiry/ownership enforcement both need real tests, not just convention).
- **Test requirements:** attempting to consume an already-consumed or expired session returns `409`/`410` (not a second requester-confirmation on the same ticket — that endpoint no longer exists).
- **Definition of Done:** all CRM-verification and Verification-Session endpoints pass their contract tests.
- **Can run in parallel:** No (blocks W2-03).
- **Workstream:** Integration → handed to Backend/Architecture developer for the snapshot-immutability piece specifically, or same person if only one Integration developer exists.

#### W2-02 — Intake Record fallback flow (backend)
- **Story:** Implements `MVP-API-Contracts.md` §2.5–2.7.
- **Business value:** FR-VER-07 — CRM outage doesn't block Critical/High intake.
- **Acceptance criteria:** create/list/promote all function; promotion links `LinkedTicketId` correctly.
- **Dependencies:** W2-01.
- **Estimated effort:** 6h. **Risk:** Low.
- **Test requirements:** promoting an already-promoted Intake Record returns `409`.
- **Definition of Done:** the outage-to-recovery flow works end-to-end against the test double's simulated-outage mode.
- **Can run in parallel:** Yes, with W2-03 once W2-01 lands.
- **Workstream:** Integration.

#### W2-03 — Ticket creation (backend)
- **Story:** Implements `MVP-API-Contracts.md` §3.1.
- **Business value:** FR-TKT-01–06, FR-CLS-01–03, FR-RTE-01.
- **Acceptance criteria:** creates a ticket with correct `TicketNumber` format from a confirmed `VerificationSessionId` (not directly-supplied unit/contact fields, per Finding DR-01), routes to the correct department from category, opens the initial `TicketSlaInstances` row, writes seed `TicketStatusHistory` rows, consumes the session and copies its snapshot into `TicketRequesterSnapshots` in the same transaction.
- **Dependencies:** W2-01, W1-04 (SLA instance creation needs the SLA due-date computation — see W2-06).
- **Estimated effort:** 12h (+2h vs. the pre-review estimate — session-consumption logic and the immutable-snapshot-from-session copy). **Risk:** Medium (many moving parts converge here).
- **Test requirements:** idempotency-key replay test (no duplicate ticket created); category-to-department routing test.
- **Definition of Done:** contract tests for §3.1 pass, including the idempotency behavior.
- **Can run in parallel:** No (central dependency for the rest of Week 2).
- **Workstream:** Architecture/Foundation.

#### W2-04 — Ticket read/list/detail/timeline (backend)
- **Story:** Implements `MVP-API-Contracts.md` §3.2–§3.4, §3.13.
- **Business value:** FR-TKT (queue and detail visibility), FR-ADM-03 (auditability via timeline).
- **Acceptance criteria:** list filters/sorts correctly; detail returns the full nested shape; timeline merges all five source tables correctly ordered.
- **Dependencies:** W2-03.
- **Estimated effort:** 10h. **Risk:** Low.
- **Test requirements:** timeline ordering test across mixed event types.
- **Definition of Done:** all four endpoints pass contract tests.
- **Can run in parallel:** Yes, with W2-05/06/07 once W2-03 lands.
- **Workstream:** QA/DevOps (moved from Architecture/Foundation in the senior-architecture-review capacity rebalance — this item is CRUD-shaped and Low-risk, a reasonable fit for a generalist with the schema already in hand from W1-03/W1-08).

#### W2-05 — Assignment, transfer, status change (backend)
- **Story:** Implements `MVP-API-Contracts.md` §3.5–§3.7.
- **Business value:** FR-RTE-03–05, FR-TKT-11.
- **Acceptance criteria:** assignment enforces department-membership check; transfer clears assignment and preserves the immutable `OriginatingDepartmentId`; status-change enforces the state-machine transition table and triggers pause on `PendingCustomer`.
- **Dependencies:** W2-03.
- **Estimated effort:** 12h. **Risk:** Medium (status-transition validation + the pause side-effect coupling is the trickiest part).
- **Test requirements:** invalid-transition rejection test; transfer-then-verify-immutable-ID test.
- **Definition of Done:** all three endpoints pass contract tests, including the pause side-effect.
- **Can run in parallel:** Yes, with W2-04.
- **Workstream:** Architecture/Foundation.

#### W2-06 — Notes and attachments (backend)
- **Story:** Implements `MVP-API-Contracts.md` §4.1–§4.6.
- **Business value:** FR-TKT-06, BR-010.
- **Acceptance criteria:** note creation; attachment upload with size/type validation and async virus-scan status; download blocked while not `Clean` or while withdrawn; **withdrawal (`IsWithdrawn`/`BlobStatus`, not physical deletion, per Finding DR-06)** respects the uploader-window/Supervisor+ policy.
- **Dependencies:** W2-03.
- **Estimated effort:** 12h (+2h vs. the pre-review estimate — the withdrawal/quarantine model replaces what was a simple `DELETE`). **Risk:** Medium (virus-scan integration and the never-downloadable-until-clean-or-withdrawn rule need careful testing).
- **Test requirements:** 11th-attachment rejection; download-while-pending rejection; download-while-withdrawn rejection; withdrawn row still present and queryable after withdrawal (regression test specifically proving no physical delete occurs).
- **Definition of Done:** all six endpoints pass contract tests.
- **Can run in parallel:** Yes, with W2-04/05.
- **Workstream:** Integration.

#### W2-07 — Resolve/close workflow (backend)
- **Story:** Implements `MVP-API-Contracts.md` §3.9–§3.10, §3.12.
- **Business value:** FR-RES-01–06, FR-TKT-10.
- **Acceptance criteria:** resolve requires a non-empty note and conditional fields per outcome; close blocked without a current resolution; duplicate-flag recommend/confirm/reject state machine works.
- **Dependencies:** W2-03.
- **Estimated effort:** 10h. **Risk:** Medium.
- **Test requirements:** empty-note rejection; close-without-resolution `409`; duplicate-chain rejection.
- **Definition of Done:** all endpoints pass contract tests.
- **Can run in parallel:** Yes, with W2-04/05/06.
- **Workstream:** Architecture/Foundation (second developer slot if available, else sequenced after W2-05).

#### W2-08 — CRM verification, creation, and detail UI (frontend)
- **Story:** Builds screens 4, 5, 6, 7 (partial — read-only detail first pass).
- **Business value:** the agent-facing core of the pilot.
- **Acceptance criteria:** screens match `MVP-UI-Wireframes.md` §4–§7's specs (loading/empty/error states included, not deferred).
- **Dependencies:** W2-01, W2-03, W2-04 (needs working endpoints to integrate against, not just contracts on paper).
- **Estimated effort:** 27h (+3h vs. the pre-review estimate — screens 4/5/6 now drive the multi-step Verification Session flow, per Finding DR-01, instead of a single combined confirm-and-create step). **Risk:** Medium (largest single frontend item this week).
- **Test requirements:** a scripted manual/E2E walkthrough of the full create-ticket happy path plus the CRM-outage fallback path.
- **Definition of Done:** an agent can search a unit, confirm a contact, create a ticket, and view its detail, entirely through the UI.
- **Can run in parallel:** Partially — screen 4/5 can start once W2-01 lands, ahead of W2-03/04.
- **Workstream:** Frontend.

#### W2-09 — Assignment, transfer, notes/attachments, resolve/close UI (frontend)
- **Story:** Builds screens 8, 9, 12, 13, 14, 15.
- **Business value:** completes the agent-facing ticket lifecycle.
- **Acceptance criteria:** matches `MVP-UI-Wireframes.md` §8, §9, §12–§15's specs.
- **Dependencies:** W2-05, W2-06, W2-07 (backend), W2-08 (shares the Ticket Details shell), W1-07 (static scaffolding for these same screens was front-loaded there).
- **Estimated effort:** 7h (**-15h vs. the pre-review estimate** — this item is now wiring the six screens front-loaded as static scaffolding in W1-07 to their real backend endpoints, not building them from nothing). **Risk:** Medium.
- **Test requirements:** scripted walkthrough of assign → note → attach → resolve → close, and separately reopen/duplicate-flag.
- **Definition of Done:** all six screens functional against real (non-mock) backend endpoints.
- **Can run in parallel:** No relative to W2-08 (same developer, same shell) — sequenced after it starts, may overlap toward week's end.
- **Workstream:** Frontend.

#### W2-10 — Ticket queue and dashboard UI, first pass (frontend)
- **Story:** Builds screens 2 (partial), 3.
- **Business value:** FR-RPT-07 (basic operational view), FR-TKT queue visibility.
- **Acceptance criteria:** matches `MVP-UI-Wireframes.md` §2–§3's specs for the pieces that don't depend on SLA data (full SLA-aware dashboard tiles land in Week 3 alongside the SLA engine).
- **Dependencies:** W2-04.
- **Estimated effort:** 10h. **Risk:** Low.
- **Test requirements:** filter/sort/pagination manual test pass.
- **Definition of Done:** queue and basic count tiles render against real data.
- **Can run in parallel:** Yes, with W2-09 if a second frontend resource exists; otherwise sequenced.
- **Workstream:** Frontend.

#### W2-11 — Integration/contract test suite for Week 2 endpoints
- **Story:** As QA, I need automated coverage of every endpoint shipped this week before Week 3 builds on top of it.
- **Business value:** catches regressions before they compound.
- **Acceptance criteria:** every endpoint in W2-01 through W2-07 has at least one happy-path and one validation-failure automated test.
- **Dependencies:** trails each backend item by roughly a day.
- **Estimated effort:** 16h. **Risk:** Low.
- **Test requirements:** N/A (this item *is* the test requirement for the week).
- **Definition of Done:** CI shows green coverage of Week 2's endpoint surface.
- **Can run in parallel:** Yes, continuously through the week.
- **Workstream:** QA/DevOps.

**Week 2 daily milestones:**
- Day 6–7: W2-01, W2-02 (Integration); W2-03 (Architecture) starts once W2-01's confirmation endpoint exists.
- Day 7–8: W2-04, W2-05, W2-06, W2-07 proceed in parallel once W2-03 lands; W2-08 (frontend) starts against W2-01/03.
- Day 8–9: W2-09, W2-10 proceed; W2-11 trails continuously.
- **End-of-week-2 gate:** an agent can, start to finish through the UI, verify a unit, create a ticket, assign it, add a note, attach a file, resolve it, and close it — with every step audited. If this gate isn't met, Week 3's SLA/escalation/Genesys work has no stable ticket lifecycle to attach to.

### 5.6 Week 3 — SLA, Escalation, Genesys, and Pilot Readiness (Reference)

#### W3-01 — SLA calculation engine (business-calendar-aware due dates)
- **Story:** Implements the due-date math behind `MVP-API-Contracts.md` §5.1, using `BusinessCalendars`/`Holidays`.
- **Business value:** FR-SLA-01–04.
- **Acceptance criteria:** Critical due dates ignore the calendar (24/7); other tiers correctly exclude non-working hours/days/holidays.
- **Dependencies:** W2-03 (ticket creation must call into this at creation time — retrofitted if W2-03 stubbed it).
- **Estimated effort:** 14h. **Risk:** High — business-calendar math (especially business-hours-with-holidays) is the single most error-prone calculation in the whole system.
- **Test requirements:** worked-example tests matching `SLA-Architecture.md`'s examples exactly, including a holiday-spanning case.
- **Definition of Done:** due-date computation matches every worked example in `SLA-Architecture.md` §8.
- **Can run in parallel:** No (blocks W3-02, W3-03, W3-05).
- **Workstream:** Architecture/Foundation.

#### W3-02 — SLA pause/resume and first-response recording
- **Story:** Implements `MVP-API-Contracts.md` §5.2–§5.4.
- **Business value:** FR-SLA-05, FR-RES-07, `TicketSlaPausePeriods` (§0.1 of `MVP-ERD.md`).
- **Acceptance criteria:** pause blocked for Critical; resume computes `PausedDurationMinutes` and shifts due dates correctly; first-response recording is write-once.
- **Dependencies:** W3-01.
- **Estimated effort:** 10h. **Risk:** Medium.
- **Test requirements:** Critical-pause-rejection test; pause-then-resume due-date-shift test; double-first-response `409` test.
- **Definition of Done:** all four endpoints pass contract tests.
- **Can run in parallel:** Yes, with W3-03.
- **Workstream:** Architecture/Foundation.

#### W3-03 — Priority upgrade / downgrade-request-and-approval flow
- **Story:** Implements `MVP-API-Contracts.md` §5.5–§5.6.
- **Business value:** FR-SLA-09, ADR-0012.
- **Acceptance criteria:** upgrade due date = earlier-of; downgrade is a two-actor flow — a `PriorityDowngradeRequests` row (§5.6.1) created by the requesting Agent, decided by a Dept Head+ via a separate approve/reject action (§5.6.4/§5.6.5) whose approver identity is taken from the caller's own JWT, never a request field (Finding DR-05); breach flags never reset.
- **Dependencies:** W3-01.
- **Estimated effort:** 16h (+6h vs. the pre-review estimate — replaces one endpoint with a new entity and four endpoints: create request, list, approve, reject, plus the at-most-one-pending-per-ticket and expiry rules). **Risk:** Medium (the breach-preservation invariant needs a dedicated regression test, since it's the highest-consequence rule per `MVP-ERD.md` §2.15; the approver-identity-never-client-supplied rule needs its own explicit test too, since it's the specific defect this rework closes).
- **Test requirements:** an explicit "breach flag stays true after downgrade" regression test; an explicit test that an approve/reject call ignores any approver-identity field in the request body and uses only the authenticated caller; a duplicate-pending-request rejection test.
- **Definition of Done:** contract tests pass, including the breach-preservation case.
- **Can run in parallel:** Yes, with W3-02.
- **Workstream:** Architecture/Foundation.

#### W3-04 — Escalation engine (manual + scheduled auto-escalation)
- **Story:** Implements `MVP-API-Contracts.md` §5.7–§5.9 plus the Hangfire-driven auto-escalation job (ADR-0015).
- **Business value:** FR-ESC-01–07.
- **Acceptance criteria:** manual flag/Level 4 role-gated correctly; scheduled job advances Level 2→3 after the configured window; Level 2 auto-triggers on breach.
- **Dependencies:** W3-01 (needs due-date/breach state to trigger from).
- **Estimated effort:** 14h (+2h vs. the pre-review estimate — this item's Hangfire scheduled-job pattern is reused to build the `VerificationSessions`/`PriorityDowngradeRequests` expiry sweeps, per Finding DR-01/DR-05, rather than building that pattern a third time elsewhere). **Risk:** Medium-High (scheduled-job correctness is hard to verify without a controllable clock in tests).
- **Test requirements:** a time-manipulated integration test proving the Level 2→3 auto-advance fires at the correct elapsed window, not before/after.
- **Definition of Done:** manual and automatic escalation paths both pass their tests.
- **Can run in parallel:** Yes, with W3-02/03.
- **Workstream:** Architecture/Foundation.

#### W3-05 — Genesys Basic Integration (real adapter over the mock-tested foundation) — **BLOCKED for real-sandbox validation; must ship behind a feature flag (§3)**
- **Story:** Complete `MVP-API-Contracts.md` §6.1–§6.6 on top of the webhook foundation built in W1-06, wiring First-Human-Response satisfaction, manual linking, and `GenesysAgentMappings` CRUD (Finding DR-02).
- **Business value:** the MVP's confirmed Genesys scope (ADR-0019).
- **Acceptance criteria:** call-answer events satisfy First Human Response via the same code path as §5.2; manual linking works; failed-events queue and retry work at the per-event grain (Finding DR-03); `GenesysAgentMappings` upsert/deactivate (§6.6.1/§6.6.2) function. **The feature flag controlling this integration defaults off and stays off until the conditions in §3 are met** — this is now a management-mandated requirement, not only an architectural recommendation.
- **Dependencies:** W1-06, W3-02 (needs first-response recording to exist).
- **Estimated effort:** 20h (+8h vs. the pre-review estimate: +2h for `GenesysAgentMappings` CRUD, Finding DR-02; +6h to complete and test the per-event idempotency/dedup model, Finding DR-03, beyond what W1-06 already built). **Risk:** High, and explicitly **BLOCKED, not merely risky**: **this entire item is built and tested against `Genesys-Mock-Contract.md`, not a real Genesys sandbox** (per that document's own open questions, §15 items 1–8 are unresolved — signature scheme, real event schema, delivery guarantees, and sandbox availability are all still unconfirmed as of this review). **Real-schema/real-sandbox integration testing cannot begin until Genesys supplies: (1) sandbox or test-environment access, (2) confirmed payload/event-type schema, (3) confirmed signature/authentication scheme.** None of those three are within this team's control, and none should be treated as "in progress" on this backlog — they are an external dependency, tracked, not scheduled.
- **Test requirements:** the full mock-event battery from `Genesys-Mock-Contract.md` §4 (idempotency via both the preferred and fallback key paths, out-of-order, missing-field, unknown-agent, signature-rejection-produces-no-persisted-row).
- **Definition of Done:** all mock-contract behaviors pass, behind a flag defaulted off. **Explicitly NOT part of this item's Definition of Done: any claim of real-Genesys-integration correctness, or turning the flag on.** The pilot readout must state plainly that Genesys integration is "mock-validated only, shipped flagged off, pending Genesys-team sandbox/schema/security confirmation" — this phrasing, or one equally unambiguous, is itself a required deliverable of this item, not optional framing.
- **Can run in parallel:** Yes, with W3-04 (different developer).
- **Workstream:** Integration.

#### W3-06 — Email acknowledgement notification
- **Story:** Implements FR-NOT-01 — the automated acknowledgement email on ticket creation.
- **Business value:** first customer-facing touchpoint of the whole system.
- **Acceptance criteria:** email attempted for every ticket via the Outbox; content matches the required fields (ticket number, expected response time, department, Geyness reference); explicitly does not set `FirstHumanResponseAtUtc`.
- **Dependencies:** W1-04, W2-03.
- **Estimated effort:** 6h. **Risk:** Low.
- **Test requirements:** a test asserting the ack email never touches `FirstHumanResponseAtUtc`.
- **Definition of Done:** ack email fires and is retryable via Outbox on transient failure.
- **Can run in parallel:** Yes, with W3-01 through W3-05.
- **Workstream:** QA/DevOps (moved from Integration in the senior-architecture-review capacity rebalance — a self-contained, Low-risk Outbox consumer, and Integration's Week 3 load grew once W3-05 absorbed the Genesys event-model rework).

#### W3-07 — SLA/escalation UI (screens 10, 11)
- **Story:** Builds screens 10 and 11 against W3-02/03/04's live endpoints.
- **Business value:** makes the SLA/escalation engine usable, not just API-correct.
- **Acceptance criteria:** matches `MVP-UI-Wireframes.md` §10–§11's specs, including the Critical-never-pauses disabled state, the breach-preservation notice text, and the two-step downgrade request/approval UI (request form for Agents, a separate pending-inbox and approve/reject action for Dept Head+, per Finding DR-05 — no approver-selection field anywhere in the request form).
- **Dependencies:** W3-02, W3-03, W3-04.
- **Estimated effort:** 17h (+3h vs. the pre-review estimate — the downgrade flow is now two screens/states instead of one combined form). **Risk:** Medium.
- **Test requirements:** scripted walkthrough of pause/resume, upgrade, downgrade-request-then-separate-approval, manual escalation.
- **Definition of Done:** both screens functional against real backend.
- **Can run in parallel:** Yes, with W3-08.
- **Workstream:** Frontend.

#### W3-08 — Genesys panel, admin screens, dashboard completion (screens 16–20, 2 finish)
- **Story:** Builds the remaining admin/operational screens and completes the dashboard's SLA-aware tiles.
- **Business value:** completes System Administrator/Supervisor+ operational capability and the day-one basic dashboard (FR-RPT-07).
- **Acceptance criteria:** matches `MVP-UI-Wireframes.md` §16–§20 and the SLA-dependent parts of §2.
- **Dependencies:** W3-01 through W3-05, W2-10.
- **Estimated effort:** 22h. **Risk:** Medium (largest single remaining frontend item; a natural item to trim first if time runs short).
- **Test requirements:** scripted walkthrough of each admin screen's primary action; dashboard tile accuracy spot-check against known seed data.
- **Definition of Done:** all six remaining screens functional.
- **Can run in parallel:** Partially — screens 16/17/18 (admin) don't depend on SLA/Genesys work and could start earlier if frontend capacity allows; 19/20 and the dashboard's SLA tiles do depend on Week 3's backend items.
- **Workstream:** Frontend.

#### W3-09 — Full regression pass and integration testing
- **Story:** As QA, exercise the entire ticket lifecycle end-to-end, including SLA/escalation/Genesys paths, before UAT.
- **Business value:** catches integration-level defects that per-endpoint unit/contract tests miss.
- **Acceptance criteria:** every scripted walkthrough referenced across W2/W3 items passes as one continuous run; the traceability matrix's test-scenario column (`MVP-Traceability-Matrix.md`) is spot-checked against actual behavior, not just endpoint existence.
- **Dependencies:** everything above.
- **Estimated effort:** 16h. **Risk:** Medium.
- **Test requirements:** N/A (this item is the test pass).
- **Definition of Done:** a documented pass/fail log against the full requirement list; any failure triaged into UAT-fix work (W3-10) rather than silently left.
- **Can run in parallel:** No (needs everything else substantially complete) — the last major QA gate before UAT.
- **Workstream:** QA/DevOps.

#### W3-10 — UAT support and fixes
- **Story:** As the team, support pilot users during UAT and fix defects found.
- **Business value:** the actual point of a pilot — real usage surfaces what tests don't.
- **Acceptance criteria:** a defect triage list is maintained; High-severity defects fixed before go-live, Medium/Low triaged into a post-pilot backlog if time is short.
- **Dependencies:** W3-09.
- **Estimated effort:** remaining time in the week (contingency buffer — deliberately not fully allocated above).
- **Risk:** Medium (unknown until UAT happens).
- **Test requirements:** each fix gets a regression test added, not just a manual verification.
- **Definition of Done:** no known High-severity defect remains open at pilot go-live.
- **Can run in parallel:** N/A (whole team, reactive).
- **Workstream:** All.

#### W3-11 — Pilot deployment
- **Story:** As the team, deploy the pilot build to its target environment.
- **Business value:** the actual delivery milestone.
- **Acceptance criteria:** deployment is repeatable (scripted, not manual click-ops); rollback path exists; hosting target per ADR-0022 (`[ASSUMPTION]`, still open per `docs/architecture/README.md`'s open-questions list — flagged again here since it directly affects this item's exact steps). **This is a pilot deployment, not a production deployment — no production deployment is authorized at this stage (§4), a constraint that applies to this reference plan too, not only the approved solo plan.**
- **Dependencies:** W3-09, W3-10 (High-severity fixes in).
- **Estimated effort:** 8h. **Risk:** Medium (depends on the still-open hosting-target assumption).
- **Test requirements:** a post-deploy smoke test (login, create ticket, view dashboard) run against the deployed instance.
- **Definition of Done:** pilot instance is live, smoke-tested, and reachable by pilot users.
- **Can run in parallel:** No (final step).
- **Workstream:** QA/DevOps.

**Week 3 daily milestones:**
- Day 11–12: W3-01 (blocking), then W3-02/03/04/05/06 fan out in parallel.
- Day 12–13: W3-07/08 (frontend) proceed against landing backend endpoints.
- Day 13–14: W3-09 full regression pass.
- Day 14–15: W3-10 UAT support/fixes, W3-11 deployment.
- **Pilot go-live gate:** the full lifecycle from `MVP-Traceability-Matrix.md`'s MVP requirement set works end-to-end, with no open High-severity defect.

### 5.7 Reference Critical Path, Parallel Workstreams, and Scope-Protection Rules

**Critical path:** `W1-01 → W1-03 → W1-04 → W2-03 → W2-05 (status/pause coupling) → W3-01 → W3-02/03/04 (any one) → W3-09 → W3-10 → W3-11.` Every other item either feeds into this chain, or branches off it in parallel (notably: the entire Frontend workstream, the Genesys integration line W1-06→W3-05, and the CRM/Intake line W1-05→W2-01→W2-02, none of which are on the critical path but all of which must still finish before W3-09's full regression pass).

**Parallel workstreams (summary):**

| Workstream | Week 1 | Week 2 | Week 3 |
|---|---|---|---|
| Architecture/Foundation | W1-01, 02, 03, 04 | W2-03, 05, 07 | W3-01, 02, 03, 04 |
| Integration | W1-05, 06 | W2-01, 02, 06 | W3-05 |
| Frontend | W1-07 (incl. front-loaded screen scaffolding) | W2-08, 09, 10 | W3-07, 08 |
| QA/DevOps | W1-08 | W2-04, 11 | W3-06, 09, 11 (+10 with everyone) |

**Scope-protection rules:**

1. No item outside `MVP-Traceability-Matrix.md`'s confirmed-MVP requirement set is started, even if time appears available — that time goes to hardening/UAT-fix buffer (W3-10) instead.
2. If Week 1's end-of-week gate slips, Week 2's scope is not silently compressed — the first items dropped are, in order: W2-10's dashboard polish, then W3-08's admin-screen depth. Invoking this rule fully absorbs Frontend's remaining overage (44h → 34h in Week 2, ~13% over instead of ~47%).
3. W3-05 (Genesys) is BLOCKED for real-sandbox validation and must ship behind a feature flag (§3), not merely "the single most likely item to slip." Real Genesys integration testing cannot begin until Genesys supplies sandbox access, a confirmed payload schema, and a confirmed signature scheme. Mock-validated must never be reported as equivalent to "Genesys integration tested" or "production-ready" — see §4.
4. The immutable/append-only invariants (write-once snapshot, breach-flag-never-resets, append-only audit) are never descoped or shortcut, even under time pressure.
5. The Architecture/Foundation capacity gap this reference plan found (135h vs. 90h) is **resolved for the current pilot by management's decision in §0** (1 developer, 4 weeks, reduced scope per §0.2) — it is not resolved *within this reference plan itself*, which remains a record of what a 4-person/3-week team would need to address if that model is adopted later.

### 5.8 Reference Fallback Note: Two Developers

If team capacity is later increased to exactly 2 (rather than back to the full 4), the two-developer fallback scope originally sketched alongside this reference plan still applies: basic SLA due-date tracking (business-calendar-aware due dates, warning threshold, breach detection — but no pause/resume, no automatic escalation), priority upgrade only (no downgrade-approval workflow), basic email acknowledgement, and a fuller UI (SLA summary, assignment/transfer, notes/attachments) — still no Genesys integration of any kind, mock or real, since the mock-contract validation work alone is substantial enough that a 2-developer team's time is better spent hardening the core lifecycle first.

---

## 6. What This Document Does Not Cover

No actual sprint-tracker tickets, no named individual assignments, no story-pointing/velocity tracking, no CI/CD pipeline YAML, no infrastructure-as-code. Those are Phase 3 execution-tooling concerns, built from this plan, not part of it. **No production deployment plan** — production deployment is not authorized at this stage (§4), and this document does not describe one.
