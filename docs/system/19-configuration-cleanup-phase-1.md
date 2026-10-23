# 19. Configuration cleanup - Phase 0 (verification) and Phase 1 (safe cleanup)

> Part of the [documentation set](README.md). Scope: configuration **files** only. No code logic, schema, job schedule or feature switch was changed, nothing was deployed. Secret values are never reproduced in this document; where a value is sensitive only its presence or emptiness is stated.

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

| Setting | Code default | Committed (base) | Development | UAT | Production | Server override |
|---|---|---|---|---|---|---|
| `BackgroundJobs:Enabled` | `true` | `false` | `false` (inherited) | `false` (inherited, test-pinned) | `false` | **Unverified** |
| `Collections:ReceivablesSnapshot:RefreshEnabled` | `true` | `true` | `true` | `true` | `true` | Unverified |
| `Collections:ReceivablesSnapshot:RefreshOnStartup` | `true` | `true` | `true` | `true` | `true` | Unverified |
| `Collections:ReceivablesSnapshot:RefreshCron` / zone | `0 */2 * * *` / `Asia/Dubai` | same | same | same | same | Unverified |
| `CollectionsSource:CrmOwners:Enabled` | `false` | `true` | `true` | `true` | `true` | Unverified |
| `CollectionsSource:CrmOwners:RefreshOnStartup` / cron | `true` / `15 * * * *` (server local time) | same | same | same | same | Unverified |
| `Genesys:Enabled` | `false` | `true` | `true` | `true` | `true` | Unverified |
| `Genesys:CustomerInactivityTimeoutMinutes` / `AwaitingReplyTimeoutMinutes` / `DefaultTicketPriority` | `10` / `5` / `Normal` | same | same | same | same | Unverified |
| `Genesys:ScreenPopWebBaseUrl` | unset | **not committed in `src/`** | not committed | not committed | not committed | Unverified (screen pop answers 503 "not configured" until set) |
| `Genesys:ClassificationDepartmentCode`, `AutoRaiseHandoffOnConversationEnd` | unset / `false` | not committed | not committed | not committed | not committed | Unverified |
| `Collections:GenesysOutbound:Enabled` / `LiveCustomerDispatchEnabled` / `SuppressionEnabled` | `false` | `false` | `false` | `false` | `false` | Unverified |
| `Tasleeh:Provider` | `Mock` | **not committed** | not committed | not committed | not committed | Unverified (the code default serves a fixture customer) |

While `BackgroundJobs:Enabled` is `false`, `RefreshEnabled`, `RefreshOnStartup` and the CRM-owners switches are inert: the registration code returns before using them (`BackgroundJobServiceCollectionExtensions`). Enabling `BackgroundJobs` anywhere makes every start trigger one PACT pull per company and one CRM-owners load.

## 19.3 Unused keys (Phase 0, item 4)

Method: every key in the five committed files was resolved to the options class its section binds to (property must exist) and searched for readers outside the declaring class.

* **Genuinely unused: one key.** `TigerCS.Web/appsettings.Development.json` -> `Logging:Api:BaseUrl`. Nothing reads it (the Web reads only `TigerCsApi:BaseUrl`), and `Logging:Api` matches no logging provider. It looks like `TigerCsApi:BaseUrl` pasted under the wrong parent.
* **Every other key has a reader.** Corrections to the earlier review: `CollectionsSource:Currency` is **active** (read by the payment-summary, next-payment and unit-statement services), and `CollectionsSource:PactReceivables:ConnectionStringName` / `CrmConnectionStringName` are active keys that merely equal their code defaults - both are kept.
* **Dormant, not unused (kept):** `SpecialCaseUnitCodes` (50 codes), `PdcExcludedUnitCodes`, `SpecialCasesConfigured`. They are read, but only by the direct PACT reader's legacy-exclusion path, which runs only if `ApplyLegacyExclusions` is `true`; the live snapshot reader throws if that flag is `true`. Moving them to SQL is a Phase 3 item.

## 19.4 Removed and relocated settings

### `src/TigerCS.Api/appsettings.Development.json`

The file layered over `appsettings.json` and repeated 80 of its 88 keys with identical values. Removing an identical key cannot change the effective value, because the base file supplies it. The file now contains only what differs from the base.

| Removed from Development (value identical to `appsettings.json`) | Effective Development value now comes from |
|---|---|
| `Logging`, `AllowedHosts` | `appsettings.json` |
| `Crm` (`Provider`, `BaseUrl`), `Pact`, `PactApi` | `appsettings.json` |
| `ConnectionStrings` (3 entries; no password in any) | `appsettings.json` |
| `Collections` (`Enabled`, `CollectionsDepartmentCode`, `TimeZoneId`, `StaleAfterMinutes`, `Authorization`, `ResponseTickets`, `Channels`, `SchedulerOwner`, `BusinessRulesConfirmed`) | `appsettings.json` |
| `CollectionsSource` (all keys incl. `PactReceivables` with the 50 `SpecialCaseUnitCodes`, `NextPayment`, `Provider`, `EdsmProvider`, deadlines) | `appsettings.json` |
| `BackgroundJobs` (`Enabled: false`), `Genesys` (4 keys) | `appsettings.json` |
| `CrmDocuments` (all keys except `Enabled`, `OtpSmsEnabled`) | `appsettings.json` |
| `EmailNotifications` (all), `CustomerNotifications` (all except `PublicBaseUrl`) | `appsettings.json` |
| `VerificationTesting:BlockRealCustomerDelivery` | `appsettings.json` |

**Kept (genuinely Development-specific):** `Sms:Provider=Fake`, `Sms:Fake:LogMessageText`, `CrmDocuments:Enabled` and `OtpSmsEnabled` (`true`), `CustomerNotifications:PublicBaseUrl`, `VerificationTesting:Enabled` (`true`), `SmsOverride`, `EmailOverride`.

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

`src/TigerCS.Api/appsettings.Production.template.json` (new). It is **not** named `appsettings.Production.json`, so no environment loads it; an operator copies it deliberately. It lists the switches a Production deployment must decide (background jobs, startup refresh, CRM owners, provider values, customer test mode) with every value **equal to today's effective value**, marks the unverified ones `DECISION`, and lists - by name only - the secrets and per-environment endpoints that must come from the secret store. It contains no secret and no URL. It is published with the Api like any `*.json` content but is never read at runtime, and `CommittedSecretsGuardTests` scans it with the other settings files.

## 19.6 `publish/` review

`publish/` is committed build output (198 files, 149 DLLs). It was last touched on 2026-10-07; `src/` on 2026-10-23.

| File | Finding |
|---|---|
| `publish/TigerCS.Api/appsettings.json` | 46 keys against 211 in `src/`; all 46 match. 165 keys are missing (`Collections:ReceivablesSnapshot`, `Campaigns`, `Review`, `GenesysOutbound`, `CrmOwners`, `PactReceivables`, ...), so code defaults would apply if this build were run. |
| `publish/TigerCS.Api/appsettings.Development.json` | Diverged: has `Jwt:Issuer/Audience/ExpirationMinutes`, `Notifications:Email:*`, `Genesys:ScreenPopWebBaseUrl` and an empty `PactApi:ApiKey`; `EmailNotifications:Enabled=true` / `Provider=Recording` where `src/` has `false` / `Smtp`. |
| `publish/TigerCS.Web/*` | Identical to `src/`. |
| Secrets | Every secret-named value in the committed files is empty (connection-string passwords, `EmailNotifications:Password`, `PactApi:ApiKey`, `Sms:Broadnet:Password`, `Collections:GenesysOutbound:ClientSecret`). Presence and emptiness only were inspected. |

**Not changed.** The files belong to an older compiled build; editing them would create a hybrid artifact that matches neither the binaries nor `src/`. Recommendation (Phase 2 decision): stop committing `publish/`, or regenerate it from `src/` with `tools/build-release.sh` before any use, as `DEV-SETUP.md` already requires.

## 19.7 Remaining risks and follow-ups

1. **`BackgroundJobs:Enabled` is `false` in every committed file while the code default is `true`.** Production behaviour depends entirely on a server override that cannot be verified here. Confirm it on each server.
2. **Startup refresh:** if background jobs are or become enabled, `RefreshOnStartup=true` triggers a PACT pull per company and a CRM-owners load on every start. The CRM-owners cron runs in server local time, and no overlap guard (SQL lock or Hangfire concurrency filter) was found in its service, store or job.
3. **`Tasleeh:Provider` is unset everywhere**, so the code default `Mock` (fixture customer) applies unless a server overrides it. `docs/system/12` says to set `Unavailable`.
4. **Production and the template** share the base file's non-secret environment values (internal URLs, SQL server and user name, localhost Web API URL). Production must override `TigerCsApi:BaseUrl` for the Web.
5. **Personal data in committed files:** `VerificationTesting:SmsOverride` / `EmailOverride` (Development, UAT) and the UAT `AdminNotification:Recipients` hold a real phone number and staff e-mail addresses. Moving them to user-secrets / environment variables needs an agreed change so the overrides do not silently disappear.
6. **`CommittedSecretsGuardTests` covers only `Crm:SecretKey` and connection-string passwords**, not `PactApi:ApiKey`, `EmailNotifications:Password`, `Sms:Broadnet:Password`, `GenesysOutbound:ClientSecret`, `Jwt:SigningKey` or `CrmDocuments:OtpCodePepper`.
7. **Documentation drift:** `DEV-SETUP.md` shows `BackgroundJobs:Enabled` as `true` "default", which is the code default but not the committed value; older documents may still describe both Api files as carrying connection strings.
8. **Dormant exclusion list** (19.3) still sits in `appsettings.json`; hardcoded company IDs (4, 32) and unit-prefix exclusions in code remain until the SQL migration phase.
