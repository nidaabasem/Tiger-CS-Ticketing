# ADR-0013: Logging, Monitoring, and Audit Trail

**Status:** Accepted
**Date:** 2026-08-17

## Context

The system must support a fully reconstructable audit trail for every ticket-lifecycle change across all five state dimensions (ADR-0006), detect integration/system downtime within 15 minutes (FR-ADM-06), and provide enough structured logging to resolve an SLA dispute or a security/access question after the fact, over a 7-year retention horizon.

## Decision

Maintain two distinct, complementary logging/audit mechanisms: (1) a domain-level `StatusChangeEvent` table, populated via domain-event subscribers, recording every change to any of the five ticket-state dimensions with actor, before/after value, correlation ID, and timestamp; and (2) a generic, cross-cutting `AuditLog` table for actions outside the ticket-lifecycle model (exports, admin/config changes, access revocation). Both are paired with structured application logging carrying the same correlation IDs used in the Outbox (ADR-0008), plus health-check-based monitoring/alerting to meet the 15-minute downtime-detection requirement.

## Alternatives Considered

- **A single, generic audit log table** covering both ticket-lifecycle changes and administrative actions.
- **Unstructured text log files only**, with no dedicated audit tables.
- **Two purpose-specific tables plus structured logging and health checks** (chosen).

## Advantages

- Separating ticket-lifecycle audit (`StatusChangeEvent`, dimension-tagged) from general administrative audit (`AuditLog`) keeps each table's query patterns simple and its retention/reporting needs distinct, rather than one large table serving two very different purposes.
- Shared correlation IDs across logs, the Outbox, and both audit tables mean any customer-facing notification or SLA calculation can be traced end-to-end for a dispute, without cross-referencing unrelated systems.
- Health-check-based monitoring is a standard, low-overhead way to meet the 15-minute detection requirement without bespoke polling logic per integration.

## Disadvantages

- Maintaining two audit tables, rather than one, means every new feature must decide which one applies — a small but real design-review overhead.
- Structured logging and correlation-ID propagation must be enforced consistently across every module; a gap in one integration's implementation would create a blind spot in traceability.
- Health checks alone do not guarantee every failure mode is caught within 15 minutes — a genuinely silent failure (e.g., a webhook that simply stops arriving without erroring) needs its own specific monitoring, to be designed per-integration from Phase 2 onward.

## Consequences

Every requirement calling for an audit trail (FR-TKT-07, FR-ADM-03, BR-015) is satisfied by this pair of tables. Every future integration or admin feature must route its notable actions through one of the two existing mechanisms, not invent a third.
