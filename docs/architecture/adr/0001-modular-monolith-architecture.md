# ADR-0001: Modular Monolith Architecture

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

The MVP domain is dominated by a single write-heavy aggregate — the ticket, with its independent lifecycle dimensions (ADR-0008), SLA/escalation engine (ADR-0009–0012), and a Genesys integration now confirmed for MVP (ADR-0019). The 3-week pilot timeline and AI-assisted-development-with-human-review process both favor a single deployable unit with strong internal boundaries over the coordination overhead of independently deployed services.

## Decision

Build a single ASP.NET Core solution structured as a modular monolith. Twelve logical modules (Identity and Access, CRM Verification, Genesys Integration, Ticketing, Classification and Routing, SLA and Escalation, Notifications, Attachments, Audit, Dashboard and Reporting, Administration, Infrastructure — detailed in `Module-Design.md`) sit within the physical project layering `TigerCS.Domain` / `TigerCS.Application` / `TigerCS.Infrastructure` / `TigerCS.Integrations` / `TigerCS.Reporting` / `TigerCS.Api` / `TigerCS.Web` / `TigerCS.Tests`, communicating in-process via application services and domain events — never a direct cross-module database call.

## Alternatives Considered

- **Microservices from day one** — independently deployed services with their own databases, coordinated via messaging.
- **Unstructured single project** — no internal module boundaries at all.
- **Modular monolith** (chosen).

## Advantages

- A single transaction boundary keeps ticket, SLA, escalation, and Genesys-interaction state changes atomic and consistent — required for the Transactional Outbox pattern (ADR-0013).
- Matches a 3-week pilot timeline: one deployable, one database, no service-discovery or orchestration overhead to build before feature work can start.
- Clean internal module boundaries (`Module-Design.md`) mean a later extraction into services remains possible without a rewrite, if volume ever justifies it.
- Well suited to AI-assisted development with human review: clear module boundaries give reviewers a concrete unit to check for boundary violations.

## Disadvantages

- All modules deploy together — a fix to one module still requires a full-application redeploy.
- Cannot independently scale a single hot module without scaling the whole application.
- Requires discipline (enforced via project references and code review) to keep module boundaries honest, since there is no runtime boundary enforcing them.

## Consequences

Module boundaries are enforced via project references, not a network boundary. `Module-Design.md` defines the dependency graph and explicitly prohibited dependencies to keep it a DAG (no cycles).

## Risks

- Under a compressed 3-week timeline, boundary discipline could slip under schedule pressure; mitigated by human engineering review on every PR, per the stated AI-assisted-with-human-review process.
- If pilot volume is materially higher than assumed (ISSUE-015, still open), a hot module may need extraction sooner than planned.
