# ADR-0005: Role-Based and Policy-Based Authorization

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

The Solution Analysis's permission matrix (§4) is not expressible with role checks alone: it requires department-scoped rules (e.g., a Department Employee may Resolve only their own department's tickets) and action-specific splits (e.g., Resolve vs. Close are different, separately permissioned actions per ISSUE-022).

## Decision

Combine ASP.NET Core role membership (ADR-0004) with **policy-based authorization**: one named policy per relevant permission-matrix cell (e.g., `CanResolveOwnDepartmentTicket`, `CanCloseTicket`, `CanApprovePriorityDowngrade`), evaluated server-side on every API endpoint. Policies read the acting `Employee`'s role and `DepartmentId` claims plus the target ticket's `CurrentDepartmentId`.

## Alternatives Considered

- **Role-only `[Authorize(Roles=...)]` checks**, with no department scoping.
- **A fully custom authorization engine**, independent of ASP.NET Core's built-in mechanism.
- **Role + policy-based authorization** (chosen).

## Advantages

- Directly expresses department-scoped rules the permission matrix requires, which role checks alone cannot.
- Every endpoint's authorization requirement is declarative and testable in isolation (unit-testable policy handlers, per ADR-0021).
- Reuses ASP.NET Core's built-in policy evaluation pipeline — no custom middleware needed, keeping this achievable within the 3-week pilot.

## Disadvantages

- More upfront design and test work than a blanket role check — one policy per matrix cell, not one attribute per controller.
- Requires discipline to keep the policy definitions synchronized with §4's permission matrix as it evolves; a drift between the two would be a silent authorization bug.

## Consequences

Every endpoint in the (future) API design must cite the specific policy/role it enforces, traceable back to Solution Analysis §4. `Security-Architecture.md` documents the full policy catalog conceptually.

## Risks

- The permission matrix has known open refinements (e.g., exact Reporting User export scope); policies should be designed to be data-driven/configurable where the matrix itself may still evolve, to avoid a redeploy for every minor permission tweak.
