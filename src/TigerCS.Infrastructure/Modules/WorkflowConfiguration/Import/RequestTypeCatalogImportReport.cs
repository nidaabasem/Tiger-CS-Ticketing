using System.Text;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Import;

/// <summary>
/// What an import did (or, for a dry run, would do): one result per catalog
/// row, the current-vs-workbook comparison for every existing request type
/// it left alone, and the distinct business decisions still open. Rendered
/// as Markdown for review before anything is applied to a shared environment.
/// </summary>
public sealed record RequestTypeCatalogImportReport(
    IReadOnlyList<RequestTypeImportResult> Results,
    RequestTypeCatalogImportOptions Options)
{
    public bool Applied => Options.Apply;

    public int Count(RequestTypeImportOutcome outcome) => Results.Count(r => r.Outcome == outcome);

    /// <summary>The distinct open questions, grouped by area, each with the request codes it concerns.</summary>
    public IReadOnlyList<(CatalogDecisionArea Area, string Question, IReadOnlyList<string> Codes)> OpenDecisions() =>
        Results
            .SelectMany(r => r.Decisions.Select(d => (Decision: d, r.Plan.Code)))
            .GroupBy(x => x.Decision)
            .Select(g => (Area: g.Key.Area, Question: g.Key.Question, Codes: (IReadOnlyList<string>)g.Select(x => x.Code).Distinct().ToList()))
            .OrderBy(x => x.Area)
            .ThenBy(x => x.Codes[0], StringComparer.Ordinal)
            .ToList();

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Applied
            ? "# Request-type catalog import — applied"
            : "# Request-type catalog import — DRY RUN (nothing written)");
        sb.AppendLine();
        if (!Options.SchemaApplied)
        {
            sb.AppendLine("The AddRequestTypeCatalogImport migration is **not applied** to this database: this dry run read existing columns only, "
                + "so no row can show as already imported.");
            sb.AppendLine();
        }

        sb.AppendLine(Options.ActivateResolved
            ? "- Activation: rows with no open decision are created **active**."
            : "- Activation: **off** — every created request type and workflow is an inactive draft.");
        sb.AppendLine(Options.LinkExisting
            ? "- Existing request types: the catalog code is attached; nothing else changes."
            : "- Existing request types: **left completely unchanged** (not even the catalog code is written).");
        sb.AppendLine(Options.AllowAgentPriorityChange is { } allow
            ? $"- Agent priority change on created types: {(allow ? "allowed" : "not allowed")} (business decision supplied)."
            : "- Agent priority change on created types: open decision (stored as allowed, matching today's unenforced behaviour).");
        sb.AppendLine();

        sb.AppendLine("| Outcome | Rows |");
        sb.AppendLine("|---|---|");
        foreach (var outcome in Enum.GetValues<RequestTypeImportOutcome>())
        {
            sb.AppendLine($"| {Label(outcome)} | {Count(outcome)} |");
        }

        sb.AppendLine($"| **Total** | **{Results.Count}** |");
        sb.AppendLine();

        sb.AppendLine("## Rows");
        sb.AppendLine();
        sb.AppendLine("| Code | Request type | Department | Priority | SLA (first response / resolution) | Outcome | Workbook mapping | Open decisions |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var result in Results)
        {
            var plan = result.Plan;
            sb.AppendLine(
                $"| {plan.Code} | {Cell(plan.Source.Name)} | {Cell(plan.Department?.Name ?? plan.Source.Department + " (not found)")} "
                + $"| {plan.DefaultPriority?.ToString() ?? "?"} | {Sla(plan)} | {Label(result.Outcome)} "
                + $"| {(plan.IsResolved ? "resolved" : "open")} | {result.Decisions.Count} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Settings the workbook does not give");
        sb.AppendLine();
        sb.AppendLine("- **Pending Customer**: inherited — allowed on the request type and its workflow, the established behaviour whenever a request type does not restrict it.");
        sb.AppendLine("- **Agent priority change**: no default exists to inherit (a required per-request-type value) — see the Configuration decision below.");
        sb.AppendLine("- **Assignment**: no rule is created, so tickets use the department queue — the existing fallback.");
        sb.AppendLine("- **SLA clock start**: TicketCreated, the existing default; pause-on-Pending stays \"not decided\" as on every other type.");
        sb.AppendLine();

        sb.AppendLine("## Workflow definitions");
        sb.AppendLine();
        sb.AppendLine("Steps — including each handoff's department — are enforced at runtime only once an administrator turns on "
            + "configuration enforcement for the request type, which is refused while any decision below is open. Even then a "
            + "ticket changes department only through the existing Transfer action (CS Manager only, subject to the source "
            + "department's transfer setting); the workflow only decides whether that transfer is the expected next step.");
        sb.AppendLine();
        foreach (var result in Results)
        {
            var steps = string.Join(" → ", result.Plan.Steps.Select(DescribeStep));
            sb.AppendLine($"- **{result.Plan.Code}**: {Cell(steps)}");
        }

        var existing = Results.Where(r => r.Existing is not null).ToList();
        if (existing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Existing request types — current vs workbook");
            sb.AppendLine();
            foreach (var result in existing)
            {
                AppendComparison(sb, result);
            }
        }

        var decisions = OpenDecisions();
        sb.AppendLine();
        sb.AppendLine("## Decisions needed from the business");
        sb.AppendLine();
        if (decisions.Count == 0)
        {
            sb.AppendLine("None.");
        }

        foreach (var group in decisions.GroupBy(d => d.Area))
        {
            sb.AppendLine($"### {AreaLabel(group.Key)}");
            sb.AppendLine();
            foreach (var (_, question, codes) in group)
            {
                sb.AppendLine($"- {question} — *{string.Join(", ", codes)}*");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendComparison(StringBuilder sb, RequestTypeImportResult result)
    {
        var plan = result.Plan;
        var current = result.Existing!;
        var source = plan.Source;

        sb.AppendLine($"### {plan.Code} — {Cell(source.Name)} ({Cell(plan.Department?.Name ?? source.Department)})");
        sb.AppendLine();
        sb.AppendLine("| Aspect | Current (this database) | Workbook |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| Status | {(current.IsActive ? "Active" : "Inactive")} | — |");
        sb.AppendLine(current.ConfigurationEnforced
            ? "| Runtime | configuration enforced: steps tracked, SLA rows applied | — |"
            : "| Runtime | not enforced: steps are not tracked and due dates come from the per-priority SLA policy — the SLA rows below are stored only | — |");
        sb.AppendLine($"| Default priority | {current.DefaultPriority} | {Cell(source.DefaultPriority)} → {plan.DefaultPriority?.ToString() ?? "?"} |");
        sb.AppendLine($"| SLA | {Cell(Join(current.Slas))} | {Cell($"{plan.DefaultPriority}: first response {source.FirstResponseSla}, resolution {source.ResolutionSla}")} |");
        sb.AppendLine($"| Approvals gating the work | {Cell(Join(current.Approvals))} | {Cell(WorkbookApproval(source))} |");
        sb.AppendLine($"| Workflow | {Cell(current.WorkflowDescription + ": " + string.Join(" → ", current.Steps))} | {Cell(string.Join(" → ", plan.Steps.Select(DescribeStep)))} |");
        sb.AppendLine($"| Pending Customer | {(current.AllowPendingCustomer ? "allowed" : "not allowed")} | not stated |");
        sb.AppendLine($"| Agent priority change | {(current.AllowAgentPriorityChange ? "allowed" : "not allowed")} | not stated |");
        sb.AppendLine($"| Reopen | {Cell(CurrentReopen(current))} | {Cell(WorkbookReopen(source))} |");
        sb.AppendLine($"| Transfer | per department setting | {Cell(source.AllowTransfer)} |");
        sb.AppendLine();
    }

    private static string WorkbookApproval(RequestTypeCatalogRow source) =>
        string.Equals(source.NeedsApproval?.Trim(), "No", StringComparison.OrdinalIgnoreCase)
            ? "none"
            : $"{source.NeedsApproval}: {source.ApprovalRole ?? "(no approver given)"}";

    // "Allow Reopen = Yes" means the request type may be reopened — by the
    // approved rule: directly by CS Agent, CS Supervisor or CS Manager, and by
    // a System Administrator through the central override. No approval is
    // involved and none is created.
    private const string ApprovedReopenRule =
        "direct Reopen by CS Agent, CS Supervisor or CS Manager; System Administrator through the central override";

    private static string CurrentReopen(ExistingRequestTypeSnapshot current) =>
        !current.AllowReopen
            ? "not allowed"
            : $"allowed — {ApprovedReopenRule}"
              + (current.ReopenRequestRoute is { } existing ? $". Note: {existing}" : string.Empty);

    private static string WorkbookReopen(RequestTypeCatalogRow source) =>
        string.Equals(source.AllowReopen?.Trim(), "Yes", StringComparison.OrdinalIgnoreCase)
            ? $"Yes — {ApprovedReopenRule}"
            : source.AllowReopen ?? "not stated";

    private static string Join(IReadOnlyList<string> items) => items.Count == 0 ? "none" : string.Join("; ", items);

    private static string Sla(RequestTypeImportPlan plan) => plan.Sla is { } s
        ? $"{s.FirstResponseValue?.ToString() ?? "?"} {Unit(s.FirstResponseUnit)} / {s.ResolutionValue} {Unit(s.ResolutionUnit)}"
        : "—";

    private static string DescribeStep(PlannedStep step)
    {
        var suffix = step.Kind == WorkflowStepKind.Assigned && step.Name.StartsWith("Handoff", StringComparison.Ordinal) && step.DepartmentId is null
            ? " [target unconfirmed]"
            : string.Empty;
        return step.Name + suffix;
    }

    private static string AreaLabel(CatalogDecisionArea area) => area switch
    {
        CatalogDecisionArea.Sla => "SLA",
        CatalogDecisionArea.ExistingRequestType => "Existing request types",
        CatalogDecisionArea.BusinessDecision => "Business decision column",
        CatalogDecisionArea.Configuration => "Configuration with no default to inherit",
        _ => area.ToString()
    };

    private static string Label(RequestTypeImportOutcome outcome) => outcome switch
    {
        RequestTypeImportOutcome.CreatedActive => "Created — active",
        RequestTypeImportOutcome.CreatedInactiveDraft => "Created — inactive draft",
        RequestTypeImportOutcome.ExistingUnchanged => "Existing — left unchanged",
        RequestTypeImportOutcome.LinkedToExisting => "Existing — code linked only",
        RequestTypeImportOutcome.AlreadyImported => "Already imported (unchanged)",
        RequestTypeImportOutcome.Skipped => "Skipped",
        _ => outcome.ToString()
    };

    private static string Unit(SlaDurationUnit? unit) => unit switch
    {
        SlaDurationUnit.Hours => "bh",
        SlaDurationUnit.Days => "bd",
        SlaDurationUnit.Minutes => "min",
        _ => "?"
    };

    private static string Cell(string? value) => (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
