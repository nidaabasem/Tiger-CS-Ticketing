# ADR-0007: Immutable CRM Snapshot Stored on Each Ticket

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

Even with the CRM as source of truth and a refreshable local cache (ADR-0006), a ticket's historical record must remain accurate to what the agent actually read back and relied on at verification time — even if the CRM record, or the cache, changes afterward (e.g., a contact detail updated post-handover).

## Decision

Each `Ticket` stores an **immutable snapshot** — unit number, property, tower, unit type, contact display name, contact channel — captured once, at verification time (or at CRM-reconciliation time for a provisional ticket), and **never updated** by later CRM or cache changes.

## Alternatives Considered

- **No snapshot** — always join to the live `UnitReference`/`ContactReference` cache for display.
- **Snapshot updated on every CRM sync** (i.e., not actually immutable).
- **Immutable, write-once snapshot** (chosen).

## Advantages

- Guarantees the ticket's historical record reflects what was true and communicated at the time, independent of later CRM changes — essential for audit and dispute resolution.
- Removes any ambiguity about "what did the agent actually tell the customer" months after the fact.
- Simple to implement: a handful of denormalized columns on `Ticket`, written once.

## Disadvantages

- Denormalizes CRM data onto `Ticket`, meaning a genuine correction to a *typo* made at verification time cannot be silently fixed by a later CRM sync — a correction would need its own explicit ticket-note/audit entry instead.
- Slightly increases `Ticket` row width.

## Consequences

Directly reflected in the schema design's `Ticket.Snapshot*` columns. `Ticketing` module code must never overwrite these columns after creation — enforced by omitting any update path for them in the application layer, not by a database trigger.

## Risks

- Low — the main risk is a future developer accidentally adding an update path for these columns; mitigated by clear code comments and this ADR's traceability.
