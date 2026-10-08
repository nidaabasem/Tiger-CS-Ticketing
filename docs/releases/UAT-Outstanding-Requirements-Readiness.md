# Genesys UAT — outstanding requirements: readiness matrix, contracts and UAT checklist

Scope: the 15 outstanding Tiger-CS-Ticketing / Genesys UAT requirements. Basis: `main` at `039ae51`
plus this branch. **Nothing here was exercised on UAT** — this environment has no network path to
UAT, CRM, PACT, EDSM, SMTP or Genesys. "Verified" below means verified by the automated suite
(`dotnet test src/TigerCS.slnx -c Release`: 3076 passed at baseline) or by reading the code.
Never read a row as "works on UAT" unless the UAT column says it was run.

## 1. What this change set contains

| Change | Where |
|---|---|
| Placeholder unit 0 excluded from CRM buyer units (`UnitId <= 0`) and PACT contract rows (`unitID` 0). Previously a PACT `unitID: 0` surfaced as selectable unit `"0"`. Tests added. | `CrmBuyerLookupAppService.cs`, `PactCustomerHttpGateway.cs` |
| Regression test pinning "same customer, new conversation after a closure = new ticket; earlier ticket untouched" | `GenesysInquiryIngestionAppServiceTests` |
| Stale XML comment on the Reopen role set corrected (Reopen Approval path does exist) | `TicketRoleSets.cs` |
| Welcome / Arabic content pack (drafts, need approval) | `docs/Genesys/Welcome-And-Arabic-Content.md` |
| UAT queue-mapping requirements and verification | `docs/Genesys/uat-routing/Queue-Mapping-Requirements.md` |
| This matrix, contract index, decisions, UAT checklist | this file |

No new API routes, no migrations, no new configuration keys. The audit found most of the
required capabilities already exist (and are gated or unconfigured) or are blocked on systems
outside this repository; duplicating routes would not unblock them.

## 2. Readiness matrix

Legend: **A** verified complete · **B** implemented, awaiting deployment/configuration · **C** partially complete · **D** blocked.
No item is **A** for UAT, because nothing was run on UAT.

| # | Requirement | Status | Owner of remaining work | Evidence / what is missing |
|---|---|---|---|---|
| 1 | Closure, timestamps, reopen | **B** | TigerCS deploy + config; business decision | Closure paths: human Resolve/Close (`TicketLifecycleAppService.ResolveAsync/CloseAsync`), inactivity job (`ChatbotInactivityCloseJob` → `CloseForCustomerInactivityAsync`). Genesys/AI has **no** closure route (PATCH contract has no status field; customer-confirmed-resolved records an event only). Audit/status history carry time, reason, actor/system. Reopen of inactivity closures: CS Agent/Supervisor/Manager (+SysAdmin override), 7-day window, reason + department required; manual Cancelled/Rejected/Duplicate stay final. Needs: `BackgroundJobs__Enabled=true`, the two SQL scripts, `Genesys__CustomerInactivityTimeoutMinutes` (**committed default 5; the handwritten timer note is unclear — business must confirm the duration, nothing was invented**). A returning customer gets a **new ticket** (no auto-link/auto-reopen — see decision D1). Tested on fakes + SQLite only; never on SQL Server/Hangfire. |
| 2 | Wrong department | **C** | UAT configuration (Genesys + Admin) | Code is correct and pinned by tests: explicit `departmentId` → explicit `departmentCode` → active queue mapping → else 422 `genesys-department-not-resolved`; no fallback; `CurrentDepartmentId` follows transfers, `OriginatingDepartmentId` write-once; handoffs use the ticket's current department; audit `GenesysInquiryIngested` records `DepartmentSource`. No seed/migration creates queue mappings, so the real cause of TG-FIN tickets is UAT data/flow (investigation SQL + correction script already exist). Remaining: the real queue ids → `Queue-Mapping-Requirements.md`; run `01_Investigate…sql`. |
| 3 | Chatbot → app | **D** | Mobile app + business decision | No deep link / universal link / app URL anywhere in the repo. Screen Pop is agent-facing, web-only (`/ScreenPop`, signs in a mapped *staff* user) and must not be used for customers. Needs: app destination, link scheme, and how the app authenticates the customer (decision D4). |
| 4 | Welcome message | **C** | Genesys Architect (text lives there) | TigerCS returns no greeting/menu/language; all configured in Genesys. Draft EN/AR chat and voice text, listing only supported services: `Welcome-And-Arabic-Content.md`. Needs wording approval and entry into the flows. |
| 5 | Arabic support | **C** | Genesys content/voice config; TigerCS templates after wording approval | TigerCS has no localisation: only `language` on Collections reminders (`en`/`ar`), and the reminder email provider **rejects `ar`** (no approved template). Customer emails, document-copy email and all `/api/genesys` ProblemDetails text are English. Web staff UI is `lang="en"`, no RTL (staff UI is out of the customer journey; decision D5). API field names stay English by design. Arabic bot/voice wording is a Genesys deliverable (draft provided). |
| 6 | Collections testing / instalments | **C** | EDSM/PACT owners; Genesys flow | Existing routes and data actions 08/09; identifier provenance below. Unit 0 now excluded (this change). `nextPayment` is gated OFF and returns `Unavailable`; EDSM instalment semantics unverified (`docs/Collections/EDSM-Instalment-Semantics.md`) — no instalment answer is derived from ambiguous dates or statuses. Per-account routes return 503 (`CollectionsSource:Provider=Unavailable`). Expired contracts are filtered in the New-Ticket wizard but deliberately kept in Collections mapping. Not run against real PACT/EDSM. |
| 7 | Document copies | **D** | Tiger CRM (document routes) + TigerGroupWeb (forwarding) | TigerCS flow built and tested vs Mock only (verification-session ownership, selection, idempotency, email attachment). Real CRM gateway is `Unimplemented` → 503 `DOCUMENT_SOURCE_UNAVAILABLE`; ships `CrmDocuments:Enabled=false`. No file/download route exists in TigerCS by design. Open: what "B.P" and "P.R" mean (kept as open questions, **not** mapped); "layout" ≈ existing `UnitLayout` type (to confirm). Needs the CRM routes in `CRM-Document-Operations-Requirements.md`. No real CRM retrieval or SMTP delivery has been demonstrated. |
| 8 | Statement of Account → email | **D** | A SOA source (CRM/PACT/EDSM document API) — business to name it | No SOA generation or delivery exists in code or docs; no source system provides one (Collections docs say so). Email infrastructure and `CrmDocumentDeliveryRequests` could carry it once a source exists; building a generator from payment rows would be an unsupported financial document. |
| 9 | Legal → live agent | **C** | Genesys queue/skills + business decision | Generic handoff exists (`PATCH /api/genesys/tickets/{id}` `handoff`, trigger `RoutingDecision`/`AiEscalated`, free-text `reason`, conversation + customer context kept on the ticket). No Legal queue, department or topic field exists; handoff goes to the ticket's current department. Needs decision D6 (Legal queue/department) then a queue mapping; bot wording in the content pack never gives legal advice. |
| 10 | Unit details | **C** | Tiger CRM (`GetUnitDetails`) + TigerGroupWeb forwarding | `POST /api/genesys/customers/unit-details` done and tested (ownership, selection, error contract). Extended (2026-10-08) with project completion %/dates and the customer's sold price + registration cost (sale only with a server-recorded verification proof and only for the buyer-lookup Lead; `financialDetailsStatus`). CRM route not built (CRM source not accessible; spec in `CRM-GetUnitDetails-Implementation-Spec.md`) → only buyer-lookup fields; the rest `null`, `detailsStatus:"NotAvailable"`. TigerGroupWeb forwarding not built (source not accessible). Not run against real CRM or the public site. CRM/PACT duplicate reconciliation not present in this API (CRM-only by design). |
| 11 | Handover date | **C** | Tiger CRM | Four separate fields (unit/project × expected/actual) in unit-details, never substituted from contract/construction dates; all `null` until CRM `GetUnitDetails` exists. Source/semantics of "expected" vs "scheduled" to be confirmed by CRM. |
| 12 | Construction update | **D** | Business to name a source; CRM/service endpoint | Nothing exists. Only request type `CS-GEN-003` and an unbuilt `project.status`. Fallback today: human handoff / ticket. Required: an approved endpoint returning latest update text + date. |
| 13 | Review after every ticket | **D** | Business decision | No CSAT/survey implementation. Docs (BR-013/BR-023) say: send on Closed with outcome Resolved; Cancelled/Rejected never; second survey after reopen undecided (ISSUE-009); SMS provider not chosen. Decision D7. Natural single trigger when agreed: the existing `TicketClosed` outbox event (once per closure) with a unique per-ticket-closure key. |
| 14 | Payment link → app | **D** | Payment provider + mobile app | No provider, link, callback or payment-session code exists. Nothing to reuse; not invented. |
| 15 | Collection reminder calls | **C** | Finance/EDSM owners, Genesys outbound, business | TigerCS side implemented, gated off: eligibility (`ReminderPolicy`), candidates → queue with `Idempotency-Key`, dedupe keys, attempts/outcomes, suppression of settled accounts at dispatch, EN/AR field, human follow-up via handoff, tables `CollectionsReminders*` (migration not applied anywhere). Blocked: financial source `Unavailable` (EDSM lacks due dates/freshness) so candidates return 503; business rules unconfirmed (`BusinessRulesConfirmed=false`, 9 open decisions in `Collections-Integration.md` §10); no Arabic voice/SMS/email wording; no SMS provider; **Genesys outbound campaign/contact list/DNC/dial number is Genesys configuration, TigerCS never dials**; TigerGroupWeb forwarding of reminder routes pending. No call was placed. |

## 3. REST contract index (existing `/api/genesys` surface — no new routes)

All calls: Genesys Data Action → TigerGroupWeb (validates Genesys OAuth scope `ticketing.genesys`,
uses its own TigerCS service-account JWT, never forwards the Genesys token) → TigerCS
(`Authorization: Bearer <service JWT>`; policy `CustomerVerification` = CS Agent/Supervisor/SysAdmin;
Collections routes `AuthenticatedStaff` + financial-read roles). Feature flag `Genesys:Enabled`
(503 when off). Full request/response samples and error tables live in the existing contract docs;
they were not re-derived because no route changed.

| Capability | Method + path | Contract doc | Data Action | Key error statuses (from code) | TigerGroupWeb forwarding |
|---|---|---|---|---|---|
| Customer lookup | `GET /api/genesys/customers/lookup?phoneNumber=` | `Genesys-Cloud-Configuration.md` | 01 | 503 disabled | in place |
| Create/reuse ticket | `POST /api/genesys/tickets` | `Genesys-Cloud-Configuration.md` | 02 | 200/201, 400, **422 department not resolved**, 503 | in place |
| Update (routing, end, transcript, handoff, confirmed-resolved, `awaitingCustomerReply`) | `PATCH /api/genesys/tickets/{id}` | `Genesys-Cloud-Configuration.md`, `Chatbot-Inactivity-UAT-Configuration.md` | 03–07, 10 | 400, 404, 409, 422, 503 | `awaitingCustomerReply` pending |
| Unit details / handover | `POST /api/genesys/customers/unit-details` | `Customer-Unit-Details-API.md` | 12 | 400, 403 `CUSTOMER_NOT_VERIFIED`/`UNIT_NOT_ELIGIBLE`, 409 `CUSTOMER_AMBIGUOUS`, 502 `CRM_UNAVAILABLE` | **pending** |
| Document copy | `POST /api/genesys/documents/send-copy` (+ `Idempotency-Key`) | `Document-Copy-API.md` | 11 | 400, 403, 404, 409, 422, 501, 502, 503 | **pending** |
| Collections summary / transactions | `GET /api/genesys/collections/customers/by-key/{customerKey}/payment-summary`, `…/payment-transactions` | `docs/Collections/Genesys-Collections-API.md` | 08, 09 | 404 `AccountNotFound`, 422, 503 | implemented, not deployed |
| Collections reminders | `GET reminders/candidates`, `POST reminders`, `POST reminders/{id}/outcomes`, `GET customers/{id}/reminders` | `docs/Collections/Genesys-Collections-API.md` | none yet | 409 `CandidateChanged`/`IdempotencyConflict`/`ReminderSuppressed`, 422, 503 | **pending** |
| Screen Pop (agent only) | `POST /api/genesys/screen-pop` | `Genesys-Cloud-Configuration.md` §7 | none | 503 `GENESYS_SCREEN_POP_NOT_CONFIGURED` | n/a |

### Identifier provenance (never guessed by Genesys)

| Identifier | Source |
|---|---|
| `conversationId` | Genesys conversation |
| `ticketId` / `ticketNumber` | Response of `POST /api/genesys/tickets` |
| `customerReference` (`crm:{id}`, `ext:Pact:{tenantID}`) and `unitId` | `GET customers/lookup` → `screenPop.*` / unit-details `eligibleUnits` |
| `customerKey` | `ext:Pact:{tenantID}` from lookup `screenPop.externalCustomerId` when `verificationSource=="Pact"`; TigerCS must already hold the mapping (a ticket with that PACT customer selected by an agent) else 404 |
| `companyId`, tenant | Discovered by TigerCS from PACT `/v1/contracts/{mobile}`; not supplied by Genesys (except optional `companyId` filter on transactions, taken from the summary response) |
| `verificationSessionId` | `POST /api/verification-sessions` by the same integration account (method must be `Otp` or `AuthenticatedDigitalUser` for documents; **OTP issue/check is outside TigerCS**) |
| `recordId` (documents) | `choices` returned by a `SelectionRequired` response |
| CRM → PACT crosswalk | does not exist; CRM customers get `MappingNotAvailable` for Collections |

## 4. Deployment order (everything in this branch is backward compatible)

1. Confirm DB is at `20261005072511_AddCollectionsReminders` (or later). Run, in order and idempotently, if not yet applied: `AddCollectionsReminders.sql`, `AddChatbotInactivityAndCrmDocumentCopies.sql`, `AddResolutionClosedForCustomerInactivity.sql`. Optional: `ConfigureReopenApprovalRequirements.sql` (reviewed first). None of these has been run on SQL Server by the implementer.
2. Deploy API/Web build from this branch (code change is limited to unit-0 filtering).
3. Configuration (environment variables, no secrets in files): `Genesys__Enabled=true`, `BackgroundJobs__Enabled=true`, `Genesys__CustomerInactivityTimeoutMinutes=<confirmed value>`; leave `CrmDocuments__Enabled=false`, Collections reminder channels and `NextPayment` off until their blockers clear.
4. Create GenesysQueueMappings per `Queue-Mapping-Requirements.md`; run `01_Investigate_Finance_Routing_UAT.sql`.
5. Genesys: import Data Actions as they become unblocked; enter approved welcome/Arabic content.
6. TigerGroupWeb: forward the pending routes per `TigerGroupWeb-Proxy-Change.md`.

Rollback: redeploy the previous build; no schema change in this change set.

## 5. Decisions needed

| ID | Decision | Owner |
|---|---|---|
| D1 | When the same customer returns after an inactivity closure: keep "new ticket + agent may reopen the old one", or build auto-link/auto-reopen (needs an authorization model for the service identity; current Genesys integration account has no reopen path and none was granted) | Business/CS |
| D2 | Inactivity duration (committed default 5 min; handwritten note unclear) | CS Manager |
| D3 | Brand wording: "Tiger Group" (requirement) vs "Tiger Properties" (current emails) | Marketing |
| D4 | App destination + customer authentication for the chatbot→app link; payment provider for the payment link | Mobile/Product |
| D5 | Arabic scope for staff UI/RTL and customer emails; approved Arabic templates | Business |
| D6 | Legal queue/department and skill; fallback when no legal agent is available | Legal/CS |
| D7 | CSAT trigger (Resolved vs Closed), channel (email/SMS), repeat after reopen, scale | CS/Business |
| D8 | Meaning of B.P and P.R; CRM identifiers/types for them and for layout | Sales Admin/CRM |
| D9 | SOA source system; construction-update source; handover date semantics | Business/CRM |
| D10 | Collections: the 9 open rules in `Collections-Integration.md` §10, EDSM instalment semantics, reminder wording, SMS provider, scheduler owner | Finance/Collections |

## 6. UAT checklist (run only after the stated preconditions; record evidence)

| Check | Precondition | Pass criterion | Run? |
|---|---|---|---|
| Unit 0 never offered | PACT/CRM test phone with a placeholder unit | Customer search/unit lists show no unit `0`/`"0"` | not run |
| Dept routing | Queue mappings created | Each test queue → expected `TG-<CODE>-` ticket; audit `DepartmentSource=QueueMapping`; unmapped queue → 422 | not run |
| Inactivity close | SQL scripts, `BackgroundJobs__Enabled`, timeout set | Ticket Closed/Cancelled after timeout; history actor=System, reason text, one closed email | not run |
| Reopen after inactivity | above | CS Agent reopens with reason+department → InProgress; original closure rows retained; Dept Employee gets 403; manual Cancelled → 422 | not run |
| Returning customer | above | New conversation → new ticket (D1) | not run |
| Welcome EN/AR | Flows updated | Text shows Tiger Group, language choice, only supported services | not run |
| Legal handoff | D6 done | Legal question → handoff to mapped queue, `reason` visible, no bot legal answer; no agent → ticket + callback message | not run |
| Collections summary | Tenant mapped by an agent ticket | `customerKey` from lookup returns summary; unknown key 404; EDSM down 503/Unavailable | not run |
| Next payment | Only after EDSM owner attests semantics | Stays `Unavailable` until then | not run |
| Unit details / handover | CRM `GetUnitDetails` deployed + proxy | `Available`, dates not substituted; other customer's unit 403 | not run |
| Document copy | CRM routes, SMTP, `CrmDocuments__Enabled` | Real email with attachment received; foreign record refused | not run |
| Reminders | All §2 item 15 blockers cleared | Dry-run candidates only; no real calls until Genesys campaign sign-off | not run |
