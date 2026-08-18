# ADR-0018: Audit Trail

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

The system must support a fully reconstructable audit trail for every ticket-lifecycle change across all five state dimensions (ADR-0008), every escalation event, every priority change, and every administrative action (exports, config changes, access revocation), over a 7-year retention horizon.

## Decision

Maintain two purpose-specific mechanisms: (1) `TicketStatusHistory`, populated via domain-event subscribers, recording every change to any of the five ticket-state dimensions with actor, before/after value, correlation ID, and timestamp; and (2) a generic `AuditEntry` table for actions outside the ticket-lifecycle model (exports, admin/config changes, access revocation, Genesys webhook signature failures).

## Alternatives Considered

- **A single, generic audit table** covering both ticket-lifecycle changes and administrative actions.
- **Unstructured log files only**, with no dedicated audit tables.
- **Two purpose-specific tables** (chosen).

## Advantages

- Separates ticket-lifecycle audit from general administrative audit, keeping each table's query patterns and retention needs distinct.
- Shared correlation IDs (ADR-0014) across both tables and the Outbox mean any notification or SLA calculation is traceable end-to-end for a dispute.
- Both tables are strictly append-only — no update or delete path exists in the application layer, guaranteeing audit immutability (see `Security-Architecture.md`).

## Disadvantages

- Every new feature must decide which of the two tables applies, a small design-review overhead.
- Structured logging and correlation-ID propagation must be enforced consistently across every module; a gap in one integration creates a blind spot.

## Consequences

Satisfies FR-TKT-07, FR-ADM-03, and BR-015. Every future integration or admin feature must route notable actions through one of these two mechanisms, not invent a third.

## Risks

- Genuinely silent failures (e.g., a Genesys webhook that simply stops arriving without erroring) are not caught by the audit trail alone — they require separate monitoring (ADR-0020), which the audit trail complements but does not replace.
