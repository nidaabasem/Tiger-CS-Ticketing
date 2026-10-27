# Tiger CS Ticketing — Production Deployment (TGAESHJAPP05)

Scope: first Production deployment of `TigerCS.Api` and `TigerCS.Web` to the existing IIS sites on `TGAESHJAPP05`,
against `TigerCS_Ticketing_Prod` on `TGAESHJDBP02`. Nothing in this repository deploys, migrates or seeds automatically.

| App | IIS site / pool | Path | Internal binding |
|---|---|---|---|
| API | `TigerCS-API` / `TigerCS-API` (No Managed Code) | `C:\inetpub\TigerCS\Api` | `8091` |
| Web | `TigerCS-Web` / `TigerCS-Web` (No Managed Code) | `C:\inetpub\TigerCS\Web` | `8092` |

The `tigercrm` site, its pool and its bindings are never touched by any step below. Only the two TigerCS sites/pools are
started, stopped or recycled — never `iisreset` outside the Hosting Bundle maintenance window (§2).

---

## 1. What is committed, and what is deliberately off

`appsettings.Production.json` (API and Web) is layered over `appsettings.json` when `ASPNETCORE_ENVIRONMENT=Production`
(also the default when the variable is unset). It contains **no secrets**. `ProductionConfigurationTests` fails the build if a
secret, a `TrustServerCertificate=True`, a test-mode redirect or an enabled outbound channel is ever committed to it.

| Setting | Production value | Why |
|---|---|---|
| `ConnectionStrings:TigerCsDatabase` | `TGAESHJDBP02` / `TigerCS_Ticketing_Prod` / `TigerCS_App`, `Encrypt=True;TrustServerCertificate=False`, **no password** | Password via env var (§4). Certificate trust: §7. |
| `ConnectionStrings:PACTRPT`, `:CrmDatabase`, `PactApi:BaseUrl` | empty | The shared base file points these at non-Production hosts; blanked so Production can never reach them by inheritance. Set from env when Collections is enabled. |
| `Jwt:Issuer` / `Audience` / `ExpirationMinutes` | `TigerCS.Api` / `TigerCS.Web` / `60` | Identifiers only (validated by the API against itself). `Jwt:SigningKey` is a secret — env var. Startup fails fast without it (≥ 32 bytes). |
| `VerificationTesting:*` | `Enabled=false`, overrides empty | No OTP/document redirection to test inboxes. |
| `CustomerNotifications:IsTestMode` | `"false"` (live routing), `TestEmail`/`TestPhone` empty | **Any customer email/SMS channel enabled later sends to real customers immediately.** All channels are currently off (below). `PublicBaseUrl` is empty → Resolved/Closed emails carry no review link until set. |
| `EmailNotifications:Enabled` | `false` | Needs the Microsoft 365 SMTP password. |
| `Sms:Provider` | `Disabled` | `Fake` is refused in Production by a startup guard anyway. |
| `CrmDocuments:Enabled` / `AllowInProduction` | `false` / `false` | Startup guard also requires explicit sign-off + `OtpCodePepper`. |
| `BackgroundJobs:Enabled` | `false` | See "Decision needed" below. |
| `Collections:Enabled`, `ReceivablesSnapshot` refresh and `RefreshOnStartup`, `CrmOwners`, `PactReceivables`, `GenesysOutbound`, campaign admin notification | all `false` | `BusinessRulesConfirmed=false`, no verified PACT/CRM Production source or Genesys outbound credentials. |
| `Genesys:Enabled` | `false` | Needs `Genesys:ScreenPopWebBaseUrl`, service identities and a verified Production Genesys org. |
| Swagger / OpenAPI | not mapped | Allow-list is `Development`,`Testing` only (`OpenApiDocumentation.EnabledEnvironments`); verified 401/no document on a Production-mode run. |
| Logging | `Warning` default, `TigerCS` at `Information` | No EF command text (would contain parameter values at higher levels). `IdentityModelEventSource.ShowPII=false` is set in code. |
| Web `TigerCsApi:BaseUrl` | `http://localhost:8091/` | See §1.1. |

**Decision needed before go-live — `BackgroundJobs:Enabled`.** While `false`, no Hangfire server runs: the SLA safety
sweep, the notification Outbox dispatcher and chatbot inactivity closure do **not** run (Outbox rows stay `Pending`; SLA
breaches are not detected by sweep). Enabling it requires Hangfire to create its `HangfireSla` schema on first start
(`PrepareSchemaIfNecessary=true`), which `TigerCS_App` as a read/write-only login cannot do — see §6.

### 1.1 Web → API address
Every typed client is registered with `client.BaseAddress = new Uri(TigerCsApi:BaseUrl)` and calls relative paths, so the
value must be an absolute URL **with a trailing slash** (tested). `http://localhost:8091/` is used because both sites are on
the same host. `UseHttpsRedirection` is on in both apps: it only redirects when it can determine an HTTPS port. **Do not set
`ASPNETCORE_HTTPS_PORT` for the API pool**, and do not add an HTTPS binding to the API site on a port that IIS reports
to the app, or the Web's plain-HTTP call to `localhost:8091` will be redirected and fail. If external callers (Genesys, chatbot)
must reach the API over HTTPS, publish it through a separate HTTPS binding and verify the Web→API call afterwards (§10).

---

## 2. Install the .NET 10 Hosting Bundle (maintenance window)

`AspNetCoreModuleV2` is missing; both `web.config` files use it (`hostingModel="inprocess"`). The installer registers a
global IIS module and **requires an IIS restart, which interrupts the `tigercrm` site** — run it only in the approved window.

1. Download the **.NET 10 Hosting Bundle** (same 10.0.x servicing line as the installed runtime, ≥ the SDK's runtime) from the
   official .NET download page; verify the publisher signature / SHA-512 from the release notes.
2. In the window: `dotnet-hosting-10.0.x-win.exe /install /quiet /norestart`
3. Restart IIS once, in the window: `net stop was /y` then `net start w3svc` (or `iisreset`). Confirm `tigercrm` responds.
4. Verify: `Get-WebGlobalModule | ? Name -eq AspNetCoreModuleV2` and
   `Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"`; `dotnet --list-runtimes` shows
   `Microsoft.AspNetCore.App 10.0.x`.

Nothing else in this guide requires an IIS restart: pools/sites are started and recycled individually.

---

## 3. Build and publish (build machine, not the server)

Scripted: `tools/deploy/Publish-TigerCS.ps1 -Output C:\build\TigerCS` runs the tests, publishes both apps to a timestamped folder and fails
if a Development/UAT file or a non-empty secret setting is in the output. Manual equivalent:

```powershell
# SDK pinned by global.json (10.0.111, rollForward latestFeature)
dotnet test src/TigerCS.Tests -c Release
dotnet publish src/TigerCS.Api -c Release -o C:\build\TigerCS\Api
dotnet publish src/TigerCS.Web -c Release -o C:\build\TigerCS\Web
```

Publish to clean directories **outside** the repo (the tracked `publish/` folder is a stale artifact; do not publish into it).
Both projects exclude `appsettings.Development.json` (and the API `appsettings.UAT.json`) from publish. Before copying,
confirm the output has `appsettings.json`, `appsettings.Production.json`, `web.config`, and no `*.Development.json`/`*.UAT.json`,
and that no setting named `Password`/`SecretKey`/`ClientSecret`/`ApiKey`/`SigningKey` has a value.

---

## 4. Production secrets and environment variables

Set on **each application pool** (not machine-wide, so the two apps and `tigercrm` do not share a secret scope), from the
organisation's secret store. Never put them in `web.config`, `appsettings*.json` or this repository.

```bat
%windir%\system32\inetsrv\appcmd.exe set config -section:system.applicationHost/applicationPools ^
  /+"[name='TigerCS-API'].environmentVariables.[name='Jwt__SigningKey',value='<from vault>']" /commit:apphost
```

This edits `applicationHost.config` (admin-readable) and recycles **only that pool**.

**API pool `TigerCS-API` — required to start and serve**

| Variable | Source |
|---|---|
| `ASPNETCORE_ENVIRONMENT=Production` | fixed (non-secret) |
| `ConnectionStrings__TigerCsDatabase` | Full string, DBA-provided `TigerCS_App` password (see §7 for the certificate setting) |
| `Jwt__SigningKey` | New random ≥ 32-byte value generated for Production; **not** the Dev/UAT key. e.g. `[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))` |

**API pool — required only when the feature is switched on** (each is a missing Production value; none is invented here)

| Feature | Keys | Source |
|---|---|---|
| Service identities (Genesys / chatbot / integrations) | `Authorization__ServiceIdentity__EmployeeIds__0..n`, `Collections__Authorization__IntegrationEmployeeIds__0..n` | Production `Employees` GUIDs of the service accounts, created after seeding |
| Genesys | `Genesys__Enabled=true`, `Genesys__ScreenPopWebBaseUrl` (public HTTPS URL of TigerCS-Web), optional `Genesys__ClassificationDepartmentCode` | Infra/DNS + Genesys admin |
| CRM | `Crm__SecretKey`; confirm `Crm__BaseUrl` (base file has `https://tigercrm.tigergroup.ae:8014`) | CRM team |
| PACT | `PactApi__BaseUrl`, `PactApi__ApiKey`, `ConnectionStrings__PACTRPT`, `ConnectionStrings__CrmDatabase` | PACT / CRM DBAs (read-only logins) |
| SMS OTP | `Sms__Provider=Broadnet`, `Sms__Broadnet__{Endpoint,User,Password,SenderId,TypeEnglish,TypeArabic,MobileFormat,SuccessBodyPattern,FailureBodyPattern}`, `CrmDocuments__OtpSmsEnabled`, `CrmDocuments__OtpSmsDefaultCountryCode` | Broadnet contract |
| CRM document delivery | `CrmDocuments__Enabled`, `CrmDocuments__AllowInProduction=true` (after UAT sign-off), `CrmDocuments__OtpCodePepper` (new random secret) | Business sign-off + generated |
| Customer email | `EmailNotifications__Enabled=true`, `EmailNotifications__Password`, review `Username`/`FromEmail`, `CustomerNotifications__PublicBaseUrl` (public Web URL), `CustomerNotifications__Branding__ContactEmail/ContactPhone` | M365 admin |
| Collections / Genesys outbound | `Collections__Enabled`, `CollectionsSource__PactReceivables__Enabled`, `CollectionsSource__CrmOwners__Enabled`, `Collections__GenesysOutbound__{ClientId,ClientSecret,Campaign__*}`; **verify `ContactListIds` and the `mypurecloud.de` region in `appsettings.json` belong to the Production Genesys org** | Collections + Genesys admin; `BusinessRulesConfirmed` is a business decision |
| Background jobs | `BackgroundJobs__Enabled=true` | After §6 |
| Host filtering | `AllowedHosts` (base value is `*`) set to the real DNS name(s) | DNS decision |

**Web pool `TigerCS-Web`:** only `ASPNETCORE_ENVIRONMENT=Production`. The Web has no secrets of its own (the API JWT lives
inside its encrypted cookie). Its cookie-encryption keys are persisted by ASP.NET Core to the HKLM registry for the
`IIS AppPool\TigerCS-Web` identity, so they survive pool recycles; if sign-ins are lost on every recycle, check the pool's
identity is `ApplicationPoolIdentity` and not a profile-less custom account.

---

## 5. Pool identities and folder permissions

- Both pools: **Identity = `ApplicationPoolIdentity`** (`IIS AppPool\TigerCS-API`, `IIS AppPool\TigerCS-Web`), .NET CLR
  version **No Managed Code**, 64-bit, start mode `AlwaysRunning` optional. Do not use a domain/admin account.
- Folders: grant `IIS AppPool\<pool>` **Read & Execute** only, on its own folder (`icacls C:\inetpub\TigerCS\Api /grant "IIS AppPool\TigerCS-API:(OI)(CI)RX"`).
  No write access is needed: `stdoutLogEnabled="false"` and neither app writes to its own folder (confirm if file-attachment storage, ADR-0017, is enabled later). If stdout
  logging is temporarily enabled for diagnosis, grant Modify on the `logs` subfolder only, and turn it off again.
- `IIS_IUSRS` and `Users` should have no access beyond what the default inheritance gives; deploy account has Modify on both
  folders for the deployment only.
- Database: the API connects as SQL login `TigerCS_App` (never Windows integrated, never `sa`/`db_owner`) — §6.

---

## 6. Database permissions

`TigerCS_App` should hold `db_datareader` + `db_datawriter` on `TigerCS_Ticketing_Prod` and nothing else (no `db_owner`,
`db_ddladmin`, server roles, `sysadmin`, or rights on other databases). Verify with `sys.database_role_members` /
`HAS_PERMS_BY_NAME`. Two features need more and must be handled deliberately, not by widening the app login:

- **Hangfire** (`BackgroundJobs:Enabled=true`) creates schema `HangfireSla` on first start. A DBA creates the schema and tables
  once with an elevated login (or temporarily grants `CREATE SCHEMA`/`CREATE TABLE`, then revokes it), and grants `TigerCS_App`
  the data rights on `HangfireSla`.
- **Schema changes** (EF migrations) are applied only by a DBA/deployment login from a reviewed script
  (`dotnet ef migrations script --idempotent`), never by the API or Web on startup. Neither app calls `Migrate()`/`EnsureCreated()`
  (checked); `DevSeedData` runs only in `Development`.

`TigerCS_App` also needs `EXECUTE` if any stored procedure is invoked on the Ticketing database — confirm against the
migration scripts before go-live.

---

## 7. SQL Server certificate (known gap — remediate, don't suppress)

The Production SQL Server's certificate fails chain validation from .NET. `TrustServerCertificate=True` disables validation
and makes TLS to the database open to interception; it is **not** in any committed Production file and a test enforces that.

Remediation (SQL maintenance window — SQL service restart):
1. Issue a server certificate from the internal CA (Server Authentication EKU, SAN = every name clients use:
   `TGAESHJDBP02`, its FQDN; add `10.10.10.124` only if clients must connect by IP).
2. Install it in `LocalMachine\My` on `TGAESHJDBP02`; grant the SQL Server service account read access to the private key.
3. SQL Server Configuration Manager → Protocols for MSSQLSERVER → Certificate tab → select it; restart the SQL service.
4. Install the CA root/intermediate in `LocalMachine\Root`/`CA` on `TGAESHJAPP05` (the .NET client uses the Windows store).
5. Keep the connection string host equal to a SAN (`Server=TGAESHJDBP02`, or the FQDN) with `Encrypt=True;TrustServerCertificate=False`.
   If the SAN is the FQDN but the app must use the short name, add `HostNameInCertificate=<fqdn>`.

**Interim exception** (only if go-live precedes the certificate fix, with written sign-off and an expiry date): override
`ConnectionStrings__TigerCsDatabase` in the **pool environment variable** with `...;TrustServerCertificate=True`, and remove it
when step 5 is done. Never edit the committed file.

---

## 8. Production reference-data seeding

**Do not run until approved. Not part of deployment of the apps (neither Api nor Web references the seeder; a test enforces it).**

`src/TigerCS.ProductionSeeder` (commit `d0705fd`) now has an explicit Production mode. All input is the environment variable
`ConnectionStrings__TigerCsDatabase` (the seeder's own, set only in the DBA's shell; never an argument or a file).

```powershell
# 1. Read-only: proves target, migration identity and shows current row counts. Writes nothing.
dotnet TigerCS.ProductionSeeder.dll validate --target production
# 2. After a full backup of TigerCS_Ticketing_Prod and written approval:
dotnet TigerCS.ProductionSeeder.dll seed --target production --confirm-production TGAESHJDBP02/TigerCS_Ticketing_Prod --backup-confirmed
```

Safeguards, in order (any failure exits non-zero before anything is written):
1. Arguments must be a complete, explicit request (exact `server/database` confirmation and `--backup-confirmed`); `seed` without them, or with the SeedTest flag, is refused.
2. The connection string must name `TigerCS_Ticketing_Prod`, and the server itself must answer `@@SERVERNAME = TGAESHJDBP02` and `DB_NAME() = TigerCS_Ticketing_Prod`.
3. `TrustServerCertificate=True` blocks a production seed unless `--allow-untrusted-certificate` is passed (logged as a warning) — fix the certificate instead (§7).
4. Applied migrations must equal the build's migrations **by identity** (missing and unknown IDs listed by name), not by count.
5. Roles, Departments, Priorities, SlaPolicies, Workflows, RequestTypes and Categories must all be empty; anything present aborts and names the tables.
6. The seed and its verification run in **one transaction** (the two request-type importers now join it instead of opening their own). Post-seed counts must be exactly 9 / 8 / 4 / 4 / 35 / 45 / 45 and every role and department code present; otherwise it rolls back. Any exception rolls back.
7. Output contains target, migration/row counts and timing only — no connection string, no stack traces.

`SeedTest` is still supported (`seed --target seedtest --confirm-seed-test`). The seeder does not create users; the first System
Administrator and service-identity employees are created separately (§12).

---

## 9. Deploy to the existing IIS directories (per-site, no IIS restart)

Scripts in `tools/deploy` (PowerShell, run elevated on the server; **not run or tested from the repository environment**):
`Test-IisReadiness.ps1` (read-only checks), `Deploy-TigerCS.ps1 -Source <publish dir> -WhatIf` (then without `-WhatIf`),
`Rollback-TigerCS.ps1 -App Api|Web -Backup <dir>`. They only address `TigerCS-API`/`TigerCS-Web`, refuse any other site, and never
restart IIS. Always run `-WhatIf` first. Manual equivalent:

Prerequisites done: Hosting Bundle (§2), database migrated by DBA, pool env vars (§4), certificate decision (§7).

```powershell
Import-Module WebAdministration
$stamp = Get-Date -Format yyyyMMdd-HHmm
foreach ($a in @(@{n='API';p='Api'}, @{n='Web';p='Web'})) {
  $site = "TigerCS-$($a.n)"; $dir = "C:\inetpub\TigerCS\$($a.p)"
  Stop-WebSite $site                       # sites are already stopped on first deploy
  if (Get-ChildItem $dir -ErrorAction SilentlyContinue) {
    robocopy $dir "C:\inetpub\TigerCS\_backup\$($a.p)-$stamp" /MIR /NFL /NDL | Out-Null
  }
  robocopy "C:\build\TigerCS\$($a.p)" $dir /MIR /XD logs /NFL /NDL
}
```

Deploy and start **API first**, then Web. Only ever address `TigerCS-API` / `TigerCS-Web`; never `tigercrm` and never `iisreset`.

Bindings: keep `8091` / `8092` internal. For users, add an HTTPS binding to `TigerCS-Web` with the certificate in
`LocalMachine\My` (DNS name to be supplied; a certificate whose chain clients trust). Ensure the firewall exposes only the
Web HTTPS port; `8091` stays loopback/internal (or restricted to the Genesys/chatbot source addresses if they call it).

---

## 10. Start-up and health checks

1. `Start-WebSite TigerCS-API`; `curl http://localhost:8091/health` → `200 {"status":"healthy"}` (anonymous, no DB access —
   it proves the process, module and JWT startup guards, not the database).
2. Prove database access: sign in through the Web as the seeded System Administrator (after seeding) or run a read-only
   `SELECT 1` as `TigerCS_App` from the app server with the same connection string.
3. `Start-WebSite TigerCS-Web`; `curl http://localhost:8092/health` → 200; open the HTTPS URL, sign in; load Tickets and Admin.
4. Negative checks: `/swagger` and `/swagger/v1/swagger.json` on the API return no document; response on the Web has the CSP header.
5. Confirm the Application log has no `System.InvalidOperationException` from the startup guards, and that `tigercrm` is unaffected.
6. Startup failure reasons are explicit and name the key (never the value): missing `Jwt:SigningKey`, `Crm:Provider=Mock`,
   `Sms:Provider=Fake`, `CrmDocuments` enabled without sign-off, SMTP enabled without settings. Enable `stdoutLogEnabled`
   temporarily only to read them, then disable.

---

## 11. Rollback

- **Application:** `Stop-WebSite` the affected site; `robocopy C:\inetpub\TigerCS\_backup\<Api|Web>-<stamp> C:\inetpub\TigerCS\<Api|Web> /MIR /XD logs`; start the site; re-run §10. On the very first deployment, rollback is "stop both sites" (they were stopped before).
- **Configuration:** remove or correct the pool environment variable with `appcmd ... /-"[name='<pool>'].environmentVariables.[name='<VAR>']"`; only that pool recycles.
- **Database:** application releases do not change the schema. A migration or seed rollback is a DBA operation from the pre-change backup taken immediately before it (take a full backup of `TigerCS_Ticketing_Prod` before the seed and before any migration).
- **Hosting Bundle:** uninstalling needs another IIS restart/maintenance window; roll back apps instead.

---

## 12. Go-live checklist

- [ ] Hosting Bundle installed in the window, IIS restarted once, `tigercrm` verified
- [ ] SQL certificate trusted (§7) — or signed, time-boxed exception recorded
- [ ] `TigerCS_App` verified `db_datareader`+`db_datawriter` only; Hangfire schema decision made (§6)
- [ ] `TigerCS_Ticketing_Prod`: 37 migrations match by identity; full backup taken
- [ ] `validate --target production` clean; the new transactional seed path exercised against an empty SQL Server copy of the schema (restored from a migrations-only backup — it has not yet run against real SQL; only its decision logic is unit-tested); then `seed --target production` run **once** with approval (§8)
- [ ] First System Administrator and service-identity employees created; IDs set (§4)
- [ ] Pool env vars set (`Jwt__SigningKey`, `ConnectionStrings__TigerCsDatabase`, `ASPNETCORE_ENVIRONMENT`)
- [ ] Published output checked: no Development/UAT files, no secret values
- [ ] HTTPS binding + trusted certificate + DNS for Web; `AllowedHosts` set; API not exposed beyond need
- [ ] Decisions recorded: `BackgroundJobs`, Genesys, Collections, SMS/OTP, email, document delivery (each off until its dependency is verified)
- [ ] Health checks (§10) pass; rollback copy exists

---

## 13. Security review notes

- No hardcoded passwords/API keys found in `src` or committed settings (Development/UAT/base have empty password fields). Remaining
  committed material is non-secret but environment-specific: base `appsettings.json` Genesys contact-list GUIDs / region,
  CRM base URL, `Special case` unit codes; `appsettings.UAT.json` contains a personal test mailbox/phone (excluded from publish;
  consider removing from the repository).
- Base `appsettings.json` keeps `TrustServerCertificate=True` on non-Production connection strings (`TigerCsDatabase`, `PACTRPT`).
  Production overrides both; the `PACTRPT` host must use a trusted certificate before Collections is enabled.
- `AllowedHosts: "*"` in the base file; set in Production once DNS is known.
- The API has no CORS policy (browser calls go through the Web server), no Hangfire dashboard is mapped, and Swagger is
  restricted to Development/Testing.
- `CustomerNotifications:IsTestMode="false"` is live routing — keep every outbound channel off until its Production
  dependency is verified.
- The tracked `publish/` directory (Release output from an earlier build) contains `appsettings.Development.json` and
  `appsettings.UAT.json` and should not be deployed or reused; consider removing it from version control.
