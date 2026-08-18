# ADR-0004: ASP.NET Core Identity and Policy-Based Authorization

**Status:** Accepted
**Date:** 2026-08-17

## Context

The system has ten internal roles (Solution Analysis §4) governed by a detailed permission matrix (View/Create/Edit/Assign/Transfer/Escalate/Resolve/Close/Reopen/Cancel/Reject/Export/Admin), plus an explicit, approved decision (ISSUE-021, Option A) that **no customer-facing authentication exists** in MVP or any currently-approved phase. Access must be revocable within 24 hours of staff departure (FR-ADM-02) and enforced server-side, not merely hidden in the UI.

## Decision

Use **ASP.NET Core Identity** for authentication of internal staff only — Geyness Agent, Supervisor, Department Employee, Department Head, CS Manager, General Manager, Chairman/CEO, System Administrator, Reporting User. No `Customer` identity or account type exists in this identity store. Authorization is enforced via ASP.NET Core's **policy-based authorization**, with one policy per relevant permission-matrix cell, checked server-side on every API endpoint — never relying on the UI alone to hide an action.

## Alternatives Considered

- **A custom-built authentication/authorization system.**
- **A third-party identity provider** (e.g., Azure AD/Entra ID, Auth0) fronting the application.
- **Role-only `[Authorize(Roles=...)]` checks**, without finer-grained policies.
- **ASP.NET Core Identity + policy-based authorization** (chosen).

## Advantages

- First-party, well-supported component of the chosen stack (ADR-0002), reducing integration risk versus a custom or third-party alternative.
- Policy-based authorization expresses department-scoped and action-specific rules (e.g., "a Department Employee may Resolve only their own department's tickets") beyond what a role-only check supports.
- Keeping the identity store internal-only, with no customer account type, enforces the no-portal decision (ISSUE-021) at the data-model level — not merely by omission in the UI, where it could be reintroduced by accident.

## Disadvantages

- Building a distinct policy for every permission-matrix cell is more upfront design and test work than a simple role check.
- ASP.NET Core Identity's default schema (`AspNetUsers`, `AspNetRoles`, etc.) is somewhat opinionated and needs extension — reflected in the schema design's `Employee` table, a 1:1 extension carrying `DepartmentId`, `IsGeynessStaff`, and `DeactivatedAtUtc`.
- Choosing an external identity provider (a rejected alternative) would have offloaded password/MFA management; this decision keeps that responsibility in-house instead.

## Consequences

Every endpoint in the API contract sketch (`Tiger-CS-Ticketing-Architecture-Design.md` §5) is annotated with the specific roles/policy drawn from the Section 4 permission matrix. A future customer portal, if ever separately approved, would require its own, separately-scoped identity surface rather than an extension of this internal-staff-only store.
