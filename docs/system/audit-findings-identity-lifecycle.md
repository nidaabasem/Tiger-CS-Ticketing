# Audit Findings - Identity, Permissions, Lifecycle, SLA, Notifications

Read-only audit of working tree HEAD `31878f4` (main `a1cba71` + 3 CRM/OTP commits). Paths relative to `src/`. Every item below was verified in code; severity is the auditor's judgement (High = customer-visible SLA/escalation or security impact; Medium = requirement not met; Low = inconsistency / hardening). Suggested fixes are minimal and **proposed**, not applied.

## Verification of the seven specific checks

| Check | Result |
|---|---|
| (a) Pending Third Party cannot be newly entered | **Holds at runtime** (no transition arm, explicit refusal, hidden in UI/filters). **Residual config hole**: F-1. |
| (b) First Response stopped only by a human reply | **Holds** (VirtualAgent/System/Customer lines and acknowledgement never record it). But the human path is not reachable from the UI: F-2. |
| (c) Call Center / AI Agent = CS Agent permissions | Call Center Agent: **equal** (it is a CS Agent in dept `CC`). AI Agent: **no role or principal exists**; effective permissions are those of the CS Agent integration account (F-8). |
| (d) CS Manager sees CS Agents and Call Center staff | **Holds** for Team Performance (CS Agent role holders incl. CC members); dashboard agent picker lists all active dept members. Limits noted in F-6/F-7. |
| (e) OriginatingDepartmentId never overwritten | **Holds** (`Ticket.cs:493` is the only assignment; private setter; no raw SQL or EF update path). |
| (f) Reopen window and approval | **Holds** (7-day configurable window from closure, shared eligibility service, approval re-validated at decision). Design caveat F-12. |
| (g) Notification dedupe keys | **Holds** (`OutboxMessage.cs:274-293`: per ticket, event type, version and reopen cycle; consumer unique on OutboxMessageId+type). Caveats F-13. |

## Confirmed defects and gaps

### F-1 (Low) Legacy "Pending Internal / Third Party" can still be configured through the API
- `TigerCS.Application/Modules/Administration/Services/AdminWorkflowAppService.cs:460-477` (`ValidateStep` uses `WorkflowStepKinds.IsSupported`, which still answers true for the legacy `PendingInternal` kind); `:277` and `AdminRequestTypeAppService.cs:150,193` accept `AllowsPendingInternal` / `AllowPendingInternal = true`.
- Scenario: an administrator (or a script) POSTs a step of kind `PendingInternal` into a draft and publishes it; the designer shows a step that no ticket can ever reach (`ChangeStatusAsync:90` refuses the status, `CanGoPendingInternal` is unused). The UI hides it, the API does not.
- Fix: in `ValidateStep` reject `WorkflowStepKinds.IsLegacyOnly(request.Kind)` unless the step already has that kind; force the two Allow flags to false on save.

### F-2 (High) No way to record First Response from the UI on manually created tickets
- `TigerCS.Web/Services/Api/TicketSlaApiClient.cs:12` defines `RecordFirstResponseAsync` but no page calls it (`grep` of `Pages/` finds none); `TicketCreationAppService` and `TicketNoteAppService` never set `FirstHumanResponseAtUtc`; the only automatic writer is the Genesys transcript path (`GenesysConversationEndAppService.cs:227-259`), and `GenesysCallAnswer` is never populated (`SlaFirstResponseAppService.cs:136`).
- Scenario: a CS Agent takes a phone/walk-in/email case, creates the ticket and speaks to the customer. No first response is ever recorded, the First Response deadline passes, `SlaBreachProcessor` marks `FirstResponseBreached`, `SlaState=Breached` and raises an automatic Level 2 escalation (`SlaBreachProcessor.cs` `RaiseAutomaticLevel2Async`) on a correctly handled ticket.
- Fix: add a "Record first response" action on Ticket Details for the owner (calls the existing endpoint, source Manual) and/or record it when a CS Agent creates a verified ticket from a live call (decision needed; **proposed**).

### F-3 (Medium) Configured SLA pause and request-type SLA are not enforced
- `RequestTypeSlaPolicy.PausesOnPendingCustomer` and per-request-type SLA tables are stored, edited (`AdminRequestTypeAppService.cs`, `Pages/Admin/RequestTypeEdit.cshtml`) and exposed, but the runtime calculator reads only priority `SlaPolicies` (`SlaDueDateService.ComputeDueDatesAsync`, `SlaDueDateService.cs:188-215`); `SlaState.Paused` is produced only for provisional tickets (`SlaState.cs` remarks). Moving a ticket to PendingCustomer does not stop either clock.
- Scenario: a ticket waiting on the customer for days breaches Resolution SLA and auto-escalates though the admin screen says the clock pauses.
- Fix: either implement pause periods (`TicketSlaPausePeriods`, backlog S-pause) or label the admin SLA/pause fields "not enforced" until built.

### F-4 (Medium, requirement gap) Priority change and downgrade approval do not exist
- Classification is write-once (`TicketClassificationAppService.cs:109-112`, `AlreadyClassified`); `TicketSlaController.cs:21-36` documents priority upgrade (S-14) and downgrade as not implemented; `RequestType.AllowAgentPriorityChange` is surfaced (`WorkflowCapabilities.cs:54`) but no code consumes `CanChangePriority`; `ApprovalType` has no priority type.
- Scenario: a mis-prioritized Low ticket cannot be raised to High, so it runs on the wrong SLA.
- Fix: new endpoint + `PriorityChangeApproval` approval type for downgrades, recomputing the Resolution SLA from change time (needs business sign-off on the recompute moment).

### F-5 (Medium) Chairman/CEO (and General Manager) hold operational ticket authority despite "read-only" role definition
- `TigerCS.Application/Modules/Ticketing/Services/TicketRoleSets.cs:53-56` puts `ChairmanCeo` in `CrossDepartmentSupervisory`, which gates status change (`TicketLifecycleAppService.cs:902`), classification (`TicketClassificationAppService.cs:86`) and manual first-response recording (`SlaRoleSets.cs:159-162`); `Roles.cs:45` says Chairman/CEO is "read-only ... Level 4 escalations only".
- Scenario: the Chairman account moves any ticket between InProgress and PendingCustomer or classifies it.
- Fix: remove `ChairmanCeo` from `CrossDepartmentSupervisory` and `SlaRoleSets.RecordFirstResponse`/`ManualEscalate` (keep view), or correct the documented intent.

### F-6 (Low) Reporting User role grants nothing useful
- No policy, role set or report endpoint admits it (`InfrastructureServiceCollectionExtensions.cs:431-452`; `ReportsPolicy.cs:54-60`; `ReportsController.cs:20`). `Roles.cs:47` promises read-only reports/dashboards.
- Scenario: a Reporting User signs in, sees an empty dashboard (scope = own departments), and "Team Performance" is hidden.
- Fix: decide scope; if intended, add the role to the reports policy and to cross-department view.

### F-7 (Low) Team Performance and policy naming/scope limits
- `TeamPerformanceAppService.cs:49` `EligibleRoles = [CsAgent]`: Call Center staff who hold only Department Employee/Supervisor roles, CS Supervisors and the AI account are not shown; `CsManagerOrGeneralManager` (`InfrastructureServiceCollectionExtensions.cs:439-441`) also admits Chairman/CEO, so `GET api/users/assignable` (`UsersController.cs:37`) is open to a role with no assignment authority.
- Fix: confirm intended population; use a dedicated policy for `users/assignable` (CS Manager + SysAdmin).

### F-8 (Medium) AI Agent is not modelled; integration account has full CS Agent power
- No AI role/principal (`Roles.cs`); the Genesys service account is a CS Agent (`docs/Genesys/Genesys-Cloud-Configuration.md:75`) behind policy `CustomerVerification` (`GenesysController.cs:39`, `GenesysVerificationController.cs:28`, `GenesysDocumentsController.cs:35`). Anything that account's token can reach (Close, Reopen, classify, notes) is allowed.
- Scenario: leaked integration credentials can close or reopen tickets in every department.
- Fix: dedicated "Integration" role or scope claim limited to `api/genesys/*`, `api/auth`; keep CS Agent for humans. Until then document that AI Agent == CS Agent account by design.

### F-9 (Low) Resolved is a dead end except Close
- `TicketStatusTransitions.cs:58-72` has no arm out of Resolved; `Ticket.Reopen` rejects Resolved (`Ticket.cs:836-839`); `Ticket.cs` Reopen remarks say Resolved work "is corrected by continuing the existing work item".
- Scenario: a ticket resolved by mistake can only be Closed (CS) and then reopened within 7 days, generating Closed + Reopened customer emails.
- Fix: allow Resolved->InProgress for the resolving department (and archive the resolution) or amend the rule/comment.

### F-10 (Low) "Awaiting human agent" dashboard KPI not shown
- API returns `AwaitingHumanAgent` and oldest wait (`DashboardAppService.cs:163-172`), but `DashboardModel.BuildCards` renders only six cards (`Dashboard.cshtml.cs:130-147`).
- Fix: add a Pending Interactions card linking to `?view=pending` with the risk class from `HumanWaitRiskThresholdSeconds`.

### F-11 (Low) No double-click / row navigation on the main ticket lists
- `wwwroot/js/site.js:57-61` binds single-click on `tr.is-link`; there is no `dblclick` handler; `_TicketRow.cshtml:7` renders a plain `<tr>`, so Queue/My/Closed rows navigate only via the ticket-number link.
- Fix: add `class="is-link" data-href` to `_TicketRow` (single-click, consistent with other tables) or add a `dblclick` binding if double-click is the agreed behaviour.

### F-12 (Low) Reopen approval is advisory and can lapse
- Granting changes nothing on the ticket and is not consumed by `ReopenAsync` (`TicketApprovalAppService.cs:50-60`); the window is not extended, so an approval granted on day 7 may be unusable by the time a CS user acts (`ReopenWindowExpired`).
- Fix: record the approval time and honour a request made within the window when evaluating `WindowExpired` (proposed; needs business decision).

### F-13 (Low) Notification caveats
- Events older than `EmailNotifications:MaxNotificationAgeHours` (24 h) are silently Skipped after an outage (`CustomerTicketEmailHandler.cs:131-134`); templates are English only (`CustomerEmailTemplates.cs:195`); `EmailNotifications:Enabled` and `BackgroundJobs:Enabled` default to false in committed settings, which also stops outbox dispatch, SLA sweeps and the chatbot inactivity closure (`BackgroundJobServiceCollectionExtensions.cs:97,128,149,177`).
- Fix: alert on Skipped(EventTooOld); add a customer language field and Arabic templates; make the disabled-jobs state visible in a health check.

### F-14 (Low) Collections navigation visible to roles that cannot use it
- `Pages/Shared/_Nav.cshtml:14` shows Collections to everyone; `Program.cs:15` only requires sign-in; denial happens after the API call (`Receivables.cshtml:126`).
- Fix: hide the link unless the user holds a Collections-eligible role/department (mirror `CollectionsAuthorizationService`), keeping API enforcement.

### F-15 (Low) Admin audit trail has no reader; routing mappings have no screen
- `AuditEntryWriter` writes (every Admin* action), but no controller or page reads `AuditEntries`; Genesys queue mappings exist only as `api/admin/genesys/queue-mappings` (`AdminGenesysController.cs`), absent from `Pages/Admin`.
- Fix: add read-only Audit History and Genesys Routing pages under `/Admin`.

### F-16 (Low) CS Manager cannot create tickets
- `POST api/tickets` uses `CustomerVerification` (CS Agent, CS Supervisor, System Administrator) (`TicketsController.cs:55-56`; `TicketCreationPolicy.cs:14-19`). If CS Manager should log tickets, the policy needs the role.
