# ADR-0005: Role-Based and Policy-Based Authorization

**Status:** Accepted — **amended by [ADR-0024](0024-system-administrator-authorization-override.md)**
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

> **Amendment (2026-08-21, confirmed management decision).** ADR-0024 adds a
> central authorization override for the **System Administrator** role, which
> now passes every policy this ADR's mechanism produces. Nothing below is
> withdrawn: the mechanism, the one-policy-per-matrix-cell rule, and every
> policy already defined remain in force, and the role lists in those
> policies still record what the permission matrix grants each role. The
> override is applied once, centrally, on top of them — see ADR-0024 for the
> mechanism, the carve-outs, and the one business rule it deliberately does
> not reach.

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

**Added by ADR-0024:** application services that make their own resource-scoped authorization decisions (the department/owner checks a policy cannot see) must route them through `AuthorizationGate`, for the same reason every endpoint must cite a policy — an authorization decision made outside the two central mechanisms is invisible to both, and to the override.

## Risks

- The permission matrix has known open refinements (e.g., exact Reporting User export scope); policies should be designed to be data-driven/configurable where the matrix itself may still evolve, to avoid a redeploy for every minor permission tweak. **This risk materialized** for the System Administrator row and was corrected by ADR-0024 — see its Context for what the literal reading of §4.1 produced in practice.
