# ADR-0006: Ticket Lifecycle Design (Five Independent Dimensions)

**Status:** Accepted
**Date:** 2026-08-17

## Context

A single combined status field cannot represent a ticket that is simultaneously "escalated" and "still being actively worked," and cannot cleanly represent Reopen (a domain event) or Duplicate (a resolution outcome requiring a linked ticket ID) without forcing them into status values they are not.

## Decision

Model ticket state as **five independent dimensions** — `TicketStatus`, `VerificationStatus`, `EscalationLevel`, `SlaState`, and `ResolutionOutcome` — each with its own value set, transition rules, and audit trail, rather than a single combined status field.

## Alternatives Considered

- **Single combined status field**, with new values added ad hoc whenever a new combination is discovered (e.g., an "EscalatedInProgress" status).
- **Two dimensions only** (status + escalation), folding verification and SLA state into flags on the status field.
- **Five independent dimensions** (chosen).

## Advantages

- Correctly represents real, simultaneous combinations — e.g., `TicketStatus = InProgress` while `EscalationLevel = Level2` — without an ever-growing combinatorial explosion of status values.
- Each dimension can be transitioned, audited, and reported on independently, which materially simplifies both the SLA/escalation engine (ADR-0007) and reporting queries.
- Reopen and Duplicate are modeled as what they actually are — a domain event and a resolution outcome, respectively — rather than forced into a status enum they don't semantically belong in.

## Disadvantages

- More columns and more transition-rule surface area to design, implement, and test than a single-status model.
- Requires disciplined UI/UX design so agents see one coherent picture of "what's going on with this ticket," rather than five separate, hard-to-reconcile fields.
- A modestly steeper learning curve for engineers and agents unfamiliar with the model, relative to a single status they might expect from other ticketing systems.

## Consequences

This is the foundation for the ticket domain model throughout `Tiger-CS-Ticketing-Architecture-Design.md`'s ERD and schema (`Ticket.TicketStatus` / `VerificationStatus` / `EscalationLevel` / `SlaState` / `ResolutionOutcome` columns, and the `StatusChangeEvent` audit table keyed by `Dimension`). This ADR is the IT/Solution-Architect-owned implementation of the required behavior approved under ISSUE-008, where management approved the behavior (escalation independently reportable from status, etc.) and delegated the specific implementation model to IT/Solution Architect.
