using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Import;

/// <summary>An existing department as the catalog mapper sees it: identity plus the existing transfer-out setting.</summary>
/// <param name="AllowsTransferOut">
/// The department's <see cref="DepartmentWorkflowSettings.AllowTransferToOtherDepartments"/>;
/// true when the department has no settings row (no narrowing configured).
/// </param>
public sealed record CatalogDepartment(int DepartmentId, string Name, bool IsActive, bool AllowsTransferOut);

/// <summary>What a pending business decision is about — the report groups by it.</summary>
public enum CatalogDecisionArea
{
    Department,
    Priority,
    Sla,
    Approval,
    Handoff,
    Workflow,
    Transfer,
    Reopen,
    ExistingRequestType,
    BusinessDecision
}

/// <summary>One question the business must answer before a row can be activated.</summary>
public sealed record CatalogDecision(CatalogDecisionArea Area, string Question);

/// <summary>One step of the workflow version the import will create.</summary>
public sealed record PlannedStep(string Name, WorkflowStepKind Kind, bool IsOptional = false, int? DepartmentId = null);

/// <summary>The single SLA row the catalog gives (at the request type's default priority).</summary>
public sealed record PlannedSla(
    PriorityLevel Priority,
    SlaDurationUnit ResolutionUnit,
    int ResolutionValue,
    SlaDurationUnit? FirstResponseUnit,
    int? FirstResponseValue,
    SlaClockBasis ClockBasis);

/// <summary>
/// The fully interpreted form of one catalog row. <see cref="Decisions"/>
/// lists every value the mapper could not resolve without inventing
/// something; a row may only be activated when it is empty.
/// </summary>
public sealed record RequestTypeImportPlan(
    RequestTypeCatalogRow Source,
    CatalogDepartment? Department,
    PriorityLevel? DefaultPriority,
    PlannedSla? Sla,
    string? RequiredFieldsJson,
    string? RequiredDocumentsJson,
    bool AllowReopen,
    IReadOnlyList<PlannedStep> Steps,
    IReadOnlyList<CatalogDecision> Decisions)
{
    public string Code => Source.RequestCode.Trim();

    /// <summary>Whether a request type can be created at all — it needs its department and a priority (both required columns).</summary>
    public bool CanCreate => Department is not null && DefaultPriority is not null;

    /// <summary>Whether every value resolved — the only state in which the row may go live.</summary>
    public bool IsResolved => CanCreate && Decisions.Count == 0;
}

/// <summary>
/// Interprets catalog rows against the EXISTING configuration model — no
/// new ticket status, step kind, approval type, role or department is ever
/// introduced here. Every vocabulary below is explicit and closed: a value
/// that is not in it (or that the business has said still needs
/// confirmation) is never guessed at; it becomes a
/// <see cref="CatalogDecision"/> and keeps the row an inactive draft.
///
/// <para>
/// <b>Workflow text becomes steps, never statuses.</b> "Proposed Workflow"
/// is split on "→"; queue/intake tokens and handoffs become Department Queue
/// / Assignment steps (the handoff target recorded as the step's
/// department), agent tokens and activity descriptions ("Review account",
/// "Escalate if needed") become the responsible team's single work step
/// whose name carries the descriptions, and Resolve/Close map onto the
/// existing Resolve/Close steps. The ticket lifecycle is unchanged: a
/// handoff is still performed with the existing Transfer action under its
/// existing role rule.
/// </para>
/// </summary>
public static class RequestTypeCatalogMapper
{
    public const string CustomerServiceDepartmentName = "Customer Service";

    private const int MaxStepNameLength = 100;

    // Stored as the business wrote it — no \u escaping of "/" or "—".
    private static readonly JsonSerializerOptions ListJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Handoff targets the business has said still need confirmation (the
    /// Accounting, Legal and Sales areas, plus the other areas no department
    /// or role exists for yet). Even where a same-named department happens
    /// to exist — Accounting is provisionally seeded — the handoff is not
    /// mapped until the business confirms it.
    /// </summary>
    private static readonly Dictionary<string, string> UnconfirmedHandoffs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["accounting"] = "Accounting",
        ["acounting"] = "Accounting", // workbook spelling
        ["admin sales"] = "Admin Sales",
        ["sales handoff"] = "Sales",
        ["legal handoff"] = "Legal",
        ["hr handoff"] = "HR",
        ["marketing handoff"] = "Marketing",
        ["route to responsible department"] = "the responsible department"
    };

    /// <summary>Handoffs to a named department that exists in TigerCS today — mapped once that department is found by name.</summary>
    private static readonly Dictionary<string, (string Department, bool Optional)> ConfirmedHandoffs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["handover agent"] = ("Handover", false),
        ["facilities management if needed"] = ("Facilities Management", true)
    };

    /// <summary>
    /// The first token of a workflow — where the ticket is taken in. Each
    /// names the department whose queue receives it, which must be the
    /// row's own Department column.
    /// </summary>
    private static readonly Dictionary<string, string> IntakeQueues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs queue"] = CustomerServiceDepartmentName,
        ["cs intake"] = CustomerServiceDepartmentName,
        ["cs / call center"] = CustomerServiceDepartmentName,
        ["reception / cs"] = CustomerServiceDepartmentName,
        ["registration queue"] = "Registration",
        ["collections queue"] = "Collections",
        ["handover queue"] = "Handover",
        ["fm queue"] = "Facilities Management",
        ["leasing cs queue"] = "Leasing Customer Services"
    };

    /// <summary>Agent tokens: null department = the team currently holding the ticket; a named one implies a return/handoff to it first.</summary>
    private static readonly Dictionary<string, (string? Department, string? Activity)> Agents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["agent"] = (null, null),
        ["agent/technician"] = (null, "Technician"),
        ["assign agent/technician"] = (null, "Technician"),
        ["cs agent"] = (CustomerServiceDepartmentName, null),
        ["cs agentt"] = (CustomerServiceDepartmentName, null) // workbook spelling
    };

    /// <summary>Activity descriptions — folded into the current team's work step, never promoted to a status or a step kind.</summary>
    private static readonly HashSet<string> Activities = new(StringComparer.OrdinalIgnoreCase)
    {
        "obtain update if needed", "escalate if needed", "record/route", "review", "review account",
        "follow-up", "confirm cheque/collection", "coordinate", "confirm available slot",
        "coordinate requirements", "in progress", "review/coordinate", "process/review",
        "confirm booking/details", "review dld/registration status", "confirm collection",
        "legal review/response"
    };

    private static readonly Regex SlaPattern = new(
        @"^(?<value>\d+)\s+business\s+(?<unit>hours?|days?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static RequestTypeImportPlan Map(
        RequestTypeCatalogRow row, IReadOnlyDictionary<string, CatalogDepartment> departmentsByName)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(departmentsByName);

        var decisions = new List<CatalogDecision>();

        // ---- department -------------------------------------------------
        var department = Find(departmentsByName, row.Department);
        if (department is null)
        {
            decisions.Add(new(CatalogDecisionArea.Department,
                $"Department '{row.Department}' does not exist in TigerCS. Create it (or name the existing department to use) before this request type can be imported."));
        }
        else if (!department.IsActive)
        {
            decisions.Add(new(CatalogDecisionArea.Department,
                $"Department '{department.Name}' is inactive. Reactivate it, or confirm this request type should stay inactive."));
        }

        // ---- priority -----------------------------------------------------
        var priority = MapPriority(row.DefaultPriority);
        if (priority is null)
        {
            decisions.Add(new(CatalogDecisionArea.Priority,
                $"Default Priority '{row.DefaultPriority}' is not one of the recognized values (Low, Normal, High, Urgent)."));
        }

        // ---- SLA ------------------------------------------------------------
        var firstResponse = ParseSla(row.FirstResponseSla);
        var resolution = ParseSla(row.ResolutionSla);
        if (firstResponse is null)
        {
            decisions.Add(new(CatalogDecisionArea.Sla,
                $"First Response SLA '{row.FirstResponseSla}' is not a duration (expected e.g. '4 business hours'). Give a number of business hours or days."));
        }

        if (resolution is null)
        {
            decisions.Add(new(CatalogDecisionArea.Sla,
                $"Resolution SLA '{row.ResolutionSla}' is not a duration (expected e.g. '2 business days'). Give a number of business hours or days"
                + (row.ResolutionSla.Contains("severity", StringComparison.OrdinalIgnoreCase)
                    ? ", per severity level if it varies (severity is not modeled today — only priority is)."
                    : ".")));
        }

        PlannedSla? sla = priority is { } p && resolution is { } res
            ? new PlannedSla(p, res.Unit, res.Value, firstResponse?.Unit, firstResponse?.Value, SlaClockBasis.BusinessHours)
            : null;

        // ---- approval -------------------------------------------------------
        switch (Normalize(row.NeedsApproval))
        {
            case "no":
                break;
            case "yes":
            case "conditional":
                decisions.Add(new(CatalogDecisionArea.Approval,
                    $"Approval is '{row.NeedsApproval}' with approver '{row.ApprovalRole ?? "(none given)"}'. Confirm "
                    + (Normalize(row.NeedsApproval) == "conditional" ? "when approval is required, " : string.Empty)
                    + "which approval type applies (today only Accounting Approval or Customer Service Approval can gate work) "
                    + "and which existing role, department or employee decides — "
                    + $"'{row.ApprovalRole}' is not one of the fixed roles."));
                break;
            default:
                decisions.Add(new(CatalogDecisionArea.Approval, $"Needs Approval? '{row.NeedsApproval}' is not Yes, No or Conditional."));
                break;
        }

        // ---- transfer / reopen -------------------------------------------
        var allowTransfer = ParseYesNo(row.AllowTransfer);
        if (allowTransfer is null)
        {
            decisions.Add(new(CatalogDecisionArea.Transfer, $"Allow Transfer? '{row.AllowTransfer}' is not Yes or No."));
        }
        else if (allowTransfer == false)
        {
            decisions.Add(new(CatalogDecisionArea.Transfer,
                "Allow Transfer? is No, but TigerCS controls transfers per department (Department Workflow Settings), not per request type. "
                + "Confirm whether the whole department should stop transferring out, or whether a per-request-type rule is needed."));
        }
        else if (department is { AllowsTransferOut: false })
        {
            decisions.Add(new(CatalogDecisionArea.Transfer,
                $"Allow Transfer? is Yes, but department '{department.Name}' is configured not to transfer tickets out. Confirm which is right."));
        }

        var allowReopen = ParseYesNo(row.AllowReopen);
        if (allowReopen is null)
        {
            decisions.Add(new(CatalogDecisionArea.Reopen, $"Allow Reopen? '{row.AllowReopen}' is not Yes or No."));
        }

        // ---- the business's own columns ---------------------------------------
        if (!string.IsNullOrWhiteSpace(row.BusinessDecision) || !string.IsNullOrWhiteSpace(row.BusinessComments))
        {
            decisions.Add(new(CatalogDecisionArea.BusinessDecision,
                $"The workbook carries a business decision/comment ('{row.BusinessDecision}' / '{row.BusinessComments}') that must be reviewed before activation."));
        }

        // ---- workflow -------------------------------------------------------
        var steps = MapWorkflow(row, department, departmentsByName, decisions);

        return new RequestTypeImportPlan(
            row,
            department,
            priority,
            sla,
            ToJsonList(row.RequiredFields),
            ToJsonList(row.RequiredDocuments),
            AllowReopen: allowReopen ?? false,
            steps,
            decisions);
    }

    /// <summary>
    /// The catalog's priority words onto the EXISTING fixed priorities, using
    /// the mapping already documented for the SLA document: "Normal" is
    /// Medium and "Urgent" is High (<see cref="WorkflowReferenceData.NormalUrgencyPriority"/>);
    /// Low and High are the same-named tiers.
    /// </summary>
    public static PriorityLevel? MapPriority(string? value) => Normalize(value) switch
    {
        "low" => PriorityLevel.Low,
        "normal" => WorkflowReferenceData.NormalUrgencyPriority,
        "high" => PriorityLevel.High,
        "urgent" => WorkflowReferenceData.UrgentUrgencyPriority,
        _ => null
    };

    /// <summary>"4 business hours" → (4, Hours); "2 business days" → (2, Days). Anything else (e.g. "Same business day", "Based on severity") → null.</summary>
    public static (int Value, SlaDurationUnit Unit)? ParseSla(string? value)
    {
        var match = SlaPattern.Match(Normalize(value));
        if (!match.Success || !int.TryParse(match.Groups["value"].Value, out var amount) || amount <= 0)
        {
            return null;
        }

        return (amount, match.Groups["unit"].Value.StartsWith("hour", StringComparison.OrdinalIgnoreCase)
            ? SlaDurationUnit.Hours
            : SlaDurationUnit.Days);
    }

    private static List<PlannedStep> MapWorkflow(
        RequestTypeCatalogRow row,
        CatalogDepartment? owningDepartment,
        IReadOnlyDictionary<string, CatalogDepartment> departmentsByName,
        List<CatalogDecision> decisions)
    {
        var steps = new List<PlannedStep> { new("Ticket Created", WorkflowStepKind.Created) };
        var tokens = row.ProposedWorkflow
            .Split('→', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => Regex.Replace(t, @"\s+", " "))
            .Where(t => t.Length > 0)
            .ToList();

        if (tokens.Count == 0)
        {
            decisions.Add(new(CatalogDecisionArea.Workflow, "Proposed Workflow is empty."));
            return steps;
        }

        // Who holds the ticket as the flow is walked: the label is always
        // known (from the workbook), the department only once resolved.
        var holderLabel = owningDepartment?.Name ?? row.Department;
        var holder = owningDepartment;

        // The open work step of the current holder, if any — activities are
        // folded into it until the holder changes or the flow resolves.
        WorkStep? work = null;

        void FlushWork()
        {
            if (work is not null)
            {
                steps.Add(new(work.Name(), WorkflowStepKind.InProgress));
                work = null;
            }
        }

        void WorkBy(string label, bool agent, string? activity)
        {
            work ??= new WorkStep(label, agent);
            work.Agent |= agent;
            if (activity is not null)
            {
                work.Activities.Add(activity);
            }
        }

        void HandTo(CatalogDepartment? target, string targetLabel, bool optional, bool isReturnToCs)
        {
            FlushWork();
            if (holder is { AllowsTransferOut: false })
            {
                decisions.Add(new(CatalogDecisionArea.Handoff,
                    $"The workflow hands the ticket from '{holder.Name}' to '{targetLabel}', but '{holder.Name}' is configured not to transfer tickets out."));
            }

            var name = isReturnToCs ? "Return to Customer Service" : $"Handoff to {targetLabel}" + (optional ? " (if needed)" : string.Empty);
            steps.Add(new(Truncate(name), WorkflowStepKind.Assigned, optional, target?.DepartmentId));
            if (!optional)
            {
                holder = target;
                holderLabel = targetLabel;
            }
        }

        void EnsureHeldBy(string departmentName)
        {
            if (string.Equals(holderLabel, departmentName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var target = Find(departmentsByName, departmentName);
            if (target is null)
            {
                decisions.Add(new(CatalogDecisionArea.Handoff,
                    $"The workflow moves the ticket to '{departmentName}', which does not exist in TigerCS."));
            }

            HandTo(target, target?.Name ?? departmentName, optional: false,
                isReturnToCs: string.Equals(departmentName, CustomerServiceDepartmentName, StringComparison.OrdinalIgnoreCase));
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var key = token.ToLowerInvariant();

            if (i == 0)
            {
                if (IntakeQueues.TryGetValue(key, out var intakeDepartment))
                {
                    if (!string.Equals(intakeDepartment, row.Department, StringComparison.OrdinalIgnoreCase))
                    {
                        decisions.Add(new(CatalogDecisionArea.Workflow,
                            $"The workflow starts in '{token}', but the request type belongs to '{row.Department}'. Confirm which department takes it in."));
                    }

                    steps.Add(new(Truncate($"{holderLabel} Queue"), WorkflowStepKind.Assigned, DepartmentId: owningDepartment?.DepartmentId));
                }
                else
                {
                    decisions.Add(new(CatalogDecisionArea.Workflow,
                        $"The workflow's intake '{token}' does not name one department queue. Confirm which department's queue receives these tickets."));
                    steps.Add(new(Truncate(token), WorkflowStepKind.Assigned));
                }

                continue;
            }

            if (UnconfirmedHandoffs.TryGetValue(key, out var unconfirmed))
            {
                decisions.Add(new(CatalogDecisionArea.Handoff,
                    $"Confirm the handoff to {unconfirmed}: which existing TigerCS department (or other mechanism) receives it."));
                HandTo(target: null, unconfirmed, optional: false, isReturnToCs: false);
                continue;
            }

            if (ConfirmedHandoffs.TryGetValue(key, out var confirmed))
            {
                var target = Find(departmentsByName, confirmed.Department);
                if (target is null)
                {
                    decisions.Add(new(CatalogDecisionArea.Handoff,
                        $"The workflow hands off to '{confirmed.Department}', which does not exist in TigerCS."));
                }

                if (!string.Equals(holderLabel, confirmed.Department, StringComparison.OrdinalIgnoreCase))
                {
                    HandTo(target, target?.Name ?? confirmed.Department, confirmed.Optional, isReturnToCs: false);
                }

                if (key.EndsWith("agent", StringComparison.Ordinal))
                {
                    WorkBy(holderLabel, agent: true, activity: null);
                }

                continue;
            }

            if (Agents.TryGetValue(key, out var agent))
            {
                if (agent.Department is { } agentDepartment)
                {
                    EnsureHeldBy(agentDepartment);
                }

                WorkBy(holderLabel, agent: true, agent.Activity);
                continue;
            }

            if (Activities.Contains(key))
            {
                WorkBy(holderLabel, agent: false, token);
                continue;
            }

            switch (key)
            {
                case "resolve":
                    FlushWork();
                    steps.Add(new("Resolve", WorkflowStepKind.Resolved));
                    continue;
                case "cs resolve":
                    EnsureHeldBy(CustomerServiceDepartmentName);
                    FlushWork();
                    steps.Add(new("Resolve", WorkflowStepKind.Resolved));
                    continue;
                case "acknowledge/resolve":
                    WorkBy(holderLabel, agent: false, "Acknowledge");
                    FlushWork();
                    steps.Add(new("Resolve", WorkflowStepKind.Resolved));
                    continue;
                case "close":
                    FlushWork();
                    steps.Add(new("Close", WorkflowStepKind.Closed));
                    continue;
                case "resolve / close":
                    FlushWork();
                    steps.Add(new("Resolve", WorkflowStepKind.Resolved));
                    steps.Add(new("Close", WorkflowStepKind.Closed));
                    continue;
            }

            decisions.Add(new(CatalogDecisionArea.Workflow,
                $"Workflow step '{token}' is not a recognized queue, agent, handoff, activity, Resolve or Close. Clarify what it means."));
            WorkBy(holderLabel, agent: false, token);
        }

        FlushWork();
        return steps;
    }

    private sealed class WorkStep(string holderLabel, bool agent)
    {
        public bool Agent { get; set; } = agent;
        public List<string> Activities { get; } = [];

        public string Name()
        {
            var name = Agent ? $"{holderLabel} Agent" : holderLabel;
            return Truncate(Activities.Count == 0 ? name : $"{name} — {string.Join("; ", Activities)}");
        }
    }

    private static CatalogDepartment? Find(IReadOnlyDictionary<string, CatalogDepartment> departmentsByName, string? name) =>
        name is not null && departmentsByName.TryGetValue(name.Trim(), out var department) ? department : null;

    private static bool? ParseYesNo(string? value) => Normalize(value) switch
    {
        "yes" => true,
        "no" => false,
        _ => null
    };

    /// <summary>The workbook's comma-separated list as a JSON array of its own wording; "None"/blank → null.</summary>
    private static string? ToJsonList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Normalize(value) == "none")
        {
            return null;
        }

        var items = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.Length == 0 ? null : JsonSerializer.Serialize(items, ListJsonOptions);
    }

    private static string Normalize(string? value) =>
        value is null ? string.Empty : Regex.Replace(value.Trim(), @"\s+", " ").ToLowerInvariant();

    private static string Truncate(string value) =>
        value.Length <= MaxStepNameLength ? value : value[..(MaxStepNameLength - 1)].TrimEnd() + "…";
}
