# Tiger Group — CS Ticketing System
## Executive Decisions — MVP

| | |
|---|---|
| **Purpose** | Decisions required before MVP development can proceed with confidence |
| **Audience** | Management sign-off meeting (30–45 minutes) |
| **Scope** | This document contains **only MVP-blocking decisions** (17 items). Phase 2 and Phase 3 decisions exist but do not block MVP — see the one-line appendix. |
| **Detail** | Full rationale, trade-offs, and worked examples for every item below are in the companion Technical Decision Register: `docs/Tiger-CS-Ticketing-Management-Decisions.md` |
| **Date** | 2026-08-17 |

**How to use this in the meeting:** For each row, confirm the recommended option (marked ✓) or select an alternative. Where no clear recommendation is possible (ISSUE-017), the ask is simply to confirm the correct fact. Record the decision in the "Approved" column or on the sign-off page.

---

## Decisions Requiring Approval

| # | Issue | Decision | Option A | Option B | Option C | Recommended | Approved |
|---|---|---|---|---|---|---|---|
| 1 | ISSUE-019 | What counts as "first response" for SLA? | Automated acknowledgement counts | First human reply only | — | **✓ B** | ☐ |
| 2 | ISSUE-001 | When does the SLA clock start? | At ticket creation | At owner assignment | At creation, track assignment lag separately | **✓ C** | ☐ |
| 3 | ISSUE-021 | Is a customer self-service portal in scope? | No portal, any phase | Approve for a later phase | — | **✓ A** | ☐ |
| 4 | ISSUE-022 | Who Resolves vs. Closes a ticket? | Same role does both | Dept. resolves; CS closes after notifying customer | — | **✓ B** | ☐ |
| 5 | ISSUE-023 (upgrade) | SLA effect of a priority **upgrade**? | Full reset to new tier | Earlier of old/new due date | — | **✓ B** | ☐ |
| 6 | ISSUE-023 (downgrade) | SLA effect of an approved priority **downgrade**? | Takes effect immediately | Requires Dept. Head approval; prior breach stays on record | — | **✓ B** | ☐ |
| 7 | ISSUE-004 | Who is notified on a Critical breach? | GM only | Dept. Head + GM together | — | **✓ B** | ☐ |
| 8 | ISSUE-006 | Ticket creation during a CRM outage? | Blocked until CRM restored | Provisional ticket for Critical/High, reconciled later | — | **✓ B** | ☐ |
| 9 | ISSUE-007 | Who is told ticket details on a multi-contact unit (no portal)? | Any linked contact told anything | Only the contact named on that ticket; no tenant/owner cross-sharing | — | **✓ B** | ☐ |
| 10 | ISSUE-018 | Does the SLA clock pause while waiting on customer/third party? | Clock keeps running | Clock pauses, resumes on restart | — | **✓ B** | ☐ |
| 11 | ISSUE-008 | How is ticket state modeled? | One combined status field | Five independent tracking fields (status/verification/escalation/SLA/outcome) | — | **✓ B** | ☐ |
| 12 | ISSUE-020 | Does the ticket ID change on department transfer? | ID's department code updates | ID is permanent; current owner tracked separately | — | **✓ B** | ☐ |
| 13 | ISSUE-010 | Who approves a department transfer? | Any employee; SLA resets | Dept. Head approval; SLA continues | — | **✓ B** | ☐ |
| 14 | ISSUE-011 | How long can a closed ticket be reopened? | No fixed window | Fixed 7-day window | — | **✓ B** | ☐ |
| 15 | ISSUE-012 | Who owns the holiday calendar? | One role decides and enters dates | CS/HR decide dates; System Admin enters them | — | **✓ B** | ☐ |
| 16 | ISSUE-013 | What triggers automatic escalation to the GM? | No defined window | Configurable time window per priority tier + early warning | — | **✓ B** | ☐ |
| 17 | ISSUE-017 | Confirm the operating week. | Sat–Thu (as documented) | Sat–Sun | — | *Confirm which is correct* | ☐ |
| 18 | ISSUE-016 | Data retention regulation — required before go-live. | Apply 7 yrs as interim; Legal confirms exact law before go-live | Confirm exact law with Legal first, before proceeding | — | **✓ A now, completed before go-live** | ☐ |

---

## Approval

| Role | Name | Signature | Date |
|---|---|---|---|
| Tiger Group CS Manager | | | |
| Tiger Group General Manager | | | |
| IT / Solution Architect | | | |
| Legal/Compliance (item 18 only) | | | |

**Overall meeting outcome:** ☐ All items approved as recommended  ☐ Approved with noted exceptions (see table)  ☐ Follow-up required on: _______________

---

## Appendix — Phase 2 and Phase 3 Decisions (Not MVP-Blocking)

These exist and will need answers before their respective phase, but do not require this meeting's time. Full detail is in the Technical Decision Register.

**Before Phase 2:** ISSUE-003 (Geyness vs. Genesys vendor/platform identity) · ISSUE-002 (ticket-number timing for auto-ticket channels) · ISSUE-015 (expected system scale) · ISSUE-009 (CSAT resend on reopen)

**Can defer to Phase 3:** ISSUE-014 (Repeat Contact Rate definition)
