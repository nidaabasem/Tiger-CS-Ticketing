# ADR-0006: CRM as the Source of Truth for Units and Contacts

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

Tiger Group's CRM already holds the authoritative unit and contact records. The ticketing system must not maintain a second, competing master copy of this data, which would risk the two systems silently diverging.

## Decision

The CRM remains the **sole master** of unit and contact data (BR-027). The ticketing system's local tables (`UnitReference`, `ContactReference` — see `Domain-Model.md`) store only the CRM-issued identifiers plus a **refreshable, non-authoritative display cache**, used for lookups and read-back during verification.

## Alternatives Considered

- **Full replication** of CRM unit/contact data into locally-mastered tables.
- **Live CRM query on every read**, with no local cache at all.
- **Reference-with-refreshable-cache model** (chosen).

## Advantages

- Avoids data-ownership disputes and synchronization bugs from two systems both claiming authority over the same entity.
- A local cache still supports fast lookups without a live CRM round-trip on every read, important for agent call-handling speed.
- Keeps the CRM integration's scope narrow and clearly bounded — a lookup/reference API, not a data-replication pipeline — achievable within the pilot timeline.

## Disadvantages

- The cache can go briefly stale between CRM syncs.
- Every ticket-creation path has a hard dependency on CRM reachability — the exact scenario the CRM-outage decision (ISSUE-006) exists to manage via `IntakeRecord` and provisional tickets.

## Consequences

`UnitReference`/`ContactReference` (cache, refreshable) are structurally distinct from the immutable per-ticket snapshot (ADR-0007) — this ADR governs the cache only; ADR-0007 governs the snapshot.

## Risks

- CRM API availability/rate limits are not yet confirmed (ISSUE-015 touches on this); the cache design mitigates load but does not eliminate the dependency.
