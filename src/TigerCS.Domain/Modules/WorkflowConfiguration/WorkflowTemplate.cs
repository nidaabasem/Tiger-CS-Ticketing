namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// One VERSION of a logical <see cref="Workflow"/> — the table that was
/// "WorkflowTemplates" in Workflow/SLA Configuration phase 1, evolved in
/// place (Administration / Workflow Designer phase) rather than duplicated:
/// every pre-versioning template became version 1, Published, of a
/// same-named workflow, and the three seeded patterns (Standard, With
/// Pending, With Approval) still exist exactly as before.
///
/// <para>
/// <b>Immutable after publication.</b> Steps, transitions, capability flags,
/// name and description can only change while <see cref="Status"/> is
/// <see cref="WorkflowVersionStatus.Draft"/>; every mutator calls
/// <see cref="EnsureDraft"/>. A change to a published workflow is always a
/// new version: existing tickets keep the exact version they were pinned to
/// (<c>Tickets.WorkflowTemplateId</c>) and only tickets created after the
/// next publication use the new one.
/// </para>
///
/// <para>
/// <b>A version configures which of the existing lifecycle's transitions and
/// actions are available; it does not define a new lifecycle.</b> The
/// capability flags gate the existing <c>TicketStatus</c> sub-machine
/// (<c>Ticket.ChangeStatus</c>) and the approval concept exactly as in
/// phases 1–3; the ordered steps and their outcome branches are the
/// structured definition the next runtime increment consumes. Publish-time
/// validation (<see cref="WorkflowVersionValidator"/>) guarantees the
/// structure is sound before any ticket can be pinned to it.
/// </para>
/// </summary>
public class WorkflowTemplate
{
    public int WorkflowTemplateId { get; private set; }

    /// <summary>The logical workflow this version belongs to.</summary>
    public int WorkflowId { get; private set; }

    /// <summary>1-based, unique per workflow, never reused.</summary>
    public int VersionNumber { get; private set; }

    public WorkflowVersionStatus Status { get; private set; }

    /// <summary>Stable machine identifier (e.g. "STANDARD", "STANDARD-V2") — seed data and tests reference versions by this, never by generated id.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    /// <summary>Whether flows on this version may use <c>TicketStatus.PendingCustomer</c> at all. A request type further restricts via <see cref="RequestType.AllowPendingCustomer"/> — see <see cref="WorkflowCapabilities.Resolve"/>.</summary>
    public bool AllowsPendingCustomer { get; private set; }

    /// <summary>Whether flows on this version may use <c>TicketStatus.PendingThirdParty</c> (internal department / external party). Same further restriction as <see cref="AllowsPendingCustomer"/>.</summary>
    public bool AllowsPendingInternal { get; private set; }

    /// <summary>Whether this flow carries an approval stage — approval records over the unchanged status machine (phase 3).</summary>
    public bool RequiresApproval { get; private set; }

    /// <summary>Kept from phase 1; a version is retired by becoming Historical, so this is informational only.</summary>
    public bool IsActive { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Null for versions created by the system (seed/backfill) — system actions carry no actor.</summary>
    public Guid? CreatedByEmployeeId { get; private set; }

    public DateTime? PublishedAtUtc { get; private set; }

    /// <summary>Null for versions published by the system (seed/backfill).</summary>
    public Guid? PublishedByEmployeeId { get; private set; }

    private readonly List<WorkflowTemplateStep> _steps = [];

    /// <summary>The step sequence, ordered by <see cref="WorkflowTemplateStep.Sequence"/>.</summary>
    public IReadOnlyList<WorkflowTemplateStep> Steps => _steps.OrderBy(s => s.Sequence).ToList().AsReadOnly();

    public bool IsDraft => Status == WorkflowVersionStatus.Draft;
    public bool IsPublished => Status == WorkflowVersionStatus.Published;
    public bool IsHistorical => Status == WorkflowVersionStatus.Historical;

    private WorkflowTemplate() { }

    /// <summary>Creates a new Draft version. Steps are added afterwards; <see cref="Publish"/> validates the result.</summary>
    public WorkflowTemplate(
        int workflowId,
        int versionNumber,
        string code,
        string name,
        string? description,
        bool allowsPendingCustomer,
        bool allowsPendingInternal,
        bool requiresApproval,
        DateTime createdAtUtc,
        Guid? createdByEmployeeId)
    {
        if (versionNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(versionNumber), "Version numbers start at 1.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code is required.", nameof(code));
        }

        WorkflowId = workflowId;
        VersionNumber = versionNumber;
        Code = code;
        Status = WorkflowVersionStatus.Draft;
        IsActive = true;
        CreatedAtUtc = createdAtUtc;
        CreatedByEmployeeId = createdByEmployeeId;
        UpdateSettings(name, description, allowsPendingCustomer, allowsPendingInternal, requiresApproval);
    }

    /// <summary>Draft-only: name, description and the capability flags.</summary>
    public void UpdateSettings(
        string name, string? description, bool allowsPendingCustomer, bool allowsPendingInternal, bool requiresApproval)
    {
        EnsureDraft();

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        AllowsPendingCustomer = allowsPendingCustomer;
        AllowsPendingInternal = allowsPendingInternal;
        RequiresApproval = requiresApproval;
    }

    /// <summary>
    /// Appends a step at an explicit sequence (phase-1 signature, kept for
    /// seeds and tests). Sequences must strictly increase so the stored order
    /// is the flow order and can never be ambiguous.
    /// </summary>
    public WorkflowTemplateStep AddStep(byte sequence, string name, WorkflowStepKind kind, bool isOptional = false, ApprovalType? approvalType = null)
    {
        EnsureDraft();

        var last = _steps.Count == 0 ? (byte)0 : _steps.Max(s => s.Sequence);
        if (sequence <= last)
        {
            throw new ArgumentException(
                $"Step sequence {sequence} must be greater than the last step's sequence {last}.",
                nameof(sequence));
        }

        var step = new WorkflowTemplateStep(this, sequence, name, kind, isOptional, approvalType);
        _steps.Add(step);
        return step;
    }

    /// <summary>Appends a step after the current last one (the designer's "Add Step").</summary>
    public WorkflowTemplateStep AppendStep(string name, WorkflowStepKind kind, bool isOptional = false, ApprovalType? approvalType = null)
    {
        EnsureDraft();

        var next = _steps.Count == 0 ? 1 : _steps.Max(s => s.Sequence) + 1;
        if (next > byte.MaxValue)
        {
            throw new InvalidOperationException("A workflow version cannot have more than 255 steps.");
        }

        return AddStep((byte)next, name, kind, isOptional, approvalType);
    }

    /// <summary>Draft-only: edits a step's name, kind, optionality and per-kind configuration. Changing the kind away from Approval drops its outcome branches.</summary>
    public WorkflowTemplateStep UpdateStep(int workflowTemplateStepId, string name, WorkflowStepKind kind, bool isOptional, ApprovalType? approvalType)
    {
        EnsureDraft();
        var step = GetStep(workflowTemplateStepId);
        step.Configure(name, kind, isOptional, approvalType);
        return step;
    }

    /// <summary>Draft-only: removes a step, drops every branch pointing at it, and renumbers the remaining steps contiguously from 1.</summary>
    public void RemoveStep(int workflowTemplateStepId)
    {
        EnsureDraft();
        var step = GetStep(workflowTemplateStepId);

        foreach (var other in _steps)
        {
            other.RemoveTransitionsTargeting(step);
        }

        _steps.Remove(step);
        Renumber();
    }

    /// <summary>Draft-only: swaps the step with its predecessor. A no-op for the first step.</summary>
    public void MoveStepUp(int workflowTemplateStepId) => Swap(workflowTemplateStepId, -1);

    /// <summary>Draft-only: swaps the step with its successor. A no-op for the last step.</summary>
    public void MoveStepDown(int workflowTemplateStepId) => Swap(workflowTemplateStepId, +1);

    /// <summary>Draft-only: configures (or, with a null target, clears) where an outcome of a decision step leads. Both steps must belong to this version.</summary>
    public void SetStepTransition(int workflowTemplateStepId, WorkflowStepOutcome outcome, int? targetWorkflowTemplateStepId)
    {
        EnsureDraft();
        var step = GetStep(workflowTemplateStepId);

        if (targetWorkflowTemplateStepId is not { } targetId)
        {
            step.ClearTransition(outcome);
            return;
        }

        var target = GetStep(targetId);
        step.SetTransition(outcome, target);
    }

    /// <summary>
    /// Draft-only: copies every step and outcome branch of another version
    /// (the "Create New Version" action), re-linking branches by position so
    /// the copy is structurally identical but independent.
    /// </summary>
    public void CopyStepsFrom(WorkflowTemplate source)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureDraft();

        if (_steps.Count > 0)
        {
            throw new InvalidOperationException("Steps can only be copied into an empty Draft.");
        }

        var sourceSteps = source.Steps;
        var copies = new Dictionary<WorkflowTemplateStep, WorkflowTemplateStep>();
        foreach (var sourceStep in sourceSteps)
        {
            copies[sourceStep] = AddStep(sourceStep.Sequence, sourceStep.Name, sourceStep.Kind, sourceStep.IsOptional, sourceStep.ApprovalType);
        }

        foreach (var sourceStep in sourceSteps)
        {
            foreach (var transition in sourceStep.Transitions)
            {
                if (transition.TargetStep is { } target && copies.TryGetValue(target, out var targetCopy))
                {
                    copies[sourceStep].SetTransition(transition.Outcome, targetCopy);
                }
            }
        }
    }

    /// <summary>Runs publish validation without changing anything — what the designer shows in its validation summary.</summary>
    public IReadOnlyList<WorkflowValidationIssue> Validate() => WorkflowVersionValidator.Validate(this);

    /// <summary>
    /// Draft → Published. Refuses (<see cref="WorkflowVersionInvalidException"/>)
    /// while any validation error remains. Capability flags are widened to
    /// cover the steps actually present (a Pending Customer step implies
    /// Pending Customer is allowed) so the flags and the designed flow can
    /// never disagree at runtime. Marking the previously published version
    /// Historical is the application service's job (it holds both rows).
    /// </summary>
    public void Publish(DateTime publishedAtUtc, Guid? publishedByEmployeeId)
    {
        EnsureDraft();

        var issues = Validate();
        if (issues.Any(i => i.Severity == WorkflowValidationSeverity.Error))
        {
            throw new WorkflowVersionInvalidException(WorkflowTemplateId, issues);
        }

        AllowsPendingCustomer |= _steps.Any(s => s.Kind == WorkflowStepKind.PendingCustomer);
        AllowsPendingInternal |= _steps.Any(s => s.Kind == WorkflowStepKind.PendingInternal);
        RequiresApproval |= _steps.Any(s => s.Kind == WorkflowStepKind.WaitingForApproval);

        Status = WorkflowVersionStatus.Published;
        PublishedAtUtc = publishedAtUtc;
        PublishedByEmployeeId = publishedByEmployeeId;
    }

    /// <summary>
    /// Stamps a pre-versioning template as version 1, Published, WITHOUT
    /// publish validation — used only by the reference-data seed (and the
    /// equivalent migration backfill) for the phase-1 templates that already
    /// governed tickets before versioning existed. Their shared approval step
    /// carries no approval type by design (the type came from the request
    /// type's approval requirement), which today's stricter Draft validation
    /// would reject; that historical configuration is preserved as-is, never
    /// rewritten. Not reachable from any administration endpoint.
    /// </summary>
    public void PublishAsSeededBaseline(DateTime publishedAtUtc)
    {
        EnsureDraft();
        Status = WorkflowVersionStatus.Published;
        PublishedAtUtc = publishedAtUtc;
        PublishedByEmployeeId = null;
    }

    /// <summary>Published → Historical, when a newer version is published. Read-only either way; the version stays queryable forever.</summary>
    public void MarkHistorical()
    {
        if (Status != WorkflowVersionStatus.Published)
        {
            throw new InvalidOperationException(
                $"Workflow version {WorkflowTemplateId} is {Status}; only a Published version becomes Historical.");
        }

        Status = WorkflowVersionStatus.Historical;
    }

    public WorkflowTemplateStep GetStep(int workflowTemplateStepId) =>
        _steps.FirstOrDefault(s => s.WorkflowTemplateStepId == workflowTemplateStepId)
        ?? throw new WorkflowStepNotFoundException(WorkflowTemplateId, workflowTemplateStepId);

    private void EnsureDraft()
    {
        if (Status != WorkflowVersionStatus.Draft)
        {
            throw new WorkflowVersionNotEditableException(WorkflowTemplateId, Status);
        }
    }

    private void Swap(int workflowTemplateStepId, int direction)
    {
        EnsureDraft();
        var ordered = Steps;
        var index = ordered.ToList().FindIndex(s => s.WorkflowTemplateStepId == workflowTemplateStepId);
        if (index < 0)
        {
            throw new WorkflowStepNotFoundException(WorkflowTemplateId, workflowTemplateStepId);
        }

        var otherIndex = index + direction;
        if (otherIndex < 0 || otherIndex >= ordered.Count)
        {
            return;
        }

        var a = ordered[index];
        var b = ordered[otherIndex];
        var aSequence = a.Sequence;
        a.SetSequence(b.Sequence);
        b.SetSequence(aSequence);
    }

    private void Renumber()
    {
        byte sequence = 1;
        foreach (var step in _steps.OrderBy(s => s.Sequence).ToList())
        {
            step.SetSequence(sequence++);
        }
    }
}
