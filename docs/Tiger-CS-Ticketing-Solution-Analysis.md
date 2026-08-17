# Tiger Group — Customer Service Ticketing System
## Solution Analysis Document

| | |
|---|---|
| **Prepared for** | Tiger Group — Transformation, Marketing & Growth Directorate |
| **Prepared by** | Solution Architecture Review (AI-assisted, human-reviewed) |
| **Status** | Draft for management review — **no implementation authorized** |
| **Date** | 2026-08-17 |
| **Primary source** | `Tiger_CS_Ticketing_System_Requirements.pdf` (Tiger Group, v1.0, June 2026) — "Powered by Geyness Call Center" |
| **Secondary source** | `tiger_cs_ticketing_workflow.png` — visual workflow reference |
| **Proposed stack** | ASP.NET Core 8 (Web API + MVC/Razor Pages), SQL Server, EF Core, ASP.NET Core Identity, Hangfire, SignalR, xUnit |

> **Scope reminder:** This document is analysis only. No application code, project scaffolding, database schema, or repository structure has been created. Section 10 describes architecture conceptually, as instructed.

---

## 0. Source Reconciliation Note

The PDF (13 sections, including its own end‑to‑end workflow diagram on page 14) is the primary source. The standalone PNG workflow image is materially **the same diagram** as PDF §13, with only cosmetic label differences (e.g., "Phone call" vs "Phone / Calls", "Real estate" vs "Real Estate Developer"). No content conflict exists between the two diagrams themselves.

The real conflicts are **between the PDF's diagram and the PDF's own prose/tables** (§1–§12). These are not assumptions on my part — they are direct textual contradictions in the source document, and are called out inline below and consolidated in **Section 9**. The most significant:

| # | Conflict | Where |
|---|---|---|
| 1 | Diagram says SLA timer starts at **"Assign Ticket to Owner"**; §4 table says **"SLA Timer: Auto-starts on ticket creation."** | Diagram vs §4 |
| 2 | Core Rule / SOP Step 02 say **no ticket without a verified unit number**; §2's channel table marks App/Website, WhatsApp/Live Chat, and Kiosk as **"Auto-ticket: Yes"** — i.e., ticket is created automatically on submission, before/without a live agent verifying a CRM match. | §1/§3 vs §2 |
| 3 | §2 lists **Social Media (Instagram/LinkedIn/Facebook)** as a fifth, manually-opened channel; the diagram folds it into "Digital," implying (incorrectly) the same auto-ticket behavior as App/Website. | §2 vs diagram |
| 4 | Diagram's escalation path is a flat "Escalate → Dept Head + GM"; §7 defines **four distinct levels** (Agent → Dept Head → GM → Chairman/CEO) with different triggers and authorities. | Diagram vs §7 |
| 5 | §6 says Critical breach triggers **"Immediate GM notification"** (Level 3); §7's general model routes every breach through Dept Head (Level 2) first. Unclear whether Critical skips Level 2. | §6 vs §7 |

Also note: the task brief that commissioned this analysis referred to a **"Genesys Call Center"** integration. The source PDF and both diagrams consistently and only use **"Geyness"** — the named, signing vendor ("Powered by Geyness Call Center," "Accepted by Geyness"). This is treated as a naming discrepancy in the *brief*, not the requirements, and is **not** resolved by assuming Genesys (a distinct, unrelated CCaaS product) is involved. See **ISSUE-003**.

---

## 1. Executive Summary

Tiger Group is commissioning a unified Customer Service Ticketing System to sit behind four intake channels (phone, digital, face‑to‑face/kiosk, WhatsApp/live chat — plus social media DMs per §2) and in front of three operating entities (Real Estate Developer, Leasing, Facility Management). Geyness Call Center is the outsourced human interface: its agents verify every caller against Tiger Group's CRM using **unit/room number as the sole primary identifier**, open a ticket, classify and prioritize it, and the system auto-routes it to the correct department with an SLA clock attached.

The requirements are unusually well specified for a first-pass document — SLA tiers, a four-level escalation path, CSAT mechanics, a full reporting cadence (daily/weekly/monthly/ad-hoc), and a live KPI dashboard with explicit targets and alert thresholds are all defined with numbers, not generalities. This is a strength: it gives the engineering team concrete acceptance criteria for SLA timers, report content, and dashboard thresholds without guesswork.

The document also has real gaps that matter architecturally, not just cosmetically:

- The **unit-number-as-sole-identifier rule** has no defined path for prospects who don't own a unit yet (new sales leads), and is contradicted by channels that auto-create tickets before verification.
- **SLA start time** (creation vs. assignment) is contradicted between the diagram and the field spec — this changes what "SLA compliance %" means and must be resolved before a single line of timer code is written.
- **Ticket lifecycle** as specified (6 statuses) has no reopen, cancellation, duplicate, rejected, or pending-third-party states, despite FM/Leasing workflows realistically needing all of them (e.g., waiting on a contractor, waiting on DEWA, waiting on a legal notice).
- **Multi-party units** (joint owners, outgoing/incoming tenant during handover) are acknowledged as a risk ("prevents data mixing") but not solved.
- No holiday calendar source, no escalation-window duration for Level 2→3, no CRM-downtime ticket-creation fallback, and no department-transfer rule are specified.

None of these gaps block starting architecture and MVP-scoping work — they block finalizing the **SLA engine, status model, and CRM adapter contract**, which are exactly the components most expensive to change after the fact. Section 13 lists the specific management decisions needed before those three components are locked, and Section 15 proposes an MVP that can proceed now using safe, documented defaults for anything still open.

**Recommended architecture** (Section 10): a **modular monolith** on .NET 8 — a single deployable ASP.NET Core solution with strongly separated modules (Ticketing, SLA/Escalation, Routing, Notifications, Reporting/KPI, Identity/Audit, Integration Gateways), communicating in-process via a mediator/domain-event pattern, with Hangfire driving SLA/report jobs and SignalR pushing live ticket/dashboard updates. This matches the stated stack, avoids premature microservice overhead for a system with one write-heavy domain (tickets) and a handful of integrations, and keeps a future split (e.g., extracting Reporting or Notifications) possible without a rewrite.

---

## 2. Functional Requirements

Legend: **MVP** = required for first production release. **Future** = explicitly out of scope for MVP per Section 15. Every requirement traces to a PDF section unless marked **[ASSUMPTION]**, in which case no source rule exists and a reasonable default is proposed pending management confirmation (see Section 9/14).

### 2.1 Module: Channel Intake — `FR-CH-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-CH-01 | System shall accept ticket-originating contact from five channels: Phone, App/Website, Social Media DM, WhatsApp/Live Chat, Face-to-Face kiosk. | • All 5 channels selectable/tagged on a ticket • Channel is immutable once set (§4 "Channel Tag") | §2, §4 | MVP |
| FR-CH-02 | Phone and Social Media channels shall **not** auto-create a ticket; an agent must open it manually after live/async verification. | • No ticket record exists until agent submits the verify+create form • Channel tag = Phone or Social records "agent-opened" flag = true | §2 | MVP |
| FR-CH-03 | App/Website, WhatsApp/Live Chat, and Kiosk channels shall auto-create a ticket on submission. | • Ticket record created within [ASSUMPTION] 5 seconds of gateway submission • See **ISSUE-002** for the unresolved conflict with mandatory pre-creation verification | §2 | MVP |
| FR-CH-04 | Kiosk (Face-to-Face/Office Screen) shall present a Tiger Group-branded on-screen form that submits directly to the ticketing system; interface design requires Tiger Group IT sign-off before deployment. | • Kiosk UI reviewed/approved workflow recorded • Kiosk submission produces the same ticket schema as other channels | §2, §11 | MVP |
| FR-CH-05 | WhatsApp/Live Chat messages shall auto-route to an available agent's queue. | • Queue assignment logged with agent ID and timestamp • No message sits unassigned beyond [ASSUMPTION] 60 seconds without a queue-overflow alert | §2 | MVP |
| FR-CH-06 | Social Media DMs (Instagram, LinkedIn, Facebook) shall be monitored and manually converted to tickets by Geyness agents. | • Each supported platform has a documented inbox/monitoring surface for agents • Conversion preserves the original message thread as the ticket's initial note/attachment | §2 | Future (MVP: manual copy-paste acceptable; native inbox integration is Future — see **INT-07**) |

### 2.2 Module: Customer & Unit Verification — `FR-VER-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-VER-01 | Unit/room number is the sole primary lookup key for any customer record. Agents must not accept name or phone number as the primary lookup key. | • CRM lookup UI has no name/phone-only search path for ticket creation • Attempting to create a ticket without a resolved unit number is blocked | §1 (Core Rule) | MVP |
| FR-VER-02 | On phone/social channels, the agent must ask for the unit/room number, pull the CRM record, and confirm the match before proceeding. | • Ticket-creation form is disabled until CRM match = true • "No match found" path forces an escalate-to-supervisor action, not silent ticket creation | §3 Step 02 | MVP |
| FR-VER-03 | Agent must read back name, property, tower, and unit type from the CRM record to the caller before proceeding, to prevent cross-owner/tenant data mixing. | • Read-back fields displayed prominently on agent screen • Agent must click "Confirmed by customer" before continuing | §3 Step 03 | MVP |
| FR-VER-04 | Where a unit record lists multiple contacts (joint owners, multiple tenants), the system shall require the agent to identify which specific contact is on the line, not just the unit. **[ASSUMPTION]** — no rule given; see **ISSUE-007**. | • Ticket stores a `contact_id` alongside `unit_id` • Agent cannot proceed past verification with unit matched but contact ambiguous | Diagram §3 note + [ASSUMPTION] | MVP |
| FR-VER-05 | Auto-ticket channels (App/Website/WhatsApp/Kiosk) shall still resolve the submitter to a CRM unit record, either via authenticated app/portal session or a required unit-number field on the form. | • No ticket persists in "confirmed" state without a resolved `unit_id` • Unresolved submissions are held in a distinct pending-verification state (see **ISSUE-002**, FR-TKT-09) | [ASSUMPTION] reconciling §1 vs §2 | MVP |
| FR-VER-06 | CRM downtime shall not silently block ticket intake for Critical/High priority contacts. **[ASSUMPTION]** — see **ISSUE-006**. | • Provisional ticket can be created with unverified unit reference, flagged `PendingCrmVerification` • Auto-escalated to supervisor if not reconciled within 15 minutes (§11) | [ASSUMPTION] | MVP |

### 2.3 Module: Ticketing Engine — `FR-TKT-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-TKT-01 | Every ticket has an auto-generated unique ID in format `TG-[DEPT]-[YYYYMMDD]-[SEQ]`. | • ID is generated server-side, never client-supplied • Uniqueness enforced at DB level (unique index) | §4 | MVP |
| FR-TKT-02 | Unit Number is the mandatory primary key field, validated against CRM before ticket creation completes. | • Ticket cannot reach `Open` status without a validated unit reference | §4 | MVP |
| FR-TKT-03 | Timestamp of creation is auto-stamped and non-editable. | • `CreatedAtUtc` set server-side; no UI/API path can modify it post-creation | §4 | MVP |
| FR-TKT-04 | Channel is tagged from a fixed enum: Phone / App / Website / Social / WhatsApp / Face-to-Face. | • Enum enforced at API boundary; invalid values rejected | §4 | MVP |
| FR-TKT-05 | Agent ID auto-links to the logged-in Geyness agent (or system, for auto-ticket channels). | • Populated from the authenticated identity context, not user-entered | §4 | MVP |
| FR-TKT-06 | Ticket carries a free-text Request Summary and up to 10 attachments (images, PDFs, videos), stored against the unit record. | • Upload rejects an 11th file • Each attachment is virus-scanned before storage [ASSUMPTION: max 25MB/file — no limit given in source] | §3 Step 04, §4 | MVP |
| FR-TKT-07 | All status changes, assignments, and notes are timestamped and attributed to the acting user (audit trail). | • Every mutating action produces an immutable audit record • Audit record includes actor, action, before/after values, timestamp | §4 | MVP |
| FR-TKT-08 | Ticket status is one of the six PDF-defined values: Open / In Progress / Pending Customer / Escalated / Resolved / Closed, extended per Section 5 of this document to also cover Reopened, Cancelled, Rejected, Duplicate, and Pending Third-Party. | • State machine enforces only the transitions defined in Section 5 | §4 + Section 5 of this doc (extension flagged in **ISSUE-008**) | MVP (extended statuses) / base 6 statuses are MVP, extended statuses recommended MVP-in per Section 15 |
| FR-TKT-09 | A ticket created via an auto-ticket channel without a resolved CRM match is held in a `PendingCrmVerification` sub-state and does not start its SLA clock, notify the customer with a final ticket number, or route to a department until verified. | • No department-visible ticket exists in this sub-state • Agent queue surfaces these for manual reconciliation | [ASSUMPTION] resolving **ISSUE-002** | MVP |
| FR-TKT-10 | Resolution Note is a mandatory free-text field before closure, stored permanently against the unit number. | • Close action is disabled in UI/API until note is non-empty | §4, §8 | MVP |

### 2.4 Module: Classification & Priority — `FR-CLS-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-CLS-01 | Agent selects exactly one primary category: Sales Enquiry, Leasing, Facility Management, Complaint, General Information. | • Category is a single-select, mandatory field | §3 Step 05 | MVP |
| FR-CLS-02 | FM tickets require a mandatory sub-category: Corrective Maintenance, Preventive, Common Area, Emergency. | • Sub-category field only appears/required when category = Facility Management | §3 Step 05 | MVP |
| FR-CLS-03 | Agent sets priority: Critical (safety/flooding/fire/access failure), High (habitability/legal deadline), Medium (standard maintenance/contract queries), Low (general info/documentation). | • Priority is mandatory, single-select • Definitions surfaced as inline agent guidance | §3 Step 06 | MVP |
| FR-CLS-04 | Priority may additionally be auto-suggested by keyword triggers on the request summary text. | • Auto-suggestion is advisory only; agent can accept or override • Every override is logged (agent value vs. system-suggested value) for QA/AI-tuning purposes | §4 ("auto-set by keyword triggers") | **AI-assisted / Future** — deterministic manual selection is MVP; keyword auto-suggestion is explicitly flagged as AI-assisted, not a deterministic system rule (see Section 6) |

### 2.5 Module: Routing & Assignment — `FR-RTE-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-RTE-01 | System auto-routes a ticket to Real Estate, Leasing, or Facility Management based on category/sub-category. | • Routing table is data-driven (category→department mapping), not hardcoded, so Tiger can adjust it without a deployment | §3 Step 07, §5 | MVP |
| FR-RTE-02 | Agent verbally confirms routing and reads the ticket number to the customer before ending a live interaction (Phone/F2F). | • UI surfaces the routed department + ticket number prominently for agent read-back • Not applicable/skipped for async auto-ticket channels | §3 Step 07 | MVP |
| FR-RTE-03 | Ticket is assigned to a named staff member (ticket owner) within the routed department; this action starts the SLA timer per the diagram's stated sequence. | • Every ticket beyond `PendingCrmVerification`/routing has exactly one current owner • Ownership changes are audited | Diagram §13 (**conflicts with §4** — see **ISSUE-001**) | MVP |
| FR-RTE-04 | A ticket can be transferred between departments with a mandatory reason code. **[ASSUMPTION]** — no rule exists; see **ISSUE-010**. | • Transfer requires reason code + note • Transfer is audited with from/to department, actor, timestamp • SLA clock behavior on transfer is configurable (see BR-018) | [ASSUMPTION] | MVP |
| FR-RTE-05 | A ticket can be reassigned to a different owner within the same department without a formal transfer. | • Reassignment logged • Original SLA timer, if creation-based, is unaffected; if assignment-based, restarts per BR-017 | [ASSUMPTION] | MVP |

### 2.6 Module: SLA & Timer Engine — `FR-SLA-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-SLA-01 | Each ticket has a first-response and resolution SLA target based on its priority tier (Critical/High/Medium/Low), per the table in Section 7. | • Targets are configuration, not code, so Tiger can retune without redeploying | §6 | MVP |
| FR-SLA-02 | Critical-priority SLA timers run 24/7 (calendar time). | • Timer calculation for Critical ignores business-hours/weekend/holiday calendar entirely | §6 | MVP |
| FR-SLA-03 | High/Medium/Low SLA timers run only during business hours (08:00–18:00, Saturday–Thursday per the source document) and pause outside that window, including weekends and public holidays. | • Timer service excludes non-business intervals from elapsed-time calculation • Holiday calendar is a maintained reference table (see **ISSUE-012**) | §6 | MVP |
| FR-SLA-04 | SLA timer is visible to the ticket owner and department as a live countdown. | • SignalR push updates the countdown without page reload • Countdown reflects paused/running state correctly | §4 ("countdown visible") | MVP |
| FR-SLA-05 | System raises a warning before SLA breach (not just at breach). **[ASSUMPTION]** — no threshold specified; see **ISSUE-013**. | • Warning fires at [ASSUMPTION] 75% of resolution-target elapsed • Warning is visually distinct from breach | [ASSUMPTION] | MVP |
| FR-SLA-06 | SLA breach triggers the priority-specific alert defined in Section 7 (Supervisor / Dept Head / Dept Head+GM / immediate GM). | • Alert recipients match Section 7 table exactly • Alert is logged as a notification event, retryable on delivery failure | §6, §7 | MVP |
| FR-SLA-07 | Reassignment or department transfer of a ticket does not, by itself, reset or pause the SLA clock unless explicitly configured to do so per priority tier. **[ASSUMPTION]** — see BR-018. | • Config flag per priority tier controls reset-on-transfer behavior • Default = no reset | [ASSUMPTION] | MVP |
| FR-SLA-08 | A priority change on an existing ticket recalculates the remaining SLA against the new tier's targets, applied from the moment of change (not retroactively). | • Elapsed time already consumed under the old tier carries forward proportionally; only future ticking uses the new tier's calendar rules | [ASSUMPTION] | MVP |

### 2.7 Module: Notifications — `FR-NOT-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-NOT-01 | On ticket creation/routing, system sends an automated SMS and email acknowledgement with ticket number, expected response time, assigned department, and Geyness reference. | • Both channels attempted; failure of one does not block the other • Content fields match spec exactly | §3 Step 08 | MVP |
| FR-NOT-02 | SLA breach notifications route to the recipients defined in Section 7, via [ASSUMPTION: email + in-app; SMS for Critical] since delivery channel per alert type is not specified in the source. | • Recipient + channel matrix configurable per priority tier | §6, §7 + [ASSUMPTION] | MVP |
| FR-NOT-03 | CSAT survey is auto-sent via SMS and email on transition to Closed. | • Trigger fires exactly once per closure (not on reopen-then-reclose without explicit resend rule — see **ISSUE-009**) | §4, §8 | MVP |
| FR-NOT-04 | Low CSAT (average score < 3.0) triggers an alert to the Geyness Account Manager and Tiger Group CS Manager within 24 hours. | • Alert generation is event-driven on survey submission, not a delayed batch job outside the 24h window | §8 | MVP |
| FR-NOT-05 | All notification sends/failures are logged and retryable (see NFR-MON, NFR-LOG). | • Failed sends visible in an operational queue • Retry policy configurable (attempts, backoff) | [ASSUMPTION — standard integration hygiene] | MVP |

### 2.8 Module: Escalation — `FR-ESC-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-ESC-01 | Level 1 (Agent) can manually flag a ticket for escalation if unable to resolve. | • Flag action available on any owned ticket • Requires a reason note | §7 | MVP |
| FR-ESC-02 | Level 2 (Department Head) escalation triggers automatically on SLA breach or agent flag; Dept Head must respond within 2 hours of escalation. | • Escalation event auto-creates a tracked SLA of its own (2h response clock) • Breach of the 2h clock is itself alertable | §7 | MVP |
| FR-ESC-03 | Level 3 (GM) triggers if Level 2 does not resolve within "the next escalation window." **[ASSUMPTION — window undefined]**, see **ISSUE-013**. | • Window is a configurable duration, defaulted per Section 9's recommendation, until management specifies it | §7 + [ASSUMPTION] | MVP |
| FR-ESC-04 | Level 4 (Chairman/CEO) is reserved for legal threats, media escalations, high-profile investor complaints, and is manual-only (never system-triggered). | • No automated rule can create a Level 4 escalation • Only specific roles (CS Manager, GM) can invoke it | §7 | MVP |
| FR-ESC-05 | Every escalation is logged against the unit number with full audit trail (timestamps, agent notes, resolution actions per level). | • Escalation history is queryable per unit and per ticket | §7 | MVP |
| FR-ESC-06 | An escalated ticket that returns to normal work ("re-assign & retry" per diagram) re-enters the resolution loop and is re-evaluated against its SLA on the next check. | • Loop does not permit infinite silent recycling — after [ASSUMPTION] 2 retry cycles without resolution, escalation auto-advances a level | Diagram + [ASSUMPTION] resolving **ISSUE-005** | MVP |

### 2.9 Module: Resolution & Closure — `FR-RES-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-RES-01 | A ticket can only close when (a) resolution note is completed AND (b) customer has been notified. | • Close action is a guarded transition; both conditions independently verified server-side, not just UI-gated | §8 | MVP |
| FR-RES-02 | Resolution note is permanently retained against the unit number in the CRM-linked record, even if the ticket itself is later archived. | • Note remains queryable via unit history after ticket retention/archival events | §8 | MVP |
| FR-RES-03 | A closed ticket can be reopened under defined conditions (see BR-020/Section 5). **[ASSUMPTION — no source rule]**, see **ISSUE-011**. | • Reopen creates a new status entry without destroying original audit history • Reopen is time-bounded (default 7 days) | [ASSUMPTION] | MVP |
| FR-RES-04 | A ticket can be cancelled (customer withdraws) or rejected (invalid/duplicate/out of scope) as terminal states distinct from Resolved. **[ASSUMPTION]** | • Both require a reason code • Neither triggers a CSAT survey (BR-023) | [ASSUMPTION] | MVP |
| FR-RES-05 | A ticket can enter Pending Third-Party (waiting on an external actor: contractor, DEWA, developer's legal team, etc.), pausing the customer-facing clock differently from Pending Customer. **[ASSUMPTION]** | • Distinct status from Pending Customer for correct SLA-pause attribution • Requires a note naming the third party and expected date | [ASSUMPTION] | MVP |

### 2.10 Module: CSAT — `FR-CSAT-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-CSAT-01 | 5-question survey covering Speed, Agent Professionalism, Issue Resolution, Communication, Overall Satisfaction; 1–5 star scale per question; optional free-text comment. | • Survey schema matches exactly; all 5 questions mandatory, comment optional | §8 | MVP |
| FR-CSAT-02 | Responses stored against unit number and ticket ID. | • Retrievable per unit, per ticket, and aggregable per department/agent | §8 | MVP |
| FR-CSAT-03 | Average score below 3.0 triggers the low-CSAT alert (FR-NOT-04). | • Threshold configurable | §8 | MVP |

### 2.11 Module: Reporting — `FR-RPT-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-RPT-01 | Daily Flash Report generated by 9:00 AM every business day for the CS Manager: tickets opened/closed prior day, open count by priority, Critical tickets in last 24h with status, SLA breaches in last 24h. | • Hangfire job produces and delivers report by 09:00 local time • Content matches §9 field list exactly | §9 | MVP |
| FR-RPT-02 | Weekly Performance Report every Monday by 10:00 AM to CS Manager + all Dept Heads, with the full field set in §9 (channel/department volumes, response/resolution vs. SLA, compliance %, top-10 issues, top-5 buildings, escalation count/reasons, CSAT by department, agent performance summary). | • All listed fields present • Delivered to full distribution list | §9 | MVP |
| FR-RPT-03 | Monthly Management Report on the 1st of each month to Senior Management + CS Manager, with MoM trends, scorecard, root-cause categories, headcount/availability, proposed actions. | • Generated on schedule • "Proposed improvements" section supports free-text/manual input by Geyness before send (not fully automatable) | §9 | MVP |
| FR-RPT-04 | Ad Hoc/Incident Report within 4 hours of any Critical ticket, to GM + CS Manager: incident description, unit number, timeline, actions per escalation level, current status/ETA, media/legal/reputational risk flag. | • Triggered automatically on Critical ticket creation, not manually initiated • 4-hour delivery SLA is itself monitored | §10 | MVP |
| FR-RPT-05 | All reports are generated from ticketing-system data with no manual manipulation. | • Report generation is a deterministic, auditable job; no ad-hoc spreadsheet editing step in the pipeline | §9 | MVP |
| FR-RPT-06 | Tiger Group retains full read/export access to underlying data at all times, independent of report generation. | • Export capability is a first-class, always-available feature, not contingent on Geyness action | §11 | MVP |

### 2.12 Module: KPI Dashboard — `FR-KPI-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-KPI-01 | Live, web-based dashboard accessible 24/7 to Tiger Group showing: First Contact Resolution Rate, Avg First Response Time, Avg Resolution Time, SLA Compliance Rate, Ticket Backlog, CSAT Average, Escalation Rate, Repeat Contact Rate, Channel Distribution, Agent Utilisation. | • Every metric in §10's table is present with its stated target and alert threshold • Dashboard reflects near-real-time data (see NFR-PERF) | §10 | MVP |
| FR-KPI-02 | Each KPI visually flags when it crosses its alert threshold (e.g., SLA Compliance < 80%, any Critical ticket backlog > 4h). | • Threshold breach is visually distinct (color/badge) and does not require the viewer to compute it manually | §10 | MVP |
| FR-KPI-03 | Repeat Contact Rate requires an operational definition of "same issue, same customer contacting again," which the source does not define. **[ASSUMPTION]**, see **ISSUE-014**. | • Default definition: same unit + same category within a rolling 7-day window, flagged for agent confirmation, not auto-merged | [ASSUMPTION] | Future (metric displayed as "provisional" in MVP) |

### 2.13 Module: Administration, Roles & Audit — `FR-ADM-##`

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-ADM-01 | Role-based access control across all roles in Section 3, backed by ASP.NET Core Identity. | • Every action in the permission matrix (Section 3) is enforced server-side, not just hidden in UI | §11 (Security: "Access must be role-based") | MVP |
| FR-ADM-02 | Agent access is revoked within 24 hours of staff departure. | • Deactivation workflow with SLA-tracked completion; overdue deactivations alert System Administrator | §11 | MVP |
| FR-ADM-03 | Full audit trail of status changes, assignments, notes, escalations, exports, and admin actions, attributed to user and timestamped. | • Audit log is append-only, queryable by ticket/unit/user/date range | §4, §7, §11 | MVP |
| FR-ADM-04 | Tiger Group data (tickets, customer records, interaction logs) is exclusively Tiger Group's property; Geyness cannot use it for any purpose outside the engagement. | • Data-access boundary enforced technically (no Geyness export path outside the contracted workspace), not just contractually | §11 | MVP |
| FR-ADM-05 | Full data export on demand, delivered within 24 hours of request. | • Export job available to authorized roles; large exports run as background jobs (Hangfire), not synchronous requests | §12 | MVP |
| FR-ADM-06 | System/integration downtime is detected and reported within 15 minutes. | • Health checks + alerting pipeline meets the 15-minute detection SLA (see NFR-MON) | §11, §12 | MVP |

### 2.14 Module: AI-Assisted Features — `FR-AI-##` (Future)

| ID | Requirement | Acceptance Criteria | Source | Tier |
|---|---|---|---|---|
| FR-AI-01 | Keyword-based priority suggestion (FR-CLS-04) evolves into a trained classifier suggesting category + priority from free-text request summary. | • Suggestion always advisory; agent override always logged for retraining | §4 (extrapolated) | Future |
| FR-AI-02 | Predictive SLA-breach risk scoring surfaced to Dept Heads before breach occurs. | • Score is explainable (top contributing factors shown), not a black box | [ASSUMPTION] | Future |
| FR-AI-03 | Chatbot/virtual agent for common Low-priority/General Information requests on Digital/WhatsApp channels. | • Bot handoff to human agent is seamless and preserves full conversation context | [ASSUMPTION] | Future |
| FR-AI-04 | Root-cause clustering for the Monthly Report's "recommended root cause" field, currently manual per §9. | • Clustering output is reviewed/edited by CS Manager before distribution, never auto-published | §9 (currently manual) | Future |

---

## 3. Non-Functional Requirements

| ID | Category | Requirement | Source |
|---|---|---|---|
| NFR-SEC-01 | Security | All customer data encrypted at rest and in transit (TLS 1.2+, encrypted DB columns/TDE for PII). | §11 |
| NFR-SEC-02 | Security | Role-based access enforced server-side on every ticketing/reporting endpoint. | §11 |
| NFR-SEC-03 | Security | Agent access revoked ≤ 24h from staff departure; access reviews logged. | §11 |
| NFR-SEC-04 | Security | Geyness technically cannot export/use Tiger data outside the contracted workspace (tenant isolation, not just policy). | §11 |
| NFR-SEC-05 | Security | Kiosk/public-facing endpoints (Face-to-Face form, WhatsApp webhook) are hardened against injection/spoofing given they accept unauthenticated or low-trust input. | [ASSUMPTION — standard practice, not explicit in source] |
| NFR-PERF-01 | Performance | CRM unit-number lookup returns within a real-time-feeling window (§11 says "real time"); target [ASSUMPTION] p95 < 1.5s. | §11 + [ASSUMPTION] |
| NFR-PERF-02 | Performance | KPI dashboard reflects ticket-state changes within [ASSUMPTION] 10 seconds (SignalR push), consistent with "real time" language in §4/§10. | §4, §10 + [ASSUMPTION] |
| NFR-SCALE-01 | Scalability | System sized for Tiger Group's full portfolio across Real Estate/Leasing/FM; exact volume TBD (see **ISSUE-015**) — architecture must not hard-code assumptions that block horizontal scaling of the API tier. | [ASSUMPTION] |
| NFR-AVAIL-01 | Availability | Ticketing system and CRM integration maintain ≥ 99.5% uptime; planned maintenance communicated 48h in advance. | §11 |
| NFR-AVAIL-02 | Availability | CRM downtime escalated within 15 minutes (see FR-ADM-06, FR-VER-06). | §11 |
| NFR-AUDIT-01 | Auditability | Every status change, assignment, escalation, note, export, and admin action is attributed and timestamped, immutable once written. | §4, §7, §11 |
| NFR-RETAIN-01 | Data retention | All records retained ≥ 7 years, stated as aligned to "UAE regulatory requirements" (exact statute not cited in source — see **ISSUE-016**). | §11 |
| NFR-BCDR-01 | Backup/Recovery | Full daily backup; RPO 24 hours; RTO 4 hours. | §11 |
| NFR-A11Y-01 | Accessibility | Customer-facing surfaces (kiosk UI, web/app forms, CSAT survey links) should meet WCAG 2.1 AA at minimum, given kiosk use by a general public/resident population. | [ASSUMPTION — not specified in source] |
| NFR-MON-01 | Monitoring | Integration/system downtime detected and alerted within 15 minutes (matches CRM-downtime SLA). | §11, §12 |
| NFR-MON-02 | Monitoring | KPI alert thresholds (Section 10's table) are monitored continuously, not just at report time. | §10 |
| NFR-LOG-01 | Logging | Structured logging across API/background jobs sufficient to reconstruct any SLA/escalation calculation for audit or dispute. | [ASSUMPTION — implied by audit requirements] |
| NFR-UAE-01 | UAE data/business-time | Business-hour SLA calculation uses 08:00–18:00, Saturday–Thursday per source text. **Flag:** this differs from the UAE federal government's post-2022 Saturday–Sunday weekend; confirm this is Tiger's/Geyness's actual operating week, not a document drafting error (see **ISSUE-017**). | §6 |
| NFR-UAE-02 | UAE data/business-time | UAE public holiday calendar must pause non-Critical SLA clocks; no calendar source is specified in the requirements (see **ISSUE-012**). | [ASSUMPTION] |
| NFR-UAE-03 | UAE data/business-time | Consider UAE data-residency expectations for customer PII (common in UAE real-estate/PDPL context) when selecting SQL Server hosting region. | [ASSUMPTION — not specified in source] |

---

## 4. User Roles and Permissions

Roles below combine those explicitly named in the PDF (Geyness Agent, Supervisor, Department Head, General Manager, Chairman/CEO, CS Manager) with roles required by the requested matrix but not explicitly named in the source (Department Employee, System Administrator, Reporting User, Customer) — the latter are marked **[ASSUMPTION]** as reasonable operational roles any enterprise ticketing system needs, not sourced from PDF text.

| Role | Source |
|---|---|
| Geyness Agent | §3, §7 (Level 1) |
| Supervisor | §3 Step 02, §6 (Low-priority breach alert) |
| Department Employee (RE/Leasing/FM staff, ticket owner) | Diagram ("Assign Ticket to Owner: Named staff") — role name is **[ASSUMPTION]** |
| Department Head | §7 (Level 2) |
| CS Manager (Tiger Group) | §9, §12 (report recipient, weekly KPI review owner) |
| General Manager | §7 (Level 3), §6 |
| Chairman/CEO | §7 (Level 4) |
| System Administrator | **[ASSUMPTION]** — standard requirement for any RBAC system; not named in source |
| Reporting User (view/export only, e.g. Senior Management recipients not otherwise privileged) | **[ASSUMPTION]** derived from §9's "Tiger Group Senior Management" report recipients |
| Customer | Implicit throughout (raises tickets, receives notifications/CSAT) |

### 4.1 Permission Matrix

`V`=View `C`=Create `E`=Edit `A`=Assign `T`=Transfer `Esc`=Escalate `Res`=Resolve `Cl`=Close `Reo`=Reopen `Ex`=Export `Adm`=Admin

| Role | V | C | E | A | T | Esc | Res | Cl | Reo | Ex | Adm |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Geyness Agent | Own dept queue | ✔ | Own tickets | — | — | ✔ (to Supervisor) | Own tickets | Own tickets (if resolution note present) | Via customer contact | — | — |
| Supervisor | Team queue | ✔ | Team tickets | ✔ (within team) | — | ✔ | ✔ | ✔ | ✔ | Team reports | — |
| Department Employee | Own/assigned tickets | — (unless also agent) | Own tickets | — | Request only | ✔ | ✔ | ✔ | Via agent/CS Manager | — | — |
| Department Head | All dept tickets | — | All dept tickets | ✔ | ✔ (approve) | ✔ (escalate/receive) | ✔ | ✔ | ✔ | Dept reports | Dept config (routing rules) |
| CS Manager | All tickets, all depts | — | All tickets | ✔ | ✔ | ✔ (receive/initiate) | — | ✔ (override) | ✔ | All reports | User/role assignment |
| General Manager | All tickets | — | — | — | — | ✔ (receive Level 3, initiate Level 4) | — | — | — | All reports/dashboard | — |
| Chairman/CEO | All tickets (read) | — | — | — | — | ✔ (receive Level 4 only) | — | — | — | Executive reports/dashboard | — |
| System Administrator | All (technical) | — | — | — | — | — | — | — | — | All | ✔ Full (users, roles, config, integrations) |
| Reporting User | Reports/dashboard only | — | — | — | — | — | — | — | — | ✔ | — |
| Customer | Own tickets/unit only | ✔ (own tickets, via channel) | — | — | — | — | — | — | Request reopen (time-bounded) | Own ticket history | — |

**Note:** cells marked "—" are explicit denials, not omissions; a Customer, for example, must never see another unit's tickets even if they are a joint contact elsewhere in the portfolio — this is the multi-party-unit boundary flagged in **ISSUE-007**.

---

## 5. Ticket Lifecycle

The PDF defines six statuses (§4): **Open, In Progress, Pending Customer, Escalated, Resolved, Closed**. The task's required lifecycle coverage (reopen, cancellation, duplicate, rejected, pending third-party) is **not** present in the source — these are proposed extensions, marked **[ASSUMPTION]**, needed to make the six-status model operable for FM/Leasing realities (e.g., waiting on a contractor is not "Pending Customer").

### 5.1 Status Definitions

| Status | Meaning | Source |
|---|---|---|
| Open | Ticket created and verified, not yet actioned by department. | §4 |
| In Progress | Department actively working the request. | §4 |
| Pending Customer | Waiting on the customer for information/action. | §4 |
| Pending Third-Party | Waiting on an external party (contractor, DEWA, legal, developer). | **[ASSUMPTION]** |
| Escalated | Breached SLA or manually flagged; under escalation-path handling. | §4, §7 |
| Resolved | Work completed, awaiting closure gate. | §4 |
| Closed | Resolution note complete + customer notified (§8 closure criteria). | §4, §8 |
| Reopened | Previously closed ticket revived because the same issue recurred. | **[ASSUMPTION]** |
| Cancelled | Customer withdrew the request before resolution. | **[ASSUMPTION]** |
| Rejected | Determined invalid, duplicate, or out of scope. | **[ASSUMPTION]** |
| Duplicate | Linked to an existing open/resolved ticket for the same unit/issue. | **[ASSUMPTION]** |

### 5.2 Transition Table

| From | To | Allowed Roles | Required Fields / Validation |
|---|---|---|---|
| (none) | Open | Geyness Agent, Auto-system (channels per FR-CH-03) | Verified unit_id + contact_id, category, priority, channel |
| (none) | Pending CRM Verification (sub-state of Open) | Auto-system | Unresolved unit reference; must reconcile ≤ 15 min or escalate (FR-VER-06) |
| Open | In Progress | Department Employee, Dept Head | Ticket owner assigned |
| In Progress | Pending Customer | Department Employee | Note stating what is awaited from customer |
| In Progress | Pending Third-Party | Department Employee | Note naming third party + expected date **[ASSUMPTION]** |
| Pending Customer / Pending Third-Party | In Progress | Department Employee, Agent (on customer contact) | Note confirming resumption |
| In Progress / Pending * | Escalated | Auto-system (SLA breach), Agent/Employee/Supervisor (manual flag) | Escalation reason (auto or manual) |
| Escalated | In Progress | Dept Head, GM | Re-assignment target + retry note (diagram's "re-assign & retry") |
| In Progress | Resolved | Department Employee, Dept Head | Resolution note (mandatory, §8) |
| Resolved | Closed | Department Employee, Dept Head, CS Manager | Customer notification confirmed sent (§8) |
| Closed | Reopened | Customer (via channel), Geyness Agent, Department Employee | Within reopen window (default 7 days, **[ASSUMPTION]**); links to original ticket ID |
| Reopened | In Progress | Department Employee | Same as Open→In Progress |
| Open / In Progress | Cancelled | Customer request via Agent, Department Employee | Reason code; no CSAT sent (BR-023) |
| Open | Rejected | Department Employee, Dept Head | Reason code (invalid/duplicate/out of scope); no CSAT sent |
| Open | Duplicate | Geyness Agent, Department Employee | Link to the original ticket ID; original ticket unaffected |

All transitions above are **[ASSUMPTION]**-extended beyond the PDF's literal six statuses; the underlying Open→In Progress→(Pending)→Resolved→Closed spine, and the Escalated branch, are sourced directly from §4/§7/§8/diagram.

---

## 6. Business Rules

Deterministic system rules are numbered `BR-###`. Rules explicitly flagged **(AI-assisted)** are advisory suggestions a human must confirm — never auto-executed state changes.

| ID | Rule | Source |
|---|---|---|
| BR-001 | Unit/room number is the only valid primary identifier for customer lookup; name/phone alone must never resolve a customer record for ticket purposes. | §1 |
| BR-002 | No ticket reaches a department-visible state without a CRM-verified unit match (see **ISSUE-002** for the channel-timing conflict this creates). | §1, §3 |
| BR-003 | Agent must read back name/property/tower/unit type and receive customer confirmation before proceeding. | §3 Step 03 |
| BR-004 | Ticket ID format is `TG-[DEPT]-[YYYYMMDD]-[SEQ]`, server-generated, immutable. | §4 |
| BR-005 | Exactly one primary category per ticket; FM requires a mandatory sub-category. | §3 Step 05 |
| BR-006 | Priority is one of Critical/High/Medium/Low per the defined criteria in §3 Step 06. | §3 |
| BR-007 | **(AI-assisted)** Keyword triggers may suggest a priority; the agent's manual selection is the rule of record unless explicitly overridden by policy. | §4 |
| BR-008 | Department routing is derived deterministically from category/sub-category via a maintained mapping table. | §3 Step 07, §5 |
| BR-009 | Acknowledgement (SMS+email) is mandatory on every ticket, containing ticket number, expected response time, department, Geyness reference. | §3 Step 08 |
| BR-010 | Up to 10 attachments per ticket, stored against the unit record. | §4 |
| BR-011 | Resolution note is mandatory text before any Resolved/Closed transition. | §4, §8 |
| BR-012 | Closure requires resolution note AND confirmed customer notification — both conditions, not either. | §8 |
| BR-013 | CSAT survey auto-sends on Closed, 5 questions, 1–5 scale, optional comment. | §8 |
| BR-014 | Average CSAT < 3.0 auto-alerts Geyness Account Manager + Tiger CS Manager within 24h. | §8 |
| BR-015 | Every status change, assignment, and note is attributed and timestamped (audit trail). | §4 |
| BR-016 | Escalation levels 1–4 follow the fixed hierarchy and triggers in §7; Level 4 is manual-only, never system-triggered. | §7 |
| BR-017 | **[ASSUMPTION]** SLA timer's start event (creation vs. assignment) must be a single, explicitly configured point — not left to differ by module — pending management decision (**ISSUE-001**). | [ASSUMPTION] |
| BR-018 | **[ASSUMPTION]** Department transfer and reassignment SLA impact (reset vs. continue) is configurable per priority tier; default = continue (no reset), to prevent SLA gaming via repeated transfers. | [ASSUMPTION] |
| BR-019 | **[ASSUMPTION]** A priority change recalculates remaining SLA against the new tier from the moment of change; already-elapsed time is not retroactively rewritten. | [ASSUMPTION] |
| BR-020 | **[ASSUMPTION]** A closed ticket may be reopened within 7 days if the same issue recurs on the same unit; beyond that window, a new linked ticket is created instead. | [ASSUMPTION] |
| BR-021 | **[ASSUMPTION]** Two tickets are treated as possible duplicates when they share the same unit and category within a rolling 7-day window; the system flags for agent confirmation, never auto-merges. | [ASSUMPTION] |
| BR-022 | **[ASSUMPTION]** Cancelled and Rejected are terminal states distinct from Resolved; neither counts toward resolution-time KPIs, both require a reason code. | [ASSUMPTION] |
| BR-023 | **[ASSUMPTION]** CSAT survey is not sent on Cancelled or Rejected closure, only on genuine Resolved→Closed. | [ASSUMPTION] |
| BR-024 | **[ASSUMPTION]** A unit record with multiple contacts requires the agent to identify the specific contact on a call, in addition to the unit match, before proceeding. | [ASSUMPTION], motivated by §3 Step 03's stated intent |
| BR-025 | Data ownership: all ticket/customer/interaction data is Tiger Group property; Geyness may not use it outside this engagement. | §11 |
| BR-026 | Data retained ≥ 7 years. | §11 |

---

## 7. SLA and Escalation Rules

### 7.1 SLA Tiers (source: §6)

| Priority | Definition | First Response | Resolution Target | Clock Basis | Breach Action |
|---|---|---|---|---|---|
| Critical | Safety, flooding, fire, access failure | 15 minutes | 4 hours | 24/7 calendar time | Immediate GM notification |
| High | Habitability-affecting maintenance or legal deadlines | 1 hour | 24 hours | Business hours only | Dept Head + GM alert |
| Medium | Standard maintenance, contract queries | 4 hours | 3 business days | Business hours only | Dept Head alert |
| Low | General information, documentation requests | 24 hours | 7 business days | Business hours only | Supervisor alert |

**Business hours** (source, §6): 08:00–18:00, Saturday–Thursday, UAE calendar. **Flagged** in **ISSUE-017**: this is a single-day weekend (Friday), differing from the UAE federal government's Saturday–Sunday weekend adopted since January 2022 — confirm this is intentional for Tiger/Geyness's actual operating calendar.

### 7.2 SLA Pause/Resume — **[ASSUMPTION]**, no explicit rule given

Non-Critical timers should pause during: non-business hours, Fridays, UAE public holidays, and while status = Pending Customer or Pending Third-Party (customer/third-party-caused delay should not consume the department's SLA budget). They resume when status returns to In Progress within business hours. **This entire mechanic is an assumption** — the source only states that timers "run during official business hours," without addressing Pending-status pausing at all; see **ISSUE-018**.

### 7.3 Warning Thresholds — **[ASSUMPTION]**

No warning threshold is specified before breach; recommended default: warn at 75% of resolution-target elapsed time, escalate the warning itself to the ticket owner + Supervisor (not Dept Head, to avoid alert fatigue at Dept Head level for near-misses that still resolve in time).

### 7.4 Escalation Levels (source: §7)

| Level | Role | Trigger | Response Requirement |
|---|---|---|---|
| 1 | Geyness Agent | Own attempt at first-contact resolution | Flags for escalation if unable to resolve |
| 2 | Department Head | Auto on SLA breach or agent flag | Must respond within 2 hours |
| 3 | General Manager | Level 2 fails to resolve within "the next escalation window" **(undefined — ISSUE-013)** | Full authority to act |
| 4 | Chairman/CEO | Manual only — legal/media/high-profile investor complaints | No defined SLA (executive discretion) |

**Conflict flagged (ISSUE-004):** §6 says Critical breach = "Immediate GM notification" (i.e., straight to Level 3), while §7's general model routes every breach through Level 2 first. Recommended default pending confirmation: Critical breaches notify **both** Dept Head and GM simultaneously, rather than skipping Level 2 — preserves Dept Head's operational awareness while meeting the "immediate GM" requirement.

### 7.5 Reassignment / Priority-Change Impact

Per **BR-018/BR-019**: reassignment does not reset the SLA clock by default; a priority change recalculates remaining time under the new tier's rules from the moment of change forward. Both are assumptions pending management confirmation, chosen specifically to prevent SLA-gaming (e.g., repeatedly reassigning a breaching ticket to reset its clock).

### 7.6 Notification Recipients Summary

| Event | Recipients | Channel |
|---|---|---|
| Ticket acknowledgement | Customer | SMS + Email |
| Critical breach | GM (+ Dept Head, per resolved conflict above) | [ASSUMPTION] Email + SMS |
| High breach | Dept Head + GM | [ASSUMPTION] Email + in-app |
| Medium breach | Dept Head | In-app + Email |
| Low breach | Supervisor | In-app |
| Escalation Level 4 | Chairman/CEO | [ASSUMPTION] Email + direct notification (manual trigger) |
| Low CSAT (<3.0) | Geyness Account Manager, Tiger CS Manager | Email, within 24h |
| Critical incident (Ad Hoc report) | GM + CS Manager | Email, within 4h |

### 7.7 Example SLA Calculations

**Example A — Critical ticket:**
Ticket created Tuesday 22:40 (10:40 PM). Critical runs 24/7.
- First response due: 22:55 same day (15 min later).
- Resolution due: Wednesday 02:40 (4 hours later) — no business-hours exclusion applies.

**Example B — High-priority ticket, created outside business hours:**
Ticket created Thursday 17:30. Business hours: 08:00–18:00, Sat–Thu (per source text).
- Clock runs 17:30–18:00 Thursday = 30 minutes elapsed.
- Friday excluded entirely (non-business day per source).
- Clock resumes Saturday 08:00.
- First response target (1h): 30 min consumed Thursday, 30 min more needed → due Saturday 08:30.
- Resolution target (24h): 30 min consumed; 23.5h remaining, consumed at 10h/day (08:00–18:00) → Saturday 10h (08:00–18:00) leaves 13.5h remaining → due Sunday 13:30.

**Example C — Medium ticket, SLA-pause scenario (illustrating the assumption in §7.2, not a sourced rule):**
Ticket created Sunday 09:00 (Medium: 4h response / 3 business days resolution). At 10:00 Sunday, status moves to Pending Customer (agent awaiting a photo of the issue). Customer responds Monday 09:00; status returns to In Progress.
- Under the assumed pause rule: 1 business hour elapsed before pause (09:00–10:00 Sunday); clock frozen through the pending period; resumes Monday 09:00 with 3h remaining on first response, due Monday 12:00.
- **This example only holds if management confirms Pending-status pausing (ISSUE-018)** — under a strict reading of the source text alone, no pause-on-Pending rule exists, and the clock would keep running, making this a materially different (and arguably unfair to the department) outcome.

---

## 8. Required Integrations

| Integration | Purpose | Data Exchanged | Direction | Auth | Failure Handling | Retry/Timeout | Audit |
|---|---|---|---|---|---|---|---|
| **INT-01 Tiger Group CRM** | Resolve unit number → full customer/unit record; write back ticket/resolution history to unit record. | Unit number, contact(s), property/tower/unit type, ticket history, resolution notes | Bi-directional (read for lookup, write for history) | [ASSUMPTION] OAuth2 client-credentials or mutual TLS + API key, per CRM vendor's supported method | Provisional-ticket fallback (FR-VER-06/FR-TKT-09); downtime escalated within 15 min (§11) | [ASSUMPTION] 3 retries, exponential backoff, 5s timeout per call | Every lookup and write logged with request/response correlation ID |
| **INT-02 Geyness Call Center Platform** | Deliver the phone/agent-desktop layer; hand off verified interactions into the ticketing system. | Call metadata, agent ID, channel source | Inbound (Geyness → Ticketing) | [ASSUMPTION] — depends on Geyness's actual platform; **not confirmed to be "Genesys"** (see ISSUE-003) | Manual ticket entry fallback if platform integration is down | [ASSUMPTION] | Agent actions always audited regardless of platform link status |
| **INT-03 WhatsApp** | Auto-route WhatsApp/live-chat messages into tickets. | Message content, sender ID, media attachments | Inbound (webhook) + Outbound (acknowledgement, CSAT) | WhatsApp Business API auth (Meta-issued token) | Queue for manual agent pickup if gateway degraded | [ASSUMPTION] webhook retry per Meta's platform behavior; internal processing timeout 10s | All inbound/outbound messages logged against the ticket |
| **INT-04 SMS Provider** | Acknowledgement, SLA/escalation alerts, CSAT survey delivery. | Phone number, message template + ticket reference | Outbound only | [ASSUMPTION] API key per provider | Fallback to email if SMS send fails | [ASSUMPTION] 3 retries, dead-letter after failure for manual follow-up | Every send/failure logged |
| **INT-05 Email Provider** | Same as SMS, plus report distribution (Daily/Weekly/Monthly/Ad Hoc). | Email address, templated content, report attachments | Outbound only | [ASSUMPTION] SMTP relay or provider API key | Retry queue; alert if report delivery fails ahead of its deadline (e.g., 9AM daily) | [ASSUMPTION] 3 retries | Every send/failure logged |
| **INT-06 Website & Mobile App** | Digital form/chat widget submission → auto-ticket. | Form fields, unit reference (if authenticated), attachments | Inbound | [ASSUMPTION] session-based auth for authenticated portal, or shared API key for the form backend | Held in `PendingCrmVerification` if unit unresolved (FR-TKT-09) | [ASSUMPTION] | Submission logged with source IP/session for fraud review |
| **INT-07 Social Media (Instagram/LinkedIn/Facebook DMs)** | Agent-monitored inbox for manual conversion to tickets. | DM thread content, platform handle | Inbound (manual/semi-automated) | Platform-specific OAuth (Meta/LinkedIn) | MVP: manual copy into ticket (FR-CH-06); Future: native inbox integration | N/A for MVP | Conversion action logged, links back to original thread reference |
| **INT-08 Office Kiosk** | Branded on-screen form submission at reception. | Form fields, unit reference, optional attachment | Inbound | [ASSUMPTION] device-scoped API key (kiosk is a trusted physical terminal, not an end-user identity) | Held in `PendingCrmVerification` if unresolved; agent confirms with customer per §2 | [ASSUMPTION] | Submission logged with kiosk/device ID |
| **INT-09 File Storage** | Store up to 10 attachments/ticket against the unit record. | Binary files (images/PDF/video) | Bi-directional (upload/retrieve) | [ASSUMPTION] signed URLs / SAS tokens scoped per ticket | Virus-scan failure blocks storage, not silent acceptance | [ASSUMPTION] | Upload/access logged |
| **INT-10 Reporting/Export Services** | Deliver scheduled reports; support on-demand export within 24h (§12). | Aggregated ticket/CSAT/KPI data | Outbound | Internal service auth (not customer-facing) | Missed scheduled report is itself alertable (meta-monitoring) | Hangfire-managed retry | Every generated report and export logged with requester/recipient |

---

## 9. Missing Requirements, Ambiguities and Contradictions

| ID | Severity | Issue | Implementation Impact | Recommended Decision | Question for Management |
|---|---|---|---|---|---|
| ISSUE-001 | **Critical** | SLA timer start point contradicts between diagram ("Assign Ticket to Owner… SLA timer starts") and §4 ("SLA Timer: Auto-starts on ticket creation"). | Determines what "SLA compliance %" measures, what the timer service listens for, and how unassigned/queued tickets are reported. Cannot finalize the SLA engine without this. | Default to **ticket creation** (§4 is the more authoritative field-spec section) while surfacing time-to-assignment as its own tracked metric, so either interpretation can be reported later without re-architecting. | "Does the SLA clock start when a ticket is created, or when it is assigned to a named owner? If assignment, what happens to tickets that sit unassigned — does an unassignment SLA apply?" |
| ISSUE-002 | **Critical** | Core Rule ("no ticket without verified unit number") is contradicted by §2, which marks App/Website, WhatsApp, and Kiosk as auto-ticket channels — meaning a ticket record exists before any agent verification. | Determines whether auto-ticket channels create a "real" ticket immediately (violating the Core Rule) or a provisional/unverified record (my proposed FR-TKT-09 default). Affects ticket-numbering, customer-facing ticket-number issuance timing, and SLA start. | Introduce a `PendingCrmVerification` sub-state (proposed FR-TKT-09/FR-VER-05) so auto-ticket channels submit instantly (good UX) without breaking the unit-verification rule. | "For auto-ticket channels, should the customer receive a ticket number immediately (before CRM verification completes), or only after verification succeeds?" |
| ISSUE-003 | **High** | The commissioning brief references a **"Genesys Call Center"** integration; the requirements PDF and both workflow diagrams consistently and only name **"Geyness"** (the signing vendor). These may be the same entity under a different spelling, or Geyness may run on an underlying Genesys CCaaS platform, or they may be unrelated. | If Genesys (the CCaaS product) is actually the underlying telephony platform, INT-02's integration contract (APIs, auth, events) is completely different from a generic "Geyness hands off verified tickets" integration. Building the wrong contract wastes integration effort. | Do not assume Genesys. Treat Geyness as the named vendor per the signed document; scope INT-02 generically until confirmed otherwise. | "Is 'Geyness' the correct, final name of the contracted call-center vendor, and if so, does Geyness's platform run on Genesys (or another named CCaaS product) that our system needs to integrate with directly — or does Geyness handle all telephony internally and hand off only ticket data?" |
| ISSUE-004 | **High** | §6 says Critical SLA breach = "Immediate GM notification," implying Level 3 is reached directly; §7's general escalation model routes every breach through Dept Head (Level 2) first, with GM only reached if Level 2 fails. | Determines whether Dept Heads are even aware of Critical incidents in real time, or only the GM. Affects notification routing logic and Dept Head operational visibility. | Notify Dept Head **and** GM simultaneously on Critical breach — satisfies "immediate GM notification" literally while not silently bypassing operational ownership. | "For a Critical-priority SLA breach, should the Department Head still be notified alongside the GM, or does Critical bypass Level 2 entirely?" |
| ISSUE-005 | **Medium** | The diagram's "re-assign & retry" loop after Escalate has no defined exit condition — nothing stops a ticket cycling indefinitely between "Department Works on Request" and "Escalate" without ever reaching Level 3/4. | Without a cap, a chronically-missed ticket could loop forever while only ever alerting at Level 2, never reaching GM/Chairman despite repeated failure. | Cap retries (proposed: 2 cycles) before forcing an automatic level-up. | "How many times can a ticket be re-assigned-and-retried after an escalation before it must automatically advance to the next escalation level?" |
| ISSUE-006 | **High** | CRM downtime must be "escalated within 15 minutes" (§11), but nothing states what happens to ticket **creation** during that downtime, given the Core Rule blocks tickets without a verified unit. | Determines whether the entire intake pipeline halts during CRM downtime (unacceptable for Critical/safety issues) or a fallback path exists. | Provisional ticket creation for Critical/High during CRM downtime, reconciled once CRM returns; reject/queue Low/Medium until restored. | "During a CRM outage, should agents be able to open provisional tickets (especially for safety-critical issues) without a live unit match, to be reconciled once the CRM is back?" |
| ISSUE-007 | **High** | §3 Step 03 acknowledges the risk of "data mixing between different owners or tenants of the same unit type across buildings" but never actually defines how multiple contacts tied to one unit (joint owners, current+incoming tenant during handover) are disambiguated or scoped for permissions. | Affects the CRM data model (unit ↔ contact cardinality), the verification UI (which contact is this?), and who can see which tickets when a unit has multiple legitimate parties. | Model unit and contact as separate entities with a many-to-many link; require contact-level confirmation on top of unit match (proposed FR-VER-04/BR-024). | "When a unit has multiple owners/tenants, should each contact see only their own tickets for that unit, or should all linked contacts see all tickets for the unit?" |
| ISSUE-008 | **Medium** | The requested ticket lifecycle (this task's Section 5 ask) needs Reopen, Cancelled, Rejected, Duplicate, and Pending Third-Party states; the source PDF defines only 6 statuses and none of these. | Without these states, real FM/Leasing scenarios (waiting on a contractor, a customer withdrawing a complaint, a duplicate call-in) get force-fit into the wrong status, corrupting SLA and reporting accuracy. | Adopt the extended 11-status model in Section 5 as the working model, explicitly flagged as an extension beyond the literal source spec. | "Please confirm the extended status list in Section 5 (adding Reopened/Cancelled/Rejected/Duplicate/Pending Third-Party) is acceptable, or specify which of these Tiger Group does not want." |
| ISSUE-009 | **Medium** | No rule addresses whether a Reopened→re-Closed ticket re-triggers the CSAT survey, or whether the original response counts. | Affects CSAT data integrity (double-counting risk) and customer experience (survey fatigue). | Re-send CSAT on every genuine re-closure, but tag it as "post-reopen" in reporting so it doesn't silently blend with first-pass CSAT trends. | "Should a ticket that is reopened and later re-closed trigger a second CSAT survey?" |
| ISSUE-010 | **Medium** | No rule exists for department-to-department ticket transfers (e.g., a ticket initially logged as FM turns out to be a Leasing dispute). | Affects SLA continuity, audit trail design, and who has authority to approve a transfer. | Allow transfer with mandatory reason code and Dept Head-level visibility; SLA continues (no reset) by default per BR-018. | "Who is authorized to approve a cross-department ticket transfer, and should the SLA clock reset when that happens?" |
| ISSUE-011 | **Medium** | No reopening policy is defined (task explicitly asks for one; source is silent). | Affects whether a customer complaint about an unresolved issue creates ticket sprawl (many near-duplicate new tickets) or a clean reopen history. | 7-day reopen window tied to original ticket ID; beyond that, new linked ticket. | "What is the acceptable time window after closure during which a customer can reopen the same ticket rather than opening a new one?" |
| ISSUE-012 | **Medium** | No UAE public holiday calendar source is specified, yet non-Critical SLA math depends on excluding holidays. | Blocks a correct business-hours SLA calculation for any month containing a public holiday (UAE holidays shift yearly per the Islamic calendar and are announced close to the date). | Maintain an internally editable holiday reference table, updated annually by the CS Manager (not hardcoded, not sourced automatically from an external feed at MVP). | "Who is responsible for maintaining the UAE public holiday calendar the SLA engine uses, and how far in advance is it typically confirmed each year?" |
| ISSUE-013 | **Medium** | Level 3 (GM) escalation trigger — "if Level 2 does not resolve within the next escalation window" — never defines the window's duration. Similarly, no SLA warning threshold (pre-breach) is specified anywhere. | Both are required inputs to the escalation and SLA-warning engines; cannot be hardcoded without guessing. | Default Level 2→3 window and warning threshold as documented in Sections 6/7, explicitly flagged as provisional. | "How long should the Department Head have to resolve an escalated ticket before it automatically advances to the General Manager?" |
| ISSUE-014 | **Low** | "Repeat Contact Rate" KPI (§10) has no operational definition of what counts as a repeat contact for the same issue. | Affects KPI dashboard accuracy and whether it can be trusted as a management metric at MVP. | Ship as a "provisional" metric using the same-unit/same-category/7-day heuristic until a real definition is confirmed. | "What should count as a 'repeat contact for the same issue' for KPI purposes — same unit and category within how many days?" |
| ISSUE-015 | **Low** | No expected ticket volume, portfolio size (number of units/towers), or agent headcount is given anywhere in the source, despite this being foundational for sizing NFR-SCALE and Hangfire/SignalR capacity planning. | Cannot size infrastructure or set realistic performance targets without at least an order-of-magnitude estimate. | Proceed with a conservative, horizontally-scalable default architecture (Section 10) that doesn't need this number to start, but flag it as needed before load-testing/capacity sign-off. | "Approximately how many units/towers, and how many concurrent Geyness agents, should the system be sized for at launch and at 3-year horizon?" |
| ISSUE-016 | **Low** | 7-year retention is stated as aligned to "UAE regulatory requirements" without citing which specific law (options include general commercial record-keeping rules, real-estate/RERA-style retention, or data-protection law provisions, each with different scope). | Affects whether 7 years applies uniformly to all record types (tickets, CSAT, audit logs, attachments) or should differ by type. | Apply 7 years uniformly at MVP as a safe default (matches the stated figure) while flagging for Legal confirmation before go-live. | "Which specific UAE regulation sets the 7-year retention requirement, and does it apply uniformly to tickets, attachments, CSAT responses, and audit logs, or differently per record type?" |
| ISSUE-017 | **Low** | Stated business hours use a Saturday–Thursday week (Friday-only weekend), which differs from the UAE federal government's Saturday–Sunday weekend in effect since January 2022. | If this is a drafting artifact rather than Tiger's actual policy, every business-hours SLA calculation would be systematically wrong by one day per week. | Implement the calendar as **configurable data**, not a hardcoded constant, specifically so this can be corrected without a code change if it turns out to be an error. | "Please confirm Tiger Group/Geyness's actual operating week is Saturday–Thursday (one non-working day, Friday) and not the more common Saturday–Sunday weekend." |
| ISSUE-018 | **Low** | No rule states whether SLA clocks pause while a ticket sits in Pending Customer or (proposed) Pending Third-Party status — only that non-Critical clocks run "during business hours." | Without pausing, a department is penalized for delays entirely outside its control (e.g., waiting on the customer), distorting SLA compliance reporting. | Pause on Pending Customer / Pending Third-Party for non-Critical tickets; **[ASSUMPTION]**, not source-confirmed. | "Should the SLA clock pause while a ticket is in a Pending Customer or Pending Third-Party state, or does the department's SLA obligation continue regardless?" |

---

## 10. Recommended Modular Architecture

**Recommendation: modular monolith**, not microservices. The domain has one dominant, highly-relational aggregate (the ticket, with its status/SLA/escalation lifecycle) and a handful of well-bounded integrations. Splitting this into services now would multiply operational complexity (service discovery, distributed transactions for a single ticket's lifecycle, cross-service SLA-clock consistency) without a corresponding scaling need — nothing in the requirements suggests independent, differently-scaled deployment units. A modular monolith keeps module boundaries clean in code (so a future extraction is a refactor, not a rewrite) while keeping deployment, transactions, and the SLA/escalation state machine simple and consistent.

### 10.1 Solution/Project Boundaries (conceptual — no scaffolding created)

```
TigerCS.Api            → ASP.NET Core Web API (agent desktop, admin, integrations)
TigerCS.Web            → ASP.NET Core MVC/Razor Pages (Tiger-facing dashboard/reports UI, optional kiosk-facing pages)
TigerCS.Domain         → Core domain: Ticket, Unit, Contact, Escalation, SLA policy — no framework dependencies
TigerCS.Application    → Application services / use cases (CQRS-style handlers), orchestration, domain events
TigerCS.Infrastructure  → EF Core, SQL Server, Identity, Hangfire job implementations, SignalR hub implementations
TigerCS.Integrations    → Gateway adapters: CRM, Geyness platform, WhatsApp, SMS, Email, Social, Storage
TigerCS.Reporting       → Report/KPI aggregation and generation logic (may consume a read-optimized store/view)
TigerCS.Tests           → xUnit test projects mirroring the above (unit + integration)
```

### 10.2 Modules and Responsibilities

| Module | Responsibility |
|---|---|
| **Identity & Access** | ASP.NET Core Identity-backed users/roles; enforces the Section 3 permission matrix; access-revocation workflow (FR-ADM-02). |
| **Verification** | Unit/contact resolution against CRM; owns the `PendingCrmVerification` sub-state and downtime fallback. |
| **Ticketing** | Ticket aggregate, status state machine (Section 5), attachments, resolution notes, audit trail. |
| **Classification & Routing** | Category/priority selection (+ AI-assisted suggestion as an advisory input), department routing table, transfer/reassignment. |
| **SLA & Escalation Engine** | Timer calculation (calendar-aware for Critical, business-hours-aware otherwise), warning thresholds, 4-level escalation state, breach notifications. |
| **Notifications** | Templated outbound messaging (ack, breach, CSAT invite) fanned out to SMS/Email adapters; delivery tracking/retry. |
| **CSAT** | Survey issuance, scoring, low-score alerting. |
| **Reporting & KPI** | Scheduled report generation (Daily/Weekly/Monthly/Ad Hoc), live dashboard data feed, export service. |
| **Audit** | Cross-cutting append-only log consumed by all modules via domain events; queryable independently of the modules that wrote it. |
| **Integration Gateways** | One adapter per external system (Section 8), each behind a narrow interface the Application layer depends on — never a direct outbound call from domain/application code. |

### 10.3 Conceptual Domain Entities & Relationships

- **Unit** (1) ↔ (many) **Contact** — supports joint owners/tenants (ISSUE-007's resolution).
- **Unit** (1) ↔ (many) **Ticket** — a unit's full ticket history.
- **Contact** (1) ↔ (many) **Ticket** — which specific contact raised each ticket.
- **Ticket** (1) ↔ (many) **StatusChange** (audit-style history, drives the state machine and SLA pause/resume calc).
- **Ticket** (1) ↔ (many) **Escalation** (one row per level reached, with timestamps and acting role).
- **Ticket** (1) ↔ (many) **Attachment**.
- **Ticket** (1) ↔ (0..1) **CsatResponse**.
- **Ticket** (1) ↔ (0..1) **DuplicateLink** / **ReopenLink** (self-referential, for the extended lifecycle).
- **Department** (1) ↔ (many) **Employee** (ticket owners); **Employee** (many) ↔ (many) **Role**.
- **SlaPolicy** (reference data: priority tier → response/resolution targets, clock basis) — configuration, not hardcoded, per multiple FRs above.
- **HolidayCalendar** (reference data, addressing ISSUE-012).

No SQL DDL or EF Core mappings are produced at this stage, per the task's explicit instruction.

### 10.4 Application Services & Integration Gateways

- **Application services** (one per module, CQRS-style command/query handlers): `VerifyUnitHandler`, `CreateTicketHandler`, `ClassifyTicketHandler`, `RouteTicketHandler`, `TransitionStatusHandler`, `EscalateTicketHandler`, `GenerateReportHandler`, etc. — each orchestrates domain logic and raises domain events (e.g., `TicketCreated`, `SlaBreached`, `TicketClosed`) rather than directly calling other modules.
- **Integration gateways** implement narrow interfaces owned by the Application layer (e.g., `ICrmGateway`, `IWhatsAppGateway`, `ISmsGateway`) — this is what makes INT-01…INT-10 swappable/mockable and keeps the domain ignorant of HTTP/webhook details.

### 10.5 Background Jobs (Hangfire) & Real-Time Events (SignalR)

| Job/Event | Trigger | Mechanism |
|---|---|---|
| SLA breach/warning sweep | Recurring (e.g., every minute) | Hangfire recurring job scanning open tickets against SLA policy |
| Daily Flash Report | Cron, daily before 09:00 | Hangfire scheduled job |
| Weekly Performance Report | Cron, Monday before 10:00 | Hangfire scheduled job |
| Monthly Management Report | Cron, 1st of month | Hangfire scheduled job |
| Ad Hoc Incident Report | Event-driven (Critical ticket created) | Hangfire fire-and-forget job, deadline-tracked |
| Notification retries | Event-driven on delivery failure | Hangfire retry-with-backoff |
| CRM reconciliation for `PendingCrmVerification` | Event-driven + timeout sweep (15 min) | Hangfire scheduled job |
| Live ticket status/SLA countdown | Every state change | SignalR push to agent desktop and Tiger dashboard clients |
| KPI dashboard metric updates | Near-real-time on relevant domain events | SignalR push to dashboard hub |
| Escalation level-up notification | Domain event (`EscalationLevelReached`) | SignalR (in-app) + Notifications module (email/SMS) |

### 10.6 Authorization & Audit Strategy

- **Authorization**: ASP.NET Core Identity for authentication; policy-based authorization (`[Authorize(Policy = "...")]`) mapped 1:1 to the Section 3 permission matrix, enforced at the API boundary — never solely in UI. Department-scoped queries (e.g., a Department Employee only sees their department's queue) enforced via query-level filters tied to the authenticated user's claims, not client-supplied parameters.
- **Audit**: a single cross-cutting `AuditLog` capturing actor, action, entity, before/after, timestamp, correlation ID — populated via domain-event subscribers so no module has to remember to log; append-only, queryable by ticket/unit/user/date range, and itself covered by the 7-year retention rule (pending ISSUE-016 confirmation).

---

## 11. Implementation Phases

| Phase | Scope | Deliverables | Dependencies | Acceptance Criteria | Est. Effort | Key Risks |
|---|---|---|---|---|---|---|
| **1. Discovery & Requirement Approval** | Resolve Section 9/13 open items with Tiger Group management; confirm CRM/Geyness platform technical details. | Signed-off answers to all 18 ISSUE items; confirmed SLA start point, status model, escalation trigger windows. | None | Management sign-off document exists and is referenced by all subsequent phases | 1–2 weeks | Decisions delayed → downstream rework on SLA/status engine |
| **2. Architecture & Database Design** | Finalize module boundaries (Section 10), ERD, EF Core model, SLA policy schema, holiday calendar schema. | ERD, module dependency diagram, API contract sketch, ADRs for key decisions | Phase 1 | Design reviewed and approved before any code | 2 weeks | Designing around unresolved ISSUE items locks in wrong assumptions |
| **3. Project Foundation** | Solution scaffolding, CI/CD, ASP.NET Core Identity setup, base EF Core migrations, logging/monitoring baseline. | Buildable solution skeleton per Section 10.1, empty but wired-up modules, test project scaffolding | Phase 2 | `dotnet build`/`dotnet test` green in CI on an empty-but-structured solution | 1–2 weeks | Under-investing here causes churn later (e.g., retrofitting audit logging) |
| **4. Core Ticketing MVP** | Channel intake (FR-CH-*), verification (FR-VER-*), ticketing engine (FR-TKT-*), classification/routing (FR-CLS-*/FR-RTE-*), basic status lifecycle. | Working ticket creation→routing→resolution→closure flow for at least Phone + one auto-ticket channel | Phase 3 | End-to-end manual test: agent creates, routes, resolves, closes a ticket; audit trail present | 4–6 weeks | Section 9's unresolved items (esp. ISSUE-001/002/007) directly shape this phase — biggest schedule risk if still open |
| **5. SLA & Escalation** | SLA engine (business-hours/24-7 calendar logic), warning thresholds, 4-level escalation, breach notifications. | Timer service, escalation state machine, notification triggers wired to Notifications module | Phase 4 | Section 7's worked examples (7.7) reproduce correctly against real system clock/test fixtures | 3–4 weeks | Calendar/holiday logic (ISSUE-012/017) is easy to get subtly wrong — needs dedicated test coverage |
| **6. Notifications & Integrations** | SMS/Email/WhatsApp/CRM/Kiosk/Social gateways; retry/failure handling. | All INT-01…INT-10 adapters implemented (Social Media at manual-conversion MVP level per FR-CH-06) | Phase 4 (can partially parallelize with Phase 5) | Each gateway has a passing integration test against a sandbox/mocked endpoint | 4–5 weeks | Vendor-side sandbox availability (esp. CRM, WhatsApp Business API) can block testing |
| **7. Dashboard & Reporting** | KPI dashboard (FR-KPI-*), scheduled reports (FR-RPT-*), export service. | Live dashboard (SignalR-backed), Hangfire-scheduled Daily/Weekly/Monthly/Ad Hoc reports, on-demand export | Phases 4–6 (needs real ticket/SLA/CSAT data to report on) | Each report's field list matches §9/§10 exactly; dashboard thresholds visually flag per §10 | 3–4 weeks | "Repeat Contact Rate" (ISSUE-014) ships as provisional unless resolved earlier |
| **8. AI-Assisted Features** | Keyword/priority suggestion (FR-AI-01), later predictive/chatbot features. | MVP: rule/keyword-based suggestion, logged override rate for future model training | Phase 4 | Suggestion is advisory-only in UI; override always logged | 2–3 weeks (MVP scope only) | Scope creep risk — keep Phase 8 to the advisory keyword layer only; defer FR-AI-02/03/04 |
| **9. Testing & UAT** | xUnit unit/integration coverage across modules; UAT with Tiger Group CS Manager and a Geyness agent cohort. | Test suite covering SLA edge cases (Section 7.7-style scenarios), UAT sign-off log | Phases 4–8 | UAT scenarios (per role in Section 3) pass; no Critical/High defects open | 3–4 weeks | Under-testing the SLA calendar logic is the highest-likelihood source of production incidents |
| **10. Production Deployment & Support** | Go-live, kiosk hardware rollout, agent training coordination (Geyness HR obligation, §12 item 12), hypercare support. | Deployed system meeting NFR-AVAIL-01 (99.5%), backup/DR verified against NFR-BCDR-01 | Phase 9 | 99.5% uptime and 4h RTO validated via a DR drill before go-live sign-off | 2–3 weeks + ongoing hypercare | Kiosk hardware/network readiness at physical sites is outside pure software control |

*(Effort estimates are order-of-magnitude planning inputs for a small-to-mid dedicated team; they are not a committed quote and should be revisited once ISSUE-015's volume/scale question is answered.)*

---

## 12. Risk Register

| Risk | Probability | Business Impact | Mitigation | Owner |
|---|---|---|---|---|
| SLA start-point ambiguity (ISSUE-001) ships un-resolved, causing disputed SLA-compliance reporting with Geyness. | Medium | High — contractual KPI (§10, ≥90% SLA compliance) becomes unenforceable if both sides measure differently | Resolve in Phase 1; store both creation and assignment timestamps regardless, so reporting can be recomputed either way | CS Manager + Solution Architect |
| Core Rule vs. auto-ticket channel contradiction (ISSUE-002) leads to tickets created with unverified/mismatched unit data. | Medium | High — corrupts unit-history data integrity, the foundation of the whole system | `PendingCrmVerification` sub-state (FR-TKT-09) implemented before any auto-ticket channel goes live | Solution Architect |
| CRM downtime blocks all new tickets, including safety-critical Emergency FM requests. | Low–Medium | Critical — potential safety/legal exposure if a fire/flood report can't be logged during an outage | Provisional-ticket fallback (ISSUE-006) implemented and tested before go-live | Solution Architect + Tiger IT |
| Geyness/Genesys naming confusion (ISSUE-003) leads to building the wrong CCaaS integration. | Low | Medium — wasted integration effort if discovered late | Confirm vendor/platform identity in Phase 1 before INT-02 design starts | Tiger Transformation Directorate |
| UAE holiday calendar (ISSUE-012) hardcoded or missed, silently breaching SLA compliance around public holidays. | Medium | Medium — inaccurate SLA reporting, unfair breach alerts to departments | Configurable holiday reference table, annual review process owned by CS Manager | CS Manager |
| Multi-party unit contacts (ISSUE-007) not modeled, causing a tenant to see an owner's (or vice versa) ticket history. | Low–Medium | High — data-privacy/reputational exposure | Contact-level modeling and permission scoping (FR-VER-04, BR-024) built into MVP, not deferred | Solution Architect |
| Report/dashboard field mismatch vs. the exact §9/§10 specification, breaching the Geyness-Tiger contractual reporting obligations (§12). | Low | Medium — contractual non-compliance | Acceptance criteria in Section 2.11/2.12 map field-by-field to source tables; UAT explicitly checks this | QA / CS Manager |
| Kiosk hardware/network readiness at physical sites delays go-live independent of software readiness. | Medium | Medium — schedule slip | Track kiosk rollout as a parallel workstream from Phase 3 onward, not bundled into software Phase 10 | Tiger IT |
| Scope creep into AI-assisted features (Phase 8) before core MVP (Phases 4–7) is stable. | Medium | Medium — schedule slip, quality risk to core ticketing | Enforce phase gating; FR-AI-02/03/04 explicitly Future, not MVP | Solution Architect / Project Sponsor |
| Volume/scale unknown (ISSUE-015) leads to under- or over-provisioned infrastructure at launch. | Medium | Low–Medium — cost inefficiency or performance risk | Build on a horizontally-scalable default (stateless API tier, SQL Server with room to scale up first) and revisit sizing once real numbers are known | Solution Architect |

---

## 13. Management Decisions Required

Prioritized — highest-impact/blocking items first. These should be answered before the corresponding phase begins; several block Phase 4 (Core Ticketing MVP) directly.

1. **(Blocks Phase 4)** Does the SLA clock start at ticket creation or at owner assignment? *(ISSUE-001)*
2. **(Blocks Phase 4)** For auto-ticket channels, is a ticket number issued to the customer before or after CRM verification completes? *(ISSUE-002)*
3. **(Blocks Phase 1/6)** Is "Geyness" the final vendor name, and does it run on Genesys or another named CCaaS platform requiring direct integration? *(ISSUE-003)*
4. **(Blocks Phase 5)** Does a Critical SLA breach still notify the Department Head, or does it go straight to the GM? *(ISSUE-004)*
5. **(Blocks Phase 4)** When a unit has multiple owners/tenants, should each see only their own tickets, or all tickets for that unit? *(ISSUE-007)*
6. **(Blocks Phase 4)** During a CRM outage, can agents open provisional tickets without a live unit match — for which priority tiers? *(ISSUE-006)*
7. **(Blocks Phase 4/5)** Please confirm (or amend) the extended ticket-status model in Section 5 (Reopened/Cancelled/Rejected/Duplicate/Pending Third-Party). *(ISSUE-008)*
8. **(Blocks Phase 5)** How long does a Department Head have before a ticket auto-escalates to the GM (Level 2→3 window)? *(ISSUE-013)*
9. **(Blocks Phase 5)** Should the SLA clock pause while a ticket is Pending Customer / Pending Third-Party? *(ISSUE-018)*
10. **(Blocks Phase 5)** How many re-assign-and-retry cycles are allowed after an escalation before it must automatically advance a level? *(ISSUE-005)*
11. **(Blocks Phase 4)** What is the acceptable window for reopening a closed ticket before a new ticket must be raised instead? *(ISSUE-011)*
12. **(Blocks Phase 4)** Who approves a cross-department ticket transfer, and does it reset the SLA clock? *(ISSUE-010)*
13. **(Blocks Phase 2)** Confirm the actual operating business week (Saturday–Thursday as stated, vs. the more common UAE Saturday–Sunday weekend). *(ISSUE-017)*
14. **(Blocks Phase 2)** Who maintains the UAE public holiday calendar the SLA engine will use, and on what cadence? *(ISSUE-012)*
15. **(Blocks Phase 7)** What is the operational definition of a "repeat contact" for the Repeat Contact Rate KPI? *(ISSUE-014)*
16. **(Blocks Phase 10)** Which specific UAE regulation sets the 7-year retention period, and does it apply uniformly to all record types? *(ISSUE-016)*
17. **(Informs Phase 2/10 sizing)** Approximate unit/tower count and concurrent-agent count expected at launch and at 3-year horizon? *(ISSUE-015)*
18. **(Confirms CSAT behavior)** Should a reopened-then-reclosed ticket trigger a second CSAT survey? *(ISSUE-009)*

---

## 14. Consolidated Assumptions Register

Every item below appears inline above; consolidated here for a single-pass management review. Nothing in this list is a source-confirmed requirement — treat each as a proposed default, not a decision already made.

1. Auto-ticket channels create a `PendingCrmVerification` provisional record, not a fully "real" ticket, until unit verification completes.
2. Multi-contact units require agent confirmation of the specific contact, in addition to the unit match.
3. CRM downtime allows provisional ticket creation for higher priorities, reconciled within 15 minutes.
4. Reopen window defaults to 7 days from closure.
5. Duplicate detection heuristic: same unit + same category within a rolling 7-day window, flagged for agent confirmation only (never auto-merged).
6. Cancelled/Rejected are terminal, CSAT-suppressing states distinct from Resolved.
7. Pending Third-Party is a distinct status from Pending Customer.
8. Reassignment/transfer does not reset the SLA clock by default (configurable per tier).
9. Priority change recalculates remaining SLA from the moment of change, not retroactively.
10. SLA warning fires at 75% of resolution-target elapsed time.
11. Level 2→3 escalation window and the re-assign-and-retry cap (2 cycles) are provisional defaults pending confirmation.
12. Critical-breach notification includes both Dept Head and GM.
13. Non-Critical SLA clocks pause during Pending Customer / Pending Third-Party status.
14. WCAG 2.1 AA targeted for customer-facing surfaces (kiosk, web/app forms, survey links).
15. Data residency/PDPL considerations favor a UAE (or otherwise compliant) SQL Server hosting region.
16. File attachment cap assumed at 25MB/file (count of 10/ticket is source-confirmed; size is not).
17. Notification channel-per-alert-type matrix (Section 7.6) is a proposed default, not source-specified.
18. 7-year retention applied uniformly across tickets, attachments, CSAT, and audit logs pending Legal confirmation.
19. System Administrator, Reporting User, Department Employee, and Customer are treated as first-class roles even though only some are explicitly named in the PDF.

---

## 15. Recommended MVP Scope

**In scope for MVP** (buildable now, using the documented defaults above where source is silent, and re-confirmable without rework once management answers Section 13):

- All 5 intake channels (Phone, App/Website, Social Media manual-conversion, WhatsApp/Live Chat, Kiosk) — FR-CH-01…06
- Unit/contact verification with the `PendingCrmVerification` fallback — FR-VER-01…06
- Full ticketing engine incl. the extended 11-status lifecycle — FR-TKT-01…10, Section 5
- Classification and manual priority setting (keyword auto-suggestion deferred as AI-assisted — FR-CLS-04 is the only Module-D item pushed to Future)
- Deterministic routing, assignment, transfer, reassignment — FR-RTE-01…05
- Full SLA engine per Section 7, including business-hours/24-7 calendar logic and the four-level escalation path
- All acknowledgement, breach, and CSAT notifications — FR-NOT-01…05
- Resolution, closure, reopen, cancel, reject, duplicate-link flows — FR-RES-01…05
- CSAT survey end-to-end — FR-CSAT-01…03
- All four scheduled/triggered reports (Daily/Weekly/Monthly/Ad Hoc) — FR-RPT-01…06
- Live KPI dashboard with all 10 metrics (Repeat Contact Rate shipped as "provisional" per ISSUE-014) — FR-KPI-01…03
- Full RBAC, audit trail, data-export, and access-revocation workflow — FR-ADM-01…06
- All 10 integrations at the level specified in Section 8 (Social Media at manual-conversion depth per FR-CH-06)

**Explicitly deferred to Future:**

- FR-CLS-04 keyword-based priority auto-suggestion and the entire FR-AI-01…04 module (predictive breach risk, chatbot, root-cause clustering)
- Native Social Media inbox integration (beyond MVP's manual conversion)
- Any refinement of the Repeat Contact Rate definition beyond the provisional heuristic, pending ISSUE-014

**Gate before starting Phase 4 (Core Ticketing MVP):** answers to Section 13's items #1, #2, #5, #6, #7, and #11 — these five directly shape the ticket/status/verification data model, and are the most expensive to retrofit after code exists. Everything else in Section 13 can be answered in parallel with Phases 4–6 without blocking their start.
