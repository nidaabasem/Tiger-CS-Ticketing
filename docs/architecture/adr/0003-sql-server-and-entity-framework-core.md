# ADR-0003: SQL Server and Entity Framework Core

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

A relational database and data-access technology are needed to persist the ticket aggregate, its independent lifecycle dimensions, SLA history, Genesys interaction links, and audit trail, with strong transactional consistency — the Transactional Outbox pattern (ADR-0013) requires a ticket state change and its outbox write to commit atomically.

## Decision

Use **SQL Server** as the relational database, accessed via **Entity Framework Core**, exclusively from `TigerCS.Infrastructure` (never referenced directly from `Domain` or `Application`, per ADR-0001).

## Alternatives Considered

- **PostgreSQL** or another open-source RDBMS.
- **A NoSQL/document store** for the ticket aggregate.
- **Dapper** (a micro-ORM) instead of full EF Core.
- **SQL Server + EF Core** (chosen).

## Advantages

- Named in the proposed stack; fits a Microsoft-centric hosting environment.
- EF Core's transaction scope is the natural mechanism for the atomic ticket-state-change-plus-outbox-write guarantee ADR-0013 depends on.
- Relational model suits the many foreign-key relationships in `Domain-Model.md` and the ad hoc reporting joins the operational dashboard needs.
- Mature migration and indexing tooling — important for a 3-week pilot where schema iteration speed matters.

## Disadvantages

- EF Core's LINQ-to-SQL translation can obscure generated SQL; requires query-plan review in code review.
- SQL Server licensing cost is a factor for infrastructure planning.
- A document store might have offered more schema flexibility for the multi-dimension ticket state, at the cost of the relational reporting this system depends on.

## Consequences

The domain model (`Domain-Model.md`) is designed to map cleanly onto a relational schema, without SQL DDL being produced at this stage. EF Core entity classes and actual migrations remain Phase 3 ("Project Foundation") deliverables, explicitly out of scope for this architecture package.

## Risks

- Schema churn during the 3-week pilot as the domain model is implemented could require multiple migration revisions; mitigated by finalizing `Domain-Model.md` and this architecture package before Phase 3 coding starts.
