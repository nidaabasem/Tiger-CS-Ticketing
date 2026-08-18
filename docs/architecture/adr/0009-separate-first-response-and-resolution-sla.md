# ADR-0009: Separate First Response and Resolution SLA Tracking

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

The source requirements define two distinct SLA targets per priority tier — First Response and Resolution — with different durations and different satisfying events (ISSUE-019: First Response is satisfied only by `FirstHumanResponseAtUtc`, never the automated acknowledgement). Treating these as one combined SLA would conflate two operationally different commitments.

## Decision

Track `FirstResponseDueAtUtc`/`FirstHumanResponseAtUtc` and `ResolutionDueAtUtc`/`ResolvedAtUtc` as fully independent pairs on the ticket (and on each `TicketSlaInstance` history row). Each has its own due-timestamp computation, its own breach/warning state, and — for Genesys-originated calls — First Response can now be satisfied by the Genesys interaction's answer timestamp (ADR-0019), operationalizing ISSUE-019's approved "call answer counts as First Human Response" rule.

## Alternatives Considered

- **A single combined SLA clock** covering both response and resolution.
- **First Response tracked informally (not a stored field)**, relying on manual note-taking.
- **Fully independent First Response and Resolution tracking** (chosen).

## Advantages

- Matches the source requirements' own SLA table (§6), which specifies separate First Response and Resolution targets per tier.
- Enables accurate reporting on "how fast did we first engage" versus "how fast did we fully resolve," which are different management questions.
- Cleanly absorbs the Genesys call-answer event as a First Response satisfier without touching Resolution SLA logic at all.

## Disadvantages

- Doubles the due-timestamp/breach bookkeeping relative to a single combined clock.
- Requires the UI to clearly distinguish the two SLAs so agents are not confused about which one they are racing against.

## Consequences

`Domain-Model.md`'s `TicketSlaInstance` entity carries both pairs. `SLA-Architecture.md` documents the full computation and pause/resume rules for each independently.

## Risks

- A bug conflating the two clocks would silently corrupt SLA-compliance reporting; mitigated by dedicated unit tests per ADR-0021 covering both clocks independently.
