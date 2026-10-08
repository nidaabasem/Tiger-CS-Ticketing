# Audit findings - Collections, verification/documents, repository hygiene

Scope: tree at `31878f4` (base `a1cba71` + CRM document gateway + email OTP). Read-only audit; nothing was changed. Each finding was checked against the code (and git history where stated). Severity is for the intended use (real PACT data, real customers). External systems were not reachable, so findings about their behaviour are not included unless the repository itself proves them.

| ID | Sev | Area | One-line |
|---|---|---|---|
| F-1 | High (latent, bites 2027-01-01) | Campaigns | default due-date window starts 1 Jan of the preview year |
| F-2 | High | Secrets | real DB password, CRM secret key and PactApi key remain retrievable in git history |
| F-3 | Medium | Campaigns | no record of what was exported: no duplicate or cross-stage control |
| F-4 | Medium | Collections data | credits, overpayments and null amounts are dropped or abort the whole list |
| F-5 | Low-Medium | Verification | numeric `verificationMethod` bypasses the new "Otp is server-only" rule |
| F-6 | Medium | OTP | blank `OtpCodePepper` silently falls back to a constant HMAC key |
| F-7 | Low | Collections nav | Collections menu shown to every user (API is the real gate) |
| F-8 | Medium | Repo hygiene | 308 MB `publish/` tree (198 tracked files incl. a 96 MB zip), 6 root `.patch` files, a duplicate source tree, root SQL scripts |
| F-9 | Low | Config | committed defaults enable Collections + PACT reads and expose internal hosts/accounts |
| F-10 | Low | Secret guard | the secrets test scans only `src/` and `publish/` |
| F-11 | Low | Secrets | hard-coded dev `sa` password fallback in `TigerCsDbContextFactory` |

---

## F-1 Campaign default window drops arrears across a year boundary - High (latent)
* **Where:** `src/TigerCS.Application/Modules/Collections/Services/CollectionsCampaignAppService.cs:42` (`from = dateFrom ?? new DateOnly(date.Year, 1, 1)`), `:57` (pushed to the PACT procedure as `@StartDate`), `:89-90` (second filter). Policy cut-offs: `CollectionsCampaignPolicy.cs:280-285`.
* **Failure scenario:** On 2027-02-01 the OverdueReminder stage qualifies instalments due before 2027-01-01, but the read starts at 2027-01-01, so the stage returns zero rows - every 2026 arrear is invisible. On 2027-03-01 only January 2027 can qualify. LegalNotice (previous month) returns nothing on 12/14 Jan; LegalReferral (older than 3 months) returns nothing from January to March. `RangeNotes` only prints a note; nothing fails. Nothing breaks in 2026 because the hard-coded `StartDate` default is also 2026-01-01.
* **Fix (minimal):** default `from` to the earliest date any stage can need (e.g. `PactReceivablesOptions.StartDate`, not `1 Jan of date.Year`), or `min(configured StartDate, stage cut-off)`; add a unit test at 2027-02-01.
* **Status: FIXED** - the default `from` is `PactReceivablesOptions.StartDate`; the remaining calendar-year fallback was removed and a window ending before `StartDate` is now rejected (see 10 section 8).
* The receivables list has the same business cap by design (`PactReceivablesOptions.cs:23`, StartDate 2026-01-01): arrears before 2026 never appear (confirm this is intended).

## F-2 Secrets remain in git history - High
* **Where (history, current HEAD is clean):** commits `dda937f`/`5bdf991`/`20114e3`/`a6968e9` (TigerCsDatabase SQL password, in `src` and `publish` appsettings); `45a4ef3`, `2167279`, `c508775`, `79c0cd1`, `69f9120` (CRM `SecretKey`); `9ec3873`, `1b5e340`, `3b5d6af` (PactApi `ApiKey`, `SecretKey` in `publish/TigerCS.Api/appsettings.json` and `src/TigerCS.Api/appsettings.json`). Values are intentionally not reproduced here.
* **Scenario:** anyone with read access to the repository (or a clone/fork) can recover them with `git log -p`. Deleting them from HEAD does not revoke them. Hosts are internal IPs, but the same repo documents VPN-reachable addresses.
* **Fix:** rotate all three credentials now (DB login, CRM `TicketingSecretKey`, PACT/EDSM API key); then optionally rewrite history. Keep injecting through environment/user-secrets.
* **Current tree:** `src/*/appsettings*.json`, `publish/*/appsettings*.json`, `publish/TigerCS.zip` (its four appsettings), the root `*.patch`, the root `*.sql` scripts and `TigerCS_Collections_Campaigns/**` contain no non-empty password/key/secret (checked with pattern scans; connection strings end in `Password=;`). The only literals are test constants and dev/CI throwaways (F-11).

## F-3 Campaign export is not recorded - no duplicate prevention, no suppression after export - Medium
* **Where:** `CollectionsCampaignAppService` constructor (`:15-18`) has no repository; `ExportAsync` (`:174-195`) writes nothing.
* **Scenario:** the same Genesys CSV can be generated and loaded twice in a day; on day 14 CurrentMonthReminder and LegalNotice both list the same unit (documented in `docs/Collections/Collections-Campaigns.md`, no priority rule); a unit that pays after the export but before the call is still contacted, and no attempt/outcome is stored. The reminder pipeline has these protections (unique channel key, pre-send revalidation) but is not wired to campaigns and has no real financial source.
* **Fix (minimal):** persist `(recordId = company:tenant:unit:cycle:stage)` on `genesys` export and reject/skip already-exported records; define stage precedence for day 14; keep re-reading balances at export time.

## F-4 Credits, overpayments and null amounts - Medium
* **Where:** `PactReceivableCustomersAppService.cs:67` and `CollectionsCampaignAppService.cs:89` (`Amount > 0` only); `PactSqlReceivablesSource.cs:113` (null `DueDate`/`Amount` throws).
* **Scenario 1:** a customer with a credit/overpayment on one instalment and arrears on another: only the positive rows are shown; the credit is neither netted nor displayed, so the quoted amount can exceed the customer's real net debt (`AmountPrecision`/`ContradictoryPaymentStatus` checks do not catch it). **Scenario 2:** one row with a null `Amount` makes the entire receivables list and every campaign 503 (`FinanceUnavailable`) - safe, but a single bad PACT row blocks all staff. **Scenario 3 (EDSM, documented in code):** owned-company due/outstanding figures exclude instalments with no credit recorded (`CollectionsPaymentSummaryAppService.cs:616-617`); completeness is unverified.
* **Fix:** surface negative remaining amounts as a "credit on account" column or `NeedsReview` reason; log and quarantine single bad rows instead of aborting (or return partial with an explicit flag). Confirm EDSM behaviour with its owners.

## F-5 `verificationMethod: "3"` bypasses the OTP-only rule - Low-Medium
* **Where:** `VerificationSessionAppService.cs:80-90` compares the text `"Otp"` only; `:108` `Enum.Parse<VerificationMethod>` and `VerificationSessionsController.cs:69` `Enum.TryParse` accept numeric text (`"3"` = `Otp`; also undefined values such as `"0"` or `"99"` with no `Enum.IsDefined`).
* **Scenario:** a CS Agent posts `{"verificationMethod":"3", ...}`; a confirmed session labelled `Otp` with no proof is created and audited as `VerificationMethod=Otp`. Document release is still refused (it requires `ProofChallengeId`, `CrmDocumentCopyAppService.cs:161`), but tickets and audit now carry an unproven "Otp", and undefined enum values can be stored.
* **Fix:** parse names only (`!char.IsDigit`, then `Enum.IsDefined`), then run the Otp check on the parsed enum.

## F-6 OTP hash key falls back to a constant - Medium
* **Where:** `src/TigerCS.Application/Modules/CrmDocuments/Services/CustomerOtpAppService.cs:542`.
* **Scenario:** `CrmDocuments:OtpCodePepper` is unset by default and nothing validates it at start-up, so the stored HMAC key is the public string `tigercs-customer-otp`. A leaked `CustomerOtpChallenges` row (challenge id, salt, hash) lets an attacker brute-force the 10^6 code space offline in milliseconds. (Online guessing is limited to 5 tries.)
* **Fix:** require a non-empty pepper when `CrmDocuments:Enabled=true` (fail start-up like the Production guard), never fall back.

## F-7 Collections navigation and authorization - Low
* **Where:** `TigerCS.Web/Pages/Shared/_Nav.cshtml:14` (item always added), `TigerCS.Web/Program.cs:15` (`AuthorizeFolder("/")` only); Admin and Reports are gated (`:26`, `:31`) but Collections is not.
* **Scenario:** Reporting Users, Department Employees outside `COL` and any other signed-in user see "Collections" and land on a page that says they lack permission; no data is exposed because `CollectionsAuthorizationService` is enforced in the API (`CollectionsCore.cs:65-90`) before any source read (verified in `ListAsync`, `PreviewAsync`, `ExportAsync`). The "Campaigns" link and export button are shown on the Receivables page to everyone with `CanReadFinancials`, while export needs the stricter send grant (the preview flag `CanExportReview` hides the button).
* **Fix:** hide the item with a presentation-only policy mirroring `FinancialReadRoles` (as done for Reports); keep the API checks.
* **Confirmed OK:** all Collections routes require `AuthenticatedStaff` + explicit grants; System Administrator override is applied through `AuthorizationGate`; `IntegrationEmployeeIds` ships empty so outcome reporting and VoiceBot queueing are denied until configured.

## F-8 Stray artifacts in the repository - Medium
* **`publish/` (tracked, 198 files, 308 MB on disk):** `publish/TigerCS.zip` (96 MB), `publish/TigerCS.Web/TigerCS.Web.staticwebassets.endpoints.zip`, hundreds of DLLs, `web.config`, appsettings. It is a stale build (e.g. `publish/TigerCS.Api/appsettings.json` has no `PactReceivables` or `NextPayment` sections and fewer Collections keys than `src/TigerCS.Api/appsettings.json`) and `docs/DEV-SETUP.md` calls it "committed build output". `.gitignore` ignores `bin/obj` but not `publish/`; the repo `.git` is 174 MB.
* **Root patches:** `collections-due-overdue.patch`, `collections-campaigns.patch`, `TigerCS_Customer_Cards_Payments.patch`, `TigerCS_Remaining_Tests_Fix.patch`, `TigerCS_Test_Analyzer_Fix.patch` plus `TigerCS_Collections_Campaigns/collections-campaigns.patch` (byte-identical to the root copy) and `collections-due-overdue.txt`. Their content is already merged (two reverse-apply cleanly; three no longer apply, i.e. they are superseded), so applying one again would regress later fixes.
* **`TigerCS_Collections_Campaigns/` (20 tracked files):** a second copy of 18 source/test/doc files (`files/src/...`) outside the build; it will drift from `src/` and can be mistaken for the real code (it already differs from the later "From/To filters" commit).
* **Root SQL scripts (11):** `AddChannels.sql`, `AddCollectionsReminders.sql`, `AddGenesysIntegration.sql`, `ApplyAgentHandoffMigration.sql`, `UAT_Update_Final.sql`, etc. Generated EF migration scripts; they duplicate `src/TigerCS.Infrastructure/Persistence/Migrations` and can be applied out of order.
* **Fix:** `git rm -r publish TigerCS_Collections_Campaigns` and the root `*.patch`/`*.txt`; add `publish/` to `.gitignore`; move any still-needed SQL under a `deploy/` folder with an index; build artifacts belong in CI/release assets. Doing so does not remove history (see F-2).

## F-9 Committed defaults are not "off" - Low
* **Where:** `src/TigerCS.Api/appsettings.json` and `appsettings.Development.json`: `Collections:Enabled=true`, `CollectionsSource:PactReceivables:Enabled=true`, `EdsmProvider="Pact"`; plain-HTTP `PactApi:BaseUrl`; internal SQL hosts/logins (`10.10.10.117/94/124`, users `TigerCsTicketing`, `crmpact`, `crmdb`) and `TrustServerCertificate=True` on two strings. `CollectionsOptions.cs:10-15` states "everything that sends is off by default"; reads are not.
* **Scenario:** any environment given a database password immediately starts reading PACTRPT and exposes the receivables list to CS Agents and above. Sending remains off (channels false, scheduler None, jobs off).
* **Fix:** commit `Collections:Enabled=false` / `PactReceivables:Enabled=false` and enable per environment; keep infrastructure addresses in deployment configuration.

## F-10 Secret guard has narrow coverage - Low
* `src/TigerCS.Tests/CustomerVerification/Services/CommittedSecretsGuardTests.cs:31` scans only `appsettings*.json` under `src` and `publish`. Root patches, SQL, `TigerCS_Collections_Campaigns/`, docs and workflow files are unscanned. History is not scanned (F-2). **Fix:** scan all tracked text files with `git ls-files`, and add a CI secret scanner.

## F-11 Hard-coded development credentials - Low
* `src/TigerCS.Infrastructure/Persistence/TigerCsDbContextFactory.cs:20` falls back to a `sa` connection string with a documented sample password for design-time use; `.github/workflows/db-migration-validation.yml:50` uses a throwaway CI password. Both are local/CI-only, but the fallback runs for anyone executing `dotnet ef` without the variable. **Fix:** fail when `TIGERCS_DESIGN_TIME_CONNECTION` is unset.

---

## Verified, no defect found
* Collections authorization runs before configuration or data reads on every Collections service entry point; stale source data never authorises a send; reminder queueing is per account/type/cycle/channel unique with idempotency replay and concurrent-race handling (`CollectionsReminderAppService.cs:340-413`).
* Genesys campaign export cannot succeed with the committed configuration (`FinancialSourceValidated=false` adds `SourceReconciliationRequired` to every row; `:128`).
* Document release requires an owned, confirmed, unexpired session **with a server-recorded OTP proof**, re-confirms the customer with CRM, filters records to the verified unit/contact, re-checks fetched content, refuses unknown file types and oversize files, never returns the document, and masks the address in responses and audit.
* CSV output neutralises spreadsheet formula prefixes (`CollectionsCampaignCsv.Cell`).

## Not verifiable here (external)
PACT stored procedure semantics and deployment of the `V2` procedures; EDSM behaviour (null credits, caches, number culture); CRM `GetBuyerByPhone` / `GetCustomerDocuments` / file routes; SMTP; TigerGroupWeb forwarding; Genesys flows; whether the credentials in F-2 are still valid.
