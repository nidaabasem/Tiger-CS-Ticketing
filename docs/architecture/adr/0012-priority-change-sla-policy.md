# ADR-0012: Priority-Change SLA Policy

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

An earlier draft referenced an undefined "proportional carry-forward" calculation for a mid-flight priority change, which describes an outcome without specifying a method. Separately, without a safeguard, a downgrade could be used to remove an at-risk or already-breached ticket from SLA-breach visibility.

## Decision

Every priority period is retained as its own immutable `TicketSlaInstance` row — never overwritten. An **upgrade** computes the new due date as the earlier of the existing due date and the freshly computed higher-tier due date (so an upgrade can only tighten a deadline, never loosen it). A **downgrade** requires Department Head (or above) approval before it takes effect; any breach already recorded under the prior tier is never removed or reversed. Management reporting shows both the original and the changed SLA period for any re-prioritized ticket.

## Alternatives Considered

- **Full clock restart** under the new tier, discarding elapsed time and any breach visibility.
- **An undefined "proportional" carry-forward** (rejected — not implementable without a specified formula).
- **Earlier-of-due-dates on upgrade; approval-gated, breach-preserving downgrade; full history retained** (chosen, per approved ISSUE-023).

## Advantages

- Fully auditable: every SLA period a ticket passed through is reconstructable from history.
- The approval gate directly closes the "quietly downgrade to hide a breach" loophole.
- The earlier-of-due-dates rule for upgrades is simple, unambiguous, and requires no formula beyond a `MIN()`.

## Disadvantages

- An upgraded ticket gets a fresh, full target under the stricter tier rather than a shortened one — the safer direction to err in, not a real downside, but worth noting as an asymmetry.
- Reporting must union current and historical `TicketSlaInstance` rows to show "original vs. changed" periods — slightly more query complexity than a single current-state field.

## Consequences

Directly implements ISSUE-023 as approved. `SLA-Architecture.md` §"Priority upgrade/downgrade behavior" documents the exact computation with worked examples.

## Risks

- The downgrade-approval gate is the single most safety-critical rule in the SLA engine from a compliance-integrity standpoint; it must ship with explicit test coverage before Phase 3 exits, flagged in `Architecture-Review-Checklist.md`.
