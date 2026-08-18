# Tiger Group — CS Ticketing System
## MVP Implementation Backlog

| | |
|---|---|
| **Status** | Design for review — planning artifact only |
| **Scope** | Week-by-week backlog for the 3-week internal pilot MVP, built from `MVP-ERD.md`, `MVP-Data-Dictionary.md`, `MVP-API-Contracts.md`, `Genesys-Mock-Contract.md`, and `MVP-UI-Wireframes.md` |
| **Explicitly not done here** | No application code, no project scaffolding, no actual sprint/task-tracker tickets created in any external tool — this is the plan those would be created from |
| **Base** | `main` @ `4fe6f19` |
| **Related documents** | All preceding `docs/design/*.md` documents, including `MVP-UI-Wireframes.md` and `MVP-Traceability-Matrix.md`; `docs/architecture/System-Architecture.md`, `Module-Design.md`, `SLA-Architecture.md`; `MVP-Design-Review-Findings.md` (this pass's full findings list) |
| **Date** | 2026-08-18; revised in the senior-architecture-review pass |

---

## 0. Team-Capacity Assumption (Governs Everything Below)

**`[ASSUMPTION — no team roster or capacity figure was provided; this document does not assume one developer can run multiple full-time parallel workstreams simultaneously, per explicit instruction]`.** This backlog assumes a **minimum viable team of 4 people** for the 3 weeks, each able to hold exactly one workstream at a time — **this remains unchanged from the pre-review version; the senior-architecture-review pass rebalances *which items* sit in each workstream, not the headcount:**

- **1 Backend/Architecture developer** — owns Foundation, Domain, SLA engine, Outbox/idempotency.
- **1 Backend/Integration developer** — owns CRM gateway, Genesys adapter, notifications.
- **1 Frontend developer** — owns all 20 UI screens.
- **1 QA/DevOps generalist** — owns environment setup, CI, test authoring/execution, deployment.

If the actual team is smaller, the **critical path (§3) does not compress** — fewer people means the same total effort spread over more elapsed time, or descoped items per the scope-protection rules (§5), or the reduced scope in §6 below. This backlog does **not** assume any person works two workstreams concurrently at full effort; where a workstream is marked "can run in parallel," that means *relative to other workstreams*, assuming a distinct person is available for each — not that one person multitasks across them.

Estimated effort is in **ideal developer-hours** (focused, uninterrupted work) — not elapsed calendar hours, which will be higher once meetings, review cycles, and context-switching are accounted for. A 3-week pilot at ~30 productive hours/person/week gives each of the 4 roles roughly **90 ideal hours** across the pilot.

### 0.1 Workload Summary — Hours per Role per Week (Added in the Senior-Architecture-Review Pass)

The pre-review backlog never verified its own capacity claim against its own item-level hour estimates. Doing so for the first time in this pass — and after folding in every finding-driven addition (§0.2 below) — found that **the original plan was already over capacity in its two heaviest weeks before this review started**, and that this review's own additions make a pre-existing gap somewhat worse, not a new one. The table below is the actual sum of every item's "Estimated effort" line in §2–§4, **after** the workstream rebalance and the Week-1 frontend-scaffolding front-load described in §0.3:

| Role | Week 1 | Week 2 | Week 3 | **3-week total** | **vs. 90h capacity** |
|---|---|---|---|---|---|
| Architecture/Foundation | 47h | 34h | 54h | **135h** | **+45h (+50%)** |
| Integration | 25h | 33h | 20h | **78h** | −12h (slack) |
| Frontend | 27h | 44h | 39h | **110h** | +20h (+22%) |
| QA/DevOps | 8h | 26h | 30h (+ W3-10 contingency) | **64h** | −26h (slack) |

**No further rebalancing closes the Architecture/Foundation gap** — Integration and QA/DevOps do not have enough combined slack (−12h and −26h respectively, 38h total) to absorb 45h of *architecture-specialist-level* work (SLA calculation math, Outbox/idempotency foundations, escalation scheduled jobs, priority-downgrade approval logic) without handing SLA-correctness-critical code to people this plan deliberately does not assign it to. This is disclosed here rather than resolved by force-fitting the arithmetic.

### 0.2 What Changed This Pass, and Why the Numbers Moved

Every finding from the senior-architecture-review adds real work; none of it was free:

| Item | Δ hours | Reason |
|---|---|---|
| W1-03 (schema) | +3h | 4 new entities: `VerificationSessions`, `GenesysAgentMappings`, `GenesysInteractionEvents`, `PriorityDowngradeRequests` |
| W1-04 (audit/Outbox) | +2h | Extends the idempotency pattern to per-event dedup and the generic expiry-sweep pattern |
| W1-06 (Genesys webhook foundation) | +7h | Per-event ingestion instead of per-conversation (DR-03); signature-rejection-is-security-log-only behavior (DR-04) |
| W2-01 (CRM verification) | +5h | `VerificationSessions` endpoints replace the circular confirm-by-`TicketId` design (DR-01) |
| W2-03 (ticket creation) | +2h | Consumes a `VerificationSessionId`; copies its snapshot |
| W2-06 (notes/attachments) | +2h | Withdrawal/quarantine model replaces physical delete (DR-06) |
| W2-08 (frontend) | +3h | Multi-step Verification Session UI flow |
| W3-03 (priority downgrade) | +6h | One endpoint → one entity + four endpoints, separating request from approval (DR-05) |
| W3-04 (escalation engine) | +2h | Reuses this item's scheduled-job pattern to build the new expiry sweeps |
| W3-05 (Genesys adapter) | +8h | `GenesysAgentMappings` CRUD (DR-02); completing/testing the per-event idempotency model (DR-03) |
| W3-07 (SLA/escalation UI) | +3h | Two-step downgrade request/approval UI instead of one combined form |
| **Total added** | **+43h** | Spread across Architecture/Foundation (+13h), Integration (+15h), Frontend (+6h) — plus W1-07's +15h front-load, which is a *resequencing*, not new work |

### 0.3 Capacity Actions Taken in This Pass (Before Any Further Decision Is Needed)

1. **W2-04 (Ticket read/list/detail/timeline, 10h, Low risk) moved from Architecture/Foundation to QA/DevOps** — a CRUD-shaped, low-risk item, and a reasonable fit for a generalist who already knows the schema from W1-03/W1-08.
2. **W3-06 (Email acknowledgement, 6h, Low risk) moved from Integration to QA/DevOps** — a self-contained Outbox consumer, freeing Integration capacity for the Genesys/CRM work this review added to W1-06/W2-01/W3-05.
3. **15h of static screen scaffolding (screens 8, 9, 12, 13, 14, 15) front-loaded from W2-09 into W1-07** — this was the single worst finding in this workload analysis: the pre-review plan put **56h of frontend work into a single Week 2** (97% over a 30h week), an untenable spike regardless of the 3-week total. Building these screens' static layout from `MVP-UI-Wireframes.md`'s structural specs doesn't require a live backend endpoint, so it can start in Week 1 (which had slack) instead of stacking entirely onto Week 2. This is a **resequencing**, not a scope cut — no work was removed, only moved earlier.

These three moves are why Frontend's worst single week dropped from 56h/97%-over to 44h/47%-over, and why Integration is no longer meaningfully over capacity in any week. **They do not, and cannot, close Architecture/Foundation's 135h/90h gap** — see §0.1's conclusion and the options in §0.4.

### 0.4 Remaining Capacity Gap — Options for the Sponsor, Not a Decision Made Here

Architecture/Foundation's Week 1 (47h) and Week 3 (54h) both require the same specialist for auth/schema/Outbox foundations and SLA/escalation/downgrade logic respectively — work this plan deliberately does not distribute to other roles, because getting it wrong is expensive to retrofit (see the Risk ratings on W1-04, W3-01, W3-03, W3-04). Three genuinely available options, presented without a decision on this team's behalf:

1. **Accept bounded overtime in the two peak weeks.** 47h/30h (+57%) in Week 1 and 54h/30h (+80%) in Week 3 is a real ask, but a short-lived one for a 3-week pilot — many teams absorb a crunch like this over a few weeks without it being sustainable long-term. If chosen, this should be an explicit, acknowledged decision by whoever is accountable for the Architecture/Foundation developer's workload, not something this backlog quietly assumes.
2. **Extend the pilot by 3–5 business days**, specifically to de-spike Weeks 1 and 3 back toward the 30h/week envelope, without cutting any MVP-scoped requirement.
3. **Trim scope** using the fallback approach in §6 below, scaled down from "1–2 developers" to "shave the heaviest architecture-specialist items" — e.g., deferring the *automatic* Level 2→3 escalation window advance (keeping manual escalation only, per FR-ESC-01/04, and treating FR-ESC-03's automatic advance as a fast-follow) would remove real hours from W3-04 without touching a requirement this pilot's core value proposition depends on. This is named as an option, not adopted here, since trimming a requirement already marked **MVP** in `Tiger-CS-Ticketing-Solution-Analysis.md` is a business decision, not an architecture one.

None of these three is silently chosen in this document. **This is the single largest open item this review surfaces — see the Design Review Findings document, Finding DR-08.**

---

## 1. Backlog Item Fields

Every item below carries: **Backlog ID**, user story/task, business value, acceptance criteria, dependencies, estimated effort (ideal dev-hours), Risk (High/Medium/Low), test requirements, Definition of Done, can-run-in-parallel (Yes/No), assigned workstream.

---

## 2. Week 1 — Project Foundation

### W1-01 — Solution scaffolding and module boundaries
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

### W1-02 — Authentication and authorization (Identity)
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

### W1-03 — Database schema implementation (all 27 entity groups)
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

### W1-04 — Audit and Outbox/Idempotency foundations
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

### W1-05 — CRM gateway interface and test double
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

### W1-06 — Genesys webhook foundation and mock contract wiring
- **Story:** As a developer, I need the webhook receiving endpoint and signature-validation scaffold in place, built against `Genesys-Mock-Contract.md`'s placeholder shape.
- **Business value:** De-risks Week 3's Genesys integration work by getting the plumbing (endpoint, signature placeholder, idempotency dedup, Outbox write) built and tested against the mock now, so Week 3 is substitution, not first-time construction.
- **Acceptance criteria:** `POST /api/genesys/webhook` accepts the mock payload shape from `Genesys-Mock-Contract.md` §1, validates a placeholder signature header (rejecting failures before any persistence, per Finding DR-04 — no signature-failure row is ever written), writes a `GenesysInteractionEvents` row per accepted event (not a single row per conversation, per Finding DR-03) with its `RawPayloadHash`/fallback-key computation, and creates/updates the parent `GenesysInteractions` row on an apply-if-absent basis.
- **Dependencies:** W1-03, W1-04.
- **Estimated effort:** 17h (+7h vs. the pre-review estimate — this item absorbs most of the Finding DR-03/DR-04 rework: per-event ingestion instead of per-conversation, fallback-key/hash computation, and the signature-rejection-is-security-log-only behavior).
- **Risk:** Medium — explicitly built against a mock, so **must be flagged for re-verification once real Genesys details arrive** (this is not a hidden risk; it's the entire premise of `Genesys-Mock-Contract.md`). **This item, and its Week 3 continuation (W3-05), remain BLOCKED from ever being claimed as production-integration-tested until Genesys supplies sandbox access, a confirmed payload schema, and a confirmed signature scheme — see §6 below.**
- **Test requirements:** duplicate-event idempotency test; missing-optional-field acceptance test; signature-failure rejection test.
- **Definition of Done:** posting a mock event twice results in exactly one `GenesysInteractions` row; posting an event missing `agentEmail`/`agentExtension` succeeds.
- **Can run in parallel:** Yes, with W1-05 (same Integration developer, sequenced after it) or independently if a second integration resource exists.
- **Workstream:** Integration.

### W1-07 — Basic UI shell and routing, plus front-loaded screen scaffolding
- **Story:** As a staff member, I need a login screen and an authenticated shell (nav, current-user display, route guards) to exist before any feature screen can be built into it.
- **Business value:** Every UI screen (2–20) depends on this shell existing.
- **Acceptance criteria:** screen 1 (Login) and the authenticated shell around screens 2+ exist; route guards redirect unauthenticated users to Login; role-based route guarding scaffold exists (even if most routes aren't built yet).
- **Dependencies:** W1-02 (needs a working login endpoint to integrate against).
- **Estimated effort:** 27h (+15h vs. the pre-review estimate). **Added in the senior-architecture-review capacity rebalance (§0): static layout/component scaffolding for screens 8, 9, 12, 13, 14, 15 (per `MVP-UI-Wireframes.md`'s structural specs, which don't require a working backend endpoint to lay out) is front-loaded into Week 1, since Week 2's frontend workload (originally 56h in a single week against a ~30h/week capacity) was the single worst per-week overload found in this review — see §0.** This is scaffolding only (layout regions, static fields, no live data wiring); W2-09 finishes wiring these screens to real endpoints once they exist.
- **Risk:** Low.
- **Test requirements:** an end-to-end smoke test: login succeeds, shell renders, logout returns to Login.
- **Definition of Done:** a user can log in, see the shell with their name/roles, log out, and every front-loaded screen's static layout matches its wireframe spec (no live data required yet).
- **Can run in parallel:** Yes, once W1-02 has a working (even partial) login endpoint — Frontend workstream, parallel to W1-03 through W1-06.
- **Workstream:** Frontend.

### W1-08 — CI pipeline and test infrastructure
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

---

## 3. Week 2 — Core Ticketing Workflow

### W2-01 — CRM verification flow, including Verification Sessions (backend)
- **Story:** Implements `MVP-API-Contracts.md` §2.1–2.4 against the test double from W1-05.
- **Business value:** FR-VER-01–05.
- **Acceptance criteria:** unit search/lookup, contact retrieval all function against the test double; `VerificationSessions` create/select-target/confirm/get (§2.4.1–2.4.4) enforce single-agent ownership, expiry, and the confirmed-before-consumable state machine.
- **Dependencies:** W1-05, W1-03.
- **Estimated effort:** 15h (+5h vs. the pre-review estimate — covers the `VerificationSessions` endpoints that replace the old, circular requester-confirmation-by-`TicketId` design, per Finding DR-01). **Risk:** Medium (the immutable-snapshot write-once rule and the session's single-use/expiry/ownership enforcement both need real tests, not just convention).
- **Test requirements:** attempting to consume an already-consumed or expired session returns `409`/`410` (not a second requester-confirmation on the same ticket — that endpoint no longer exists).
- **Definition of Done:** all CRM-verification and Verification-Session endpoints pass their contract tests.
- **Can run in parallel:** No (blocks W2-03).
- **Workstream:** Integration → handed to Backend/Architecture developer for the snapshot-immutability piece specifically, or same person if only one Integration developer exists.

### W2-02 — Intake Record fallback flow (backend)
- **Story:** Implements `MVP-API-Contracts.md` §2.5–2.7.
- **Business value:** FR-VER-07 — CRM outage doesn't block Critical/High intake.
- **Acceptance criteria:** create/list/promote all function; promotion links `LinkedTicketId` correctly.
- **Dependencies:** W2-01.
- **Estimated effort:** 6h. **Risk:** Low.
- **Test requirements:** promoting an already-promoted Intake Record returns `409`.
- **Definition of Done:** the outage-to-recovery flow works end-to-end against the test double's simulated-outage mode.
- **Can run in parallel:** Yes, with W2-03 once W2-01 lands.
- **Workstream:** Integration.

### W2-03 — Ticket creation (backend)
- **Story:** Implements `MVP-API-Contracts.md` §3.1.
- **Business value:** FR-TKT-01–06, FR-CLS-01–03, FR-RTE-01.
- **Acceptance criteria:** creates a ticket with correct `TicketNumber` format from a confirmed `VerificationSessionId` (not directly-supplied unit/contact fields, per Finding DR-01), routes to the correct department from category, opens the initial `TicketSlaInstances` row, writes seed `TicketStatusHistory` rows, consumes the session and copies its snapshot into `TicketRequesterSnapshots` in the same transaction.
- **Dependencies:** W2-01, W1-04 (SLA instance creation needs the SLA due-date computation — see W2-06).
- **Estimated effort:** 12h (+2h vs. the pre-review estimate — session-consumption logic and the immutable-snapshot-from-session copy). **Risk:** Medium (many moving parts converge here).
- **Test requirements:** idempotency-key replay test (no duplicate ticket created); category-to-department routing test.
- **Definition of Done:** contract tests for §3.1 pass, including the idempotency behavior.
- **Can run in parallel:** No (central dependency for the rest of Week 2).
- **Workstream:** Architecture/Foundation.

### W2-04 — Ticket read/list/detail/timeline (backend)
- **Story:** Implements `MVP-API-Contracts.md` §3.2–§3.4, §3.13.
- **Business value:** FR-TKT (queue and detail visibility), FR-ADM-03 (auditability via timeline).
- **Acceptance criteria:** list filters/sorts correctly; detail returns the full nested shape; timeline merges all five source tables correctly ordered.
- **Dependencies:** W2-03.
- **Estimated effort:** 10h. **Risk:** Low.
- **Test requirements:** timeline ordering test across mixed event types.
- **Definition of Done:** all four endpoints pass contract tests.
- **Can run in parallel:** Yes, with W2-05/06/07 once W2-03 lands.
- **Workstream:** QA/DevOps (**moved from Architecture/Foundation in the senior-architecture-review capacity rebalance, §0** — this item is CRUD-shaped and Low-risk, a reasonable fit for a generalist with the schema already in hand from W1-03/W1-08).

### W2-05 — Assignment, transfer, status change (backend)
- **Story:** Implements `MVP-API-Contracts.md` §3.5–§3.7.
- **Business value:** FR-RTE-03–05, FR-TKT-11.
- **Acceptance criteria:** assignment enforces department-membership check; transfer clears assignment and preserves the immutable `OriginatingDepartmentId`; status-change enforces the state-machine transition table and triggers pause on `PendingCustomer`.
- **Dependencies:** W2-03.
- **Estimated effort:** 12h. **Risk:** Medium (status-transition validation + the pause side-effect coupling is the trickiest part).
- **Test requirements:** invalid-transition rejection test; transfer-then-verify-immutable-ID test.
- **Definition of Done:** all three endpoints pass contract tests, including the pause side-effect.
- **Can run in parallel:** Yes, with W2-04.
- **Workstream:** Architecture/Foundation. (Considered moving this to Integration during the capacity rebalance in §0, but Integration's own Week 2 load is already near capacity without it — moving W2-04 to QA/DevOps instead was the more effective single change; see §0's workload table.)

### W2-06 — Notes and attachments (backend)
- **Story:** Implements `MVP-API-Contracts.md` §4.1–§4.6.
- **Business value:** FR-TKT-06, BR-010.
- **Acceptance criteria:** note creation; attachment upload with size/type validation and async virus-scan status; download blocked while not `Clean` or while withdrawn; **withdrawal (`IsWithdrawn`/`BlobStatus`, not physical deletion, per Finding DR-06)** respects the uploader-window/Supervisor+ policy.
- **Dependencies:** W2-03.
- **Estimated effort:** 12h (+2h vs. the pre-review estimate — the withdrawal/quarantine model replaces what was a simple `DELETE`). **Risk:** Medium (virus-scan integration and the never-downloadable-until-clean-or-withdrawn rule need careful testing).
- **Test requirements:** 11th-attachment rejection; download-while-pending rejection; download-while-withdrawn rejection; withdrawn row still present and queryable after withdrawal (regression test specifically proving no physical delete occurs).
- **Definition of Done:** all six endpoints pass contract tests.
- **Can run in parallel:** Yes, with W2-04/05.
- **Workstream:** Integration.

### W2-07 — Resolve/close workflow (backend)
- **Story:** Implements `MVP-API-Contracts.md` §3.9–§3.10, §3.12.
- **Business value:** FR-RES-01–06, FR-TKT-10.
- **Acceptance criteria:** resolve requires a non-empty note and conditional fields per outcome; close blocked without a current resolution; duplicate-flag recommend/confirm/reject state machine works.
- **Dependencies:** W2-03.
- **Estimated effort:** 10h. **Risk:** Medium.
- **Test requirements:** empty-note rejection; close-without-resolution `409`; duplicate-chain rejection.
- **Definition of Done:** all endpoints pass contract tests.
- **Can run in parallel:** Yes, with W2-04/05/06.
- **Workstream:** Architecture/Foundation (second developer slot if available, else sequenced after W2-05).

### W2-08 — CRM verification, creation, and detail UI (frontend)
- **Story:** Builds screens 4, 5, 6, 7 (partial — read-only detail first pass).
- **Business value:** the agent-facing core of the pilot.
- **Acceptance criteria:** screens match `MVP-UI-Wireframes.md` §4–§7's specs (loading/empty/error states included, not deferred).
- **Dependencies:** W2-01, W2-03, W2-04 (needs working endpoints to integrate against, not just contracts on paper).
- **Estimated effort:** 27h (+3h vs. the pre-review estimate — screens 4/5/6 now drive the multi-step Verification Session flow, per Finding DR-01, instead of a single combined confirm-and-create step). **Risk:** Medium (largest single frontend item this week).
- **Test requirements:** a scripted manual/E2E walkthrough of the full create-ticket happy path plus the CRM-outage fallback path.
- **Definition of Done:** an agent can search a unit, confirm a contact, create a ticket, and view its detail, entirely through the UI.
- **Can run in parallel:** Partially — screen 4/5 can start once W2-01 lands, ahead of W2-03/04.
- **Workstream:** Frontend.

### W2-09 — Assignment, transfer, notes/attachments, resolve/close UI (frontend)
- **Story:** Builds screens 8, 9, 12, 13, 14, 15.
- **Business value:** completes the agent-facing ticket lifecycle.
- **Acceptance criteria:** matches `MVP-UI-Wireframes.md` §8, §9, §12–§15's specs.
- **Dependencies:** W2-05, W2-06, W2-07 (backend), W2-08 (shares the Ticket Details shell), W1-07 (static scaffolding for these same screens was front-loaded there — see §0).
- **Estimated effort:** 7h (**-15h vs. the pre-review estimate** — this item is now wiring the six screens front-loaded as static scaffolding in W1-07 to their real backend endpoints, not building them from nothing). **Risk:** Medium.
- **Test requirements:** scripted walkthrough of assign → note → attach → resolve → close, and separately reopen/duplicate-flag.
- **Definition of Done:** all six screens functional against real (non-mock) backend endpoints.
- **Can run in parallel:** No relative to W2-08 (same developer, same shell) — sequenced after it starts, may overlap toward week's end.
- **Workstream:** Frontend.

### W2-10 — Ticket queue and dashboard UI, first pass (frontend)
- **Story:** Builds screens 2 (partial), 3.
- **Business value:** FR-RPT-07 (basic operational view), FR-TKT queue visibility.
- **Acceptance criteria:** matches `MVP-UI-Wireframes.md` §2–§3's specs for the pieces that don't depend on SLA data (full SLA-aware dashboard tiles land in Week 3 alongside the SLA engine).
- **Dependencies:** W2-04.
- **Estimated effort:** 10h. **Risk:** Low.
- **Test requirements:** filter/sort/pagination manual test pass.
- **Definition of Done:** queue and basic count tiles render against real data.
- **Can run in parallel:** Yes, with W2-09 if a second frontend resource exists; otherwise sequenced.
- **Workstream:** Frontend.

### W2-11 — Integration/contract test suite for Week 2 endpoints
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

---

## 4. Week 3 — SLA, Escalation, Genesys, and Pilot Readiness

### W3-01 — SLA calculation engine (business-calendar-aware due dates)
- **Story:** Implements the due-date math behind `MVP-API-Contracts.md` §5.1, using `BusinessCalendars`/`Holidays`.
- **Business value:** FR-SLA-01–04.
- **Acceptance criteria:** Critical due dates ignore the calendar (24/7); other tiers correctly exclude non-working hours/days/holidays.
- **Dependencies:** W2-03 (ticket creation must call into this at creation time — retrofitted if W2-03 stubbed it).
- **Estimated effort:** 14h. **Risk:** High — business-calendar math (especially business-hours-with-holidays) is the single most error-prone calculation in the whole system.
- **Test requirements:** worked-example tests matching `SLA-Architecture.md`'s examples exactly, including a holiday-spanning case.
- **Definition of Done:** due-date computation matches every worked example in `SLA-Architecture.md` §8.
- **Can run in parallel:** No (blocks W3-02, W3-03, W3-05).
- **Workstream:** Architecture/Foundation.

### W3-02 — SLA pause/resume and first-response recording
- **Story:** Implements `MVP-API-Contracts.md` §5.2–§5.4.
- **Business value:** FR-SLA-05, FR-RES-07, `TicketSlaPausePeriods` (§0.1 of `MVP-ERD.md`).
- **Acceptance criteria:** pause blocked for Critical; resume computes `PausedDurationMinutes` and shifts due dates correctly; first-response recording is write-once.
- **Dependencies:** W3-01.
- **Estimated effort:** 10h. **Risk:** Medium.
- **Test requirements:** Critical-pause-rejection test; pause-then-resume due-date-shift test; double-first-response `409` test.
- **Definition of Done:** all four endpoints pass contract tests.
- **Can run in parallel:** Yes, with W3-03.
- **Workstream:** Architecture/Foundation.

### W3-03 — Priority upgrade / downgrade-request-and-approval flow
- **Story:** Implements `MVP-API-Contracts.md` §5.5–§5.6.
- **Business value:** FR-SLA-09, ADR-0012.
- **Acceptance criteria:** upgrade due date = earlier-of; **downgrade is now a two-actor flow — a `PriorityDowngradeRequests` row (§5.6.1) created by the requesting Agent, decided by a Dept Head+ via a separate approve/reject action (§5.6.4/§5.6.5) whose approver identity is taken from the caller's own JWT, never a request field (Finding DR-05)**; breach flags never reset.
- **Dependencies:** W3-01.
- **Estimated effort:** 16h (+6h vs. the pre-review estimate — replaces one endpoint with a new entity and four endpoints: create request, list, approve, reject, plus the at-most-one-pending-per-ticket and expiry rules). **Risk:** Medium (the breach-preservation invariant needs a dedicated regression test, since it's the highest-consequence rule per `MVP-ERD.md` §2.15; the approver-identity-never-client-supplied rule needs its own explicit test too, since it's the specific defect this rework closes).
- **Test requirements:** an explicit "breach flag stays true after downgrade" regression test; an explicit test that an approve/reject call ignores any approver-identity field in the request body and uses only the authenticated caller; a duplicate-pending-request rejection test.
- **Definition of Done:** contract tests pass, including the breach-preservation case.
- **Can run in parallel:** Yes, with W3-02.
- **Workstream:** Architecture/Foundation.

### W3-04 — Escalation engine (manual + scheduled auto-escalation)
- **Story:** Implements `MVP-API-Contracts.md` §5.7–§5.9 plus the Hangfire-driven auto-escalation job (ADR-0015).
- **Business value:** FR-ESC-01–07.
- **Acceptance criteria:** manual flag/Level 4 role-gated correctly; scheduled job advances Level 2→3 after the configured window; Level 2 auto-triggers on breach.
- **Dependencies:** W3-01 (needs due-date/breach state to trigger from).
- **Estimated effort:** 14h (+2h vs. the pre-review estimate — this item's Hangfire scheduled-job pattern is reused to build the `VerificationSessions`/`PriorityDowngradeRequests` expiry sweeps, per Finding DR-01/DR-05, rather than building that pattern a third time elsewhere). **Risk:** Medium-High (scheduled-job correctness is hard to verify without a controllable clock in tests).
- **Test requirements:** a time-manipulated integration test proving the Level 2→3 auto-advance fires at the correct elapsed window, not before/after.
- **Definition of Done:** manual and automatic escalation paths both pass their tests.
- **Can run in parallel:** Yes, with W3-02/03.
- **Workstream:** Architecture/Foundation.

### W3-05 — Genesys Basic Integration (real adapter over the mock-tested foundation) — **BLOCKED for real-sandbox validation**
- **Story:** Complete `MVP-API-Contracts.md` §6.1–§6.6 on top of the webhook foundation built in W1-06, wiring First-Human-Response satisfaction, manual linking, and `GenesysAgentMappings` CRUD (Finding DR-02).
- **Business value:** the MVP's confirmed Genesys scope (ADR-0019).
- **Acceptance criteria:** call-answer events satisfy First Human Response via the same code path as §5.2; manual linking works; failed-events queue and retry work at the per-event grain (Finding DR-03); `GenesysAgentMappings` upsert/deactivate (§6.6.1/§6.6.2) function.
- **Dependencies:** W1-06, W3-02 (needs first-response recording to exist).
- **Estimated effort:** 20h (+8h vs. the pre-review estimate: +2h for `GenesysAgentMappings` CRUD, Finding DR-02; +6h to complete and test the per-event idempotency/dedup model, Finding DR-03, beyond what W1-06 already built). **Risk:** High, and explicitly **BLOCKED, not merely risky**: **this entire item is built and tested against `Genesys-Mock-Contract.md`, not a real Genesys sandbox** (per that document's own open questions, §15 items 1–8 are unresolved — signature scheme, real event schema, delivery guarantees, and sandbox availability are all still unconfirmed as of this review). **Real-schema/real-sandbox integration testing cannot begin until Genesys supplies: (1) sandbox or test-environment access, (2) confirmed payload/event-type schema, (3) confirmed signature/authentication scheme.** None of those three are within this team's control, and none should be treated as "in progress" on this backlog — they are an external dependency, tracked, not scheduled.
- **Test requirements:** the full mock-event battery from `Genesys-Mock-Contract.md` §4 (idempotency via both the preferred and fallback key paths, out-of-order, missing-field, unknown-agent, signature-rejection-produces-no-persisted-row).
- **Definition of Done:** all mock-contract behaviors pass. **Explicitly NOT part of this item's Definition of Done: any claim of real-Genesys-integration correctness.** The pilot readout must state plainly that Genesys integration is "mock-validated only, pending Genesys-team sandbox/schema/security confirmation" — this phrasing, or one equally unambiguous, is itself a required deliverable of this item, not optional framing.
- **Can run in parallel:** Yes, with W3-04 (different developer).
- **Workstream:** Integration.

### W3-06 — Email acknowledgement notification
- **Story:** Implements FR-NOT-01 — the automated acknowledgement email on ticket creation.
- **Business value:** first customer-facing touchpoint of the whole system.
- **Acceptance criteria:** email attempted for every ticket via the Outbox; content matches the required fields (ticket number, expected response time, department, Geyness reference); explicitly does not set `FirstHumanResponseAtUtc`.
- **Dependencies:** W1-04, W2-03.
- **Estimated effort:** 6h. **Risk:** Low.
- **Test requirements:** a test asserting the ack email never touches `FirstHumanResponseAtUtc`.
- **Definition of Done:** ack email fires and is retryable via Outbox on transient failure.
- **Can run in parallel:** Yes, with W3-01 through W3-05.
- **Workstream:** QA/DevOps (**moved from Integration in the senior-architecture-review capacity rebalance, §0** — a self-contained, Low-risk Outbox consumer, and Integration's Week 3 load grew once W3-05 absorbed the Genesys event-model rework).

### W3-07 — SLA/escalation UI (screens 10, 11)
- **Story:** Builds screens 10 and 11 against W3-02/03/04's live endpoints.
- **Business value:** makes the SLA/escalation engine usable, not just API-correct.
- **Acceptance criteria:** matches `MVP-UI-Wireframes.md` §10–§11's specs, including the Critical-never-pauses disabled state, the breach-preservation notice text, and **the two-step downgrade request/approval UI (request form for Agents, a separate pending-inbox and approve/reject action for Dept Head+, per Finding DR-05 — no approver-selection field anywhere in the request form)**.
- **Dependencies:** W3-02, W3-03, W3-04.
- **Estimated effort:** 17h (+3h vs. the pre-review estimate — the downgrade flow is now two screens/states instead of one combined form). **Risk:** Medium.
- **Test requirements:** scripted walkthrough of pause/resume, upgrade, downgrade-request-then-separate-approval, manual escalation.
- **Definition of Done:** both screens functional against real backend.
- **Can run in parallel:** Yes, with W3-08.
- **Workstream:** Frontend.

### W3-08 — Genesys panel, admin screens, dashboard completion (screens 16–20, 2 finish)
- **Story:** Builds the remaining admin/operational screens and completes the dashboard's SLA-aware tiles.
- **Business value:** completes System Administrator/Supervisor+ operational capability and the day-one basic dashboard (FR-RPT-07).
- **Acceptance criteria:** matches `MVP-UI-Wireframes.md` §16–§20 and the SLA-dependent parts of §2.
- **Dependencies:** W3-01 through W3-05, W2-10.
- **Estimated effort:** 22h. **Risk:** Medium (largest single remaining frontend item; a natural item to trim first if time runs short — see §5).
- **Test requirements:** scripted walkthrough of each admin screen's primary action; dashboard tile accuracy spot-check against known seed data.
- **Definition of Done:** all six remaining screens functional.
- **Can run in parallel:** Partially — screens 16/17/18 (admin) don't depend on SLA/Genesys work and could start earlier if frontend capacity allows; 19/20 and the dashboard's SLA tiles do depend on Weeks 3's backend items.
- **Workstream:** Frontend.

### W3-09 — Full regression pass and integration testing
- **Story:** As QA, exercise the entire ticket lifecycle end-to-end, including SLA/escalation/Genesys paths, before UAT.
- **Business value:** catches integration-level defects that per-endpoint unit/contract tests miss.
- **Acceptance criteria:** every scripted walkthrough referenced across W2/W3 items passes as one continuous run; the traceability matrix's test-scenario column (`MVP-Traceability-Matrix.md`) is spot-checked against actual behavior, not just endpoint existence.
- **Dependencies:** everything above.
- **Estimated effort:** 16h. **Risk:** Medium.
- **Test requirements:** N/A (this item is the test pass).
- **Definition of Done:** a documented pass/fail log against the full requirement list; any failure triaged into UAT-fix work (W3-10) rather than silently left.
- **Can run in parallel:** No (needs everything else substantially complete) — the last major QA gate before UAT.
- **Workstream:** QA/DevOps.

### W3-10 — UAT support and fixes
- **Story:** As the team, support pilot users during UAT and fix defects found.
- **Business value:** the actual point of a pilot — real usage surfaces what tests don't.
- **Acceptance criteria:** a defect triage list is maintained; High-severity defects fixed before go-live, Medium/Low triaged into a post-pilot backlog if time is short (see Pilot-Done vs. Production-Ready, §6).
- **Dependencies:** W3-09.
- **Estimated effort:** remaining time in the week (contingency buffer — deliberately not fully allocated above).
- **Risk:** Medium (unknown until UAT happens).
- **Test requirements:** each fix gets a regression test added, not just a manual verification.
- **Definition of Done:** no known High-severity defect remains open at pilot go-live.
- **Can run in parallel:** N/A (whole team, reactive).
- **Workstream:** All.

### W3-11 — Pilot deployment
- **Story:** As the team, deploy the pilot build to its target environment.
- **Business value:** the actual delivery milestone.
- **Acceptance criteria:** deployment is repeatable (scripted, not manual click-ops); rollback path exists; hosting target per ADR-0022 (`[ASSUMPTION]`, still open per `docs/architecture/README.md`'s open-questions list — flagged again here since it directly affects this item's exact steps).
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

---

## 5. Critical Path

The single unbroken chain that determines the pilot's minimum possible duration, assuming ample parallel capacity elsewhere:

**W1-01 → W1-03 → W1-04 → W2-03 → W2-05 (status/pause coupling) → W3-01 → W3-02/03/04 (any one) → W3-09 → W3-10 → W3-11.**

Every other item either feeds into this chain, or branches off it in parallel (notably: the entire Frontend workstream, the Genesys integration line W1-06→W3-05, and the CRM/Intake line W1-05→W2-01→W2-02, none of which are on the critical path but all of which must still finish before W3-09's full regression pass, since that pass covers the whole system).

## Parallel Workstreams (Summary)

**Updated for the senior-architecture-review rebalance (§0.3):**

| Workstream | Week 1 | Week 2 | Week 3 |
|---|---|---|---|
| Architecture/Foundation | W1-01, 02, 03, 04 | W2-03, 05, 07 | W3-01, 02, 03, 04 |
| Integration | W1-05, 06 | W2-01, 02, 06 | W3-05 |
| Frontend | W1-07 (incl. front-loaded screen scaffolding) | W2-08, 09, 10 | W3-07, 08 |
| QA/DevOps | W1-08 | W2-04, 11 | W3-06, 09, 11 (+10 with everyone) |

## Scope-Protection Rules

1. **No item outside `MVP-Traceability-Matrix.md`'s confirmed-MVP requirement set is started**, even if time appears available — that time goes to hardening/UAT-fix buffer (W3-10) instead, per the explicit "no future-phase feature creep" instruction running through this whole engagement.
2. **If Week 1's end-of-week gate (§2) slips, Week 2's scope is not silently compressed** — the first items dropped are, in order: W2-10's dashboard polish (basic counts can be a simpler interim view), then W3-08's admin-screen depth (departments/categories/calendar admin can launch pilot with seeded data and no live-editing UI if truly necessary, as already flagged `[ASSUMPTION]` in `MVP-UI-Wireframes.md` §17–18). **Invoking this rule fully absorbs Frontend's remaining §0.1 overage** (44h → 34h in Week 2, ~13% over instead of ~47%) — this is the concrete mechanism behind that number, not a separate claim.
3. **W3-05 (Genesys) is BLOCKED for real-sandbox validation, not merely "the single most likely item to slip."** This review pass makes this explicit rather than a soft risk note: real Genesys integration testing **cannot begin** — not "is unlikely to finish," but categorically cannot start — until Genesys supplies sandbox access, a confirmed payload schema, and a confirmed signature scheme (`Genesys-Integration.md` §15 items 1, 2, 3, 8). Until then, the pilot ships with the mock-contract-validated adapter behind a feature flag, and falls back to **manual ticket creation only** for calls, exactly as designed for CRM/Genesys-outage handling elsewhere in this system. **Mock-validated must never be reported, in any pilot readout, status update, or go-live sign-off, as equivalent to "Genesys integration tested" or "production-ready" — see §7's explicit distinction.**
4. **The immutable/append-only invariants (write-once snapshot, breach-flag-never-resets, append-only audit) are never descoped or shortcut**, even under time pressure — these are the items every later architecture decision depends on being correct, and are exactly the kind of thing that's expensive to retrofit after the pilot if skipped now.
5. **The Architecture/Foundation capacity gap identified in §0.4 (135h vs. 90h) is not resolved by any rule in this list** — rules 1–4 address Frontend's and Genesys's risks specifically; Architecture/Foundation's gap requires a sponsor decision among §0.4's three options, or acceptance of the bounded-overtime risk it names.

---

## 6. Fallback Scope for a 1–2 Developer Team

**Added in the senior-architecture-review pass, per explicit instruction.** The 4-person model above (§0) is this backlog's primary plan. If the real team is smaller, **the critical path does not compress** (§5) — the only honest response is to cut scope, not to compress the same work into fewer people's hours. This section defines what "MVP" shrinks to at each smaller team size, so a 1–2 person team has a concrete, pre-thought-through target instead of an ad hoc, under-pressure improvisation.

### 6.1 One Developer

A single developer cannot deliver the full MVP scope in 3 weeks under any realistic estimate — the 4-person plan alone totals ~390 ideal hours (135+78+110+64, before slack) against one person's ~90-hour budget. A 1-developer "pilot" is realistically a **4–6 week walking skeleton**, not a 3-week MVP, covering only:

- Core ticket lifecycle: create → assign → status change → resolve → close (§3.1, §3.5, §3.7, §3.9, §3.10 of `MVP-API-Contracts.md`), using the five-dimension model (ADR-0008) since collapsing it would be a bigger rework later, not a saving now.
- A **simplified** Verification Sessions flow: single-step (no separate select-target/confirm calls — one endpoint does lookup-and-confirm together), since the circular-dependency defect this review fixes (Finding DR-01) still must not be reintroduced even at reduced scope.
- Manual priority field only — **no SLA due-date computation, no business-calendar math, no pause/resume, no escalation, no breach detection.** `TicketSlaInstances` still exists (so the schema doesn't need reworking later) but its due-date fields are simply not computed or enforced.
- **No Genesys integration of any kind, mock or real** — fully manual ticket creation only.
- **No priority-downgrade approval workflow** — priority is agent-editable directly, with no downgrade-approval gate at all (a business-rule reduction, flagged, not silently applied — this removes ADR-0012's protection and must be called out to the sponsor as a real behavior change, not just a schedule one).
- Attachments: upload + basic virus-scan status only; withdrawal/quarantine (Finding DR-06) deferred — a withdrawn-attachment concept doesn't need to exist yet if nothing has been uploaded long enough to need withdrawing in a walking skeleton.
- A minimal UI: queue, create, detail, resolve/close only (roughly screens 3, 6, 7, 13, 14 of `MVP-UI-Wireframes.md`) — no dashboard, no admin screens, no SLA/escalation panel.
- No notifications (no Outbox-driven email acknowledgement) — deferred entirely.

### 6.2 Two Developers

Adds back the items most load-bearing for a genuine pilot, still without Genesys or full escalation automation:

- Everything in §6.1, plus:
- **Basic SLA due-date tracking**: business-calendar-aware `FirstResponseDueAtUtc`/`ResolutionDueAtUtc` computation (W3-01's core), a warning threshold, and breach detection — but **no pause/resume** (`TicketSlaPausePeriods` deferred) and **no automatic escalation** (manual `ManualFlag`/`ManualLevel4` escalation only, per FR-ESC-01/04 — FR-ESC-02/03's automatic Level 2/3 advance deferred).
- **Priority upgrade only** (earlier-of-due-dates, ADR-0012's upgrade half) — downgrade approval (Finding DR-05's full request/approve/reject flow) deferred to a fast-follow, since it's the most process-heavy addition in this review and adds the least value at pilot scale compared to the core lifecycle.
- Basic email acknowledgement via the Outbox pattern (worth doing correctly even at reduced scope, since it's foundational and cheap once Outbox exists for other reasons).
- Notes and read-only attachment listing; upload with virus-scan status; withdrawal still deferred, same reasoning as §6.1.
- A slightly fuller UI: adds the SLA summary (a simpler version of screen 11, without pause/escalation actions), assignment/transfer (screens 8/9), notes/attachments (screen 12).
- **Still no Genesys integration, mock or real** — the mock-contract validation work (Finding DR-03's per-event model) is substantial enough on its own that it isn't worth attempting below the 4-person model; a 2-developer team's time is better spent hardening the core lifecycle.

**Timeline for either fallback:** `[ASSUMPTION]` roughly 4–6 weeks (1 developer) or 3–4 weeks (2 developers) for the scope above — not the original 3-week window, since even reduced scope needs the same foundational Week-1 work (auth, schema, Outbox) that the 4-person plan's Week 1 already shows taking a full week for one specialist alone.

---

## 7. Pilot-Done vs. Production-Ready — An Explicit Distinction

**Pilot-Done** (the bar this backlog is built to clear) means:
- Every MVP-scoped requirement in `MVP-Traceability-Matrix.md` works for the internal pilot's actual usage pattern.
- No known High-severity defect is open.
- The system has been smoke-tested post-deployment.
- Genesys integration is mock-validated only (Scope-Protection Rule 3) — this is an accepted, explicitly-flagged pilot-scope gap, not silently hidden, and **"mock-validated" is never a synonym for "tested" in any go-live communication.**

**Production-Ready** (explicitly **not** this backlog's target, and not claimed at pilot go-live) would additionally require, at minimum:
- **Real Genesys sandbox validation, not mock-only — closing every open question in `Genesys-Integration.md` §15, and specifically re-running the full `Genesys-Mock-Contract.md` §4 test battery against the real schema/signature scheme once confirmed, not assuming the mock's results transfer.** This item is currently **BLOCKED** on an external party (the Genesys integration team), not scheduled on this backlog — see W3-05 and Scope-Protection Rule 3.
- Confirmed retention/regulatory policy (ISSUE-016) rather than the interim 7-year default.
- Load/performance testing beyond pilot-scale usage.
- Full security review sign-off per `Security-Architecture.md` §14's testing section, beyond what Week 3's regression pass covers.
- A confirmed hosting target (ADR-0022) with production-grade infrastructure (backup/DR, monitoring/alerting beyond the 15-minute detection requirement's basic implementation), rather than whatever pilot-expedient hosting choice gets the 3-week window met.
- Resolution of every other open item in `docs/architecture/README.md`'s "Remaining Open Questions" section that this pilot's scope allowed to stay open.
- **Resolution of the Architecture/Foundation capacity gap (§0.4)** via one of its three named options, formally decided rather than absorbed as unplanned overtime indefinitely.

This backlog deliberately targets the first bar, not the second, and this document should not be read as a production launch plan.

---

## 8. What This Document Does Not Cover

No actual sprint-tracker tickets, no named individual assignments (roles only, per §0's anonymized capacity model), no story-pointing/velocity tracking, no CI/CD pipeline YAML, no infrastructure-as-code. Those are Phase 3 execution-tooling concerns, built from this plan, not part of it.
