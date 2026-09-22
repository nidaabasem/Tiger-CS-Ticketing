using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Administration.Services;

public class AdminRequestTypeAppServiceTests
{
    private static readonly Guid Admin = Guid.NewGuid();

    private sealed record Fixture(
        AdminRequestTypeAppService Service,
        FakeRequestTypeRepository RequestTypes,
        FakeDepartmentRepository Departments,
        FakeWorkflowRepository Workflows,
        FakeWorkflowTemplateRepository Versions,
        FakeEmployeeRepository Employees,
        FakeUserDepartmentAssignmentRepository Memberships,
        FakeRequestTypeAssignmentRuleRepository Rules,
        FakeRequestTypeApprovalRequirementRepository Approvals,
        FakeRequestTypeSlaPolicyRepository Slas)
    {
        public Workflow PublishedWorkflow(string name = "Standard")
        {
            var workflow = Workflows.Add(new Workflow(Workflow.CodeFromName(name), name, null, DateTime.UtcNow));
            Versions.Add(TestWorkflows.PublishedStandard(workflow.WorkflowId, code: workflow.Code));
            return workflow;
        }

        public Workflow DraftOnlyWorkflow(string name = "Draft Only")
        {
            var workflow = Workflows.Add(new Workflow(Workflow.CodeFromName(name), name, null, DateTime.UtcNow));
            Versions.Add(TestWorkflows.StandardDraft(workflow.WorkflowId, code: workflow.Code));
            return workflow;
        }
    }

    private static Fixture Create()
    {
        var requestTypes = new FakeRequestTypeRepository();
        var departments = new FakeDepartmentRepository();
        var workflows = new FakeWorkflowRepository();
        var versions = new FakeWorkflowTemplateRepository();
        var employees = new FakeEmployeeRepository();
        var memberships = new FakeUserDepartmentAssignmentRepository();
        var rules = new FakeRequestTypeAssignmentRuleRepository();
        var approvals = new FakeRequestTypeApprovalRequirementRepository();
        var slas = new FakeRequestTypeSlaPolicyRepository();
        var service = new AdminRequestTypeAppService(
            requestTypes, departments, workflows, versions, rules, approvals, slas, employees, memberships,
            new FakeWorkflowConfigurationUnitOfWork(), new FakeAuditEntryWriter());
        return new Fixture(service, requestTypes, departments, workflows, versions, employees, memberships, rules, approvals, slas);
    }

    private static SaveRequestTypeRequestDto Request(int departmentId, int workflowId, string name = "Send Receipts") =>
        new(departmentId, name, workflowId, (byte)PriorityLevel.Medium, false, false, true, true);

    [Fact]
    public async Task Add_edit_deactivate_and_department_filtering()
    {
        var f = Create();
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var registration = f.Departments.AddDepartment("Registration", "REG");
        var workflow = f.PublishedWorkflow();

        var created = await f.Service.CreateAsync(Admin, Request(collections.DepartmentId, workflow.WorkflowId));
        Assert.Equal(AdminOutcome.Success, created.Outcome);
        Assert.Equal("Collections", created.Value!.DepartmentName);
        Assert.Equal(1, created.Value.Workflow.ActiveVersionNumber);

        Assert.Equal(AdminOutcome.ValidationFailed, (await f.Service.CreateAsync(Admin, Request(collections.DepartmentId, workflow.WorkflowId))).Outcome);
        await f.Service.CreateAsync(Admin, Request(registration.DepartmentId, workflow.WorkflowId, "Register Unit"));

        var edited = await f.Service.UpdateAsync(Admin, created.Value.RequestTypeId,
            Request(collections.DepartmentId, workflow.WorkflowId) with { Name = "Send Receipts (Email)", AllowPendingCustomer = true });
        Assert.Equal("Send Receipts (Email)", edited.Value!.Name);
        Assert.True(edited.Value.AllowPendingCustomer);

        // Department filtering, for administration and for the New Ticket picker.
        Assert.Single(await f.Service.ListAsync(collections.DepartmentId, includeInactive: true));
        Assert.Equal(2, (await f.Service.ListAsync(null, includeInactive: true)).Count);
        Assert.Equal("Register Unit", Assert.Single(await f.Service.ListOptionsAsync(registration.DepartmentId)).Name);

        var deactivated = await f.Service.SetActivationAsync(Admin, created.Value.RequestTypeId, new SetActiveRequestDto(false));
        Assert.False(deactivated.Value!.IsActive);
        Assert.Empty(await f.Service.ListOptionsAsync(collections.DepartmentId));
        Assert.NotNull(await f.RequestTypes.GetByIdAsync(created.Value.RequestTypeId));
        Assert.Contains(await f.Service.ListAsync(collections.DepartmentId, includeInactive: true), r => r.RequestTypeId == created.Value.RequestTypeId);
    }

    [Fact]
    public async Task Workflow_without_a_published_version_cannot_be_assigned_or_activated()
    {
        var f = Create();
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var draftOnly = f.DraftOnlyWorkflow();

        var refused = await f.Service.CreateAsync(Admin, Request(collections.DepartmentId, draftOnly.WorkflowId));

        Assert.Equal(AdminOutcome.ValidationFailed, refused.Outcome);
        Assert.Contains(refused.Errors!, e => e.Contains("no published version", StringComparison.Ordinal));

        var published = f.PublishedWorkflow("Later");
        var created = await f.Service.CreateAsync(Admin, Request(collections.DepartmentId, published.WorkflowId));
        Assert.Equal(AdminOutcome.ValidationFailed, (await f.Service.UpdateAsync(Admin, created.Value!.RequestTypeId, Request(collections.DepartmentId, draftOnly.WorkflowId))).Outcome);
    }

    [Fact]
    public async Task Referenced_request_type_keeps_its_department_and_is_never_deleted()
    {
        var f = Create();
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var registration = f.Departments.AddDepartment("Registration", "REG");
        var workflow = f.PublishedWorkflow();
        var created = await f.Service.CreateAsync(Admin, Request(collections.DepartmentId, workflow.WorkflowId));
        f.RequestTypes.TicketCounts[created.Value!.RequestTypeId] = 3;

        var moved = await f.Service.UpdateAsync(Admin, created.Value.RequestTypeId, Request(registration.DepartmentId, workflow.WorkflowId));

        Assert.Equal(AdminOutcome.Conflict, moved.Outcome);
        Assert.Equal(collections.DepartmentId, (await f.Service.GetAsync(created.Value.RequestTypeId))!.DepartmentId);
        Assert.Equal(3, (await f.Service.GetAsync(created.Value.RequestTypeId))!.TicketCount);
    }

    [Fact]
    public async Task Assignment_rule_only_accepts_active_members_of_the_department()
    {
        var f = Create();
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var workflow = f.PublishedWorkflow();
        var requestType = (await f.Service.CreateAsync(Admin, Request(collections.DepartmentId, workflow.WorkflowId))).Value!;
        var member = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        f.Employees.Add(new Employee(member, "Member", false, DateTime.UtcNow));
        f.Employees.Add(new Employee(outsider, "Outsider", false, DateTime.UtcNow));
        f.Memberships.Assignments.Add(new UserDepartmentAssignment(member, collections.DepartmentId, true, DateTime.UtcNow, null));

        var outsiderRule = await f.Service.SaveAssignmentRuleAsync(Admin, requestType.RequestTypeId,
            new SaveAssignmentRuleRequestDto(AssignmentMode.SpecificEmployee, outsider, null, null));
        Assert.Equal(AdminOutcome.ValidationFailed, outsiderRule.Outcome);

        var saved = await f.Service.SaveAssignmentRuleAsync(Admin, requestType.RequestTypeId,
            new SaveAssignmentRuleRequestDto(AssignmentMode.SpecificEmployee, member, null, null));
        Assert.Equal(AdminOutcome.Success, saved.Outcome);
        Assert.Equal("Member", saved.Value!.AssignmentRule!.PrimaryEmployeeName);
        Assert.Equal("Member", (await f.Service.ListAsync(null, true)).Single().AssignmentSummary);

        var queue = await f.Service.SaveAssignmentRuleAsync(Admin, requestType.RequestTypeId,
            new SaveAssignmentRuleRequestDto(AssignmentMode.DepartmentQueue, null, null, null));
        Assert.Equal(AssignmentMode.DepartmentQueue, queue.Value!.AssignmentRule!.Mode);
        Assert.Equal("Collections Queue", (await f.Service.ListAsync(null, true)).Single().AssignmentSummary);
    }

    [Fact]
    public async Task Approval_requirement_and_sla_rows_are_edited_in_place_with_controlled_values()
    {
        var f = Create();
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var accounting = f.Departments.AddDepartment("Accounting", "ACC");
        var workflow = f.PublishedWorkflow();
        var requestType = (await f.Service.CreateAsync(Admin, Request(collections.DepartmentId, workflow.WorkflowId))).Value!;

        var requirement = await f.Service.SaveApprovalRequirementAsync(Admin, requestType.RequestTypeId, ApprovalType.AccountingApproval,
            new SaveApprovalRequirementRequestDto(ApprovalTargetKind.Department, accounting.DepartmentId, null, null));
        Assert.Equal(AdminOutcome.Success, requirement.Outcome);
        var stored = Assert.Single(requirement.Value!.ApprovalRequirements);
        Assert.Equal("Accounting", stored.TargetDepartmentName);

        var repointed = await f.Service.SaveApprovalRequirementAsync(Admin, requestType.RequestTypeId, ApprovalType.AccountingApproval,
            new SaveApprovalRequirementRequestDto(ApprovalTargetKind.Role, null, Roles.CsSupervisor, null));
        Assert.Equal(ApprovalTargetKind.Role, Assert.Single(repointed.Value!.ApprovalRequirements).TargetKind);

        var badRole = await f.Service.SaveApprovalRequirementAsync(Admin, requestType.RequestTypeId, ApprovalType.CustomerServiceApproval,
            new SaveApprovalRequirementRequestDto(ApprovalTargetKind.Role, null, "Wizard", null));
        Assert.Equal(AdminOutcome.ValidationFailed, badRole.Outcome);
        Assert.Equal(AdminOutcome.ValidationFailed, (await f.Service.SaveApprovalRequirementAsync(Admin, requestType.RequestTypeId, (ApprovalType)9,
            new SaveApprovalRequirementRequestDto(ApprovalTargetKind.Role, null, Roles.CsSupervisor, null))).Outcome);

        var sla = await f.Service.SaveSlaPolicyAsync(Admin, requestType.RequestTypeId, (byte)PriorityLevel.Medium,
            new SaveSlaPolicyRequestDto(SlaTriggerType.ApprovalReceived, SlaDurationUnit.Days, null, null, 1, null, false, null, null, null, null));
        Assert.Equal(AdminOutcome.Success, sla.Outcome);
        Assert.Equal(1, Assert.Single(sla.Value!.SlaPolicies).ResolutionTargetValue);

        var slaEdited = await f.Service.SaveSlaPolicyAsync(Admin, requestType.RequestTypeId, (byte)PriorityLevel.Medium,
            new SaveSlaPolicyRequestDto(SlaTriggerType.ApprovalReceived, SlaDurationUnit.Days, null, null, 1, 2, false, null, true, null, null));
        Assert.Equal(2, Assert.Single(slaEdited.Value!.SlaPolicies).ResolutionMaximumValue);
        Assert.True(Assert.Single(slaEdited.Value.SlaPolicies).PausesOnPendingCustomer);

        var invalidRange = await f.Service.SaveSlaPolicyAsync(Admin, requestType.RequestTypeId, (byte)PriorityLevel.High,
            new SaveSlaPolicyRequestDto(SlaTriggerType.TicketCreated, SlaDurationUnit.Days, null, null, 12, 10, false, null, null, null, null));
        Assert.Equal(AdminOutcome.ValidationFailed, invalidRange.Outcome);
    }

    // ---- The retired PendingThirdParty pause setting ----

    [Fact]
    public async Task Saving_an_sla_policy_preserves_a_historical_PausesOnPendingInternal_value()
    {
        // The setting configures a pause window for TicketStatus.PendingThirdParty,
        // which is retired. It is frozen rather than deleted: the column and its
        // stored value stay, so a save that changes unrelated SLA numbers must
        // leave it exactly as it was — including when the request says otherwise.
        // This matters because the Administration form is a blank upsert, never
        // pre-filled from the policy being saved.
        var f = Create();
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var workflow = f.PublishedWorkflow();
        var requestType = (await f.Service.CreateAsync(Admin, Request(collections.DepartmentId, workflow.WorkflowId))).Value!;

        // A historical row, written when the setting was still live.
        await f.Slas.AddAsync(new RequestTypeSlaPolicy(
            requestType.RequestTypeId, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days,
            firstResponseTargetValue: null, firstResponseMaximumValue: null,
            resolutionTargetValue: 5, resolutionMaximumValue: null,
            isImmediate: false, clockBasis: null,
            pausesOnPendingCustomer: true, pausesOnPendingInternal: true));

        var edited = await f.Service.SaveSlaPolicyAsync(Admin, requestType.RequestTypeId, (byte)PriorityLevel.Medium,
            new SaveSlaPolicyRequestDto(SlaTriggerType.TicketCreated, SlaDurationUnit.Days, null, null, 7, null, false, null, true, null, null));

        var policy = Assert.Single(edited.Value!.SlaPolicies);
        Assert.Equal(7, policy.ResolutionTargetValue);
        Assert.True(policy.PausesOnPendingCustomer);
        Assert.True(policy.PausesOnPendingInternal);

        // Even an explicit attempt to change it leaves the stored value alone.
        var overwritten = await f.Service.SaveSlaPolicyAsync(Admin, requestType.RequestTypeId, (byte)PriorityLevel.Medium,
            new SaveSlaPolicyRequestDto(SlaTriggerType.TicketCreated, SlaDurationUnit.Days, null, null, 7, null, false, null, true, false, null));
        Assert.True(Assert.Single(overwritten.Value!.SlaPolicies).PausesOnPendingInternal);
    }

    [Fact]
    public async Task A_new_sla_policy_is_created_with_no_PausesOnPendingInternal_decision()
    {
        var f = Create();
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var workflow = f.PublishedWorkflow();
        var requestType = (await f.Service.CreateAsync(Admin, Request(collections.DepartmentId, workflow.WorkflowId))).Value!;

        // The request still carries the field (the wire contract is unchanged)
        // and it is deliberately not applied: a brand-new policy is never
        // configured for a pause window no ticket can open any more.
        var created = await f.Service.SaveSlaPolicyAsync(Admin, requestType.RequestTypeId, (byte)PriorityLevel.Medium,
            new SaveSlaPolicyRequestDto(SlaTriggerType.TicketCreated, SlaDurationUnit.Days, null, null, 3, null, false, null, true, true, null));

        var policy = Assert.Single(created.Value!.SlaPolicies);
        Assert.Null(policy.PausesOnPendingInternal);

        // Pending Customer's own setting is untouched by any of this.
        Assert.True(policy.PausesOnPendingCustomer);
    }
}
