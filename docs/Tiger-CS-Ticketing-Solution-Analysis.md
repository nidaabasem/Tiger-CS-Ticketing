# Tiger Group — Customer Service Ticketing System
## Solution Analysis Document

| | |
|---|---|
| **Prepared for** | Tiger Group — Transformation, Marketing & Growth Directorate |
| **Prepared by** | Solution Architecture Review (AI-assisted, human-reviewed) |
| **Status** | **Approved for Architecture Design** — management has reviewed and approved the MVP decisions (Section 13 Group A + Group C); ADRs are being prepared per that approval. Application code, ERD, SQL schema, EF migrations, and API implementation remain unauthorized until a further, explicit go-ahead. |
| **Version** | 2.1 — final correction pass following management review of the decision documents |
| **Date** | 2026-08-17 |
| **Primary source** | `Tiger_CS_Ticketing_System_Requirements.pdf` (Tiger Group, v1.0, June 2026) — "Powered by Geyness Call Center" |
| **Secondary source** | `tiger_cs_ticketing_workflow.png` — visual workflow reference |
| **Proposed stack** | ASP.NET Core 8 (Web API + MVC/Razor Pages), SQL Server, EF Core, ASP.NET Core Identity, Hangfire, SignalR, xUnit |

> **Scope reminder:** This document is analysis only. No application code, project scaffolding, database schema, or repository structure has been created. Section 10 describes architecture conceptually, as instructed.

---

## Summary of Changes in This Revision

A senior architecture review of v1.0 raised 13 points. Every point is addressed below; nothing from the review has been softened or partially applied.

| # | Review point | What changed | Where |
|---|---|---|---|
| 1 | Define what satisfies First Response SLA | New Critical decision **ISSUE-019**; recommendation = first genuine human response, excluding automated acknowledgement | §6, §7, §9, §13 |
| 2 | Ticket-number format on transfer | New decision **ISSUE-020**; ticket ID is immutable, `DEPT` segment reflects the *initial* routing department only | §6 (BR-004), §9, §13 |
| 3 | Fix Unit–Contact inconsistency | Domain model corrected: CRM is sole source of truth; ticketing system stores CRM identifiers + an immutable ticket-time snapshot, never a mastered customer database | §10.3 |
| 4 | Don't assume a Customer Portal | New decision **ISSUE-021**; all self-service/portal capability removed from MVP and from the permission matrix by default | §3 (matrix), §9, §13, §15 |
| 5 | Redesign the lifecycle into independent dimensions | Section 5 fully rewritten into `TicketStatus` / `VerificationStatus` / `EscalationLevel` / `SlaState` / `ResolutionOutcome` | §5 |
| 6 | Decide who Resolves vs. Closes vs. Reopens/Cancels/Rejects | New decision **ISSUE-022**; permission matrix revised — department employees resolve, CS agents/managers close | §3, §5, §9, §13 |
| 7 | Fix undefined priority-change SLA algorithm | New decision **ISSUE-023**; proportional carry-forward removed, replaced with a configurable policy, full SLA history retention, and mandatory approval to downgrade Critical/High | §6 (BR-019), §7.5, §9, §13 |
| 8 | Revise SLA architecture | Explicit due-timestamp columns, scheduled deadline jobs, sweep-as-safety-net-only, idempotency, Transactional Outbox | §10.5 |
| 9 | Revise SignalR usage | Server publishes state/deadline *changes* only; clients compute the visible countdown locally from the due timestamp | §10.5 |
| 10 | Add reliability patterns to integrations | New §10.7 (Outbox, idempotency keys, correlation IDs, retry policy, dead-letter handling) applied across all integrations and notifications | §8, §10.7 |
| 11 | Split scope into MVP / Phase 2 / Phase 3 | Every FR, integration, and phase re-tagged; Section 15 rewritten to the prescribed three-tier scope | §2, §8, §11, §15 |
| 12 | Update estimates/dependencies for the reduced MVP | Section 11 rewritten around the smaller MVP, with two new release phases for Phase 2 and Phase 3 scope | §11 |
| 13 | Update decisions list and assumptions register | Section 13 and 14 rewritten; 5 new decisions added, all 18 original items re-bucketed against the new MVP boundary | §13, §14 |

### Version 2.1 — Final Correction Pass

A further management review of the decision documents raised 11 corrections, applied throughout this version:

| # | Correction | What changed | Where |
|---|---|---|---|
| 1 | Remove ISSUE-005 as a separate retry-count decision | Merged into ISSUE-013; escalation progression is now time-based and priority-based, never a count of retry cycles | §2 (FR-ESC-07), §9, §13, §14 |
| 2 | ISSUE-018 priority Low → High | SLA pause behavior directly affects contractual SLA compliance | §9, §13 |
| 3 | Revise ISSUE-023 | Never erases elapsed time/breach/history; explicit upgrade rule (earlier-of-due-dates) and downgrade rule (Department Head approval, breach preserved) | §2 (FR-SLA-09), §6 (BR-019), §7.5, §7.9, §9 |
| 4 | Rewrite ISSUE-007 for a system without a Customer Portal | New questions on contact authorization, notification recipients, tenant/owner cross-visibility, representative verification; portal visibility kept as a separate concern (ISSUE-021) | §6 (BR-030), §9, §13 |
| 5 | ISSUE-016 ownership and timing | Owner: Legal/Compliance; required before production go-live, not described as safe to defer | §9, §13, §14 |
| 6 | ISSUE-012 ownership split | Business owner: Customer Service or HR; technical administrator: System Administrator | §9, §13, §14 |
| 7–9 | Companion documents | The Management Decisions document is retitled the Technical Decision Register with neutral language and a sign-off table; a new, MVP-only Executive Decisions document is added | `Tiger-CS-Ticketing-Management-Decisions.md`, `Tiger-CS-Ticketing-Executive-Decisions.md` |
| 11 | Update counts | 22 total items (17 original + 5 new), 16 in Group A | §9, §11, §13 |

---

## 0. Source Reconciliation Note

The PDF (13 sections, including its own end‑to‑end workflow diagram on page 14) is the primary source. The standalone PNG workflow image is materially **the same diagram** as PDF §13, with only cosmetic label differences. No content conflict exists between the two diagrams themselves.

The real conflicts are **between the PDF's diagram and the PDF's own prose/tables** (§1–§12), consolidated in Section 9. The architecture review (this revision) adds further precision on top of those conflicts — several open items are not contradictions in the source at all, but simply gaps the source never addressed (e.g., what exactly satisfies "first response," whether a customer portal is in scope). Those are now tracked as their own decisions rather than folded into the contradiction list, so management can tell the two kinds of open item apart.

Also: the task brief that originally commissioned this analysis referred to a **"Genesys Call Center"** integration, while the source PDF and both diagrams consistently used **"Geyness."** **This is now resolved** — an explicit management directive confirms the platform is Genesys and authorizes a basic Genesys integration within MVP (see ISSUE-003's updated entry in Section 9, ADR-0019 in `docs/architecture/adr/`, and `docs/architecture/Genesys-Integration.md`). What remains open is not the vendor identity, but specific technical details of Genesys's API/webhook contract, tracked as open questions for the Genesys team rather than a management decision.

---

## 1. Executive Summary

Tiger Group is commissioning a unified Customer Service Ticketing System sitting behind Geyness Call Center, serving Real Estate Developer, Leasing, and Facility Management. The source requirements are unusually well specified for SLA tiers, escalation, CSAT mechanics, and reporting cadence — but this revision, following senior architecture review, has materially changed two things relative to v1.0: **the scope that ships first**, and **the internal model used to represent a ticket's lifecycle**.

**Scope.** v1.0 proposed shipping nearly everything (all five channels, CSAT, full KPI dashboard, all integrations) as MVP. The architecture review correctly identified this as over-scoped: several of those features depend on decisions that are not yet made (channel-verification timing, vendor identity, customer-portal policy), and bundling them into MVP means the riskiest, least-defined parts of the system are also the ones on the critical path to a first release. This revision adopts a three-tier scope instead:

- **MVP** — an *internal* web application: Geyness/Tiger agents manually create and manage tickets by phone, with full classification, routing, assignment, the complete lifecycle/SLA/escalation engine, notes/attachments, audit trail, email acknowledgement, and a basic operational dashboard. No customer-facing digital channel, no customer portal, no CSAT, no SMS, and no external call-center platform integration ship in MVP.
- **Phase 2** — the customer-facing and vendor-integration expansion: SMS, CSAT, the full contractual reporting suite, Website/mobile intake, WhatsApp, and the Genesys/Geyness call-center platform integration.
- **Phase 3** — Kiosk, social media, AI-assisted features, and advanced KPI/root-cause analytics.

This is a smaller, safer first release: it proves out the hardest, most contractually load-bearing part of the system (SLA correctness, escalation, audit) against a controlled input channel (a human agent typing, not five different automated intake surfaces), before adding customer-facing complexity on top of a model that already works.

**Lifecycle model.** v1.0's ticket status model conflated several independent concerns into one status field — verification, escalation, and resolution outcome were all trying to be represented as values of a single "status." The review correctly flagged this as unsustainable (e.g., "a ticket may remain In Progress while escalated" cannot be expressed in a single-status model without an awkward "Escalated" status that loses the underlying In Progress state). Section 5 now models the ticket as five independent dimensions — `TicketStatus`, `VerificationStatus`, `EscalationLevel`, `SlaState`, and `ResolutionOutcome` — each changing independently and each auditable independently. This is a materially better foundation for the SLA/escalation engine and does not require guessing at additional statuses to cover real scenarios.

**Five new Critical/High decisions** are added by this revision (Sections 9/13): what event satisfies First Response SLA; how the ticket-ID format behaves on department transfer; whether a customer portal is in scope at all; who is authorized to Resolve vs. Close vs. Reopen/Cancel/Reject; and the policy governing SLA behavior on a priority change. Combined with the reduced MVP boundary, several of the original 18 open items (notably auto-ticket channel verification timing and the Geyness/Genesys vendor question) no longer block MVP at all — they now gate Phase 2, because the features they concern are no longer in the first release.

**Architecture.** The recommendation remains a **modular monolith** on .NET 8. This revision adds specific reliability engineering the original architecture omitted: SLA due-dates are stored as explicit timestamps and enforced with scheduled deadline jobs (not solely a periodic sweep), all cross-boundary writes use a Transactional Outbox with idempotency keys, and SignalR is used for state-change notification only — never as a per-second countdown broadcast, which the client now computes locally from the due timestamp it already has.

---

## 2. Functional Requirements

Tier values are now **MVP**, **Phase 2**, or **Phase 3**, replacing v1.0's MVP/Future binary, per review point 11. Every requirement traces to a PDF section unless marked **[ASSUMPTION]**.

### 2.1 Module: Channel Intake — `FR-CH-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-CH-01 | Geyness/Tiger agents create tickets manually for phone contacts, following the verify-then-create sequence (§3). | • Ticket-creation form requires a completed CRM verification step before it can be submitted • No ticket exists until the agent explicitly creates it | §2, §3 | **MVP** |
| FR-CH-02 | The system shall eventually accept ticket-originating contact from App/Website, Social Media DM, WhatsApp/Live Chat, and Face-to-Face kiosk, in addition to Phone. | • Channel is a fixed, extensible enum, so later channels are additive, not a rework | §2, §4 | Channel-dependent — see rows below |
| FR-CH-03 | App/Website and WhatsApp/Live Chat shall auto-create a ticket on submission. | • Ticket record created within [ASSUMPTION] 5 seconds of gateway submission • Verification-timing conflict tracked at **ISSUE-002**, which must be resolved before this ships | §2 | **Phase 2** |
| FR-CH-04 | Face-to-Face kiosk shall present a Tiger Group-branded on-screen form submitting directly to the ticketing system. | • Kiosk UI reviewed/approved by Tiger IT before deployment | §2, §11 | **Phase 3** |
| FR-CH-05 | WhatsApp/Live Chat messages shall auto-route to an available agent's queue. | • Queue assignment logged with agent ID and timestamp | §2 | **Phase 2** |
| FR-CH-06 | Social Media DMs (Instagram, LinkedIn, Facebook) shall be monitored and manually converted to tickets by Geyness agents. | • Each supported platform has a documented monitoring surface | §2 | **Phase 3** |

### 2.2 Module: Customer & Unit Verification — `FR-VER-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-VER-01 | Unit/room number is the sole primary lookup key for any customer record. Agents must not accept name or phone number as the primary lookup key. | • CRM lookup UI has no name/phone-only search path for ticket creation | §1 (Core Rule) | **MVP** |
| FR-VER-02 | Agent must ask for the unit/room number, pull the CRM record, and confirm the match before proceeding. | • Ticket-creation form disabled until CRM match = true • "No match found" forces an escalate-to-supervisor action | §3 Step 02 | **MVP** |
| FR-VER-03 | Agent must read back name, property, tower, and unit type from the CRM record before proceeding. | • Read-back fields displayed on agent screen • Agent must confirm before continuing | §3 Step 03 | **MVP** |
| FR-VER-04 | Where a unit record lists multiple contacts, the agent must identify which specific contact is on the line, not just the unit. | • Ticket stores a `contact_id` alongside `unit_id` • See **ISSUE-007** for the access-scoping policy this depends on | Diagram + [ASSUMPTION] | **MVP** |
| FR-VER-05 | **[Corrected — review point 3]** The ticketing system never mirrors or masters CRM customer/unit data. Every ticket stores the CRM-issued `unit_id`/`contact_id` plus an **immutable snapshot** (name, property, tower, unit type, contact details) captured at ticket-creation time — a point-in-time record for audit and read-back, not a live-synced copy. | • Ticket table has no editable "customer name" field independent of the CRM • Snapshot fields are written once at creation and never updated by later CRM changes • See §10.3 | [ASSUMPTION] correcting v1.0's implied local mastering | **MVP** |
| FR-VER-06 | Auto-ticket channels (Phase 2/3) shall still resolve the submitter to a CRM unit/contact record before the ticket becomes department-visible. | • No ticket persists in "confirmed" state without a resolved snapshot | [ASSUMPTION] | **Phase 2** |
| FR-VER-07 | CRM downtime shall not silently block Critical/High ticket intake. | • Provisional record can be created with `VerificationStatus = PendingCrmVerification`, escalated to supervisor if not reconciled within 15 minutes (§11) | [ASSUMPTION] | **MVP** (CRM integration is in MVP; see **ISSUE-006**) |

### 2.3 Module: Ticketing Engine — `FR-TKT-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-TKT-01 | Every ticket has an auto-generated unique ID in format `TG-[DEPT]-[YYYYMMDD]-[SEQ]`. | • ID generated server-side, never client-supplied • Unique index enforced | §4 | **MVP** |
| FR-TKT-02 | Unit reference is the mandatory primary key field, resolved against CRM before the ticket reaches `Open`. | • Ticket cannot reach `Open` without a resolved unit snapshot (FR-VER-05) | §4 | **MVP** |
| FR-TKT-03 | Timestamp of creation is auto-stamped and non-editable. | • `CreatedAtUtc` set server-side; no path can modify it | §4 | **MVP** |
| FR-TKT-04 | Channel is tagged from a fixed enum. | • Enum enforced at the API boundary | §4 | **MVP** |
| FR-TKT-05 | Agent ID auto-links to the authenticated identity. | • Populated from the authentication context, never user-entered | §4 | **MVP** |
| FR-TKT-06 | Ticket carries a free-text Request Summary and up to 10 attachments, stored against the ticket (referencing the unit by ID, not by a mastered copy). | • 11th upload rejected • Every attachment virus-scanned before storage [ASSUMPTION: 25MB/file cap] | §3 Step 04, §4 | **MVP** |
| FR-TKT-07 | Every state change across all five lifecycle dimensions (Section 5) is timestamped and attributed to the acting user or system process (audit trail). | • Every mutating action produces an immutable audit record with actor, action, before/after value, correlation ID (§10.7), timestamp | §4 | **MVP** |
| FR-TKT-08 | **[Redesigned — review point 5]** Ticket state is represented by five independent dimensions, not one status field: `TicketStatus`, `VerificationStatus`, `EscalationLevel`, `SlaState`, `ResolutionOutcome`. See Section 5 for full definitions. | • State machine enforces only the transitions defined in Section 5, per dimension • A ticket can be `TicketStatus = In Progress` and `EscalationLevel = Level2` simultaneously | §4 + Section 5 (redesigned) | **MVP** |
| FR-TKT-09 | A ticket with `VerificationStatus != Verified` is not visible to any department queue, does not start its SLA clock, and does not receive a final customer-facing ticket number. | • No department-visible ticket exists with an unverified snapshot | [ASSUMPTION] resolving **ISSUE-002** (applies from Phase 2 onward, when auto-ticket channels exist) | **MVP** (mechanism); **Phase 2** (first channel it actually matters for) |
| FR-TKT-10 | Resolution requires a mandatory free-text note before `ResolutionOutcome` can be set. | • `Resolve` action is disabled in UI/API until note is non-empty | §4, §8 | **MVP** |
| FR-TKT-11 | **[New — review point 2]** The `[DEPT]` segment of the ticket ID reflects the **department that originally created and routed the ticket**, and never changes — including after a department transfer (FR-RTE-04). The ticket ID itself is immutable for the life of the ticket. | • Transferring a ticket from FM to Leasing does not alter its `TG-FM-...` ID • Current owning department is a separate, mutable field (`CurrentDepartment`), distinct from the immutable ID • See **ISSUE-020** | [ASSUMPTION — recommendation per architecture review] | **MVP** |

### 2.4 Module: Classification & Priority — `FR-CLS-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-CLS-01 | Agent selects exactly one primary category: Sales Enquiry, Leasing, Facility Management, Complaint, General Information. | • Category is a single-select, mandatory field | §3 Step 05 | **MVP** |
| FR-CLS-02 | FM tickets require a mandatory sub-category. | • Sub-category only appears/required when category = Facility Management | §3 Step 05 | **MVP** |
| FR-CLS-03 | Agent sets priority: Critical, High, Medium, Low, per the defined criteria. | • Priority is mandatory, single-select, with inline agent guidance | §3 Step 06 | **MVP** |
| FR-CLS-04 | Priority may additionally be auto-suggested by keyword triggers on the request summary text. | • Suggestion is advisory only; every override logged | §4 | **AI-assisted / Phase 3** |

### 2.5 Module: Routing & Assignment — `FR-RTE-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-RTE-01 | System auto-routes a ticket to Real Estate, Leasing, or FM based on category/sub-category, via a data-driven mapping table. | • Routing table configurable without a deployment | §3 Step 07, §5 | **MVP** |
| FR-RTE-02 | Agent verbally confirms routing and reads the ticket number to the customer before ending a phone interaction. | • UI surfaces routed department + ticket number for read-back | §3 Step 07 | **MVP** |
| FR-RTE-03 | Ticket is assigned to a named staff member (`CurrentOwner`); SLA clock start point is governed by **ISSUE-001**, not by this action. | • Every ticket beyond `PendingCrmVerification`/routing has exactly one current owner • Ownership changes audited | Diagram + §4 (conflict resolved by **ISSUE-001**) | **MVP** |
| FR-RTE-04 | A ticket can be transferred between departments; `CurrentDepartment` changes, the immutable ticket ID (FR-TKT-11) does not. | • Transfer requires reason code + note • Audited with from/to department, actor, timestamp | [ASSUMPTION] + **ISSUE-010**, **ISSUE-020** | **MVP** |
| FR-RTE-05 | A ticket can be reassigned to a different owner within the same department. | • Reassignment logged | [ASSUMPTION] | **MVP** |

### 2.6 Module: SLA & Timer Engine — `FR-SLA-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-SLA-01 | Each ticket has a first-response and resolution SLA target based on priority tier, per Section 7. | • Targets are configuration, not code | §6 | **MVP** |
| FR-SLA-02 | Critical-priority SLA timers run 24/7 (calendar time). | • Timer calculation ignores business-hours/weekend/holiday calendar for Critical | §6 | **MVP** |
| FR-SLA-03 | High/Medium/Low SLA timers run only during business hours and pause outside that window, including weekends and public holidays. | • Timer excludes non-business intervals from elapsed-time calculation | §6 | **MVP** |
| FR-SLA-04 | **[Revised — review point 8/9]** Each ticket stores explicit `FirstResponseDueAt` and `ResolutionDueAt` timestamps, computed at creation and recalculated on any priority change. Clients render the live countdown by computing locally against the server-provided due timestamp; the server does not broadcast a per-second tick. | • `Due*At` columns exist and are queryable independent of any background job • SignalR payload on ticket load/update contains the due timestamp, not a countdown value | §4 ("countdown visible") + review points 8/9 | **MVP** |
| FR-SLA-05 | **[New — review point 1]** `FirstResponseDueAt` is satisfied only by the timestamp of the **first genuine, human-authored response sent to the customer** — the automated channel acknowledgement (FR-NOT-01) does **not** satisfy it. | • System stores `FirstHumanResponseAt` distinct from `AcknowledgementSentAt` • SLA compliance reporting uses `FirstHumanResponseAt` • See **ISSUE-019** | [ASSUMPTION — recommendation per architecture review] | **MVP** |
| FR-SLA-06 | System raises a warning before SLA breach. | • Warning fires at [ASSUMPTION] 75% of resolution-target elapsed time • Visually distinct from breach | [ASSUMPTION] | **MVP** |
| FR-SLA-07 | SLA breach triggers the priority-specific alert defined in Section 7. | • Alert recipients match Section 7 exactly • Logged as a notification event via the Outbox (§10.7), retryable | §6, §7 | **MVP** |
| FR-SLA-08 | Reassignment or department transfer does not, by itself, reset or pause the SLA clock unless explicitly configured per priority tier. | • Config flag per tier controls reset-on-transfer behavior; default = no reset | [ASSUMPTION] | **MVP** |
| FR-SLA-09 | **[Revised — final correction pass]** A priority change never erases elapsed time, an existing breach, or the original SLA history. **Upgrade:** the new `Due*At` is the earlier of (a) the due date already in effect and (b) the due date freshly computed under the higher tier from the change moment. **Downgrade:** requires Department-Head-or-above approval before it takes effect; any breach already recorded under the prior tier is never removed or reversed; recalculated due dates apply only from the approval moment forward. In both cases the prior SLA period is closed and archived in `SlaHistory`, never overwritten, and management reporting shows both the original and the changed period. | • `SlaHistory` never deletes/overwrites a prior period, including recorded breaches • A pending downgrade from Critical/High is blocked until an approval record exists • Upgrade due date is provably the minimum of the two candidate dates • See **ISSUE-023** | [ASSUMPTION] | **MVP** |

### 2.7 Module: Notifications — `FR-NOT-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-NOT-01 | On ticket creation/routing, system sends an automated **email** acknowledgement with ticket number, expected response time, assigned department, and Geyness reference. SMS/WhatsApp acknowledgement is Phase 2 (review point 11). | • Email attempted for every ticket • Content fields match spec • Does **not** satisfy FR-SLA-05 | §3 Step 08 | **MVP** (email only); **Phase 2** (SMS/WhatsApp) |
| FR-NOT-02 | SLA breach notifications route to the recipients defined in Section 7, via [ASSUMPTION] email + in-app for MVP; SMS for Critical is Phase 2. | • Recipient/channel matrix configurable per tier | §6, §7 | **MVP** (email/in-app); **Phase 2** (SMS) |
| FR-NOT-03 | CSAT survey is auto-sent via SMS and email on ticket closure. | • Trigger fires once per genuine closure | §4, §8 | **Phase 2** |
| FR-NOT-04 | Low CSAT (average score < 3.0) triggers an alert to the Geyness Account Manager and Tiger Group CS Manager within 24 hours. | • Event-driven on survey submission | §8 | **Phase 2** |
| FR-NOT-05 | All notification sends/failures are logged, correlation-tracked, and retryable via the Transactional Outbox pattern (§10.7). | • Failed sends visible in an operational queue with a dead-letter path after exhausted retries | [ASSUMPTION] + review point 10 | **MVP** |

### 2.8 Module: Escalation — `FR-ESC-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-ESC-01 | Level 1 (Agent) can manually flag a ticket for escalation. | • Flag action available on any owned ticket, requires a reason note | §7 | **MVP** |
| FR-ESC-02 | Level 2 (Department Head) escalation triggers automatically on SLA breach or agent flag; Dept Head must respond within 2 hours. | • Escalation event has its own tracked 2h response clock | §7 | **MVP** |
| FR-ESC-03 | Level 3 (GM) triggers if Level 2 does not resolve within the configured escalation window (**ISSUE-013**). | • Window is configurable, not hardcoded | §7 + [ASSUMPTION] | **MVP** |
| FR-ESC-04 | Level 4 (Chairman/CEO) is manual-only, never system-triggered. | • Only specific roles can invoke it | §7 | **MVP** |
| FR-ESC-05 | Every `EscalationLevel` change is logged with full audit trail. | • Escalation history queryable per ticket/unit | §7 | **MVP** |
| FR-ESC-06 | `EscalationLevel` is fully independent of `TicketStatus`: an escalated ticket continues to be actively worked (`TicketStatus = In Progress`) rather than parked in a separate "Escalated" status. | • A ticket can simultaneously show `In Progress` + `Level2` | Diagram + Section 5 redesign | **MVP** |
| FR-ESC-07 | **[Revised — final correction pass]** Escalation progression from Level 2 to Level 3 is time-based and priority-based, not based on a count of re-assign-and-retry cycles: a configurable window per priority tier determines how long Level 2 has before `EscalationLevel` auto-advances. | • Window is configurable per priority tier, not a fixed retry count • See **ISSUE-013** | Diagram + [ASSUMPTION] resolving **ISSUE-013** (merged former ISSUE-005) | **MVP** |

### 2.9 Module: Resolution & Closure — `FR-RES-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-RES-01 | **[Revised — review point 6]** A **Department Employee** (or Department Head) sets `ResolutionOutcome = Resolved` with a mandatory resolution note. This action does **not** close the ticket. | • `Resolve` transitions `TicketStatus` toward `Resolved`; ticket remains open for closure | §4, §8 + review point 6 | **MVP** |
| FR-RES-02 | **[Revised — review point 6]** Only a **Geyness Agent, Supervisor, or CS Manager** may transition `TicketStatus` to `Closed`, and only after confirming the customer has been notified of the resolution. | • `Close` action is a distinct, separately-permissioned action from `Resolve` • Guarded server-side: cannot close without `ResolutionOutcome` set and notification confirmed | §8 + review point 6 | **MVP** |
| FR-RES-03 | Resolution note is permanently retained against the ticket and unit reference, even after archival. | • Note remains queryable via unit/ticket history after retention/archival events | §8 | **MVP** |
| FR-RES-04 | **[Revised — review point 5]** Reopening a `Closed` ticket is a **domain event**, not a status value: it transitions `TicketStatus` from `Closed` back to `In Progress`, increments a `ReopenCount`, and preserves the prior `ResolutionOutcome` as history rather than deleting it. | • Reopen action available only within the confirmed window (**ISSUE-011**) • Full history of prior outcome retained | Section 5 redesign + [ASSUMPTION] | **MVP** |
| FR-RES-05 | A ticket can reach `Closed` with `ResolutionOutcome = Cancelled` (customer withdraws) or `Rejected` (invalid/duplicate/out of scope) instead of `Resolved`. | • Both require a reason code • Neither triggers CSAT (Phase 2) | Section 5 redesign + [ASSUMPTION] | **MVP** |
| FR-RES-06 | **[Revised — review point 5]** `ResolutionOutcome = Duplicate` **requires** a `DuplicateOfTicketId` pointing to the original ticket; the original ticket's own lifecycle is unaffected. | • `Duplicate` outcome cannot be set without a valid, existing `DuplicateOfTicketId` | Section 5 redesign + [ASSUMPTION] | **MVP** |
| FR-RES-07 | A ticket can enter `TicketStatus = Pending Third-Party` (waiting on an external actor), distinct from `Pending Customer`. | • Requires a note naming the third party and expected date | [ASSUMPTION] | **MVP** |

### 2.10 Module: CSAT — `FR-CSAT-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-CSAT-01 | 5-question survey (Speed, Professionalism, Resolution, Communication, Overall), 1–5 scale, optional comment. | • Schema matches exactly | §8 | **Phase 2** |
| FR-CSAT-02 | Responses stored against the ticket and unit reference. | • Retrievable per unit, ticket, department, agent | §8 | **Phase 2** |
| FR-CSAT-03 | Average score below 3.0 triggers the low-CSAT alert (FR-NOT-04). | • Threshold configurable | §8 | **Phase 2** |

### 2.11 Module: Reporting — `FR-RPT-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-RPT-01 | Daily Flash Report (opened/closed counts, open-by-priority, Critical status, SLA breaches). | • Content matches §9 field list | §9 | **Phase 2** |
| FR-RPT-02 | Weekly Performance Report (channel/department volumes, SLA compliance, top issues/buildings, escalation count, CSAT, agent performance). | • All listed fields present | §9 | **Phase 2** |
| FR-RPT-03 | Monthly Management Report (MoM trends, scorecard, root-cause categories, headcount). | • Generated on schedule | §9 | **Phase 2** |
| FR-RPT-04 | Ad Hoc/Incident Report within 4 hours of any Critical ticket. | • Triggered automatically on Critical ticket creation | §10 | **Phase 2** |
| FR-RPT-05 | All reports generated from ticketing-system data with no manual manipulation. | • Deterministic, auditable generation job | §9 | **Phase 2** |
| FR-RPT-06 | Tiger Group retains full read/export access to underlying data at all times. | • Export capability is always available, independent of report generation | §11 | **MVP** (basic export of raw ticket data; formatted reports are Phase 2) |
| FR-RPT-07 | **[New]** A basic operational view (open ticket counts by status/priority/department, current SLA backlog, current escalation counts) is available to internal roles from day one. | • No CSAT- or channel-mix-dependent metric required to render this view | Review point 11 ("basic operational dashboard") | **MVP** |

### 2.12 Module: KPI Dashboard — `FR-KPI-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-KPI-01 | Live dashboard showing the full 10-metric set from §10 (First Contact Resolution, response/resolution time, SLA compliance, backlog, CSAT, escalation rate, repeat contact, channel mix, agent utilisation). | • Every metric present with target/threshold | §10 | **Phase 2** (CSAT- and channel-mix-dependent metrics cannot exist until those features do) |
| FR-KPI-02 | Threshold-breach visual flagging on each KPI. | • Visually distinct from normal state | §10 | **Phase 2** |
| FR-KPI-03 | Repeat Contact Rate ships as a provisional metric pending definition (**ISSUE-014**). | • Labeled "provisional" until definition confirmed | [ASSUMPTION] | **Phase 3** |
| FR-KPI-04 | **[New]** Advanced KPI and root-cause analytics (clustering, trend explanation) beyond the raw metric values. | • Output reviewed/edited by CS Manager before distribution | Review point 11 | **Phase 3** |

### 2.13 Module: Administration, Roles & Audit — `FR-ADM-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-ADM-01 | Role-based access control across all roles in Section 3, backed by ASP.NET Core Identity. | • Every permission-matrix cell enforced server-side | §11 | **MVP** |
| FR-ADM-02 | Agent access is revoked within 24 hours of staff departure. | • Deactivation workflow SLA-tracked | §11 | **MVP** |
| FR-ADM-03 | Full audit trail of all five lifecycle dimensions, notes, escalations, exports, and admin actions. | • Append-only, queryable by ticket/unit/user/date range | §4, §7, §11 | **MVP** |
| FR-ADM-04 | Tiger Group data is exclusively Tiger Group's property; the technical access boundary enforces this, not just the contract. | • No Geyness export path outside the contracted workspace | §11 | **MVP** |
| FR-ADM-05 | Full data export on demand, delivered within 24 hours of request. | • Background export job for large exports | §12 | **MVP** |
| FR-ADM-06 | System/integration downtime is detected and reported within 15 minutes. | • Health checks + alerting pipeline | §11, §12 | **MVP** |
| FR-ADM-07 | **[New — review point 4]** No customer-facing authentication, self-service portal, or customer login capability exists unless **ISSUE-021** is explicitly approved. | • No customer-facing login endpoint exists in MVP • Permission matrix (§3) reflects this by default | Review point 4 | **MVP boundary (exclusion)** |

### 2.14 Module: AI-Assisted Features — `FR-AI-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-AI-01 | Keyword-based priority suggestion evolving into a trained classifier. | • Advisory only; overrides logged | §4 (extrapolated) | **Phase 3** |
| FR-AI-02 | Predictive SLA-breach risk scoring. | • Explainable, not a black box | [ASSUMPTION] | **Phase 3** |
| FR-AI-03 | Chatbot/virtual agent for common Low-priority requests on Digital/WhatsApp channels. | • Seamless handoff preserving context | [ASSUMPTION] | **Phase 3** |
| FR-AI-04 | Root-cause clustering for the Monthly Report's root-cause field. | • Reviewed by CS Manager before publication | §9 | **Phase 3** |

---

## 3. Non-Functional Requirements

Unchanged from v1.0 except NFR-SEC-05 (kiosk endpoint hardening — now explicitly Phase 3-scoped) and the addition of NFR-REL (reliability patterns, review point 10).

| ID | Category | Requirement | Source |
|---|---|---|---|
| NFR-SEC-01 | Security | All customer data encrypted at rest and in transit. | §11 |
| NFR-SEC-02 | Security | Role-based access enforced server-side on every endpoint. | §11 |
| NFR-SEC-03 | Security | Agent access revoked ≤ 24h from staff departure; access reviews logged. | §11 |
| NFR-SEC-04 | Security | Geyness technically cannot export/use Tiger data outside the contracted workspace. | §11 |
| NFR-SEC-05 | Security | Kiosk/public-facing endpoints hardened against injection/spoofing (relevant from Phase 3, when the kiosk ships). | [ASSUMPTION] |
| NFR-PERF-01 | Performance | CRM unit-number lookup returns within a real-time-feeling window; target [ASSUMPTION] p95 < 1.5s. | §11 + [ASSUMPTION] |
| NFR-PERF-02 | Performance | Dashboard/agent UI reflects state changes within [ASSUMPTION] 10 seconds via SignalR, using the change-event model in §10.5 (not a per-second broadcast). | §4, §10 + review point 9 |
| NFR-SCALE-01 | Scalability | Architecture must not hard-code assumptions blocking horizontal scaling of the API tier, even though MVP volume is modest (internal-only, phone channel). | [ASSUMPTION] |
| NFR-AVAIL-01 | Availability | Ticketing system and CRM integration maintain ≥ 99.5% uptime; planned maintenance communicated 48h in advance. | §11 |
| NFR-AVAIL-02 | Availability | CRM downtime escalated within 15 minutes. | §11 |
| NFR-AUDIT-01 | Auditability | Every change across all five lifecycle dimensions is attributed, timestamped, and immutable once written. | §4, §7, §11 |
| NFR-RETAIN-01 | Data retention | All records retained ≥ 7 years (exact statute unconfirmed — **ISSUE-016**). | §11 |
| NFR-BCDR-01 | Backup/Recovery | Full daily backup; RPO 24 hours; RTO 4 hours. | §11 |
| NFR-A11Y-01 | Accessibility | Internal agent UI (MVP) should meet WCAG 2.1 AA at minimum; customer-facing surfaces (Phase 2/3) are held to the same bar once built. | [ASSUMPTION] |
| NFR-MON-01 | Monitoring | Integration/system downtime detected and alerted within 15 minutes. | §11, §12 |
| NFR-MON-02 | Monitoring | KPI alert thresholds monitored continuously once the relevant metric exists (Phase 2/3). | §10 |
| NFR-LOG-01 | Logging | Structured logging sufficient to reconstruct any SLA/escalation calculation for audit or dispute. | [ASSUMPTION] |
| NFR-REL-01 | **Reliability (new — review point 10)** | All cross-boundary state changes (domain event → notification, domain event → integration call) use the Transactional Outbox pattern; nothing is dispatched from application code directly inside a request handler without first being durably recorded in the same transaction as the state change. | Review point 10 |
| NFR-REL-02 | **Reliability (new)** | Every outbound message/job carries an idempotency key derived from a stable business fact (e.g., `TicketId + EventType + EventVersion`), so retries and duplicate sweep/scheduled-job overlaps never produce a duplicate customer-facing notification. | Review point 10 |
| NFR-REL-03 | **Reliability (new)** | Every request/event carries a correlation ID propagated through logs, the Outbox, and any downstream integration call, so any customer-facing notification or SLA calculation can be traced end-to-end. | Review point 10 |
| NFR-REL-04 | **Reliability (new)** | Outbound integrations (SMS/Email/WhatsApp/CRM/etc.) use a defined retry policy with backoff, and move to a dead-letter queue for manual review after retries are exhausted — no failed send is ever silently dropped. | Review point 10 |
| NFR-UAE-01 | UAE data/business-time | Business-hour SLA calculation uses configurable data (not a hardcoded constant), pending confirmation of Sat–Thu vs. Sat–Sun (**ISSUE-017**). | §6 |
| NFR-UAE-02 | UAE data/business-time | UAE public holiday calendar pauses non-Critical SLA clocks; maintained as configurable reference data (**ISSUE-012**). | [ASSUMPTION] |
| NFR-UAE-03 | UAE data/business-time | Consider UAE data-residency expectations for customer PII when selecting SQL Server hosting region. | [ASSUMPTION] |

---

## 4. User Roles and Permissions

Roles are unchanged from v1.0, **except the Customer role**, which is materially reduced per review point 4 (no assumed portal), and the Resolve/Close split, which changes several role capabilities per review point 6.

| Role | Source |
|---|---|
| Geyness Agent | §3, §7 (Level 1) |
| Supervisor | §3 Step 02, §6 |
| Department Employee | Diagram ("Assign Ticket to Owner") — role name **[ASSUMPTION]** |
| Department Head | §7 (Level 2) |
| CS Manager (Tiger Group) | §9, §12 |
| General Manager | §7 (Level 3), §6 |
| Chairman/CEO | §7 (Level 4) |
| System Administrator | **[ASSUMPTION]** |
| Reporting User | **[ASSUMPTION]** |
| Customer | **Revised** — see below |

### 4.1 Permission Matrix (revised)

`V`=View `C`=Create `E`=Edit `A`=Assign `T`=Transfer `Esc`=Escalate `Res`=Resolve `Cl`=Close `Reo`=Reopen `Cn`=Cancel `Rj`=Reject `Ex`=Export `Adm`=Admin

| Role | V | C | E | A | T | Esc | Res | Cl | Reo | Cn | Rj | Ex | Adm |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Geyness Agent | Own dept queue | ✔ (from phone call) | Own tickets | — | — | ✔ (flag L1) | Own tickets (simple Info/Sales handled end-to-end) | **✔** (after customer notified) | **✔** (on customer contact) | ✔ | — | — | — |
| Supervisor | Team queue | ✔ | Team tickets | ✔ (within team) | — | ✔ | ✔ | **✔** | **✔** | ✔ | — | Team reports | — |
| Department Employee | Own/assigned tickets | — | Own tickets | — | Request only | ✔ | **✔** (marks work done) | — *(not permitted — see ISSUE-022)* | — *(request only — must go via CS)* | ✔ | ✔ | — | — |
| Department Head | All dept tickets | — | All dept tickets | ✔ | ✔ (approve) | ✔ | ✔ | — *(not primary channel — see ISSUE-022)* | ✔ (escalated cases) | ✔ | ✔ | Dept reports | Dept config (routing rules) |
| CS Manager | All tickets, all depts | — | All tickets | ✔ | ✔ | ✔ | — | **✔** (general/override) | ✔ | ✔ | ✔ | All reports | User/role assignment |
| General Manager | All tickets | — | — | — | — | ✔ (receive L3, initiate L4) | — | — | — | — | — | All reports/dashboard | — |
| Chairman/CEO | All tickets (read) | — | — | — | — | ✔ (receive L4 only) | — | — | — | — | — | Executive reports/dashboard | — |
| System Administrator | All (technical) | — | — | — | — | — | — | — | — | — | — | All | ✔ Full |
| Reporting User | Reports/dashboard only | — | — | — | — | — | — | — | — | — | — | ✔ | — |
| Customer | **None (MVP)** | **None (MVP)** | — | — | — | — | — | — | **None (MVP)** | — | — | **None (MVP)** | — |

**Customer row note (review point 4):** In MVP, the Customer has **no direct system access at all** — no login, no self-service ticket creation, no self-service view, no self-service reopen. All customer interaction is via a live agent (phone) or an outbound email notification. This is a deliberate reduction from v1.0, which implicitly assumed a customer portal. See **ISSUE-021** — if management approves authenticated customer self-service, this row (and the corresponding Website/App/Kiosk channel FRs) will need Create/View/Export/Reopen capabilities added, scoped strictly to the authenticated customer's own unit/tickets.

**Resolve/Close note (review point 6):** Department Employee and Department Head can `Resolve` (mark the underlying work done) but not `Close` (finalize the ticket) — closure is reserved for the CS layer (Geyness Agent, Supervisor, CS Manager) who confirm the customer has actually been notified, per §8's closure criteria. This enforces separation of duties: the department confirms the work; CS confirms the customer knows. See **ISSUE-022**.

---

## 5. Ticket Lifecycle *(redesigned — review point 5)*

v1.0 used a single, conflated status field that could not represent "escalated but still being actively worked," and treated Reopen and Duplicate as if they were ordinary statuses rather than what they actually are — an event and an outcome, respectively. This revision models the ticket as **five independent dimensions**, each with its own value set and transition rules, changing independently of the others.

### 5.1 `TicketStatus` — the ticket's primary workflow state

| Value | Meaning |
|---|---|
| Open | Created and verified, not yet actioned by department |
| In Progress | Department actively working the request |
| Pending Customer | Waiting on the customer |
| Pending Third-Party | Waiting on an external party (contractor, DEWA, legal, etc.) |
| Resolved | Department has completed the work (`ResolutionOutcome` set) but the ticket is not yet closed |
| Closed | Resolution complete AND customer notification confirmed (§8) |

**No "Escalated" status.** Escalation is tracked entirely by `EscalationLevel` (5.3) — a ticket can be `In Progress` and `Level2` at the same time, exactly as the review specified.

**No "Reopened" status.** Reopening is a domain event: `Closed → In Progress`, incrementing `ReopenCount` and preserving the prior `ResolutionOutcome` as a historical record rather than a live value.

### 5.2 `VerificationStatus` — independent of `TicketStatus`

| Value | Meaning |
|---|---|
| Unverified | No CRM match attempted/found yet |
| PendingCrmVerification | Provisional record awaiting CRM reconciliation (auto-ticket channels, or CRM downtime fallback) |
| Verified | CRM unit/contact match confirmed; snapshot captured (FR-VER-05) |

Only `Verified` tickets are department-visible, SLA-clocked, and issued a final ticket number (resolves **ISSUE-002** for the channels it applies to).

### 5.3 `EscalationLevel` — independent of `TicketStatus`

| Value | Meaning |
|---|---|
| None | No escalation active |
| Level1 | Agent flagged, attempting first-contact resolution |
| Level2 | Department Head, auto-triggered on breach or agent flag |
| Level3 | General Manager |
| Level4 | Chairman/CEO (manual-only) |

`EscalationLevel` can be non-`None` while `TicketStatus` is `In Progress`, `Pending Customer`, or `Pending Third-Party` — escalation reflects *oversight*, not a pause in work.

### 5.4 `SlaState` — independent of `TicketStatus`

| Value | Meaning |
|---|---|
| Running | Clock actively counting against `FirstResponseDueAt`/`ResolutionDueAt` |
| Paused | Clock frozen (e.g., `Pending Customer`/`Pending Third-Party`, per **ISSUE-018**) |
| Met | Target achieved before its due timestamp |
| Breached | Due timestamp passed without the corresponding event |
| NotApplicable | Set once `ResolutionOutcome` is `Cancelled`, `Rejected`, or `Duplicate` — the resolution SLA no longer applies, though whatever `SlaState` existed for First Response at the time of cancellation is retained historically |

### 5.5 `ResolutionOutcome` — set once, at/before the transition to `Resolved`/`Closed`

| Value | Meaning | Required data |
|---|---|---|
| Resolved | Genuine resolution of the request | Resolution note (FR-TKT-10) |
| Cancelled | Customer withdrew the request | Reason code |
| Rejected | Invalid, out of scope, or determined not genuine | Reason code |
| Duplicate | Same issue as an existing ticket | **`DuplicateOfTicketId`, mandatory** — points to the original ticket, which is unaffected |

On **Reopen**, the prior `ResolutionOutcome` is archived (not deleted) and the field returns to unset until the ticket is resolved/closed again.

### 5.6 Transition Rules by Dimension

| Dimension | Transition | Allowed Roles | Required Fields / Validation |
|---|---|---|---|
| VerificationStatus | (none) → Unverified/PendingCrmVerification | Auto-system, Geyness Agent | Channel-dependent |
| VerificationStatus | PendingCrmVerification → Verified | Auto-system (on CRM reconciliation), Geyness Agent (manual) | CRM match confirmed; snapshot captured |
| TicketStatus | (none) → Open | Geyness Agent, Auto-system | `VerificationStatus = Verified` |
| TicketStatus | Open → In Progress | Department Employee, Dept Head | Ticket owner assigned |
| TicketStatus | In Progress ↔ Pending Customer | Department Employee | Note on what is awaited |
| TicketStatus | In Progress ↔ Pending Third-Party | Department Employee | Note naming third party + expected date |
| TicketStatus | In Progress/Pending * → Resolved | **Department Employee, Department Head** | Resolution note; `ResolutionOutcome` set |
| TicketStatus | Resolved → Closed | **Geyness Agent, Supervisor, CS Manager only** | Customer notification confirmed (§8) |
| TicketStatus | Closed → In Progress (Reopen event) | **Geyness Agent, Supervisor, CS Manager** (on customer contact) | Within reopen window (**ISSUE-011**); `ReopenCount` incremented |
| TicketStatus | Open/In Progress → Closed, `ResolutionOutcome = Cancelled` | Geyness Agent, Department Employee, Supervisor, Dept Head | Reason code |
| TicketStatus | Open → Closed, `ResolutionOutcome = Rejected` | Department Employee, Department Head | Reason code |
| TicketStatus | Open → Closed, `ResolutionOutcome = Duplicate` | Geyness Agent, Department Employee | Mandatory `DuplicateOfTicketId` |
| EscalationLevel | None → Level1 | Geyness Agent | Reason note |
| EscalationLevel | Level1 → Level2 | Auto-system (SLA breach or flag) | — |
| EscalationLevel | Level2 → Level3 | Auto-system (window elapsed, **ISSUE-013**) or manual by CS Manager | — |
| EscalationLevel | * → Level4 | CS Manager, GM only, **manual-only, never automatic** | — |
| EscalationLevel | Any → None | Auto-system on `TicketStatus → Resolved` | — |

All transitions marked **[ASSUMPTION]**-extended beyond the PDF's literal six statuses remain assumptions pending the decisions in Section 9/13 — the model above is the recommended shape, not a confirmed decision.

---

## 6. Business Rules

Deterministic system rules are numbered `BR-###`. Rules flagged **(AI-assisted)** are advisory only.

| ID | Rule | Source |
|---|---|---|
| BR-001 | Unit/room number is the only valid primary identifier for customer lookup. | §1 |
| BR-002 | No ticket reaches a department-visible state without `VerificationStatus = Verified`. | §1, §3 |
| BR-003 | Agent must read back name/property/tower/unit type and receive customer confirmation before proceeding. | §3 Step 03 |
| BR-004 | **[Revised — review point 2]** Ticket ID format is `TG-[DEPT]-[YYYYMMDD]-[SEQ]`, server-generated. **The ID is immutable for the life of the ticket. `[DEPT]` always reflects the department that originally created and routed the ticket, never the current owning department.** Current ownership is tracked separately (`CurrentDepartment`), and is what changes on transfer. | §4 + **ISSUE-020** | 
| BR-005 | Exactly one primary category per ticket; FM requires a mandatory sub-category. | §3 Step 05 |
| BR-006 | Priority is one of Critical/High/Medium/Low per the defined criteria. | §3 |
| BR-007 | **(AI-assisted)** Keyword triggers may suggest a priority; the agent's manual selection is the rule of record. | §4 |
| BR-008 | Department routing is derived deterministically via a maintained mapping table. | §3 Step 07, §5 |
| BR-009 | Email acknowledgement is mandatory on every ticket (MVP); SMS/WhatsApp acknowledgement is Phase 2. | §3 Step 08 |
| BR-010 | Up to 10 attachments per ticket. | §4 |
| BR-011 | Resolution note is mandatory before `ResolutionOutcome` is set. | §4, §8 |
| BR-012 | **[Revised — review point 6]** Closure (`TicketStatus → Closed`) requires (a) `ResolutionOutcome` already set AND (b) confirmed customer notification, AND (c) is performed only by a Geyness Agent, Supervisor, or CS Manager — never by the Department Employee/Head who resolved it. | §8 + **ISSUE-022** |
| BR-013 | CSAT survey auto-sends on Closed with `ResolutionOutcome = Resolved` (Phase 2). | §8 |
| BR-014 | Average CSAT < 3.0 auto-alerts within 24h (Phase 2). | §8 |
| BR-015 | Every change to any of the five lifecycle dimensions is attributed and timestamped. | §4 |
| BR-016 | Escalation levels 1–4 follow the fixed hierarchy in §7; Level 4 is manual-only. | §7 |
| BR-017 | SLA timer's start event (creation vs. assignment) must be a single, explicitly configured point (**ISSUE-001**). | [ASSUMPTION] |
| BR-018 | Department transfer/reassignment SLA impact is configurable per priority tier; default = continue (no reset), so a transfer does not reset a ticket's SLA clock. | [ASSUMPTION] |
| BR-019 | **[Revised — final correction pass]** A priority change never erases elapsed time, an existing breach, or the original SLA history. On an **upgrade**, the new due date is the earlier of the date already in effect and the date freshly computed under the higher tier. On a **downgrade**, Department-Head-or-above approval is required before it takes effect, and any breach already recorded under the prior tier is never removed or reversed. Every prior SLA period is retained in `SlaHistory`, never overwritten, and management reporting shows both the original and changed periods (see **ISSUE-023**). | [ASSUMPTION] |
| BR-030 | **[New — final correction pass]** For a unit with multiple linked contacts, ticket notifications and disclosures are sent only to the contact who raised (or is directly named on) that specific ticket; other linked contacts are not notified or told about it unless separately authorized. Tenant and owner histories are never disclosed to each other through this system. A caller not personally listed on the unit record (e.g., a representative) may only be given information once a CRM-recorded authorization exists for them — a verbal claim of authority is not sufficient. This governs live agent-mediated disclosure only; it does not imply or require a customer self-service portal (see **ISSUE-021**). | [ASSUMPTION], resolving **ISSUE-007** |
| BR-020 | A closed ticket may be reopened within the confirmed window (**ISSUE-011**); beyond that, a new linked ticket is created instead. | [ASSUMPTION] |
| BR-021 | Two tickets are treated as possible duplicates when they share the same unit and category within a rolling window; the system flags for agent confirmation only — never auto-merges. Confirmed duplicates require `DuplicateOfTicketId` (BR-022). | [ASSUMPTION] |
| BR-022 | `ResolutionOutcome = Duplicate` requires a valid `DuplicateOfTicketId`; the referenced original ticket's lifecycle is not altered by the link. | Section 5 redesign |
| BR-023 | Cancelled and Rejected are `ResolutionOutcome` values, not separate statuses; neither counts toward resolution-time KPIs and neither triggers CSAT. | [ASSUMPTION] |
| BR-024 | A unit record with multiple contacts requires the agent to identify the specific contact on a call, in addition to the unit match, before proceeding. | [ASSUMPTION], motivated by §3 Step 03 |
| BR-025 | Data ownership: all ticket/customer/interaction data is Tiger Group property; Geyness may not use it outside this engagement. | §11 |
| BR-026 | Data retained ≥ 7 years. | §11 |
| BR-027 | **[New — review point 3]** The CRM is the sole source of truth for unit and contact master data. The ticketing system never replicates or masters this data — it stores the CRM-issued identifiers and an immutable, ticket-time snapshot only. A later change to the CRM record does not retroactively alter any existing ticket's snapshot. | Review point 3 |
| BR-028 | **[New — review point 6]** A Department Employee/Head may `Resolve` a ticket but may never `Close` one; a Geyness Agent, Supervisor, or CS Manager may `Close` a ticket but only after `ResolutionOutcome` is already set by the owning department. | Review point 6 |
| BR-029 | **[New — review point 1]** `FirstResponseDueAt` is satisfied only by `FirstHumanResponseAt` — the automated acknowledgement message never counts as the first response for SLA purposes. | Review point 1 |

---

## 7. SLA and Escalation Rules

### 7.1 SLA Tiers (source: §6)

| Priority | Definition | First Response | Resolution Target | Clock Basis | Breach Action |
|---|---|---|---|---|---|
| Critical | Safety, flooding, fire, access failure | 15 minutes | 4 hours | 24/7 calendar time | Immediate GM notification (+ Dept Head, per **ISSUE-004**'s recommended resolution) |
| High | Habitability-affecting maintenance or legal deadlines | 1 hour | 24 hours | Business hours only | Dept Head + GM alert |
| Medium | Standard maintenance, contract queries | 4 hours | 3 business days | Business hours only | Dept Head alert |
| Low | General information, documentation requests | 24 hours | 7 business days | Business hours only | Supervisor alert |

Business hours (source, §6): 08:00–18:00, Saturday–Thursday, UAE calendar — **flagged** in **ISSUE-017**.

### 7.2 What Satisfies First Response — *(new, review point 1)*

**This is now Critical Decision ISSUE-019.** The automated acknowledgement (FR-NOT-01) fires within seconds of ticket creation for every channel and would trivially "satisfy" a 15-minute or 1-hour first-response target every time — making the KPI meaningless as a measure of actual service. The recommended rule: `FirstResponseDueAt` is satisfied only by `FirstHumanResponseAt` — a timestamped record of the first substantive, human-authored communication to the customer about their specific request (a phone callback, a personalized email/note, a WhatsApp reply addressing the ticket). The system must capture this as a distinct event from the automated acknowledgement, and SLA compliance reporting must use it, not the acknowledgement timestamp.

### 7.3 SLA Pause/Resume — **[ASSUMPTION]**

Non-Critical timers pause during: non-business hours, Fridays (pending **ISSUE-017**), UAE public holidays, and while `TicketStatus = Pending Customer` or `Pending Third-Party` (**ISSUE-018**). They resume when work resumes within business hours.

### 7.4 Warning Thresholds — **[ASSUMPTION]**

Recommended default: warn at 75% of resolution-target elapsed time, escalating the warning to the ticket owner + Supervisor.

### 7.5 Priority-Change SLA Policy — *(revised — final correction pass)*

A priority change must never erase elapsed time, an existing breach, or the original SLA history. An earlier draft referenced an undefined "proportional carry-forward" calculation for what happens to the SLA clock when a ticket's priority changes mid-flight; that is a description of a desired outcome, not an algorithm, and has been replaced. The policy below (see **ISSUE-023** for the options considered) separates the rule for an **upgrade** from the rule for an **approved downgrade**.

**Upgrade to a higher priority:**
- The prior SLA period is closed and archived in `SlaHistory` exactly as it stood, including any breach already recorded within it.
- A new due date is computed under the new, higher tier from the moment of change.
- The ticket's operative `Due*At` becomes the **earlier of** (a) the due date already in effect before the change, and (b) the due date freshly computed under the higher tier — an upgrade can only tighten a deadline, never loosen it.

**Downgrade to a lower priority:**
- Requires a recorded approval from a Department Head (or above) before it takes effect.
- Any breach already recorded under the prior (higher) tier is never removed or reversed by the downgrade.
- Recalculated due dates under the lower tier apply only from the approval moment forward.

**In all cases:**
- Every previous SLA period — and every breach within it — is preserved permanently in `SlaHistory`, never overwritten or deleted.
- A new operational SLA period begins at the moment of the priority change (or, for a downgrade, at the moment of approval).
- Management reporting displays both the original and the changed SLA period for any ticket that had a priority change.

### 7.6 Escalation Levels (source: §7)

| Level | Role | Trigger | Response Requirement |
|---|---|---|---|
| 1 | Geyness Agent | Own attempt at first-contact resolution | Flags for escalation if unable to resolve |
| 2 | Department Head | Auto on SLA breach or agent flag | Must respond within 2 hours |
| 3 | General Manager | Level 2 fails to resolve within the configured window (**ISSUE-013**) | Full authority to act |
| 4 | Chairman/CEO | Manual only | No defined SLA (executive discretion) |

`EscalationLevel` is independent of `TicketStatus` (Section 5.3) — escalating a ticket never removes it from active work.

### 7.7 Reassignment / Priority-Change Impact

Reassignment does not reset the SLA clock by default (BR-018). Priority changes follow Section 7.5's revised policy — never the v1.0 proportional formula.

### 7.8 Notification Recipients Summary

| Event | Recipients | Channel (MVP) |
|---|---|---|
| Ticket acknowledgement | — (no customer-facing acknowledgement channel exists pre-Phase-2 other than email) | Email |
| Critical breach | GM + Dept Head | Email + in-app |
| High breach | Dept Head + GM | Email + in-app |
| Medium breach | Dept Head | In-app + Email |
| Low breach | Supervisor | In-app |
| Escalation Level 4 | Chairman/CEO | Email (manual trigger) |
| Low CSAT (<3.0) | Geyness Account Manager, Tiger CS Manager | Phase 2 |
| Critical incident (Ad Hoc report) | GM + CS Manager | Phase 2 (formal report); MVP raises an in-app/email alert only |

### 7.9 Example SLA Calculations

**Example A — Critical ticket:** Created Tuesday 22:40. First response due 22:55 (15 min, 24/7). Resolution due Wednesday 02:40 (4h, 24/7).

**Example B — High-priority ticket outside business hours:** Created Thursday 17:30 (business hours 08:00–18:00, Sat–Thu). 30 min elapsed Thursday; Friday excluded; clock resumes Saturday 08:00. First response (1h) due Saturday 08:30. Resolution (24h) due Sunday 13:30.

**Example C — Priority upgrade mid-flight (illustrating the revised §7.5 policy):** A Medium ticket (4h resolution target, business hours) is created Monday 09:00, giving an original `ResolutionDueAt` later that week. By 10:00 Monday, the situation is found to involve a safety risk and is upgraded to Critical. The Medium-tier period (09:00–10:00 Monday, not yet breached) is closed and archived in `SlaHistory` exactly as it stood. A fresh Critical-tier due date is computed from 10:00 Monday: `ResolutionDueAt = 14:00 Monday` (4h, 24/7). Because 14:00 Monday is earlier than the original Medium-tier due date, the earlier-of-the-two rule selects 14:00 Monday as the ticket's operative deadline (`FirstResponseDueAt` is recalculated the same way: 10:15 Monday, 15 min, 24/7). No approval is required for an upgrade.

**Example D — Priority downgrade requiring approval:** A Critical ticket created Tuesday 09:00 (`ResolutionDueAt = 13:00`, 4h, 24/7) breaches at 13:00 without resolution — the breach is recorded in `SlaHistory`. At 14:00, the ticket is reassessed as Medium severity. The downgrade is proposed but held pending; it only takes effect once a Department Head approves it — recorded with approver, timestamp, and reason — say at 14:30. From 14:30 onward, the ticket is measured against a fresh Medium-tier due date computed from the approval moment. The 13:00 Critical breach remains permanently on the ticket's record and is not removed or reversed by the later downgrade; management reporting shows both the original Critical period (with its breach) and the new Medium period.

---

## 8. Required Integrations

Tier reflects review point 11's phase split. **Reliability pattern** column reflects review point 10 — every integration below is designed against the cross-cutting patterns detailed in **§10.7** (Transactional Outbox, idempotency keys, correlation IDs, retry policy, dead-letter handling); this column names which of those patterns is most load-bearing for that specific integration, not an exhaustive list.

| Integration | Purpose | Direction | Auth | Failure Handling | Reliability Pattern (see §10.7) | Tier |
|---|---|---|---|---|---|---|
| **INT-01 Tiger Group CRM** | Resolve unit number → customer/unit record; write back ticket/resolution history. | Bi-directional | [ASSUMPTION] OAuth2/mTLS per CRM vendor | Provisional-record fallback (**ISSUE-006**); downtime escalated ≤15 min | Idempotency key per lookup/write; correlation ID on every call | **MVP** |
| **INT-02 Genesys Call Center Platform** | **[Amended]** Basic scope now MVP: webhook-delivered conversation ID, caller number, agent ID/email/extension, channel, start/answer/end timestamps, linked to a ticket. Deeper capability (outbound dialing, recording retrieval, desktop automation) remains Phase 2. | Inbound (webhooks) + limited outbound | Webhook signature validation (exact scheme unconfirmed — open question for the Genesys team, see `docs/architecture/Genesys-Integration.md`) | Manual entry fallback if Genesys unavailable (ticket creation stays manual regardless) | Idempotency key on `(ConversationId, EventType)`; correlation ID; Outbox for any outbound effect | **MVP (basic)** / **Phase 2 (deeper capability)** |
| **INT-03 WhatsApp** | Auto-route WhatsApp/live-chat messages into tickets. | Inbound + Outbound | WhatsApp Business API auth | Queue for manual pickup if degraded | Outbox for outbound sends; idempotent webhook handling (Meta may redeliver) | **Phase 2** |
| **INT-04 SMS Provider** | Acknowledgement, breach, CSAT delivery. | Outbound only | [ASSUMPTION] API key | Fallback to email on failure; DLQ after retries exhausted | Outbox + retry/backoff + DLQ | **Phase 2** |
| **INT-05 Email Provider** | Acknowledgement (MVP); report distribution (Phase 2). | Outbound only | [ASSUMPTION] SMTP relay/API key | Retry queue; DLQ after exhaustion | Outbox + retry/backoff + DLQ | **MVP** |
| **INT-06 Website & Mobile App** | Digital form/chat widget → auto-ticket. | Inbound | [ASSUMPTION] session auth or shared API key | Held `PendingCrmVerification` if unresolved | Idempotent submission handling (duplicate form posts) | **Phase 2** |
| **INT-07 Social Media** | Agent-monitored inbox → manual ticket conversion. | Inbound (manual) | Platform-specific OAuth | Manual conversion (no auto-ticket) | Correlation ID linking back to source thread | **Phase 3** |
| **INT-08 Office Kiosk** | Branded on-screen form at reception. | Inbound | [ASSUMPTION] device-scoped API key | Held `PendingCrmVerification` if unresolved | Idempotent submission per device/session | **Phase 3** |
| **INT-09 File Storage** | Store up to 10 attachments/ticket. | Bi-directional | [ASSUMPTION] signed URLs/SAS tokens | Virus-scan failure blocks storage | Idempotent upload handling (retry-safe) | **MVP** |
| **INT-10 Reporting/Export** | Scheduled reports; on-demand export ≤24h. | Outbound | Internal service auth | Missed scheduled report is itself alertable | Outbox for report-ready events; correlation ID per report run | **MVP** (raw export only); **Phase 2** (formatted reports) |

---

## 9. Missing Requirements, Ambiguities and Contradictions

**Final correction pass:** ISSUE-005 has been removed as a standalone item — its underlying concern (no defined exit from the escalation retry loop) is now addressed as part of ISSUE-013, which defines configurable, priority-based Level 2→3 escalation windows instead of a retry count. This leaves **22 items total** (17 original + 5 new from the architecture review). Full options/pros/cons/recommendations for every item are maintained in the companion **`Tiger-CS-Ticketing-Management-Decisions.md`** (the Technical Decision Register), which is the presentation-ready version of this list; a shorter **`Tiger-CS-Ticketing-Executive-Decisions.md`** covers only the items that block MVP.

| ID | Severity | Issue | Recommended Decision | Question for Management |
|---|---|---|---|---|
| **ISSUE-019** *(new)* | **Critical** | What event satisfies First Response SLA? The automated acknowledgement fires in seconds for every channel and would make the target meaningless if it counted. | First response = first genuine, human-authored communication to the customer (`FirstHumanResponseAt`), never the automated acknowledgement. | "Should the SLA 'first response' clock stop at the automated acknowledgement, or only at the first human-authored reply to the customer?" |
| **ISSUE-020** *(new)* | Medium | Does the ticket-ID `[DEPT]` segment change when a ticket transfers departments? | ID is immutable; `[DEPT]` always reflects the *originating* department; current ownership is a separate mutable field. | "Please confirm the ticket ID should never change after creation, even when the owning department changes." |
| **ISSUE-021** *(new)* | **High** | Is an authenticated customer self-service portal (login, ticket history, self-service reopen) in scope at all? v1.0 implicitly assumed one existed. | No — remove all portal capability from MVP; customer interacts only via phone/email until explicitly approved. | "Do we want customers to have a login-based self-service portal at any point, and if so, in which phase — or should all customer interaction remain agent-mediated?" |
| **ISSUE-022** *(new)* | High | Who may Resolve vs. Close a ticket, and who may Reopen/Cancel/Reject? | Department Employee/Head resolves (marks work done); Geyness Agent/Supervisor/CS Manager closes (confirms customer notified) and reopens. | "Do you agree that closing a ticket — the final, customer-facing action — should be a CS-side responsibility distinct from a department marking its own work done?" |
| **ISSUE-023** *(new)* | High | What SLA policy applies when a ticket's priority changes mid-flight, without erasing elapsed time, a breach, or history? An earlier draft's "proportional carry-forward" was never defined. | Upgrade: new due date is the earlier of the existing due date and the fresh higher-tier due date. Downgrade: requires Department Head approval and never removes a recorded breach. All prior periods retained in full; both shown in reporting. | "Please approve the upgrade and downgrade SLA rules in Section 7.5, including the approval requirement and breach-preservation guarantee for downgrades." |
| ISSUE-001 | Critical | SLA timer start point (creation vs. assignment) contradicts between diagram and §4. | Default to creation; track time-to-assignment separately. | "Does the SLA clock start at ticket creation or at owner assignment?" |
| ISSUE-002 | Critical | Core Rule vs. §2's auto-ticket channels — **now applies from Phase 2 onward only**, since MVP has no auto-ticket channel. | `PendingCrmVerification` sub-state (FR-VER-06). | "For auto-ticket channels (Phase 2), is the ticket number issued before or after CRM verification completes?" |
| **ISSUE-003** *(resolved)* | High | Geyness vs. Genesys vendor/platform identity. **Resolved:** management's explicit direction confirms the platform is Genesys ("Genesys APIs and webhooks" specified directly), and authorizes Genesys Basic Integration within MVP. | Platform = Genesys, confirmed. Basic integration proceeds in MVP per ADR-0019; full API contract detail still depends on the open questions listed in `docs/architecture/Genesys-Integration.md` §15. | *(No longer open — retained here for traceability. Remaining open items are technical questions for the Genesys team, not a management decision.)* |
| ISSUE-004 | High | §6 "Immediate GM notification" for Critical vs. §7's Dept-Head-first model. | Notify both simultaneously on Critical breach. | "Should the Department Head still be notified alongside the GM on a Critical breach?" |
| ISSUE-006 | High | CRM downtime fallback for ticket creation — **applies to MVP**, since INT-01 (CRM) ships in MVP. | Provisional ticket creation for Critical/High during outage. | "During a CRM outage, can agents open provisional tickets for safety-critical issues?" |
| **ISSUE-007** *(rewritten — no Customer Portal assumed)* | High | With no customer portal, how is unit/contact information disclosed by phone/notification? Specifically: which linked contact may receive ticket details; who receives notifications; whether a tenant may receive an owner's ticket history (or vice versa); and how joint owners/authorized representatives are verified. | Notifications and disclosure go only to the contact who raised or is named on the ticket; tenant/owner histories are never cross-disclosed; a representative not personally listed requires a CRM-recorded authorization before anything is disclosed. Self-service portal visibility remains a separate question (**ISSUE-021**). | "Which linked contact is authorized to receive ticket details and notifications for a unit with multiple contacts, may tenants see owner history (or vice versa), and how should agents verify a caller claiming to represent an owner or tenant?" |
| ISSUE-008 | Medium | Confirm the redesigned five-dimension lifecycle model (Section 5) and its value sets. | Adopt as specified. | "Please confirm the TicketStatus/VerificationStatus/EscalationLevel/SlaState/ResolutionOutcome model in Section 5." |
| ISSUE-009 | Medium | CSAT resend on reopen — **now a Phase 2 question**, since CSAT itself is Phase 2. | Resend, tagged "post-reopen" in reporting. | "Should a reopened-then-reclosed ticket trigger a second CSAT survey?" |
| ISSUE-010 | Medium | Department transfer approval authority and SLA impact. | Department Head approval required; SLA clock continues (no reset). | "Who approves a cross-department transfer, and does the SLA clock reset?" |
| ISSUE-011 | Medium | Reopen window duration. | 7 days, configurable. | "What is the allowed window to reopen a closed ticket?" |
| **ISSUE-012** *(ownership revised)* | Medium | UAE holiday calendar ownership and maintenance. | Business owner: Customer Service or HR (decides the actual dates). Technical administrator: System Administrator (enters them into the configurable reference table). | "Should Customer Service or HR own confirming each year's UAE public holidays, with the System Administrator responsible for entering them into the system?" |
| **ISSUE-013** *(expanded — absorbs former ISSUE-005)* | Medium | Level 2→3 escalation window and SLA warning threshold undefined; escalation progression must be time-based and priority-based, not a count of re-assign-and-retry cycles. | 75% warning threshold; Level 2→3 window configurable per priority tier, replacing any retry-count mechanism. | "How long should Level 2 have before an escalated ticket auto-advances to the GM, and should that window differ by priority tier?" |
| ISSUE-014 | Low | Repeat Contact Rate definition — **now a Phase 3 question**, since Advanced KPI is Phase 3. | Ship as provisional heuristic when built. | "What counts as a 'repeat contact for the same issue'?" |
| ISSUE-015 | Low | No volume/scale figures given — **gates Phase 2 capacity planning**, when real customer-facing load appears. | Proceed on scalable defaults; revisit before Phase 2. | "Approximate unit/tower count and concurrent-agent count expected?" |
| **ISSUE-016** *(reclassified)* | Low | Exact UAE retention regulation uncited. **Required before production go-live — not safe to defer beyond launch**, since retained records begin accumulating from MVP's first day of use. | Legal/Compliance confirms the specific regulation and per-record-type periods before go-live; 7 years uniformly is only an interim placeholder, not a final answer. | "Which UAE regulation sets the 7-year retention period, and must this be confirmed with Legal before MVP goes live, or can go-live proceed on the interim 7-year default?" |
| ISSUE-017 | Low | Business week Sat–Thu vs. Sat–Sun. | Confirm; build calendar as configurable data regardless. | "Please confirm the actual operating week." |
| **ISSUE-018** *(severity raised Low → High)* | **High** | Does the SLA clock pause on Pending Customer/Third-Party? SLA pause behavior directly affects contractual SLA compliance figures, not just an internal convenience. | Pause, with monitoring for misuse. | "Should the SLA clock pause while waiting on the customer or a third party?" |

---

## 10. Recommended Modular Architecture

**Recommendation unchanged: modular monolith**, not microservices — the reduced MVP reinforces this further, since MVP has exactly one write-heavy domain (tickets) and two integrations (CRM, Email/File Storage).

### 10.1 Solution/Project Boundaries (conceptual — no scaffolding created)

```
TigerCS.Api            → ASP.NET Core Web API (agent desktop, admin, integrations)
TigerCS.Web            → ASP.NET Core MVC/Razor Pages (Tiger-facing dashboard/reports UI)
TigerCS.Domain         → Core domain: Ticket (5-dimension state), SlaPolicy, Escalation — no framework dependencies
TigerCS.Application    → Application services / use cases (CQRS-style handlers), orchestration, domain events, Outbox writer
TigerCS.Infrastructure  → EF Core, SQL Server, Identity, Hangfire job implementations, SignalR hub implementations, Outbox dispatcher
TigerCS.Integrations    → Gateway adapters: CRM (MVP), Email/File Storage (MVP); WhatsApp/SMS/Website/Geyness platform (Phase 2); Kiosk/Social (Phase 3)
TigerCS.Reporting       → Report/KPI aggregation (basic operational view at MVP; full reports Phase 2+)
TigerCS.Tests           → xUnit test projects mirroring the above (unit + integration)
```

### 10.2 Modules and Responsibilities

Unchanged from v1.0 in shape; scope of each module's *content* is now MVP-first per Section 2.

| Module | Responsibility |
|---|---|
| **Identity & Access** | ASP.NET Core Identity-backed users/roles; enforces the Section 4 permission matrix, including the Customer-role exclusion (FR-ADM-07). |
| **Verification** | Unit/contact resolution against CRM; owns `VerificationStatus` and the CRM-snapshot capture (FR-VER-05). |
| **Ticketing** | The five-dimension ticket aggregate (Section 5), attachments, notes, audit trail. |
| **Classification & Routing** | Category/priority selection, department routing table, transfer/reassignment (with immutable-ID handling, FR-TKT-11). |
| **SLA & Escalation Engine** | Due-timestamp calculation, scheduled deadline jobs, `EscalationLevel` state, priority-change policy (§7.5), breach notifications. |
| **Notifications** | Templated outbound messaging via the Outbox; MVP = email only. |
| **CSAT** *(Phase 2)* | Survey issuance, scoring, low-score alerting. |
| **Reporting & KPI** | Basic operational view (MVP); scheduled reports and full KPI dashboard (Phase 2); advanced analytics (Phase 3). |
| **Audit** | Cross-cutting append-only log consumed by all modules via domain events. |
| **Integration Gateways** | One adapter per external system, each behind a narrow interface, each following the reliability patterns in §10.7. |

### 10.3 Conceptual Domain Entities & Relationships — **corrected (review point 3)**

v1.0 modeled `Unit` and `Contact` as locally-owned, richly-related entities (implying the ticketing system would maintain its own relational copy of Tiger's customer/unit data). **This was incorrect and is corrected here.** The CRM is the sole system of record for unit and contact master data. The ticketing system's local model is deliberately thin:

- **`UnitReference`** — stores only the CRM-issued `UnitId` (an opaque foreign identifier), never a locally-owned unit record.
- **`ContactReference`** — stores only the CRM-issued `ContactId`, same principle.
- **`TicketSnapshot`** (value object, embedded on `Ticket`) — an **immutable, point-in-time copy** of the fields the agent actually read back and relied on at ticket-creation time: unit number, property, tower, unit type, contact name, contact detail used for outreach. Written once at creation (or at CRM-reconciliation time for provisional tickets); **never updated** by later CRM changes. This is what makes the ticket a faithful historical record even if the CRM record itself changes or the contact moves out.
- **`Ticket`** — the aggregate root: `TicketStatus`, `VerificationStatus`, `EscalationLevel`, `SlaState`, `ResolutionOutcome`, `UnitReference`, `ContactReference`, `TicketSnapshot`, `Due*At` timestamps, `CurrentDepartment` (mutable), immutable `TicketId`.
- **`Ticket` (1) ↔ (many) `StatusChangeEvent`** — one row per change to *any* of the five dimensions, forming the audit trail and the basis for SLA-history reconstruction.
- **`Ticket` (1) ↔ (many) `EscalationEvent`** — one row per `EscalationLevel` change.
- **`Ticket` (1) ↔ (many) `SlaHistoryEntry`** — one row per SLA period (created on priority change, per §7.5), never overwritten.
- **`Ticket` (1) ↔ (many) `Attachment`**.
- **`Ticket` (1) ↔ (0..1) `CsatResponse`** *(Phase 2)*.
- **`Ticket` (1) ↔ (0..1) `DuplicateOfTicketId`** (self-referential, only when `ResolutionOutcome = Duplicate`).
- **`Department` (1) ↔ (many) `Employee`**; **`Employee` (many) ↔ (many) `Role`**.
- **`SlaPolicy`** (reference data: priority tier → response/resolution targets, clock basis, priority-change policy) — configuration, not hardcoded.
- **`HolidayCalendar`** (reference data, addressing **ISSUE-012**).
- **`OutboxMessage`** (infrastructure entity — see §10.7).

No SQL DDL or EF Core mappings are produced at this stage, per the task's explicit instruction.

### 10.4 Application Services & Integration Gateways

Application services (`VerifyUnitHandler`, `CreateTicketHandler`, `ResolveTicketHandler`, `CloseTicketHandler`, `TransferTicketHandler`, `ChangePriorityHandler`, `EscalateTicketHandler`, etc.) orchestrate domain logic and — per §10.7 — write resulting domain events to the Outbox in the same transaction as the state change, rather than calling out to notifications/integrations directly. Integration gateways implement narrow interfaces (`ICrmGateway`, `IEmailGateway`, `IFileStorageGateway` for MVP; `IWhatsAppGateway`, `ISmsGateway`, etc. from Phase 2) owned by the Application layer.

### 10.5 Background Jobs (Hangfire) & Real-Time Events (SignalR) — **revised (review points 8/9)**

**SLA scheduling (point 8).** v1.0 relied on a periodic sweep as the primary SLA/escalation mechanism. This is now the *safety net*, not the primary mechanism:

1. On ticket creation and on every priority change, the system computes `FirstResponseDueAt` and `ResolutionDueAt` and stores them as explicit columns.
2. A Hangfire **scheduled (delayed) job** is enqueued for each due timestamp, firing exactly at that moment to check whether the corresponding event (`FirstHumanResponseAt`, `ResolutionOutcome` set) has occurred; if not, it marks `SlaState = Breached` and raises the breach event via the Outbox.
3. A **recurring sweep** (e.g., every 1–5 minutes) independently re-scans all open tickets against their due timestamps, purely to catch cases where a scheduled job was lost (e.g., a deploy/restart during its window) — it is not the primary breach-detection path, only a backstop.
4. **Idempotency:** every breach/warning check is keyed by `(TicketId, DueType, DueTimestamp)`, so the scheduled job and the sweep firing near-simultaneously never produce a duplicate breach notification (NFR-REL-02).

**SignalR (point 9).** The server never broadcasts a per-second countdown. It publishes discrete events — `TicketStatusChanged`, `SlaDueTimestampChanged`, `EscalationLevelChanged` — each carrying the relevant due timestamp(s). The browser/agent-desktop client receives the timestamp once per change and renders the live countdown by computing `dueAt - now()` locally on a client-side timer. This is both more correct (no server-side clock-skew-sensitive broadcast loop) and far lower load on the SignalR hub.

| Job/Event | Trigger | Mechanism |
|---|---|---|
| SLA deadline check (per due timestamp) | Scheduled at creation/priority-change time | Hangfire scheduled (delayed) job — **primary mechanism** |
| SLA sweep (safety net) | Recurring, e.g. every 1–5 min | Hangfire recurring job — **backstop only** |
| Email acknowledgement | Ticket created (MVP) | Outbox → Notifications module |
| Notification retries | Delivery failure | Outbox retry-with-backoff → DLQ after exhaustion |
| CRM reconciliation for `PendingCrmVerification` | Event-driven + 15-min timeout sweep | Hangfire scheduled job |
| Ticket/SLA/escalation state changes | Every relevant domain event | SignalR — state/deadline-change events only, never a countdown tick |
| Basic operational dashboard updates | Domain events | SignalR (MVP); full KPI dashboard updates from Phase 2 |

### 10.6 Authorization & Audit Strategy

Unchanged from v1.0: ASP.NET Core Identity + policy-based authorization mapped 1:1 to Section 4's matrix, enforced server-side; a single cross-cutting `AuditLog` populated via domain-event subscribers, covering every one of the five lifecycle dimensions (not just a single status field, per the Section 5 redesign).

### 10.7 Reliability & Messaging Patterns — **new (review point 10)**

Applied uniformly across the SLA engine, Notifications module, and every Integration Gateway:

| Pattern | Applied to | Purpose |
|---|---|---|
| **Transactional Outbox** | Every domain event that must trigger a notification or integration call | Domain events are written to an `OutboxMessage` table in the *same database transaction* as the state change itself. A separate dispatcher process reads and publishes them. This eliminates the dual-write problem — a state change can never be committed while its corresponding notification/integration call is silently lost, or vice versa. |
| **Idempotency Keys** | SLA breach/warning checks, all outbound notifications, all inbound webhooks (from Phase 2 onward) | Every dispatched message/job carries a key derived from a stable business fact (`TicketId + EventType + EventVersion`). Retries, sweep/scheduled-job overlap, and redelivered webhooks never produce a duplicate customer-facing effect. |
| **Correlation IDs** | Every request, domain event, Outbox message, and downstream integration call | A single ID (generated at the point of customer/agent interaction) is propagated through logs, the Outbox, and any external API call, so any notification or SLA calculation can be traced end-to-end for audit or dispute resolution. |
| **Retry Policy with Backoff** | All outbound integration calls (Email/File Storage at MVP; SMS/WhatsApp/CRM/Website from Phase 2) | A defined number of attempts with exponential backoff before a call is considered failed — not an unbounded retry loop, not a single silent attempt. |
| **Dead-Letter Handling** | Any message that exhausts its retry policy | Routed to a dead-letter store, visible to an operational role, for manual review/resend — never silently dropped. |

This directly answers review point 10 and is the same set of patterns referenced throughout Sections 2, 6, and 8 wherever a notification or integration call is described.

---

## 11. Implementation Phases — **restructured around the reduced MVP (review points 11/12)**

Phases 1–10 below are scoped to **MVP only**, per the smaller boundary in Section 2/15. Two new phases (11 and 12) cover the deferred Phase 2 and Phase 3 scope as distinct release efforts, each with its own dependencies and estimate — this directly answers review point 12's instruction to update estimates and dependencies for the reduced MVP.

| Phase | Scope | Deliverables | Dependencies | Acceptance Criteria | Est. Effort | Key Risks |
|---|---|---|---|---|---|---|
| **1. Discovery & Requirement Approval** | Resolve the 22 items in Section 9/13 that gate MVP, go-live, and Phase 2; confirm CRM technical details. | Signed-off decisions for all MVP-gating items (Section 13, Group A). | None | Management sign-off document exists | 1–2 weeks | Delayed decisions on the five new Critical/High items (ISSUE-019/021/022/023, plus 001) block Phase 4 directly |
| **2. Architecture & Database Design** | Finalize module boundaries, the five-dimension lifecycle ERD, `SlaHistory`/`Outbox` schema, holiday calendar schema. | ERD, module dependency diagram, API contract sketch, ADRs | Phase 1 | Design reviewed and approved before any code | 2 weeks | Designing around unresolved decisions locks in wrong assumptions |
| **3. Project Foundation** | Solution scaffolding, CI/CD, Identity setup, base EF Core migrations, Outbox infrastructure, logging/monitoring baseline. | Buildable solution skeleton per §10.1, empty but wired-up modules | Phase 2 | `dotnet build`/`dotnet test` green in CI | 1–2 weeks | Under-investing in Outbox/idempotency scaffolding here causes painful retrofits in Phase 5 |
| **4. Core Ticketing MVP** | Manual phone-only intake (FR-CH-01), verification with CRM snapshot (FR-VER-*), five-dimension ticketing engine, classification/routing, Resolve/Close split. | Working agent-desktop flow: create → verify → classify → route → resolve → close, for phone only | Phase 3 | End-to-end manual test with the correct Resolve/Close role separation enforced; audit trail present across all 5 dimensions | **3–4 weeks** *(reduced from v1.0's 4–6 weeks — no auto-ticket channel complexity)* | Any of ISSUE-001/007/019/021/022 still open at this point directly re-shapes the data model |
| **5. SLA & Escalation** | Due-timestamp storage, scheduled deadline jobs + sweep-as-safety-net, `EscalationLevel` state machine, priority-change policy with approval gate, breach notifications via Outbox. | Timer service, escalation state machine, `SlaHistory`, Outbox dispatcher | Phase 4 | §7.9's worked examples reproduce correctly against test fixtures; duplicate-breach idempotency test passes | **4–5 weeks** *(slightly up from v1.0's 3–4 weeks — the Outbox/idempotency/scheduled-job architecture is more engineering than a plain sweep, but is the correct trade for reliability)* | Calendar/holiday logic (ISSUE-012/017) and the priority-change approval gate (ISSUE-023) are easy to get subtly wrong — dedicated test coverage required |
| **6. Notifications & CRM/Storage Integration** | Email acknowledgement, CRM adapter (with downtime fallback), File Storage adapter — **all other integrations deferred to Phase 11**. | INT-01, INT-05, INT-09 implemented with Outbox/retry/DLQ | Phase 4 | Each has a passing integration test against a sandbox/mocked endpoint | **2–3 weeks** *(reduced from v1.0's 4–5 weeks)* | CRM sandbox availability can still block testing even at this reduced scope |
| **7. Basic Operational Dashboard** | Ticket counts by status/priority/department, SLA backlog, escalation counts — **no CSAT- or channel-mix-dependent KPI (those are Phase 11)**. | SignalR-backed live view (state-change events, not countdown broadcast) | Phases 4–6 | Dashboard reflects real ticket/SLA/escalation state within NFR-PERF-02's target | **1–2 weeks** *(reduced from v1.0's 3–4 weeks)* | None material at this reduced scope |
| **8. *(Removed from MVP roadmap — see Phase 12)*** | AI-assisted features are entirely Phase 3 scope. | — | — | — | — | — |
| **9. Testing & UAT** | xUnit coverage across modules; UAT with Tiger CS Manager and a Geyness agent cohort, scoped to the phone-only MVP flow. | Test suite covering SLA edge cases (§7.9-style scenarios, including the new priority-change/approval tests); UAT sign-off log | Phases 4–7 | UAT scenarios per role (§4) pass; no Critical/High defects open | **3 weeks** *(down slightly from v1.0's 3–4 weeks)* | Under-testing the SLA calendar/idempotency logic remains the highest-likelihood production-incident source |
| **10. Production Deployment & Support** | Go-live for the internal, phone-only MVP; hypercare support. | Deployed system meeting NFR-AVAIL-01 (99.5%), backup/DR verified against NFR-BCDR-01 | Phase 9 | 99.5% uptime and 4h RTO validated via a DR drill before go-live | **1–2 weeks** *(down from v1.0's 2–3 weeks — no kiosk hardware, no external channel cutover to coordinate)* + ongoing hypercare | None material beyond the standard go-live risks |
| **11. Phase 2 Release — Channel & Integration Expansion** | SMS, CSAT, the full contractual reporting suite (Daily/Weekly/Monthly/Ad Hoc), Website/mobile intake, WhatsApp, Genesys/Geyness call-center platform integration, full 10-metric KPI dashboard. | INT-02/03/04/06 implemented; CSAT end-to-end; formal reports on schedule; auto-ticket verification-timing policy (ISSUE-002) live | MVP live and stable; **ISSUE-002, ISSUE-003, ISSUE-009, ISSUE-015 resolved before this phase starts** | Each report's fields match §9 exactly; CSAT flow matches §8; SLA compliance figures reconcile with the MVP-era manual baseline | **8–10 weeks** | Vendor-side sandbox availability (CRM, WhatsApp Business API, Geyness/Genesys platform) can block testing; this phase reopens ISSUE-002's verify-before/after-create question for real, since it did not matter during phone-only MVP |
| **12. Phase 3 Release — Kiosk, Social, AI, Advanced Analytics** | Kiosk intake, social media integration, AI-assisted classification/prediction (FR-AI-*), Repeat Contact Rate and root-cause analytics. | INT-07/08 implemented; FR-AI-01…04 delivered at MVP-for-AI scope (advisory only); advanced KPI dashboard | Phase 11 live; **ISSUE-014, ISSUE-016 resolved**; kiosk hardware/network readiness at physical sites | AI suggestions are advisory-only with logged override rate; kiosk UI approved by Tiger IT before deployment | **6–8 weeks** | Kiosk hardware/network readiness at physical sites is outside pure software control; AI feature scope creep risk if not held to advisory-only |

*(Effort estimates remain order-of-magnitude planning inputs, not a committed quote, and should be revisited once ISSUE-015's volume/scale question is answered ahead of Phase 11.)*

---

## 12. Risk Register

| Risk | Probability | Business Impact | Mitigation | Owner |
|---|---|---|---|---|
| SLA start-point ambiguity (ISSUE-001) ships unresolved, causing disputed SLA-compliance reporting with Geyness. | Medium | High | Resolve in Phase 1; store both creation and assignment timestamps regardless | CS Manager + Solution Architect |
| First Response definition (ISSUE-019) left as "the automated acknowledgement," silently making the SLA meaningless. | Medium | **Critical** — the entire first-response KPI becomes unenforceable/undisputable in Geyness's favor by default | Resolve in Phase 1, before Phase 4; build `FirstHumanResponseAt` as a distinct, mandatory field from day one | Management |
| Reduced MVP means Geyness's contractual reporting obligations (§9/§12 of the source PDF — Daily Flash by 9AM, Weekly, Monthly, live KPI dashboard) are **not met by the MVP system** until Phase 11. | **High** (this is a direct, known consequence of review point 11's rescoping, not a risk of oversight) | High — a real gap between what the signed source document commits Geyness to deliver and what the MVP system can produce | Tiger Group must agree an interim manual/contractual accommodation with Geyness for the MVP period (e.g., manual interim reporting) until Phase 11 ships; this is a business decision, not an engineering one | Management |
| Core Rule vs. auto-ticket channel contradiction (ISSUE-002) resurfaces at Phase 11 without having been re-validated against the now-larger dataset from MVP. | Medium | High | Re-confirm ISSUE-002's decision explicitly before Phase 11 starts, even though MVP avoided it | Solution Architect |
| CRM downtime blocks ticket creation, including safety-critical Emergency FM requests — relevant from MVP, since CRM integration is MVP-scoped. | Low–Medium | Critical | Provisional-record fallback (ISSUE-006) implemented and tested before go-live | Solution Architect + Tiger IT |
| **[Resolved]** Geyness/Genesys naming confusion (ISSUE-003) — platform confirmed as Genesys by explicit management directive; residual risk is now the unconfirmed webhook/API contract details, tracked as open questions for the Genesys team in `docs/architecture/Genesys-Integration.md` §15, not a vendor-identity risk. | Medium (contract-detail risk, not identity risk) | Medium — could cause pilot rework if coded against wrong assumptions | Confirm the 8 open technical questions with the Genesys team before Phase 3 implementation of the Genesys adapter begins | Solution Architect / Tiger Transformation Directorate |
| Multi-party unit contacts (ISSUE-007) not modeled correctly, causing a tenant to see an owner's (or vice versa) ticket history. | Low–Medium | High | Contact-level modeling and permission scoping (FR-VER-04, BR-024) built into MVP, not deferred | Solution Architect |
| Priority-change approval gate (ISSUE-023) is bypassed or misconfigured, allowing a downgrade to take effect without Department Head approval and without the prior breach remaining visible in reporting. | Low | High | Dedicated test coverage for the approval gate and for breach-history preservation; audit trail on every priority change is reviewable | Solution Architect / CS Manager |
| Outbox/idempotency architecture (§10.7) is under-built in Phase 3 (Project Foundation), causing duplicate notifications or lost events discovered only in Phase 5. | Medium | Medium | Explicitly scope Outbox infrastructure into Phase 3's deliverables, not left implicit | Solution Architect |
| Scope creep — Customer Portal capability (ISSUE-021) gets built into MVP anyway because "it seemed easy," reintroducing the exact over-scoping the review flagged. | Medium | Medium | FR-ADM-07 explicitly excludes it; UAT checklist verifies no customer-facing login endpoint exists in the MVP build | Solution Architect / Project Sponsor |
| Kiosk hardware/network readiness at physical sites delays Phase 12 independent of software readiness. | Medium | Medium | Track kiosk rollout as a parallel workstream from Phase 11 onward | Tiger IT |
| Volume/scale unknown (ISSUE-015) leads to under- or over-provisioned infrastructure once Phase 11's customer-facing channels go live. | Medium | Low–Medium | Build on a horizontally-scalable default; revisit sizing once real numbers are known, before Phase 11 | Solution Architect |

---

## 13. Management Decisions Required

Re-bucketed against the **reduced MVP boundary** and the final correction pass. Full detail (options, pros/cons, recommendation, impact, priority, owner) for every item is in `Tiger-CS-Ticketing-Management-Decisions.md` (the Technical Decision Register); a shorter `Tiger-CS-Ticketing-Executive-Decisions.md` covers only Group A below for a management meeting. **ISSUE-005 has been removed** — its concern is now covered by ISSUE-013. This leaves **22 items** across four groups.

### Group A — Required Before MVP Development (blocks Phase 4) — 16 items

1. **(New, Critical)** What event satisfies First Response SLA? *(ISSUE-019)*
2. Does the SLA clock start at ticket creation or at owner assignment? *(ISSUE-001)*
3. **(New, High)** Is a customer self-service portal in scope at all — for any phase? *(ISSUE-021)*
4. **(New, High)** Who may Resolve vs. Close a ticket, and who may Reopen/Cancel/Reject? *(ISSUE-022)*
5. **(New, High)** What SLA policy governs a priority change — separately for an upgrade and an approved downgrade — without erasing elapsed time, a breach, or history? *(ISSUE-023)*
6. Does a Critical SLA breach still notify the Department Head, or the GM only? *(ISSUE-004)*
7. During a CRM outage, can agents open provisional tickets — for which priority tiers? *(ISSUE-006)*
8. **(Rewritten — no Customer Portal assumed)** Which linked contact may receive ticket details and notifications for a multi-contact unit; may tenants and owners see each other's history; how are joint owners/representatives verified? *(ISSUE-007)*
9. Please confirm the five-dimension lifecycle model in Section 5. *(ISSUE-008)*
10. Who approves a cross-department transfer, and does the SLA clock reset? *(ISSUE-010)*
11. What is the acceptable reopen window? *(ISSUE-011)*
12. **(Ownership revised)** Should Customer Service or HR own the UAE holiday calendar's business content, with the System Administrator responsible for entering it into the system? *(ISSUE-012)*
13. **(Expanded — absorbs former ISSUE-005)** What configurable, priority-based time window governs Level 2→3 escalation, replacing any retry-count mechanism? *(ISSUE-013)*
14. Confirm the actual operating business week (Sat–Thu vs. Sat–Sun). *(ISSUE-017)*
15. **(Severity raised Low → High)** Should the SLA clock pause on Pending Customer/Third-Party? This directly affects contractual SLA compliance figures. *(ISSUE-018)*
16. **(New, Medium)** Confirm the ticket ID is immutable and `[DEPT]` reflects only the originating department. *(ISSUE-020)*

### Group B — Required Before Phase 2 — 3 items *(ISSUE-003 resolved and moved out of this group — see Section 9)*

18. For auto-ticket channels, is the ticket number issued before or after CRM verification? *(ISSUE-002)*
19. Approximate unit/tower and concurrent-agent counts expected at Phase 2 launch? *(ISSUE-015)*
20. Should a reopened-then-reclosed ticket trigger a second CSAT survey? *(ISSUE-009)*

### Group C — Required Before Production Go-Live — 1 item

21. **(Reclassified — not deferrable past launch)** Which specific UAE regulation sets the retention period, confirmed by Legal/Compliance before the MVP goes live? *(ISSUE-016)*

### Group D — Can Be Deferred Until Phase 3 — 1 item

22. What is the operational definition of a "repeat contact" for the KPI? *(ISSUE-014)*

---

## 14. Consolidated Assumptions Register

Updated per review points 3, 5, 6, 7, 11, 13. Nothing below is a confirmed decision — each is a proposed default pending the corresponding item in Section 13.

1. Auto-ticket channels (Phase 2+) create a `PendingCrmVerification` provisional record until unit verification completes.
2. Multi-contact units require agent confirmation of the specific contact, in addition to the unit match.
3. CRM downtime allows provisional ticket creation for higher priorities, reconciled within 15 minutes.
4. Reopen window defaults to 7 days from closure.
5. Duplicate detection heuristic: same unit + category within a rolling 7-day window, flagged for confirmation only, never auto-merged; a confirmed duplicate requires `DuplicateOfTicketId`.
6. Cancelled/Rejected are `ResolutionOutcome` values (not statuses), CSAT-suppressing, distinct from Resolved.
7. Pending Third-Party is a distinct `TicketStatus` from Pending Customer.
8. Reassignment/transfer does not reset the SLA clock by default (configurable per tier).
9. **[Revised]** A priority change never erases elapsed time, a breach, or SLA history: an upgrade takes the earlier of the existing and freshly-computed due dates; a downgrade requires Department Head approval and never removes a recorded breach; both are shown in reporting (§7.5).
10. SLA warning fires at 75% of resolution-target elapsed time.
11. **[Revised]** Level 2→3 escalation advances on a configurable, priority-based time window (not a retry-cycle count) — a provisional default pending confirmation (**ISSUE-013**, which now also covers what was previously ISSUE-005).
12. Critical-breach notification includes both Dept Head and GM.
13. Non-Critical SLA clocks pause during Pending Customer / Pending Third-Party status.
14. WCAG 2.1 AA targeted for the MVP agent UI, extended to customer-facing surfaces once built (Phase 2/3).
15. Data residency/PDPL considerations favor a UAE (or otherwise compliant) SQL Server hosting region.
16. File attachment cap assumed at 25MB/file (count of 10/ticket is source-confirmed; size is not).
17. Notification channel-per-alert-type matrix (§7.8) is a proposed default; MVP uses email/in-app only, SMS added Phase 2.
18. **[Revised — reclassified, not deferrable]** 7-year retention applied uniformly across tickets, attachments, CSAT (Phase 2+), and audit logs as an interim default only; Legal/Compliance must confirm the exact regulation and per-record-type periods **before MVP goes live**, not at some unspecified later time (**ISSUE-016**).
19. System Administrator, Reporting User, Department Employee are treated as first-class roles even though only some are explicitly named in the PDF.
20. **[New]** `FirstResponseDueAt` is satisfied only by `FirstHumanResponseAt`, never the automated acknowledgement.
21. **[New]** Ticket ID is immutable; `[DEPT]` reflects only the originating department, never current ownership.
22. **[New]** No customer self-service portal exists unless ISSUE-021 is explicitly approved; MVP customer interaction is agent-mediated (phone) and email-notified only.
23. **[New]** Department Employee/Head Resolve; Geyness Agent/Supervisor/CS Manager Close — enforced as a hard permission split, not a convention.
24. **[New]** The CRM is the sole master of unit/contact data; the ticketing system stores CRM identifiers plus an immutable ticket-time snapshot only (BR-027).
25. **[New]** With no customer portal, ticket notifications/disclosure go only to the contact who raised or is named on a ticket; tenant and owner histories are never cross-disclosed; an unlisted representative requires a CRM-recorded authorization before anything is disclosed (BR-030, resolving **ISSUE-007**).
26. **[New]** The UAE holiday calendar's business content (which dates) is owned by Customer Service or HR; entering it into the system is a System Administrator task (**ISSUE-012**).

---

## 15. Recommended MVP Scope — **rewritten per review point 11**

### MVP (first release)

- Internal web application (agent desktop + Tiger-facing basic dashboard) — no customer-facing surface beyond outbound email
- Identity and role-based access control (Section 4), **excluding any Customer-role system access** (FR-ADM-07, pending ISSUE-021)
- CRM unit/contact lookup with immutable ticket-time snapshot (FR-VER-01…05, corrected per review point 3)
- Manual agent/phone ticket creation only — no auto-ticket channel
- Classification and routing (FR-CLS-01…03, FR-RTE-01…05)
- Assignment and the full five-dimension core lifecycle (Section 5): `TicketStatus`, `VerificationStatus`, `EscalationLevel`, `SlaState`, `ResolutionOutcome`, including Resolve/Close role separation (FR-RES-01…07)
- Notes and attachments (FR-TKT-06, INT-09)
- Full SLA and escalation engine (Section 7), including the revised priority-change policy and approval gate (§7.5) and the scheduled-deadline-job + Outbox architecture (§10.5, §10.7)
- Email acknowledgement only (FR-NOT-01, MVP scope)
- Full audit trail across all five dimensions (FR-ADM-03)
- Basic operational dashboard (FR-RPT-07): ticket counts by status/priority/department, SLA backlog, escalation counts — no CSAT- or channel-mix-dependent metric
- **[Amended]** Genesys Basic Integration (INT-02, basic scope only): conversation ID, caller number, Genesys agent ID, agent email/extension when available, channel/media type, interaction start/answer/end timestamps, ticket linkage, idempotent webhook processing, correlation ID, signature validation, retry/failure handling, and manual fallback if Genesys is unavailable. Ticket creation itself **remains manual** — Genesys supplies call metadata to an agent-created ticket, it does not auto-create one. This amendment resolves ISSUE-003 (the platform is confirmed as Genesys) and is authorized by an explicit management directive; see ADR-0019 in `docs/architecture/adr/` and `docs/architecture/Genesys-Integration.md` for full design and open questions still pending confirmation from the Genesys team.

### Phase 2

- SMS (acknowledgement, breach alerts, CSAT delivery)
- CSAT (FR-CSAT-01…03, FR-NOT-03…04)
- Advanced/formal reports: Daily Flash, Weekly Performance, Monthly Management, Ad Hoc/Incident (FR-RPT-01…05), plus the full 10-metric KPI dashboard (FR-KPI-01…02)
- Website/mobile intake (FR-CH-03 for App/Website, INT-06) — **gated on ISSUE-002's decision**
- WhatsApp (FR-CH-03/05, INT-03) — **gated on ISSUE-002's decision**
- **[Reduced scope — basic integration moved to MVP above]** Deeper Genesys integration beyond the basic scope: outbound dialing control, call-recording retrieval, agent-desktop screen-pop automation beyond a simple notification, and any richer CCaaS capability

### Phase 3

- Kiosk (FR-CH-04, INT-08)
- Social media integration (FR-CH-06, INT-07)
- AI-assisted features (FR-AI-01…04) — keyword/priority suggestion, predictive breach risk, chatbot, root-cause clustering, always advisory-only
- Advanced KPI and root-cause analytics (FR-KPI-03…04) — including the Repeat Contact Rate definition once **ISSUE-014** is resolved

**Gate before starting Phase 4 (Core Ticketing MVP):** answers to Section 13 Group A's items #1–5 at minimum (ISSUE-019, ISSUE-001, ISSUE-021, ISSUE-022, ISSUE-023) — these five directly shape the ticket/SLA/permission data model and are the most expensive to retrofit after code exists. The remaining Group A items can be answered in parallel with early Phase 4 work without blocking its start, but must close before Phase 5 (SLA & Escalation) begins.

**Gate before Phase 10 go-live:** Section 13 Group C's single item (ISSUE-016, retention regulation) must be confirmed with Legal/Compliance before the MVP is deployed to production — it is explicitly not safe to leave for after launch, since retained records begin accumulating from the first day of live use.
