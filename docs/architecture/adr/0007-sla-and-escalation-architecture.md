# ADR-0007: SLA and Escalation Architecture

**Status:** Accepted
**Date:** 2026-08-17

## Context

SLA compliance is a contractual KPI. The calculation must correctly handle 24/7 vs. business-hours clocks, pause behavior that differs by SLA type and pending reason (Critical never pauses; Pending Customer and Pending Third-Party are separate, configurable decisions; First Response cannot pause once contact is received), priority changes mid-flight without erasing elapsed time, a recorded breach, or history, and a four-level escalation hierarchy where notification and a formal escalation-level change are related but distinct events.

## Decision

Store explicit `FirstResponseDueAtUtc`/`ResolutionDueAtUtc` timestamps on each ticket, computed at creation and recalculated on priority change under the approved policy: an **upgrade** takes the earlier of the existing due date and the freshly computed higher-tier due date; a **downgrade** requires Department Head approval and never removes or reverses a breach already recorded under the prior tier. Every SLA period is retained as an immutable `SlaHistoryEntry` row. `EscalationLevel` is tracked independently of `TicketStatus` (ADR-0006), and `EscalationEvent.NotifiedRoles` records who was informed (e.g., both Department Head and GM on a Critical breach) **separately** from the formal escalation level itself, which only changes via the configured, priority-specific Level 2→GM window.

## Alternatives Considered

- **Compute SLA compliance on the fly** from the creation timestamp and current priority alone, with no stored due-date columns.
- **A single "breached: yes/no" flag**, with no period-by-period history.
- **Tie GM notification directly to a formal Level 3 transition**, with no independently trackable "notified" concept.
- **Explicit due timestamps + full period history + notification/escalation-level split** (chosen).

## Advantages

- Explicit due timestamps make SLA state queryable and reportable without recomputing business-hours/holiday-calendar logic on every read.
- Full period history satisfies the approved requirement that management reporting show both the original and the changed SLA period for any re-prioritized ticket, and guarantees a breach can never be erased by a later downgrade.
- Separating notification from formal escalation level correctly represents the real intermediate state "GM has been told, but the Department Head is still the ticket's active owner" — a state a single combined signal cannot express.

## Disadvantages

- More moving parts than a simpler SLA model: due-timestamp computation, the scheduled-job-plus-sweep detection mechanism (ADR-0009), and a multi-row history table all need to stay consistent with each other.
- The priority-change earlier-of-due-dates rule and the downgrade approval gate need careful, well-tested logic — a subtle area where a bug would directly corrupt a contractual metric.
- The notification/escalation-level split adds one more distinction the UI must surface clearly, to avoid confusing agents and management about whether a ticket has "really" escalated.

## Consequences

Directly implements the approved decisions ISSUE-001, ISSUE-004, ISSUE-013, ISSUE-018, and ISSUE-023. Any future change to SLA business rules must be reflected consistently in both the due-timestamp computation logic and the `SlaHistoryEntry` retention behavior — the two must never be allowed to drift apart.
