using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Seed;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.WorkflowConfiguration.Runtime;

/// <summary>
/// Configuration enforcement end to end through the real Api — routing,
/// authorization, application services and persistence — on a synthetic,
/// fully configured request type:
/// <code>
/// Ticket Created → Intake Queue [Intake] → Intake Agent → Waiting on customer (optional)
///   → Handoff to Handover [Handover] → Handover Work → Return to Intake [Intake]
///   → Intake Follow-up → Resolve → Close
/// </code>
/// with a Medium SLA of 2 business hours first response and 1 business day
/// resolution. Nothing here invents a role, status or approval: every action
/// is an existing endpoint under its existing authorization.
/// </summary>
public class ConfiguredRuntimeApiTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public ConfiguredRuntimeApiTests(TigerCsApiFactory factory) => _factory = factory;

    private static readonly string[] StepNames =
    [
        "Ticket Created", "Intake Queue", "Intake Agent", "Waiting on customer", "Handoff to Handover",
        "Handover Work", "Return to Intake", "Intake Follow-up", "Resolve", "Close"
    ];

    private sealed record Synthetic(int Intake, int Handover, int Collections, int CategoryId, int RequestTypeId, int WorkflowId, int VersionId, int DecisionId);

    // ------------------------------------------------------------ setup

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}"[..(prefix.Length + 9)];

    /// <summary>
    /// The synthetic configuration, written directly (it stands in for an
    /// imported catalog row): inactive, not enforced, one open catalog
    /// decision — exactly the state the import leaves a row in.
    /// </summary>
    private async Task<Synthetic> CreateSyntheticAsync(bool openDecision = true)
    {
        await _factory.SeedPrioritiesAsync();
        var intake = await _factory.CreateDepartmentAsync(Unique("Intake"), Guid.NewGuid().ToString("N")[..8]);
        var handover = await _factory.CreateDepartmentAsync(Unique("Handover"), Guid.NewGuid().ToString("N")[..8]);
        var collections = await _factory.CreateDepartmentAsync(Unique("Collections"), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync(Unique("Synthetic"), intake);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();

        var code = "SYN" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var workflow = new Workflow("RT-" + code, "Synthetic " + code, null, DateTime.UtcNow, isActive: true);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var version = new WorkflowTemplate(workflow.WorkflowId, 1, "RT-" + code + "-V1", "Synthetic " + code, null,
            allowsPendingCustomer: true, allowsPendingInternal: false, requiresApproval: false, DateTime.UtcNow, null);
        version.AppendStep("Ticket Created", WorkflowStepKind.Created);
        version.AppendStep("Intake Queue", WorkflowStepKind.Assigned, departmentId: intake);
        version.AppendStep("Intake Agent", WorkflowStepKind.InProgress);
        version.AppendStep("Waiting on customer", WorkflowStepKind.PendingCustomer, isOptional: true);
        version.AppendStep("Handoff to Handover", WorkflowStepKind.Assigned, departmentId: handover);
        version.AppendStep("Handover Work", WorkflowStepKind.InProgress);
        version.AppendStep("Return to Intake", WorkflowStepKind.Assigned, departmentId: intake);
        version.AppendStep("Intake Follow-up", WorkflowStepKind.InProgress);
        version.AppendStep("Resolve", WorkflowStepKind.Resolved);
        version.AppendStep("Close", WorkflowStepKind.Closed);
        version.Publish(DateTime.UtcNow, null);
        db.WorkflowTemplates.Add(version);

        var requestType = new RequestType(intake, "Synthetic " + code, workflow.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: true, allowPendingCustomer: true, allowPendingInternal: false, allowReopen: true, isActive: false);
        requestType.SetCatalogDetails(code, "Synthetic", "Fully configured synthetic request type", null);
        db.RequestTypes.Add(requestType);
        await db.SaveChangesAsync();

        db.RequestTypeSlaPolicies.Add(new RequestTypeSlaPolicy(
            requestType.RequestTypeId, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days,
            firstResponseTargetValue: 2, firstResponseMaximumValue: null, resolutionTargetValue: 1, resolutionMaximumValue: null,
            clockBasis: SlaClockBasis.BusinessHours, firstResponseUnit: SlaDurationUnit.Hours));
        db.RequestTypeApprovalRequirements.Add(RequestTypeApprovalRequirement.ForRole(
            requestType.RequestTypeId, ApprovalType.ReopenApproval, Roles.CsManager, blocksWorkUntilApproved: false));

        var decision = new RequestTypeCatalogDecision(requestType.RequestTypeId, "Handoff",
            "Confirm the handoff to Handover.", DateTime.UtcNow);
        db.RequestTypeCatalogDecisions.Add(decision);
        await db.SaveChangesAsync();
        if (!openDecision)
        {
            decision.Resolve("Confirmed by the business.", Guid.NewGuid(), DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        return new Synthetic(intake, handover, collections, categoryId, requestType.RequestTypeId, workflow.WorkflowId,
            version.WorkflowTemplateId, decision.RequestTypeCatalogDecisionId);
    }

    /// <summary>The synthetic type made live through the real admin endpoints: decision answered, enforcement on, activated.</summary>
    private async Task<Synthetic> CreateEnforcedAsync()
    {
        var synthetic = await CreateSyntheticAsync(openDecision: false);
        var admin = await ClientAsync(Roles.SystemAdministrator);
        await ReadAsync<AdminRequestTypeDetailDto>(await admin.PutAsJsonAsync(
            $"/api/admin/request-types/{synthetic.RequestTypeId}/configuration-enforcement", new SetConfigurationEnforcementRequestDto(true, "test")));
        await ReadAsync<AdminRequestTypeDetailDto>(await admin.PatchAsJsonAsync(
            $"/api/admin/request-types/{synthetic.RequestTypeId}/activation", new SetActiveRequestDto(true, "test")));
        return synthetic;
    }

    private async Task<(HttpClient Client, Guid EmployeeId)> UserAsync(string role, int? departmentId = null)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        if (departmentId is { } id)
        {
            await _factory.AssignPrimaryDepartmentAsync(employeeId, id);
        }

        var client = _factory.CreateClient();
        var login = await ReadAsync<LoginResponseDto>(await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return (client, employeeId);
    }

    private async Task<HttpClient> ClientAsync(string role, int? departmentId = null) => (await UserAsync(role, departmentId)).Client;

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<long> CreateTicketAsync(HttpClient agent, Synthetic synthetic)
    {
        var intake = await ReadAsync<IntakeRecordResponseDto>(await agent.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971509990003", synthetic.Intake, false, null, null)));
        var ticket = await ReadAsync<TicketResponseDto>(await agent.PostAsJsonAsync("/api/tickets",
            new CreateTicketRequestDto(intake.IntakeRecordId, null, null, synthetic.CategoryId, (byte)PriorityLevel.Medium,
                "Synthetic request", RequestTypeId: synthetic.RequestTypeId)));
        return ticket.TicketId;
    }

    private static async Task<TicketDetailDto> GetAsync(HttpClient client, long ticketId) =>
        await ReadAsync<TicketDetailDto>(await client.GetAsync($"/api/tickets/{ticketId}"));

    private static async Task<byte[]> RowVersionAsync(HttpClient client, long ticketId) =>
        Convert.FromBase64String((await GetAsync(client, ticketId)).RowVersion);

    private static async Task<HttpResponseMessage> TransferAsync(HttpClient client, HttpClient reader, long ticketId, int departmentId) =>
        await client.PostAsJsonAsync($"/api/tickets/{ticketId}/transfer",
            new TransferTicketRequestDto(departmentId, "Workflow handoff", await RowVersionAsync(reader, ticketId)));

    private static async Task<HttpResponseMessage> AssignAsync(HttpClient head, HttpClient reader, long ticketId, Guid employeeId) =>
        await head.PostAsJsonAsync($"/api/tickets/{ticketId}/assignment",
            new AssignTicketRequestDto(employeeId, await RowVersionAsync(reader, ticketId)));

    private static async Task<HttpResponseMessage> StartWorkAsync(HttpClient owner, HttpClient reader, long ticketId) =>
        await owner.PostAsJsonAsync($"/api/tickets/{ticketId}/status",
            new ChangeStatusRequestDto(nameof(TicketStatus.InProgress), await RowVersionAsync(reader, ticketId)));

    private static async Task<HttpResponseMessage> ResolveAsync(HttpClient worker, HttpClient reader, long ticketId, string outcome = "Resolved") =>
        await worker.PostAsJsonAsync($"/api/tickets/{ticketId}/resolution",
            new ResolveTicketRequestDto(outcome, "Done", null, null, await RowVersionAsync(reader, ticketId)));

    private static async Task<HttpResponseMessage> CloseAsync(HttpClient cs, HttpClient reader, long ticketId) =>
        await cs.PostAsJsonAsync($"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(await RowVersionAsync(reader, ticketId)));

    private static async Task AssertWorkflowRefusalAsync(HttpResponseMessage response, string expectedNext)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        Assert.Contains("workflow-step-not-allowed", body);
        Assert.Contains(expectedNext, body);
    }

    private async Task<IReadOnlyList<string>> StepTrailAsync(long ticketId) =>
        (await _factory.GetAuditEntriesAsync(ticketId.ToString()))
            .Where(e => e.Action == "WorkflowStep")
            .OrderBy(e => e.AuditEntryId)
            .Select(e => e.AfterValue!.Split(';').Single(p => p.StartsWith("Step=", StringComparison.Ordinal))["Step=".Length..])
            .ToList();

    private static BusinessCalendarSnapshot SeededCalendar() => new(
        TimeZoneInfo.FindSystemTimeZoneById(SlaReferenceData.DefaultTimeZoneId),
        SlaReferenceData.DefaultBusinessDayStartLocal, SlaReferenceData.DefaultBusinessDayEndLocal,
        SlaReferenceData.DefaultWorkingDays, []);

    // ------------------------------------------------------------ admin: guards

    [Fact]
    public async Task Activation_and_enforcement_are_refused_while_a_catalog_decision_is_open_and_allowed_once_it_is_answered()
    {
        var synthetic = await CreateSyntheticAsync(openDecision: true);
        var admin = await ClientAsync(Roles.SystemAdministrator);

        var activate = await admin.PatchAsJsonAsync($"/api/admin/request-types/{synthetic.RequestTypeId}/activation", new SetActiveRequestDto(true));
        Assert.Equal(HttpStatusCode.Conflict, activate.StatusCode);
        Assert.Contains("Confirm the handoff to Handover", await activate.Content.ReadAsStringAsync());

        var enforce = await admin.PutAsJsonAsync($"/api/admin/request-types/{synthetic.RequestTypeId}/configuration-enforcement",
            new SetConfigurationEnforcementRequestDto(true));
        Assert.Equal(HttpStatusCode.Conflict, enforce.StatusCode);
        Assert.Contains("catalog decision", await enforce.Content.ReadAsStringAsync());

        var before = await ReadAsync<AdminRequestTypeDetailDto>(await admin.GetAsync($"/api/admin/request-types/{synthetic.RequestTypeId}"));
        Assert.False(before.IsActive);
        Assert.False(before.ConfigurationEnforced);
        Assert.False(Assert.Single(before.CatalogDecisions!).IsResolved);

        var resolved = await ReadAsync<AdminRequestTypeDetailDto>(await admin.PostAsJsonAsync(
            $"/api/admin/request-types/{synthetic.RequestTypeId}/catalog-decisions/{synthetic.DecisionId}/resolution",
            new ResolveCatalogDecisionRequestDto("The business confirmed Handover.")));
        Assert.Equal("The business confirmed Handover.", Assert.Single(resolved.CatalogDecisions!).Resolution);
        Assert.Empty(resolved.ReadinessIssues!);

        var enforced = await ReadAsync<AdminRequestTypeDetailDto>(await admin.PutAsJsonAsync(
            $"/api/admin/request-types/{synthetic.RequestTypeId}/configuration-enforcement", new SetConfigurationEnforcementRequestDto(true)));
        Assert.True(enforced.ConfigurationEnforced);
        var active = await ReadAsync<AdminRequestTypeDetailDto>(await admin.PatchAsJsonAsync(
            $"/api/admin/request-types/{synthetic.RequestTypeId}/activation", new SetActiveRequestDto(true)));
        Assert.True(active.IsActive);

        // Only the System Administrator configures this — no role gains anything.
        var manager = await ClientAsync(Roles.CsManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync(
            $"/api/admin/request-types/{synthetic.RequestTypeId}/configuration-enforcement", new SetConfigurationEnforcementRequestDto(false))).StatusCode);
    }

    [Fact]
    public async Task Enforcement_is_refused_for_an_unconfirmed_handoff_a_gating_approval_and_an_undecided_SLA()
    {
        var synthetic = await CreateSyntheticAsync(openDecision: false);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var accounting = await db.Departments.FirstAsync(d => d.DepartmentId == synthetic.Collections);

            // The published handoff step loses its department — the state of
            // an imported, unconfirmed handoff (set directly: published
            // versions are immutable through every product path).
            var version = await db.WorkflowTemplates.SingleAsync(t => t.WorkflowTemplateId == synthetic.VersionId);
            typeof(WorkflowTemplateStep).GetProperty(nameof(WorkflowTemplateStep.DepartmentId))!
                .SetValue(version.Steps.Single(s => s.Name == "Handoff to Handover"), null);

            db.RequestTypeApprovalRequirements.Add(RequestTypeApprovalRequirement.ForDepartment(
                synthetic.RequestTypeId, ApprovalType.AccountingApproval, accounting.DepartmentId));
            var sla = await db.RequestTypeSlaPolicies.SingleAsync(p => p.RequestTypeId == synthetic.RequestTypeId);
            sla.Update(SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 2, null, 1, 2, false, null, null, null, null, true);
            await db.SaveChangesAsync();
        }

        var admin = await ClientAsync(Roles.SystemAdministrator);
        var response = await admin.PutAsJsonAsync($"/api/admin/request-types/{synthetic.RequestTypeId}/configuration-enforcement",
            new SetConfigurationEnforcementRequestDto(true));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("does not name its department", body);
        Assert.Contains("AccountingApproval", body);
        Assert.Contains("clock basis", body);
        Assert.Contains("is a range", body);
    }

    [Fact]
    public async Task The_designer_sets_a_queue_steps_department_and_an_enforced_types_new_version_must_stay_trackable()
    {
        var synthetic = await CreateEnforcedAsync();
        var admin = await ClientAsync(Roles.SystemAdministrator);

        var draft = await ReadAsync<WorkflowVersionDetailDto>(await admin.PostAsJsonAsync(
            $"/api/admin/workflows/{synthetic.WorkflowId}/versions", new { }));
        var handoff = draft.Steps.Single(s => s.Name == "Handoff to Handover");
        Assert.Equal(synthetic.Handover, handoff.DepartmentId);

        // Point the handoff at Collections through the designer — then clear it.
        var edited = await ReadAsync<WorkflowVersionDetailDto>(await admin.PutAsJsonAsync(
            $"/api/admin/workflows/versions/{draft.WorkflowTemplateId}/steps/{handoff.WorkflowTemplateStepId}",
            new SaveStepRequestDto("Handoff to Collections", WorkflowStepKind.Assigned, false, null, synthetic.Collections)));
        Assert.Equal(synthetic.Collections, edited.Steps.Single(s => s.WorkflowTemplateStepId == handoff.WorkflowTemplateStepId).DepartmentId);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(
            $"/api/admin/workflows/versions/{draft.WorkflowTemplateId}/steps/{handoff.WorkflowTemplateStepId}",
            new SaveStepRequestDto("Work", WorkflowStepKind.InProgress, false, null, synthetic.Collections))).StatusCode);
        await ReadAsync<WorkflowVersionDetailDto>(await admin.PutAsJsonAsync(
            $"/api/admin/workflows/versions/{draft.WorkflowTemplateId}/steps/{handoff.WorkflowTemplateStepId}",
            new SaveStepRequestDto("Handoff (to be confirmed)", WorkflowStepKind.Assigned, false, null, null)));

        var publish = await admin.PostAsJsonAsync($"/api/admin/workflows/versions/{draft.WorkflowTemplateId}/publish", new { });

        Assert.Equal(HttpStatusCode.Conflict, publish.StatusCode);
        Assert.Contains("does not name its department", await publish.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_enforced_types_SLA_cannot_be_edited_into_a_value_the_runtime_cannot_apply()
    {
        var synthetic = await CreateEnforcedAsync();
        var admin = await ClientAsync(Roles.SystemAdministrator);

        var range = await admin.PutAsJsonAsync($"/api/admin/request-types/{synthetic.RequestTypeId}/sla-policies/{(byte)PriorityLevel.Medium}",
            new SaveSlaPolicyRequestDto(SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 2, null, 1, 2, false, SlaClockBasis.BusinessHours,
                null, null, null, FirstResponseUnit: SlaDurationUnit.Hours));
        Assert.Equal(HttpStatusCode.Conflict, range.StatusCode);
        Assert.Contains("is a range", await range.Content.ReadAsStringAsync());

        var gating = await admin.PutAsJsonAsync($"/api/admin/request-types/{synthetic.RequestTypeId}/approval-requirements/AccountingApproval",
            new SaveApprovalRequirementRequestDto(ApprovalTargetKind.Department, synthetic.Collections, null, null));
        Assert.Equal(HttpStatusCode.Conflict, gating.StatusCode);

        var unchanged = await ReadAsync<AdminRequestTypeDetailDto>(await admin.GetAsync($"/api/admin/request-types/{synthetic.RequestTypeId}"));
        Assert.Null(Assert.Single(unchanged.SlaPolicies).ResolutionMaximumValue);
        Assert.DoesNotContain(unchanged.ApprovalRequirements, a => a.ApprovalType == ApprovalType.AccountingApproval);
    }

    // ------------------------------------------------------------ runtime

    [Fact]
    public async Task Steps_progress_through_handoff_and_return_via_the_existing_actions_and_out_of_order_actions_are_refused()
    {
        var synthetic = await CreateEnforcedAsync();
        var (agent, _) = await UserAsync(Roles.CsAgent, synthetic.Intake);
        var manager = await ClientAsync(Roles.CsManager);
        var intakeHead = await ClientAsync(Roles.DepartmentHead, synthetic.Intake);
        var (intakeWorker, intakeWorkerId) = await UserAsync(Roles.DepartmentEmployee, synthetic.Intake);
        var handoverHead = await ClientAsync(Roles.DepartmentHead, synthetic.Handover);
        var (handoverWorker, handoverWorkerId) = await UserAsync(Roles.DepartmentEmployee, synthetic.Handover);

        var ticketId = await CreateTicketAsync(agent, synthetic);
        var created = await GetAsync(manager, ticketId);
        Assert.Equal(synthetic.VersionId, created.WorkflowTemplateId);
        Assert.Equal("Intake Queue", created.CurrentWorkflowStepName);

        // Intake's head assigns → the queue step gives way to the work step.
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(intakeHead, manager, ticketId, intakeWorkerId)).StatusCode);
        Assert.Equal("Intake Agent", (await GetAsync(manager, ticketId)).CurrentWorkflowStepName);
        Assert.Equal(HttpStatusCode.OK, (await StartWorkAsync(intakeWorker, manager, ticketId)).StatusCode);

        // Resolving now would skip the handoff: refused, naming the expected step.
        await AssertWorkflowRefusalAsync(await ResolveAsync(intakeWorker, manager, ticketId), "Handoff to Handover");

        // Transfer stays CS Manager only: the workflow never widens it.
        Assert.Equal(HttpStatusCode.Forbidden, (await TransferAsync(intakeHead, manager, ticketId, synthetic.Handover)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TransferAsync(agent, manager, ticketId, synthetic.Handover)).StatusCode);

        // A CS Manager may transfer — but only to the department the workflow expects next.
        await AssertWorkflowRefusalAsync(await TransferAsync(manager, manager, ticketId, synthetic.Collections), "Handoff to Handover");
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(manager, manager, ticketId, synthetic.Handover)).StatusCode);
        var handedOff = await GetAsync(manager, ticketId);
        Assert.Equal(synthetic.Handover, handedOff.CurrentDepartmentId);
        Assert.Equal("Handoff to Handover", handedOff.CurrentWorkflowStepName);

        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(handoverHead, manager, ticketId, handoverWorkerId)).StatusCode);
        Assert.Equal("Handover Work", (await GetAsync(manager, ticketId)).CurrentWorkflowStepName);
        await AssertWorkflowRefusalAsync(await ResolveAsync(handoverWorker, manager, ticketId), "Return to Intake");

        // The return to the intake department is the same existing Transfer.
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(manager, manager, ticketId, synthetic.Intake)).StatusCode);
        Assert.Equal("Return to Intake", (await GetAsync(manager, ticketId)).CurrentWorkflowStepName);
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(intakeHead, manager, ticketId, intakeWorkerId)).StatusCode);
        Assert.Equal("Intake Follow-up", (await GetAsync(manager, ticketId)).CurrentWorkflowStepName);

        // Close before Resolve stays refused by the existing lifecycle rule
        // (409 not-yet-resolved), before the workflow is ever consulted.
        var earlyClose = await CloseAsync(agent, manager, ticketId);
        Assert.Equal(HttpStatusCode.Conflict, earlyClose.StatusCode);
        Assert.Contains("not-yet-resolved", await earlyClose.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(intakeWorker, manager, ticketId)).StatusCode);
        Assert.Equal("Resolve", (await GetAsync(manager, ticketId)).CurrentWorkflowStepName);
        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(agent, manager, ticketId)).StatusCode);

        var closed = await GetAsync(manager, ticketId);
        Assert.Equal("Close", closed.CurrentWorkflowStepName);
        Assert.Equal(nameof(TicketStatus.Closed), closed.TicketStatus);

        Assert.Equal(
            ["Intake Queue", "Intake Agent", "Handoff to Handover", "Handover Work", "Return to Intake", "Intake Follow-up", "Resolve", "Close"],
            await StepTrailAsync(ticketId));

        // Handoffs are ordinary audited transfers.
        Assert.Equal(2, (await _factory.GetAuditEntriesAsync(ticketId.ToString())).Count(e => e.Action == "Transfer"));
    }

    [Fact]
    public async Task Deadlines_come_from_the_request_type_SLA_in_its_own_units_on_the_business_calendar()
    {
        var synthetic = await CreateEnforcedAsync();
        var agent = await ClientAsync(Roles.CsAgent, synthetic.Intake);

        var ticketId = await CreateTicketAsync(agent, synthetic);

        var sla = await _factory.GetCurrentSlaInstanceAsync(ticketId);
        Assert.NotNull(sla);
        var start = DateTime.SpecifyKind(sla!.PeriodStartAtUtc, DateTimeKind.Utc);
        var calendar = SeededCalendar();

        // 2 business hours; 1 business day = one 600-minute working-day window.
        Assert.Equal(SlaDueDateCalculator.ComputeDueAtUtc(start, 120, SlaClockBasis.BusinessHours, calendar), sla.FirstResponseDueAtUtc);
        Assert.Equal(SlaDueDateCalculator.ComputeDueAtUtc(start, 600, SlaClockBasis.BusinessHours, calendar), sla.ResolutionDueAtUtc);

        // …and not Medium's per-priority policy (4 business hours / 3 business days).
        Assert.NotEqual(SlaDueDateCalculator.ComputeDueAtUtc(start, 240, SlaClockBasis.BusinessHours, calendar), sla.FirstResponseDueAtUtc);
        Assert.NotEqual(SlaDueDateCalculator.ComputeDueAtUtc(start, 1800, SlaClockBasis.BusinessHours, calendar), sla.ResolutionDueAtUtc);

        var computation = Assert.Single(await _factory.GetAuditEntriesAsync(ticketId.ToString()), e => e.Action == "ComputeSlaDueDates");
        Assert.Contains("\"firstResponseSource\":\"RequestTypeSla:", computation.AfterValue);
        Assert.Contains("\"resolutionSource\":\"RequestTypeSla:", computation.AfterValue);
    }

    /// <summary>The whole happy path, asserting only that each existing action succeeds.</summary>
    private async Task<(long TicketId, HttpClient Agent, HttpClient Manager)> DriveToClosedAsync(Synthetic synthetic)
    {
        var (agent, _) = await UserAsync(Roles.CsAgent, synthetic.Intake);
        var manager = await ClientAsync(Roles.CsManager);
        var intakeHead = await ClientAsync(Roles.DepartmentHead, synthetic.Intake);
        var (intakeWorker, intakeWorkerId) = await UserAsync(Roles.DepartmentEmployee, synthetic.Intake);
        var handoverHead = await ClientAsync(Roles.DepartmentHead, synthetic.Handover);
        var (_, handoverWorkerId) = await UserAsync(Roles.DepartmentEmployee, synthetic.Handover);
        var ticketId = await CreateTicketAsync(agent, synthetic);

        foreach (var action in new Func<Task<HttpResponseMessage>>[]
        {
            () => AssignAsync(intakeHead, manager, ticketId, intakeWorkerId),
            () => StartWorkAsync(intakeWorker, manager, ticketId),
            () => TransferAsync(manager, manager, ticketId, synthetic.Handover),
            () => AssignAsync(handoverHead, manager, ticketId, handoverWorkerId),
            () => TransferAsync(manager, manager, ticketId, synthetic.Intake),
            () => AssignAsync(intakeHead, manager, ticketId, intakeWorkerId),
            () => ResolveAsync(intakeWorker, manager, ticketId),
            () => CloseAsync(agent, manager, ticketId)
        })
        {
            var response = await action();
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        return (ticketId, agent, manager);
    }

    [Fact]
    public async Task Reopen_resumes_at_the_departments_last_queue_step_restarts_resolution_from_the_request_type_SLA_and_keeps_first_response()
    {
        var synthetic = await CreateEnforcedAsync();
        var (ticketId, agent, manager) = await DriveToClosedAsync(synthetic);
        var firstCycle = await _factory.GetCurrentSlaInstanceAsync(ticketId);

        // The existing Reopen rule (CS layer, window, outcome) is untouched;
        // the workflow only requires the target to have a queue step.
        var wrong = await agent.PostAsJsonAsync($"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Customer came back", synthetic.Collections, await RowVersionAsync(manager, ticketId)));
        await AssertWorkflowRefusalAsync(wrong, "reopen into this department");

        var reopen = await agent.PostAsJsonAsync($"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Customer came back", synthetic.Intake, await RowVersionAsync(manager, ticketId)));
        Assert.True(reopen.IsSuccessStatusCode, await reopen.Content.ReadAsStringAsync());
        Assert.Equal("Return to Intake", (await GetAsync(manager, ticketId)).CurrentWorkflowStepName);

        var cycle = await _factory.GetCurrentSlaInstanceAsync(ticketId);
        Assert.Equal(firstCycle!.FirstResponseDueAtUtc, cycle!.FirstResponseDueAtUtc);
        var reopenedAt = DateTime.SpecifyKind(cycle.PeriodStartAtUtc, DateTimeKind.Utc);
        Assert.Equal(SlaDueDateCalculator.ComputeDueAtUtc(reopenedAt, 600, SlaClockBasis.BusinessHours, SeededCalendar()), cycle.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task A_cancellation_ends_the_request_from_any_step_but_a_non_owner_still_cannot_resolve()
    {
        var synthetic = await CreateEnforcedAsync();
        var (agent, _) = await UserAsync(Roles.CsAgent, synthetic.Intake);
        var manager = await ClientAsync(Roles.CsManager);
        var intakeHead = await ClientAsync(Roles.DepartmentHead, synthetic.Intake);
        var (intakeWorker, intakeWorkerId) = await UserAsync(Roles.DepartmentEmployee, synthetic.Intake);
        var ticketId = await CreateTicketAsync(agent, synthetic);
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(intakeHead, manager, ticketId, intakeWorkerId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StartWorkAsync(intakeWorker, manager, ticketId)).StatusCode);

        // The CS Agent holds no Resolve authority — refused before the workflow is consulted.
        Assert.Equal(HttpStatusCode.Forbidden, (await ResolveAsync(agent, manager, ticketId, "Cancelled")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(intakeWorker, manager, ticketId, "Cancelled")).StatusCode);
        Assert.Equal("Resolve", (await GetAsync(manager, ticketId)).CurrentWorkflowStepName);
        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(agent, manager, ticketId)).StatusCode);
        Assert.Equal("Close", (await GetAsync(manager, ticketId)).CurrentWorkflowStepName);
    }

    // ------------------------------------------------------------ unchanged behaviour

    [Fact]
    public async Task Without_enforcement_the_same_configuration_changes_nothing_no_tracking_priority_SLA_unrestricted_actions()
    {
        // Same workflow, same SLA row — but enforcement never enabled.
        var synthetic = await CreateSyntheticAsync(openDecision: false);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            (await db.RequestTypes.SingleAsync(r => r.RequestTypeId == synthetic.RequestTypeId)).Activate();
            await db.SaveChangesAsync();
        }

        var (agent, _) = await UserAsync(Roles.CsAgent, synthetic.Intake);
        var manager = await ClientAsync(Roles.CsManager);
        var intakeHead = await ClientAsync(Roles.DepartmentHead, synthetic.Intake);
        var (intakeWorker, intakeWorkerId) = await UserAsync(Roles.DepartmentEmployee, synthetic.Intake);
        var ticketId = await CreateTicketAsync(agent, synthetic);

        var ticket = await GetAsync(manager, ticketId);
        Assert.Null(ticket.CurrentWorkflowStepId);
        Assert.Null(ticket.CurrentWorkflowStepName);

        var sla = await _factory.GetCurrentSlaInstanceAsync(ticketId);
        var start = DateTime.SpecifyKind(sla!.PeriodStartAtUtc, DateTimeKind.Utc);
        Assert.Equal(SlaDueDateCalculator.ComputeDueAtUtc(start, 240, SlaClockBasis.BusinessHours, SeededCalendar()), sla.FirstResponseDueAtUtc);
        Assert.Equal(SlaDueDateCalculator.ComputeDueAtUtc(start, 1800, SlaClockBasis.BusinessHours, SeededCalendar()), sla.ResolutionDueAtUtc);

        // Any department, resolve straight away — exactly as before.
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(intakeHead, manager, ticketId, intakeWorkerId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StartWorkAsync(intakeWorker, manager, ticketId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(intakeWorker, manager, ticketId)).StatusCode);
        Assert.Empty(await StepTrailAsync(ticketId));

        var second = await CreateTicketAsync(agent, synthetic);
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(manager, manager, second, synthetic.Collections)).StatusCode);
    }

    [Fact]
    public async Task A_ticket_created_before_enforcement_was_enabled_stays_untracked()
    {
        var synthetic = await CreateSyntheticAsync(openDecision: false);
        var admin = await ClientAsync(Roles.SystemAdministrator);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            (await db.RequestTypes.SingleAsync(r => r.RequestTypeId == synthetic.RequestTypeId)).Activate();
            await db.SaveChangesAsync();
        }

        var agent = await ClientAsync(Roles.CsAgent, synthetic.Intake);
        var manager = await ClientAsync(Roles.CsManager);
        var inFlight = await CreateTicketAsync(agent, synthetic);

        await ReadAsync<AdminRequestTypeDetailDto>(await admin.PutAsJsonAsync(
            $"/api/admin/request-types/{synthetic.RequestTypeId}/configuration-enforcement", new SetConfigurationEnforcementRequestDto(true)));

        Assert.Null((await GetAsync(manager, inFlight)).CurrentWorkflowStepId);
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(manager, manager, inFlight, synthetic.Collections)).StatusCode);

        var tracked = await CreateTicketAsync(agent, synthetic);
        Assert.Equal("Intake Queue", (await GetAsync(manager, tracked)).CurrentWorkflowStepName);
    }
}
