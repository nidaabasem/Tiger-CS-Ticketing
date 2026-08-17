# ADR-0021: Automated Testing Strategy

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

The domain has subtle, high-consequence logic — SLA due-date computation, the priority-change earlier-of-due-dates/approval-gate rule (ADR-0012), business-hours/holiday-calendar exclusion (ADR-0010), Genesys webhook idempotency (ADR-0014/0019) — where a bug would directly corrupt a contractual metric or cause a duplicate/lost effect. The stated development process is AI-assisted with human engineering review, within a 3-week pilot: automated tests are the primary safety net for that process, not a nice-to-have.

## Decision

Use xUnit across three layers: (1) **unit tests** for `Domain`/`Application` logic (SLA calculations, state-transition rules, priority-change policy, permission-policy handlers) with no database or network dependency; (2) **integration tests** for `Infrastructure`/`Integrations` (EF Core mappings, Outbox dispatch, CRM/Email/Genesys adapters) against a real or containerized SQL Server instance; (3) targeted **end-to-end tests** for the highest-risk flows — verify → create → classify → route → resolve → close, the four-level escalation path, and the Genesys call-answer-to-First-Response path.

## Alternatives Considered

- **Unit tests only.**
- **Manual QA/UAT only**, no automated suite.
- **A different framework** (NUnit, MSTest) in place of xUnit.
- **Three-layer xUnit strategy** (chosen).

## Advantages

- Fast unit tests can exhaustively cover the SLA/priority-change worked examples (`SLA-Architecture.md`) without a live database.
- Integration tests catch the class of bug unit tests structurally cannot (EF Core mapping errors, a failed Outbox dispatch against a real transaction).
- End-to-end tests on the highest-risk flows give confidence in the assembled system, particularly important given AI-assisted code generation, where a human reviewer benefits from an automated safety net covering the same ground.

## Disadvantages

- Three layers require more test code and a slower full-suite run than unit tests alone — a real cost against a 3-week timeline, mitigated by prioritizing the highest-risk logic first.
- Test infrastructure (a seeded test database) must be kept consistent with the schema design as it evolves.

## Consequences

Every ADR introducing subtle logic (SLA, Outbox/idempotency, ticket lifecycle, Genesys webhook handling) is expected to ship with unit-test coverage for its edge cases as a condition of being considered done. No test files are created by this ADR itself — it records the strategy Phase 3 will follow.

## Risks

- Under a 3-week deadline, test coverage is the most likely thing to be compressed under schedule pressure; `Architecture-Review-Checklist.md` explicitly checks for this before pilot sign-off.
