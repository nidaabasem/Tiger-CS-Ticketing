# ADR-0022: Deployment Strategy

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

The pilot targets a 3-week internal delivery. It needs a deployment approach simple enough to fit that timeline, while meeting the ≥99.5% uptime and 48-hour-advance-notice-for-planned-maintenance requirements already established, and supporting the modular monolith's single-deployable model (ADR-0001).

## Decision

Deploy the modular monolith (`TigerCS.Api`/`TigerCS.Web`, sharing the same solution) as a **single deployable unit** to a controlled internal environment — [ASSUMPTION: containerized deployment (e.g., a single container image built from the solution) to whatever hosting environment Tiger Group's IT designates, pending confirmation]. Database migrations (Phase 3+) run as an explicit, reviewed deployment step, not automatically on application startup. Configuration (SLA policy defaults, holiday calendar, Genesys/CRM/Email endpoint settings) is externalized, not hardcoded, so environment promotion does not require a code change.

## Alternatives Considered

- **Manual, ad hoc deployment** (copy files, run manually) — rejected as too fragile for a system carrying a contractual SLA.
- **A full CI/CD pipeline with automated blue-green or canary deployment** — likely more than a 3-week pilot needs or can build.
- **Single-container, single-environment deployment with externalized configuration and an explicit migration step** (chosen, as the pragmatic middle ground).

## Advantages

- Matches the modular monolith's single-deployable nature — no orchestration complexity to build within the pilot window.
- Externalized configuration means the same build can be validated in a staging-like setting before the pilot's production/internal environment, without a code change between the two.
- An explicit, reviewed migration step (never automatic on startup) avoids an accidental schema change reaching a live database unreviewed.

## Disadvantages

- A single deployable unit means the whole application redeploys for any change, including a small configuration fix — an accepted trade-off for pilot simplicity, per ADR-0001.
- Without a full CI/CD pipeline, some deployment steps may initially be manual, which is more error-prone than an automated pipeline — a risk to manage via a documented deployment runbook (Phase 3 deliverable) rather than architecture alone.

## Consequences

`Architecture-Review-Checklist.md` includes explicit "deployment readiness" checks. The exact hosting target (on-premises, Azure, or another cloud) is **not yet confirmed** — marked as an open question, not assumed.

## Risks

- **Hosting environment is unconfirmed** — this ADR proceeds on an [ASSUMPTION] of containerized deployment to an internal environment; if Tiger Group's IT mandates a different target (e.g., on-premises IIS, a specific cloud), this ADR needs revisiting before Phase 3 deployment work begins.
- Given the 3-week timeline, any delay in confirming the hosting target directly threatens the pilot delivery date.
