# Tiger Group — Customer Service Ticketing System
## Security Architecture

| | |
|---|---|
| **Status** | Approved for Architecture Design |
| **Related ADRs** | 0004 (Identity), 0005 (authorization policies), 0013/0014 (Outbox/idempotency), 0017 (attachments), 0019 (Genesys), 0020 (logging/monitoring), **0024 (System Administrator authorization override)** |
| **Date** | 2026-08-17 (amended 2026-08-21 — §2.1 added, §3.1 updated) |

---

## 1. Authentication

Internal staff only, via ASP.NET Core Identity (ADR-0004). No customer-facing authentication exists (ISSUE-021, approved). Password policy and multi-factor authentication configuration are [ASSUMPTION — exact policy not yet specified by management; recommend a minimum of ASP.NET Core Identity's default complexity rules plus account lockout, confirmed before Phase 3].

## 2. Authorization Policies

Policy-based authorization (ADR-0005), one policy per relevant cell of the Solution Analysis §4 permission matrix. Every API endpoint declares its required policy explicitly; there is no "authenticated user can do anything" default. Policies are evaluated server-side on every request — never trusted from client-side UI state alone.

### 2.1 System Administrator Authorization Override — confirmed management decision (2026-08-21)

**Management has confirmed that the System Administrator role must have access to every application feature and every API endpoint.** This supersedes Solution-Analysis.md §4.1's permission matrix, which excluded the role from every operational column (Create, Edit, Assign, Transfer, Escalate, Resolve, Close, Reopen, Cancel, Reject) and which the implementation had followed literally — producing `403 Forbidden` on, among others, `POST /api/intake-records`. Recorded formally in **ADR-0024**, which amends ADR-0005.

**Implemented as one central mechanism per authorization layer, not per endpoint.** The overridden role is defined once (`AuthorizationOverride`); no policy, controller, role set, or application service names it inline:

- **Policy layer.** `SystemAdministratorOverrideHandler` is a bare `IAuthorizationHandler`, registered once. ASP.NET Core runs it against every authorization evaluation and hands it the whole handler context, so it satisfies every policy in the catalog — including requirement types and policies that do not exist yet. **A future SLA, escalation, reporting or administration policy includes the role automatically**, with no change to the override, the policy catalog, or any controller. Resource-based requirements (`DepartmentScopedRequirement`) are covered by the same evaluation.
- **Application-service layer.** The resource-scoped decisions a policy cannot see — whether this caller may act on *this* ticket, given its current department and owner (§3) — run through `AuthorizationGate`, the single point at which the override is applied to them. A service passes its own rule to the gate and uses the answer; it never branches on the override itself.

**This is an authorization override and nothing else.** A System Administrator still obeys, unchanged: request validation, ticket status-transition rules, closed-ticket immutability, optimistic-concurrency control, database constraints, required business data, and the audit requirements of §8/ADR-0018 — every action it takes writes an `AuditEntry` attributed to its own employee id. All of these are enforced downstream of every authorization decision and are reached, not bypassed.

**Three deliberate carve-outs.** Full authorization does not mean an invalid session becomes valid, and it does not dissolve per-record business invariants:

1. **Session validity is never overridden.** `IIdentityGateRequirement` marks requirements that establish *who the caller is* rather than *what they may do*; the override never satisfies them. `ActiveEmployeeRequirement` implements it, so **a deactivated System Administrator holding an unexpired token is still refused** — §14 and FR-ADM-02's 24-hour revocation requirement would otherwise be defeated by this decision. The identity module already refuses to deactivate the last active System Administrator, so this cannot lock the organization out. Future identity gates opt out the same way.
2. **The framework's authenticated-user gate is never overridden.** An anonymous caller is rejected before any of this runs; §5 is unaffected.
3. **Verification-session single-agent ownership (MVP-ERD.md §2.24) is not overridden.** See ADR-0024's "Business rules the override does not reach" — the administrator reaches all three affected endpoints, but cannot consume another agent's in-flight verification session, because doing so would attribute a `TicketRequesterSnapshot` to a verification it never performed. Flagged for management rather than decided unilaterally.

**No role is added to any account.** A System Administrator remains only "System Administrator" — the nine approved roles (ADR-0004) are unchanged in name, number, and membership, and the role lists inside the policies still record what the permission matrix grants each role, as distinct from what the override grants on top of it.

**Tested** (ADR-0021, and §15 below): a System Administrator JWT is proven authorized for every currently protected API endpoint/action, with `ProtectedEndpointInventoryTests` reading the host's real endpoint table so a newly-added protected endpoint fails the suite rather than going untested. The corresponding negative tests prove Reporting User and the other unauthorized roles still receive `403` across the same surface, and that department scoping and the ISSUE-022 Resolve/Close split are otherwise unchanged.

**Residual risk, flagged for the pilot retrospective.** ISSUE-022's separation of duties (the department confirms the work; CS confirms the customer knows) is enforced as authorization, so it is bypassable by a System Administrator. The audit trail records who performed each step, so the split remains *observable* where it is no longer *enforced* for this role. The number of accounts holding the role is the practical control.

## 3. Department Data Scoping

A Department Employee/Head's effective access is scoped to `CurrentDepartmentId` matching their own `Employee.DepartmentId` for department-specific actions (Resolve, view own-department queue). CS-layer roles (Geyness Agent, Supervisor, CS Manager) are scoped differently — across all departments for Close/Reopen actions, per the approved ISSUE-022 split. Scoping is enforced in the policy handler, evaluated against the ticket's current data, not cached or assumed from the user's session alone.

### 3.1 Department Visibility Boundaries — as implemented (Identity and Access increment)

The `DepartmentScoped` authorization policy (`TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization.DepartmentScopedRequirement`) is the mechanism enforcing the paragraph above, ahead of any endpoint actually consuming it (no Ticketing endpoint exists yet in this increment). Its boundary, as implemented and tested (`DepartmentScopedAuthorizationTests.cs`):

- **Own-department access:** an employee is authorized for any department they hold a `UserDepartmentAssignments` row for (primary or otherwise) — re-queried from the database on every request via `DepartmentClaimsTransformation`, never taken from a claim baked into the JWT at login. A department transfer with no new login takes effect on the very next request.
- **Cross-department access, confirmed:** **CS Agent, CS Supervisor, and CS Manager** — this §3's own citation of "CS-layer roles (Geyness Agent, Supervisor, CS Manager) ... scoped across all departments" (role names per ADR-0004's Pilot Role-Naming Decision) — are authorized for *any* department regardless of membership, matching the approved ISSUE-022 split.
- **Cross-department access, broader read grant:** **General Manager, Chairman/CEO, and System Administrator** are also authorized for any department under this same policy, on the separate basis of Solution-Analysis.md §4.1's permission matrix, which gives each of them "View: All tickets" — a read-level grant that predates and is broader than §3's Close/Reopen-specific carve-out.
- **Not cross-department:** Department Employee and Department Head are scoped strictly to their own department assignments — confirmed by `DepartmentHead_IsNotACrossDepartmentRole_CannotAccessOtherDepartment`.
- **Reporting User** is not included in `DepartmentScoped`'s cross-department set; §4.1 scopes that role to reports/dashboards only, not per-ticket/per-department data.

**Approved for this increment; one point still open before a Ticketing endpoint consumes this policy for write actions:** whether General Manager/Chairman-CEO/System Administrator's cross-department reach should extend to *write* actions (assign, close, reopen), or stay read-only as §4.1's citation literally supports. The read-level boundary above is what's implemented and tested today.

**Resolved for System Administrator only (2026-08-21, §2.1/ADR-0024):** that role's cross-department reach now extends to every action, read and write, via the central override rather than through this policy's role list — which is unchanged. The question stays open for **General Manager** and **Chairman/CEO**, whose §4.1 rows this decision does not touch: both remain read-level cross-department here. Department Employee and Department Head remain scoped strictly to their own department assignments, and Reporting User remains outside the cross-department set entirely.

## 4. Protection of Customer and Unit Data

- Unit/contact data is never locally mastered (ADR-0006) — reducing the surface area of data that could be exposed if the ticketing system's database were compromised, since the CRM remains the authoritative record.
- Multi-contact disclosure follows the approved rule (ISSUE-007): disclosure only to the CRM-verified ticket requester or an explicitly authorized representative; no tenant/owner cross-sharing by default.
- Genesys caller numbers are treated as personal data — masked in logs (Section 11) and access-restricted to roles that need them for active call handling.

## 5. API Authentication

All internal API endpoints require an authenticated Identity session/token; no anonymous endpoint exists except the health-check surface used for monitoring (ADR-0020), which returns only aggregate status, not business data.

## 6. Genesys Webhook Security

Every inbound Genesys webhook must pass signature/authentication validation (exact mechanism is an open question for the Genesys team — see `Genesys-Integration.md` §15) before any processing. A failed validation is rejected (not silently ignored) and logged to `AuditEntry` with the correlation ID and source IP, without logging the full unvalidated payload if it might contain unverified/untrusted personal data. Repeated validation failures from the same source should trigger an operational alert (ADR-0020), since this could indicate a spoofing attempt.

## 7. File-Upload Security

Every `TicketAttachment` upload is virus-scanned before being made available (ADR-0017); a failed scan blocks the file rather than silently discarding or serving it. File type/size limits are enforced at the application layer (10 attachments/ticket confirmed; 25MB/file size cap is an [ASSUMPTION] pending confirmation). Uploaded files are stored in object storage referenced by an opaque `StorageReference`, never a predictable/guessable path, to prevent unauthorized direct access.

## 8. Audit Immutability

`TicketStatusHistory` and `AuditEntry` are append-only: the application layer provides no update or delete path for either table (ADR-0018). This is enforced in code (no `Update`/`Delete` method exists on their repositories), not merely by convention — a reviewer should treat any proposed update/delete path on these tables as a defect.

## 9. Secrets Management

CRM API credentials, Genesys authentication material, Office 365 email credentials, and the database connection string must be stored in a secrets manager or environment-based configuration external to source control — never committed as plaintext. [ASSUMPTION — exact secrets-management tooling (e.g., Azure Key Vault, environment variables via the hosting platform) depends on the still-unconfirmed hosting target, ADR-0022.]

## 10. Encryption in Transit and at Rest

- All client-to-application and application-to-external-system traffic uses TLS (HTTPS).
- Database encryption at rest [ASSUMPTION — SQL Server Transparent Data Encryption or equivalent, pending confirmation of the specific hosting environment].
- Object storage for attachments uses the provider's encryption-at-rest capability.

## 11. Logging Without Exposing Personal Information

Structured logs (ADR-0020) must mask or omit: full caller numbers (log a truncated/masked form, e.g., last 4 digits only), full contact display names in non-essential log lines, and any attachment content. Correlation IDs, ticket IDs, and non-personal metadata are logged freely, since they support the audit/monitoring requirements without exposing personal data. This rule applies uniformly across Notifications, Genesys Integration, and CRM Verification logging.

## 12. Rate Limiting

[ASSUMPTION — not yet specified by management] Recommend basic rate limiting on the Genesys webhook endpoint (to absorb a redelivery storm without degrading the rest of the application) and on the staff login endpoint (to slow brute-force attempts, complementing account lockout below). Exact thresholds are a Phase 3 configuration detail, not an architectural decision requiring management approval at this stage.

## 13. Account Lockout

ASP.NET Core Identity's built-in lockout mechanism (a configurable number of failed attempts within a window) is enabled for staff accounts, consistent with the 24-hour access-revocation requirement (FR-ADM-02) for departing staff — lockout handles the "too many failed attempts" case, while explicit deactivation (Identity and Access module) handles the "this person no longer works here" case.

## 14. Session Management

Staff sessions/tokens expire after a configurable inactivity period [ASSUMPTION — exact duration not yet specified; recommend a value consistent with typical call-center shift patterns, confirmed before Phase 3]. A deactivated `Employee` (ADR-0004) cannot obtain a new session even if their prior token has not yet expired — deactivation is checked on every request, not only at login.

## 15. Security Testing

Per the automated testing strategy (ADR-0021), security-relevant logic — authorization policy handlers, the §2.1 authorization override and its carve-outs, Genesys webhook signature validation, department data scoping — must have dedicated unit-test coverage, not rely solely on manual QA. A focused security review (not a full penetration test, given the 3-week pilot timeline) should occur before pilot go-live, covering at minimum: authorization bypass attempts, webhook signature bypass attempts, and confirmation that no customer-facing endpoint exists anywhere in the deployed surface.
