# ADR-0015: Hangfire Scheduled Jobs

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

The system needs reliable background processing for: per-ticket SLA deadline checks (First Response and Resolution, independently — ADR-0009), a periodic SLA sweep as a safety net, the Outbox dispatcher (ADR-0013), and CRM-downtime reconciliation for provisional Intake Records (ISSUE-006).

## Decision

Use **Hangfire**, backed by the same SQL Server database (ADR-0003), for all background job execution: a **scheduled (delayed) job** enqueued per due timestamp as the primary SLA-breach detection mechanism; a **recurring sweep** (every 1–5 minutes) as a safety net only, catching a scheduled job lost to a deploy or restart; a recurring job for the Outbox dispatcher; and scheduled/triggered jobs for CRM reconciliation.

## Alternatives Considered

- **A custom-built job scheduler.**
- **A separate serverless job runner** (e.g., Azure Functions), outside the main application.
- **Hangfire** (chosen).

## Advantages

- Storage-backed job persistence means scheduled jobs survive an application restart — exactly the failure mode the sweep exists to catch.
- Built-in dashboard for job status, retries, and failures materially helps operational monitoring (ADR-0020) without extra tooling — valuable given the compressed pilot timeline.
- First-party .NET integration with minimal additional infrastructure to stand up.

## Disadvantages

- Shared database storage means a job-processing spike could add load to the same database serving ticket traffic.
- Default retry semantics must be explicitly configured to align with the idempotency requirements (ADR-0014); naive retries alone do not guarantee no duplicate effect.

## Consequences

The SLA engine, Outbox dispatcher, and CRM reconciliation flow all depend on Hangfire's scheduling and persistence guarantees. Monitoring must surface Hangfire's own job-health metrics alongside application-level ones.

## Risks

- If pilot ticket volume spikes unexpectedly, Hangfire's shared-database model could contend with primary application traffic; low likelihood at pilot scale, worth revisiting before Phase 2.
