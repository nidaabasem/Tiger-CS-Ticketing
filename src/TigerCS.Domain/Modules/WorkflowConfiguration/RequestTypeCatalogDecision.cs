namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// One open business question recorded against a request type by the
/// request-type catalog import (e.g. "Confirm the handoff to Accounting").
/// While any is unresolved the request type cannot be activated and runtime
/// enforcement cannot be enabled. Resolving one records the business's
/// answer and who entered it; it never changes configuration by itself —
/// the administrator still makes the configuration match the answer.
/// </summary>
public class RequestTypeCatalogDecision
{
    public const int AreaMaxLength = 40;
    public const int TextMaxLength = 1000;

    public int RequestTypeCatalogDecisionId { get; private set; }
    public int RequestTypeId { get; private set; }

    /// <summary>What the question is about (Handoff, Approval, Sla, …) — the importer's decision area name.</summary>
    public string Area { get; private set; } = string.Empty;

    public string Question { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? ResolvedAtUtc { get; private set; }
    public Guid? ResolvedByEmployeeId { get; private set; }

    /// <summary>The business's answer as entered when resolving.</summary>
    public string? Resolution { get; private set; }

    public bool IsResolved => ResolvedAtUtc is not null;

    private RequestTypeCatalogDecision() { }

    public RequestTypeCatalogDecision(int requestTypeId, string area, string question, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(area))
        {
            throw new ArgumentException("Area is required.", nameof(area));
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException("Question is required.", nameof(question));
        }

        RequestTypeId = requestTypeId;
        Area = Clip(area.Trim(), AreaMaxLength);
        Question = Clip(question.Trim(), TextMaxLength);
        CreatedAtUtc = createdAtUtc;
    }

    public void Resolve(string resolution, Guid resolvedByEmployeeId, DateTime resolvedAtUtc)
    {
        if (IsResolved)
        {
            throw new InvalidOperationException($"Catalog decision {RequestTypeCatalogDecisionId} is already resolved.");
        }

        if (string.IsNullOrWhiteSpace(resolution))
        {
            throw new ArgumentException("The business's answer is required to resolve a decision.", nameof(resolution));
        }

        Resolution = Clip(resolution.Trim(), TextMaxLength);
        ResolvedByEmployeeId = resolvedByEmployeeId;
        ResolvedAtUtc = resolvedAtUtc;
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
