using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Import;

namespace TigerCS.Tests.WorkflowConfiguration.Import;

/// <summary>
/// The catalog → configuration mapping, without a database: every workbook
/// value either lands on an existing configuration concept or becomes an
/// explicit business decision — never a guess, a new status, or a new
/// approval type or role.
/// </summary>
public class RequestTypeCatalogMapperTests
{
    private const int Cs = 1, Col = 2, Reg = 3, Ho = 4, Cc = 5, Acc = 6, Fm = 7, Lcs = 8;

    /// <summary>Every department the workbook names, plus the reference seed's provisional Accounting.</summary>
    private static Dictionary<string, CatalogDepartment> AllDepartments(Action<Dictionary<string, CatalogDepartment>>? change = null)
    {
        var departments = new Dictionary<string, CatalogDepartment>(StringComparer.OrdinalIgnoreCase)
        {
            ["Customer Service"] = new(Cs, "Customer Service", true, true),
            ["Collections"] = new(Col, "Collections", true, true),
            ["Registration"] = new(Reg, "Registration", true, true),
            ["Handover"] = new(Ho, "Handover", true, true),
            ["Call Center"] = new(Cc, "Call Center", true, true),
            ["Accounting"] = new(Acc, "Accounting", true, true),
            ["Facilities Management"] = new(Fm, "Facilities Management", true, true),
            ["Leasing Customer Services"] = new(Lcs, "Leasing Customer Services", true, true)
        };
        change?.Invoke(departments);
        return departments;
    }

    private static RequestTypeCatalogRow Row(string code) =>
        RequestTypeCatalog.Load().Single(r => r.RequestCode == code);

    private static RequestTypeImportPlan Map(string code, Dictionary<string, CatalogDepartment>? departments = null) =>
        RequestTypeCatalogMapper.Map(Row(code), departments ?? AllDepartments());

    // ---- the source --------------------------------------------------------

    [Fact]
    public void Embedded_catalog_is_the_workbooks_35_rows_with_unique_codes_and_no_business_decisions_yet()
    {
        var rows = RequestTypeCatalog.Load();

        Assert.Equal(35, rows.Count);
        Assert.Equal(35, rows.Select(r => r.RequestCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rows, r => Assert.True(r.RequestCode.Length <= RequestType.CodeMaxLength));
        Assert.All(rows, r => Assert.Null(r.BusinessDecision));
        Assert.Equal("CS-GEN-001", rows[0].RequestCode);
        Assert.Equal("REC-OTH-001", rows[^1].RequestCode);
    }

    [Fact]
    public void A_catalog_that_repeats_a_code_is_refused_outright()
    {
        const string json = """{ "rows": [ { "requestCode": "X-1", "name": "A" }, { "requestCode": "x-1", "name": "B" } ] }""";

        var error = Assert.Throws<InvalidOperationException>(() => RequestTypeCatalog.Parse(json));
        Assert.Contains("X-1", error.Message);
    }

    // ---- scalar mappings -------------------------------------------------------

    [Theory]
    [InlineData("Low", PriorityLevel.Low)]
    [InlineData("Normal", PriorityLevel.Medium)] // the documented Normal ↔ Medium mapping
    [InlineData("High", PriorityLevel.High)]
    [InlineData("Urgent", PriorityLevel.High)]
    public void Priorities_map_onto_the_existing_fixed_tiers(string value, PriorityLevel expected) =>
        Assert.Equal(expected, RequestTypeCatalogMapper.MapPriority(value));

    [Fact]
    public void An_unknown_priority_is_a_decision_not_a_default() =>
        Assert.Null(RequestTypeCatalogMapper.MapPriority("Critical-ish"));

    [Theory]
    [InlineData("4 business hours", 4, SlaDurationUnit.Hours)]
    [InlineData("2 business hours", 2, SlaDurationUnit.Hours)]
    [InlineData("1 business day", 1, SlaDurationUnit.Days)]
    [InlineData("2 Business Days", 2, SlaDurationUnit.Days)]
    public void Business_durations_parse_in_their_own_unit(string value, int amount, SlaDurationUnit unit) =>
        Assert.Equal((amount, unit), RequestTypeCatalogMapper.ParseSla(value));

    [Theory]
    [InlineData("Same business day")]
    [InlineData("Based on severity")]
    [InlineData("Based on issue severity")]
    [InlineData("")]
    public void Non_durations_are_not_guessed(string value) => Assert.Null(RequestTypeCatalogMapper.ParseSla(value));

    [Fact]
    public void A_fully_specified_row_maps_every_setting_and_needs_no_decision()
    {
        var plan = Map("COL-PAY-001");

        Assert.True(plan.IsResolved);
        Assert.Empty(plan.Decisions);
        Assert.Equal(Col, plan.Department!.DepartmentId);
        Assert.Equal(PriorityLevel.Medium, plan.DefaultPriority);
        Assert.Equal(new PlannedSla(PriorityLevel.Medium, SlaDurationUnit.Days, 1, SlaDurationUnit.Hours, 4, SlaClockBasis.BusinessHours), plan.Sla);
        Assert.Equal("""["Customer","Project","Unit"]""", plan.RequiredFieldsJson);
        Assert.Equal("""["Payment proof if applicable"]""", plan.RequiredDocumentsJson);
        Assert.True(plan.AllowReopen);
    }

    [Fact]
    public void Required_documents_of_None_are_stored_as_none()
    {
        var plan = Map("CS-GEN-001");

        Assert.Null(plan.RequiredDocumentsJson);
        Assert.Equal("""["Customer","Project/Unit if applicable","Description"]""", plan.RequiredFieldsJson);
    }

    [Theory]
    [InlineData("CS-GEN-002")]
    [InlineData("FM-MNT-001")]
    [InlineData("FM-COM-001")]
    [InlineData("HO-HND-004")]
    public void A_resolution_sla_that_is_not_a_duration_leaves_no_sla_row_and_a_decision(string code)
    {
        var plan = Map(code);

        Assert.Null(plan.Sla);
        Assert.False(plan.IsResolved);
        Assert.Contains(plan.Decisions, d => d.Area == CatalogDecisionArea.Sla);
    }

    [Theory]
    [InlineData("CS-CMP-001", "CS Supervisor / Manager")]
    [InlineData("COL-PAY-002", "Collections Supervisor / Manager")]
    [InlineData("REG-NOC-001", "Registration Supervisor / Authorized Approver")]
    [InlineData("LCS-TEN-001", "Leasing Supervisor / Authorized Approver")]
    [InlineData("LEG-INQ-001", "Legal / Authorized Approver")]
    [InlineData("BRK-COM-001", "Responsible Manager")]
    public void Conditional_approvals_are_never_mapped_to_an_approval_type_or_role(string code, string approver)
    {
        var plan = Map(code);

        var decision = Assert.Single(plan.Decisions, d => d.Area == CatalogDecisionArea.Approval);
        Assert.Contains(approver, decision.Question);
        Assert.False(plan.IsResolved);
        Assert.DoesNotContain(plan.Steps, s => s.Kind is WorkflowStepKind.WaitingForApproval or WorkflowStepKind.Review);
    }

    [Fact]
    public void A_department_missing_from_TigerCS_blocks_creation_and_is_never_created()
    {
        var plan = Map("LCS-BKG-001", AllDepartments(d => d.Remove("Leasing Customer Services")));

        Assert.False(plan.CanCreate);
        Assert.Null(plan.Department);
        Assert.Contains(plan.Decisions, d => d.Area == CatalogDecisionArea.Department && d.Question.Contains("Leasing Customer Services"));
    }

    [Fact]
    public void Allow_Transfer_Yes_maps_onto_the_departments_existing_transfer_setting_and_a_conflict_is_a_decision()
    {
        Assert.Empty(Map("REG-CON-001").Decisions);

        var blocked = Map("REG-CON-001", AllDepartments(d => d["Registration"] = new(Reg, "Registration", true, AllowsTransferOut: false)));

        Assert.Contains(blocked.Decisions, d => d.Area == CatalogDecisionArea.Transfer);
        Assert.False(blocked.IsResolved);
    }

    [Fact]
    public void Allow_Transfer_No_is_a_decision_because_transfer_is_not_configured_per_request_type()
    {
        var row = Row("CS-GEN-001") with { AllowTransfer = "No" };

        var plan = RequestTypeCatalogMapper.Map(row, AllDepartments());

        Assert.Contains(plan.Decisions, d => d.Area == CatalogDecisionArea.Transfer && d.Question.Contains("per department"));
    }

    [Fact]
    public void Allow_Reopen_No_maps_to_a_request_type_that_cannot_be_reopened()
    {
        var row = Row("CS-GEN-001") with { AllowReopen = "No" };

        var plan = RequestTypeCatalogMapper.Map(row, AllDepartments());

        Assert.False(plan.AllowReopen);
        Assert.True(plan.IsResolved);
    }

    [Fact]
    public void A_filled_in_business_decision_column_is_surfaced_for_review_never_interpreted()
    {
        var row = Row("CS-GEN-001") with { BusinessDecision = "Approved with changes" };

        var plan = RequestTypeCatalogMapper.Map(row, AllDepartments());

        Assert.Contains(plan.Decisions, d => d.Area == CatalogDecisionArea.BusinessDecision);
    }

    // ---- workflows -------------------------------------------------------

    [Fact]
    public void A_single_department_flow_becomes_queue_work_resolve_close_in_that_department()
    {
        var plan = Map("COL-PAY-002");

        Assert.Equal(
            [
                new PlannedStep("Ticket Created", WorkflowStepKind.Created),
                new PlannedStep("Collections Queue", WorkflowStepKind.Assigned, DepartmentId: Col),
                new PlannedStep("Collections Agent — Follow-up; Escalate if needed", WorkflowStepKind.InProgress),
                new PlannedStep("Resolve", WorkflowStepKind.Resolved),
                new PlannedStep("Close", WorkflowStepKind.Closed)
            ],
            plan.Steps);
    }

    [Theory]
    [InlineData("REG-NOC-001")]
    [InlineData("REG-NOC-002")]
    [InlineData("REG-NOC-003")]
    public void The_Accounting_handoff_is_kept_as_an_unconfirmed_step_even_though_a_provisional_Accounting_department_exists(string code)
    {
        var plan = Map(code);

        var handoff = Assert.Single(plan.Steps, s => s.Name == "Handoff to Accounting");
        Assert.Equal(WorkflowStepKind.Assigned, handoff.Kind);
        Assert.Null(handoff.DepartmentId); // not the seeded ACC department — unconfirmed
        Assert.Contains(plan.Decisions, d => d.Area == CatalogDecisionArea.Handoff && d.Question.Contains("Accounting"));

        // ...and the return to Customer Service after it is a real, mapped handoff.
        var returnStep = Assert.Single(plan.Steps, s => s.Name == "Return to Customer Service");
        Assert.Equal(Cs, returnStep.DepartmentId);
        Assert.True(plan.Steps.ToList().IndexOf(returnStep) > plan.Steps.ToList().IndexOf(handoff));
    }

    [Fact]
    public void A_handoff_to_an_existing_department_is_mapped_to_that_department()
    {
        var plan = Map("HO-NOC-001");

        Assert.Equal(
            ["Ticket Created", "Customer Service Queue", "Customer Service Agent", "Handoff to Accounting",
             "Return to Customer Service", "Customer Service Agent", "Handoff to Handover", "Handover Agent", "Resolve", "Close"],
            plan.Steps.Select(s => s.Name));
        Assert.Equal(Ho, plan.Steps.Single(s => s.Name == "Handoff to Handover").DepartmentId);
    }

    [Fact]
    public void A_conditional_handoff_is_an_optional_step()
    {
        var plan = Map("HO-HND-004");

        var handoff = Assert.Single(plan.Steps, s => s.Name.StartsWith("Handoff to Facilities Management", StringComparison.Ordinal));
        Assert.True(handoff.IsOptional);
        Assert.Equal(Fm, handoff.DepartmentId);
    }

    [Fact]
    public void The_legal_flow_hands_off_unconfirmed_and_returns_to_Customer_Service_to_resolve()
    {
        var plan = Map("LEG-INQ-001");

        Assert.Equal(
            ["Ticket Created", "Customer Service Queue", "Handoff to Legal", "Legal — Legal Review/Response",
             "Return to Customer Service", "Resolve", "Close"],
            plan.Steps.Select(s => s.Name));
        Assert.Null(plan.Steps.Single(s => s.Name == "Handoff to Legal").DepartmentId);
        Assert.Equal(Cs, plan.Steps.Single(s => s.Name == "Return to Customer Service").DepartmentId);
    }

    [Theory]
    [InlineData("SAL-INQ-001", "Sales")]
    [InlineData("BRK-COM-001", "Admin Sales")]
    [InlineData("BRK-CHK-001", "Admin Sales")]
    [InlineData("REC-HR-001", "HR")]
    [InlineData("REC-MKT-001", "Marketing")]
    [InlineData("REC-OTH-001", "the responsible department")]
    public void Handoffs_to_areas_without_a_confirmed_department_stay_unmapped(string code, string target)
    {
        var plan = Map(code);

        var handoff = Assert.Single(plan.Steps, s => s.Name == $"Handoff to {target}");
        Assert.Null(handoff.DepartmentId);
        Assert.Contains(plan.Decisions, d => d.Area == CatalogDecisionArea.Handoff && d.Question.Contains(target));
    }

    [Fact]
    public void An_ambiguous_intake_queue_is_a_decision()
    {
        var plan = Map("FM-SVC-001");

        Assert.Contains(plan.Decisions, d => d.Area == CatalogDecisionArea.Workflow && d.Question.Contains("FM/Responsible Finance Queue"));
        Assert.Null(plan.Steps[1].DepartmentId);
    }

    [Fact]
    public void An_unrecognized_workflow_step_is_a_decision_and_is_kept_visible()
    {
        var row = Row("CS-GEN-001") with { ProposedWorkflow = "CS Queue → Agent → Teleport to Mars → Resolve → Close" };

        var plan = RequestTypeCatalogMapper.Map(row, AllDepartments());

        Assert.Contains(plan.Decisions, d => d.Area == CatalogDecisionArea.Workflow && d.Question.Contains("Teleport to Mars"));
        Assert.Contains(plan.Steps, s => s.Name.Contains("Teleport to Mars", StringComparison.Ordinal));
    }

    [Fact]
    public void Leaving_a_department_that_does_not_transfer_out_is_flagged()
    {
        var plan = Map("HO-NOC-001", AllDepartments(d => d["Customer Service"] = new(Cs, "Customer Service", true, AllowsTransferOut: false)));

        Assert.Contains(plan.Decisions, d => d.Area == CatalogDecisionArea.Handoff && d.Question.Contains("not to transfer"));
    }

    [Fact]
    public void Every_mapped_workflow_uses_only_offered_step_kinds_and_is_structurally_publishable()
    {
        var selectable = WorkflowStepKinds.Selectable.Select(k => k.Kind).ToHashSet();

        foreach (var row in RequestTypeCatalog.Load())
        {
            var plan = RequestTypeCatalogMapper.Map(row, AllDepartments());

            // Workflow text never becomes a status: only the designer's own
            // step kinds, never the retired Pending Internal, never an
            // approval stage the workbook did not resolve.
            Assert.All(plan.Steps, s => Assert.Contains(s.Kind, selectable));
            Assert.All(plan.Steps, s => Assert.True(s.DepartmentId is null || s.Kind == WorkflowStepKind.Assigned));

            var version = new WorkflowTemplate(0, 1, "T", "T", null, false, false, false, CatalogImportTestDb.Now, null);
            foreach (var step in plan.Steps)
            {
                version.AppendStep(step.Name, step.Kind, step.IsOptional, null, step.DepartmentId);
            }

            Assert.DoesNotContain(version.Validate(), i => i.Severity == WorkflowValidationSeverity.Error);
        }
    }

    [Fact]
    public void Exactly_the_fully_specified_rows_resolve()
    {
        var resolved = RequestTypeCatalog.Load()
            .Select(r => RequestTypeCatalogMapper.Map(r, AllDepartments()))
            .Where(p => p.IsResolved)
            .Select(p => p.Code)
            .ToList();

        Assert.Equal(
            [
                "CS-GEN-001", "CS-GEN-003", "CS-CMP-002",
                "REG-CON-001", "REG-DLD-001",
                "COL-PAY-001", "COL-PAY-003", "COL-PAY-004",
                "HO-HND-001", "HO-HND-002", "HO-HND-003",
                "FM-UTL-001",
                "LCS-BKG-001", "LCS-MOV-001", "LCS-CHK-001"
            ],
            resolved);
    }
}
