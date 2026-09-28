using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// Whether a request type's configuration is complete enough to run: the
/// single rule set consulted before runtime enforcement is enabled, before an
/// enforced type is activated, and before a new version of an enforced
/// type's workflow is published. Every issue is something only a business
/// decision or an administrator's configuration can resolve — nothing here
/// fills a gap with a default.
/// </summary>
public static class ConfiguredRuntimeReadiness
{
    public static IReadOnlyList<string> Evaluate(
        RequestType requestType,
        WorkflowTemplate? publishedVersion,
        IReadOnlyCollection<RequestTypeSlaPolicy> slaPolicies,
        IReadOnlyCollection<RequestTypeApprovalRequirement> approvalRequirements,
        int unresolvedCatalogDecisions,
        Func<int, bool> isActiveDepartment)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(slaPolicies);
        ArgumentNullException.ThrowIfNull(approvalRequirements);
        ArgumentNullException.ThrowIfNull(isActiveDepartment);

        var issues = new List<string>();

        if (unresolvedCatalogDecisions > 0)
        {
            issues.Add($"{unresolvedCatalogDecisions} catalog decision(s) are still unresolved.");
        }

        if (publishedVersion is null)
        {
            issues.Add("The workflow has no published version.");
        }
        else
        {
            issues.AddRange(EvaluateVersion(publishedVersion, requestType.DepartmentId, isActiveDepartment));
        }

        // ---- approvals ---------------------------------------------------------
        // Reopen Approval is a request raised on a Closed ticket, outside the
        // forward flow, and keeps working exactly as before. Any other active
        // approval would gate work the tracked flow has no stage for.
        foreach (var approval in approvalRequirements.Where(a => a.IsActive && a.ApprovalType != ApprovalType.ReopenApproval))
        {
            issues.Add(
                $"An active {approval.ApprovalType} requirement exists, but an enforced workflow has no approval stage to sequence it. "
                + "Resolve the approval condition (remove or deactivate the requirement) before enabling.");
        }

        // ---- SLA -------------------------------------------------------------
        var active = slaPolicies.Where(p => p.IsActive).ToList();
        var defaultRow = active.FirstOrDefault(p => p.PriorityId == requestType.DefaultPriorityId);
        if (defaultRow is null)
        {
            issues.Add($"No active SLA is configured for the default priority ({(PriorityLevel)requestType.DefaultPriorityId}).");
        }
        else
        {
            if (defaultRow.FirstResponseTargetValue is null)
            {
                issues.Add($"The {(PriorityLevel)requestType.DefaultPriorityId} SLA has no first-response target.");
            }

            if (defaultRow.ResolutionTargetValue is null)
            {
                issues.Add($"The {(PriorityLevel)requestType.DefaultPriorityId} SLA has no resolution target.");
            }
        }

        foreach (var row in active)
        {
            issues.AddRange(SlaRowIssues(row));
        }

        return issues;
    }

    /// <summary>The structural rules a workflow version must meet to be tracked — also applied when publishing a new version of an enforced type's workflow.</summary>
    public static IReadOnlyList<string> EvaluateVersion(WorkflowTemplate version, int requestTypeDepartmentId, Func<int, bool> isActiveDepartment)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(isActiveDepartment);

        var issues = new List<string>();
        foreach (var step in version.Steps)
        {
            if (!WorkflowProgression.SupportedKinds.Contains(step.Kind))
            {
                issues.Add($"Step '{step.Name}' is a {WorkflowStepKinds.Describe(step.Kind).Label} step, which an enforced workflow cannot track.");
            }
            else if (step.Kind == WorkflowStepKind.PendingCustomer && !step.IsOptional)
            {
                issues.Add($"Pending Customer step '{step.Name}' must be optional — waiting on the customer is never a mandatory stage.");
            }

            if (step.Kind == WorkflowStepKind.Assigned)
            {
                if (step.DepartmentId is not { } departmentId)
                {
                    issues.Add($"Step '{step.Name}' does not name its department (an unconfirmed handoff).");
                }
                else if (!isActiveDepartment(departmentId))
                {
                    issues.Add($"Step '{step.Name}' names a department that does not exist or is inactive.");
                }
            }
        }

        var entry = WorkflowProgression.Entry(version, requestTypeDepartmentId);
        if (entry is null || version.Steps.Count < 2 || !ReferenceEquals(version.Steps[1], entry))
        {
            issues.Add("The step after Start must be the Department Queue step of the request type's own department.");
        }

        return issues;
    }

    /// <summary>Why one SLA row cannot be applied at runtime — empty when it can.</summary>
    public static IReadOnlyList<string> SlaRowIssues(RequestTypeSlaPolicy row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var priority = (PriorityLevel)row.PriorityId;
        var issues = new List<string>();
        if (row.Trigger != SlaTriggerType.TicketCreated)
        {
            issues.Add($"The {priority} SLA starts at {row.Trigger}; only TicketCreated is applied at runtime (the established clock start).");
        }

        if (row.IsImmediate)
        {
            issues.Add($"The {priority} SLA is 'Immediately', which is not a duration.");
        }

        if (row.ClockBasis is null)
        {
            issues.Add($"The {priority} SLA's clock basis (business hours or 24/7) is not decided.");
        }

        if (row.FirstResponseMaximumValue is not null || row.ResolutionMaximumValue is not null)
        {
            issues.Add($"The {priority} SLA is a range; which bound is the deadline is not decided.");
        }

        if (row.ClockBasis == SlaClockBasis.BusinessHours
            && ((row.ResolutionTargetValue is not null && row.Unit == SlaDurationUnit.Days)
                || (row.FirstResponseTargetValue is not null && row.EffectiveFirstResponseUnit == SlaDurationUnit.Days)))
        {
            issues.Add($"The {priority} SLA is in business days, and what a business day means is not confirmed "
                + "(for example: a full working-day window of business time, or by the end of the Nth working day). "
                + BusinessDayDecision);
        }

        return issues;
    }

    /// <summary>The open business decision every day-based business-hours SLA waits on.</summary>
    public const string BusinessDayDecision =
        "Day-based business-hours SLAs are stored but not applied until the business confirms the meaning of a business day.";

    /// <summary>
    /// A configured duration in minutes of the clock it runs on: minutes and
    /// hours are business minutes of whatever calendar is configured (the
    /// calculator walks its own working days, window and holidays — nothing
    /// here assumes a window length or a work week); with 24/7 a day is 24
    /// hours. A day on the BusinessHours basis is deliberately NOT converted:
    /// its meaning is an open business decision (see <see cref="SlaRowIssues"/>),
    /// so such a row never reaches this method at runtime.
    /// </summary>
    public static int ToMinutes(int value, SlaDurationUnit unit, SlaClockBasis basis) => (unit, basis) switch
    {
        (SlaDurationUnit.Minutes, _) => value,
        (SlaDurationUnit.Hours, _) => checked(value * 60),
        (SlaDurationUnit.Days, SlaClockBasis.TwentyFourSeven) => checked(value * 1440),
        (SlaDurationUnit.Days, _) => throw new InvalidOperationException(BusinessDayDecision),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown SLA duration unit.")
    };
}
