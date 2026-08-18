# ADR-0001: Modular Monolith Architecture

**Status:** Accepted
**Date:** 2026-08-17

## Context

The MVP domain is dominated by a single write-heavy aggregate — the ticket, with its five-dimension lifecycle (see ADR-0006), SLA/escalation engine (ADR-0007), and an audit trail spanning every state change. At MVP, there are only two true external integrations (CRM, Email/File Storage), with all other channels and vendor integrations deferred to Phase 2/Phase 3. Ticket state changes, SLA due-date calculations, and audit logging all need strong transactional consistency with each other.

## Decision

Build a single ASP.NET Core solution structured as a modular monolith: `TigerCS.Domain`, `TigerCS.Application`, `TigerCS.Infrastructure`, `TigerCS.Integrations`, `TigerCS.Reporting`, `TigerCS.Api`, `TigerCS.Web`, `TigerCS.Tests`, with a strict, one-directional dependency rule — `Domain` has no dependencies; `Application` depends only on `Domain`; every outer layer depends inward, never sideways or outward. Modules communicate in-process via application services and domain events; no module queries another module's tables directly.

## Alternatives Considered

- **Microservices from day one** — per-module deployable services, each with its own database, communicating via messaging or HTTP.
- **Unstructured single project** — one ASP.NET Core project with no internal module boundaries at all.
- **Modular monolith** (chosen).

## Advantages

- A single transaction boundary keeps ticket/SLA/escalation state changes atomic and consistent, which the Transactional Outbox pattern (ADR-0008) depends on.
- Simple deployment, debugging, and local development — one process, one database connection, no service discovery or orchestration to stand up for MVP.
- Avoids premature distributed-systems complexity: nothing in the current requirements demands independently scaling a specific module.
- Clean internal module boundaries mean a future extraction (e.g., pulling `Reporting` out as its own service, if volume ever justifies it) is a refactor, not a rewrite.

## Disadvantages

- All modules deploy together — a fix to one module still requires a full-application redeploy.
- Cannot independently scale a single hot module (e.g., the SLA sweep) without scaling the whole application.
- Requires discipline to keep module boundaries honest in code, since there is no physical or network boundary enforcing them — only project references and code review.

## Consequences

Module boundaries are enforced via project references (e.g., `Application` cannot reference `Infrastructure`) and code review, not by a runtime boundary. The solution is structured now so that extraction of a module into its own service remains possible later without a rewrite, should volume (see the open scale question, ISSUE-015) eventually justify it. This ADR formalizes the architecture already described in `Tiger-CS-Ticketing-Solution-Analysis.md` §10 and `Tiger-CS-Ticketing-Architecture-Design.md` §2.
