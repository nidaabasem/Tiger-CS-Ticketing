# Several tickets under one Genesys conversation — root causes, contract, Genesys changes

Trigger (UAT): a customer asked the Genesys AI Agent for CRM documents (emailed, ticket created), then — in the **same conversation** —
asked for apartment leasing. No Leasing ticket appeared, the original ticket was closed although the leasing request needed a person, and the
AI transcript was not visible in Ticketing.

> **What is and is not proven.** Everything below about Ticketing is read from the code and covered by automated tests. **No Genesys logs or
> events were available**, so this document does **not** claim that Genesys sent a second ticket request or any transcript. Where the code can
> only explain the symptom *if* Genesys behaved a certain way, that is said explicitly. End-to-end behaviour with Genesys is **not verified**.

## 1. Root causes confirmed from code

| # | Symptom | Cause in Ticketing | Evidence |
|---|---|---|---|
| R1 | No second (Leasing) ticket | `POST /api/genesys/tickets` was idempotent on `conversationId` **alone**: `GenesysInquiryIngestionAppService` looked the conversation up first and returned `AlreadyIngested` (HTTP 200, the *first* ticket); the database enforced it with the unique filtered index `UX_TicketInteractions_GenesysConversationId`. There was no way to express "a second, distinct request in this conversation", so any second call — whatever its request type — came back as the documents ticket. A flow that treats a 200 as "ticket created" never notices. *(Whether the Genesys flow actually made that second call is not established by anything in this repository.)* | `GenesysInquiryIngestionAppService.IngestAsync` (read-before-write), `TicketInteractionConfiguration` (unique index) |
| R2 | Leasing request landing on, or closed with, the documents ticket | Every conversation-addressed operation (PATCH, handoff, request type, transcript, **document delivery**) resolved *the* interaction by `conversationId`, i.e. always the one (documents) ticket. `DocumentDeliveryTicketService` then closed that ticket on `Sent` after checking only status and customer — **not** whether a person still owed the customer something on it. A leasing handoff / request type applied to that ticket before or while the document outcome arrived was closed with it. | `DocumentDeliveryTicketService.FindTicketAsync` (no human-follow-up check), `GenesysTicketUpdateAppService` (`GetByConversationIdAsync`) |
| R3 | A ticket needing a person could be auto-closed | The chatbot inactivity closer (`ChatbotInactivityCloseAppService`, 10-min customer-inactive rule for website/chat and the original **5-min** bot-prompt rule for untracked tickets) only skipped tickets with an **open handoff row**, a human owner/handler, or Call Center. A ticket that is human work but has no handoff row (a created Leasing ticket nobody had yet requested a human for) was tracked from creation and closed after the customer went quiet. `closedByAi` had the same blind spot. | `ChatbotInactivityCloseAppService.TryCloseFor*`, `GenesysTicketUpdateAppService.ApplyClosedByAiAsync` |
| R4 | Transcript not visible | Ticketing **does** store and show transcripts (`PATCH /api/genesys/tickets/{id}` → `transcript[]` or `ended.transcript`; Ticket Details → Conversation History reads `TicketInteractionMessages`; de-duplicated on `externalMessageId`, else sender+time+body). It stores **only what Genesys sends**, and nothing in this repository shows Genesys sending it: the exported `04-end-conversation.json` data action sends **no** transcript, and the *Send Transcript Message* action described in `Genesys-UAT-Final-Handoff.md` §5.2 is "not yet created / never imported". The human-leg messages are an open item there too (blocker B3). Independently, even once sent, the transcript is stored on the interaction of the one ticket it was PATCHed against, so a second ticket of the conversation would have shown none. | `GenesysConversationEndAppService`, `data-actions/04-end-conversation.json`, `Genesys-UAT-Final-Handoff.md` §5.2/B3 |
| — | Ending a conversation closing tickets? | **Not a cause.** `TicketInteraction.End()` / `GenesysConversationEndAppService` never touch ticket status (existing tests prove it). The closures come from document delivery, `closedByAi` and the two inactivity rules above. | tests `GenesysConversationEndAppServiceTests`, new `EndingTheConversation_NeverClosesAHumanFollowUpTicket…` |

## 2. What changed in Ticketing

1. **Service request id.** New optional `serviceRequestId` (≤ 64 chars, caller-chosen, trimmed) on `POST /api/genesys/tickets`. The idempotency identity is now **(`conversationId`, `serviceRequestId`)**: each distinct pair is its own ticket and interaction; a retry of a pair returns the original (200 `AlreadyIngested`, same body). **Without `serviceRequestId` nothing changes** — the conversation is one ticket and every repeat returns it, so existing integrations are untouched. Stored in `TicketInteractions.GenesysServiceRequestId`; unique index `UX_TicketInteractions_GenesysConversationId_ServiceRequestId` (concurrent duplicates still lose at the database and are answered with the winner's ticket).
2. **PATCH is addressed by (ticketId, conversationId).** `PATCH /api/genesys/tickets/{ticketId}` finds *that ticket's* interaction in the conversation. Transcript, `ended`, handoff, assignment, timers, `closedByAi` and classification therefore apply to the right ticket and to no sibling. A ticket that is not part of the conversation → `409` (as before); unknown conversation → `404`.
3. **Request-type routing (no hard-coded ids).** Genesys supplies the detected `requestType` (`requestTypeId` preferred, or exact `name`). Ticketing resolves its configured department and applies the existing transfer, automatic assignment and SLA logic (unchanged). New: with no department/queue named, a **valid** request type now also supplies the department it is configured for (audit `DepartmentSource=RequestTypeDepartment`). New option **`Genesys:ClassificationDepartmentCode`** (a department *code*; unset by default): when configuration cannot place the ticket — nothing named a department, or the supplied request type is unusable (unknown, inactive, ambiguous, retired department, no published workflow) — the ticket is created **Unclassified in that department** for a person to classify (no handoff is raised for it; an explicit `departmentId`/`departmentCode` from the caller is always respected). Unset ⇒ the previous behaviour: `422`, nothing created.
4. **Human follow-up cannot be auto-closed.** New `humanFollowUpRequired` (on `POST` and `PATCH`; sticky, idempotent). A flagged ticket: is never tracked by either inactivity clock, cannot be closed by `closedByAi`, and is not closed by document delivery. Document delivery additionally refuses to close any ticket with an **open handoff**, and — with `serviceRequestId` on `send-copy` — closes only the documents ticket of the conversation. Ending the conversation closes nothing (unchanged). Each ticket keeps its own interaction, timers, handoff and status; sibling tickets never affect each other. Completed document requests keep their lifecycle (Sent → Resolved/Closed with the fixed reason; failure → Cancelled/Closed).
5. **Transcript visibility.** Conversation History for any ticket now shows the **whole conversation** — the messages stored under every sibling ticket of the same Genesys conversation, de-duplicated and ordered by time — so it no longer matters which ticket Genesys happened to PATCH. "Handled by" (AI / waiting / human) is still judged per ticket. No new endpoint, no new payload: the existing `transcript[]` contract is used.

## 3. Database migration

`20261022090000_AddGenesysServiceRequestToTicketInteractions` (script: `AddGenesysServiceRequestToTicketInteractions.sql`, idempotent):
adds `TicketInteractions.GenesysServiceRequestId nvarchar(64) NULL` and `RequiresHumanFollowUp bit NOT NULL DEFAULT 0`; drops unique `UX_TicketInteractions_GenesysConversationId`; creates non-unique `IX_TicketInteractions_GenesysConversationId` and unique
`UX_TicketInteractions_GenesysConversationId_ServiceRequestId` (conversation, service request) `WHERE [GenesysConversationId] IS NOT NULL`. Existing rows get NULL / 0, so each existing conversation keeps its single-ticket guarantee (SQL Server treats NULLs as equal in a unique index). Additive; no data touched. **Down** restores the old index and fails if multi-ticket conversations already exist (reconcile by hand first). Not applied to any SQL Server by this work.

## 4. Exact contract for the Genesys team

### 4.1 Create — one call per distinct service request (`02-create-reuse-ticket.json`, updated)
```json
POST /api/genesys/tickets
{ "conversationId": "<ConversationId>", "serviceRequestId": "documents", "channel": "LiveChat",
  "customerPhone": "+9715…", "queueId": "<bot queue>", "subject": "CRM documents" }
→ 201 { "outcome":"TicketCreated","ticketId":101,"ticketNumber":"…","serviceRequestId":"documents",… }
```
The apartment-leasing request, later in the **same** conversation:
```json
POST /api/genesys/tickets
{ "conversationId": "<same ConversationId>", "serviceRequestId": "leasing", "channel": "LiveChat",
  "customerPhone": "+9715…", "queueId": "<bot queue>", "subject": "Apartment leasing",
  "requestType": { "requestTypeId": 17 }, "humanFollowUpRequired": true }
→ 201 { "outcome":"TicketCreated","ticketId":102,"classificationStatus":"Classified","departmentId":<Leasing>,… }
```
Rules: pick the `serviceRequestId` yourself and **reuse the same value on every retry** of that request (a retry returns `200 AlreadyIngested` with the same ticket); use a **different** value for each distinct request. Keep one `ticketId` participant-data variable **per service request** (e.g. `TigerCsDocumentsTicketId`, `TigerCsLeasingTicketId`) and use the right one on every PATCH. `requestType` should be the id (or exact name) from `GET /api/request-types` mapped from the AI's intent; if the intent is unmapped send none — the ticket is Unclassified (in the classification queue, if configured). Send `humanFollowUpRequired: true` for any request the bot cannot complete itself. **Send `serviceRequestId` on the *first* call of a conversation too** (a conversation created without one is one ticket; a later call *with* one would create a second).

### 4.2 Document delivery (`11-send-document-copy.json`, updated)
Add `serviceRequestId` (the documents request's id) to `POST /api/genesys/documents/send-copy` whenever the conversation can have more than one ticket, together with `conversationId`. Without it the conversation's first ticket is used (old behaviour).

### 4.3 Mark human follow-up later (`17-mark-human-follow-up.json`, new)
`PATCH /api/genesys/tickets/{ticketId}` `{ "conversationId": "…", "humanFollowUpRequired": true }` — when the AI only discovers during the conversation that a person is needed. Use it once, on the right ticket.

### 4.4 Transcript — what Ticketing needs from Genesys (nothing new is invented)
Ticketing already accepts, on `PATCH /api/genesys/tickets/{ticketId}`:
`"transcript":[{"sender":"Customer|VirtualAgent|HumanAgent|System","sentAtUtc":"<ISO-8601 UTC>","body":"…","senderName":"…","senderId":"…","externalMessageId":"<Genesys message id>"}]`
(live, one or more per call, sequential) and/or `ended.transcript` on the final `ended` call. Genesys must therefore (a) create and import the *Send Transcript Message* data action specified in `Genesys-UAT-Final-Handoff.md` §5.2 **or** include the whole conversation in `ended.transcript` (the `04-end-conversation` template currently sends none), (b) always send `externalMessageId` (Genesys' message id) so retries and live+final deliveries never duplicate, (c) send the bot as `VirtualAgent`, never `HumanAgent`, and (d) decide how human-leg messages reach Ticketing (open item B3). With this release it is enough to send the transcript against **any one** ticket of the conversation; Ticketing shows it on all of them. Whether Genesys currently sends any transcript can only be confirmed from the TigerGroupWeb forwarder / Genesys flow logs: look for `PATCH …/tickets/{id}` bodies containing `transcript` or `ended.transcript`, and for `TranscriptAppended` / `GenesysConversationEnded` audit entries (`TranscriptMessagesAppended=0` on a chat that had messages means none arrived).

## 5. Deployment and configuration
1. Apply the migration (or `AddGenesysServiceRequestToTicketInteractions.sql`) **before** deploying the new API build; deploy API and Web together.
2. Optional config: `Genesys:ClassificationDepartmentCode` = the code of the triage department (e.g. the department whose staff classify Unclassified tickets). Leave unset to keep the 422 behaviour.
3. Genesys changes in §4 (import the updated `02`, `11`, new `17`; transcript action). Order is flexible: until Genesys sends `serviceRequestId`, behaviour is exactly as before.
4. No change to TigerGroupWeb code: `serviceRequestId`, `humanFollowUpRequired` and `transcript` are body members forwarded byte-for-byte; the route table gains no route (data action 17 uses the existing PATCH).
5. Rollback: redeploy the previous build; the migration's Down only succeeds while no conversation has more than one ticket.

## 6. Leasing routing review (configuration as delivered)

Verified from code, seed/import and tests (no database was reachable; run `docs/Genesys/uat/verify-leasing-routing.sql`, read-only, to check the real one):

| Question | Finding |
|---|---|
| Leasing department exists and is active? | **Only after the New Request Types import.** Neither the dev seed nor migrations define it; `NewRequestTypesImporter` / `ImportNewRequestTypes_UAT.sql` create **Leasing Customer Services** (code `LCS`, active) if no department has that code or name. |
| Active Leasing request type? | Five, owned by LCS, active, each with a Published workflow: Tenancy Contract, Ejari, Booking, Move-In / Move-Out, Rent / DEWA / AC Cheque Inquiry. **None is named "Leasing"** (not in the approved 35-row workbook). |
| Mapped to Leasing through routing config? | Yes — `RequestType.DepartmentId` is the routing; `POST /api/genesys/tickets` applies it (transfer, assignment, SLA). `requestType: "Leasing"` therefore resolves **only if a request type with that exact name exists**; otherwise it is refused (422), or queued Unclassified when `Genesys:ClassificationDepartmentCode` is set. Nothing is guessed from the word. |
| Automatic assignment configured? | **No.** The import creates no assignment rules; Leasing tickets wait in the LCS department queue until an administrator adds a rule per request type (Administration → Request Types; `RequestTypeAssignmentRules`, one per type, members must belong to LCS). The mechanism itself works (tested). |
| Follow-up protection | `humanFollowUpRequired: true` → stays Open against AI completion, document delivery and both inactivity jobs (tested with the exact UAT payload). |

**Not changed on purpose:** no "Leasing" request type was seeded. Adding one would alter the business-approved workbook catalogue (and its pinned 35-row tests) and needs a business-chosen workflow, SLA and assignee. Create it in Administration (department `LCS`, name exactly `Leasing`, published workflow) and add its assignment rule — or have Genesys send the id/name of one of the five existing types. One code change: `requestType` may now be sent as a bare string (`"requestType": "Leasing"`), read as the name; the object form still works.
