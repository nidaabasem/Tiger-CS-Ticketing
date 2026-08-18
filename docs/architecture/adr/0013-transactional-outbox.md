# ADR-0013: Transactional Outbox

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

Writing a ticket state change and then separately calling a notification or integration API (email, Genesys webhook acknowledgement, CRM write-back) within the same request risks the dual-write problem: the state change commits but the notification is lost, or vice versa.

## Decision

Every domain event that must trigger a notification or integration call is written to an `OutboxMessage` row in the **same database transaction** as the triggering state change. A separate Hangfire-driven dispatcher (ADR-0015) reads and publishes pending messages.

## Alternatives Considered

- **Direct, synchronous calls** to notification/integration APIs within the request handler.
- **Two-phase commit** across the database and external systems.
- **Transactional Outbox with an asynchronous dispatcher** (chosen).

## Advantages

- Eliminates the dual-write problem: the state change and the "intent to notify" are atomic.
- Decouples the request/response cycle from potentially slow external calls (email, Genesys, CRM), keeping agent-facing UI responsive.
- One consistent pattern applied everywhere is easier to reason about, test, and monitor than a bespoke reliability mechanism per integration — valuable when several integrations (Email, CRM, Genesys) are being built in parallel within a 3-week window.

## Disadvantages

- Requires an always-running dispatcher (Hangfire) and additional infrastructure (`OutboxMessage` table, dead-letter handling).
- Introduces brief eventual consistency between "ticket state changed" and "notification actually sent."

## Consequences

Underlies every notification and integration call in the system, including the Genesys webhook-acknowledgement path (ADR-0019). See ADR-0014 for the companion idempotency-key design that makes retries and duplicate dispatch safe.

## Risks

- If the dispatcher falls behind under load, notification latency increases; mitigated by monitoring the `OutboxMessage` pending-count as an operational metric (ADR-0020).
