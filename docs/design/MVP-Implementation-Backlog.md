# Tiger Group — CS Ticketing System
## MVP Implementation Backlog

| | |
|---|---|
| **Status** | Design for review — planning artifact only |
| **Scope** | Week-by-week backlog for the 3-week internal pilot MVP, built from `MVP-ERD.md`, `MVP-Data-Dictionary.md`, `MVP-API-Contracts.md`, `Genesys-Mock-Contract.md`, and `MVP-UI-Wireframes.md` |
| **Explicitly not done here** | No application code, no project scaffolding, no actual sprint/task-tracker tickets created in any external tool — this is the plan those would be created from |
| **Base** | `main` @ `4fe6f19` |
| **Related documents** | All five preceding `docs/design/*.md` documents; `docs/architecture/System-Architecture.md`, `Module-Design.md`, `SLA-Architecture.md` |
| **Date** | 2026-08-18 |

---

## 0. Team-Capacity Assumption (Governs Everything Below)

**`[ASSUMPTION — no team roster or capacity figure was provided; this document does not assume one developer can run multiple full-time parallel workstreams simultaneously, per explicit instruction]`.** This backlog assumes a **minimum viable team of 4 people** for the 3 weeks, each able to hold exactly one workstream at a time:

- **1 Backend/Architecture developer** — owns Foundation, Domain, SLA engine, Outbox/idempotency.
- **1 Backend/Integration developer** — owns CRM gateway, Genesys adapter, notifications.
- **1 Frontend developer** — owns all 20 UI screens.
- **1 QA/DevOps generalist** — owns environment setup, CI, test authoring/execution, deployment.

If the actual team is smaller, the **critical path (§3) does not compress** — fewer people means the same total effort spread over more elapsed time, or descoped items per the scope-protection rules (§5). This backlog does **not** assume any person works two workstreams concurrently at full effort; where a workstream is marked "can run in parallel," that means *relative to other workstreams*, assuming a distinct person is available for each — not that one person multitasks across them.

Estimated effort is in **ideal developer-hours** (focused, uninterrupted work) — not elapsed calendar hours, which will be higher once meetings, review cycles, and context-switching are accounted for. A 3-week pilot at ~30 productive hours/person/week gives each of the 4 roles roughly **90 ideal hours** across the pilot; the backlog below is sized to fit within that envelope per role, with contingency intentionally left thin (this is a pilot, not a production hardening pass — see §6).

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

### W1-03 — Database schema implementation (all 23 entity groups)
- **Story:** As the team, we need the schema from `MVP-ERD.md`/`MVP-Data-Dictionary.md` realized in the database.
- **Business value:** Every feature needs its tables to exist.
- **Acceptance criteria:** all entities in `MVP-Data-Dictionary.md` §2.1–2.23 exist with correct types/nullability; relationships/cardinalities from `MVP-ERD.md` §2 enforced where DB-enforceable (FKs), documented where app-enforced (filtered unique indexes, etc.).
- **Dependencies:** W1-01.
- **Estimated effort:** 14h.
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
- **Estimated effort:** 12h.
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
- **Acceptance criteria:** `POST /api/genesys/webhook` accepts the mock payload shape from `Genesys-Mock-Contract.md` §1, validates a placeholder signature header, writes a `GenesysInteractions` row and an `OutboxMessages`/`IdempotencyRecords` pair per `MVP-API-Contracts.md` §6.1.
- **Dependencies:** W1-03, W1-04.
- **Estimated effort:** 10h.
- **Risk:** Medium — explicitly built against a mock, so **must be flagged for re-verification once real Genesys details arrive** (this is not a hidden risk; it's the entire premise of `Genesys-Mock-Contract.md`).
- **Test requirements:** duplicate-event idempotency test; missing-optional-field acceptance test; signature-failure rejection test.
- **Definition of Done:** posting a mock event twice results in exactly one `GenesysInteractions` row; posting an event missing `agentEmail`/`agentExtension` succeeds.
- **Can run in parallel:** Yes, with W1-05 (same Integration developer, sequenced after it) or independently if a second integration resource exists.
- **Workstream:** Integration.

### W1-07 — Basic UI shell and routing
- **Story:** As a staff member, I need a login screen and an authenticated shell (nav, current-user display, route guards) to exist before any feature screen can be built into it.
- **Business value:** Every UI screen (2–20) depends on this shell existing.
- **Acceptance criteria:** screen 1 (Login) and the authenticated shell around screens 2+ exist; route guards redirect unauthenticated users to Login; role-based route guarding scaffold exists (even if most routes aren't built yet).
- **Dependencies:** W1-02 (needs a working login endpoint to integrate against).
- **Estimated effort:** 12h.
- **Risk:** Low.
- **Test requirements:** an end-to-end smoke test: login succeeds, shell renders, logout returns to Login.
- **Definition of Done:** a user can log in, see the shell with their name/roles, and log out.
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

### W2-01 — CRM verification flow (backend)
- **Story:** Implements `MVP-API-Contracts.md` §2.1–2.4 against the test double from W1-05.
- **Business value:** FR-VER-01–05.
- **Acceptance criteria:** unit search/lookup, contact retrieval, requester-confirmation-with-snapshot-write all function against the test double.
- **Dependencies:** W1-05, W1-03.
- **Estimated effort:** 10h. **Risk:** Medium (the immutable-snapshot write-once rule needs a real enforcement test, not just a convention).
- **Test requirements:** attempting a second requester-confirmation on the same ticket returns `409`.
- **Definition of Done:** all four endpoints pass their contract tests.
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
- **Acceptance criteria:** creates a ticket with correct `TicketNumber` format, routes to the correct department from category, opens the initial `TicketSlaInstances` row, writes seed `TicketStatusHistory` rows.
- **Dependencies:** W2-01, W1-04 (SLA instance creation needs the SLA due-date computation — see W2-06).
- **Estimated effort:** 10h. **Risk:** Medium (many moving parts converge here).
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
- **Workstream:** Architecture/Foundation.

### W2-05 — Assignment, transfer, status change (backend)
- **Story:** Implements `MVP-API-Contracts.md` §3.5–§3.7.
- **Business value:** FR-RTE-03–05, FR-TKT-11.
- **Acceptance criteria:** assignment enforces department-membership check; transfer clears assignment and preserves the immutable `OriginatingDepartmentId`; status-change enforces the state-machine transition table and triggers pause on `PendingCustomer`.
- **Dependencies:** W2-03.
- **Estimated effort:** 12h. **Risk:** Medium (status-transition validation + the pause side-effect coupling is the trickiest part).
- **Test requirements:** invalid-transition rejection test; transfer-then-verify-immutable-ID test.
- **Definition of Done:** all three endpoints pass contract tests, including the pause side-effect.
- **Can run in parallel:** Yes, with W2-04.
- **Workstream:** Architecture/Foundation.

### W2-06 — Notes and attachments (backend)
- **Story:** Implements `MVP-API-Contracts.md` §4.1–§4.6.
- **Business value:** FR-TKT-06, BR-010.
- **Acceptance criteria:** note creation; attachment upload with size/type validation and async virus-scan status; download blocked while not `Clean`; deletion respects the uploader-window/Supervisor+ policy.
- **Dependencies:** W2-03.
- **Estimated effort:** 10h. **Risk:** Medium (virus-scan integration and the never-downloadable-until-clean rule need careful testing).
- **Test requirements:** 11th-attachment rejection; download-while-pending rejection.
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
- **Estimated effort:** 24h. **Risk:** Medium (largest single frontend item this week).
- **Test requirements:** a scripted manual/E2E walkthrough of the full create-ticket happy path plus the CRM-outage fallback path.
- **Definition of Done:** an agent can search a unit, confirm a contact, create a ticket, and view its detail, entirely through the UI.
- **Can run in parallel:** Partially — screen 4/5 can start once W2-01 lands, ahead of W2-03/04.
- **Workstream:** Frontend.

### W2-09 — Assignment, transfer, notes/attachments, resolve/close UI (frontend)
- **Story:** Builds screens 8, 9, 12, 13, 14, 15.
- **Business value:** completes the agent-facing ticket lifecycle.
- **Acceptance criteria:** matches `MVP-UI-Wireframes.md` §8, §9, §12–§15's specs.
- **Dependencies:** W2-05, W2-06, W2-07 (backend), W2-08 (shares the Ticket Details shell).
- **Estimated effort:** 22h. **Risk:** Medium.
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

### W3-03 — Priority upgrade/downgrade approval flow
- **Story:** Implements `MVP-API-Contracts.md` §5.5–§5.6.
- **Business value:** FR-SLA-09, ADR-0012.
- **Acceptance criteria:** upgrade due date = earlier-of; downgrade blocked without Dept-Head+ approval; breach flags never reset.
- **Dependencies:** W3-01.
- **Estimated effort:** 10h. **Risk:** Medium (the breach-preservation invariant needs a dedicated regression test, since it's the highest-consequence rule per `MVP-ERD.md` §2.15).
- **Test requirements:** an explicit "breach flag stays true after downgrade" regression test.
- **Definition of Done:** contract tests pass, including the breach-preservation case.
- **Can run in parallel:** Yes, with W3-02.
- **Workstream:** Architecture/Foundation.

### W3-04 — Escalation engine (manual + scheduled auto-escalation)
- **Story:** Implements `MVP-API-Contracts.md` §5.7–§5.9 plus the Hangfire-driven auto-escalation job (ADR-0015).
- **Business value:** FR-ESC-01–07.
- **Acceptance criteria:** manual flag/Level 4 role-gated correctly; scheduled job advances Level 2→3 after the configured window; Level 2 auto-triggers on breach.
- **Dependencies:** W3-01 (needs due-date/breach state to trigger from).
- **Estimated effort:** 12h. **Risk:** Medium-High (scheduled-job correctness is hard to verify without a controllable clock in tests).
- **Test requirements:** a time-manipulated integration test proving the Level 2→3 auto-advance fires at the correct elapsed window, not before/after.
- **Definition of Done:** manual and automatic escalation paths both pass their tests.
- **Can run in parallel:** Yes, with W3-02/03.
- **Workstream:** Architecture/Foundation.

### W3-05 — Genesys Basic Integration (real adapter over the mock-tested foundation)
- **Story:** Complete `MVP-API-Contracts.md` §6.1–§6.6 on top of the webhook foundation built in W1-06, wiring First-Human-Response satisfaction and manual linking.
- **Business value:** the MVP's confirmed Genesys scope (ADR-0019).
- **Acceptance criteria:** call-answer events satisfy First Human Response via the same code path as §5.2; manual linking works; failed-events queue and retry work.
- **Dependencies:** W1-06, W3-02 (needs first-response recording to exist).
- **Estimated effort:** 12h. **Risk:** High — **this entire item is built and tested against `Genesys-Mock-Contract.md`, not a real Genesys sandbox** (per that document's own open questions, §15 items 1–8 are unresolved). Flag explicitly: **if real Genesys sandbox access or schema confirmation doesn't arrive during the pilot window, this item ships as "mock-validated only" and is not claimed as production-integration-tested.**
- **Test requirements:** the full mock-event battery from `Genesys-Mock-Contract.md` §4 (idempotency, out-of-order, missing-field, unknown-agent).
- **Definition of Done:** all mock-contract behaviors pass; a clear note in the pilot readout that real-schema validation is outstanding if it remains so.
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
- **Workstream:** Integration.

### W3-07 — SLA/escalation UI (screens 10, 11)
- **Story:** Builds screens 10 and 11 against W3-02/03/04's live endpoints.
- **Business value:** makes the SLA/escalation engine usable, not just API-correct.
- **Acceptance criteria:** matches `MVP-UI-Wireframes.md` §10–§11's specs, including the Critical-never-pauses disabled state and the breach-preservation notice text.
- **Dependencies:** W3-02, W3-03, W3-04.
- **Estimated effort:** 14h. **Risk:** Medium.
- **Test requirements:** scripted walkthrough of pause/resume, upgrade, downgrade-with-approval, manual escalation.
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

| Workstream | Week 1 | Week 2 | Week 3 |
|---|---|---|---|
| Architecture/Foundation | W1-01, 02, 03, 04 | W2-03, 04, 05, 07 | W3-01, 02, 03, 04 |
| Integration | W1-05, 06 | W2-01, 02, 06 | W3-05, 06 |
| Frontend | W1-07 | W2-08, 09, 10 | W3-07, 08 |
| QA/DevOps | W1-08 | W2-11 | W3-09, 11 (+10 with everyone) |

## Scope-Protection Rules

1. **No item outside `MVP-Traceability-Matrix.md`'s confirmed-MVP requirement set is started**, even if time appears available — that time goes to hardening/UAT-fix buffer (W3-10) instead, per the explicit "no future-phase feature creep" instruction running through this whole engagement.
2. **If Week 1's end-of-week gate (§2) slips, Week 2's scope is not silently compressed** — the first items dropped are, in order: W2-10's dashboard polish (basic counts can be a simpler interim view), then W3-08's admin-screen depth (departments/categories/calendar admin can launch pilot with seeded data and no live-editing UI if truly necessary, as already flagged `[ASSUMPTION]` in `MVP-UI-Wireframes.md` §17–18).
3. **W3-05 (Genesys) is the single most likely item to slip past the pilot window** given its dependency on external, currently-unconfirmed information (`Genesys-Integration.md` §15). If the real Genesys schema/sandbox isn't available in time, the fallback is: ship with the mock-contract-validated adapter behind a feature flag, and the pilot runs on **manual ticket creation only** for calls, exactly as designed for CRM/Genesys-outage handling elsewhere in this system — this is not a new fallback invented for this risk, it reuses the existing manual-fallback design.
4. **The immutable/append-only invariants (write-once snapshot, breach-flag-never-resets, append-only audit) are never descoped or shortcut**, even under time pressure — these are the items every later architecture decision depends on being correct, and are exactly the kind of thing that's expensive to retrofit after the pilot if skipped now.

---

## 6. Pilot-Done vs. Production-Ready — An Explicit Distinction

**Pilot-Done** (the bar this backlog is built to clear) means:
- Every MVP-scoped requirement in `MVP-Traceability-Matrix.md` works for the internal pilot's actual usage pattern.
- No known High-severity defect is open.
- The system has been smoke-tested post-deployment.
- Genesys integration may be mock-validated only if real-schema access didn't materialize in time (§5, rule 3) — this is an accepted, explicitly-flagged pilot-scope gap, not silently hidden.

**Production-Ready** (explicitly **not** this backlog's target, and not claimed at pilot go-live) would additionally require, at minimum:
- Real Genesys sandbox validation, not mock-only (closing every open question in `Genesys-Integration.md` §15).
- Confirmed retention/regulatory policy (ISSUE-016) rather than the interim 7-year default.
- Load/performance testing beyond pilot-scale usage.
- Full security review sign-off per `Security-Architecture.md` §14's testing section, beyond what Week 3's regression pass covers.
- A confirmed hosting target (ADR-0022) with production-grade infrastructure (backup/DR, monitoring/alerting beyond the 15-minute detection requirement's basic implementation), rather than whatever pilot-expedient hosting choice gets the 3-week window met.
- Resolution of every other open item in `docs/architecture/README.md`'s "Remaining Open Questions" section that this pilot's scope allowed to stay open.

This backlog deliberately targets the first bar, not the second, and this document should not be read as a production launch plan.

---

## 7. What This Document Does Not Cover

No actual sprint-tracker tickets, no named individual assignments (roles only, per §0's anonymized capacity model), no story-pointing/velocity tracking, no CI/CD pipeline YAML, no infrastructure-as-code. Those are Phase 3 execution-tooling concerns, built from this plan, not part of it.
