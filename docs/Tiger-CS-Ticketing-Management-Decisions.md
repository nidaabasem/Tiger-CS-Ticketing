# Tiger Group — CS Ticketing System
## Management Decision Document

| | |
|---|---|
| **Purpose** | Obtain management decisions on the 18 open items identified in the Solution Analysis (Section 13) before development proceeds |
| **Status** | Awaiting decisions — **no development, database design, or integration work has started** |
| **Related document** | `docs/Tiger-CS-Ticketing-Solution-Analysis.md` |
| **Date** | 2026-08-17 |

### How to use this document
Each item below states the decision needed, why it matters, the realistic options, and a recommendation. Nothing has been decided on management's behalf — the "Recommended option" is a starting position for discussion, not a default that will be built without sign-off. Items are grouped by when the decision is actually needed, not by how important they sound, so the group headings double as a sequencing guide.

---

## Decision Log (at a glance)

| ID | Question | Priority | Owner | Needed by |
|---|---|---|---|---|
| ISSUE-001 | When does the SLA clock start — creation or assignment? | Critical | Management | Before MVP development |
| ISSUE-002 | Can a ticket exist before unit verification completes? | Critical | Management | Before MVP development |
| ISSUE-004 | Does a Critical breach still notify the Department Head, or GM only? | High | Management | Before MVP development |
| ISSUE-005 | How many escalation retry cycles before forced level-up? | Medium | Customer Service | Before MVP development |
| ISSUE-007 | How are multiple contacts on one unit (joint owners/tenants) scoped for access? | High | CRM Team | Before MVP development |
| ISSUE-008 | Confirm the extended ticket status list (Reopened/Cancelled/Rejected/Duplicate/Pending Third-Party) | Medium | Customer Service | Before MVP development |
| ISSUE-010 | Who approves cross-department transfers, and does the SLA clock reset? | Medium | Department Head | Before MVP development |
| ISSUE-011 | What is the allowed window to reopen a closed ticket? | Medium | Customer Service | Before MVP development |
| ISSUE-012 | Who maintains the UAE public holiday calendar, and how often? | Medium | Customer Service | Before MVP development |
| ISSUE-013 | How long before an escalated ticket auto-advances Dept Head → GM? | Medium | Management | Before MVP development |
| ISSUE-017 | Confirm the actual operating week (Sat–Thu vs. Sat–Sun) | Low | Management | Before MVP development |
| ISSUE-018 | Does the SLA clock pause while waiting on the customer or a third party? | Low | Management | Before MVP development |
| ISSUE-003 | Is "Geyness" the final vendor name, and what platform does it run on? | High | Geyness/Genesys | Before integrations |
| ISSUE-006 | Can agents create provisional tickets during a CRM outage? | High | IT | Before integrations |
| ISSUE-015 | Expected unit/tower count and concurrent-agent count? | Low | IT | Before integrations |
| ISSUE-009 | Does a reopened-then-reclosed ticket trigger a second CSAT survey? | Medium | Customer Service | Can be deferred until after MVP |
| ISSUE-014 | What counts as a "repeat contact" for the KPI? | Low | Customer Service | Can be deferred until after MVP |
| ISSUE-016 | Which UAE regulation sets the 7-year retention period? | Low | Management | Can be deferred until after MVP |

---

## Group A — Required Before MVP Development

These twelve items shape the core ticket, status, SLA, and escalation data model. Answering them after coding starts means rebuilding rather than configuring — they gate the start of core build work.

### ISSUE-001 — SLA clock start point
**Decision required:** Does the SLA clock start when a ticket is created, or when it is assigned to a named department owner?

**Why this decision is needed:** The requirements document contradicts itself — the field specification says the timer starts at creation, but the workflow diagram shows it starting at assignment. This is the single number used to calculate SLA compliance %, which is a contractual KPI target (≥90%) between Tiger Group and Geyness. Both sides must measure it the same way.

**Options:**
- **A — Start at ticket creation.** *Pros:* Matches the written field specification; simplest to implement and explain; puts pressure on fast assignment. *Cons:* Penalizes departments for delays in queue/assignment that may be outside their control.
- **B — Start at owner assignment.** *Pros:* Matches the workflow diagram; measures the department's actual working time only. *Cons:* Creates an unmeasured gap between ticket creation and assignment, during which a customer could be waiting with no SLA accountability at all.
- **C — Start at creation, but separately track and report time-to-assignment as its own metric.** *Pros:* Satisfies the written spec, closes Option A's accountability gap, and is compatible with either final answer without rework. *Cons:* Slightly more reporting complexity.

**Recommended option:** C.

**Impact if no decision is made:** The SLA engine cannot be built with confidence; whichever assumption is coded in risks a later dispute with Geyness over contractual SLA compliance figures.

**Priority:** Critical
**Decision owner:** Management

---

### ISSUE-002 — Ticket creation before unit verification
**Decision required:** For channels that auto-create a ticket (App/Website, WhatsApp, Kiosk), should the customer receive a ticket number immediately, or only after the unit number is verified against the CRM?

**Why this decision is needed:** The stated Core Rule is "no ticket without a verified unit number," but the same document marks these channels as auto-ticketing on submission — before any agent has verified anything. This is a direct contradiction in the source requirements.

**Options:**
- **A — Verify first, ticket number issued after.** *Pros:* Fully honors the Core Rule; no unverified data ever enters departmental queues. *Cons:* Slower, worse customer experience on digital/self-service channels — the customer submits and waits, unsure their request registered.
- **B — Issue a ticket number immediately; verify in the background.** *Pros:* Better customer experience; matches how most digital self-service systems behave. *Cons:* Breaks the Core Rule as literally written; unverified records could reach departments if verification fails silently.
- **C — Issue a provisional reference immediately, convert to a full ticket only once verified, with an automatic escalation if verification is not completed within a set time.** *Pros:* Preserves good customer experience without breaking the Core Rule; unverified submissions are visible and time-bounded, not silently lost. *Cons:* Requires a "provisional" state in the system that needs its own handling rules.

**Recommended option:** C.

**Impact if no decision is made:** Developers must guess which rule takes precedence — either building slower digital channels than customers expect, or quietly abandoning the unit-verification rule that the CRM lookup depends on.

**Priority:** Critical
**Decision owner:** Management

---

### ISSUE-004 — Critical breach notification routing
**Decision required:** When a Critical-priority ticket breaches its SLA, is the Department Head still notified alongside the General Manager, or does the alert go to the GM only?

**Why this decision is needed:** One part of the requirements says a Critical breach means "immediate GM notification"; the general escalation model elsewhere says every breach routes through the Department Head first. As written, these conflict.

**Options:**
- **A — GM only, as literally stated.** *Pros:* Matches the specific SLA table wording. *Cons:* The Department Head — who is operationally responsible for the ticket — may be unaware a Critical issue is underway in their own department.
- **B — Department Head and GM notified simultaneously.** *Pros:* GM still gets the required immediate visibility; the Department Head stays informed and can act without waiting to be told by the GM. *Cons:* One additional notification per Critical breach — negligible operational cost.

**Recommended option:** B.

**Impact if no decision is made:** Notification routing is built on a guess; if wrong, either the GM is not alerted fast enough on a safety-critical issue, or a Department Head is blindsided by an escalation they never saw.

**Priority:** High
**Decision owner:** Management

---

### ISSUE-005 — Escalation retry cap
**Decision required:** After a ticket is escalated and "re-assigned to retry," how many retry cycles are allowed before the system must force it up to the next escalation level automatically?

**Why this decision is needed:** The workflow diagram shows escalated tickets looping back into normal work with no defined exit condition. Without a cap, a chronically mishandled ticket could cycle indefinitely at the same escalation level and never reach the General Manager or Chairman/CEO, despite repeated failure.

**Options:**
- **A — No automatic cap; rely on staff judgment to escalate further.** *Pros:* No extra system logic. *Cons:* Relies entirely on someone remembering to push it up manually — the exact failure mode this rule exists to prevent.
- **B — Cap at a fixed number of retries (e.g., two), then force an automatic level-up.** *Pros:* Guarantees chronic issues surface to senior management without manual intervention; simple to configure. *Cons:* A fixed number may occasionally escalate a ticket that was genuinely close to resolution.

**Recommended option:** B, with the exact number configurable rather than hardcoded.

**Impact if no decision is made:** A ticket could remain stuck at Department Head level indefinitely with no automatic safeguard, undermining the entire purpose of a four-level escalation path.

**Priority:** Medium
**Decision owner:** Customer Service

---

### ISSUE-007 — Multi-party unit access scoping
**Decision required:** When a unit has multiple linked contacts (joint owners, an outgoing and incoming tenant during handover), should each contact see only their own tickets for that unit, or should all linked contacts see all tickets raised for the unit?

**Why this decision is needed:** The requirements acknowledge the risk of "data mixing between different owners or tenants" but never specify the actual access rule. This is a genuine data-privacy question, not a cosmetic detail — getting it wrong means one resident could see another's complaint or maintenance history.

**Options:**
- **A — Unit-level visibility: any linked contact sees all tickets for the unit.** *Pros:* Simple, matches how a single physical unit is often treated administratively. *Cons:* A previous tenant could see tickets raised by the new tenant, or vice versa — a real privacy exposure during handovers.
- **B — Contact-level visibility: each contact sees only tickets they personally raised.** *Pros:* Strongest privacy protection; avoids inappropriate disclosure between unrelated occupants of the same unit over time. *Cons:* A legitimate joint owner might not see a ticket their co-owner raised, which could be confusing in some cases.

**Recommended option:** B, with an explicit exception process for joint owners who request shared visibility.

**Impact if no decision is made:** The CRM/contact data model cannot be finalized, and there is a real risk of shipping a privacy defect — one occupant seeing another's service history.

**Priority:** High
**Decision owner:** CRM Team

---

### ISSUE-008 — Extended ticket status list
**Decision required:** Confirm whether the ticket lifecycle should include Reopened, Cancelled, Rejected, Duplicate, and Pending Third-Party statuses, in addition to the six statuses named in the requirements (Open, In Progress, Pending Customer, Escalated, Resolved, Closed).

**Why this decision is needed:** The six named statuses cannot represent common real scenarios — a customer withdrawing a complaint, a duplicate call-in, or a Facility Management ticket waiting on an external contractor (which is not the same as waiting on the customer). Building the status model without these leads to inaccurate SLA and reporting data.

**Options:**
- **A — Use only the six statuses as literally specified.** *Pros:* Matches the document exactly. *Cons:* Forces real scenarios into the wrong status (e.g., a contractor delay reported as "Pending Customer"), corrupting SLA attribution and reporting accuracy.
- **B — Adopt the five additional statuses as proposed.** *Pros:* Produces accurate, defensible reporting; matches how FM/Leasing work actually happens. *Cons:* A slightly larger status model to communicate to agents and departments.

**Recommended option:** B.

**Impact if no decision is made:** Agents will informally work around missing statuses using free-text notes, which breaks the reporting and KPI accuracy the rest of this system depends on.

**Priority:** Medium
**Decision owner:** Customer Service

---

### ISSUE-010 — Department transfer authority and SLA impact
**Decision required:** Who is authorized to approve moving a ticket from one department to another (e.g., an issue logged as Facility Management that is actually a Leasing dispute), and does the SLA clock reset when that happens?

**Why this decision is needed:** No transfer rule exists in the requirements at all. Without one, either transfers cannot happen through the system (agents will work around it manually), or they happen with no approval control and a route to game SLA compliance by repeatedly transferring a ticket to restart its clock.

**Options:**
- **A — Any Department Employee can transfer freely; SLA clock resets on transfer.** *Pros:* Fast and flexible. *Cons:** Open to abuse — a ticket about to breach can be "transferred" to reset its clock and hide the breach.
- **B — Transfer requires Department Head approval; SLA clock continues without resetting.** *Pros:* Prevents SLA gaming; keeps a single accountable approval point. *Cons:* Slightly slower than free transfer.

**Recommended option:** B.

**Impact if no decision is made:** Either transfers are blocked entirely (frustrating legitimate misrouted tickets) or built with no safeguard against SLA-clock manipulation.

**Priority:** Medium
**Decision owner:** Department Head

---

### ISSUE-011 — Reopen window
**Decision required:** How long after closure can a customer or agent reopen a ticket for the same issue before a new ticket must be raised instead?

**Why this decision is needed:** No reopening policy exists in the requirements at all, despite reopening being a normal part of any service ticketing system (e.g., "the leak came back"). Without a defined window, agents will improvise inconsistently.

**Options:**
- **A — No formal reopen window; agents decide case by case.** *Pros:* Maximum flexibility. *Cons:* Inconsistent customer experience; makes CSAT and resolution-time reporting unreliable, since it's unclear which tickets are "really" reopens versus new issues.
- **B — Fixed window (e.g., 7 days) after which a new, linked ticket is created instead.** *Pros:* Consistent, predictable, easy to explain to customers and staff; preserves clean reporting. *Cons:* An edge case just outside the window creates a new ticket instead of reopening — a minor administrative inconvenience, not a service failure.

**Recommended option:** B, 7 days, configurable.

**Impact if no decision is made:** Reopen behavior is inconsistent across agents, and resolution-time/CSAT metrics become unreliable because it's unclear which "new" tickets are actually unresolved old ones.

**Priority:** Medium
**Decision owner:** Customer Service

---

### ISSUE-012 — UAE public holiday calendar ownership
**Decision required:** Who is responsible for maintaining the UAE public holiday calendar that the SLA engine uses to pause non-Critical timers, and on what schedule is it confirmed each year?

**Why this decision is needed:** Non-Critical SLA calculations exclude non-business days, but UAE public holidays shift yearly (based on the Islamic calendar) and are typically confirmed close to the date. Without an owner and a process, the calendar will go stale and SLA calculations will silently drift out of accuracy around each holiday period.

**Options:**
- **A — Hardcode holidays into the system per year, updated by IT on request.** *Pros:* No new process to design. *Cons:** Creates a recurring, easy-to-miss IT dependency; a missed update means every SLA calculation that period is wrong.
- **B — Maintain holidays in an editable reference table, owned by the Customer Service Manager, reviewed annually and whenever the government announces dates.** *Pros:* Business owner controls business data without needing a code change; matches how holiday dates are actually confirmed in the UAE. *Cons:* Requires a simple internal process to be followed reliably.

**Recommended option:** B.

**Impact if no decision is made:** SLA compliance figures around any unaccounted holiday will be systematically wrong, generating false breach alerts or masking real ones.

**Priority:** Medium
**Decision owner:** Customer Service

---

### ISSUE-013 — Escalation window and SLA warning threshold
**Decision required:** How long does the Department Head have to resolve an escalated ticket before it automatically advances to the General Manager? Separately, at what point before an SLA breach should the system issue an early warning?

**Why this decision is needed:** The requirements state the General Manager is triggered "if Level 2 does not resolve within the next escalation window" without ever defining that window's length. Similarly, no early-warning threshold exists — currently the only signal is the breach itself, which is too late to prevent it.

**Options:**
- **A — No proactive warning; alert only at breach.** *Pros:* Simplest to build. *Cons:* Removes any chance for staff to prevent a breach before it happens; the whole point of a warning threshold is lost.
- **B — Warn at a percentage of the resolution target elapsed (e.g., 75%), and set the Level 2→3 escalation window as a fixed, configurable duration per priority tier.** *Pros:* Gives staff a real chance to act before a breach; makes escalation timing predictable and tunable without a code change. *Cons:* Requires management to pick and periodically review specific numbers.

**Recommended option:** B — 75% warning threshold as a starting point, escalation window set per priority tier, both configurable.

**Impact if no decision is made:** The escalation engine cannot be finalized — a core part of the system's value proposition (fast senior visibility on failing tickets) is left undefined.

**Priority:** Medium
**Decision owner:** Management

---

### ISSUE-017 — Confirm the actual operating business week
**Decision required:** Confirm that Tiger Group/Geyness's actual operating week for SLA business-hours purposes is Saturday–Thursday (Friday as the sole non-working day), as stated in the requirements — not the more commonly used Saturday–Sunday weekend adopted by the UAE federal government since 2022.

**Why this decision is needed:** If "Saturday–Thursday" was a drafting error rather than intentional policy, every business-hours SLA calculation would be systematically wrong by one working day per week, every week.

**Options:**
- **A — Confirm Saturday–Thursday as stated.** *Pros:* No change needed; matches the current document. *Cons:* None, if this genuinely reflects operating policy.
- **B — Correct to Saturday–Sunday weekend (Monday–Friday work week).** *Pros:* Aligns with current UAE federal government convention. *Cons:* Changes every SLA calculation and reporting cadence (e.g., "Monday" reports) built around the stated week.

**Recommended option:** Confirm which is actually correct before building the calendar logic — build the work week as configurable data either way, so this can be corrected later without a code change if needed.

**Impact if no decision is made:** Low likelihood of blocking development (the system will be built to treat the work week as configurable regardless), but every SLA figure reported before confirmation carries a risk of being off by a full working day.

**Priority:** Low
**Decision owner:** Management

---

### ISSUE-018 — SLA pause during Pending Customer / Pending Third-Party
**Decision required:** Should the SLA clock pause while a ticket is waiting on the customer or on an external third party (e.g., a contractor), or does the department's SLA obligation continue running regardless?

**Why this decision is needed:** The requirements state non-Critical clocks run "during business hours" but never address what happens during a Pending status. Without pausing, departments are penalized for delays entirely outside their control, which would make SLA compliance reporting unfair and inaccurate.

**Options:**
- **A — Clock keeps running regardless of Pending status.** *Pros:* Simple; no pause logic needed. *Cons:* Punishes departments for customer or third-party delays they cannot control, distorting SLA compliance data and staff incentives.
- **B — Clock pauses on Pending Customer / Pending Third-Party, resumes when work restarts.** *Pros:* Fair, accurate attribution of delay; matches how most professional service SLAs work. *Cons:* Slightly more complex timer logic; requires disciplined use of Pending statuses by agents (a status left "Pending" incorrectly could unfairly pause a clock that should be running).

**Recommended option:** B, paired with monitoring for tickets left in a Pending status unusually long, to catch misuse.

**Impact if no decision is made:** SLA compliance reporting will either unfairly penalize departments for external delays, or (if built the other way without confirmation) allow the clock to be effectively paused without a real business decision behind it.

**Priority:** Low
**Decision owner:** Management

---

## Group B — Required Before Integrations

These three items shape the actual technical contract with an external system or vendor. They do not block starting core ticketing development, but must be resolved before the corresponding integration is built.

### ISSUE-003 — Geyness vs. Genesys vendor/platform identity
**Decision required:** Confirm that "Geyness" is the correct, final name of the contracted call-center vendor, and confirm whether Geyness's platform runs on Genesys (a distinct, well-known contact-center software product) or another named platform that this system would need to integrate with directly — or whether Geyness handles all telephony internally and only hands off ticket data.

**Why this decision is needed:** The requirements document and its own workflow diagram consistently name "Geyness" throughout, including in the signature block. A separate reference to "Genesys" surfaced when this analysis was commissioned. These may refer to the same vendor, a vendor built on that platform, or two unrelated things. Building the wrong integration contract wastes real engineering effort.

**Options:**
- **A — Treat "Geyness" as the vendor and design a generic hand-off integration (ticket data only), independent of whatever telephony platform Geyness uses internally.** *Pros:* Safe, does not assume anything unconfirmed; works regardless of Geyness's internal platform choice. *Cons:* If Geyness genuinely runs on Genesys and Tiger Group needs deeper telephony-level integration (e.g., call recordings, live call events), this narrower scope would need to be revisited.
- **B — Assume Genesys is the underlying platform and design directly against Genesys's APIs.** *Pros:* Potentially richer integration if true. *Cons:* If incorrect, this is wasted design and development effort, and the actual Geyness platform integration would still need to be built from scratch.

**Recommended option:** A, until vendor confirmation is received in writing.

**Impact if no decision is made:** The call-center integration (INT-02) cannot be scoped or estimated accurately; risk of building against the wrong API entirely.

**Priority:** High
**Decision owner:** Geyness/Genesys

---

### ISSUE-006 — CRM outage fallback for ticket creation
**Decision required:** During a CRM system outage, should agents be able to open a provisional ticket without a live, verified unit match — and if so, for which priority levels?

**Why this decision is needed:** The requirements require CRM downtime to be escalated within 15 minutes, but say nothing about what happens to new customer contacts arriving during that outage — particularly safety-critical issues (fire, flooding, access failure) that cannot simply wait for CRM to come back.

**Options:**
- **A — No ticket creation is possible during CRM downtime; all contacts are logged manually outside the system and entered once CRM is restored.** *Pros:* No changes needed to core ticket-creation logic. *Cons:* A genuine safety emergency during an outage could go unlogged in the system that is supposed to track it, and could be delayed in reaching Facility Management.
- **B — Allow provisional ticket creation (unverified unit reference) during CRM downtime for Critical/High priority only, reconciled against CRM automatically once it returns.** *Pros:* Ensures safety-critical issues are never blocked by a system outage; reconciliation keeps data integrity intact once CRM returns. *Cons:* Requires a small amount of additional logic to support and later reconcile provisional records.

**Recommended option:** B.

**Impact if no decision is made:** The CRM integration's failure-handling behavior is undefined, creating real safety/legal exposure if a Critical issue cannot be logged during an outage.

**Priority:** High
**Decision owner:** IT

---

### ISSUE-015 — Expected system scale
**Decision required:** Approximately how many units/towers, and how many concurrent Geyness agents, should the system be sized for at launch, and at a three-year horizon?

**Why this decision is needed:** No volume figures exist anywhere in the requirements. Integration capacity (especially CRM API call volume) and infrastructure sizing cannot be planned responsibly without at least an order-of-magnitude estimate.

**Options:**
- **A — Proceed without a figure, using a conservative, horizontally scalable default architecture, and revisit sizing once real usage data exists.** *Pros:* Does not block starting integration work. *Cons:* Risk of under- or over-provisioning at launch; capacity/cost conversations with vendors (e.g., CRM API rate limits) happen later than ideal.
- **B — Provide a volume estimate now and size integrations and infrastructure accordingly before building them.** *Pros:* More accurate capacity planning and vendor conversations (e.g., confirming CRM API limits support expected call volume) from the start. *Cons:* Requires management to produce a number that may itself only be an estimate.

**Recommended option:** B — even a rough estimate materially improves integration planning; Option A remains the fallback if no figure is available in time.

**Impact if no decision is made:** Integration and infrastructure sizing proceeds on generic defaults, with a real risk of hitting a CRM API rate limit or under-provisioned hosting capacity discovered only after go-live.

**Priority:** Low
**Decision owner:** IT

---

## Group C — Can Be Deferred Until After MVP

These three items are refinements to reporting, CSAT, and legal record-keeping. None of them block starting or completing core development; they should be resolved before the affected feature is finalized (dashboard tuning, CSAT policy, and the retention/backup configuration ahead of production go-live, respectively).

### ISSUE-009 — CSAT resend on reopened tickets
**Decision required:** Should a ticket that is reopened and later re-closed trigger a second CSAT survey?

**Why this decision is needed:** No rule addresses this. Sending a second survey risks survey fatigue and double-counting in CSAT trend reporting if not clearly separated from the first response.

**Options:**
- **A — Always resend CSAT on every closure, including after a reopen.** *Pros:* Simple, consistent rule. *Cons:* Risk of survey fatigue for a customer who already responded once; must be careful not to blend both responses into the same trend line.
- **B — Never resend CSAT after a reopen; only the first closure counts.** *Pros:* Avoids survey fatigue and double-counting entirely. *Cons:* Loses feedback on how well the reopened issue was actually resolved the second time.

**Recommended option:** A, with the survey explicitly tagged as "post-reopen" in reporting so it is analyzed separately from first-pass CSAT.

**Impact if no decision is made:** A default behavior will need to be picked to ship the reopen feature at all; low risk either way, but should be confirmed before CSAT trend reporting is relied upon for performance reviews.

**Priority:** Medium
**Decision owner:** Customer Service

---

### ISSUE-014 — "Repeat contact" definition for the KPI dashboard
**Decision required:** What should count as a customer "contacting again for the same issue" for the Repeat Contact Rate KPI (target ≤5%, alert threshold >10%)?

**Why this decision is needed:** The KPI is named and has numeric targets, but the source requirements never define what makes two contacts "the same issue" versus two genuinely separate requests from the same customer.

**Options:**
- **A — Ship the KPI as "provisional" using a working definition (e.g., same unit and category within 7 days), clearly labeled as such on the dashboard, and refine once confirmed.** *Pros:* Lets the dashboard go live on schedule with the other nine KPIs. *Cons:* The metric is not fully trustworthy for performance decisions until refined.
- **B — Hold this one KPI off the dashboard entirely until a definition is confirmed.** *Pros:* Avoids presenting an unreliable number. *Cons:* Delivers an incomplete dashboard relative to the full ten-KPI specification.

**Recommended option:** A.

**Impact if no decision is made:** The Repeat Contact Rate figure on the dashboard may not mean what management assumes it means, risking a wrong read on service quality trends.

**Priority:** Low
**Decision owner:** Customer Service

---

### ISSUE-016 — Applicable UAE data retention regulation
**Decision required:** Which specific UAE regulation sets the 7-year retention requirement stated in the source document, and does it apply uniformly to tickets, attachments, CSAT responses, and audit logs — or differently by record type?

**Why this decision is needed:** The requirements state a 7-year retention period "in line with UAE regulatory requirements" without citing the specific law. Different UAE regulations can carry different retention periods depending on record type (general commercial records vs. real-estate transaction documents vs. personal data), and this affects backup/retention configuration and storage cost.

**Options:**
- **A — Apply 7 years uniformly to all record types as a safe default, matching the stated figure exactly.** *Pros:* Matches the document as written; simple, single retention rule to configure and audit. *Cons:* May over-retain some record types (unnecessary storage cost) or, if the true regulation actually requires longer for a specific record type (e.g., title/SPA-related correspondence), under-retain it.
- **B — Confirm the specific regulation with Legal first, then configure retention per record type accordingly.** *Pros:* Ensures actual regulatory compliance rather than a best-guess approximation. *Cons:* Requires a Legal review step before the retention/backup configuration can be finalized ahead of go-live.

**Recommended option:** A as the interim default so development is not blocked, with B completed before production go-live.

**Impact if no decision is made:** Low near-term risk (development proceeds on the stated 7-year default), but a compliance gap could remain undetected until an audit or legal review after go-live.

**Priority:** Low
**Decision owner:** Management

---

## Summary for Sign-Off

| Group | Item count | Gate |
|---|---|---|
| A — Required before MVP development | 12 | Core ticket/status/SLA/escalation build cannot start with confidence until these are answered |
| B — Required before integrations | 3 | CRM, call-center, and infrastructure-sizing integration work cannot be scoped accurately until these are answered |
| C — Can be deferred until after MVP | 3 | Reporting/CSAT refinements and a Legal retention citation — resolve before the affected feature is finalized or before production go-live |

**Requested action:** Management review and decision (or delegation to the named owner) on each item in Groups A and B before Phase 2 (Architecture & Database Design) and Phase 4 (Core Ticketing MVP) begin, per the phased plan in the Solution Analysis document. Group C items may be resolved in parallel with development.
