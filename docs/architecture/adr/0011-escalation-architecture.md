# ADR-0011: Escalation Architecture

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

The four-level escalation hierarchy (Agent → Department Head → GM → Chairman/CEO) needs a mechanism to advance automatically that is time-based and priority-based (ISSUE-013, absorbing the earlier, rejected retry-count approach), and must keep "who was notified" distinct from "what formal level the ticket is at" (ISSUE-004 + ISSUE-013's approved clarification) — a Critical breach notifying the GM does not, by itself, set `EscalationLevel = 3`.

## Decision

Track `EscalationLevel` independently of `TicketStatus` (ADR-0008). A `TicketEscalation` entry records `Level`, `TriggerType`, and `NotifiedRoles` separately from the level itself. Formal advancement from Level 2 to Level 3 occurs only when the configured, priority-specific Level 2→GM window (per-tier defaults already approved: Critical 30 min, High 2h, Medium 1 business day, Low 2 business days) expires without resolution — never from a retry count.

## Alternatives Considered

- **A capped retry-count mechanism** (rejected in an earlier pass — replaced by ISSUE-013).
- **Tie GM notification directly to a formal Level 3 change**, with no independent "notified" concept.
- **Time-and-priority-based window, with notification tracked separately from formal level** (chosen).

## Advantages

- Escalation urgency scales with actual priority rather than an arbitrary retry count, which does not correlate with how urgent a ticket actually is.
- The notification/level split correctly represents "GM has been told, but Department Head is still the active owner" — a real intermediate state a single signal cannot express.
- Per-tier windows are configurable data (`SlaPolicy`), not hardcoded, so Administration can tune them without a redeploy.

## Disadvantages

- Two distinct signals ("who was told" and "what level is this at") must both be surfaced clearly in the UI to avoid confusing agents and management.
- Requires the scheduled-job infrastructure (ADR-0015) to reliably fire the window-expiry check per ticket, per tier.

## Consequences

Implements ISSUE-004, ISSUE-005 (absorbed into ISSUE-013), and ISSUE-013 as approved. `SLA-Architecture.md` documents the full escalation-window and warning-threshold design with worked examples.

## Risks

- A misconfigured window (e.g., Critical set to a long window by accident) would silently weaken the escalation guarantee for the most urgent tier; mitigated by dedicated test coverage and an Administration-side confirmation step when editing `SlaPolicy`.
