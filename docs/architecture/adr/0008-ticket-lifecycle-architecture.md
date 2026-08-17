# ADR-0008: Ticket Lifecycle Architecture

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

A single combined status field cannot represent a ticket that is simultaneously "escalated" and "still being actively worked," and cannot cleanly represent Reopen (a domain event) or Duplicate (a resolution outcome requiring a linked ticket) without forcing them into status values they are not.

## Decision

Model ticket state as **five independent dimensions** — `TicketStatus`, `VerificationStatus`, `EscalationLevel`, `SlaState`, and `ResolutionOutcome` — each with its own value set, transition rules, and audit trail (`TicketStatusHistory`, dimension-tagged).

## Alternatives Considered

- **Single combined status field**, with new values added ad hoc as combinations are discovered.
- **Two dimensions only** (status + escalation).
- **Five independent dimensions** (chosen).

## Advantages

- Correctly represents real, simultaneous combinations (e.g., `InProgress` + `EscalationLevel 2`) without a combinatorial explosion of status values.
- Each dimension is independently transitionable, auditable, and reportable — directly simplifying the SLA/escalation engine (ADR-0009–0012).
- Reopen and Duplicate are modeled as what they are — an event and an outcome — not forced into a status enum.

## Disadvantages

- More columns and transition-rule surface area than a single-status model.
- Requires disciplined UI design so agents see one coherent picture, not five hard-to-reconcile fields.

## Consequences

Forms the foundation of `Domain-Model.md`'s `Ticket` entity. Implements the required behavior approved under ISSUE-008 (management approved the behavior; this ADR is the IT/Solution-Architect-owned implementation).

## Risks

- Given the 3-week pilot, the five-dimension model must be fully specified before Phase 3 coding starts to avoid a mid-sprint redesign; `Domain-Model.md` and this ADR are intended to close that risk now.
