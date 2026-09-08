namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The LOGICAL workflow an administrator names and a <see cref="RequestType"/>
/// selects (Administration / Workflow Designer phase) — e.g. "Send Receipts
/// Workflow". It carries identity only; the actual step sequence lives in
/// its numbered, immutable-once-published versions
/// (<see cref="WorkflowTemplate"/> rows sharing this <see cref="WorkflowId"/>).
///
/// <para>
/// At most one version of a workflow is <see cref="WorkflowVersionStatus.Published"/>
/// at a time (enforced by a filtered unique index): that is the version new
/// tickets are pinned to. Earlier published versions become
/// <see cref="WorkflowVersionStatus.Historical"/> and remain readable forever,
/// because tickets created under them keep referencing them. Nothing here is
/// ever physically deleted once referenced — deactivation is the only
/// retirement mechanism.
/// </para>
/// </summary>
public class Workflow
{
    public const int CodeMaxLength = 24;

    public int WorkflowId { get; private set; }

    /// <summary>Stable machine identifier (e.g. "STANDARD") — seeds and version codes derive from it; never shown to management as the workflow's name.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private Workflow() { }

    public Workflow(string code, string name, string? description, DateTime createdAtUtc, bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code is required.", nameof(code));
        }

        if (code.Length > CodeMaxLength)
        {
            throw new ArgumentException($"Code must be at most {CodeMaxLength} characters.", nameof(code));
        }

        Code = code;
        Rename(name, description);
        CreatedAtUtc = createdAtUtc;
        IsActive = isActive;
    }

    public void Rename(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    /// <summary>
    /// Derives a stable code from a management-facing name ("Send Receipts
    /// Workflow" → "SEND-RECEIPTS-WORKFLOW"), truncated to
    /// <see cref="CodeMaxLength"/>. Uniqueness is the caller's job (it appends
    /// a numeric suffix on collision).
    /// </summary>
    public static string CodeFromName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var chars = name.ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("--", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("--", "-", StringComparison.Ordinal);
        }

        collapsed = collapsed.Trim('-');
        if (collapsed.Length == 0)
        {
            collapsed = "WORKFLOW";
        }

        return collapsed.Length > CodeMaxLength ? collapsed[..CodeMaxLength].TrimEnd('-') : collapsed;
    }
}
