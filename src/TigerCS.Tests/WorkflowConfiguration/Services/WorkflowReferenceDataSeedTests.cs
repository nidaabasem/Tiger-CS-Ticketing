using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;

namespace TigerCS.Tests.WorkflowConfiguration.Services;

/// <summary>
/// The seeded configuration must be a faithful representation of the
/// Customer Service SLA document: ranges stay ranges, urgency is a priority
/// row (never a second request type), conditional SLAs carry their real
/// trigger, and everything the business has not decided stays null.
/// </summary>
public class WorkflowReferenceDataSeedTests
{
    [Fact]
    public async Task Seeds_the_three_reusable_templates_with_their_flows()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();

        var templates = await db.WorkflowTemplates.ToListAsync();
        Assert.Equal(3, templates.Count);

        var standard = templates.Single(t => t.Code == WorkflowReferenceData.StandardTemplateCode);
        Assert.False(standard.AllowsPendingCustomer);
        Assert.False(standard.AllowsPendingInternal);
        Assert.False(standard.RequiresApproval);
        Assert.Equal(
            [WorkflowStepKind.Created, WorkflowStepKind.Assigned, WorkflowStepKind.InProgress, WorkflowStepKind.Resolved, WorkflowStepKind.Closed],
            standard.Steps.OrderBy(s => s.Sequence).Select(s => s.Kind));

        var withPending = templates.Single(t => t.Code == WorkflowReferenceData.WithPendingTemplateCode);
        Assert.True(withPending.AllowsPendingCustomer);
        Assert.True(withPending.AllowsPendingInternal);
        Assert.False(withPending.RequiresApproval);
        Assert.Contains(withPending.Steps, s => s.Kind == WorkflowStepKind.PendingCustomer && s.IsOptional);
        Assert.Contains(withPending.Steps, s => s.Kind == WorkflowStepKind.PendingInternal && s.IsOptional);

        var withApproval = templates.Single(t => t.Code == WorkflowReferenceData.WithApprovalTemplateCode);
        Assert.True(withApproval.RequiresApproval);
        Assert.Contains(withApproval.Steps, s => s.Kind == WorkflowStepKind.Review);
        Assert.Contains(withApproval.Steps, s => s.Kind == WorkflowStepKind.WaitingForApproval);
    }

    [Fact]
    public async Task Every_request_type_belongs_to_its_documented_department()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();

        var departmentCodesById = await db.Departments.ToDictionaryAsync(d => d.DepartmentId, d => d.Code);
        var requestTypes = await db.RequestTypes.ToListAsync();

        Assert.Equal(WorkflowReferenceData.RequestTypes().Count, requestTypes.Count);

        foreach (var seed in WorkflowReferenceData.RequestTypes())
        {
            var stored = requestTypes.Single(
                r => r.Name == seed.Name && departmentCodesById[r.DepartmentId] == seed.DepartmentCode);
            Assert.True(stored.IsActive);
        }

        // The two shared names exist once per department, never globally
        // merged.
        Assert.Equal(2, requestTypes.Count(r => r.Name == "Ticketing System"));
        Assert.Equal(2, requestTypes.Count(r => r.Name == "E-mail"));
    }

    [Fact]
    public async Task Request_types_select_their_documented_workflow_template()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();

        var templateCodesById = await db.WorkflowTemplates.ToDictionaryAsync(t => t.WorkflowTemplateId, t => t.Code);
        var departmentIdsByCode = await db.Departments.ToDictionaryAsync(d => d.Code, d => d.DepartmentId);

        foreach (var seed in WorkflowReferenceData.RequestTypes())
        {
            var stored = await db.RequestTypes.SingleAsync(
                r => r.Name == seed.Name && r.DepartmentId == departmentIdsByCode[seed.DepartmentCode]);
            Assert.Equal(seed.TemplateCode, templateCodesById[stored.WorkflowTemplateId]);
        }
    }

    [Fact]
    public async Task Urgent_variants_are_priority_rows_of_the_same_request_type_not_second_request_types()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();

        Assert.False(await db.RequestTypes.AnyAsync(r => r.Name.Contains("URGENT") || r.Name.Contains("Urgent")));

        var departmentIdsByCode = await db.Departments.ToDictionaryAsync(d => d.Code, d => d.DepartmentId);
        var resale = await db.RequestTypes.SingleAsync(
            r => r.Name == "NOC for Resale" && r.DepartmentId == departmentIdsByCode[WorkflowReferenceData.CustomerServiceCode]);

        var slaRows = await db.RequestTypeSlaPolicies.Where(p => p.RequestTypeId == resale.RequestTypeId).ToListAsync();
        Assert.Equal(2, slaRows.Count);

        // Normal → Medium: 10–12 days, verbatim range.
        var normal = slaRows.Single(p => p.PriorityId == (byte)WorkflowReferenceData.NormalUrgencyPriority);
        Assert.Equal(10, normal.ResolutionTargetValue);
        Assert.Equal(12, normal.ResolutionMaximumValue);
        Assert.Equal(SlaDurationUnit.Days, normal.Unit);

        // Urgent → High: 2–4 days.
        var urgentRow = slaRows.Single(p => p.PriorityId == (byte)WorkflowReferenceData.UrgentUrgencyPriority);
        Assert.Equal(2, urgentRow.ResolutionTargetValue);
        Assert.Equal(4, urgentRow.ResolutionMaximumValue);
    }

    [Fact]
    public async Task Immediate_entries_carry_the_flag_not_a_fabricated_duration()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();

        var departmentIdsByCode = await db.Departments.ToDictionaryAsync(d => d.Code, d => d.DepartmentId);

        foreach (var departmentCode in new[] { WorkflowReferenceData.CustomerServiceCode, WorkflowReferenceData.CollectionsCode })
        {
            var ticketingSystem = await db.RequestTypes.SingleAsync(
                r => r.Name == "Ticketing System" && r.DepartmentId == departmentIdsByCode[departmentCode]);
            var urgentRow = await db.RequestTypeSlaPolicies.SingleAsync(
                p => p.RequestTypeId == ticketingSystem.RequestTypeId
                     && p.PriorityId == (byte)WorkflowReferenceData.UrgentUrgencyPriority);

            Assert.True(urgentRow.IsImmediate);
            Assert.Null(urgentRow.ResolutionTargetValue);
            Assert.Null(urgentRow.ResolutionMaximumValue);
        }
    }

    [Fact]
    public async Task Conditional_slas_carry_their_documented_trigger_never_ticket_creation()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();

        var departmentIdsByCode = await db.Departments.ToDictionaryAsync(d => d.Code, d => d.DepartmentId);

        // Send Receipts: 1 day AFTER Accounting approval.
        var sendReceipts = await db.RequestTypes.SingleAsync(
            r => r.Name == "Send Receipts" && r.DepartmentId == departmentIdsByCode[WorkflowReferenceData.CollectionsCode]);
        var sendReceiptsSla = await db.RequestTypeSlaPolicies.SingleAsync(p => p.RequestTypeId == sendReceipts.RequestTypeId);
        Assert.Equal(SlaTriggerType.ApprovalReceived, sendReceiptsSla.Trigger);
        Assert.Equal(1, sendReceiptsSla.ResolutionTargetValue);

        // Handover: 1–4 days after Customer Service approval.
        var handover = await db.RequestTypes.SingleAsync(
            r => r.Name == "Handover Request" && r.DepartmentId == departmentIdsByCode[WorkflowReferenceData.HandoverCode]);
        var handoverSla = await db.RequestTypeSlaPolicies.SingleAsync(p => p.RequestTypeId == handover.RequestTypeId);
        Assert.Equal(SlaTriggerType.CustomerServiceApproved, handoverSla.Trigger);
        Assert.Equal(1, handoverSla.ResolutionTargetValue);
        Assert.Equal(4, handoverSla.ResolutionMaximumValue);

        // Register Unit: 1–3 days once prerequisites are satisfied.
        var registerUnit = await db.RequestTypes.SingleAsync(
            r => r.Name == "Register Unit" && r.DepartmentId == departmentIdsByCode[WorkflowReferenceData.RegistrationCode]);
        var registerUnitSla = await db.RequestTypeSlaPolicies.SingleAsync(p => p.RequestTypeId == registerUnit.RequestTypeId);
        Assert.Equal(SlaTriggerType.PrerequisitesCompleted, registerUnitSla.Trigger);

        // Everything else starts at ticket creation — the existing behavior.
        var otherTriggers = await db.RequestTypeSlaPolicies
            .Where(p => p.RequestTypeId != sendReceipts.RequestTypeId
                        && p.RequestTypeId != handover.RequestTypeId
                        && p.RequestTypeId != registerUnit.RequestTypeId)
            .Select(p => p.Trigger)
            .ToListAsync();
        Assert.All(otherTriggers, t => Assert.Equal(SlaTriggerType.TicketCreated, t));
    }

    [Fact]
    public async Task Pending_business_decisions_are_seeded_as_null_never_defaulted()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();

        var allRows = await db.RequestTypeSlaPolicies.ToListAsync();
        Assert.NotEmpty(allRows);

        Assert.All(allRows, row =>
        {
            // Pause behavior, clock basis (business vs. calendar days), and
            // first-response targets are all awaiting business decisions —
            // the seed must not smuggle in defaults.
            Assert.Null(row.PausesOnPendingCustomer);
            Assert.Null(row.PausesOnPendingInternal);
            Assert.Null(row.ClockBasis);
            Assert.Null(row.FirstResponseTargetValue);
            Assert.Null(row.FirstResponseMaximumValue);
        });
    }

    [Fact]
    public async Task Participating_departments_get_workflow_settings_with_role_based_head()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();

        var departmentIdsByCode = await db.Departments.ToDictionaryAsync(d => d.Code, d => d.DepartmentId);
        var settings = await db.DepartmentWorkflowSettings.ToListAsync();

        Assert.Equal(WorkflowReferenceData.ParticipatingDepartmentCodes().Count, settings.Count);
        foreach (var code in WorkflowReferenceData.ParticipatingDepartmentCodes())
        {
            var row = settings.Single(s => s.DepartmentId == departmentIdsByCode[code]);
            Assert.Equal(TigerCS.Domain.Modules.IdentityAndAccess.Roles.DepartmentHead, row.HeadRoleName);
        }
    }

    [Fact]
    public async Task Reseeding_is_idempotent()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();

        var templateCount = await db.WorkflowTemplates.CountAsync();
        var requestTypeCount = await db.RequestTypes.CountAsync();
        var slaCount = await db.RequestTypeSlaPolicies.CountAsync();
        var departmentCount = await db.Departments.CountAsync();

        await WorkflowReferenceData.SeedAsync(db);

        Assert.Equal(templateCount, await db.WorkflowTemplates.CountAsync());
        Assert.Equal(requestTypeCount, await db.RequestTypes.CountAsync());
        Assert.Equal(slaCount, await db.RequestTypeSlaPolicies.CountAsync());
        Assert.Equal(departmentCount, await db.Departments.CountAsync());
    }
}
