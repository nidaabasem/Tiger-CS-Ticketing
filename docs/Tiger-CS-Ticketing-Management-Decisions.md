# Tiger Group — CS Ticketing System
## Management Decision Document

| | |
|---|---|
| **Purpose** | Obtain management decisions on the open items identified in the Solution Analysis before development proceeds |
| **Status** | Awaiting decisions — **no development, database design, or integration work has started** |
| **Version** | 2.0 — revised following senior architecture review; adds 5 new decisions and re-buckets all items against the reduced MVP scope |
| **Related document** | `docs/Tiger-CS-Ticketing-Solution-Analysis.md` |
| **Date** | 2026-08-17 |

### How to use this document
Each item states the decision needed, why it matters, the realistic options, and a recommendation. Nothing has been decided on management's behalf — the "Recommended option" is a starting position for discussion. Items are grouped by when the decision is actually needed, aligned to the three-tier scope now in the Solution Analysis: **MVP** (internal web app, phone-only intake, full SLA/escalation/lifecycle engine), **Phase 2** (SMS, CSAT, formal reporting, Website/WhatsApp, Geyness/Genesys platform integration), and **Phase 3** (Kiosk, social media, AI, advanced analytics).

### What changed in this revision
A senior architecture review added five new decisions — **ISSUE-019 through ISSUE-023** — covering the First Response SLA event, ticket-ID behavior on transfer, customer-portal scope, Resolve/Close/Reopen/Cancel/Reject authority, and the priority-change SLA policy. It also **shrank the MVP**, which moved four previously-blocking items (auto-ticket verification timing, the Geyness/Genesys vendor question, CSAT-on-reopen, and volume sizing) out of the MVP gate and into the Phase 2 gate, since the features they concern no longer ship in the first release.

---

## Decision Log (at a glance)

| ID | Question | Priority | Owner | Needed by |
|---|---|---|---|---|
| ISSUE-019 | What event satisfies First Response SLA — automated ack, or first human reply? | **Critical** | Management | Before MVP development |
| ISSUE-001 | When does the SLA clock start — creation or assignment? | Critical | Management | Before MVP development |
| ISSUE-021 | Is a customer self-service portal in scope, in any phase? | **High** | Management | Before MVP development |
| ISSUE-022 | Who may Resolve vs. Close a ticket, and who may Reopen/Cancel/Reject? | **High** | Management | Before MVP development |
| ISSUE-023 | What SLA policy applies when a ticket's priority changes? | **High** | Management | Before MVP development |
| ISSUE-004 | Does a Critical breach still notify the Department Head, or GM only? | High | Management | Before MVP development |
| ISSUE-006 | Can agents create provisional tickets during a CRM outage? | High | IT | Before MVP development |
| ISSUE-007 | How are multiple contacts on one unit scoped for access? | High | CRM Team | Before MVP development |
| ISSUE-008 | Confirm the five-dimension lifecycle model (TicketStatus/VerificationStatus/EscalationLevel/SlaState/ResolutionOutcome). | Medium | Customer Service | Before MVP development |
| ISSUE-020 | Should the ticket-ID `[DEPT]` segment change on department transfer? | Medium | IT | Before MVP development |
| ISSUE-005 | How many escalation retry cycles before forced level-up? | Medium | Customer Service | Before MVP development |
| ISSUE-010 | Who approves cross-department transfers, and does the SLA clock reset? | Medium | Department Head | Before MVP development |
| ISSUE-011 | What is the allowed window to reopen a closed ticket? | Medium | Customer Service | Before MVP development |
| ISSUE-012 | Who maintains the UAE public holiday calendar, and how often? | Medium | Customer Service | Before MVP development |
| ISSUE-013 | How long before an escalated ticket auto-advances Dept Head → GM? | Medium | Management | Before MVP development |
| ISSUE-017 | Confirm the actual operating week (Sat–Thu vs. Sat–Sun) | Low | Management | Before MVP development |
| ISSUE-018 | Does the SLA clock pause while waiting on the customer or a third party? | Low | Management | Before MVP development |
| ISSUE-003 | Is "Geyness" the final vendor name, and what platform does it run on? | High | Geyness/Genesys | Before Phase 2 |
| ISSUE-002 | For auto-ticket channels, is the ticket number issued before or after CRM verification? | Critical | Management | Before Phase 2 |
| ISSUE-015 | Expected unit/tower count and concurrent-agent count for Phase 2? | Low | IT | Before Phase 2 |
| ISSUE-009 | Does a reopened-then-reclosed ticket trigger a second CSAT survey? | Medium | Customer Service | Before Phase 2 |
| ISSUE-014 | What counts as a "repeat contact" for the KPI? | Low | Customer Service | Phase 3 / post-launch |
| ISSUE-016 | Which UAE regulation sets the 7-year retention period? | Low | Management | Phase 3 / post-launch |

---

## Group A — Required Before MVP Development

These seventeen items shape the core ticket, permission, and SLA/escalation data model that ships in the first release. Answering them after coding starts means rebuilding rather than configuring.

### ISSUE-019 — What event satisfies First Response SLA *(new)*
**Decision required:** Does the SLA "first response" clock stop at the automated channel acknowledgement, or only at the first genuine, human-authored reply to the customer?

**Why this decision is needed:** The system sends an automated acknowledgement (ticket number, expected response time) within seconds of every ticket being created. If that automated message counts as "first response," the 15-minute/1-hour/4-hour/24-hour first-response targets in Section 7.1 would be satisfied automatically, every single time, regardless of how quickly a human actually engaged with the request. The KPI would measure nothing real.

**Options:**
- **A — The automated acknowledgement counts as first response.** *Pros:* Simplest possible rule; the target is always met, so no breach alerts to manage. *Cons:* Makes the KPI meaningless as a measure of service quality — a genuinely slow agent response would be invisible to every report and dashboard, undermining the entire purpose of tracking it. This also directly weakens Tiger Group's position in the SLA-compliance conversation with Geyness, since a contractual KPI that always passes protects the vendor, not the customer.
- **B — Only the first human-authored response to the customer counts.** *Pros:* Measures what actually matters — how quickly a real person engaged with the specific request; matches the customer's actual experience; keeps the SLA meaningful as a management and contractual tool. *Cons:* Requires the system to capture a distinct "first human response" event, separate from the automated send — a small but necessary piece of additional tracking.

**Recommended option:** B.

**Impact if no decision is made:** By default, the automated acknowledgement will effectively become the answer (since it is the only response the system currently guarantees), silently adopting Option A's downside without anyone having chosen it.

**Priority:** Critical
**Decision owner:** Management

---

### ISSUE-001 — SLA clock start point
**Decision required:** Does the SLA clock start when a ticket is created, or when it is assigned to a named department owner?

**Why this decision is needed:** The requirements document contradicts itself — the field specification says the timer starts at creation, but the workflow diagram shows it starting at assignment. Both sides must measure the contractual SLA-compliance KPI the same way.

**Options:**
- **A — Start at ticket creation.** *Pros:* Matches the written field specification; simplest to implement. *Cons:* Penalizes departments for queue/assignment delays outside their control.
- **B — Start at owner assignment.** *Pros:* Matches the workflow diagram; measures actual working time only. *Cons:* Creates an unmeasured gap between creation and assignment with no accountability.
- **C — Start at creation, but separately track time-to-assignment as its own metric.** *Pros:* Satisfies the written spec, closes Option A's accountability gap, compatible with either final answer without rework. *Cons:* Slightly more reporting complexity.

**Recommended option:** C.

**Impact if no decision is made:** The SLA engine cannot be built with confidence; whichever assumption is coded in risks a later dispute with Geyness over contractual SLA-compliance figures.

**Priority:** Critical
**Decision owner:** Management

---

### ISSUE-021 — Customer self-service portal scope *(new)*
**Decision required:** Is an authenticated customer self-service portal — login, ticket-history view, self-service reopen — in scope for this system, in any phase? If so, which phase?

**Why this decision is needed:** The source requirements never describe a customer login or self-service capability anywhere — the customer's only touchpoints named in the document are phone, digital form/chat, kiosk, and WhatsApp, all mediated by a Geyness agent or a simple auto-ticket submission, never an authenticated account. An earlier draft of this analysis implicitly assumed a portal existed (e.g., "customer views own ticket history," "customer self-service reopen"). That assumption adds real, uncosted scope — a second identity system exposed to the public internet, with its own security and data-exposure risk — that nobody has actually approved.

**Options:**
- **A — No customer portal, in any phase. All customer interaction remains agent-mediated (phone) plus outbound notifications (email now, SMS/WhatsApp from Phase 2).** *Pros:* Matches the source requirements exactly; avoids building and securing an external-facing authentication surface nobody asked for; keeps the system's security boundary simple (only internal staff ever authenticate). *Cons:* A customer who wants to check their ticket status must call in, which may add call volume Geyness has to absorb.
- **B — Approve a customer portal, to be scoped and built as its own initiative in a later phase (e.g., Phase 2 or Phase 3).** *Pros:* Reduces repeat-contact call volume over time; matches modern customer service expectations. *Cons:* A real, non-trivial addition — external authentication, strict per-customer data scoping (especially given the multi-party-unit question in ISSUE-007), and its own security review — that should not be assumed into any existing phase's estimate.

**Recommended option:** A for now — explicitly exclude portal capability from every phase in this roadmap unless and until it is separately approved and scoped.

**Impact if no decision is made:** Without an explicit "no," portal-like features tend to creep back in piecemeal (a "view my ticket" link here, a "reopen" button there) because they seem individually reasonable, quietly reintroducing an entire unapproved system boundary.

**Priority:** High
**Decision owner:** Management

---

### ISSUE-022 — Resolve / Close / Reopen / Cancel / Reject authority *(new)*
**Decision required:** Who is authorized to mark a ticket's underlying work as done (Resolve), who finalizes it after confirming the customer has been told (Close), and who may Reopen, Cancel, or Reject a ticket?

**Why this decision is needed:** The source's closure criteria state a ticket may only close when the resolution note is complete **and** the customer has been notified — two separate facts. But nothing says who is accountable for each fact, or whether one person can simply assert both. In practice, the department doing the maintenance/leasing/sales work typically has no visibility into whether Geyness has actually reached the customer — that communication channel belongs to the CS/Geyness side, not the department.

**Options:**
- **A — The same role does both: whoever resolves the work also closes the ticket.** *Pros:* Fast, no handoff, fewer clicks. *Cons:* The department employee closing the ticket has no direct way to confirm the customer was actually told anything — they would effectively be certifying a fact they can't verify, which risks tickets being closed (and CSAT surveys sent) to customers who were never actually informed.
- **B — Department Employee/Head Resolves (marks the work done); Geyness Agent/Supervisor/CS Manager Closes (confirms the customer was notified, then finalizes).** *Pros:* Matches how the two facts required for closure are actually known — the department genuinely knows if the work is done, CS genuinely knows if the customer was told; enforces the source document's own closure criteria as two independently-checked steps, not one person's word. *Cons:* Adds one handoff to every ticket, which could add a small delay between "work finished" and "ticket formally closed."

**Recommended option:** B.

**Impact if no decision is made:** Left undefined, the natural default in most implementations is Option A (single-role convenience), which quietly drops the "customer notified" half of the closure criteria as anything more than an unverified checkbox — directly undermining a requirement the source document states explicitly.

**Priority:** High
**Decision owner:** Management

---

### ISSUE-023 — Priority-change SLA policy *(new)*
**Decision required:** What specific, defined policy governs the SLA clock when a ticket's priority changes mid-flight, and should downgrading a ticket away from Critical or High require approval?

**Why this decision is needed:** An earlier draft of this analysis referenced a "proportional carry-forward" calculation for handling a priority change — but that is a description of a desired outcome, not an algorithm; it cannot actually be implemented without someone specifying the exact formula. Separately, without any safeguard, a ticket at real risk of breaching its SLA could simply be re-prioritized downward at the last minute, making the impending breach disappear from reporting without ever being resolved.

**Options:**
- **A — Full clock restart under the new tier, discarding elapsed time.** *Pros:* Simple to implement and explain. *Cons:* Arguably too generous on an upgrade (a ticket that's been sitting for days suddenly gets a fresh, full Critical clock) and, without a separate safeguard, does nothing to prevent the "downgrade to hide a breach" problem.
- **B — Attempt to carry forward the elapsed proportion into the new tier's target.** *Pros:* Feels intuitively fairer than a full restart. *Cons:* This is exactly the undefined approach being replaced — there is no single, obviously-correct formula for "proportion" across tiers with very different targets (e.g., 15 minutes vs. 7 business days), and any formula chosen would need to be independently justified and tested.
- **C — Close the current SLA period entirely (retained in full history) and open a fresh period under the new tier from the moment of change — no proration in either direction — combined with a mandatory Department-Head-or-above approval before any downgrade from Critical or High takes effect.** *Pros:* Simple, unambiguous, and fully auditable — every SLA period a ticket passed through is reconstructable from history; the approval gate directly closes the "quiet downgrade to avoid a breach" loophole, which a pure clock-recalculation policy (A or B) does not address on its own. *Cons:* An *upgraded* ticket gets a fresh, full target under the stricter tier rather than a shortened one — which is the safer direction to err in, not a real downside.

**Recommended option:** C.

**Impact if no decision is made:** The SLA engine's priority-change logic cannot be built at all — "proportional carry-forward" is not something a developer can implement without the missing formula, and without the approval gate, the system has no defense against SLA-compliance figures being manipulated by re-prioritizing at-risk tickets.

**Priority:** High
**Decision owner:** Management

---

### ISSUE-004 — Critical breach notification routing
**Decision required:** When a Critical-priority ticket breaches its SLA, is the Department Head still notified alongside the General Manager, or does the alert go to the GM only?

**Why this decision is needed:** One part of the requirements says a Critical breach means "immediate GM notification"; the general escalation model elsewhere says every breach routes through the Department Head first. As written, these conflict.

**Options:**
- **A — GM only, as literally stated.** *Pros:* Matches the specific SLA table wording. *Cons:* The Department Head — operationally responsible for the ticket — may be unaware a Critical issue is underway in their own department.
- **B — Department Head and GM notified simultaneously.** *Pros:* GM still gets immediate visibility; the Department Head stays informed and can act without waiting to be told by the GM. *Cons:* One additional notification per Critical breach — negligible cost.

**Recommended option:** B.

**Impact if no decision is made:** Notification routing is built on a guess; if wrong, either the GM is not alerted fast enough, or a Department Head is blindsided by an escalation they never saw.

**Priority:** High
**Decision owner:** Management

---

### ISSUE-006 — CRM outage fallback for ticket creation
**Decision required:** During a CRM system outage, should agents be able to open a provisional ticket without a live, verified unit match — and if so, for which priority levels? (This applies to the MVP itself, since the CRM lookup integration is core to phone-based ticket creation, not deferred to a later phase.)

**Why this decision is needed:** The requirements require CRM downtime to be escalated within 15 minutes, but say nothing about what happens to new customer contacts arriving during that outage — particularly safety-critical issues that cannot simply wait for CRM to come back, and MVP's entire intake model is a live agent on the phone needing that CRM lookup in real time.

**Options:**
- **A — No ticket creation is possible during CRM downtime; contacts are logged manually outside the system and entered once CRM is restored.** *Pros:* No changes needed to core ticket-creation logic. *Cons:* A genuine safety emergency during an outage could go unlogged in the system that is supposed to track it.
- **B — Allow provisional ticket creation (unverified unit reference) during CRM downtime for Critical/High priority only, reconciled against CRM automatically once it returns.** *Pros:* Ensures safety-critical issues are never blocked by a system outage; reconciliation keeps data integrity intact. *Cons:* Requires additional logic to support and later reconcile provisional records.

**Recommended option:** B.

**Impact if no decision is made:** The CRM integration's failure-handling behavior is undefined, creating real safety/legal exposure if a Critical issue cannot be logged during an outage — and this now affects the MVP directly, not a future phase.

**Priority:** High
**Decision owner:** IT

---

### ISSUE-007 — Multi-party unit access scoping
**Decision required:** When a unit has multiple linked contacts (joint owners, an outgoing and incoming tenant during handover), should each contact see only their own tickets for that unit, or should all linked contacts see all tickets raised for the unit?

**Why this decision is needed:** The requirements acknowledge the risk of "data mixing between different owners or tenants" but never specify the actual access rule. This is a genuine data-privacy question — getting it wrong means one resident could see another's complaint or maintenance history. Note: the ticketing system itself never masters this data (the CRM does — see the Solution Analysis §10.3 correction); this decision is about the *access rule* applied on top of CRM-sourced identifiers, not about where the data lives.

**Options:**
- **A — Unit-level visibility: any linked contact sees all tickets for the unit.** *Pros:* Simple. *Cons:* A previous tenant could see tickets raised by the new tenant, or vice versa — a real privacy exposure during handovers.
- **B — Contact-level visibility: each contact sees only tickets they personally raised.** *Pros:* Strongest privacy protection. *Cons:* A legitimate joint owner might not see a ticket their co-owner raised.

**Recommended option:** B, with an explicit exception process for joint owners who request shared visibility.

**Impact if no decision is made:** Real risk of shipping a privacy defect — one occupant seeing another's service history.

**Priority:** High
**Decision owner:** CRM Team

---

### ISSUE-008 — Confirm the five-dimension lifecycle model
**Decision required:** Confirm the redesigned ticket-state model — `TicketStatus`, `VerificationStatus`, `EscalationLevel`, `SlaState`, and `ResolutionOutcome`, tracked as five independent dimensions rather than one combined status field — and each dimension's value set.

**Why this decision is needed:** A single combined status field cannot represent real scenarios correctly — for example, "escalated but still being actively worked" cannot exist as one status value without either losing the "still being worked" information or the "escalated" information. The revised model tracks these as independent facts about the same ticket. This also resolves how Reopen (an event, not a status) and Duplicate (an outcome requiring a linked ticket ID, not a status) are represented.

**Options:**
- **A — Keep a single combined status field with additional values bolted on (Escalated, Reopened, Duplicate, etc., as originally proposed).** *Pros:* Familiar, one field to look at. *Cons:* Cannot express combinations that genuinely occur (escalated + in progress), and "Reopened"/"Duplicate" are conceptually an event and an outcome, not a workflow stage — forcing them into the status field misrepresents what actually happened.
- **B — Adopt the five independent dimensions as specified.** *Pros:* Correctly represents real combinations; each dimension has clean, independent transition rules and its own audit trail; matches how the escalation and resolution concepts actually behave. *Cons:* Slightly more to explain to agents/staff up front (five fields instead of one), though the agent-facing UI can still present it as a single clear picture.

**Recommended option:** B.

**Impact if no decision is made:** Development proceeds on the flatter single-status model, which will need to be reworked later once the "escalated while still in progress" scenario is hit in practice — exactly the kind of retrofit this document exists to avoid.

**Priority:** Medium
**Decision owner:** Customer Service

---

### ISSUE-020 — Ticket-ID behavior on department transfer *(new)*
**Decision required:** When a ticket transfers from one department to another, does the ticket ID's `[DEPT]` segment change to reflect the new department, or does the ID stay exactly as originally issued?

**Why this decision is needed:** Ticket numbers are read out to customers, quoted in emails, and used as the audit reference for the entire lifecycle of the request. If the ID itself can change, a customer's reference number could literally stop matching what they were told the moment their ticket moves department — and any previously sent communication referencing the old ID becomes inconsistent with the system's current record.

**Options:**
- **A — `[DEPT]` updates to reflect whichever department currently owns the ticket.** *Pros:* At a glance, the ID always shows current ownership. *Cons:** Breaks the basic expectation that a reference number, once issued, stays fixed — every previously sent acknowledgement, escalation notice, or customer conversation referencing the original ID becomes stale the moment a transfer happens.
- **B — The ticket ID is immutable for the life of the ticket; `[DEPT]` always reflects the department that originally created and routed it. Current ownership is tracked as a separate, mutable field, visible on the ticket record but not part of the permanent ID.** *Pros:* The customer-facing reference number never changes, matching standard practice in ticketing systems generally; audit history stays clean and unambiguous. *Cons:* Looking at the ID alone doesn't tell you who currently owns the ticket — a separate field must be checked.

**Recommended option:** B.

**Impact if no decision is made:** Without an explicit rule, a mutable-ID implementation is a real risk simply because "update the DEPT code on transfer" sounds intuitive — and it would break every previously issued reference number the first time a ticket moves departments.

**Priority:** Medium
**Decision owner:** IT

---

### ISSUE-005 — Escalation retry cap
**Decision required:** After a ticket is escalated and "re-assigned to retry," how many retry cycles are allowed before the system must force it up to the next escalation level automatically?

**Why this decision is needed:** The workflow diagram shows escalated tickets looping back into normal work with no defined exit condition. Without a cap, a chronically mishandled ticket could cycle indefinitely at the same escalation level.

**Options:**
- **A — No automatic cap; rely on staff judgment.** *Pros:* No extra logic. *Cons:* Relies entirely on someone remembering to push it up manually.
- **B — Cap at a fixed number of retries (e.g., two), then force an automatic level-up.** *Pros:* Guarantees chronic issues surface to senior management automatically. *Cons:* A fixed number may occasionally escalate a ticket that was close to resolution.

**Recommended option:** B, with the number configurable.

**Impact if no decision is made:** A ticket could remain stuck at Department Head level indefinitely with no safeguard.

**Priority:** Medium
**Decision owner:** Customer Service

---

### ISSUE-010 — Department transfer authority and SLA impact
**Decision required:** Who is authorized to approve moving a ticket from one department to another, and does the SLA clock reset when that happens?

**Why this decision is needed:** No transfer rule exists in the requirements at all. Without one, transfers either can't happen through the system, or happen with no approval control and a route to game SLA compliance by repeatedly transferring a ticket to restart its clock.

**Options:**
- **A — Any Department Employee can transfer freely; SLA clock resets on transfer.** *Pros:* Fast and flexible. *Cons:* Open to abuse — a ticket about to breach can be "transferred" to reset its clock.
- **B — Transfer requires Department Head approval; SLA clock continues without resetting.** *Pros:* Prevents SLA gaming; keeps a single accountable approval point. *Cons:* Slightly slower than free transfer.

**Recommended option:** B.

**Impact if no decision is made:** Either transfers are blocked entirely, or built with no safeguard against SLA-clock manipulation.

**Priority:** Medium
**Decision owner:** Department Head

---

### ISSUE-011 — Reopen window
**Decision required:** How long after closure can a ticket be reopened for the same issue before a new ticket must be raised instead?

**Why this decision is needed:** No reopening policy exists in the requirements at all, despite reopening being a normal part of any service ticketing system.

**Options:**
- **A — No formal reopen window; case by case.** *Pros:* Maximum flexibility. *Cons:* Inconsistent experience; unreliable CSAT/resolution-time reporting.
- **B — Fixed window (e.g., 7 days), after which a new, linked ticket is created instead.** *Pros:* Consistent, predictable, preserves clean reporting. *Cons:* An edge case just outside the window creates a new ticket instead — a minor inconvenience.

**Recommended option:** B, 7 days, configurable.

**Impact if no decision is made:** Reopen behavior is inconsistent, and resolution-time/CSAT metrics become unreliable.

**Priority:** Medium
**Decision owner:** Customer Service

---

### ISSUE-012 — UAE public holiday calendar ownership
**Decision required:** Who is responsible for maintaining the UAE public holiday calendar the SLA engine uses, and on what schedule is it confirmed each year?

**Why this decision is needed:** Non-Critical SLA calculations exclude non-business days; UAE holidays shift yearly and are confirmed close to the date. Without an owner and process, the calendar goes stale and SLA calculations drift silently.

**Options:**
- **A — Hardcode holidays per year, updated by IT on request.** *Pros:* No new process. *Cons:* Recurring, easy-to-miss IT dependency.
- **B — Editable reference table, owned by the CS Manager, reviewed annually and on each government announcement.** *Pros:* Business owner controls business data without a code change. *Cons:* Requires a simple internal process to be followed reliably.

**Recommended option:** B.

**Impact if no decision is made:** SLA compliance figures around any unaccounted holiday will be systematically wrong.

**Priority:** Medium
**Decision owner:** Customer Service

---

### ISSUE-013 — Escalation window and SLA warning threshold
**Decision required:** How long does the Department Head have to resolve an escalated ticket before it automatically advances to the GM? At what point before a breach should the system issue an early warning?

**Why this decision is needed:** The requirements never define the Level 2→3 escalation window's length, nor any pre-breach warning threshold.

**Options:**
- **A — No proactive warning; alert only at breach.** *Pros:* Simplest. *Cons:* Removes any chance to act before a breach happens.
- **B — Warn at a percentage of the resolution target elapsed (e.g., 75%), and set the Level 2→3 window as a fixed, configurable duration per priority tier.** *Pros:* Gives staff a real chance to act before a breach; predictable, tunable escalation timing. *Cons:* Requires management to pick and periodically review specific numbers.

**Recommended option:** B.

**Impact if no decision is made:** The escalation engine cannot be finalized.

**Priority:** Medium
**Decision owner:** Management

---

### ISSUE-017 — Confirm the actual operating business week
**Decision required:** Confirm Tiger Group/Geyness's actual operating week is Saturday–Thursday (Friday as sole non-working day), as stated — not the more common Saturday–Sunday weekend used by the UAE federal government since 2022.

**Why this decision is needed:** If this was a drafting error, every business-hours SLA calculation would be systematically wrong by one working day per week.

**Options:**
- **A — Confirm Saturday–Thursday as stated.** *Pros:* No change needed. *Cons:* None, if genuinely correct.
- **B — Correct to Saturday–Sunday weekend.** *Pros:* Aligns with current UAE federal convention. *Cons:* Changes every SLA calculation and reporting cadence built around the stated week.

**Recommended option:** Confirm before building the calendar logic; build the work week as configurable data regardless, so it can be corrected without a code change if needed.

**Impact if no decision is made:** Low likelihood of blocking development (built as configurable data regardless), but every SLA figure reported before confirmation risks being off by a full working day.

**Priority:** Low
**Decision owner:** Management

---

### ISSUE-018 — SLA pause during Pending Customer / Pending Third-Party
**Decision required:** Should the SLA clock pause while a ticket is waiting on the customer or on an external third party, or does the department's SLA obligation continue running regardless?

**Why this decision is needed:** The requirements never address what happens to the clock during a Pending status. Without pausing, departments are penalized for delays entirely outside their control.

**Options:**
- **A — Clock keeps running regardless.** *Pros:* Simple. *Cons:* Punishes departments for customer/third-party delays they cannot control.
- **B — Clock pauses on Pending Customer / Pending Third-Party, resumes when work restarts.** *Pros:* Fair, accurate delay attribution. *Cons:* Requires disciplined use of Pending statuses (a status left "Pending" incorrectly could unfairly pause a clock that should be running).

**Recommended option:** B, paired with monitoring for tickets left Pending unusually long.

**Impact if no decision is made:** SLA compliance reporting will unfairly penalize departments for external delays, or effectively pause without a real decision behind it.

**Priority:** Low
**Decision owner:** Management

---

## Group B — Required Before Phase 2

Four items — three of which previously blocked MVP under the earlier, larger scope. Because MVP is now phone-only with no CSAT and no external call-center integration, these decisions no longer gate the first release — they gate Phase 2, when auto-ticket channels, CSAT, and the Geyness/Genesys platform integration are actually built.

### ISSUE-003 — Geyness vs. Genesys vendor/platform identity
**Decision required:** Confirm "Geyness" is the correct, final name of the contracted call-center vendor, and confirm whether Geyness's platform runs on Genesys or another named platform this system would need to integrate with directly — or whether Geyness handles telephony internally and only hands off ticket data.

**Why this decision is needed:** The requirements document and its own workflow diagram consistently name "Geyness" throughout; a separate reference to "Genesys" surfaced when this analysis was commissioned. Building the wrong integration contract wastes real engineering effort — and this integration (INT-02) is explicitly Phase 2 scope, so there is no need to guess before then.

**Options:**
- **A — Treat "Geyness" as the vendor and design a generic hand-off integration, independent of whatever telephony platform Geyness uses internally.** *Pros:* Safe; works regardless of Geyness's internal platform choice. *Cons:* If Geyness genuinely runs on Genesys and deeper telephony-level integration is wanted, this narrower scope would need revisiting.
- **B — Assume Genesys is the underlying platform and design directly against its APIs.** *Pros:* Potentially richer integration if true. *Cons:* If incorrect, wasted design/development effort.

**Recommended option:** A, until vendor confirmation is received in writing, and in any case no later than the start of Phase 2 design.

**Impact if no decision is made:** Phase 2's call-center integration cannot be scoped or estimated accurately.

**Priority:** High
**Decision owner:** Geyness/Genesys

---

### ISSUE-002 — Ticket creation before unit verification (auto-ticket channels)
**Decision required:** For auto-ticket channels (App/Website, WhatsApp — Phase 2 scope), should the customer receive a ticket number immediately, or only after the unit number is verified against the CRM?

**Why this decision is needed:** The stated Core Rule is "no ticket without a verified unit number," but the same document marks these channels as auto-ticketing on submission — before any agent has verified anything. **This did not need to be resolved for MVP, since MVP has no auto-ticket channel at all** (phone-only, agent-verified before creation). It becomes a real, blocking question the moment Phase 2's Website/WhatsApp intake is designed.

**Options:**
- **A — Verify first, ticket number issued after.** *Pros:* Fully honors the Core Rule. *Cons:* Slower, worse customer experience on digital/self-service channels.
- **B — Issue a ticket number immediately; verify in the background.** *Pros:* Better customer experience. *Cons:* Breaks the Core Rule as literally written; unverified records could reach departments if verification fails silently.
- **C — Issue a provisional reference immediately, convert to a full ticket only once verified, with automatic escalation if verification is not completed within a set time.** *Pros:* Preserves good customer experience without breaking the Core Rule; unverified submissions stay visible and time-bounded. *Cons:* Requires a "provisional" state with its own handling rules.

**Recommended option:** C.

**Impact if no decision is made:** Phase 2's auto-ticket channels cannot be designed — developers must guess which rule takes precedence.

**Priority:** Critical (for Phase 2 — not a blocker for MVP)
**Decision owner:** Management

---

### ISSUE-015 — Expected system scale
**Decision required:** Approximately how many units/towers, and how many concurrent Geyness agents, should the system be sized for at Phase 2 launch (when customer-facing channels add real load), and at a three-year horizon?

**Why this decision is needed:** No volume figures exist anywhere in the requirements. MVP's phone-only, internal-agent-driven load is modest and does not require this number to proceed; Phase 2's multi-channel, CRM-API-heavy load does.

**Options:**
- **A — Proceed without a figure, using a conservative, horizontally scalable default architecture, and revisit sizing before Phase 2.** *Pros:* Does not block MVP or early Phase 2 design. *Cons:* Risk of under/over-provisioning once real customer-facing volume appears.
- **B — Provide a volume estimate now.** *Pros:* More accurate capacity planning and vendor conversations (e.g., CRM API rate limits) ahead of Phase 2. *Cons:* Requires management to produce a number that may itself only be an estimate.

**Recommended option:** B — even a rough estimate materially improves Phase 2 planning; Option A remains the fallback.

**Impact if no decision is made:** Phase 2 integration and infrastructure sizing proceeds on generic defaults, with real risk of hitting a CRM API rate limit or under-provisioned hosting only discovered after Phase 2 go-live.

**Priority:** Low
**Decision owner:** IT

---

### ISSUE-009 — CSAT resend on reopened tickets
**Decision required:** Should a ticket that is reopened and later re-closed trigger a second CSAT survey?

**Why this decision is needed:** CSAT itself is Phase 2 scope — this question simply doesn't arise until then. No rule addresses it in the source; sending a second survey risks fatigue and double-counting if not clearly separated from the first response.

**Options:**
- **A — Always resend CSAT on every closure, including after a reopen.** *Pros:* Simple, consistent rule. *Cons:* Risk of survey fatigue; must avoid blending both responses into one trend line.
- **B — Never resend after a reopen; only the first closure counts.** *Pros:* Avoids fatigue/double-counting entirely. *Cons:* Loses feedback on how well the reopened issue was resolved the second time.

**Recommended option:** A, with the survey explicitly tagged "post-reopen" in reporting.

**Impact if no decision is made:** A default behavior must be picked to ship CSAT at all; low risk either way, but should be confirmed before CSAT trend reporting is relied upon for performance reviews.

**Priority:** Medium
**Decision owner:** Customer Service

---

## Group C — Can Be Deferred Until Phase 3 / Production Go-Live

Two items — refinements to advanced analytics and legal record-keeping. Neither blocks MVP or Phase 2 development.

### ISSUE-014 — "Repeat contact" definition for the KPI dashboard
**Decision required:** What should count as a customer "contacting again for the same issue" for the Repeat Contact Rate KPI? This metric is now explicitly Phase 3 (Advanced KPI/analytics) scope.

**Why this decision is needed:** The KPI is named with numeric targets, but the source never defines what makes two contacts "the same issue."

**Options:**
- **A — Ship as "provisional" using a working definition (e.g., same unit and category within 7 days), clearly labeled, refined once confirmed.** *Pros:* Lets Phase 3's dashboard go live on schedule with the other metrics. *Cons:* Not fully trustworthy for performance decisions until refined.
- **B — Hold this one KPI off the dashboard entirely until a definition is confirmed.** *Pros:* Avoids presenting an unreliable number. *Cons:* Delivers an incomplete dashboard relative to the full metric specification.

**Recommended option:** A.

**Impact if no decision is made:** The figure on the dashboard may not mean what management assumes, risking a wrong read on service-quality trends.

**Priority:** Low
**Decision owner:** Customer Service

---

### ISSUE-016 — Applicable UAE data retention regulation
**Decision required:** Which specific UAE regulation sets the 7-year retention requirement, and does it apply uniformly to tickets, attachments, CSAT responses, and audit logs — or differently by record type?

**Why this decision is needed:** The requirements state a 7-year retention period "in line with UAE regulatory requirements" without citing the specific law. Different UAE regulations can carry different retention periods depending on record type.

**Options:**
- **A — Apply 7 years uniformly to all record types as a safe default.** *Pros:* Matches the document as written; simple, single rule to configure and audit. *Cons:* May over-retain some record types or under-retain another if the true regulation requires longer for a specific one.
- **B — Confirm the specific regulation with Legal first, then configure retention per record type.** *Pros:* Ensures actual regulatory compliance rather than a best-guess approximation. *Cons:* Requires a Legal review step before the retention/backup configuration is finalized.

**Recommended option:** A as the interim default so development is not blocked, with B completed before production go-live (in practice, before Phase 2's data volume grows materially).

**Impact if no decision is made:** Low near-term risk, but a compliance gap could remain undetected until an audit or legal review after go-live.

**Priority:** Low
**Decision owner:** Management

---

## Summary for Sign-Off

| Group | Item count | Gate |
|---|---|---|
| A — Required before MVP development | 17 | Core ticket/permission/SLA/escalation build (Phase 4 in the Solution Analysis) cannot start with confidence until these are answered — 5 of them (ISSUE-019, 001, 021, 022, 023) are the highest-priority subset and should be answered first |
| B — Required before Phase 2 | 4 | Auto-ticket channel design, the CRM/Geyness vendor integration, and CSAT policy cannot be scoped accurately until these are answered |
| C — Can be deferred until Phase 3 / production go-live | 2 | Advanced KPI refinement and a Legal retention citation |

**Requested action:** Management review and decision (or delegation to the named owner) on each item in Group A before Phase 2 (Architecture & Database Design) and Phase 4 (Core Ticketing MVP) begin, per the Solution Analysis's implementation-phase plan. Group B items should be resolved before Phase 11 (Phase 2 Release) design starts. Group C items may be resolved any time before the affected feature or production go-live.
