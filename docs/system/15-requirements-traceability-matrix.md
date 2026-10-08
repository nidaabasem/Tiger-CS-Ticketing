# 15. Requirements traceability matrix and outstanding decisions

> Part of the [documentation set](README.md). Baseline `main` @ `a1cba71` + this review branch (integrated CRM‑documents/OTP commits and review fixes).
> **No requirement is marked "Deployed & verified"**: no UAT/production access or evidence was available to this review. "Verified" below always means *automated tests in this repo* (3396 pass) or *code reading*.

> Defect references like "F‑2" point into the audit file for that module: identity/lifecycle → [audit-findings-identity-lifecycle](audit-findings-identity-lifecycle.md); Genesys/customers → [audit-findings-genesys-customers](audit-findings-genesys-customers.md) (written "F‑02"); collections/services → [audit-findings-collections-services](audit-findings-collections-services.md).

**Status vocabulary** — **M** implemented & merged in `main` · **R** implemented here, in this review PR (previously on another branch or newly fixed) · **D** implemented, awaiting deployment/configuration · **G** gap in TigerCS code (not built) · **X** blocked by an external system/decision · **C** contradiction/unresolved decision. Evidence cites `src/…` paths; defect IDs refer to the `audit-findings-*.md` files.

<a id="branches"></a>
## 15.1 Branches and PR state

| Item | State |
|---|---|
| PRs #66–#77 | Merged into `main` (closed with merge timestamps); #76 closed unmerged and superseded by #77. No PR open at review start. |
| `claude/admiring-brown-73jnmx` (4 commits) | **Not in `main`.** Commit 1 (chatbot inactivity + document-copy API) is already in `main` in rewritten form; commits 2–4 (real CRM `GetCustomerDocuments` gateway, CRM contract specs, email‑OTP verification `/api/genesys/verification/*`, Genesys data actions) **were cherry‑picked into this review branch** with zero conflicts (3389 tests green before further fixes). |
| `claude/trusting-knuth-91lf1v` | One commit (configurable PACT `StartDate`); `main` already carries the 2026‑01‑01 default via #77 — treated as superseded. |
| Other unmerged `claude/*`, `security/*`, `feature/genesys-*` branches (≈30) | Old (Sept 2026), squash‑merged through later PRs or abandoned; not reviewed line‑by‑line. Candidates for cleanup. `claude/campaigns-final`, `…/dreamy-rubin…`, `…/stoic-albattani…`, `…/sweet-mccarthy…`, `…/dazzling-cerf…`, `collections/read-deadline`, etc. = squash‑merged PR heads. |
| TigerGroupWeb / TigerWebsite, Tiger CRM, mobile app | **Not accessible.** Only contracts exist here; see §15.5. |

## 15.2 Requirements matrix

| ID | Requirement (source) | Implementation (UI / service / API / DB / external) | Status | Evidence | Tests / verification | Remaining · owner |
|---|---|---|---|---|---|---|
| **Identity & administration** |||||||
| R-01 | Login, access denied, change/reset password, account mgmt (UAT‑2026‑10‑07 release) | `Pages/Login`, `AccessDenied`, `Account/ChangePassword`, `Admin/Users`; `AuthController`, `AdminUserAppService` | M | [03](03-roles-and-permissions.md) | Identity tests in suite | Deploy; password‑reset is admin‑initiated (no self‑service e‑mail reset) — confirm with business |
| R-02 | Roles incl. CS Agent/Supervisor/Manager, Dept Employee/Head, GM, CEO, Reporting, SysAdmin | `Roles.cs`, policies in `InfrastructureServiceCollectionExtensions.cs` | M | [03 §1–4](03-roles-and-permissions.md) | Policy/endpoint inventory tests | Chairman/CEO has write powers despite "read‑only" definition (F‑5), Reporting User grants little (F‑6) — **decision D11** |
| R-03 | Call Center Agent and AI Agent aligned with CS Agent permissions | Call Center Agent = CS Agent in dept `CC`; AI/Genesys integration = CS Agent service account | M | F‑8 | — | No distinct "Call Center Agent"/"AI Agent" roles exist; **C**: if distinct roles are wanted they must be created — decision D11 |
| R-04 | System Administrator central override; admin screens, routing mappings, audit history | ADR‑0024 override; `Admin/*`; `AdminGenesysController` (queue mappings), `AuditEntry` | M / G | [03 §2](03-roles-and-permissions.md) | Override tests | No UI page for queue mappings or audit history (API only) (F‑16) |
| **Customers & units** |||||||
| R-05 | CRM, PACT, Tasleeh lookup; phone normalization | `CustomerLookup*`, `CrmBuyerLookupAppService`, `PactCustomerHttpGateway`, `CustomerPhoneNumber` | M (Tasleeh: mock only) | [05](05-customer-identity-and-reconciliation.md); F‑08 | Phone tests | Tasleeh real gateway missing — X (owner: Tasleeh/IT) |
| R-06 | PACT‑first verification; CRM/PACT reconciliation into one card | New Ticket wizard unification (#70) | M (web wizard) / G (Genesys lookup does not reconcile, F‑10) | [05](05-customer-identity-and-reconciliation.md) | Wizard tests | Decide whether Genesys lookup must reconcile — D12 |
| R-07 | Exclude invalid unit 0 everywhere incl. fallbacks | CRM `UnitId<=0`, PACT `unitID 0` filters (#74); **ticket creation now rejects `CrmBuyerUnitId<=0` (this PR)** | R | `TicketCreationAppService.cs` | New theory (0, ‑1) | PACT `unitCode` fallback ids / EDSM `unitID` cast (F‑12) reviewed, low risk |
| R-08 | Expired‑contract filtering per screen | Wizard filters; Collections intentionally keeps | M | [05](05-customer-identity-and-reconciliation.md) | — | Flat screen‑pop list shows expired units unflagged (F‑11) |
| **Tickets** |||||||
| R-09 | New Ticket wizard; Department/Category/Request Type labels | `NewTicket` page (#69) | M | — | UI tests | — |
| R-10 | Unclassified Genesys tickets, queue‑mapping department, no fallback | `GenesysInquiryIngestionAppService` (422 `genesys-department-not-resolved`) | M / D | Readiness doc item 2 | Ingestion tests | UAT queue mappings must be created — owner: UAT admin |
| R-11 | `CurrentDepartmentId` ownership; write‑once `OriginatingDepartmentId` | `Ticket.cs:493` | M | audit (e) | Domain tests | — |
| R-12 | Lifecycle Open→InProgress→PendingCustomer→Resolved→Closed; Pending Third Party removed | `TicketStatusTransitions`, `TicketLifecycleAppService` | M; **R**: admin API no longer accepts new Pending Internal steps | F‑1 | New admin test | Legacy rows still readable by design; request‑type `AllowPendingInternal` flag still accepted (stored only) |
| R-13 | Reopen from Closed under latest policy (7 days, reason + department) | `ReopenAsync`, approvals | M | audit (f) | Lifecycle tests | Reopen approval is advisory (F‑12); **C**: "approval vs direct reopen" — D1/D13 |
| R-14 | Inactivity closure, timeout, resolution flags, audit | `ChatbotInactivityCloseJob`, `Genesys:CustomerInactivityTimeoutMinutes` (5) | D | Readiness item 1 | Fakes/SQLite only | `BackgroundJobs:Enabled=false` by default stops it — enable + confirm duration (D2) |
| R-15 | Returning customer / eligibility for reopening | new ticket per new conversation | M **C** | D1 | Regression test | Auto‑link/reopen not built — D1 |
| R-16 | Human handoff, unavailable agents, human follow‑up queues, Pending Interactions | `AgentHandoffAppService`, `PendingCustomerInteractionsController` | M | [04](04-ticket-lifecycle-sla-approvals-notifications.md) | Handoff tests | Legal queue (D6) |
| **SLA, approvals, notifications** |||||||
| R-17 | First Response SLA from first **human** reply | `FirstHumanResponseRecorder`; Genesys transcript path | M; **R**: Ticket Details "Record first response" action (F‑2: UI previously had none, so manual tickets breached) | F‑2 | Build + existing SLA tests; no new UI test | Business to confirm whether tickets created by an agent during a live call should auto‑record (proposal) |
| R-18 | Resolution SLA, calendars, pauses, escalation | `SlaDueDateService`, `SlaBreachProcessor` | M / G | F‑3 | SLA tests | Request‑type SLA and `PausesOnPendingCustomer` stored but not enforced |
| R-19 | Priority change, downgrade approval | — | G | F‑4 | — | Not implemented — D14 |
| R-20 | Notifications created/resolved/closed/reopened; dedupe; failures; language | Outbox + `EmailNotifications` | D | audit (g) | Outbox tests | English only, 24 h age limit, `EmailNotifications:Enabled=false` default; wording "Tiger Properties" vs "Tiger Group" (D3); Arabic templates (D5) |
| **Dashboard & navigation** |||||||
| R-21 | CS Manager sees CS Agents and Call Center staff; workload | Team Performance (#71) | M | audit (d) | Report tests | — |
| R-22 | Dashboard cards/order/channel/filters; Queue, My Tickets, Pending, Closed; double‑click | `Dashboard`, `Tickets` pages | M / G | F‑10, F‑11 | — | Awaiting‑human KPI computed not shown; no double‑click/row‑click handler |
| R-23 | Collections navigation and authorization | `/Collections/*`; `CollectionsAuthorizationService` | M | F‑7 | Auth tests | Nav item shown to all roles (API enforces) |
| **Collections** |||||||
| R-24 | Receivables list (PACT companies 4, 32), filters, selection | `PactReceivablesController`, `PactSqlReceivablesSource` (#73, #75) | M / D | [10](10-collections-and-campaigns.md) | Unit tests; **never run against PACTRPT** | Committed config enables source; ensure creds provisioned |
| R-25 | Verified meaning of due date / unpaid / remaining amount | `Collections/Pact-Receivable-Customers.md` | M (documented) **C** | [10](10-collections-and-campaigns.md) | — | Credits/negative amounts dropped (F‑4); amount conventions need Finance sign‑off |
| R-26 | Campaign preview / CSV export (communication policy 7/8 Oct 2026) | `CollectionsCampaignAppService` (#77) | M; **R**: default lookback uses configured `StartDate` (was 1 Jan of preview year → empty overdue/legal lists from Jan 2027) | `…CampaignAppService.cs` | Updated tests | `FinancialSourceValidated=false` blocks Genesys export; exports unrecorded — no cross‑stage suppression (F‑3) |
| R-27 | Genesys outbound campaign sync, outcomes, suppression | reminders API (gated), manual CSV only | G / X | [10](10-collections-and-campaigns.md) | — | No automated sync; Genesys config + business rules (D10) |
| R-28 | Instalments, payment history, next payment | EDSM provider | X | docs `EDSM-Instalment-Semantics` | — | Source returns 503/Unavailable; EDSM owner |
| **Customer services** |||||||
| R-29 | Customer verification & OTP | `VerificationSessionAppService`; **`CustomerOtpAppService` + `/api/genesys/verification/*` (integrated here)** | R | [11](11-verification-and-document-delivery.md) | 3396 tests incl. OTP; fixes: numeric method bypass, `tel:`, pepper guard | E‑mail only (no SMS/WhatsApp); needs CRM e‑mail on record; set `OtpCodePepper` |
| R-30 | Contract/reservation/receipt/layout copies, e‑mail delivery | `CrmDocumentCopyAppService`, real CRM `GetCustomerDocuments` gateway (**R**) | R / X | [11](11-verification-and-document-delivery.md) | Mock + contract‑sample tests | **CRM file routes unverified**; `CrmDocuments:Enabled=false`; real e‑mail with attachment not demonstrated |
| R-31 | B.P / P.R document meanings | — | X **C** | D8 | — | Meaning unconfirmed; not mapped |
| R-32 | Statement of Account → e‑mail | — | G / X | Readiness item 8 | — | Source system unnamed (D9) |
| R-33 | Unit details, expected/actual handover | `POST /api/genesys/customers/unit-details` (#72) | M / X | F‑03 (default 0 for numbers) | Tests | CRM `GetUnitDetails` not built → `NotAvailable`; proxy route pending |
| R-34 | Construction updates | — | G / X | — | — | Source needed (D9) |
| R-35 | Legal question → live agent | generic handoff | M / C | — | — | Legal queue (D6) |
| R-36 | CSAT after ticket | — | G | — | — | D7 |
| R-37 | Chatbot→app link, payment link | — | X | — | — | Mobile/payment provider (D4) |
| R-38 | EN/AR welcome messages & journeys | `Welcome-And-Arabic-Content.md` (drafts) | C / X | — | — | Genesys Architect content; approval (D3, D5) |
| **Genesys integration** |||||||
| R-39 | Contract parity: Data Actions ↔ TigerCS ↔ TigerGroupWeb | [08](08-genesys-integration-guide.md), [09](09-data-action-inventory.md) | R (fixes: 08 invalid JSON, 12 quoting, numbering clash 12→13‑16) | F‑02, F‑03, F‑15 | JSON validity script (see [16](16-uat-checklist.md)) | Proxy forwarding for unit‑details, send‑copy, verification/*, agent‑context, screen‑pop not confirmed (F‑14); `ticketing.genesys` OAuth lives only in TigerGroupWeb |
| R-40 | Idempotency, retries, unavailable‑source paths | `Idempotency-Key`, 503 codes | M | [08](08-genesys-integration-guide.md) | Tests | PATCH commits handoff before transcript validation (F‑05); transcript sender `Agent` documented but rejected (F‑06) |
| R-41 | Screen Pop separate from customer auth | `/api/genesys/screen-pop`, `/ScreenPop` | M; **R**: ANI `tel:` normalised | F‑04 | Updated test | `GENESYS_SCREEN_POP_NOT_CONFIGURED` until base URL configured |
| **Hygiene / security** |||||||
| R-42 | No secrets in repository | current tree clean; **history contains real SQL password, CRM SecretKey and PACT ApiKey** (commits dda937f, 5bdf991, 2167279, 9ec3873, 1b5e340) | **Action required** | audit F‑2 | secrets test scans `src/` and `publish/` only | **Rotate those credentials now**; consider history purge — owner: IT/security |
| R-43 | Repository hygiene | tracked `publish/` (198 files, 96 MB zip), 6 root `.patch`, duplicate `TigerCS_Collections_Campaigns/files/` | G | F‑8 | — | Not deleted in this PR (may be used by deployment) — decision D15 |

## 15.3 Unresolved contradictions

| # | Contradiction | Sources | Treatment |
|---|---|---|---|
| C1 | Readiness doc (#74) says OTP is "outside TigerCS" and the CRM document gateway is `Unimplemented`; branch `admiring-brown` implements both. | `UAT-Outstanding-Requirements-Readiness.md` vs `CRM-Required-Contracts.md` | Newer code wins; the readiness doc is marked superseded (this PR). |
| C2 | "Tiger Group" (requirement) vs "Tiger Properties" (e‑mail from‑name) | D3 | Not changed. |
| C3 | Collections default lookback: "1 January of preview year" (test name, #77) vs need to see prior‑year arrears | #77 vs policy | Changed to configured `StartDate` (2026‑01‑01) — **confirm** with Collections. |
| C4 | Inactivity timeout: committed 5 min vs unclear handwritten note | D2 | Unchanged, flagged. |
| C5 | `Collections:Enabled=true` + receivables enabled committed in `appsettings.json` while docs say features ship off | appsettings vs docs | Documented in [12](12-configuration-reference.md). |

## 15.4 Outstanding decisions

| ID | Decision | Owner |
|---|---|---|
| D1 | Returning customer after inactivity closure: new ticket (current) vs auto‑link/reopen | Business/CS |
| D2 | Inactivity duration | CS Manager |
| D3 | Brand wording Tiger Group vs Tiger Properties | Marketing |
| D4 | App destination & customer auth for chatbot→app; payment provider | Mobile/Product |
| D5 | Arabic scope (staff UI/RTL, customer e‑mails) and approved templates | Business |
| D6 | Legal queue/department/skill and fallback | Legal/CS |
| D7 | CSAT trigger, channel, repeat after reopen, scale | CS |
| D8 | Meaning of B.P and P.R | Sales Admin/CRM |
| D9 | SOA source; construction‑update source; handover semantics | Business/CRM |
| D10 | Collections open rules (`Collections-Integration.md` §10), reminder wording, SMS provider, scheduler owner | Finance/Collections |
| D11 | Distinct Call Center Agent / AI Agent roles? CEO/Reporting User powers? | Business/Security |
| D12 | Should Genesys customer lookup reconcile CRM+PACT? | Product |
| D13 | Reopen approval: advisory vs mandatory | CS Manager |
| D14 | Priority change/downgrade approval — build? | Business |
| D15 | Remove `publish/`, `*.patch`, stale folders from the repo | Repo owner |

## 15.5 External handover (not changeable from this repository)

| System | Needed change | Contract | Owner |
|---|---|---|---|
| TigerGroupWeb | Forward: `customers/unit-details`, `documents/send-copy`, `verification/{buyer-lookup,otp/send,otp/resend,otp/verify}`, `agent-context`, `screen-pop`, Collections reminder routes; verify `awaitingCustomerReply` body and `nextPayment` pass‑through | [TigerGroupWeb-Proxy-Change](../Genesys/TigerGroupWeb-Proxy-Change.md) | Web team |
| Tiger CRM | `GetUnitDetails`; document list/file routes; JSON 401 (not 302); confirm e‑mail data quality | [CRM-Required-Contracts](../Genesys/CRM-Required-Contracts.md) | CRM team |
| Genesys | Import data actions 01–16; Architect flows; queue ids for mappings; outbound campaign/DNC | [09](09-data-action-inventory.md) | Genesys admin |
| PACT/EDSM | Instalment/payment history sources, freshness | [10](10-collections-and-campaigns.md) | Finance IT |
| Mobile/payment | App link scheme, payment provider | — | Product |
