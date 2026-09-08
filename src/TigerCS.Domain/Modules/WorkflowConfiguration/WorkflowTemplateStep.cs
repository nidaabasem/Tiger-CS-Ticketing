namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// One step of a workflow VERSION (<see cref="WorkflowTemplate"/>) — the
/// unit the Workflow Designer adds, edits, reorders and deletes while the
/// version is a Draft, and read-only configuration afterwards. It carries
/// the controlled step kind, the per-kind configuration the kind supports
/// (today: the approval type of an approval step) and, for decision steps,
/// the configured Approved/Rejected outcome branches. Steps never drive the
/// status machine themselves: <see cref="WorkflowStepKind"/>'s remarks
/// explain the mapping onto the existing lifecycle, and dynamic runtime
/// enforcement is the next increment.
/// </summary>
public class WorkflowTemplateStep
{
    public int WorkflowTemplateStepId { get; private set; }
    public int WorkflowTemplateId { get; private set; }

    /// <summary>Display/flow order within the version. Strictly increasing per version (unique index); the designer keeps it contiguous from 1.</summary>
    public byte Sequence { get; private set; }

    /// <summary>Human-readable step name shown in the designer and timeline (e.g. "Accounting Approval") — never a technical event name.</summary>
    public string Name { get; private set; } = string.Empty;

    public WorkflowStepKind Kind { get; private set; }

    /// <summary>True for steps a given ticket may skip entirely (e.g. an optional Pending Customer step).</summary>
    public bool IsOptional { get; private set; }

    /// <summary>
    /// For <see cref="WorkflowStepKind.WaitingForApproval"/>: which controlled
    /// approval this step waits for. Null only on pre-versioning (legacy) steps
    /// whose shared template served several request types; publish validation
    /// requires it on every new Draft.
    /// </summary>
    public ApprovalType? ApprovalType { get; private set; }

    private readonly List<WorkflowStepTransition> _transitions = [];

    /// <summary>The configured outcome branches (decision steps only); an outcome with no branch falls through to the next step in sequence (Approved) or stays explicitly open (Rejected, per the phase-3 decision).</summary>
    public IReadOnlyList<WorkflowStepTransition> Transitions => _transitions.AsReadOnly();

    /// <summary>Navigation back to the owning version — set by the version's own step methods, never independently.</summary>
    public WorkflowTemplate? WorkflowTemplate { get; private set; }

    private WorkflowTemplateStep() { }

    internal WorkflowTemplateStep(
        WorkflowTemplate template, byte sequence, string name, WorkflowStepKind kind, bool isOptional, ApprovalType? approvalType)
    {
        WorkflowTemplate = template;
        Sequence = sequence;
        Configure(name, kind, isOptional, approvalType);
        if (sequence == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence must be positive.");
        }
    }

    internal void Configure(string name, WorkflowStepKind kind, bool isOptional, ApprovalType? approvalType)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (!WorkflowStepKinds.IsSupported(kind))
        {
            throw new ArgumentException($"Kind {kind} is not a supported workflow step kind.", nameof(kind));
        }

        if (approvalType is { } type && !Enum.IsDefined(type))
        {
            throw new ArgumentException($"ApprovalType {type} is not a defined approval type.", nameof(approvalType));
        }

        var info = WorkflowStepKinds.Describe(kind);
        if (approvalType is not null && !info.RequiresApprovalType)
        {
            throw new WorkflowStepConfigurationException(
                $"Step '{name}' is a {info.Label} step and cannot carry an approval type — only Approval steps do.");
        }

        Name = name.Trim();
        Kind = kind;
        IsOptional = isOptional;
        ApprovalType = approvalType;

        if (!info.SupportsOutcomeBranches)
        {
            _transitions.Clear();
        }
    }

    internal void SetSequence(byte sequence) => Sequence = sequence;

    internal WorkflowStepTransition? FindTransition(WorkflowStepOutcome outcome) =>
        _transitions.FirstOrDefault(t => t.Outcome == outcome);

    internal void SetTransition(WorkflowStepOutcome outcome, WorkflowTemplateStep target)
    {
        var info = WorkflowStepKinds.Describe(Kind);
        if (!info.SupportsOutcomeBranches)
        {
            throw new WorkflowStepConfigurationException(
                $"Step '{Name}' is a {info.Label} step and has no {outcome} outcome to configure — only Approval steps branch.");
        }

        var existing = FindTransition(outcome);
        if (existing is null)
        {
            _transitions.Add(new WorkflowStepTransition(this, outcome, target));
        }
        else
        {
            existing.Retarget(target);
        }
    }

    internal void ClearTransition(WorkflowStepOutcome outcome)
    {
        var existing = FindTransition(outcome);
        if (existing is not null)
        {
            _transitions.Remove(existing);
        }
    }

    internal void RemoveTransitionsTargeting(WorkflowTemplateStep target) =>
        _transitions.RemoveAll(t => ReferenceEquals(t.TargetStep, target));
}
