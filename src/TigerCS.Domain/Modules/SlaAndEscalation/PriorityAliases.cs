namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// The business vocabulary for priorities. The SLA document says "Normal";
/// the system's fixed priority set is Critical/High/Medium/Low, and the
/// documented mapping (<c>WorkflowReferenceData.NormalUrgencyPriority</c>) is
/// that Normal <i>is</i> the Medium tier. Resolving "Normal" through this
/// table — by <see cref="PriorityLevel"/>, never a numeric id — keeps the
/// mapping in one place and lets the Priorities table decide the id.
/// </summary>
public static class PriorityAliases
{
    private static readonly Dictionary<string, PriorityLevel> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Normal"] = PriorityLevel.Medium
    };

    /// <summary>The priority level a business name stands for, when it is an alias rather than a priority's own name.</summary>
    public static bool TryResolve(string? name, out PriorityLevel level)
    {
        level = default;
        return !string.IsNullOrWhiteSpace(name) && Aliases.TryGetValue(name.Trim(), out level);
    }
}
