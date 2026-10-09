# 11 - Verification and document delivery

Status: code audit of the tree at `31878f4` (includes the real CRM `GetCustomerDocuments` gateway and the server-side email-OTP flow). The code is the source of truth; `docs/Genesys/*` and `docs/releases/*` may lag. Nothing here was exercised against real CRM, PACT, SMTP, Genesys or TigerGroupWeb: those behaviours are **unverified external assumptions**. Per `docs/Genesys/Document-Copy-API.md` the feature is itself "PENDING REAL UAT VERIFICATION".

State vocabulary: **Implemented** (code + tests in repo), **Gated** (implemented, off or blocked by configuration), **Missing** (no code), **External** (depends on a system or content not in this repo).

## 1. Matrix

| Requirement | State | Notes |
|---|---|---|
| Agent customer verification sessions | Implemented | `POST/GET /api/verification-sessions`; 30-minute lifetime; one confirmed session = one unit + one contact snapshot; consumed once by a ticket or reconciliation |
| OTP (email one-time code) | Implemented + Gated | TigerCS generates, emails, stores (salted HMAC) and checks the code. Gated by `CrmDocuments:Enabled=false` (committed), `EmailNotifications:Enabled=false`, and a Production start-up guard. Real SMTP/CRM delivery **External/unverified**. **SMS is a second channel of the same flow** (`channel: "Sms"`, `Sms-Verification-Channel.md`): gated by `CrmDocuments:OtpSmsEnabled=false` and `Sms:Provider=Disabled`; verified with a fake sender only, **no real SMS sent**. No WhatsApp OTP |
| Chatbot "send me a copy" of Contract | Gated | `POST /api/genesys/documents/send-copy`; real CRM HTTP gateway exists |
| Reservation form copy | Gated | same flow |
| Registration receipt copy | Gated | same flow |
| Unit layout copy | Gated | layout = the unit's `unitplan` field, record id `LAYOUT-{leadId}` |
| B.P / P.R status documents | Missing / unconfirmed | no code; decision D8 "meaning of B.P and P.R; CRM identifiers" open (`docs/releases/UAT-Outstanding-Requirements-Readiness.md`). Do not infer a meaning |
| Ownership checks | Implemented | see 3.3 |
| Selection when several records | Implemented | status `SelectionRequired` + `choices`, nothing sent |
| Retrieval | Implemented | CRM list call + file download; delivery only, no download endpoint in TigerCS |
| Email delivery | Implemented, Gated | attachment through the shared `IEmailSender`; WhatsApp/SMS answer 501 `DELIVERY_CHANNEL_NOT_INTEGRATED` |
| Statement of Account (SOA) | Missing | no document type or generator; the Payment tab shows EDSM summary figures, which is not an SOA |
| Unit details / handover dates | Implemented (not gated by CrmDocuments) | `POST /api/genesys/customers/unit-details`, real `CrmUnitDetailsHttpGateway` (`GET TicketingSystem/GetUnitDetails`); unit-level and project-level expected/actual handover dates kept separate, `handoverDateSource` Unit/Project/null. CRM data quality **External** |
| Construction updates | Partial | only the project `status` text and handover dates recorded in CRM are returned; no dedicated construction-update feed/document. Anything richer is Missing |
| Legal handoff | Partial | Collections: LegalReferral is an internal review list only, never exported to customers; LegalNotice export gated by `LegalNoticeExportEnabled=false`. No legal case creation (`docs/Collections/Collections-Legal-Requirements.md`) |
| Human-agent handoff | Implemented | `handoff` part of `PATCH /api/genesys/tickets/{id}`, work list `/api/pending-customer-interactions` |
| CSAT | Missing | no survey, score or endpoint; only a record-only `customerConfirmation.confirmedResolved` (never resolves the ticket) |
| Chatbot-to-app deep links | Missing | no code |
| Payment links | Missing | no code. Collections `AlreadyPaid` only opens verification follow-up, posts nothing |
| Welcome / Arabic messages | External (content draft) | TigerCS returns no greeting text; `docs/Genesys/Welcome-And-Arabic-Content.md` is DRAFT, needs business approval and native-Arabic review. Reminder email is English only |
| Inactivity close of chatbot tickets | Implemented, Gated | `awaitingCustomerReply` timer, `Genesys:CustomerInactivityTimeoutMinutes=5`, runs only if `BackgroundJobs:Enabled=true` (false) |

## 2. Agent verification sessions (`VerificationSessionAppService`)
* Flow (agent UI): intake record -> customer lookup (CRM/PACT/Tasleeh by phone) -> optional `GET /api/crm/units/...` (fills `UnitReferences`/`ContactReferences` cache) -> `POST /api/verification-sessions` with `unitReferenceId`, `contactReferenceId`, `confirmed=true`, method.
* Methods accepted by this endpoint: `ManualAgentConfirmation`, `AuthenticatedDigitalUser`, `FaceToFaceDocumentCheck`, `Other`. `"Otp"` is refused with `400 otp-requires-challenge` because OTP evidence is produced only by the server-side challenge (`VerificationSessionAppService.cs:80-90`). Defect: a numeric string `"3"` parses to `Otp` and bypasses the string comparison (`:108` `Enum.Parse`, controller `:69` `Enum.TryParse`); the resulting session has no proof, so document release still refuses it, but the audit trail then says "Otp" (finding F-5).
* Real CRM caveat: with `Crm:Provider=Http`, the three unit/contact lookup routes are `UnimplementedCrmHttpGateway` (fail closed), so the unit/contact cache can only be filled by the buyer-lookup path of the OTP flow (`docs/Genesys/CRM-Required-Contracts.md` §1).

## 3. Chatbot document flow (all via TigerGroupWeb -> TigerCS; TigerGroupWeb forwarding of these routes is **PENDING / unverified**)

### 3.1 Sequence
1. `POST /api/genesys/verification/buyer-lookup {phoneNumber}` -> CRM `GET TicketingSystem/GetBuyerByPhone` (existing buyer gateway). 0 customers -> 404 `CUSTOMER_NOT_FOUND`; >1 -> 409 `CUSTOMER_AMBIGUOUS`. Returns unit labels and a masked email only.
2. `POST .../otp/send {phoneNumber, crmUnitId?, channel?, language?}` -> unit must belong to the customer (403 `UNIT_NOT_OWNED`); several units and none chosen -> `UnitSelectionRequired`; a 6-digit code is emailed (default) or, with `channel: "Sms"`, texted to **the address / mobile CRM returns** (no destination field exists in any request). An SMS whose delivery is unknown answers 504 `OTP_DELIVERY_UNCONFIRMED` and is never resent automatically.
3. `POST .../otp/verify {challengeId, code}` -> on success creates the OTP-verified session in the same transaction that spends the challenge, recording challenge id, CRM customer id and lead id (`AttachOtpProof`).
4. `POST /api/genesys/documents/send-copy` with `Idempotency-Key` -> see 3.3.

Limits (`CrmDocumentOptions`): code valid 10 min (`OtpLifetimeMinutes`), 5 wrong tries (`OtpMaxAttempts`), 3 sends per challenge (`OtpMaxSendsPerChallenge`), >= 60 s between sends (`OtpMinResendSeconds`), 5 challenges per CRM customer per hour across all callers (`OtpMaxChallengesPerCustomerPerHour`). Code storage: HMAC-SHA256 over challenge id + random salt + code, constant-time compare (`CustomerOtpAppService.cs:538-558`). **The HMAC key is `CrmDocuments:OtpCodePepper`, and when it is blank the code falls back to the constant `"tigercs-customer-otp"`** (finding F-6). Challenges are bound to the calling integration account: another caller gets `OTP_CHALLENGE_NOT_FOUND`.

### 3.2 Gates (all must hold for anything to be sent)
* `CrmDocuments:Enabled` (committed `false`; `appsettings.Development.json` sets `true`).
* `EmailNotifications:Enabled` (committed `false`) - without it `otp/send` answers 503 "Email delivery is switched off" (`CustomerOtpAppService.cs:452`), so no code is ever sent and no document can be released. Real SMTP also needs `EmailNotifications:Password` (blank in repo).
* In Production the host refuses to start with `CrmDocuments:Enabled=true` unless `CrmDocuments:AllowInProduction=true` (`Program.cs`).
* `Crm:Provider=Http`, `Crm:BaseUrl`, `Crm:SecretKey` (header `X-SECRET-KEY`; not committed). Document files may be fetched only from the CRM origin or hosts in `Crm:DocumentFileHosts`; the secret is never sent to other hosts; redirects are not followed; max body `Crm:MaxDocumentBytes` (10 MB). File type is decided from magic bytes (pdf/png/jpeg/gif/webp/tiff/bmp/doc/docx); anything else is refused.

### 3.3 Selection, ownership and retrieval (`CrmDocumentCopyAppService.SendAsync`)
1. Disabled -> 503 `DOCUMENT_COPY_DISABLED`. `Idempotency-Key` required.
2. Replay by (caller, key): Sent -> `Sent duplicate:true`; in-progress and not stale (`InProgressStaleAfterMinutes=5`) -> 202 `Queued`; different request -> 409 `IDEMPOTENCY_KEY_REUSED`.
3. Verification: session must exist, be owned by the caller, be `Confirmed`/`Consumed`, unexpired, method in `AcceptedVerificationMethods` (default `Otp`, `AuthenticatedDigitalUser`) **and carry a recorded OTP proof** (`ProofChallengeId`, `:159-161`); CRM is re-asked and the same customer/bound lead must still match. Any failure is one answer: 403 `VERIFICATION_FAILED`.
4. A supplied `crmUnitId` must equal the verified unit, else 403 `RECORD_OWNERSHIP_MISMATCH`.
5. CRM `POST TicketingSystem/GetCustomerDocuments` for the verified customer + lead + document type (mapping: Contract -> `TigerContract` (attachment type 5), ReservationForm (4), RegistrationReceipt (6), UnitLayout -> `Layout`). Returned records are filtered again to the verified unit/contact; a named `recordId` outside that list -> 403 mismatch (never fetched).
6. 0 records -> 404 `DOCUMENT_NOT_FOUND`; >1 and no `recordId` -> 200 `SelectionRequired` with `choices[{recordId,label,unitNumber,issuedOn}]`; the follow-up call must use a **new** key.
7. Destination = CRM's email for the contact; invalid/missing -> 422 `DELIVERY_DESTINATION_UNAVAILABLE`. Duplicate suppression: same session + document + record + channel sent within `DuplicateSuppressionMinutes=15` returns the earlier send even under a new key.
8. The send is claimed in a unique-indexed row before the file is fetched; the fetched content is re-checked against the owned record (unit, owner contact) before sending; size 0 or > `MaxAttachmentBytes` -> 502 `DOCUMENT_TOO_LARGE`; delivery failure -> 502 `DELIVERY_FAILED` with `retryable`.
9. Audit entry `CrmDocumentCopySent` (masked destination, no content). The document is never stored or logged and never returned to the caller; there is no download endpoint.

### 3.4 Email OTP vs other proof methods
`AuthenticatedDigitalUser` is configured as acceptable but no code path can create such a session with a proof, so for the chatbot flow only the email OTP can succeed. `ManualAgentConfirmation` is deliberately not accepted for chatbot document release.

## 4. Unit details / handover dates (`POST /api/genesys/customers/unit-details`)
* Request `{customerReference, phoneNumber, unitId?}` (POST only to keep the number out of URLs). CRM is asked which customer the verified number resolves to and which units they own on every call; mismatch -> 403 `CUSTOMER_NOT_VERIFIED` / `UNIT_NOT_ELIGIBLE`; multiple customers -> 409 `CUSTOMER_AMBIGUOUS`.
* Response modes: `UnitSelectionRequired` (eligible units) or `UnitDetails` (unit + project details). Values CRM does not record are `null`. Handover: unit `expectedHandoverDate` / `actualHandoverDate` and project-level pair kept separate; `handoverDateSource` says which pair applies. `construction status` is the project's recorded `Status` text only.
* Not behind `CrmDocuments:Enabled`; gated only by `Genesys:Enabled` (committed true). TigerGroupWeb forwarding **unverified**.

## 5. Not built (do not promise to customers)
Statement of Account, payment links, chatbot-to-app deep links, CSAT, construction-update documents, B.P/P.R documents, WhatsApp delivery or OTP, SMS/WhatsApp delivery of documents (SMS carries only the code), Arabic reminder/OTP email templates, tenant/representative document release (Phase 1 is CRM **buyers** only), a document download endpoint.

## 6. Welcome messages
Content only (Genesys Architect). Draft English/Arabic pack in `docs/Genesys/Welcome-And-Arabic-Content.md`; advertise only logging a request and speaking to an agent until the matrix rows above turn green on UAT. Brand wording ("Tiger Group" vs "Tiger Properties") is an open business decision (D3).
