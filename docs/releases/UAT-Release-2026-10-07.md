# UAT release — 7 October 2026

Branch: `claude/dazzling-cerf-98fvtz` (base: `main` at `f98ab5e` plus the
PRs #68–#70 and app-settings commits already on the branch).
Release package: `publish/` (framework-dependent IIS output, API + Web) and
`publish.zip`, both regenerated from this branch. The API assembly's
informational version names the exact source commit (`1.0.0+<sha>`).

Build and tests on the release head: `dotnet build TigerCS.slnx -c Release`
0 errors; `dotnet test` **2855 passed, 0 failed, 0 skipped** (baseline
before this release: 2790). CI (`.github/workflows/ci.yml`) runs the same
commands on push.

## What is in it

| # | Item | Status | Where |
|---|---|---|---|
| 1 | CS Manager → Team Performance | Implemented, tested (SQLite query tests, API authorization, Razor render) | Web `/Reports/TeamPerformance` (nav "Team Performance" for CS Manager / GM / Chairman-CEO / SysAdmin); API `GET api/reports/team-performance`, `GET api/reports/team-performance/records`; `docs/Team-Performance-Report.md` defines every metric |
| 2 | Genesys tickets under Finance | Root cause established in code (no fallback exists; the environment's queue mapping or explicit Finance id/code is the cause); traceability added; investigation SQL + reviewed correction script delivered | `docs/Genesys/uat-routing/` (README, `01_Investigate…sql`, `02_Correct…ps1`). The `GenesysInquiryIngested` audit entry now records `DepartmentSource` and the raw routing inputs |
| 3 | Explain SLA in the UI | Implemented, tested | Ticket Details → SLA Information (applied priority/policy, targets, clock basis + calendar, clock start, due times, exact "why not started" reason, first-response and pause rules); Admin → Request Types → "How the SLA is applied" (effective per-priority policies + calendar, read-only); API `GET api/tickets/{id}/sla` (`explanation`), `GET api/admin/sla/configuration` |
| 4 | Password management | Implemented, tested | Admin → Users → Edit User → Reset password (`POST api/admin/users/{id}/password`); account menu → Change my password (`/Account/ChangePassword`, `POST api/auth/change-password`). Identity validators enforce the policy; audit `AdminResetUserPassword` / `ChangeOwnPassword` carry no secrets; the JWT now carries the Identity security stamp (`sst`) and every request checks it, so a reset/change ends all of that user's sessions |
| 5 | CS Manager — empty Assign dropdown | Implemented, tested | Ticket Details → Assign: CS Manager gets the whole directory grouped by department (ticket's own department first; multi-department users under each; names, roles, departments); choosing another department = transfer-and-assign with a required reason (`POST api/tickets/{id}/transfer` with `assignToEmployeeId`); a failed/forbidden member-list call is shown as such, never as "No active members"; API `GET api/users/assignable` |
| 6 | Dashboard layout | Implemented, tested | Filters + KPI cards → Volume by Channel, Volume by Request Type, Open Backlog Ageing, Priority → ticket table; existing responsive grid rules keep one column under 720 px |
| 7 | Pending Interactions actions | Implemented, tested | Buttons: Open Ticket · Accept & Start · Complete Follow-up · Cancel Follow-up (reason required); "What do these actions do?" help block; "session ended" tooltip states the follow-up is not completed by it |
| 8 | Chatbot/voicebot — verified customer's unit and project details | Implemented, tested. **CRM supplies only part of the fields today** (below) | API `POST /api/genesys/customers/unit-details` (Genesys service-account auth, same proxy pattern); `docs/Genesys/Customer-Unit-Details-API.md` (request, samples, errors, field sources); Data Action `docs/Genesys/data-actions/10-customer-unit-details.json` |

### Item 8 — what UAT will and will not show

* **Works now, from CRM's buyer lookup:** unit id/number, floor, unit-type *code*,
  booking reference (CRM Lead id) and status, project id and name. Eligible-unit
  list when no unit is selected; `403 UNIT_NOT_ELIGIBLE` / `CUSTOMER_NOT_VERIFIED`
  for another customer's unit or customer; `409`/`502` for ambiguous/unavailable CRM.
* **Returns `null` until CRM publishes the data:** tower, bedrooms, area and its
  unit, parking, unit-type name, project address/status/description/amenities,
  and expected/actual handover dates at unit and project level. The CRM schema
  and `GetBuyerByPhone` carry none of these, so none were invented
  (`ICrmUnitDetailsGateway` → `UnimplementedCrmUnitDetailsGateway`;
  `detailsStatus: "NotAvailable"`). **Open dependency: CRM team to expose them.**
* **TigerGroupWeb must forward the new route** before the Genesys Data Action
  can reach it (the proxy forwards a fixed route list).
* No migration and no new configuration key.

Not part of this branch: the chatbot-timeout and document-copy APIs. They are
not in this repository (no code or docs on any branch), so they are not
described here; add their rows when that work lands.

Nothing in this release changes approved SLA durations, working hours,
pause rules or request-type policy precedence, and nothing changes the
Genesys routing rule (explicit `departmentId` → explicit `departmentCode` →
queue mapping → 422).

## Database

**No new migration.** `dotnet ef migrations has-pending-model-changes`
reports no model changes; the latest migration remains
`20261005072511_AddCollectionsReminders`. The UAT database must already be
at that migration (idempotent script `AddCollectionsReminders.sql` at the
repository root; `docs/Collections/Collections-Integration.md` §8 notes it
had not been applied anywhere when written — check `__EFMigrationsHistory`
first). No seed or data script is required for items 1, 3–7.

Item 2 scripts (run by hand, after review): `docs/Genesys/uat-routing/01_Investigate_Finance_Routing_UAT.sql`
(read-only) and `02_Correct_Misrouted_Tickets_UAT.ps1` (per-ticket transfer
through the API from a reviewed CSV; never bulk).

## Configuration

No new configuration keys. Existing environment variables stay as in
`docs/DEV-SETUP.md` (`ConnectionStrings__TigerCsDatabase`, `Jwt__SigningKey`,
`Crm__SecretKey`, `PactApi__ApiKey`, `EmailNotifications__Password`).
Committed `appsettings*.json` in `publish/` are unchanged placeholders.

**Behavioural change to plan for:** access tokens issued before this
deployment have no security-stamp claim and are rejected once the new API
is live. Every signed-in user must sign in again after the deployment
(cookie sessions expire within 60 minutes anyway). Genesys Screen Pop tokens
issued by the new build carry the stamp.

## Deployment steps (established IIS workflow)

1. Confirm the UAT database is at `20261005072511_AddCollectionsReminders`
   (`SELECT TOP 1 MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC`).
   If not, run `AddCollectionsReminders.sql` (idempotent) in a reviewed window.
2. Stop the two IIS applications (TigerCS.Api, TigerCS.Web) or recycle after copy.
3. Copy `publish/TigerCS.Api/*` and `publish/TigerCS.Web/*` from this branch
   (or unzip `publish.zip`) over the current site folders. Do not overwrite
   environment variables / `web.config` `environmentVariables` the server
   carries; the committed `web.config` has none.
4. Start the applications. Check `GET /health` on the API and sign in on the Web.
5. Run `docs/Genesys/uat-routing/01_Investigate_Finance_Routing_UAT.sql`,
   fix the routing cause it names (queue mapping in Admin → Genesys routing,
   or the Architect flow's `departmentCode`), then correct the reviewed
   tickets with `02_Correct_Misrouted_Tickets_UAT.ps1 -WhatIf` first.

Rollback: redeploy the previous `publish/` (commit `9c450b6`); no schema
change to reverse. Tokens issued by the new build are accepted by the old
build (extra claim ignored).

## Verification checklist per account

System Administrator
- Admin → Users → Edit User: reset a test user's password with a weak value
  (expect the policy errors inline), then a valid one; sign in as that user
  with the new password; the user's previous session is rejected.
- Admin → Request Types → open one → "How the SLA is applied" shows the
  four per-priority policies and the Asia/Dubai calendar.
- Audit entries `AdminResetUserPassword` contain no password.

CS Manager
- Reports → Team Performance: CS Agent and Call Center Agent rows appear
  (Call Center Agent = CS Agent with a membership in department code `CC`),
  zero-activity agents included; filters by employee, role, department, dates;
  each count opens the record list.
- Ticket Details → Assign on a ticket in a department with no members: the
  directory lists every department; pick a user in another department with a
  reason → the ticket moves and is assigned; the Originating department is
  unchanged. Pick a user without a reason → validation message.
- Transfer a reviewed TG-FIN ticket to its intended department via the
  script or UI; history shows Transfer (+ Assign).

CS Agent and Call Center Agent
- Account menu → Change my password: wrong current password is refused; a
  valid change signs you out and the new password works.
- Tickets → Pending Interactions: labels Accept & Start / Complete Follow-up /
  Cancel Follow-up, help block; Accept & Start on an Open ticket moves it to
  In Progress, the SLA panel still shows no first response.
- Ticket Details on an unclassified Genesys ticket: SLA panel states that
  classification and priority are required and the clock has not started;
  after classification, the clock start time, targets and calendar appear.
- Dashboard: breakdown cards sit above the ticket table on desktop and phone.
- Team Performance is not in the navigation and `/Reports/TeamPerformance`
  is refused.

Chatbot/voicebot integration account (CS Agent service account)
- `POST /api/genesys/customers/unit-details` with a known buyer's `crm:{id}` and
  number, no `unitId` → `UnitSelectionRequired` listing that buyer's units.
- Same call with a listed `unitId` → `UnitDetails`; fields CRM does not hold are
  `null` and `detailsStatus` is `NotAvailable`.
- Same call with a `unitId` that belongs to a different buyer → `403 UNIT_NOT_ELIGIBLE`;
  with another customer's `crm:` id → `403 CUSTOMER_NOT_VERIFIED`.

## Verified on UAT

Not yet. This session has no network path to the UAT host
(`10.10.10.117` / the IIS servers) or its database, so nothing above has
been exercised on UAT. All items are verified by the automated suite and
the build only; the role-based checklist is the UAT acceptance run.
