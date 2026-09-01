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
([ADR-0022's "Pilot-Stage Production Restriction"](architecture/adr/0022-deployment-strategy.md),
`MVP-Implementation-Backlog.md` §0), **not** by the application refusing to
start under `ASPNETCORE_ENVIRONMENT=Production`. The app runs the same way
regardless of environment name, given valid configuration — whoever
controls the deployment pipeline is responsible for not pointing it at a
production environment until that's actually authorized.

## 3a. Configure CRM Buyer Lookup (`Crm:BaseUrl` / `Crm:SecretKey`)

The CRM Buyer Lookup integration (`CrmBuyerHttpGateway`, `GET /api/crm/buyers`)
calls the legacy CRM MVC 4.7 application's own
`GET /TicketingSystem/GetBuyerByPhone` endpoint directly — it is a real
integration from day one, unlike `ICrmGateway`'s unit/contact lookups, which
still run against `MockCrmGateway` (`Crm:Provider`) until a real endpoint for
those exists.

Two configuration values under the same `Crm` section:

- **`Crm:BaseUrl`** — the CRM application's base URL. Not a secret: safe to
  commit per environment in `appsettings.{Environment}.json`, the same way
  `TigerCsApi:BaseUrl` is committed in `src/TigerCS.Web/appsettings.json`.
- **`Crm:SecretKey`** — the shared secret CRM validates via the
  `X-SECRET-KEY` request header (the same value CRM reads from
  `ConfigurationManager.AppSettings["TicketingSecretKey"]` on its own side).
  **Never committed** — configure it the same way as `Jwt:SigningKey` above.

**Development** (user-secrets, from `src/TigerCS.Api`):

```bash
dotnet user-secrets set "Crm:BaseUrl" "https://crm-dev.tigergroup.internal/"
dotnet user-secrets set "Crm:SecretKey" "<the value CRM's TicketingSecretKey app setting holds>"
```

**UAT / Production** (environment variables, e.g. in the deployment
pipeline/container — same `__` separator as `Jwt__SigningKey` above):

```bash
export Crm__BaseUrl="https://crm-uat.tigergroup.internal/"
export Crm__SecretKey="<the value CRM's TicketingSecretKey app setting holds>"
```

If `Crm:SecretKey` (or `Crm:BaseUrl`) is left unconfigured, `GET /api/crm/buyers`
does not crash the app or fail startup — `CrmBuyerHttpGateway` returns a 502
(`CrmBuyerLookupOutcome.Unavailable`) for every request until both are set,
consistent with every other CRM port never blocking the rest of the
application. This is also why local development works without a real CRM
connection: the endpoint simply reports CRM as unavailable until you opt in
by setting the two values above.

## 3b. Configure PACT customer lookup (`Pact:Provider` / `PactApi:BaseUrl` / `PactApi:ApiKey`)

The PACT customer/contract lookup (`PactCustomerHttpGateway`, the Pact leg of
`GET /api/intake-records/{id}/customer-lookup`) calls PACT's
`GET v1/contracts/{mobile}` and `GET v1/contracts/{mobile}/customer-type`
endpoints. Unlike CRM Buyer Lookup it sits behind a provider switch:

- **`Pact:Provider`** — `"Mock"` (the default: `MockPactGateway`'s fixture
  data, deterministic and offline — what dev and the test suite use) or
  `"Http"` (the real PACT integration).

With `"Http"` selected, two values under the `PactApi` section:

- **`PactApi:BaseUrl`** — PACT's base URL. Not a secret: safe to commit per
  environment in `appsettings.{Environment}.json`, same as `Crm:BaseUrl`.
- **`PactApi:ApiKey`** — the API key PACT validates via the `X-API-KEY`
  request header. **Never committed** — configure it the same way as
  `Crm:SecretKey` above.

**Development** (user-secrets, from `src/TigerCS.Api`):

```bash
dotnet user-secrets set "Pact:Provider" "Http"
dotnet user-secrets set "PactApi:BaseUrl" "http://pact-dev.tigergroup.internal:5020/"
dotnet user-secrets set "PactApi:ApiKey" "<the API key PACT issued for this system>"
```

**UAT / Production** (environment variables, same `__` separator):

```bash
export Pact__Provider="Http"
export PactApi__BaseUrl="http://pact-uat.tigergroup.internal:5020/"
export PactApi__ApiKey="<the API key PACT issued for this system>"
```

If `PactApi:ApiKey` (or `PactApi:BaseUrl`) is left unconfigured while
`Pact:Provider` is `"Http"`, nothing crashes or fails startup —
`PactCustomerHttpGateway` reports `PactCustomerLookupOutcome.Unavailable`,
which customer lookup surfaces as a `Failed` Pact source entry; ticket
creation is never blocked and the agent falls back to manual customer/unit
entry, consistent with every other lookup source.

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

### Swagger UI

With the API running, browse to:

```
https://localhost:PORT/swagger
```

The generated OpenAPI document itself is at
`/swagger/v1/swagger.json` (and, unchanged from before Swagger UI was added,
at `/openapi/v1.json`).

To call an authenticated endpoint from the UI:

1. Expand **Authentication → POST /api/auth/login** and execute it with a
   seeded account (step 3).
2. Copy the `accessToken` value out of the response.
3. Press **Authorize** at the top right and paste **only the token** — no
   `Bearer ` prefix. Swagger UI sends `Authorization: Bearer {token}` on
   every request from then on.

Swagger is served in the `Development` and `Testing` environments only
(`OpenApiDocumentation.EnabledEnvironments`). In any other environment —
Production included — neither the UI nor the JSON document is mapped, so
both paths behave exactly like a route this application does not have.

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

**Confirmed by that workflow.** The pinned expected-table list is refreshed with
every schema-adding increment, which is the point of it: an unannounced table is
a workflow failure, not a silent pass. It stood at 11 tables after Identity and
Access, 31 after SLA and Escalation, and **33 as of the Notifications increment**
(`OutboxMessages` and `Notifications`, MVP-Data-Dictionary.md §2.21/§2.23).
`TicketSlaPausePeriods` and `PriorityDowngradeRequests` remain deliberately
absent (MVP-Implementation-Backlog.md §0/§0.2), as do all Genesys tables.

- [x] `dotnet ef database update` against a real SQL Server 2022 container completes with no error.
- [x] `SELECT name FROM sys.tables ORDER BY name;` returns exactly the expected table list — nothing else, confirmed by the workflow's own table-list diff, not eyeballed.
- [x] Every filtered unique index is present **with its exact `filter_definition`** — the primary-department index, the CRM verification-session idempotency index, the current-assignment and current-resolution indexes, the current-SLA-period and one-auto-breach-per-ticket indexes, and the Notifications increment's `UX_Notifications_OutboxMessageId_NotificationType`. The EF Core InMemory provider ignores filtered indexes entirely, so this is the only place they are actually exercised.
- [x] The `OutboxMessages` dispatcher and dead-letter indexes carry their `[Status]` filters, so `Processed` rows — which nothing deletes — never grow the indexes the hot path reads.
- [x] Column nullability on `Notifications`/`OutboxMessages` matches §2.21/§2.23, including `RecipientAddress` being nullable: a ticket with no deliverable requester address still gets a dead-lettered, visible row rather than a fabricated address.
- [x] Every foreign key on the SLA, Escalation and Notifications tables is `NO_ACTION` (Restrict).
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

## 9. SLA and escalation background jobs (ADR-0015)

The SLA and Escalation increment adds Hangfire, backed by the same SQL Server
database (ADR-0003). It runs two things:

- a **scheduled (delayed) job per due timestamp**, enqueued the moment the
  timestamp is computed — the primary breach-detection mechanism
  (`SLA-Architecture.md` §13);
- a **recurring safety sweep**, every 1–5 minutes, that re-scans due deadlines
  solely to catch a scheduled job lost to a deploy or restart (§14). It is
  never the primary path, and on a healthy system it records nothing.

Neither depends on an open browser or a SignalR client: both are server-side
background work against the stored `TicketSlaInstances.*DueAtUtc` columns.

### Configuration

```jsonc
"BackgroundJobs": {
  "Enabled": true,            // default; set false to run the API with no Hangfire server
  "SweepIntervalMinutes": 5   // clamped to the approved 1-5 range
}
```

Hangfire provisions its own tables in a separate `HangfireSla` schema on first
start, so they are never confused with — or migrated alongside — the
application schema EF Core owns. `ConnectionStrings:TigerCsDatabase` is
required whenever `Enabled` is `true`; the API refuses to start otherwise
rather than silently running without SLA breach detection.

**`Enabled: false` disables job *execution*, never job *logic*.** Breach
detection stays fully wired and reachable — the deadline scheduler simply
becomes a no-op, so a breach is detected on the next sweep instead of at the
exact due moment, and detected exactly once either way (both paths share one
idempotency key, `SLA-Architecture.md` §15). The integration-test hosts use
this, since they have no SQL Server behind them.

### Reference data

`SlaPolicies` and the default `BusinessCalendars`/`BusinessCalendarWorkingDays`
rows are seeded alongside `Priorities` by `DevSeedData` in Development, from
`SlaReferenceData` — the same source the integration-test host seeds from, so
the approved per-tier targets exist in exactly one place. The seeded calendar
is ISSUE-017's approved Option A: **Saturday–Thursday working, Friday off,
08:00–18:00, `Asia/Dubai`**.

**No `Holidays` rows are seeded.** ADR-0010 makes holiday entry a manual
annual process with a named business owner (ISSUE-012), and every row requires
an `EnteredByEmployeeId` — so holiday dates are entered per year, not shipped.
A missing holiday entry silently produces an SLA deadline that runs through a
day the business was closed; that is ADR-0010's own stated operational risk,
and it is the one piece of this calculation a code change cannot protect.

The host must be able to resolve the calendar's `TimeZone` (`Asia/Dubai`). On
Linux that needs `tzdata` installed. A zone that cannot be resolved fails
loudly rather than falling back to UTC, which would silently shift every
business-hours deadline by four hours.

## 11. Running TigerCS.Web locally

TigerCS.Web is a separate ASP.NET Core app — it never touches the database
directly; every page that needs data calls TigerCS.Api over HTTP through a
handful of typed `HttpClient`s (`src/TigerCS.Web/Services/Api/*ApiClient.cs`).
All of them share **one** configuration source: the `TigerCsApi:BaseUrl`
value `Program.cs` binds into `TigerCsApiOptions` and hands to every
`AddHttpClient<...>()` registration. There is no per-client override and
nothing here is ever hard-coded — if a page can't reach the Api, this is the
one setting to check.

```jsonc
// src/TigerCS.Web/appsettings.Development.json
"TigerCsApi": {
  "BaseUrl": "https://localhost:7283"
}
```

**This must match whatever port TigerCS.Api is actually listening on for
you.** `src/TigerCS.Api/Properties/launchSettings.json`'s `https` profile
binds `https://localhost:7283` (and, unless you've overridden it, also
`http://localhost:5179`) — if you start the Api a different way (a custom
`--urls`, a different launch profile, IIS Express, a container port
mapping), update `TigerCsApi:BaseUrl` to match instead of assuming the
default. A stale or mismatched value here is exactly what an "Unable to load
the department list" / "Could not record this interaction" pair on the New
Ticket page means — the Api itself can be completely healthy (confirmed via
Swagger) while Web is still pointed at the wrong address.

Both apps run over HTTPS in Development with the standard ASP.NET Core
localhost dev certificate. Trust it once, machine-wide, if you haven't
already — this is what lets the Web app's server-side `HttpClient` complete
TLS to the Api without any certificate-validation code change:

```bash
dotnet dev-certs https --trust
```

Never work around a certificate error here by disabling TLS validation
(`ServerCertificateCustomValidationCallback`, `HttpClientHandler.DangerousAcceptAnyServerCertificateValidator`,
etc.) — that reintroduces the exact risk the dev certificate exists to avoid,
for a problem `dotnet dev-certs https --trust` already solves.

TigerCS.Web authenticates its own users with a cookie
(`TigerCS.Web.Auth`, `Program.cs`) that never reaches the browser as
JS-readable data. Server-side, `BearerTokenHandler` reads the Api access
token back out of that cookie's claims and attaches it as
`Authorization: Bearer {token}` on every outgoing call — every typed client
except `AuthApiClient` (which signs in/out, before any token exists) is
registered with `.AddHttpMessageHandler<BearerTokenHandler>()` in
`Program.cs`. A page failing with an authorization-specific message ("Your
session is not authorized to perform this action.") rather than a
connectivity one means you're signed into Web but the forwarded token is
being rejected — check that you're signed in with an account TigerCS.Api
still considers active, not that the Api address is wrong.

In Development, every non-success Api call (a mapped HTTP failure or a
connection failure) is logged from `ApiClientBase` — method, relative
endpoint, status code, the Api's own "detail"/"title" if it sent one, and
the exception type on a connection failure. Never a token, a cookie, or a
full response body. Check the Web app's own console/log output first when a
page reports a generic failure — the specific cause is there even when the
page itself only shows a safe, generic message.

Run it from `src/TigerCS.Web`:

```bash
dotnet run
```

then open `https://localhost:7219`.
