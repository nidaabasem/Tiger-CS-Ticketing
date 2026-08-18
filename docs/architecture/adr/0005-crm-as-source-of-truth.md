# ADR-0005: CRM as the Source of Truth

**Status:** Accepted
**Date:** 2026-08-17

## Context

Tiger Group's CRM already holds the authoritative unit and contact records. An earlier draft of the Solution Analysis risked implying the ticketing system would maintain its own mastered copy of that data, which would let the two systems silently diverge — a real risk given the ticketing system is not the party responsible for maintaining ownership/tenancy records.

## Decision

The CRM remains the **sole master** of unit and contact data. The ticketing system's local tables (`UnitReference`, `ContactReference`) store only the CRM-issued identifiers plus a **refreshable, non-authoritative display cache**. Each `Ticket` additionally captures an **immutable snapshot** of the relevant unit/contact fields at verification time — a snapshot that never changes even if the CRM record, or the local cache, later does.

## Alternatives Considered

- **Full replication/mirroring** of CRM unit and contact data into locally-mastered tables.
- **Live CRM query on every read**, with no local cache or snapshot at all.
- **Snapshot-and-reference-only model** (chosen).

## Advantages

- Ticket history remains a faithful, immutable, point-in-time record even after a later CRM update (e.g., a contact-detail change post-handover) — essential for audit and any future dispute resolution.
- Avoids data-ownership disputes and synchronization bugs that arise when two systems both claim to be authoritative for the same entity.
- A local, refreshable cache — distinct from the immutable snapshot — still supports fast unit/contact lookups without a live CRM round-trip on every single read.

## Disadvantages

- Requires careful modeling and code-review discipline to avoid conflating "cache" (refreshable, used for lookups) with "snapshot" (immutable, used for historical record) — a real risk if the two concepts are not kept structurally distinct.
- The cache can go briefly stale between CRM syncs, in principle showing slightly outdated display data until the next refresh.
- Every ticket-creation path has a hard dependency on the CRM integration being reachable — the exact scenario the CRM-downtime decision (ISSUE-006, Intake Record + provisional tickets) exists to manage.

## Consequences

Reflected directly in the schema design: `UnitReference`/`ContactReference` (cache) versus `Ticket.Snapshot*` columns (immutable). No future feature may introduce a locally-mastered copy of unit/contact data without revisiting this ADR.
