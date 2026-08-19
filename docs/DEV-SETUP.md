# Tiger CS Ticketing — Local Development Setup (Identity and Access)

Scope: this covers the Identity and Access increment only (authentication,
roles, departments, department assignments). No ticketing/SLA/Genesys setup
is described here — those arrive with their own increments.

---

## 1. Prerequisites

- .NET 10 SDK — pinned by `global.json` (`10.0.111`).
- A local SQL Server instance. Docker is the easiest route and is what's
  assumed below; a native local SQL Server install works identically as
  long as the connection string in step 2 matches it.
- The `dotnet-ef` CLI tool: `dotnet tool install --global dotnet-ef --version 10.0.11`
  (add `/root/.dotnet/tools` or `~/.dotnet/tools` to `PATH` if prompted).

## 2. Start a local SQL Server

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=YourStrong!Passw0rd" \
  -p 1433:1433 --name tigercs-sql -d mcr.microsoft.com/mssql/server:2022-latest
```

`YourStrong!Passw0rd` is Microsoft's own widely-published example password
for local SQL Server containers (used verbatim in Microsoft's official
`mssql-server` Docker documentation) — not a real secret, and this
connection targets `localhost` only. It's the value already in
`src/TigerCS.Api/appsettings.Development.json`'s `ConnectionStrings:TigerCsDatabase`
and in `TigerCsDbContextFactory`'s design-time fallback. If your local
container uses a different password, override the connection string via
user-secrets (step 3) rather than editing the committed file.

## 3. Configure secrets (never committed)

Two values must **not** come from `appsettings*.json`: the JWT signing key,
and (optionally) a development administrator's credentials. Both are read
from ASP.NET Core user-secrets or environment variables.

From `src/TigerCS.Api`:

```bash
dotnet user-secrets init   # only needed once; sets <UserSecretsId> (already set)

dotnet user-secrets set "Jwt:SigningKey" "a-random-string-at-least-32-characters-long"
# ^ must be at least 32 bytes (256 bits) — the app now validates this at
#   startup and refuses to run on a missing or too-short key, rather than
#   accepting a default/fallback signing secret.

# Optional: creates a System Administrator on first run. Omit these two and
# no development administrator is created — roles/departments still seed.
dotnet user-secrets set "DevAdmin:Username" "admin@tigercs.local"
dotnet user-secrets set "DevAdmin:Password" "Choose-Your-Own-Strong-Password-1!"
```

Equivalent environment variables (e.g. for CI or a container), using the
`__` double-underscore separator ASP.NET Core configuration expects:

```bash
export Jwt__SigningKey="a-random-string-at-least-32-characters-long"
export DevAdmin__Username="admin@tigercs.local"
export DevAdmin__Password="Choose-Your-Own-Strong-Password-1!"
```

The password policy enforced at signup (Security-Architecture.md §1's
placeholder, made concrete for this increment): minimum 8 characters, at
least one digit, one lowercase, one uppercase, one non-alphanumeric
character, and at least 4 unique characters — the same rule applies to the
`DevAdmin:Password` value above. Lockout: 5 failed attempts locks the
account for 15 minutes (Security-Architecture.md §13).

These are **pilot defaults, not hardcoded final values** — override any of
them via configuration without a code change:

```bash
dotnet user-secrets set "Identity:Password:RequiredLength" "10"
dotnet user-secrets set "Identity:Lockout:MaxFailedAccessAttempts" "3"
```

Only the keys you actually set are overridden; anything you don't set keeps
the pilot default. The app validates these at startup regardless of source
(`Identity:Password:RequiredLength` must be ≥ 8, `Identity:Lockout:MaxFailedAccessAttempts`
must be between 1 and 10, `Identity:Lockout:DefaultLockoutTimeSpan` must be
at least 1 minute) and refuses to start if a configured value violates
that floor.

**No production deployment is authorized at this stage of the pilot** — this
is enforced through release governance and documentation
([ADR-0022's "Pilot-Stage Production Restriction"](https://github.com/nidaabasem/Tiger-CS-Ticketing/blob/implementation/mvp-identity-access/docs/architecture/adr/0022-deployment-strategy.md),
`MVP-Implementation-Backlog.md` §0), **not** by the application refusing to
start under `ASPNETCORE_ENVIRONMENT=Production`. The app runs the same way
regardless of environment name, given valid configuration — whoever
controls the deployment pipeline is responsible for not pointing it at a
production environment until that's actually authorized.

## 4. Apply the database migration

From `src/`:

```bash
dotnet ef database update --project TigerCS.Infrastructure --startup-project TigerCS.Infrastructure
```

This creates `TigerCsTicketing_Dev` with Identity's own tables plus
`Employees`, `Departments`, and `UserDepartmentAssignments` — nothing else
(no ticketing/SLA/Genesys tables exist yet). If you don't have `dotnet ef`
on `PATH`, run it via `dotnet tool run dotnet-ef ...` instead.

To generate a new migration after changing an entity in this module:

```bash
dotnet ef migrations add <Name> --project TigerCS.Infrastructure --startup-project TigerCS.Infrastructure -o Persistence/Migrations
```

To preview the SQL a migration would run, without needing a live database:

```bash
dotnet ef migrations script --project TigerCS.Infrastructure --startup-project TigerCS.Infrastructure --idempotent
```

## 5. Run the API

From `src/TigerCS.Api`:

```bash
dotnet run
```

In the `Development` environment, `Program.cs` seeds the fixed role set,
four sample departments, and — only if `DevAdmin:Username`/`DevAdmin:Password`
are set per step 3 — one System Administrator account. Nothing here ever
runs against a non-development database; there's no production deployment
path in this increment.

## 6. API examples (no real credentials)

Log in (replace with your own dev admin or a seeded test account):

```bash
curl -s -X POST https://localhost:PORT/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"<your-dev-username>","password":"<your-dev-password>"}'
```

```json
{
  "accessToken": "<jwt>",
  "expiresAtUtc": "2026-08-19T13:00:00Z",
  "employeeId": "00000000-0000-0000-0000-000000000000",
  "displayName": "Development Administrator",
  "roles": ["System Administrator"],
  "primaryDepartmentId": null
}
```

Call an authenticated endpoint:

```bash
curl -s https://localhost:PORT/api/users/me \
  -H "Authorization: Bearer <jwt-from-login>"
```

List roles (System Administrator only):

```bash
curl -s https://localhost:PORT/api/roles \
  -H "Authorization: Bearer <jwt-from-login>"
```

Deactivate a staff account (System Administrator only):

```bash
curl -s -X PATCH https://localhost:PORT/api/users/<employeeId>/activation \
  -H "Authorization: Bearer <jwt-from-login>" \
  -H "Content-Type: application/json" \
  -d '{"isActive": false, "reason": "Left the company"}'
```

Log out:

```bash
curl -s -X POST https://localhost:PORT/api/auth/logout \
  -H "Authorization: Bearer <jwt-from-login>"
```

**Logout semantics — stated explicitly, not implied.** MVP authentication is
a stateless JWT (`AuthenticationAppService`'s own doc comment says this
too): there is no server-side token-revocation list. `POST /api/auth/logout`
returns `204 No Content` and does nothing else — it does **not** invalidate
the token. The token the client was holding remains cryptographically valid
and will keep passing *authentication* until it expires (`Jwt:ExpirationMinutes`,
60 by default) or the client discards it. What logout does not protect
against — a stolen still-valid token being replayed after "logout" — is a
known, accepted limitation of this MVP increment, not an oversight; adding a
revocation mechanism was explicitly out of scope ("do not invent
refresh-token functionality unless already approved"). The one thing that
*does* stop a token from working immediately, regardless of logout, is
**deactivation** (`PATCH .../activation`) — see
`Token_ReusedAfterDeactivation_IsRejectedOnEveryProtectedEndpoint` in
`AuthEndpointsTests.cs`.

## 7. SQL Server validation checklist

This sandbox has no Docker daemon available to run these steps itself (the
`docker` CLI is present but `docker info` fails to reach a daemon socket).
Since a real SQL Server was needed, `.github/workflows/db-migration-validation.yml`
runs the migration against an actual SQL Server 2022 service container in
GitHub Actions instead — automated, not a manual local run, and not merely
`dotnet ef migrations script` reviewed by eye.

**Confirmed by that workflow (run #4, commit `88818d2`, [green run](https://github.com/nidaabasem/Tiger-CS-Ticketing/actions/runs/32250361013)):**

- [x] `dotnet ef database update` against a real SQL Server 2022 container completes with no error.
- [x] `SELECT name FROM sys.tables ORDER BY name;` against the resulting database returns exactly the 11 expected tables (`AspNetRoleClaims`, `AspNetRoles`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserRoles`, `AspNetUserTokens`, `AspNetUsers`, `Departments`, `Employees`, `UserDepartmentAssignments`, `__EFMigrationsHistory`) — nothing else, confirmed by the workflow's own table-list diff, not eyeballed.
- [x] `IX_UserDepartmentAssignments_EmployeeId_PrimaryOnly`'s `filter_definition` is exactly `([IsPrimary]=(1))`.
- [x] `dotnet ef migrations has-pending-model-changes` against the live, migrated database reports "No changes have been made to the model since the last migration." — no drift between the model and the applied schema.

**Not covered by that workflow — still only verified via the InMemory-backed integration tests, not against a real SQL Server:**

- [ ] A full application login round-trip (seed → `POST /api/auth/login` → authenticated request) against a real SQL-Server-backed database, run locally per steps 1-6 above. Worth doing before this increment is considered fully production-schema-verified, since InMemory doesn't exercise the same SQL Server-specific behavior (e.g. the filtered index, real transaction/locking semantics) that a login-and-request flow would touch end-to-end.

Re-run the workflow any time via `workflow_dispatch` (Actions tab → "DB Migration Validation" → "Run workflow") to re-verify after a schema change.

## 8. Running the tests

```bash
cd src
dotnet test --configuration Release
```

The integration tests (`TigerCS.Tests/IdentityAndAccess/Integration`) run
the real Api in-process against a per-test EF Core InMemory database — no
SQL Server is needed to run `dotnet test`. Only the migration itself (step 4)
needs a real SQL Server, since InMemory doesn't exercise the filtered
unique index or real T-SQL constraints.
