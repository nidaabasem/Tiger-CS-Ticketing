# 18. Remaining work: blockers by type, owner and next action

> Part of the [documentation set](README.md). Each row states the exact missing input, the system owner and the next action. Rows are grouped so that business decisions are not confused with missing code, inaccessible repositories or deployment configuration. "Implemented" items are in the [matrix](15-requirements-traceability-matrix.md).

## A. Business decisions (nothing to build until answered)

| Item | Exact missing input | Owner | Next action |
|---|---|---|---|
| Request-type SLA beyond the unambiguous subset | (1) calendar vs business days when `ClockBasis` is null; (2) weekend rule; (3) holiday rule; (4) whether a range "10–12 days" means target/breach; (5) clock-start events for ApprovalReceived / CustomerServiceApproved / PrerequisitesCompleted / Assigned; (6) does Pending Internal pause | CS Manager + Finance/Operations | Answer 1–6 per request type, then set `ClockBasis`/flags in Admin → Request Types (code already enforces whatever is set) |
| Downgrade clock start | approval moment (built, per SLA-Architecture §7) vs original period start; priority **upgrade** clock start | CS Manager | Confirm or specify; upgrade then needs a small change in `SlaDueDateService` |
| Collections amount conventions & campaign policy | confirm: remaining-amount meaning, month-based aged thresholds, default lookback = configured `StartDate`, "both CurrentMonth and LegalNotice qualify on day 14" priority, credits not netted | Collections / Finance | Sign-off line in the campaign doc; then set `FinancialSourceValidated=true` |
| Inactivity duration | confirmed timeout (committed 5 min) | CS Manager | Set `Genesys__CustomerInactivityTimeoutMinutes` |
| Returning customer after closure | new ticket (built) vs auto-link/reopen | CS | Decide D1; auto-reopen needs an authorization model for the service identity |
| Reopen approval | advisory (built) vs mandatory | CS Manager | Decide D13 |
| Chairman/CEO scope, Call Center/AI roles | confirm "read-only" also bars notes (built: yes); whether distinct Call Center / AI Agent roles are wanted | Business / Security | Decide D11 |
| Legal handoff, CSAT, brand wording, Arabic scope | D3, D5, D6, D7 in the matrix | Business | Decide; then Architect/templates |
| B.P / P.R documents | what the two handwritten abbreviations mean and the CRM document type ids | Sales Admin | Provide mapping; then extend `CrmDocumentTypeMapping` |

## B. Missing code that is blocked by missing contracts (cannot be built honestly yet)

| Item | Exact missing input | Owner | Next action |
|---|---|---|---|
| **Tasleeh** real lookup | endpoint URL, authentication, request/response schema, test data | Tasleeh/IT owner | Supply the contract; implement `ITasleehGateway` (interface and tests exist). Until then `Tasleeh__Provider=Unavailable` (implemented) reports the source as unavailable instead of serving fixture data |
| **Statement of Account** | the system that produces the PDF/data (CRM? PACT? EDSM?) and its API | Business to name; Finance IT | Name source → reuse `CrmDocumentDeliveryRequests` + e-mail pipeline |
| **Construction updates** | source and an endpoint returning latest update text + date per project/unit | Projects / CRM | Provide endpoint → add a read-only Genesys route |
| **CSAT** | trigger, channel (e-mail vs SMS), scale, repeat-after-reopen, SMS provider | CS / Business | Decide D7; natural hook = existing `TicketClosed` outbox event |
| **Payment link / app deep link** | app link scheme, customer authentication in the app, payment provider callback contract | Mobile / Product / Payments | Provide contract; nothing reusable exists |
| **Instalments / payment history / outstanding** | an approved source (EDSM instalment semantics, freshness) | Finance IT | Attest semantics → enable `NextPayment`; source currently `Unavailable` |
| **Unit details & handover dates** | CRM `GetUnitDetails` route | CRM team | Build per `CRM-Required-Contracts.md`; TigerCS side is done (`NotAvailable` until then) |
| **Unresolved document types** | CRM document type ids beyond Contract / Reservation Form / Unit Layout / Registration Receipt | CRM | Provide ids |

## C. Inaccessible repositories (exact changes delivered, not applied)

| System | Change | Where specified | Owner |
|---|---|---|---|
| **TigerGroupWeb** | forward all 19 routes (incl. verification/*, agent-context, screen-pop, send-copy, unit-details, reminders) with the stated header/status rules | `docs/Genesys/TigerGroupWeb-Forwarding-Implementation.md` (compiled and smoke-tested against a fake TigerCS), route table guarded by `TigerGroupWebRouteTableTests` | Web team |
| **Tiger CRM** | JSON 401 (not 302); `GetCustomerDocuments` + file routes verified; `GetUnitDetails` | `docs/Genesys/CRM-Required-Contracts.md`, `crm-contracts/*.json`, `uat/verify-crm-file-auth.sh` | CRM team |
| **Genesys Cloud** | import data actions 01–16, flows, queue ids for mappings | [09](09-data-action-inventory.md) | Genesys admin |
| **Mobile app** | none accessible | — | Product |

## D. Deployment / configuration (code done, switch not flipped)

`BackgroundJobs__Enabled`, `Authorization__ServiceIdentity__EmployeeIds`, `Genesys__ScreenPopWebBaseUrl`, `CrmDocuments__Enabled` + `OtpCodePepper` + SMTP, `Tasleeh__Provider=Unavailable`, queue mappings, migrations 4–5, credential rotation — see [17](17-release-package-and-credentials.md).

## E. Known caveats in delivered code

* Service identity is not blocked from being *assigned* a ticket by id (hidden from directories only).
* Pause: tickets already in legacy Pending Third Party get no retroactive pause; a period already breached on Resolution does not pause; an immediate request type reopened gets due = reopen time (breaches at next check).
* OTP challenge / verification-session ownership is per service account, not per conversation (all Genesys traffic uses one account).
* Real Genesys Velocity/JSONPath behaviour (including failure paths) is unverified: tests use a simulator.
