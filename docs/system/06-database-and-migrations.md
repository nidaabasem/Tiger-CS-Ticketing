# 6. Database overview, migrations and SQL scripts

> Part of the [documentation set](README.md). Source: `src/TigerCS.Infrastructure` (EF Core, SQL Server). Local tests use SQLite/fakes; **no migration in this list has been applied by this review to SQL Server.**

## 6.1 Tables (EF `DbSet`s)

| Area | Tables / entities |
|---|---|
| Identity & org | ASP.NET Identity tables, `Employee`, `Department`, `UserDepartmentAssignment`, `Channel` |
| Customers | `ContactReference`, `UnitReference`, `IntakeRecord`, `VerificationSession`, `DepartmentCustomerLookupSource`, `TicketRequesterSnapshot` |
| Tickets | `Ticket`, `TicketStatusHistory`, `TicketAssignment`, `TicketNote`, `TicketResolution`, `TicketPendingRecord`, `TicketInteraction`, `TicketInteractionMessage`, `TicketAgentHandoff`, `TicketWorkflowEvent`, `TicketApproval`, `TicketEscalation` |
| Classification | `Category`, `RequestType`, `Priority`, `RequestTypeAssignmentRule(+Member)`, `RequestTypeApprovalRequirement`, `RequestTypeSlaPolicy` |
| Workflow config | `Workflow`, `WorkflowStepTransition`, `WorkflowTemplate(+Step)`, `DepartmentWorkflowSettings` |
| SLA | `SlaPolicy`, `TicketSlaInstance`, `BusinessCalendar(+WorkingDay)`, `Holiday` |
| Notifications | `Notification`, `OutboxMessage`, `IdempotencyRecord` |
| Genesys | `GenesysQueueMapping`, `GenesysScreenPopLaunch` |
| Collections | `CollectionsReminder`, `CollectionsReminderChannel`, `CollectionsReminderEvent` |
| Documents / OTP | `CrmDocumentDeliveryRequest`, `CustomerOtpChallenge` (hashed code, salt, attempts, sends) |
| Audit | `AuditEntry` |

Campaign preview/export (PR #77) **persists nothing** — there is no campaign table or migration by design.

## 6.2 Migrations (chronological)

`20260819072347_InitialIdentityAndAccess` → `AddCustomerVerification` → `AddTicketing` → `AddTicketOperations` → `AddSlaAndEscalation` →
`AddNotificationsAndOutbox` → `AddIntakeRecordPhoneNumber` → `AddDepartmentCustomerLookupSource(s)` → `AddCrmBuyerLookupToTickets` →
`AddExternalCustomerVerificationToTickets` → `AddWorkflowConfiguration` → `AddWorkflowAutomation` →
`ReplaceInteractionContextWithTicketInteractions` → `AddApprovalWorkflow` → `AddWorkflowVersioning` → `AddChannels` →
`AddGenesysIntegration` → `AddGenesysAgentMappingAndInteractionOwnership` → `AddAgentHandoffTriggerAndConcurrency` →
`AddGenesysScreenPopLaunches` → **`20261005072511_AddCollectionsReminders`** → **`20261007093644_AddChatbotInactivityAndCrmDocumentCopies`** →
**`20261007103153_AddResolutionClosedForCustomerInactivity`** → **`20261007180009_AddCustomerOtpVerification`** (new in the review branch; table `CustomerOtpChallenges`, script `AddCustomerOtpVerification.sql`).

## 6.3 SQL scripts at the repo root (idempotent hand-apply alternatives)

| Script | Purpose | Apply when |
|---|---|---|
| `AddChannels.sql`, `AddGenesysIntegration.sql`, `AddGenesysAgentMappingAndInteractionOwnership.sql`, `ApplyAgentHandoffMigration.sql`, `AddGenesysScreenPopLaunches.sql` | Genesys tables/columns | DB behind the matching migration |
| `AddExternalCustomerSnapshotToTickets.sql` | External (PACT) customer snapshot on tickets | before PACT-first ticketing |
| `AddCollectionsReminders.sql` | reminder tables | before enabling reminders |
| `AddChatbotInactivityAndCrmDocumentCopies.sql` | inactivity + document delivery tables | before `BackgroundJobs`/documents |
| `AddResolutionClosedForCustomerInactivity.sql` | resolution lookup row for inactivity closure | after the previous |
| `AddCustomerOtpVerification.sql` | OTP challenge table | before enabling `CrmDocuments` |
| `ConfigureReopenApprovalRequirements.sql` | optional: reopen approval config — review before running | business decision |
| `ImportNewRequestTypes_UAT.sql` | UAT request-type import ([doc](../New-Request-Types-UAT-Import.md)) | UAT only |
| `UAT_Update_Final.sql` | UAT data update | UAT only; read first |
| `docs/Genesys/uat-routing/01_Investigate_Finance_Routing_UAT.sql`, `02_Correct_Misrouted_Tickets_UAT.ps1` | queue-mapping diagnosis/correction | UAT troubleshooting |
| `docs/Collections/pact-sql/*` | read-only PACT queries used to verify field semantics | reference |

Check status: `SELECT MigrationId FROM __EFMigrationsHistory ORDER BY 1 DESC` — expect `20261007103153_…` or later.
Migration check performed in this review: the EF model snapshot builds and the persistence tests run on SQLite (see [16](16-uat-checklist.md)); no automated model‑vs‑migration drift check exists, and application to SQL Server was **not performed**.
