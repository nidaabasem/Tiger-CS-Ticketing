using System.Text;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Import;

/// <summary>
/// What an import did (or, for a dry run, would do): one result per catalog
/// row, plus the distinct business decisions still open. Rendered as
/// Markdown for review before anything is applied to a shared environment.
/// </summary>
public sealed record RequestTypeCatalogImportReport(
    IReadOnlyList<RequestTypeImportResult> Results,
    bool Applied,
    bool ActivateResolved)
{
    public int Count(RequestTypeImportOutcome outcome) => Results.Count(r => r.Outcome == outcome);

    /// <summary>The distinct open questions, grouped by area, each with the request codes it blocks.</summary>
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
        if (!ActivateResolved)
        {
            sb.AppendLine("Run with activation disabled: every created row stays an inactive draft.");
            sb.AppendLine();
        }

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
        sb.AppendLine("| Code | Request type | Department | Priority | SLA (first response / resolution) | Outcome | Open decisions |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var result in Results)
        {
            var plan = result.Plan;
            var sla = plan.Sla is { } s
                ? $"{s.FirstResponseValue?.ToString() ?? "?"} {Unit(s.FirstResponseUnit)} / {s.ResolutionValue} {Unit(s.ResolutionUnit)}"
                : "—";
            sb.AppendLine(
                $"| {plan.Code} | {Cell(plan.Source.Name)} | {Cell(plan.Department?.Name ?? plan.Source.Department + " (not found)")} "
                + $"| {plan.DefaultPriority?.ToString() ?? "?"} | {sla} | {Label(result.Outcome)} | {result.Decisions.Count} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Workflows");
        sb.AppendLine();
        foreach (var result in Results)
        {
            var steps = string.Join(" → ", result.Plan.Steps.Select(DescribeStep));
            sb.AppendLine($"- **{result.Plan.Code}**: {Cell(steps)}");
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
        _ => area.ToString()
    };

    private static string Label(RequestTypeImportOutcome outcome) => outcome switch
    {
        RequestTypeImportOutcome.CreatedActive => "Created — active",
        RequestTypeImportOutcome.CreatedInactiveDraft => "Created — inactive draft",
        RequestTypeImportOutcome.LinkedToExisting => "Linked to existing (unchanged)",
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

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
