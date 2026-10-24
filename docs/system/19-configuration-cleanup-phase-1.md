# 19. Configuration cleanup - Phase 0 (verification) and Phase 1 (safe cleanup)

> Part of the [documentation set](README.md). **Reflects `main` as of the second sync into this branch (commit `ab7b839`)**; the base settings changed on `main` while this work was in progress (including `BackgroundJobs`, `EmailNotifications`, `VerificationTesting` and `FinancialSourceValidated` being set back to `false`) and are described as they are now. Scope: configuration **files** only. No code logic, schema, job schedule or feature switch was changed, nothing was deployed. Secret values are never reproduced in this document; where a value is sensitive only its presence or emptiness is stated.

## 19.1 Configuration precedence (verified from the code)

Both `TigerCS.Api/Program.cs` and `TigerCS.Web/Program.cs` use `WebApplication.CreateBuilder(args)` and add **no custom configuration source** (no `AddJsonFile`, `AddEnvironmentVariables` or `ConfigurationBuilder` in either). The framework default therefore applies, lowest to highest priority:

1. `appsettings.json`
2. `appsettings.{EnvironmentName}.json`
3. user-secrets - **only when the environment name is `Development`** (the Api has a `UserSecretsId`)
4. environment variables (`Section__Key`)
5. command-line arguments

| Environment | Files loaded from the repository | Everything above is layered on top by the host |
|---|---|---|
| Development | `appsettings.json` + `appsettings.Development.json` | user-secrets, env vars |
| UAT | `appsettings.json` + `appsettings.UAT.json` | env vars |
| Production | `appsettings.json` only (**no** `appsettings.Production.json` is committed) | env vars |
| any other name / name unset | `appsettings.json` only (an unset name defaults to `Production` in .NET) | env vars |

How the environment *name* is chosen on a server is **not verifiable from the repository**: `launchSettings.json` sets `Development` for local runs only, and the committed `web.config` sets no environment variables. A mis-named environment silently skips `appsettings.UAT.json`.

Arrays merge **by index** across files and never shrink; the cleanup below does not touch any array whose values differ between files.

## 19.2 Effective values: what is verified and what is not

"Verified" means derived from committed files and code. Anything supplied by the host (environment variables, user-secrets, IIS settings, a Production file) is **unverified** - it cannot be seen from the repository, and no value has been assumed.

| Setting | Code default | Development | UAT | Production (base only) | Server override |
|---|---|---|---|---|---|
| `BackgroundJobs:Enabled` | `true` | `false` | `false` | `false` | **Unverified** |
| `Collections:ReceivablesSnapshot:RefreshEnabled` / `RefreshOnStartup` | `true` / `true` | `true` / `true` | `true` / `true` | `true` / `true` | Unverified (inert while jobs are off) |
| `Collections:ReceivablesSnapshot:RefreshCron` / zone | `0 */2 * * *` / `Asia/Dubai` | same | same | same | Unverified |
| `CollectionsSource:CrmOwners:Enabled` | `false` | `true` | `true` | `true` | Unverified (inert while jobs are off) |
| `CollectionsSource:CrmOwners:RefreshOnStartup` / cron | `true` / `15 * * * *` (server local time) | same | same | same | Unverified |
| `Genesys:Enabled` | `false` | `true` | `true` | `true` | Unverified |
| `Genesys:CustomerInactivityTimeoutMinutes` / `AwaitingReplyTimeoutMinutes` / `DefaultTicketPriority` | `10` / `5` / `Normal` | same | same | same | Unverified |
| `Genesys:ScreenPopWebBaseUrl`, `ClassificationDepartmentCode` | unset | not committed | not committed | not committed | Unverified (screen pop answers 503 until the URL is set) |
| `Collections:GenesysOutbound:Enabled` / `LiveCustomerDispatchEnabled` / `SuppressionEnabled` | `false` | `false` | `false` | `false` | Unverified |
| `Collections:Campaigns:FinancialSourceValidated` / `LegalNoticeExportEnabled` / `LegalCaseDispatchApproved` | `false` | `false` | `false` | `false` | Unverified |
| `EmailNotifications:Enabled` / `Provider` | `false` / `Smtp` | `false` / `Smtp` | `false` / `Smtp` | `false` / `Smtp` | Unverified (`true` needs the SMTP settings or the host will not start) |
| `CrmDocuments:Enabled` | `false` | `true` | `false` | `false` | Unverified |
| `VerificationTesting:Enabled` | `false` | `true` | `true` | `false` | Unverified. Allowed only in Local/Development/UAT/Testing and needs valid overrides: `true` on a host named Production **refuses to start**. |
| `Tasleeh:Provider` | `Mock` | not committed | not committed | not committed | Unverified (the code default serves a fixture customer) |

Background jobs are **off** in every committed Api settings file, matching the test that pins this. On a real server they only run if an override turns them on (`BackgroundJobs__Enabled=true`, as `docs/system/17` expects); that override cannot be seen from the repository. If it is on, each application start triggers one PACT pull per company (`RefreshOnStartup`) and one CRM-owners load.

## 19.3 Unused keys (Phase 0, item 4)

Method: every key in the five committed files was resolved to the options class its section binds to (property must exist) and searched for readers outside the declaring class.

* **Genuinely unused: one key.** `TigerCS.Web/appsettings.Development.json` -> `Logging:Api:BaseUrl`. Nothing reads it (the Web reads only `TigerCsApi:BaseUrl`), and `Logging:Api` matches no logging provider. It looks like `TigerCsApi:BaseUrl` pasted under the wrong parent.
* **Every other key has a reader.** Corrections to the earlier review: `CollectionsSource:Currency` is **active** (read by the payment-summary, next-payment and unit-statement services), and `CollectionsSource:PactReceivables:ConnectionStringName` / `CrmConnectionStringName` are active keys that merely equal their code defaults - both are kept.
* **Dormant, not unused (kept):** `SpecialCaseUnitCodes` (50 codes), `PdcExcludedUnitCodes`, `SpecialCasesConfigured`. They are read, but only by the direct PACT reader's legacy-exclusion path, which runs only if `ApplyLegacyExclusions` is `true`; the live snapshot reader throws if that flag is `true`. Moving them to SQL is a Phase 3 item.

## 19.4 Removed and relocated settings

### `src/TigerCS.Api/appsettings.Development.json`

The file layered over `appsettings.json` and repeated 127 of its 137 keys with identical values. Removing an identical key cannot change the effective value, because the base file supplies it. The file now contains only what differs from the base.

| Removed from Development (value identical to `appsettings.json`) | Effective Development value now comes from |
|---|---|
| `Logging`, `AllowedHosts` | `appsettings.json` |
| `Crm` (`Provider`, `BaseUrl`), `Pact`, `PactApi` | `appsettings.json` |
| `ConnectionStrings` (3 entries; no password in any) | `appsettings.json` |
| `Collections` (`Enabled`, `CollectionsDepartmentCode`, `TimeZoneId`, `StaleAfterMinutes`, `Authorization`, `ResponseTickets`, `Channels`, `SchedulerOwner`, `BusinessRulesConfirmed`) | `appsettings.json` |
| `CollectionsSource` (all keys incl. `PactReceivables` with the 50 `SpecialCaseUnitCodes`, `NextPayment`, `Provider`, `EdsmProvider`, deadlines) | `appsettings.json` |
| `BackgroundJobs` (`Enabled: false`), `Genesys` (4 keys) | `appsettings.json` |
| `CrmDocuments` (all keys except `Enabled`, `OtpSmsEnabled`) | `appsettings.json` |
| `EmailNotifications` (all keys, including the SMTP username; `Password` is empty in `src/`), `CustomerNotifications` (all except `PublicBaseUrl`, `TestEmail`, `TestPhone`), `Crm:DocumentFileHosts` | `appsettings.json` |
| `VerificationTesting:BlockRealCustomerDelivery` | `appsettings.json` |

**Kept (genuinely Development-specific, 10 keys):** `Sms:Provider=Fake`, `Sms:Fake:LogMessageText`, `CrmDocuments:Enabled` and `OtpSmsEnabled` (`true`), `CustomerNotifications:PublicBaseUrl`, `TestEmail` and `TestPhone`, and `VerificationTesting:Enabled` (`true`) with its `SmsOverride` and `EmailOverride` (Development-only contact details that differ from the base).

### `src/TigerCS.Web/appsettings.Development.json`

| Change | Reason |
|---|---|
| Removed `Logging:Api:BaseUrl` | The one unused key (19.3). |
| Removed `Logging:LogLevel` | Identical to `appsettings.json`. |
| Kept `DetailedErrors`, `TigerCsApi:BaseUrl` | Differ from the base. |

### Not moved, deliberately

* **Environment-specific values in the shared base** (`Crm:BaseUrl`, `PactApi:BaseUrl`, the SQL server/user in the connection strings, the Genesys contact-list IDs, Web `TigerCsApi:BaseUrl=http://localhost:5179/`, the Genesys/UAT staff e-mail and phone overrides). Production loads **only** the base file, so moving any of them out would change Production unless every server already overrides it - which cannot be verified. They are listed in 19.7 for a later, coordinated change.
* **`appsettings.UAT.json`** was left as is. Its entries that equal the base (test-mode and `BlockRealCustomerDelivery` flags) are deliberate explicit safety pins for UAT.

### Test changes (assertion, not behaviour)

Two tests asserted the *text* of `appsettings.Development.json`, which no longer holds those keys. They now assert the **effective** value (base file with the Development file on top), which is what the application reads:

* `CrmProviderRegistrationTests.CommittedApiSettings_UseHttp_AndCarryNoCrmSecretKey` - still asserts `Crm:Provider == "Http"` and that no file carries `Crm:SecretKey`.
* `CustomerEmailRegressionTests.ShippedConfiguration_UsesTheOfficialLogo` - still asserts the official `LogoUrl` and that the old logo path is absent from the file text.

## 19.5 Production template

`src/TigerCS.Api/appsettings.Production.template.json` (new). It is **not** named `appsettings.Production.json`, so no environment loads it; an operator copies it deliberately. It lists the switches a Production deployment must decide (background jobs, startup refresh, CRM owners, provider values, customer test mode) with every value **equal to the current effective Production value** (checked key by key; 33 keys), marks the ones to decide `DECISION`, and lists - by name only - the secrets and per-environment endpoints that must come from the secret store. It contains no secret and no URL. It is published with the Api like any `*.json` content but is never read at runtime, and `CommittedSecretsGuardTests` scans it with the other settings files.

## 19.6 `publish/` review

`publish/` is committed build output (230 files). `main` regenerated it on 2026-10-22; `src/` has moved since.

| File | Finding |
|---|---|
| `publish/TigerCS.Api/appsettings.json` | 199 keys against 212 in `src/`. **9 values disagree with `src/`**: `BackgroundJobs:Enabled`, `EmailNotifications:Enabled`, `VerificationTesting:Enabled`, `Collections:Campaigns:FinancialSourceValidated`, `LegalNoticeExportEnabled` and `LegalCaseDispatchApproved` are `true` in `publish/` and `false` in `src/`; the `VerificationTesting` overrides and `EmailNotifications:Password` also differ. 14 keys exist only in `src/` (reconciliation settings, `CustomerNotifications:*`), and the admin report recipient exists only in `publish/`. |
| `publish/TigerCS.Api/appsettings.Development.json` | A full copy (128 keys) of the Development settings as they were before this cleanup; `src/` Development now holds 10 keys. Its effective values equal the base it ships with, so it is redundant. |
| `publish/TigerCS.Web/appsettings.json` | Identical to `src/`. |
| `publish/TigerCS.Web/appsettings.Development.json` | Still carries the dead `Logging:Api:BaseUrl` and the duplicate `Logging:LogLevel`. |
| **Secrets** | **`EmailNotifications:Password` is non-empty in `publish/TigerCS.Api/appsettings.json` and `publish/TigerCS.Api/appsettings.Development.json`** (an SMTP mailbox password, 11 characters; the value is not reproduced anywhere). It was also non-empty in `src/TigerCS.Api/appsettings.json` and `.Development.json` across 14 commits on `main` (from `43c834c` to `b96e5a3`) and has been blanked in `src/` on `main` since. Because it is in git history and in `publish/`, treat it as exposed: **rotate the mailbox password** and keep `EmailNotifications__Password` in the secret store. Every other secret-named value in the committed settings files is empty (connection-string passwords, `PactApi:ApiKey`, `Sms:Broadnet:Password`, `Collections:GenesysOutbound:ClientSecret`). Presence and emptiness only were inspected. |

An earlier version of this document and of the PR discussion said every committed secret-named value was empty; that was checked before this content reached the branch and was **not repeated after merging `main`**, so it was wrong for the `4fb11c1` snapshot. `CommittedSecretsGuardTests` did not catch it because it checks only `Crm:SecretKey` and connection-string passwords.

**Not changed.** The `publish/` files belong to a compiled build; editing them would create a hybrid artifact that matches neither the binaries nor `src/`, and removing the password could stop e-mail on a server that was deployed from them. Decision for the owner: blank the value (and rotate), then regenerate `publish/` from `src/` or stop committing it. Until then, deploying `publish/` as committed would turn on jobs, e-mail, verification testing and the legal/sign-off flags that `src/` has off.

## 19.7 Remaining risks and follow-ups

1. **A leaked SMTP password:** see 19.6. Rotate it; it remains in `publish/` and in history.
2. **Startup refresh (if jobs are switched on by an override):** `RefreshOnStartup=true` triggers a PACT pull per company and a CRM-owners load on every start. The CRM-owners cron runs in server local time, and no overlap guard (SQL lock or Hangfire concurrency filter) was found in its service, store or job.
3. **`Tasleeh:Provider` is unset everywhere**, so the code default `Mock` (fixture customer) applies unless a server overrides it. `docs/system/12` says to set `Unavailable`.
4. **Production and the template** share the base file's non-secret environment values (internal URLs, SQL server and user name, localhost Web API URL). Production must override `TigerCsApi:BaseUrl` for the Web.
5. **Personal data in committed files:** `VerificationTesting:SmsOverride` / `EmailOverride`, `CustomerNotifications:TestEmail` / `TestPhone` (Development, and the UAT file), the UAT `AdminNotification:Recipients` and the SMTP username hold a real phone number and staff e-mail addresses; the same values are in `publish/` and in history. Moving them to user-secrets / environment variables needs an agreed change, because `VerificationTesting:Enabled=true` requires valid overrides or the host refuses to start.
6. **`CommittedSecretsGuardTests` covers only `Crm:SecretKey` and connection-string passwords** (this is how a committed `EmailNotifications:Password` went unnoticed), not `PactApi:ApiKey`, `EmailNotifications:Password`, `Sms:Broadnet:Password`, `GenesysOutbound:ClientSecret`, `Jwt:SigningKey` or `CrmDocuments:OtpCodePepper`.
7. **Documentation drift:** `DEV-SETUP.md` shows `BackgroundJobs:Enabled` as `true` "default", which is the code default but not the committed value; older documents may still describe both Api files as carrying connection strings.
8. **Dormant exclusion list** (19.3) still sits in `appsettings.json`; hardcoded company IDs (4, 32) and unit-prefix exclusions in code remain until the SQL migration phase.
9. **Servers that rely on `publish/` or on the earlier committed `true` values** (jobs, e-mail, verification testing, sign-off and legal flags) would lose those behaviours when deployed from `src/`; confirm each server's real overrides before the next deployment.
