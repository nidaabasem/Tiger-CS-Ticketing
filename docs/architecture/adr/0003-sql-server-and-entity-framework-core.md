# ADR-0003: SQL Server and Entity Framework Core

**Status:** Accepted
**Date:** 2026-08-17

## Context

A relational database and data-access technology are needed to persist the ticket aggregate and its five independent state dimensions (ADR-0006), SLA history, audit trail, and reference data, with strong consistency guarantees — in particular, the Transactional Outbox pattern (ADR-0008) requires a ticket state change and its corresponding outbox write to commit atomically in a single transaction.

## Decision

Use **SQL Server** as the relational database, accessed via **Entity Framework Core** as the ORM, exclusively from the `TigerCS.Infrastructure` module (never referenced directly from `Domain` or `Application`, per ADR-0001's dependency rule).

## Alternatives Considered

- **PostgreSQL** or another open-source RDBMS.
- **A NoSQL/document store** (e.g., Cosmos DB) for the ticket aggregate.
- **Dapper** (a micro-ORM) instead of full EF Core.
- **SQL Server + EF Core** (chosen).

## Advantages

- SQL Server is the database named in the proposed technology stack, and fits a Microsoft-centric hosting environment if that is Tiger Group's existing infrastructure.
- EF Core's `DbContext` transaction scope is a natural fit for atomically writing a ticket state change and its corresponding `OutboxMessage` row in one commit — the exact guarantee ADR-0008 depends on.
- The relational model suits the many foreign-key relationships already documented in the schema design (`Ticket` → `Department`, `UnitReference`, `ContactReference`, `SlaHistoryEntry`, `EscalationEvent`, `Attachment`) and the ad hoc reporting joins the dashboard and Weekly/Monthly reports (Phase 2) will need.
- Mature tooling for migrations, indexing, and query analysis; established patterns for audit/history tables like the ones this system already relies on (`StatusChangeEvent`, `SlaHistoryEntry`).

## Disadvantages

- EF Core's change tracking and LINQ-to-SQL translation can obscure the exact generated SQL, risking inefficient queries if not reviewed in code review and via query-plan checks.
- SQL Server licensing cost is a real factor to account for in infrastructure planning versus an open-source alternative.
- A document-oriented store might have modeled the five-dimension ticket state with more schema flexibility, but at the cost of the relational reporting and joins this system depends on heavily.

## Consequences

The full column-level database schema design already produced in `Tiger-CS-Ticketing-Architecture-Design.md` §4 targets SQL Server types (`nvarchar`, `datetime2`, `tinyint`, `rowversion`, etc.). EF Core entity classes and actual migrations are explicitly out of scope for this ADR and for the current phase — they are Phase 3 ("Project Foundation") deliverables.
