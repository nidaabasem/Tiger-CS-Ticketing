# ADR-0004: ASP.NET Core Identity

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

The system requires authentication for ten internal roles (Geyness Agent, Supervisor, Department Employee, Department Head, CS Manager, General Manager, Chairman/CEO, System Administrator, Reporting User). An explicit, approved decision (ISSUE-021, Option A) excludes any customer-facing authentication from MVP and every currently-approved phase. Access must be revocable within 24 hours of staff departure (FR-ADM-02).

## Decision

Use **ASP.NET Core Identity** for authentication of internal staff only. No `Customer` account type exists in this identity store. The `AspNetUsers` table is extended 1:1 by a domain `Employee` record carrying `DepartmentId`, `IsGeynessStaff`, and `DeactivatedAtUtc`.

## Alternatives Considered

- **A custom-built authentication system.**
- **A third-party identity provider** (Azure AD/Entra ID, Auth0).
- **ASP.NET Core Identity** (chosen).

## Advantages

- First-party component of the chosen stack (ADR-0002); lowest integration risk within a 3-week timeline.
- Keeping the identity store internal-staff-only enforces the no-portal decision (ISSUE-021) at the data-model level, not merely by UI omission.
- Well-documented account lockout, password policy, and token infrastructure that Security-Architecture.md builds directly on.

## Disadvantages

- Default Identity schema is opinionated and needs the `Employee` extension table to carry domain-specific fields.
- Choosing an external identity provider (rejected) would have offloaded MFA/password management; this decision keeps that in-house.

## Consequences

Authorization *policies* (which roles can do what) are a separate concern, covered in ADR-0005. This ADR only establishes the authentication mechanism and the identity/employee data model.

## Risks

- If Tiger Group later mandates SSO via an existing corporate identity provider, this decision would need revisiting — flagged as an open question for a future phase, not a pilot blocker.
