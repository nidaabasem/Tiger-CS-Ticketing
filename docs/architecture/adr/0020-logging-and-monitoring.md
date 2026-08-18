# ADR-0020: Logging and Monitoring

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

Integration/system downtime must be detected within 15 minutes (FR-ADM-06), and enough structured, correlated logging must exist to diagnose an SLA dispute, a Genesys webhook failure, or a CRM outage after the fact — all without exposing personal/customer information in log output (see `Security-Architecture.md`).

## Decision

Use structured application logging (`ILogger` with a structured-logging provider) carrying the correlation ID established in ADR-0014 on every log entry. Combine this with health-check-based monitoring for each external dependency (CRM, Email, Genesys) and Hangfire's own job-health dashboard (ADR-0015), alerting when a health check fails or the Outbox pending-count/dead-letter-count crosses a threshold.

## Alternatives Considered

- **Unstructured text logging only.**
- **Monitoring via manual dashboard checks**, with no automated alerting.
- **Structured, correlated logging + automated health-check alerting** (chosen).

## Advantages

- Structured logs are queryable and correlate directly with the audit trail (ADR-0018) via the shared correlation ID — one identifier to trace an issue end-to-end.
- Health-check-based alerting is a standard, low-overhead way to meet the 15-minute detection requirement without bespoke polling logic per integration.
- Achievable within the pilot timeline using first-party ASP.NET Core logging/health-check primitives, with no new infrastructure to stand up.

## Disadvantages

- Log fields must be deliberately designed to exclude personal information (caller number, contact details) or to mask them — an explicit discipline requirement, not automatic.
- Health checks alone do not catch every silent-failure mode (e.g., a webhook endpoint that stops receiving calls without erroring); such gaps need per-integration-specific monitoring, not covered by a generic health check.

## Consequences

Every module's notable actions are logged with a correlation ID; `Security-Architecture.md` §"Logging without exposing personal information" defines the specific masking rules.

## Risks

- Silent Genesys webhook delivery failures (Genesys stops sending, no error surfaced) are the highest-risk monitoring gap; flagged as an open question for the Genesys team in `Genesys-Integration.md`.
