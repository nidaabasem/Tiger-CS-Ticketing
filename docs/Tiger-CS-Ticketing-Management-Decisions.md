# Tiger Group — CS Ticketing System
## Technical Decision Register

| | |
|---|---|
| **Purpose** | Record every open decision identified in the Solution Analysis, with options, trade-offs, and a recommendation, for review and approval before development proceeds |
| **Status** | Awaiting decisions — **no development, database design, or integration work has started** |
| **Version** | 3.0 — final correction pass; retitled from "Management Decision Document," language revised to a neutral register, sign-off table added |
| **Related documents** | `docs/Tiger-CS-Ticketing-Solution-Analysis.md` (full analysis) · `docs/Tiger-CS-Ticketing-Executive-Decisions.md` (MVP-blocking decisions only, for a management meeting) |
| **Date** | 2026-08-17 |

### How to use this document
Each item states the decision required, why it is needed, the realistic options with their trade-offs, and a recommendation. The recommendation is a starting position for discussion, not a decision made on management's behalf. Items are grouped by when the decision is actually needed, aligned to the scope in the Solution Analysis: **MVP** (internal web application, phone-only intake, full SLA/escalation/lifecycle engine), **Phase 2** (SMS, CSAT, formal reporting, Website/WhatsApp, Geyness/Genesys platform integration), and **Phase 3** (Kiosk, social media, AI, advanced analytics). This document is the detailed technical register; for a shorter meeting-ready version covering only MVP-blocking items, see the companion Executive Decisions document.

### Changes in this revision
- ISSUE-005 is removed; its concern (no defined exit from the escalation retry loop) is now covered by ISSUE-013, which defines a configurable, priority-based Level 2→3 escalation window instead of a retry count.
- ISSUE-018's priority is raised from Low to High.
- ISSUE-023 is revised to state explicit, separate rules for a priority upgrade and an approved downgrade, with a guarantee that elapsed time, recorded breaches, and SLA history are never erased.
- ISSUE-007 is rewritten for a system with no customer portal, focused on phone/notification-based disclosure rather than screen-level access control.
- ISSUE-016 is reassigned to Legal/Compliance and reclassified as required before production go-live.
- ISSUE-012 now names a business owner (Customer Service or HR) separately from a technical administrator (System Administrator).
- A final decision sign-off table is added at the end.
- This document's language has been revised to a neutral, concise register throughout.

This leaves **22 items** (17 original + 5 added in the prior architecture review, minus ISSUE-005).

---

## Decision Log (at a glance)

| ID | Question | Priority | Owner | Needed by |
|---|---|---|---|---|
| ISSUE-019 | What event satisfies First Response SLA — automated acknowledgement, or first human reply? | Critical | Management | Before MVP development |
| ISSUE-001 | When does the SLA clock start — creation or assignment? | Critical | Management | Before MVP development |
| ISSUE-021 | Is a customer self-service portal in scope, in any phase? | High | Management | Before MVP development |
| ISSUE-022 | Who may Resolve vs. Close a ticket, and who may Reopen/Cancel/Reject? | High | Management | Before MVP development |
| ISSUE-023 | What SLA policy applies to a priority upgrade, and to an approved downgrade? | High | Management | Before MVP development |
| ISSUE-004 | Does a Critical breach still notify the Department Head, or GM only? | High | Management | Before MVP development |
| ISSUE-006 | During a CRM outage, does every interaction get an Intake Record, with Critical/High proceeding immediately as provisional tickets? | High | IT | Before MVP development |
| ISSUE-007 | With no customer portal, disclosure limited to the CRM-verified requester or an authorized representative — confirm, plus the exception process. | High | CRM Team | Before MVP development |
| ISSUE-018 | SLA pause behavior, split into four parts: Critical (fixed: never pauses), Pending Customer, Pending Third-Party, and First Response after contact (fixed: cannot pause). | **High** *(raised from Low)* | Management | Before MVP development |
| ISSUE-008 | Confirm required ticket-state behavior (escalation independent of status, etc.); implementation model is IT's call. | Medium | Management (behavior); IT/Solution Architect (model) | Before MVP development |
| ISSUE-020 | Should the ticket-ID `[DEPT]` segment change on department transfer? | Medium | IT | Before MVP development |
| ISSUE-010 | Who approves cross-department transfers, and does the SLA clock reset? | Medium | Department Head | Before MVP development |
| ISSUE-011 | What is the allowed window to reopen a closed ticket? | Medium | Customer Service | Before MVP development |
| ISSUE-012 | Who owns the UAE public holiday calendar's content, and who administers it in the system? | Medium | Customer Service/HR (business); System Administrator (technical) | Before MVP development |
| ISSUE-013 | What configurable, priority-based time window governs Level 2→3 escalation, and what early-warning threshold precedes a breach? Proposed per-tier defaults are in the Executive Decisions document, for management to accept or change. *(absorbs former ISSUE-005)* | Medium | Management | Before MVP development |
| ISSUE-017 | Confirm the actual operating work week: Sat–Thu (Fri off), Mon–Fri (Sat–Sun off), or another configurable calendar. | Low | Management | Before MVP development |
| ISSUE-003 | Is "Geyness" the final vendor name, and what platform does it run on? | High | Geyness/Genesys | Before Phase 2 |
| ISSUE-002 | For auto-ticket channels, is the ticket number issued before or after CRM verification? | Critical (for Phase 2) | Management | Before Phase 2 |
| ISSUE-015 | Expected unit/tower count and concurrent-agent count for Phase 2? | Low | IT | Before Phase 2 |
| ISSUE-009 | Does a reopened-then-reclosed ticket trigger a second CSAT survey? | Medium | Customer Service | Before Phase 2 |
| ISSUE-016 | Which UAE regulation sets the retention period? | Low severity, **required before go-live** | Legal/Compliance | Before production go-live |
| ISSUE-014 | What counts as a "repeat contact" for the KPI? | Low | Customer Service | Phase 3 |

---

## Group A — Required Before MVP Development

These sixteen items shape the core ticket, permission, and SLA/escalation data model that ships in the first release.

### ISSUE-019 — First Response SLA event
**Decision required:** Does the SLA "first response" clock stop at the automated channel acknowledgement, or only at the first human-authored reply to the customer?

**Why this decision is needed:** The automated acknowledgement (ticket number, expected response time) is sent within seconds of every ticket being created. If that message counts as "first response," the 15-minute/1-hour/4-hour/24-hour first-response targets would be satisfied automatically every time, regardless of when a person actually engaged with the request.

**Options:**
- **A — The automated acknowledgement counts as first response.** *Pros:* Simplest rule; the target is always met. *Cons:* The metric no longer reflects actual response time, since it is satisfied identically on every ticket regardless of how quickly a person engaged.
- **B — Only the first human-authored response counts, defined per channel:** on inbound phone contact, the call-answer/accept timestamp (or, in the manual MVP, the moment an agent confirms live handling of the call) serves as the event; on digital channels, the event is the first substantive human-authored reply addressing the request. The automated acknowledgement never counts, on any channel. *Pros:* Measures the interval that actually matters to the customer's experience and to SLA-compliance reporting, with an unambiguous event for both a live call and an asynchronous message. *Cons:* Requires capturing a distinct "first human response" event per channel, separate from the automated send.

**Recommended option:** B.

**Impact if no decision is made:** The automated acknowledgement becomes the de facto answer by default, since it is the only response event the system currently guarantees.

**Priority:** Critical
**Decision owner:** Management

---

### ISSUE-001 — SLA clock start point
**Decision required:** Does the SLA clock start when a ticket is created, or when it is assigned to a named department owner?

**Why this decision is needed:** The field specification states the timer starts at creation; the workflow diagram shows it starting at assignment. Both measurements must be defined consistently for SLA-compliance reporting.

**Options:**
- **A — Start at ticket creation.** *Pros:* Matches the written field specification; simplest to implement. *Cons:* Attributes queue/assignment delay to the department, even when that delay is outside their control.
- **B — Start at owner assignment.** *Pros:* Matches the workflow diagram; measures working time only. *Cons:* Leaves the interval between creation and assignment unmeasured.
- **C — Start at creation, and separately track time-to-assignment as its own metric.** *Pros:* Satisfies the written specification while keeping the creation-to-assignment interval visible; compatible with either final answer without rework. *Cons:* Slightly more reporting complexity.

**Recommended option:** C.

**Impact if no decision is made:** The SLA engine cannot be finalized; whichever assumption is implemented carries a risk of later disagreement over SLA-compliance figures.

**Priority:** Critical
**Decision owner:** Management

---

### ISSUE-021 — Customer self-service portal scope
**Decision required:** Is an authenticated customer self-service portal — login, ticket-history view, self-service reopen — in scope for this system, in any phase?

**Why this decision is needed:** The source requirements describe only phone, digital form/chat, kiosk, and WhatsApp, each mediated by an agent or a simple auto-ticket submission — never an authenticated customer account. An earlier draft of this analysis implicitly assumed a portal existed. Building one adds a public-facing identity and data-exposure surface that has not been separately approved or scoped.

**Options:**
- **A — No customer portal, in any phase.** All customer interaction remains agent-mediated (phone) plus outbound notifications (email at MVP; SMS/WhatsApp from Phase 2). *Pros:* Matches the source requirements; avoids an unapproved external authentication surface. *Cons:* A customer checking ticket status must call in.
- **B — Approve a customer portal, scoped as its own initiative in a later phase.** *Pros:* Reduces repeat-contact call volume over time. *Cons:* A non-trivial addition — external authentication, per-customer data scoping (see ISSUE-007), and its own security review — that should not be assumed into an existing phase's estimate.

**Recommended option:** A, pending separate approval and scoping if a portal is later wanted.

**Impact if no decision is made:** Portal-like features tend to be added individually without being recognized as a cumulative scope and security decision.

**Priority:** High
**Decision owner:** Management

---

### ISSUE-022 — Resolve / Close / Reopen / Cancel / Reject authority
**Decision required:** Who is authorized to mark a ticket's work as done (Resolve), who finalizes it after confirming the customer has been told (Close), and who may Reopen, Cancel, or Reject a ticket?

**Why this decision is needed:** The closure criteria require both a completed resolution note and confirmed customer notification. The department performing the work typically has no visibility into whether the customer has actually been reached — that channel belongs to the CS/Geyness side.

**Options:**
- **A — The same role performs both Resolve and Close.** *Pros:* Fewer steps. *Cons:* The closing role would be certifying customer notification without a means to verify it.
- **B — Department Employee/Head Resolves; Geyness Agent/Supervisor/CS Manager Closes, after confirming notification.** *Pros:* Matches which role actually knows each fact required for closure. *Cons:* Adds one handoff to the lifecycle.

**Recommended option:** B.

**Impact if no decision is made:** The default in most implementations is Option A, which reduces "customer notified" to an unverified checkbox.

**Priority:** High
**Decision owner:** Management

---

### ISSUE-023 — Priority-change SLA policy
**Decision required:** What policy governs the SLA due dates, history, and breach record when a ticket's priority changes — separately for an **upgrade** to a higher priority and an **approved downgrade** to a lower priority? The policy must never erase elapsed time, an existing breach, or the original SLA history.

**Why this decision is needed:** An earlier draft referenced an undefined "proportional carry-forward" calculation, which describes a desired outcome without specifying a method. Separately, without a safeguard, a ticket at risk of breaching could be downgraded to remove the risk from view.

**Upgrade options:**
- **A — Full reset to the new tier's full target from the change moment.** *Pros:* Simple. *Cons:* Provides no guarantee that the new deadline is not later than the deadline already in effect.
- **B — The new due date is the earlier of the existing due date and the freshly computed higher-tier due date.** *Pros:* An upgrade can only tighten a deadline, never loosen it. *Cons:* Requires computing and comparing two candidate dates rather than one.

**Downgrade options:**
- **A — Takes effect immediately on request, with due dates recalculated right away.** *Pros:* Fast. *Cons:* No control against a downgrade being used to remove an at-risk or already-breached ticket from SLA-breach visibility.
- **B — Requires Department Head (or above) approval before taking effect; any breach already recorded under the prior tier remains on record.** *Pros:* Preserves an accurate compliance record. *Cons:* Adds an approval step.

**Recommended policy (combining both, plus retention/reporting):**
- Every previous SLA period, including any breach within it, is preserved permanently — never overwritten.
- A new operational SLA period begins at the moment of the priority change.
- Upgrade: due date = earlier of the existing due date and the newly computed higher-tier due date (Option B above).
- Downgrade: requires Department Head approval before taking effect (Option B above); a breach already recorded is not removed or reversed.
- Management reporting shows both the original and the changed SLA period for any ticket with a priority change.

**Impact if no decision is made:** The SLA engine's priority-change logic cannot be implemented without a defined method, and without the approval gate, SLA-compliance figures could be altered by re-prioritizing at-risk tickets.

**Priority:** High
**Decision owner:** Management

---

### ISSUE-004 — Critical breach notification routing
**Decision required:** When a Critical-priority ticket breaches its SLA, is the Department Head notified alongside the General Manager, or does the alert go to the GM only?

**Why this decision is needed:** One part of the requirements states a Critical breach means "immediate GM notification"; the general escalation model routes every breach through the Department Head first. As written, these conflict.

**Options:**
- **A — GM only, as literally stated.** *Pros:* Matches the specific wording. *Cons:* The Department Head, who is operationally responsible for the ticket, may not be informed.
- **B — Department Head and GM notified simultaneously.** *Pros:* Meets the "immediate GM" requirement while keeping the Department Head informed. *Cons:* One additional notification per Critical breach.

**Recommended option:** B.

**Note — notification is distinct from formal escalation:** Notifying the GM on a Critical (or High) breach is a visibility action; it does not by itself change the ticket's `EscalationLevel` to Level 3. Formal Level 3 escalation is governed separately by ISSUE-013's configured Level 2→GM window, which management may set to expire immediately for Critical tickets if an instant formal escalation is preferred over the proposed 30-minute default.

**Impact if no decision is made:** Notification routing is implemented on an assumption that may leave either the GM or the Department Head uninformed.

**Priority:** High
**Decision owner:** Management

---

### ISSUE-006 — CRM outage fallback for ticket creation *(revised)*
**Decision required:** During a CRM outage, how is a customer interaction handled — should every interaction still create a record, and should any priority level be allowed to proceed immediately without a live, verified unit match?

**Why this decision is needed:** The requirements state CRM downtime must be escalated within 15 minutes, but do not address what happens to new contacts arriving during the outage. MVP's entire intake model depends on a real-time CRM lookup during the call, and no interaction may be silently lost simply because CRM is unavailable.

**Options:**
- **A — No ticket creation during CRM downtime; contacts are logged manually and entered once CRM is restored.** *Pros:* No change to core ticket-creation logic. *Cons:* A safety-related contact during the outage may not be logged in the system meant to track it, and depends entirely on manual diligence to avoid being lost.
- **B — Every interaction creates an Intake Record regardless of CRM status. Critical/High proceed immediately as provisional tickets (unverified unit reference), reconciled once CRM returns. Medium/Low remain queued in the Intake Record for CRM verification once restored, rather than becoming a ticket immediately.** *Pros:* Safety-critical issues are not blocked by an outage, and no interaction — at any priority — is silently lost, since even a queued Medium/Low contact has an Intake Record from the moment it occurred. *Cons:* Requires logic to support the Intake Record, the provisional-ticket path, and later reconciliation for both paths.

**Recommended option:** B.

**Impact if no decision is made:** The CRM integration's failure-handling behavior is undefined for the MVP's primary intake path.

**Priority:** High
**Decision owner:** IT

---

### ISSUE-007 — Multi-party unit contact authorization *(rewritten — no customer portal assumed)*
**Decision required:** With no customer self-service portal (ISSUE-021), and for a unit with multiple linked contacts (joint owners, current/former tenants, authorized representatives):
- Which linked contact is authorized to receive ticket details over the phone/email/SMS?
- Who receives outbound notifications (acknowledgement, status updates, resolution) for a given ticket?
- May a tenant receive an owner's ticket history, or an owner a tenant's?
- How are joint owners and authorized representatives verified before information is shared with them?

**Why this decision is needed:** The requirements acknowledge a risk of data mixing between owners and tenants of the same unit but do not specify a disclosure rule. Because every customer interaction is agent-mediated (phone) or an outbound message — not a screen a customer logs into — the practical question is what an agent may say to a given caller, not a screen-level access control. Portal-based visibility, if ever approved, is tracked separately under ISSUE-021 and is out of scope here.

**Options:**
- **A — Whole-unit disclosure:** any verified contact linked to the unit may be told about, and notified of, any ticket for that unit. *Pros:* One simple rule for agents. *Cons:* A contact not involved in a specific matter could be told about it — including disclosure between an owner and a tenant who share only a landlord-tenant relationship, not a household one.
- **B — Contact-level disclosure:** only the contact who raised (or is directly named on) a ticket is told its details or receives its notifications. Tenant and owner histories are not disclosed to each other by default. A caller not personally listed on the unit record must have a CRM-recorded authorization on file before anything is disclosed to them; a verbal claim of authority is not sufficient. *Pros:* Limits disclosure to the party with a direct interest in the specific ticket; a documented authorization step for representatives. *Cons:* A joint owner not personally named on a ticket would need the co-owner to inform them directly, unless an exception is set up.

**Recommended option:** B, with an explicit exception process available for joint owners who request shared visibility of each other's tickets.

**Impact if no decision is made:** Agents will apply individual judgment about what to disclose to whom, resulting in inconsistent handling of information between parties linked to the same unit.

**Priority:** High
**Decision owner:** CRM Team

---

### ISSUE-018 — SLA pause behavior, split by SLA type and pending reason *(priority raised: Low → High; split into four sub-decisions)*
**Decision required:** SLA pause behavior does not reduce to one blanket question — it is four separate decisions:
- **(a) Critical SLA:** Does it ever pause? *Fixed rule, not an open choice:* the Critical SLA never pauses — it runs 24/7 regardless of ticket status, consistent with Section 7.1's 24/7 clock basis for Critical.
- **(b) Non-Critical Resolution SLA — Pending Customer:** Does the clock pause while waiting on the customer?
- **(c) Non-Critical Resolution SLA — Pending Third-Party:** Does the clock pause while waiting on an external party (contractor, DEWA, etc.)? Decided separately from (b), since management may reasonably want different treatment for a customer-caused delay versus a third-party-caused one.
- **(d) First Response SLA:** Can it be paused after customer contact has already been received? *Fixed rule, not an open choice:* no — once contact has been received, the First Response event has either already occurred or the metric is no longer meaningful to pause.

**Why this decision is needed:** SLA pause behavior directly determines what the contractual SLA-compliance percentage measures. Without pausing, a department's compliance figure includes time it had no ability to act on; the requirements do not state which behavior applies, and treating "SLA pause" as a single undifferentiated question obscures that Critical, First Response, and the two Pending reasons are genuinely different cases.

**Options (for (b) and (c) only — (a) and (d) are fixed rules for confirmation):**
- **A — Clock keeps running regardless.** *Pros:* Simple. *Cons:* Attributes customer- or third-party-caused delay to the department's compliance figure.
- **B — Clock pauses, resumes when work restarts.** *Pros:* Attributes delay to its actual cause. *Cons:* Requires disciplined use of Pending statuses; a status left Pending incorrectly would pause a clock that should be running.

**Recommended option:** B for both (b) and (c), with monitoring for tickets left in a Pending status for an unusual duration.

**Impact if no decision is made:** SLA-compliance reporting either includes delay outside the department's control, or is effectively paused without that behavior having been decided deliberately — and without treating Pending Customer and Pending Third-Party as potentially distinct policies, one may be set incorrectly to match the other by default.

**Priority:** High
**Decision owner:** Management

---

### ISSUE-008 — Confirm required ticket-state behavior *(decision split — see note)*
**Decision required, split into two parts:**
- **Management approves** the required behavior and reporting outcomes: a ticket must be capable of being escalated while still actively being worked, and verification status, escalation level, SLA state, and resolution outcome must each be reportable independently of one another.
- **IT / Solution Architect decides** the implementation used to satisfy that required behavior — specifically, whether it is modeled as five independent dimensions (`TicketStatus` / `VerificationStatus` / `EscalationLevel` / `SlaState` / `ResolutionOutcome`) or some other internal representation. This half is an architecture decision, not a management decision point.

**Why this decision is needed:** A single combined status field cannot represent a ticket that is both "escalated" and "still being worked" without losing one of those two facts. Management's role is to confirm this behavior is actually required; the specific field design that satisfies it is an implementation choice.

**Options (for the management-approved behavior only):**
- **A — No: track a single combined status only.** *Pros:* Simplest to describe. *Cons:* Cannot represent combinations that occur in practice (escalated while in progress); an event (Reopen) and an outcome (Duplicate) would be represented as if they were workflow stages.
- **B — Yes: escalation, verification, SLA state, and resolution outcome must all be independently reportable.** *Pros:* Represents real combinations correctly; each dimension has its own transition rules and audit trail. *Cons:* Slightly more to document up front, though the user-facing view can still present a single summary.

**Recommended option:** B for the required behavior; the five-dimension model is IT/Solution Architect's recommended way to satisfy it, subject to their own design review.

**Impact if no decision is made:** Development proceeds on the single-field model, which would need to be reworked once an escalated-while-in-progress case is encountered.

**Priority:** Medium
**Decision owner:** Management (required behavior) — IT / Solution Architect (implementation model)

---

### ISSUE-020 — Ticket-ID behavior on department transfer
**Decision required:** When a ticket transfers between departments, does the ticket ID's `[DEPT]` segment change to reflect the new department, or does the ID remain exactly as issued?

**Why this decision is needed:** Ticket numbers are read to customers and referenced in prior communications and in the audit trail for the ticket's full lifecycle. A change to the ID after issuance would make it inconsistent with anything already sent referencing the original number.

**Options:**
- **A — `[DEPT]` updates to the current owning department.** *Pros:* The ID reflects current ownership at a glance. *Cons:* Breaks the expectation that an issued reference number remains fixed; any prior communication referencing the original ID becomes inconsistent with the system's current record.
- **B — The ticket ID is immutable; `[DEPT]` always reflects the originating department. Current ownership is tracked as a separate, mutable field.** *Pros:* The customer-facing reference number never changes; audit history remains unambiguous. *Cons:* The ID alone does not show current ownership — a separate field must be checked.

**Recommended option:** B.

**Impact if no decision is made:** An implementation based on the intuitive-sounding Option A would break every previously issued reference number on the first department transfer.

**Priority:** Medium
**Decision owner:** IT

---

### ISSUE-010 — Department transfer authority and SLA impact
**Decision required:** Who is authorized to approve moving a ticket from one department to another, and does the SLA clock reset when that happens?

**Why this decision is needed:** No transfer rule exists in the requirements. Without one, transfers either cannot occur through the system, or occur without approval control and with a route to reset SLA compliance by repeated transfer.

**Options:**
- **A — Any Department Employee transfers freely; SLA clock resets.** *Pros:* Fast. *Cons:* A ticket approaching breach could be transferred specifically to reset its clock.
- **B — Transfer requires Department Head approval; SLA clock continues without resetting.** *Pros:* A single accountable approval point; no SLA-clock reset incentive. *Cons:* Slower than free transfer.

**Recommended option:** B.

**Impact if no decision is made:** Transfers are either blocked entirely or implemented without a safeguard against SLA-clock manipulation.

**Priority:** Medium
**Decision owner:** Department Head

---

### ISSUE-011 — Reopen window
**Decision required:** How long after closure can a ticket be reopened for the same issue before a new ticket must be raised instead?

**Why this decision is needed:** No reopening policy exists in the requirements, despite reopening being a normal part of ticketing systems generally.

**Options:**
- **A — No formal window; case by case.** *Pros:* Flexible. *Cons:* Inconsistent handling; unreliable CSAT/resolution-time reporting.
- **B — Fixed window (e.g., 7 days), after which a new, linked ticket is created instead.** *Pros:* Consistent and predictable; preserves clean reporting. *Cons:* A case just outside the window creates a new ticket rather than reopening.

**Recommended option:** B, 7 days, configurable.

**Impact if no decision is made:** Reopen handling is inconsistent, and resolution-time/CSAT metrics become unreliable.

**Priority:** Medium
**Decision owner:** Customer Service

---

### ISSUE-012 — UAE public holiday calendar ownership *(ownership revised)*
**Decision required:** Who decides which dates are on the UAE public holiday calendar the SLA engine uses (business ownership), and who enters and maintains those dates in the system (technical administration)?

**Why this decision is needed:** Non-Critical SLA calculations exclude non-business days. UAE public holidays shift yearly and are confirmed close to the date; without a named business owner and a named technical administrator, the calendar will go stale and SLA calculations will drift.

**Options:**
- **A — A single role does both: decides the dates and enters them.** *Pros:* One point of contact. *Cons:* Combines a business judgment (which dates apply to Tiger's operations) with a technical task (maintaining reference data) in a way that does not match how most organizations split this responsibility.
- **B — Business owner (Customer Service or HR) confirms the dates each year; technical administrator (System Administrator) enters them into the configurable reference table.** *Pros:* Matches the natural split between business knowledge and system administration. *Cons:* Requires a defined handoff between the two roles each year.

**Recommended option:** B — business owner: Customer Service or HR; technical administrator: System Administrator.

**Impact if no decision is made:** SLA-compliance figures around any unaccounted holiday will be incorrect, and responsibility for catching this will be unclear.

**Priority:** Medium
**Decision owner:** Business — Customer Service or HR; Technical — System Administrator

---

### ISSUE-013 — Escalation progression window and SLA warning threshold *(expanded — absorbs former ISSUE-005)*
**Decision required:** What configurable, time-based and priority-based window governs how long a Level 2 (Department Head) escalation may remain unresolved before it automatically advances to Level 3 (General Manager)? What early-warning threshold should precede an SLA breach?

**Why this decision is needed:** The requirements do not define the Level 2→3 window's length or any pre-breach warning threshold. An earlier draft proposed capping the number of "re-assign and retry" cycles as the trigger for automatic level-up; that approach has been replaced, since a fixed retry count does not reflect how urgent a ticket actually is — escalation progression should depend on elapsed time relative to the ticket's priority, not on how many times it was reassigned.

**Options:**
- **A — No proactive warning; alert only at breach; no defined window for automatic level-up.** *Pros:* Simplest. *Cons:* No opportunity to act before a breach; no guarantee an unresolved escalation ever reaches Level 3.
- **B — Warning at a percentage of the resolution target elapsed (e.g., 75%); a configurable Level 2→3 window set per priority tier.** *Pros:* Reflects each tier's actual urgency; gives staff a chance to act before a breach; guarantees automatic advancement without depending on a retry count. *Cons:* Requires setting and periodically reviewing a window value for each priority tier.

**Recommended option:** B, with proposed starting defaults for each priority tier (early-warning threshold and Level 2→GM window) provided as a fill-in table in the companion Executive Decisions document, for management to accept or change value-by-value.

**Note — GM notification is distinct from formal Level 3 escalation:** An immediate GM notification on a Critical or High breach (ISSUE-004) is a visibility action and does not by itself set `EscalationLevel = Level3`. The formal transition to Level 3 occurs only when this issue's configured Level 2→GM window expires without resolution. For Critical tickets specifically, management may approve an immediate Level 3 transition (i.e., a window of zero) instead of the proposed 30-minute default, if notification and formal escalation should be simultaneous for that tier.

**Impact if no decision is made:** The escalation engine cannot be finalized, and a ticket could remain at Level 2 indefinitely regardless of its priority.

**Priority:** Medium
**Decision owner:** Management

---

### ISSUE-017 — Confirm the actual operating business week *(options clarified — "Sat–Sun" alone is ambiguous)*
**Decision required:** Confirm the operating week for SLA business-hours purposes. Stating this as "Sat–Thu" versus "Sat–Sun" alone is ambiguous about which days are actually worked, so the options below spell out the full working-day range for each.

**Why this decision is needed:** If the stated week is a drafting error, every business-hours SLA calculation would be incorrect by one or two working days per week.

**Options:**
- **A — Working days Saturday–Thursday; Friday off.** As currently stated in the requirements. *Pros:* No change needed, if correct. *Cons:* None, if correct.
- **B — Working days Monday–Friday; Saturday–Sunday off.** Aligns with the UAE federal government's convention since 2022. *Pros:* Matches current common practice. *Cons:* Changes every SLA calculation and reporting cadence built around the originally stated week.
- **C — Another configurable company calendar**, if Tiger Group/Geyness's actual working days differ from both A and B (e.g., a hybrid arrangement).

**Recommended option:** Confirm the correct week before building the calendar logic; the work week will be stored as configurable data regardless (supporting A, B, or C without a code change), so this decision only needs to select the correct starting configuration.

**Impact if no decision is made:** Development proceeds on configurable data either way, but any SLA figure reported before confirmation carries a risk of being off by a working day.

**Priority:** Low
**Decision owner:** Management

---

## Group B — Required Before Phase 2

Four items that concern features not present in MVP (auto-ticket channels, the Geyness/Genesys platform integration, and CSAT). They do not block the start of MVP development.

### ISSUE-003 — Geyness vs. Genesys vendor/platform identity
**Decision required:** Confirm "Geyness" is the correct, final name of the contracted call-center vendor, and confirm whether its platform is Genesys or another named platform requiring direct integration, or whether Geyness handles telephony internally and only hands off ticket data.

**Why this decision is needed:** The requirements document and its workflow diagram consistently name "Geyness." A separate reference to "Genesys" surfaced when this analysis was commissioned. This integration (INT-02) is Phase 2 scope.

**Options:**
- **A — Treat "Geyness" as the vendor; design a generic hand-off integration independent of its internal platform.** *Pros:* Works regardless of the internal platform choice. *Cons:* May need revisiting if deeper telephony-level integration with a specific platform is later wanted.
- **B — Assume Genesys is the underlying platform and design against its APIs.** *Pros:* Potentially richer integration, if accurate. *Cons:* Wasted effort if the assumption is incorrect.

**Recommended option:** A, until vendor confirmation is received in writing, and no later than the start of Phase 2 design.

**Impact if no decision is made:** Phase 2's call-center integration cannot be scoped accurately.

**Priority:** High
**Decision owner:** Geyness/Genesys

---

### ISSUE-002 — Ticket creation before unit verification (auto-ticket channels)
**Decision required:** For auto-ticket channels (App/Website, WhatsApp — Phase 2), should the customer receive a ticket number immediately, or only after CRM verification completes?

**Why this decision is needed:** The stated rule requires a verified unit number before ticket creation; the channel table marks these channels as auto-ticketing on submission, before verification. This does not affect MVP, which has no auto-ticket channel.

**Options:**
- **A — Verify first, ticket number issued after.** *Pros:* Fully honors the verification rule. *Cons:* Slower for the customer on digital channels.
- **B — Issue a ticket number immediately; verify in the background.** *Pros:* Better customer experience. *Cons:* Unverified records could reach departments if background verification fails silently.
- **C — Issue a provisional reference immediately; convert to a full ticket once verified, with automatic escalation if verification is not completed within a set time.** *Pros:* Preserves customer experience while keeping unverified submissions visible and time-bounded. *Cons:* Requires a provisional state with its own handling rules.

**Recommended option:** C.

**Impact if no decision is made:** Phase 2's auto-ticket channels cannot be designed.

**Priority:** Critical for Phase 2 (not a blocker for MVP)
**Decision owner:** Management

---

### ISSUE-015 — Expected system scale
**Decision required:** Approximately how many units/towers and concurrent agents should the system be sized for at Phase 2 launch, and at a three-year horizon?

**Why this decision is needed:** No volume figures exist in the requirements. MVP's phone-only load is modest; Phase 2's multi-channel, CRM-API-heavy load is not.

**Options:**
- **A — Proceed on a conservative, horizontally scalable default; revisit before Phase 2.** *Pros:* Does not block MVP or early Phase 2 design. *Cons:* Risk of under/over-provisioning once customer-facing volume appears.
- **B — Provide a volume estimate now.** *Pros:* More accurate capacity planning and vendor conversations (e.g., CRM API rate limits) ahead of Phase 2. *Cons:* Requires an estimate that may itself be approximate.

**Recommended option:** B, with A as the fallback if no figure is available in time.

**Impact if no decision is made:** Phase 2 sizing proceeds on generic defaults, with risk of hitting a CRM API rate limit or under-provisioned hosting discovered only after go-live.

**Priority:** Low
**Decision owner:** IT

---

### ISSUE-009 — CSAT resend on reopened tickets
**Decision required:** Should a ticket that is reopened and later re-closed trigger a second CSAT survey?

**Why this decision is needed:** CSAT is Phase 2 scope. No rule addresses this in the source; resending risks survey fatigue and double-counting if not tagged separately from the first response.

**Options:**
- **A — Always resend on every closure, including after a reopen.** *Pros:* Simple, consistent. *Cons:* Risk of fatigue; must avoid blending both responses into one trend line.
- **B — Never resend after a reopen.** *Pros:* Avoids fatigue and double-counting. *Cons:* Loses feedback on the reopened issue's resolution.

**Recommended option:** A, with the survey tagged "post-reopen" in reporting.

**Impact if no decision is made:** A default must be chosen to ship CSAT at all; should be confirmed before CSAT trend reporting is used for performance review.

**Priority:** Medium
**Decision owner:** Customer Service

---

## Group C — Required Before Production Go-Live

### ISSUE-016 — Applicable UAE data retention regulation *(owner and timing revised)*
**Decision required:** Which specific UAE regulation sets the retention period, and does it apply uniformly to tickets, attachments, CSAT responses, and audit logs, or differently by record type?

**Why this decision is needed:** The requirements state a 7-year retention period "in line with UAE regulatory requirements" without citing the specific law. This decision is required **before the MVP goes into production** — retained records begin accumulating from the first day of live use, so it is not a decision that can be left until after launch.

**Options:**
- **A — Apply 7 years uniformly as an interim configuration while Legal/Compliance confirms the exact regulation, completed before go-live.** *Pros:* Development is not blocked while the citation is pending. *Cons:* The configuration may need adjustment once confirmed, before go-live.
- **B — Confirm the specific regulation and per-record-type periods with Legal/Compliance first, then configure retention accordingly, before go-live.** *Pros:* Retention is configured correctly the first time. *Cons:* Requires the Legal review to complete before go-live rather than in parallel.

**Recommended option:** A as the working configuration during development, with confirmation from Legal/Compliance completed and, if needed, applied **before production go-live** — this step must not be scheduled for after launch.

**Impact if no decision is made:** A compliance gap could exist from the first day of live use and remain undetected until an audit.

**Priority:** Low severity, but required before go-live
**Decision owner:** Legal/Compliance

---

## Group D — Can Be Deferred Until Phase 3

### ISSUE-014 — "Repeat contact" definition for the KPI dashboard
**Decision required:** What should count as a customer "contacting again for the same issue" for the Repeat Contact Rate KPI? This metric is Phase 3 (Advanced KPI) scope.

**Why this decision is needed:** The KPI has numeric targets but no definition of what makes two contacts "the same issue."

**Options:**
- **A — Ship as "provisional" using a working definition (e.g., same unit and category within 7 days), clearly labeled, refined once confirmed.** *Pros:* Lets the Phase 3 dashboard go live on schedule. *Cons:* Not fully reliable for performance decisions until refined.
- **B — Hold the KPI off the dashboard entirely until a definition is confirmed.** *Pros:* Avoids presenting an unreliable number. *Cons:* An incomplete dashboard relative to the full specification.

**Recommended option:** A.

**Impact if no decision is made:** The figure may not mean what it is assumed to mean, risking a misread of service-quality trends.

**Priority:** Low
**Decision owner:** Customer Service

---

## Final Decision Sign-Off

To be completed by the named decision owner (or delegate) for each item. "Approved option/decision" should record the option letter selected, or the specific policy agreed if different from the options presented above.

| Issue ID | Approved option/decision | Approved by | Department | Approval date | Comments |
|---|---|---|---|---|---|
| ISSUE-019 | | | | | |
| ISSUE-001 | | | | | |
| ISSUE-021 | | | | | |
| ISSUE-022 | | | | | |
| ISSUE-023 | | | | | |
| ISSUE-004 | | | | | |
| ISSUE-006 | | | | | |
| ISSUE-007 | | | | | |
| ISSUE-018 | | | | | |
| ISSUE-008 | | | | | |
| ISSUE-020 | | | | | |
| ISSUE-010 | | | | | |
| ISSUE-011 | | | | | |
| ISSUE-012 | | | | | |
| ISSUE-013 | | | | | |
| ISSUE-017 | | | | | |
| ISSUE-003 | | | | | |
| ISSUE-002 | | | | | |
| ISSUE-015 | | | | | |
| ISSUE-009 | | | | | |
| ISSUE-016 | | | | | |
| ISSUE-014 | | | | | |

---

## Summary for Sign-Off

| Group | Item count | Gate |
|---|---|---|
| A — Required before MVP development | 16 | Core ticket/permission/SLA/escalation build cannot start with confidence until these are answered |
| B — Required before Phase 2 | 4 | Auto-ticket channel design, the CRM/Geyness vendor integration, and CSAT policy cannot be scoped accurately until these are answered |
| C — Required before production go-live | 1 | Retention regulation must be confirmed with Legal/Compliance before the MVP is deployed to production |
| D — Can be deferred until Phase 3 | 1 | Advanced KPI refinement |

**Requested action:** Decision (or delegation) on each Group A item before Phase 2/Phase 4 of the implementation plan begin. Group B items should be resolved before Phase 2-release design starts. Group C's single item must be resolved before production go-live. Group D may be resolved any time before the affected feature is built.
