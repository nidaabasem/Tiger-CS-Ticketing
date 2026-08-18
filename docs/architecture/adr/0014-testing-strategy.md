# ADR-0014: Testing Strategy

**Status:** Accepted
**Date:** 2026-08-17

## Context

The proposed stack specifies xUnit for automated tests. The domain has subtle, high-consequence logic — SLA due-date computation, the priority-change earlier-of-due-dates/approval-gate rule, business-hours/holiday-calendar exclusion, Outbox idempotency — where a bug would directly corrupt a contractual metric or cause a duplicate or lost notification. This is exactly the kind of logic that is easy to get subtly wrong without dedicated test coverage.

## Decision

Use xUnit across three layers of automated testing: (1) **unit tests** for `TigerCS.Domain` and `TigerCS.Application` logic (SLA calculations, state-transition rules, the priority-change policy, permission checks) with no database or network dependency; (2) **integration tests** for `TigerCS.Infrastructure`/`TigerCS.Integrations` (EF Core mappings, Outbox dispatch, CRM/Email/File-Storage adapters) against a real or containerized SQL Server instance; and (3) targeted **end-to-end tests** for the highest-risk flows — verify → create → classify → route → resolve → close, and the four-level escalation path — exercised through the API layer.

## Alternatives Considered

- **Unit tests only**, with no integration or end-to-end coverage.
- **Manual QA/UAT only**, with no automated test suite.
- **A different test framework** (e.g., NUnit or MSTest) in place of xUnit.
- **A three-layer xUnit strategy** (chosen).

## Advantages

- Unit tests without infrastructure dependencies run fast and can exhaustively cover the SLA/priority-change edge cases (the worked examples already documented in the Solution Analysis) without needing a live database for every run.
- Integration tests catch the class of bug unit tests structurally cannot — an EF Core mapping mistake, a misconfigured index, an Outbox message that fails to dispatch against a real transactional boundary.
- End-to-end tests on the highest-risk flows give confidence the whole assembled system behaves correctly, not just its individual pieces in isolation.
- xUnit is the framework specified in the proposed stack and has first-party, mature tooling support for .NET 8 (ADR-0002).

## Disadvantages

- Three layers of tests mean more code to write and maintain than a single-layer strategy, and a slower full-suite run than unit tests alone.
- Integration and end-to-end tests require test infrastructure (a test database, seeded reference data) that must itself be kept consistent with the schema design as it evolves.
- Without disciplined boundaries between layers, overlap is possible (e.g., an "integration test" that could have been a pure unit test), which needs code-review attention to avoid a redundant, slow test suite.

## Consequences

Every ADR above that introduces subtle logic (SLA architecture, Outbox/idempotency, ticket lifecycle) is expected to ship with unit-test coverage for its edge cases as a condition of that logic being considered done. This ADR does not itself create any test files — consistent with this task's scope boundary of ADRs only, no code — it records the strategy those future test files will follow once Phase 3 begins.
