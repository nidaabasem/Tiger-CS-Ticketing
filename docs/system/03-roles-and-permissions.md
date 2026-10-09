# 03 - Roles and Permissions

Derived from code at working tree HEAD `31878f4` (main `a1cba71` plus 3 cherry-picked CRM-document/OTP commits). Paths are relative to `src/`. The code is the source of truth; anything marked **(unverified)** or **(proposed)** is not enforced by code.

## 1. Fixed role set

`TigerCS.Domain/Modules/IdentityAndAccess/Roles.cs:13-21` defines nine roles, seeded by `TigerCS.Infrastructure/Modules/IdentityAndAccess/Seed/DevSeedData.cs:209-225`:

CS Agent, CS Supervisor, Department Employee, Department Head, CS Manager, General Manager, Chairman/CEO, System Administrator, Reporting User.

Roles are assigned only by a System Administrator (`AdminUserAppService.SetRolesAsync`, `TigerCS.Application/Modules/Administration/Services/AdminUserAppService.cs:211`); a user can hold several roles; unknown role names are rejected; the last active System Administrator cannot lose the role or be deactivated.

**There is no "Call Center Agent" role and no "AI Agent" role** (see section 6). The `Roles.Descriptions` text (`Roles.cs:37-48`) is partly stale (see section 9).

## 2. Enforcement layers

1. **API policy catalog** - `TigerCS.Infrastructure/InfrastructureServiceCollectionExtensions.cs:395-455`. Every policy starts from `Base()` = authenticated user + `ActiveEmployeeRequirement` (employee active AND JWT security-stamp equals current stamp, `Authorization/ActiveEmployeeRequirement.cs:308-336`). Fallback policy = `Base()` (no anonymous endpoint except login, screen-pop redeem, health).
2. **Application services** - resource-level checks (ownership, department membership) through `AuthorizationGate` (`TigerCS.Application/Authorization/AuthorizationGate.cs:57-76`), role sets in `TicketRoleSets.cs` and `SlaRoleSets.cs`.
3. **Web** - `Program.cs:15-31` (`AuthorizeFolder("/")`, `/Admin` = System Administrator, `/Reports` = `ReportsPolicy`). Display-only mirrors: `TicketCreationPolicy`, `AdministrationPolicy`, `ReportsPolicy`, `TicketActions.cs`. The API is the enforcement point.

### Central override (ADR-0024)
`AuthorizationOverride.OverrideRole = System Administrator` (`TigerCS.Domain/.../AuthorizationOverride.cs:121-138`). Policy layer: `SystemAdministratorOverrideHandler` succeeds every pending requirement except `IIdentityGateRequirement` (active employee + stamp) and the anonymous-deny requirement (`SystemAdministratorOverrideHandler.cs:194-220`). Application layer: `AuthorizationGate.Evaluate*` short-circuits to true. The override does **not** bypass: status-transition rules, closed-ticket immutability, reopen window, concurrency, validation, audit, per-record verification-session ownership (`VerificationSessionAppService.cs:204-208`), and department-membership invariants of an assignee (`AgentHandoffAppService` accept).

## 3. API policies (`PolicyNames`)

| Policy | Roles allowed (plus System Administrator via override) | Defined at |
|---|---|---|
| AuthenticatedStaff | any active authenticated employee | `InfrastructureServiceCollectionExtensions.cs:409` |
| DepartmentScoped | member of resource department OR CS Agent, CS Supervisor, CS Manager, General Manager, Chairman/CEO | `:411-429` |
| SupervisorOrAbove | CS Supervisor, CS Manager, General Manager, Chairman/CEO | `:431` |
| DepartmentHeadOrAbove | Department Head, CS Manager, General Manager, Chairman/CEO | `:435` |
| CsManagerOrGeneralManager | CS Manager, General Manager, **Chairman/CEO** (name is misleading) | `:439` |
| SystemAdministrator | System Administrator only | `:443` |
| CustomerVerification | **CS Agent, CS Supervisor** only | `:450` |

## 4. Endpoint groups and their policy

| Group (controller) | Policy / rule |
|---|---|
| `api/admin/*` (users, departments, request types, workflows, channels, SLA config, Genesys queue mappings), `api/roles`, `PATCH api/users/{id}/activation` | SystemAdministrator |
| `api/reports/team-performance[/records]` (`ReportsController.cs:20`) | CsManagerOrGeneralManager |
| `GET api/users/assignable` (`UsersController.cs:37`) | CsManagerOrGeneralManager |
| `POST api/tickets`, `POST tickets/{id}/reconciliation`, customer search/lookup, intake records, `api/crm/*`, `api/verification-sessions`, `api/genesys/*` (tickets, customers, agent-context, screen-pop), **`api/genesys/verification/*` (buyer-lookup, otp/send, otp/resend, otp/verify)**, **`api/genesys/documents/send-copy`**, `api/collections/customer-lookup/payment-summary` | CustomerVerification (CS Agent, CS Supervisor, System Administrator) |
| Remaining `api/tickets/*` reads/writes, SLA, escalations, approvals, notes, dashboard, pending-customer-interactions, categories, request-types, customers directory/profile/history | AuthenticatedStaff, then service-level role/department checks (section 5) |
| `api/collections/*` (receivables, campaigns, customer payments, reminders), Genesys collections | AuthenticatedStaff, then `CollectionsAuthorizationService` (section 7) |
| `api/auth/login`, `screen-pop/redeem` | Anonymous (credentials / one-time token) |
| `api/auth/change-password`, `logout` | AuthenticatedStaff / any |

New in the cherry-picked commits: `GenesysVerificationController` (`TigerCS.Api/Controllers/GenesysVerificationController.cs:27-30`) and `GenesysDocumentsController` (`GenesysDocumentsController.cs:34-37`) use `CustomerVerification` at class level; there is no per-record role check beyond that. A CS Manager, General Manager, Chairman/CEO, Department Employee/Head and Reporting User receive 403 on them; the integration service account must therefore be a **CS Agent** (or CS Supervisor) account (`docs/Genesys/Genesys-Cloud-Configuration.md:75`). Verification sessions are additionally single-agent owned: only the creating employee may read one, with no supervisor override (`VerificationSessionAppService.cs:204-208`).

## 5. Ticket action matrix

Legend: Y = allowed by role; O = only if current owner; D = only if member of ticket's current department; V = all departments; "-" = refused. System Administrator passes every row through the override (state rules still apply).

| Action | CS Agent | CS Supervisor | CS Manager | Dept Employee | Dept Head | GM | Chairman/CEO | Reporting User | Source |
|---|---|---|---|---|---|---|---|---|---|
| View ticket / queue / dashboard scope | V | V | V | D (own depts) | D | V | V | D (own depts, if any) | `TicketRoleSets.cs:38`, `TicketVisibilityRule.cs:31-43` |
| Create ticket | Y | Y | **-** | - | - | - | - | - | CustomerVerification policy; `TicketCreationPolicy.cs:14` |
| Classify unclassified ticket | Y (any dept) | Y | Y | D | D | Y | Y | - | `TicketClassificationAppService.cs:86-90` |
| Change status (Open->InProgress, InProgress<->PendingCustomer) | O | Y | Y | O | D or O | Y | Y | - | `TicketLifecycleAppService.cs:893-909` |
| Assign / reassign | - | D | V | - | D | - | - | - | `TicketRoleSets.cs:66,74` |
| Transfer department | - | - | Y | - | - | - | - | - | `TicketRoleSets.cs:81` |
| Resolve | - | - | - | O | D | - | - | - | `TicketRoleSets.cs:84`, `TicketLifecycleAppService.cs:912-927` |
| Close | Y | Y | Y | - | - | - | - | - | `TicketRoleSets.cs:87` |
| Reopen (direct) | Y + can view dept | Y | Y | - | - | - | - | - | `TicketRoleSets.cs:127`, `ReopenAsync:480-491` |
| Request Reopen Approval | - | - | - | D | D | Y | Y | - | `TicketApprovalAppService.cs:86,103` |
| Decide approvals | by approval target snapshot (role / employee / department member with Dept Employee or Dept Head) | | | | | | | | `TicketApprovalAppService.cs` (`IsAuthorizedApproverAsync`) |
| Record First Response (manual API) | O | Y | Y | O | D or O | Y | Y | - | `SlaRoleSets.cs:159`, `SlaFirstResponseAppService.cs:113-129` |
| Manual escalation Level 1-3 | Y | Y | Y | Y (own dept/owner) | Y (own dept) | Y | Y | - | `SlaRoleSets.cs:173` |
| Escalate Level 4 | - | - | Y | - | - | Y | - | - | `SlaRoleSets.cs:188` |
| Accept pending customer interaction | member of ticket dept | same | same | same | same | same | same | - | `AgentHandoffAppService.cs` (membership precondition) |
| Team Performance report | - | - | Y | - | - | Y | Y | - | `ReportsController.cs:20` |
| Admin console | System Administrator only | | | | | | | | `Program.cs:26` |

Notes:
- CS Agent cannot self-assign, transfer or resolve. Resolve belongs to the department side.
- Priority change after classification, SLA pause and downgrade approval are **not implemented** (section 8 of doc 04).

## 6. Call Center Agent, AI Agent and service accounts

### Call Center Agent
Not a role. Per `TeamPerformanceAppService.cs:15-19,158-162`, a Call Center Agent is an employee with role **CS Agent** who is a member of the department with code `CC` ("Call Center", seeded `WorkflowReferenceData.cs:49`). `docs/Genesys/Genesys-Cloud-Configuration.md:143` states the same.

**Equal to CS Agent?** Role permissions: yes, identical by construction (same role). Differences, all consequences of department membership rather than role:
1. Department-membership checks (accept pending interaction, being an assignment target, Department-scoped lookups) are evaluated against `CC` membership, so a CC member can accept/own only tickets whose current department they belong to.
2. Team Performance labels them "Call Center Agent" (filter `AgentType`).
3. Customer-lookup source narrowing is per department (`DepartmentCustomerLookupSources`), so CC may see different lookup sources if configured **(configuration-dependent)**.
No code path grants or withholds a ticket permission by CC membership.

### AI Agent
Not a role, not an employee, not an authenticated principal. The AI/virtual agent is represented only as a transcript sender `InteractionMessageSender.VirtualAgent` (`TigerCS.Domain/Modules/Ticketing/TicketInteractionMessage.cs`) and as handoff triggers (`CustomerRequestedHuman`, `AiConnectionLost`, `AiEscalated`). Everything it "does" reaches TigerCS through the **Genesys integration service account**, which is a CS Agent account (`Genesys-Cloud-Configuration.md:75`). Therefore effective AI permissions = CS Agent (policy `CustomerVerification` + AuthenticatedStaff). Differences from a human CS Agent, all by design in code:
1. Cannot record First Response; VirtualAgent lines are ignored (`GenesysConversationEndAppService.cs:227-259`, `FirstHumanResponseRecorder.cs:38-46`).
2. Chatbot-inactivity closure is a system action (`actorIsSystem: true`, audit actor = integration account) and only for tickets with **no owner** and no open handoff (`ChatbotInactivityCloseAppService.cs:114-118`, `Ticket.CloseForCustomerInactivity` `Ticket.cs:746`).
3. Auto-assignment writes null actor (system), not an employee (`TicketAutoAssignmentService.cs`).
4. It is excluded from Team Performance (not an employee in role CS Agent unless the service account is, in which case it appears as a CS Agent row **(environment-dependent)**).
5. The service account can in principle perform any CS Agent action (close, reopen) if credentials are used directly; there is no separate least-privilege scope for it. See audit finding F-8.

### Service accounts
- Genesys integration account (CS Agent): `api/genesys/*`. Optional additional Collections grant: `Collections:Authorization:IntegrationEmployeeIds` (`CollectionsOptions.cs:86`) enables reporting outcomes / queuing VoiceBot reminders. Config default is an empty list (`Api/appsettings.json`).
- Genesys auth to TigerGroupWeb uses a separate client-credentials scope `ticketing.genesys` (outside this repo's API auth).
- Genesys Screen Pop: anonymous one-time token (1 h, single use) redeemed to an ordinary session of the mapped agent; re-checks mapping/active/lockout (`AuthController.cs:96-160`).

## 7. Department scope, Collections and reporting

- **Department scope**: cross-department view for CS Agent, CS Supervisor, CS Manager, GM, Chairman/CEO, System Administrator (`TicketRoleSets.CrossDepartmentView`). Department Employee/Head and Reporting User see only departments in their `UserDepartmentAssignments` (`TicketQueryAppService.ResolveVisibleDepartmentIdsAsync:193-203`). Dashboard, queue, pending interactions and Team Performance use the same primitive; filters can only narrow.
- **Collections** (`CollectionsAuthorizationService`, `Application/Modules/Collections/Services/CollectionsCore.cs:65-90`, options `CollectionsOptions.cs:73-87`, all config-overridable):
  - Read financials: CS Agent, CS Supervisor, CS Manager, GM, Chairman/CEO; Department Employee/Head only if member of the Collections department (`COL`); integration accounts; System Administrator.
  - Send/queue reminders and export campaigns: CS Supervisor, CS Manager, Collections-department Employee/Head, integration accounts, System Administrator.
  - Report outcomes: integration accounts (and System Administrator) only.
  - **Navigation**: the "Collections" nav item is shown to every signed-in user (`Pages/Shared/_Nav.cshtml:14`) and the pages are only `AuthorizeFolder("/")`; authorization happens in the API, and the page renders "Your account does not have permission..." on 403 (`Pages/Collections/Receivables.cshtml:126`, `Campaigns.cshtml:114`). So the link is visible but the content is denied for e.g. Reporting User or non-Collections Department staff.
- **Reports**: Team Performance requires CS Manager, GM, Chairman/CEO (Web adds System Administrator). Eligible rows = active employees with role CS Agent only (`TeamPerformanceAppService.cs:49`); Call Center agents are included (type label), supervisors, department staff and AI are not.
- **Reporting User**: holds no permission in any role set or policy. It cannot reach `/Reports` or `api/reports`; it effectively only gets the Dashboard/queue scoped to its own departments.

## 8. Administration, configuration, audit

Admin console (`Pages/Admin`, System Administrator only; five modules `AdminModules.cs`): Users, Departments, Request Types (assignment rule, approval requirements, SLA policies), Workflows (versioned designer), Channels. Every API write writes an audit entry (`AuditEntryWriter`). **Gaps**: Genesys queue->department routing mappings are API-only (`api/admin/genesys/queue-mappings`), no Web page; there is **no audit-history read endpoint or screen** (audit is write-only; ticket history and approvals are viewable on Ticket Details). SLA configuration is read-only (`GET api/admin/sla/configuration`).

## 9. Identity flows

- **Login** `POST api/auth/login` -> 200 token, 401 invalid, 423 locked. Policy: 8+ chars with upper/lower/digit/symbol, 4 unique chars; lockout 5 failures / 15 min (`InfrastructureServiceCollectionExtensions.cs:80-89`). Web: `Pages/Login.cshtml.cs` (email or employee ID, remember-me, cookie 60 min non-sliding, bound to JWT expiry).
- **Access denied**: `/AccessDenied` (`Program.cs` cookie options), reached when an authenticated user fails a Web policy.
- **Change own password**: `POST api/auth/change-password`; rotates security stamp so all sessions end; Web `Pages/Account/ChangePassword`.
- **Reset password**: admin only (`POST api/admin/users/{id}/password`, `AdminUserAppService.ResetPasswordAsync:324`); clears lockout, rotates stamp, audited. There is **no self-service "forgot password"** flow.
- **Account management**: create, edit profile, activate/deactivate (history-referenced users are never deleted), roles, departments (all System Administrator).
- **Deactivation** is enforced on every request (token stamp + active check), including for System Administrator.

## 10. Stale descriptions to correct (documentation only)

`Roles.Descriptions` says CS Manager can "manage user/role assignment" (it cannot; System Administrator only), Chairman/CEO is "read-only" (code grants it status change, classify, first-response and Level 1-3 escalation through `CrossDepartmentSupervisory`, see audit F-5), Reporting User has "read-only access to reports" (no report policy admits it, F-6), and CS Supervisor can "view team reports" (Reports API excludes it).


## 10. Changes since the first review (implemented)

* **Chairman/CEO is read-only on the server.** It was removed from status change, classification, first-response recording, manual escalation, approval operations and from the `SupervisorOrAbove`, `DepartmentHeadOrAbove` and `CsManagerOrGeneralManager` policies. A new `ReportsRead` policy keeps report reads. `GET api/users/assignable` is CS Manager/GM only. A guard (`Roles.IsReadOnlyCaller`) refuses any mutating HTTP method for callers holding only Chairman/CEO and/or Reporting User; the same check protects notes, handoff accept/complete/cancel and approval decisions; UI helpers (`TicketActions`) hide write controls. Exception kept by decision (Solution-Analysis §4.1): Chairman/CEO may *request* a Reopen Approval.
* **Service identity separated from human roles.** `Authorization:ServiceIdentity:EmployeeIds` (unioned with `Collections:Authorization:IntegrationEmployeeIds`) lists integration accounts. Listed accounts reach only `CustomerVerification`-policy routes (`api/genesys/*`, verification sessions), `api/genesys/collections`, `users/me`, logout/change-password; every other route returns 403. They are excluded from Team Performance and assignee directories. No administrator access is granted. **Deployment requirement:** the Genesys/TigerGroupWeb service account must be listed there, otherwise it is treated as a human CS Agent. Not yet blocked: such an account being *assigned* a ticket by id outside the directory.
* **Call Center Agent / AI Agent** remain aligned with CS Agent by construction (Call Center = CS Agent in department `CC`; the AI/Genesys path is the CS Agent service account). No distinct roles were invented; a distinct AI-agent identity does not exist (same Genesys account).
* **Verified without change:** CS Manager sees CS Agents and Call Center agents with workload/performance in Team Performance and the dashboard picker; pending-interaction *accept* is member-only by design and cross-department hand-over uses Transfer.
* **Tests:** `ReadOnlyRolesAndServiceIdentityEndpointTests` (17 HTTP tests incl. Department Employee/Head cross-department denial: 403 on read and write; 404 only for a missing ticket), `ReadOnlyViewerActionTests`, `AssignableUserAppServiceTests`, `PendingInteractionRoleScopeTests`.
* Policy rows in §3 above that list Chairman/CEO for `SupervisorOrAbove`, `DepartmentHeadOrAbove` and `CsManagerOrGeneralManager` are **superseded** by this section.
