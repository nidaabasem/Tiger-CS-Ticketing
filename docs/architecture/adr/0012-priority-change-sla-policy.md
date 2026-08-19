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

## Pilot-Scope Note (Added Following the 4-Week, 1-Developer Pilot Decision — Does Not Change This ADR's Decision)

For the 4-week, 1-developer pilot (`docs/design/MVP-Implementation-Backlog.md` §0), management has decided to go further than deferring the downgrade-approval workflow described above: **priority downgrades are disabled completely after ticket creation for the duration of the pilot.** No downgrade path exists anywhere in the pilot build — not a partially-built request queue, not a disabled UI control, simply no server-side capability. This satisfies this ADR's core safeguard (a downgrade can never be used to quietly hide a breach) by the simplest possible means available at reduced capacity: removing the downgrade direction entirely rather than building its approval gate. Priority **upgrades** remain unaffected and are built in the pilot, since an upgrade can only tighten a deadline and needs no approval gate.

This is a **pilot-scope restriction, not a revision of this ADR's decision.** The approved downgrade-request-and-approval design above — including the self-authorization fix in `docs/design/MVP-Design-Review-Findings.md` Finding DR-05, and its full specification in `docs/design/MVP-API-Contracts.md` §5.6.1–§5.6.5 and `docs/design/MVP-ERD.md`/`MVP-Data-Dictionary.md` §2.27 — remains the approved design for the post-pilot phase, unchanged and not deleted. The standard restriction statement used consistently across the design package:

> "Priority is fixed after ticket creation during the pilot. Downgrades are not permitted. The approved downgrade-request and approval design remains documented for the post-pilot phase."
