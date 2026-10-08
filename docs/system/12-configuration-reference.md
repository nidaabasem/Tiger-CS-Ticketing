# 12. Configuration reference

> Part of the [documentation set](README.md). Source: `src/TigerCS.Api/appsettings.json` and the `*Options` classes. Secrets are **never** committed: set them as environment variables (`Section__Key`), user‑secrets or the host's secret store. `<…>` marks a placeholder.

## 12.1 Secrets and connection strings

| Key (env var) | Purpose | Placeholder |
|---|---|---|
| `ConnectionStrings__TigerCsDatabase` | TigerCS DB (also Hangfire storage) | `Server=<host>;Database=TigerCsTicketing;User Id=<user>;Password=<secret>;TrustServerCertificate=True` |
| `ConnectionStrings__PACTRPT` | PACT reporting DB (receivables, campaigns) | read‑only login recommended |
| `ConnectionStrings__CrmDatabase` | CRMDB (receivables contact enrichment) | read‑only login |
| `Jwt__SigningKey`, `Jwt__Issuer`, `Jwt__Audience`, `Jwt__ExpirationMinutes` | API tokens (web and service accounts) | `<≥32 random chars>` |
| `Crm__SecretKey` | `X-SECRET-KEY` for Tiger CRM | `<secret>` |
| `PactApi__ApiKey`, `PactApi__BaseUrl` | PACT customer API | `<secret>` |
| `EmailNotifications__Password` | SMTP password | `<secret>` |
| `CrmDocuments__OtpCodePepper` | **Required** (startup guard) when `CrmDocuments:Enabled=true` outside Development | `<random ≥32 chars>` |
| `Genesys__ScreenPopWebBaseUrl` | Public base URL of TigerCS.Web for Screen Pop links | `https://<web-host>` |

> **Rotate** the SQL password, CRM `SecretKey` and PACT `ApiKey` that appear in git history (see [15 R‑42](15-requirements-traceability-matrix.md)).

## 12.2 Feature switches and behaviour (committed defaults)

| Key | Default | Effect / notes |
|---|---|---|
| `Crm:Provider` | `Http` | `Http` real CRM; `Mock` only in Development/Testing (startup guard) |
| `Pact:Provider` | `Http` | |
| `BackgroundJobs:Enabled` | `false` | **Must be `true`** for outbox/e‑mail dispatch, SLA sweeps, chatbot inactivity closure, reminder scheduler |
| `Genesys:Enabled` | `true` | `false` → every `/api/genesys/*` returns 503 |
| `Genesys:CustomerInactivityTimeoutMinutes` | `5` | duration unconfirmed (D2) |
| `CrmDocuments:Enabled` | `false` | OTP + document copy; Production also needs `AllowInProduction=true` |
| `CrmDocuments:AcceptedVerificationMethods` | `Otp`, `AuthenticatedDigitalUser` | |
| `CrmDocuments:Otp*` | lifetime 10 min, 5 attempts, 3 sends, 60 s resend, 5 challenges/customer/h | |
| `CrmDocuments:MaxAttachmentBytes` | 10 MB | |
| `EmailNotifications:Enabled` | `false` | when false no customer e‑mail (and document/OTP e‑mail return 503) |
| `EmailNotifications:FromName` | `Tiger Properties` | brand wording open (D3) |
| `Collections:Enabled` | `true` | **committed on** |
| `CollectionsSource:PactReceivables:Enabled` | `true` | **committed on**; uses `PACTRPT`; `StartDate` default 2026‑01‑01; `MaxSourceRows` 250000 |
| `CollectionsSource:Provider` | `Unavailable` | no financial source for outstanding/payments/reminders (503) |
| `CollectionsSource:EdsmProvider` | `Pact` | `Fixture` refused outside Development/Testing |
| `CollectionsSource:NextPayment:Enabled` | `false` | gated until EDSM semantics attested |
| `Collections:Campaigns:FinancialSourceValidated` | `false` | must stay false until remaining amounts reconciled; blocks Genesys export |
| `Collections:Campaigns:LegalNoticeExportEnabled` | `false` | |
| `Collections:Campaigns:MaxExportRows` | `5000` | |
| `Collections:Channels:{VoiceBot,Sms,Email}Enabled` | `false` | reminder sending off |
| `Collections:BusinessRulesConfirmed` | `false` | |
| `Collections:Authorization:IntegrationEmployeeIds` | `[]` | add the Genesys service account's employee id for reminder outcomes |
| `Authorization:ServiceIdentity:EmployeeIds` | `[]` | **list the Genesys/TigerGroupWeb service account here** (restricts it to integration routes, hides it from human lists) |
| `SlaAndEscalation:PriorityDowngrade:ExpiryHours` | `168` | how long a downgrade request stays decidable |
| `Tasleeh:Provider` | `Mock` | set `Unavailable` in UAT/production: no approved Tasleeh contract exists, `Mock` serves a fixture customer |

## 12.3 Web app (`TigerCS.Web/appsettings.json`)

`TigerCsApi:BaseUrl` (API root), cookie/session defaults. Dev only: `DevAdmin:*` (never in UAT/prod).

## 12.4 TigerGroupWeb (external — unverified)

`Ticketing:BaseUrl`, service‑account credentials, `Ticketing:CollectionsTimeoutSeconds` (27 s), Genesys OAuth client for scope `ticketing.genesys`. See [TigerGroupWeb-Proxy-Change](../Genesys/TigerGroupWeb-Proxy-Change.md).

## 12.5 Startup guards (fail fast)

Mock CRM outside Dev/Test · `CrmDocuments` in Production without `AllowInProduction` · `CrmDocuments` enabled without `OtpCodePepper` outside Development · Fixture Collections provider outside Dev/Test · recording e‑mail sender while e‑mail enabled · others in `Program.cs`.
