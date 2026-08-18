# ADR-0014: Idempotency and Correlation IDs

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

Several mechanisms can fire for the same logical event more than once: the SLA scheduled-job and the sweep safety net (ADR-0015) can both check the same due timestamp; Genesys can redeliver the same webhook (a documented behavior of most webhook providers); the Outbox dispatcher (ADR-0013) can retry a failed send. None of these should ever produce a duplicate customer-facing or business-visible effect. Separately, any notification or SLA calculation must be traceable end-to-end for audit or dispute resolution.

## Decision

Every dispatched Outbox message, every SLA breach/warning check, and every inbound Genesys webhook carries an **idempotency key** derived from a stable business fact (e.g., `TicketId + EventType + EventVersion` for outbox messages; the Genesys `ConversationId` + event type for webhooks). Every request, domain event, and downstream call carries a **correlation ID**, propagated through logs, the Outbox, and integration calls.

## Alternatives Considered

- **No idempotency keys** — rely on careful scheduling to avoid double-firing (rejected as fragile).
- **Database-level unique constraints only**, with no explicit application-level key concept.
- **Explicit idempotency keys + correlation IDs, checked at the application layer** (chosen).

## Advantages

- Makes retries, redelivery, and safety-net overlap provably safe rather than "probably fine" — a hard guarantee, not a hope.
- Correlation IDs let any notification or SLA calculation be traced end-to-end across logs, the Outbox, and Genesys webhook records — directly supporting the audit and dispute-resolution requirement.
- A single, consistent mechanism reused everywhere (SLA engine, Notifications, Genesys Integration) rather than a bespoke de-duplication scheme per integration.

## Disadvantages

- Requires every new integration or scheduled check to be designed with its idempotency key in mind from the start, not bolted on afterward.
- A poorly chosen idempotency key (not actually unique to the logical event) would silently defeat the guarantee — requires careful key design per integration, documented in each relevant ADR/integration doc.

## Consequences

`OutboxMessage.IdempotencyKey` is a unique-indexed column (per the prior schema design); Genesys webhook processing (ADR-0019) uses the same discipline for `ConversationId`-keyed events.

## Risks

- The Genesys-specific idempotency key design is flagged as an open question for the Genesys team in `Genesys-Integration.md` (exact event/conversation ID semantics need confirmation).
