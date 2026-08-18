# ADR-0008: Transactional Outbox and Idempotency

**Status:** Accepted
**Date:** 2026-08-17

## Context

Writing a ticket state change and then separately calling a notification or integration API within the same request risks the classic dual-write problem: the state change commits but the corresponding notification is lost, or the notification is sent but the state change is later rolled back — either way, the two go out of sync.

## Decision

Every domain event that must trigger a notification or integration call is written to an `OutboxMessage` row **in the same database transaction** as the triggering state change. A separate dispatcher process reads and publishes pending messages. Every dispatched message, and every idempotency-sensitive check (e.g., an SLA breach check that could fire from both a scheduled job and the sweep safety net), carries an idempotency key derived from a stable business fact (`TicketId + EventType + EventVersion`) and a correlation ID for end-to-end tracing.

## Alternatives Considered

- **Direct, synchronous calls** to notification/integration APIs within the request handler that changes ticket state.
- **Two-phase commit** / a distributed transaction spanning the database and the external notification system.
- **Transactional Outbox with an asynchronous dispatcher** (chosen).

## Advantages

- Eliminates the dual-write problem: the state change and the "intent to notify" are atomic, so a later dispatcher failure can be retried safely, with the idempotency key preventing a duplicate effect.
- Decouples the request/response cycle from potentially slow external calls (email, SMS, CRM), improving perceived responsiveness for agents.
- One consistent pattern applied everywhere (SLA breach notification, ticket acknowledgement, CRM write-back) is far easier to reason about, test, and monitor than a different ad hoc reliability mechanism per integration.

## Disadvantages

- Requires an always-running dispatcher process (via Hangfire, ADR-0009) and additional infrastructure (the `OutboxMessage` table, dead-letter handling) that a direct-call approach would not need.
- Introduces brief eventual consistency between "ticket state changed" and "notification actually sent," which must be communicated correctly in any UI showing notification status.
- Every consumer of an Outbox message must itself be written to be safely idempotent — an extra discipline requirement across the Integrations module, not something the pattern enforces automatically.

## Consequences

This pattern underlies every notification and integration call described in the API contract sketch, and is the basis for the reliability requirements (NFR-REL-01 through NFR-REL-04) in the Solution Analysis. Any new integration added in a future phase must follow this same pattern rather than inventing a bespoke reliability mechanism.
