# Tiger Group — CS Ticketing System
## Executive Decisions — MVP

| | |
|---|---|
| **Purpose** | Decisions required before MVP development can proceed with confidence |
| **Audience** | Management sign-off meeting (30–45 minutes) |
| **Scope** | **16 MVP-development issues represented by 20 decision rows, plus 1 production go-live issue.** Phase 2 and Phase 3 decisions exist but do not block MVP — see the one-line appendix. |
| **Detail** | Full rationale, trade-offs, and worked examples for every item below are in the companion Technical Decision Register: `docs/Tiger-CS-Ticketing-Management-Decisions.md` |
| **Date** | 2026-08-17 |

**How to use this in the meeting:** For each row, confirm the recommended option (marked ✓) or select an alternative. Write **A**, **B**, **C**, or **Modified** (with a short note) in the decision column — not a simple checkbox, since some rows have three options and some decisions may be approved with a change. Where a row is marked "fixed rule," it is a stated constraint for confirmation, not an open choice. The go-live gate (ISSUE-016) is kept in its own section at the end because it does not block starting MVP development — it blocks deploying MVP to production, and it is owned by Legal/Compliance rather than by this meeting's usual approvers.

---

## Decisions Requiring Approval — MVP Development

| # | Issue | Decision | Option A | Option B | Option C | Recommended | Decision (A/B/C/Modified) |
|---|---|---|---|---|---|---|---|
| 1 | ISSUE-019 | What event counts as "First Human Response" for SLA? | Automated acknowledgement counts | Phone: call answer/accept timestamp, or agent-confirmed live handling in manual MVP. Digital channels: first substantive human-authored reply. Automated acknowledgement never counts, on any channel. | — | **✓ B** | |
| 2 | ISSUE-001 | When does the SLA clock start? | At ticket creation | At owner assignment | At creation, track assignment lag separately | **✓ C** | |
| 3 | ISSUE-021 | Is a customer self-service portal in scope? | No portal, any phase | Approve for a later phase | — | **✓ A** | |
| 4 | ISSUE-022 | Who Resolves vs. Closes a ticket? | Same role does both | Dept. resolves; CS closes after notifying customer | — | **✓ B** | |
| 5 | ISSUE-023 (upgrade) | SLA effect of a priority **upgrade**? | Full reset to new tier | Earlier of old/new due date | — | **✓ B** | |
| 6 | ISSUE-023 (downgrade) | SLA effect of an approved priority **downgrade**? | Takes effect immediately | Requires Dept. Head approval; prior breach stays on record | — | **✓ B** | |
| 7 | ISSUE-004 | Who is notified on a Critical breach? | GM only | Dept. Head + GM together | — | **✓ B** | |
| 8 | ISSUE-006 | How is a customer interaction handled during a CRM outage? | Block ticket creation entirely until CRM restored | Every interaction creates an Intake Record regardless of CRM status. Critical/High proceed immediately as provisional tickets. Medium/Low remain queued for CRM verification once restored. No interaction is silently lost. | — | **✓ B** | |
| 9 | ISSUE-007 | Who may be told ticket details on a multi-contact unit (no portal)? | Any linked contact told anything about the unit | Disclosure only to the CRM-verified ticket requester or an explicitly authorized representative. No owner/tenant/joint-owner cross-sharing by default. Exceptions require a defined verification/authorization process. | — | **✓ B** | |
| 10 | ISSUE-018a | Does the **Critical** SLA clock ever pause? | *Fixed rule:* never pauses — runs 24/7 regardless of status | — | — | *Confirm only* | |
| 11 | ISSUE-018b | Does the non-Critical **Resolution** SLA pause during Pending Customer? | Clock keeps running | Clock pauses, resumes when work restarts | — | **✓ B** | |
| 12 | ISSUE-018c | Does the non-Critical **Resolution** SLA pause during Pending Third-Party? | Clock keeps running | Clock pauses, resumes when work restarts | — | **✓ B** | |
| 13 | ISSUE-018d | Can the **First Response** SLA be paused after customer contact has already been received? | *Fixed rule:* no — once contact is received, there is nothing left to pause for this metric | — | — | *Confirm only* | |
| 14 | ISSUE-008 *(see note)* | Confirm the required behavior: must a ticket be escalatable while still actively being worked, and must verification, escalation, SLA state, and resolution outcome be reportable independently of each other? | No — track a single combined status only | Yes — all of the above must be true and independently reportable | — | **✓ B** | |
| 15 | ISSUE-020 | Does the ticket ID change on department transfer? | ID's department code updates | ID is permanent; current owner tracked separately | — | **✓ B** | |
| 16 | ISSUE-010 | Who approves a department transfer? | Any employee; SLA resets | Dept. Head approval; SLA continues | — | **✓ B** | |
| 17 | ISSUE-011 | How long can a closed ticket be reopened? | No fixed window | Fixed 7-day window | — | **✓ B** | |
| 18 | ISSUE-012 | Who owns the holiday calendar? | One role decides and enters dates | CS/HR decide dates; System Admin enters them | — | **✓ B** | |
| 19 | ISSUE-013 | What triggers automatic escalation from Dept. Head (Level 2) to GM (Level 3), and what early-warning threshold precedes a breach? | No defined window; alert only at breach | Configurable early-warning threshold and Level 2→GM window, set per priority tier (see table below) | — | **✓ B** | |
| 20 | ISSUE-017 | Confirm the operating work week for SLA business-hours calculation. | Working days Saturday–Thursday; Friday off | Working days Monday–Friday; Saturday–Sunday off | Another configurable company calendar | *Confirm which is correct* | |

**Note on row 14 (ISSUE-008):** Management approves the required behavior and reporting outcomes above. The specific implementation — five independent tracking fields (`TicketStatus` / `VerificationStatus` / `EscalationLevel` / `SlaState` / `ResolutionOutcome`) — is an architecture decision owned by **IT / Solution Architect**, not a management decision point. This row exists so management confirms *what the system must be able to do*, not *how it is built*.

### ISSUE-013 — Escalation Window Defaults (fill in or accept as proposed)

Proposed defaults below; management may change any value before approval.

| Priority | Early-warning threshold | Level 2 → GM window |
|---|---|---|
| Critical | 50% of resolution target elapsed | 30 minutes |
| High | 75% of resolution target elapsed | 2 hours |
| Medium | 75% of resolution target elapsed | 1 business day |
| Low | 75% of resolution target elapsed | 2 business days |

**Clarification:** Immediate GM notification on a Critical or High breach does not by itself change the ticket's EscalationLevel to Level 3. Notification provides visibility; formal Level 3 escalation occurs only when the configured Level 2-to-GM window expires without resolution. For Critical tickets, management may approve an immediate Level 3 transition instead of the proposed 30-minute window.

---

## Production Go-Live Gate — Separate From MVP Development

This item does not block starting MVP development. It blocks **deploying MVP to production**, and is owned by **Legal/Compliance**, not by the roles approving the table above.

| # | Issue | Decision | Option A | Option B | Recommended | Decision (A/B/C/Modified) |
|---|---|---|---|---|---|---|
| 21 | ISSUE-016 | Data retention regulation — must be confirmed before go-live, not after. | Apply 7 years as an interim configuration now; Legal confirms the exact regulation before go-live | Confirm the exact regulation with Legal first, before proceeding | **✓ A now, completed before go-live** | |

---

## Approval

| Role | Name | Signature | Date |
|---|---|---|---|
| Tiger Group CS Manager | | | |
| Tiger Group General Manager | | | |
| IT / Solution Architect | | | |
| Legal/Compliance (row 21 only) | | | |

**Overall meeting outcome:** ☐ All items approved as recommended  ☐ Approved with noted exceptions (see table)  ☐ Follow-up required on: _______________

---

## Appendix — Phase 2 and Phase 3 Decisions (Not MVP-Blocking)

These exist and will need answers before their respective phase, but do not require this meeting's time. Full detail is in the Technical Decision Register.

**Before Phase 2:** ISSUE-003 (Geyness vs. Genesys vendor/platform identity) · ISSUE-002 (ticket-number timing for auto-ticket channels) · ISSUE-015 (expected system scale) · ISSUE-009 (CSAT resend on reopen)

**Can defer to Phase 3:** ISSUE-014 (Repeat Contact Rate definition)
