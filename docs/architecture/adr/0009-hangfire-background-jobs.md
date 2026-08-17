# ADR-0009: Hangfire Background Jobs

**Status:** Accepted
**Date:** 2026-08-17

## Context

The system needs reliable, observable background processing for: per-ticket SLA deadline checks, a periodic SLA sweep as a safety net, the Outbox dispatcher, CRM-downtime reconciliation for provisional Intake Records, and — from Phase 2 onward — scheduled report generation.

## Decision

Use **Hangfire**, backed by the same SQL Server database (ADR-0003), for all background job execution: scheduled (delayed) jobs for per-ticket SLA due-timestamp checks, a recurring job for the SLA sweep safety net, a recurring job for the Outbox dispatcher, and scheduled/triggered jobs for CRM reconciliation.

## Alternatives Considered

- **A custom-built job scheduler/worker service.**
- **Azure Functions or another serverless job runner**, separate from the main application.
- **Hangfire** (chosen).

## Advantages

- Hangfire's storage-backed job persistence (using the same SQL Server instance) means scheduled jobs survive an application restart — important given ADR-0007's design, where the sweep exists specifically to catch a scheduled job lost to a restart or deploy.
- A built-in dashboard for observing job status, retries, and failures materially helps operational monitoring (ADR-0013) without standing up separate tooling.
- First-party .NET integration fits the chosen runtime (ADR-0002) with minimal additional infrastructure — no separate message broker or serverless platform needed for MVP.

## Disadvantages

- Job storage sharing the primary application database means a job-processing spike could, in principle, add load to the same database serving ticket read/write traffic — worth watching as volume grows (the open scale question, ISSUE-015).
- Hangfire's dashboard and default retry semantics must be explicitly configured to align with the idempotency requirements in ADR-0008; naive default retry behavior alone would not guarantee no duplicate effect.
- A serverless alternative could scale background processing independently of the main application — a capability this decision does not provide, consistent with the modular-monolith choice in ADR-0001.

## Consequences

All scheduled-job and recurring-job design in the SLA engine, the Outbox dispatcher, and the CRM reconciliation flow assumes Hangfire. Monitoring dashboards (ADR-0013) should surface Hangfire's own job-health metrics alongside application-level ones.
