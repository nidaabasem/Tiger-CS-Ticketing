# ADR-0024: System Administrator Authorization Override

**Status:** Accepted — **confirmed management decision**, superseding the previous exclusion
**Date:** 2026-08-21
**Amends:** ADR-0005 (role-based and policy-based authorization). ADR-0005 is not withdrawn: its mechanism, its one-policy-per-matrix-cell rule, and every policy it produced remain in force. This ADR adds a single, centrally-applied override on top of them, and supersedes `Tiger-CS-Ticketing-Solution-Analysis.md` §4.1's exclusion of System Administrator from the operational permission columns.
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

`Tiger-CS-Ticketing-Solution-Analysis.md` §4.1's permission matrix grants the
System Administrator role `View: All (technical)`, `Export: All`, and
`Admin: ✔ Full`, and a dash in every operational column — Create, Edit,
Assign, Transfer, Escalate, Resolve, Close, Reopen, Cancel, Reject. The
implementation followed that matrix literally and correctly. The observable
result was that a System Administrator received `403 Forbidden` from the
CRM verification surface, intake-record creation, ticket creation, and every
ticket operation — `POST /api/intake-records` being the case that surfaced
it.

Management has confirmed that this is not the intended access model: the
System Administrator role must have access to every application feature and
every API endpoint. This ADR records that decision and the mechanism chosen
to implement it.

Two properties were required alongside the grant itself:

1. **It must be centralized.** Adding the role to each controller, policy,
   or role set would work today and drift tomorrow — a policy added next
   sprint for SLA, escalation, reporting, or administration would silently
   exclude the role again, reproducing exactly the defect being corrected.
2. **It must be an authorization override, and nothing more.** Full access
   is a statement about permission. It is not a licence to bypass request
   validation, ticket status-transition rules, closed-ticket immutability,
   optimistic-concurrency control, database constraints, required business
   data, or audit requirements.

## Decision

Grant `System Administrator` an authorization override applied in **one
central mechanism per authorization layer**, with the overridden role itself
defined in exactly one place.

### The role is named once

`TigerCS.Domain.Modules.IdentityAndAccess.AuthorizationOverride` holds the
override role and the predicate that tests for it. No policy, controller,
role set, or application service names the role inline. Changing the set of
overridden roles is a one-line change in that file.

### Layer 1 — the ASP.NET Core policy catalog

`SystemAdministratorOverrideHandler` is a bare `IAuthorizationHandler`
(not an `AuthorizationHandler<TRequirement>`), registered once in
`AddTigerCsInfrastructure`. ASP.NET Core invokes every registered
`IAuthorizationHandler` on every authorization evaluation and hands it the
whole `AuthorizationHandlerContext`, so succeeding that context's pending
requirements satisfies **every** policy in the catalog — including
requirement types that do not exist yet.

This is what makes future policies automatically safe: a policy added later
for SLA, escalation, reporting, or administration is covered with no change
to the override, the policy catalog, or any controller.

Resource-based requirements are covered by the same evaluation, so
`DepartmentScopedRequirement` passes for any department without its
cross-department role list having to name the role.

### Layer 2 — application-service authorization

Authorization in this system is deliberately enforced at two levels
(`Security-Architecture.md` §3: department scoping is "evaluated against the
ticket's current data, not cached or assumed from the user's session
alone"). The policy layer decides whether a caller may reach an endpoint;
the application services decide the resource-scoped part a policy cannot
see — whether this caller may act on *this* ticket, given its current
department and owner.

`TigerCS.Application.Authorization.AuthorizationGate` is the single point
through which those decisions are evaluated, and the one place the override
is applied to them. An application service passes its own rule to the gate
and uses the answer; it never branches on the override itself. A new
application service written this way includes the override automatically.

`TicketRoleSets` and the policy role lists are deliberately left unchanged:
they continue to record what the **permission matrix** grants each role,
which stays distinct from what the override grants on top of it.

### What the override does not reach

**Business rules.** Everything that is not a permission sits outside both
mechanisms and is reached, unchanged, by an overridden caller: request
validation (`400`), status-transition rules and closed-ticket immutability
(`422`), optimistic concurrency (`409`), database constraints, required
business data, and audit writes. A System Administrator that closes a
ticket that is not yet resolved gets the same `422` as anyone else, and
every action it takes writes the same `AuditEntry` (ADR-0018) attributed to
its own employee id.

**Session validity.** `IIdentityGateRequirement` marks requirements that
establish *who the caller is* rather than *what they may do*. The override
never satisfies them. `ActiveEmployeeRequirement` implements it, so a
deactivated administrator holding an unexpired token is refused exactly as
any other deactivated employee is — `Security-Architecture.md` §14 and
FR-ADM-02's 24-hour revocation requirement would otherwise be defeated by
this ADR. `UserActivationAppService` already refuses to deactivate the last
active System Administrator, so this cannot lock the organization out.
Future identity gates opt out the same way, by implementing the marker.

**Verification-session single-agent ownership.** `MVP-ERD.md` §2.24's rule
that a `VerificationSession` belongs to the agent who created it is a
per-record business invariant, not a permission-matrix cell, and is not
overridden. See "Business rules the override does not reach" below.

### Roles are not changed

No account gains a second role. A System Administrator remains only
"System Administrator"; the nine approved roles (ADR-0004) are unchanged in
name, number, and membership.

## Alternatives Considered

- **Add `Roles.SystemAdministrator` to every policy and role set.** Rejected:
  it is the per-call-site duplication that caused this defect, and it
  guarantees the next policy re-introduces it. It also destroys the
  distinction between what the permission matrix grants and what the
  override grants.
- **Assign the administrator account additional roles (e.g. CS Agent).**
  Rejected explicitly by the correction: it would misattribute operational
  actions in the audit trail, distort role-based reporting, and make the
  administrator indistinguishable from an agent in `TicketAssignment` and
  `AuditEntry` history.
- **A custom `IAuthorizationPolicyProvider` that rewrites each policy to add
  the role.** Rejected: it only reaches policies expressed as role
  requirements, misses the application-layer decisions entirely, and adds a
  policy-construction indirection that is harder to reason about than one
  handler.
- **Override everything, including the active-employee check.** Rejected:
  it would let a deactivated administrator retain full access until token
  expiry, contradicting `Security-Architecture.md` §14 and FR-ADM-02. Full
  authorization does not mean an invalid session becomes valid.
- **A bare `IAuthorizationHandler` plus a central application-layer gate**
  (chosen).

## Advantages

- One definition of the overridden role, and one application point per
  authorization layer — the drift ADR-0005's own Disadvantages section warned
  about ("requires discipline to keep the policy definitions synchronized
  ... a drift between the two would be a silent authorization bug") is
  removed for this role rather than restated.
- Future policies are covered by construction, not by remembering.
- The permission matrix's per-role grants stay readable in code, because the
  override is layered on top of them rather than merged into them.
- The carve-outs are explicit and typed (`IIdentityGateRequirement`), so a
  future requirement opts out deliberately instead of by omission.
- Uses ASP.NET Core's own handler pipeline — no custom middleware, no
  policy-provider indirection.

## Disadvantages

- Reading a policy definition no longer tells the whole authorization story:
  the override is a second thing to know about. Mitigated by cross-references
  in `PolicyNames`, `TicketRoleSets`, and this ADR, and by
  `ProtectedEndpointInventoryTests`, which fails if a protected endpoint has
  no override coverage test.
- A blanket grant is a larger blast radius for a compromised administrator
  account than the previous matrix allowed. This is inherent in the decision,
  not in the mechanism; the audit trail (ADR-0018) is what makes the
  administrator's actions attributable, and it is unaffected.
- The two-layer implementation means an application service that invents its
  own authorization check without using `AuthorizationGate` would not be
  covered. This is a code-review item, called out in Consequences below.

## Consequences

- Every current policy — `AuthenticatedStaff`, `DepartmentScoped`,
  `SupervisorOrAbove`, `DepartmentHeadOrAbove`, `CsManagerOrGeneralManager`,
  `SystemAdministrator`, `CustomerVerification` — admits System
  Administrator, without any of them naming the role.
- Every future policy admits it too, automatically. A policy that must
  *exclude* System Administrator is now the case requiring deliberate work
  (a requirement implementing `IIdentityGateRequirement`, or an explicit
  design decision recorded in a new ADR).
- **New application services must route authorization decisions through
  `AuthorizationGate`.** A hand-rolled role check would not be covered by
  the override. This belongs on the review checklist alongside ADR-0005's
  existing "every endpoint must cite the specific policy it enforces".
- `Security-Architecture.md` §2.1 and §3.1, and `Solution-Analysis.md`
  §4.1's permission matrix, are updated to record this decision.
- The open question `Security-Architecture.md` §3.1 carried — "whether
  General Manager/Chairman-CEO/System Administrator's cross-department reach
  should extend to write actions" — is now answered for System Administrator
  (yes, everywhere) and remains open for General Manager and Chairman/CEO,
  whose matrix rows are untouched by this decision.

## Business rules the override does not reach

Requirement 9 of the correction asks for any endpoint where granting
authorization conflicts with a business rule. One does:

**`GET /api/verification-sessions/{id}`, `POST /api/tickets`, and
`POST /api/tickets/{id}/reconciliation` — verification-session ownership.**
These three consume a `VerificationSession`, which `MVP-ERD.md` §2.24 binds
to the single agent who created it ("no Supervisor+ override at MVP"). The
System Administrator now passes the `CustomerVerification` policy and
reaches all three application services — which is what requirement 1 asks
for — but a session belonging to *another* agent is still refused with
`403`.

This was left as a business rule rather than overridden because overriding
it does not grant access to a feature; it grants the ability to consume
another agent's in-flight verification and attribute the resulting
`TicketRequesterSnapshot` (ADR-0007) to a verification the administrator
never performed. That is an audit-integrity problem, and the administrator
loses no capability: it can create its own verification session and drive
the same flows end to end, which the endpoint tests demonstrate.

**If management wants this overridden too**, it is a one-line change —
route the three `session.IsOwnedBy(...)` checks through `AuthorizationGate`
— but it should be a recorded decision, because it changes what a requester
snapshot means. It is flagged here rather than decided unilaterally.

No other endpoint conflicts. Every other business rule an administrator now
reaches (status transitions, closed-ticket immutability, the
assignment-target department check, duplicate-chain rules, the last-active-
administrator rule, concurrency, validation) applies to it exactly as to any
other role, which is the intended outcome, not a conflict.

## Risks

- **Blast radius.** A compromised System Administrator account can now
  perform every operational action, not only technical administration.
  Mitigations already in place: mandatory audit (ADR-0018), account lockout
  and deactivation-checked-per-request (`Security-Architecture.md` §13/§14,
  which this ADR deliberately does not override), and the fact that role
  assignment itself is administrator-gated. Recommend the pilot
  retrospective review how many accounts hold this role.
- **Separation of duties.** ISSUE-022's Resolve/Close split (the department
  confirms the work; CS confirms the customer knows) is bypassable by a
  System Administrator, since it is enforced as authorization. The audit
  trail records who actually did each step, so the split remains
  *observable* even where it is no longer *enforced* for this role. Flagged
  for the retrospective.
- **Silent scope growth.** Because future policies include the role
  automatically, a future policy intended to exclude it will not do so by
  default. `ProtectedEndpointInventoryTests` makes new protected endpoints
  visible in review, but the intent still has to be stated.
