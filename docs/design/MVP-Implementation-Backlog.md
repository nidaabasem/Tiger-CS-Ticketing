# Tiger Group — CS Ticketing System
## MVP Implementation Backlog

| | |
|---|---|
| **Status** | Design for review — planning artifact only. **Revised following management's approved delivery decision** (see §0). |
| **Scope** | The approved plan is a **4-week, 1-developer functional pilot** (§2), built from `MVP-ERD.md`, `MVP-Data-Dictionary.md`, `MVP-API-Contracts.md`, `Genesys-Mock-Contract.md`, and `MVP-UI-Wireframes.md`. The original 4-person/3-week plan is retained as a reference appendix (§5) for a future team scale-up, not as an active plan. |
| **Explicitly not done here** | No application code, no project scaffolding, no actual sprint/task-tracker tickets created in any external tool — this is the plan those would be created from. **No production deployment is authorized at this stage — see §4.** |
| **Base** | `main` @ `4fe6f19` |
| **Related documents** | All preceding `docs/design/*.md` documents, including `MVP-UI-Wireframes.md` and `MVP-Traceability-Matrix.md`; `docs/architecture/System-Architecture.md`, `Module-Design.md`, `SLA-Architecture.md`; `MVP-Design-Review-Findings.md` (Finding DR-08, resolved by the decision recorded in §0) |
| **Date** | 2026-08-18; revised in the senior-architecture-review pass; revised again to record management's approved delivery decision |

---

## 0. Team-Capacity Decision (Governs Everything Below) — **Approved, Not an Assumption**

**Management has approved the following delivery decision, resolving Finding DR-08 (`MVP-Design-Review-Findings.md`):**

- **Target: a 4-week functional pilot** (not the original 3 weeks).
- **Team capacity: 1 developer.**
- **Genesys integration must remain behind a feature flag, defaulted off, until the official sandbox, webhook schema, and authentication mechanism are confirmed by Genesys** — see §3.
- **Mock validation must never be described as production-ready**, in any status update, pilot readout, or go-live communication — see §4.
- **No production deployment is authorized at this stage** — see §4. Only a pilot deployment to a non-production environment is in scope.

This is no longer `[ASSUMPTION]` — it is a decision, recorded here as the source of truth for every hour estimate and scope choice below. The original 4-person/3-week plan (§5) is **superseded as the active plan** and retained only as a reference for a future team scale-up; nothing in §5 should be treated as current.

### 0.1 Workload Summary — Hours per Week (1 Developer, 4 Weeks)

A single developer at ~30 ideal-hours/week gives a **~120-hour budget** across 4 weeks (the same per-person weekly rate used throughout this document's prior revisions, for consistency — no assumption that a solo developer works harder or longer per week than a team member did). The approved plan in §2 totals **129 ideal hours** — a disclosed **9-hour (~8%) overage**, concentrated in Week 4:

| Week | Hours | vs. 30h/week budget |
|---|---|---|
| Week 1 | 29h | −1h (within budget) |
| Week 2 | 30h | On budget |
| Week 3 | 30h | On budget |
| Week 4 | 40h | **+10h (+33% over)** |
| **Total** | **129h** | **+9h (+8%) over the 120h budget** |

**This is disclosed, not hidden**, consistent with how this document has handled every other capacity finding: Week 4 carries the regression pass and pilot deployment on top of its own feature work, and is this plan's one real pressure point. If it manifests as schedule slip, the same three-option framework used for the prior (now-superseded) Architecture/Foundation gap applies here: accept the bounded overtime, extend by 2–3 days, or trim further (the first candidate to trim is S-15's attachment/notes UI polish, per §2's own notes).

### 0.2 What Was Cut to Fit 1 Developer, and Why

The reference plan (§5) totals ~387 ideal hours across 4 roles (135+78+110+64 from its own workload table). One developer at ~120h for 4 weeks cannot deliver that scope — not through better sequencing, only through genuinely doing less. The approved plan (§2) is the **1-developer fallback scope already sketched in this document's prior revision (formerly §6.1)**, now made concrete and scheduled across exactly 4 weeks rather than left as a range estimate. What's included and excluded, and why, is stated inline in §2; the short version:

- **Included:** the full five-dimension ticket lifecycle (create → assign → status change → resolve → close), a simplified single-step verification flow (still session-based, so Finding DR-01's circular-dependency lesson is not silently reintroduced), notes and attachments (upload/list/download, virus-scan status), a basic email acknowledgement, and a minimal UI covering exactly these flows.
- **Excluded (deferred to a fast-follow phase, once team capacity increases):** SLA due-date computation, business-calendar math, pause/resume, breach detection, escalation (manual or automatic), the priority-downgrade approval workflow (Finding DR-05), attachment withdrawal/quarantine (Finding DR-06), and — per management's explicit decision — **all Genesys integration, mock or real** (see §3). The full 27-entity schema design from `MVP-ERD.md`/`MVP-Data-Dictionary.md` is **not rebuilt or narrowed** — the entities for deferred features simply aren't implemented yet, so no design rework is needed when the team scales up.
- **Not a business-rule reduction without flagging it as one:** removing the priority-downgrade approval gate for this scope means priority is agent-editable directly, with no Dept-Head+ check — this is a real behavior change from ADR-0012, not just a schedule one, and must be communicated to the sponsor as such, not silently absorbed.

---

## 1. Backlog Item Fields

Every item below carries: **Backlog ID**, user story/task, business value, acceptance criteria, dependencies, estimated effort (ideal dev-hours), Risk (High/Medium/Low), test requirements, Definition of Done.

Since there is exactly one developer, there is no "can run in parallel" field and no "workstream" field in this section — every item is sequential on one person's calendar. (The reference plan in §5 retains those fields, since they were meaningful there.)

---

## 2. Approved 4-Week, 1-Developer Pilot Plan

Items are sequential; "Week N" markers show where each item falls by cumulative hours, not a hard boundary — solo work naturally straddles week boundaries more than a multi-person plan's would. Each item names the reference-plan item(s) it's adapted from, so nothing here is invented independent of the detailed design work already done.

### Week 1 (target ~30h; actual 29h)

**S-01 — Solution scaffolding** *(adapted from W1-01)*
- **Story/value:** the physical solution structure (Domain/Application/Infrastructure/Api/Web/Tests) in place before feature work starts.
- **Acceptance criteria:** solution builds; module boundaries match `Module-Design.md`'s dependency rules; a placeholder health-check endpoint responds.
- **Dependencies:** none. **Estimated effort:** 6h. **Risk:** Low.
- **Test requirements:** a build-verification check that fails on a prohibited project reference.
- **Definition of Done:** solution builds clean.

**S-02 — Minimal CI** *(adapted from W1-08, reduced)*
- **Story/value:** build + unit tests run automatically on every push — a containerized integration-test harness is deferred as unnecessary overhead at this scale; local/in-memory test execution is sufficient for a 1-developer pilot.
- **Acceptance criteria:** CI runs build + unit tests on push.
- **Dependencies:** S-01. **Estimated effort:** 3h (**-5h vs. the reference plan's W1-08** — a full containerized harness isn't worth the setup cost for one developer who can run integration tests locally). **Risk:** Low.
- **Test requirements:** N/A (this item builds the test infrastructure).
- **Definition of Done:** a deliberately-broken test fails the pipeline.

**S-03 — Authentication and authorization, simplified** *(adapted from W1-02)*
- **Story/value:** login and role/department-based access control (FR-ADM-01).
- **Acceptance criteria:** `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/users/me` per `MVP-API-Contracts.md` §1.1–1.3; role checks enforced on every endpoint as it's built (not deferred as stubs, since there's no second developer to catch a missed check later).
- **Dependencies:** S-01. **Estimated effort:** 8h (**-2h vs. W1-02** — the lockout-policy edge cases get a simpler, less exhaustively-tested implementation at this scale; still functionally present, per Security-Architecture.md's baseline). **Risk:** Medium.
- **Test requirements:** login success/failure unit tests; a smoke test on a protected endpoint with/without a token.
- **Definition of Done:** a seeded test user logs in and receives a JWT with correct roles.

**S-04 — Database schema, reduced entity set** *(adapted from W1-03, narrowed)*
- **Story/value:** the schema for exactly this scope's features — not the full 27-entity design.
- **Acceptance criteria:** implements `AspNetUsers/Roles`, `Employees`, `Departments`, `UserDepartmentAssignments`, `Categories`, `Priorities` (fixed list only, no `SlaPolicies` computation), `UnitReferences`, `ContactReferences`, a **simplified single-step `VerificationSessions`** (see S-07), `TicketRequesterSnapshots`, `Tickets`, `TicketAssignments`, `TicketStatusHistory`, `TicketResolutions`, `TicketNotes`, `TicketAttachments`, `Notifications`, `OutboxMessages`, `AuditEntries` — roughly 19 of the 27 groups in `MVP-Data-Dictionary.md`. **Explicitly not built in this pass:** `SlaPolicies`' computed fields, `TicketSlaInstances`, `TicketSlaPausePeriods`, `TicketEscalations`, `PriorityDowngradeRequests`, `GenesysInteractions`, `GenesysInteractionEvents`, `GenesysAgentMappings`, `IdempotencyRecords` (not needed without Genesys/SLA-sweep idempotency concerns at this scope — a single `Outbox` retry counter suffices for the one notification type in scope).
- **Dependencies:** S-01. **Estimated effort:** 12h. **Risk:** Medium (schema mistakes are still expensive to fix later, even solo).
- **Test requirements:** a schema-verification check that every FK in scope is present.
- **Definition of Done:** migrations apply cleanly; seed data for `Priorities`/`Departments` loads.

### Week 2 (target ~30h; actual 30h)

**S-05 — Audit trail and a minimal Outbox** *(adapted from W1-04, reduced)*
- **Story/value:** every mutating action produces an audit record (FR-TKT-07, FR-ADM-03 — **not descoped even at reduced team size, per the immutable/append-only invariant rule carried over from the reference plan's Scope-Protection Rule 4**); a minimal Outbox exists for the one notification type in scope (email acknowledgement, S-13).
- **Acceptance criteria:** an audit-writing mechanism used by every later feature; a simple Outbox dispatch loop (no generalized cross-feature idempotency table, since there's no Genesys/SLA-sweep to share it with at this scope — `[ASSUMPTION]` a single retry-counter column on `OutboxMessages` is sufficient here, revisited when Genesys/SLA work resumes).
- **Dependencies:** S-04. **Estimated effort:** 8h (**-6h vs. W1-04** — no cross-feature idempotency generalization needed yet). **Risk:** High — still the highest-leverage item in this plan; getting the audit mechanism wrong is expensive to retrofit even solo.
- **Test requirements:** a round-trip test — write an Outbox message, confirm dispatch.
- **Definition of Done:** a sample event flows end-to-end through audit + Outbox.

**S-06 — CRM gateway interface and test double** *(adapted from W1-05)*
- **Story/value:** an `ICrmGateway` abstraction with a fake implementation, so verification can be built and tested before real CRM access exists.
- **Acceptance criteria:** covers unit lookup, unit search, contact lookup; the test double simulates an outage for fallback-path testing.
- **Dependencies:** S-01. **Estimated effort:** 6h. **Risk:** Low.
- **Test requirements:** happy-path and simulated-outage unit tests.
- **Definition of Done:** the test double is wired into DI; a real implementation can be swapped in later.

**S-07 — Verification flow, simplified single-step** *(adapted from W2-01, substantially reduced — Finding DR-01's lesson preserved)*
- **Story/value:** verify a unit/contact and capture the immutable read-back snapshot, **without** reintroducing the circular dependency Finding DR-01 fixed.
- **Acceptance criteria:** **one endpoint** combines what the full design (§5, `MVP-API-Contracts.md` §2.4.1–§2.4.4) splits into four — lookup, select, and confirm happen in a single call against a short-lived `VerificationSessions` row, still single-use and still consumed at ticket creation (S-08), so the sequencing defect DR-01 found is not silently reintroduced at reduced scope just because there's less UI around it.
- **Dependencies:** S-04, S-06. **Estimated effort:** 8h (**-7h vs. the reference plan's W2-01's 15h** — one endpoint instead of four, no separate selection/confirmation round-trip). **Risk:** Medium (the single-use/write-once rules still need a real test, not just convention, even simplified).
- **Test requirements:** a second consumption attempt on the same session returns `409`.
- **Definition of Done:** the single verification endpoint passes its contract test.

**S-08 — Ticket creation** *(adapted from W2-03)*
- **Story/value:** create a ticket from a confirmed verification session (FR-TKT-01–06, FR-CLS-01–03, FR-RTE-01).
- **Acceptance criteria:** correct `TicketNumber` format; routes to department from category; consumes the session and writes `TicketRequesterSnapshots` in the same transaction; seeds `TicketStatusHistory`. **No `TicketSlaInstances` row is opened** — SLA tracking is out of this scope entirely (§0.2), so ticket creation does not attempt to compute a due date at all.
- **Dependencies:** S-07. **Estimated effort:** 8h. **Risk:** Medium.
- **Test requirements:** idempotency-key replay test (no duplicate ticket); category-to-department routing test.
- **Definition of Done:** contract test passes, including idempotency.

### Week 3 (target ~30h; actual 30h)

**S-09 — Ticket read/list/detail/timeline** *(adapted from W2-04)*
- **Story/value:** queue and detail visibility (FR-TKT, FR-ADM-03's auditability via timeline).
- **Acceptance criteria:** list filters/sorts; detail returns the full shape (minus anything SLA/Genesys-related, which doesn't exist at this scope); timeline merges status history, assignments, notes, resolutions in order (no escalations — none exist at this scope).
- **Dependencies:** S-08. **Estimated effort:** 8h. **Risk:** Low.
- **Test requirements:** timeline ordering test.
- **Definition of Done:** endpoints pass contract tests.

**S-10 — Assignment, transfer, status change** *(adapted from W2-05)*
- **Story/value:** ownership and status management (FR-RTE-03–05, FR-TKT-11).
- **Acceptance criteria:** assignment enforces department membership; transfer preserves the immutable `OriginatingDepartmentId`; status-change enforces the transition table. **No pause side-effect** — `PendingCustomer` is a valid `TicketStatus` value, but it triggers no `TicketSlaPausePeriods` row, since none exist at this scope.
- **Dependencies:** S-08. **Estimated effort:** 8h. **Risk:** Medium.
- **Test requirements:** invalid-transition rejection test; transfer-preserves-immutable-ID test.
- **Definition of Done:** endpoints pass contract tests.

**S-11 — Notes and attachments, without withdrawal** *(adapted from W2-06, reduced per §0.2)*
- **Story/value:** internal notes and file attachments (FR-TKT-06, BR-010 — both still core MVP, not descoped).
- **Acceptance criteria:** note creation; attachment upload with size/type validation and virus-scan status; download blocked while not `Clean`. **No withdrawal/quarantine model** (Finding DR-06 deferred, §0.2) — an uploaded attachment simply cannot be removed at this scope; this is disclosed as a real limitation, not a silent gap, since `MVP-API-Contracts.md` §4.6 exists and describes withdrawal for when the team scales up.
- **Dependencies:** S-08. **Estimated effort:** 8h. **Risk:** Medium (virus-scan integration and never-downloadable-until-clean still need real tests).
- **Test requirements:** 11th-attachment rejection; download-while-pending rejection.
- **Definition of Done:** endpoints pass contract tests.

**S-12 — Resolve/close workflow** *(adapted from W2-07, reduced)*
- **Story/value:** the resolution lifecycle (FR-RES-01–06, FR-TKT-10).
- **Acceptance criteria:** resolve requires a non-empty note and conditional fields per outcome; close blocked without a current resolution. **Duplicate-flag recommend/confirm/reject (`MVP-API-Contracts.md` §3.12) deferred** — a ticket can still be resolved with `ResolutionOutcome = Duplicate` directly (§3.9), just without the separate lighter-weight flagging step; this preserves the underlying capability while cutting the extra endpoint.
- **Dependencies:** S-08. **Estimated effort:** 6h (**-4h vs. W2-07** — the duplicate-flag sub-flow is cut). **Risk:** Medium.
- **Test requirements:** empty-note rejection; close-without-resolution `409`.
- **Definition of Done:** endpoints pass contract tests.

### Week 4 (target ~30h; actual 40h — this plan's one disclosed pressure point, see §0.1)

**S-13 — Basic email acknowledgement** *(adapted from W3-06)*
- **Story/value:** the automated acknowledgement email on ticket creation (FR-NOT-01) — the only notification in this scope.
- **Acceptance criteria:** email attempted via the Outbox (S-05) for every ticket; content matches the required fields; does not set `FirstHumanResponseAtUtc` (moot at this scope, since First-Human-Response/SLA tracking don't exist here, but the field still shouldn't be touched by the ack path, for forward compatibility when SLA work resumes).
- **Dependencies:** S-05, S-08. **Estimated effort:** 4h. **Risk:** Low.
- **Test requirements:** a test asserting the ack path never touches `FirstHumanResponseAtUtc`.
- **Definition of Done:** ack email fires and retries via Outbox on transient failure.

**S-14 — UI: login, shell, verification/create flow, ticket detail** *(adapted from W1-07 + W2-08, reduced)*
- **Story/value:** the agent-facing core of the pilot — screens 1, 6, 7, and the simplified verification step from S-07 (a single combined lookup-and-confirm screen rather than the full screens 4–5 split).
- **Acceptance criteria:** login and shell work; an agent can look up and confirm a unit/contact in one screen, create a ticket, and view its detail.
- **Dependencies:** S-03 (login), S-07/S-08/S-09 (backend). **Estimated effort:** 16h. **Risk:** Medium (the largest single UI item, since one developer builds all of it).
- **Test requirements:** a scripted walkthrough of the full create-ticket happy path.
- **Definition of Done:** an agent can verify, create, and view a ticket entirely through the UI.

**S-15 — UI: queue, assignment/transfer, notes/attachments, resolve/close** *(adapted from W2-09/W2-10, reduced)*
- **Story/value:** completes the agent-facing ticket lifecycle and the basic queue view.
- **Acceptance criteria:** queue list/filter; assign/transfer/status-change actions; notes/attachments (upload/list/download, no withdrawal per S-11); resolve/close.
- **Dependencies:** S-09, S-10, S-11, S-12, S-14 (shares the ticket-detail shell). **Estimated effort:** 10h. **Risk:** Medium. **First candidate to trim if Week 4's disclosed overage (§0.1) needs to shrink** — the queue's filter/sort richness can launch simpler (a plain list, no multi-field filter) without losing the core capability.
- **Test requirements:** scripted walkthrough of assign → note → attach → resolve → close.
- **Definition of Done:** all screens functional against real backend endpoints.

**S-16 — End-to-end regression pass and pilot-readiness check**
- **Story/value:** exercise the full lifecycle before calling the pilot ready.
- **Acceptance criteria:** every scripted walkthrough from S-08 through S-15 passes as one continuous run; `MVP-Traceability-Matrix.md`'s in-scope rows are spot-checked against actual behavior.
- **Dependencies:** everything above. **Estimated effort:** 6h. **Risk:** Medium.
- **Test requirements:** N/A (this item is the test pass).
- **Definition of Done:** a documented pass/fail log; any failure fixed before S-17, not deferred past it.

**S-17 — Pilot deployment (non-production)**
- **Story/value:** deploy the pilot build to a **non-production** target.
- **Acceptance criteria:** deployment is repeatable; **this is explicitly not a production deployment — no production deployment is authorized at this stage** (management's decision, §0); hosting target per ADR-0022 remains `[ASSUMPTION]`/open.
- **Dependencies:** S-16. **Estimated effort:** 4h. **Risk:** Medium (depends on the still-open hosting-target assumption).
- **Test requirements:** a post-deploy smoke test (login, create ticket, view detail).
- **Definition of Done:** the pilot instance is live in a non-production environment, smoke-tested, and reachable by pilot users.

**Pilot go-live gate:** the reduced-scope lifecycle above works end-to-end, with no open High-severity defect, in a non-production environment.

---

## 3. Genesys Policy for This Pilot — Feature-Flagged, Deferred

**Per management's explicit decision:** Genesys integration (the webhook endpoint, `GenesysInteractions`, `GenesysInteractionEvents`, `GenesysAgentMappings` — all fully designed in `MVP-API-Contracts.md` §6 and `MVP-ERD.md`/`MVP-Data-Dictionary.md` §2.11/§2.25/§2.26) is **not built in this 4-week, 1-developer scope at all** — not even the mock-validated version W1-06/W3-05 described in the reference plan (§5). At ~37 hours for the mock-validated adapter alone (per §5's own estimates), it would consume roughly 30% of this plan's entire 120-hour budget to build something pilot users would never see, since it would ship flagged off regardless.

**The standing policy is recorded here now, so it is not lost by the time Genesys work resumes:**

1. **Genesys integration must ship behind a feature flag, defaulted off**, in whatever future phase builds it.
2. **The flag must not be turned on** until Genesys has supplied: (a) sandbox/test-environment access, (b) a confirmed webhook payload schema, (c) a confirmed authentication/signature mechanism (`Genesys-Integration.md` §15 items 1, 2, 3, 8 — still open).
3. **Mock-contract validation (`Genesys-Mock-Contract.md`) is not a substitute for real-schema validation** and must never be described as such — this applies whenever that work happens, not only in this pilot's timeframe. See §4.
4. Until the flag is turned on, ticket creation from a Genesys-originated call is not possible — the existing manual-fallback design (agents create tickets manually) is the only path, which is already this pilot's only path for every ticket regardless of Genesys, so there is no functional gap introduced by deferring Genesys entirely.

---

## 4. Pilot-Done vs. Production-Ready — An Explicit Distinction

**Pilot-Done** (the bar this 4-week, 1-developer plan is built to clear) means:
- Every requirement in §2's reduced scope works for the internal pilot's actual usage pattern.
- No known High-severity defect is open.
- The system has been smoke-tested post-deployment, **in a non-production environment**.
- Genesys integration does not exist in this scope at all (§3) — not "mock-validated," not "flagged off but built," simply not present. There is nothing to mis-describe as tested, because nothing has been built.

**Production-Ready** (explicitly **not** this plan's target, and **not claimed at pilot go-live, and no production deployment is authorized at this stage** — management's explicit decision, §0) would additionally require, at minimum:
- Every requirement deferred in §0.2 (SLA, escalation, priority-downgrade approval, attachment withdrawal) actually built, per the full design in `MVP-ERD.md`/`MVP-API-Contracts.md`.
- **Real Genesys sandbox validation, not mock-only** — and even once mock-contract work happens in a future phase, mock-validated must never be reported as equivalent to tested or production-ready, per management's explicit decision. This item is **BLOCKED** on Genesys supplying sandbox/schema/authentication confirmation (§3), not scheduled on any backlog.
- Confirmed retention/regulatory policy (ISSUE-016) rather than the interim 7-year default.
- Load/performance testing beyond pilot-scale usage.
- Full security review sign-off per `Security-Architecture.md` §14's testing section.
- A confirmed hosting target (ADR-0022) with production-grade infrastructure — and, separately from any technical readiness, **explicit authorization to deploy to production, which does not exist at this stage** and is a decision outside this document's authority to grant.
- Team capacity beyond 1 developer, sufficient to build everything deferred in §0.2 and named in §5's reference plan.

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
