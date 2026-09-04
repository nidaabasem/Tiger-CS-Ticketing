namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// One displayable step of a <see cref="WorkflowTemplate"/>'s flow —
/// configuration for the workflow timeline / "current workflow step" UI and,
/// for <see cref="WorkflowStepKind.Review"/>/<see cref="WorkflowStepKind.WaitingForApproval"/>
/// steps, the anchor the phase-3 approval records attach to. Steps never
/// drive the status machine themselves: <see cref="WorkflowStepKind"/>'s
/// remarks explain the mapping onto the existing lifecycle.
/// </summary>
public class WorkflowTemplateStep
{
    public int WorkflowTemplateStepId { get; private set; }
    public int WorkflowTemplateId { get; private set; }

    /// <summary>Display order within the template. Strictly increasing per template (enforced by <see cref="WorkflowTemplate.AddStep"/> and a unique index).</summary>
    public byte Sequence { get; private set; }

    /// <summary>Human-readable step name shown in the timeline (e.g. "Waiting for Accounting Approval") — never a technical event name.</summary>
    public string Name { get; private set; } = string.Empty;

    public WorkflowStepKind Kind { get; private set; }

    /// <summary>True for steps a given ticket may skip entirely (e.g. the optional Pending Customer step of the With Pending template).</summary>
    public bool IsOptional { get; private set; }

    private WorkflowTemplateStep() { }

    internal WorkflowTemplateStep(WorkflowTemplate template, byte sequence, string name, WorkflowStepKind kind, bool isOptional)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException($"Kind {kind} is not a defined workflow step kind.", nameof(kind));
        }

        if (sequence == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence must be positive.");
        }

        WorkflowTemplate = template;
        Sequence = sequence;
        Name = name;
        Kind = kind;
        IsOptional = isOptional;
    }

    /// <summary>Navigation back to the owning template — set by the template's own <c>AddStep</c>, never independently.</summary>
    public WorkflowTemplate? WorkflowTemplate { get; private set; }
}
