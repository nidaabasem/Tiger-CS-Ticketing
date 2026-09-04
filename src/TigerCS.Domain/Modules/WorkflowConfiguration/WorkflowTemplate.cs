namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// A reusable workflow pattern a <see cref="RequestType"/> selects — never a
/// per-request-type hard-coded flow. Three are seeded initially (Workflow/SLA
/// Configuration phase 1): Standard (A), With Pending (B), With Approval (C).
///
/// <para>
/// <b>A template configures which of the existing lifecycle's transitions and
/// actions are available; it does not define a new lifecycle.</b> The
/// capability flags below gate the existing <c>TicketStatus</c> sub-machine
/// (<c>Ticket.ChangeStatus</c>) and the approval concept: a template with
/// <see cref="AllowsPendingCustomer"/> false makes
/// <c>InProgress → PendingCustomer</c> unavailable for request types using
/// it (enforcement is phase 2); <see cref="RequiresApproval"/> marks the flow
/// as carrying an approval stage realized as approval records (phase 3), not
/// as new <c>TicketStatus</c> values. The ordered
/// <see cref="WorkflowTemplateStep"/> rows carry the same flow as displayable
/// configuration for the workflow timeline/current-step UI.
/// </para>
/// </summary>
public class WorkflowTemplate
{
    public int WorkflowTemplateId { get; private set; }

    /// <summary>Stable machine identifier (e.g. "STANDARD") — seed data and tests reference templates by this, never by generated id.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    /// <summary>Whether flows on this template may use <c>TicketStatus.PendingCustomer</c> at all. A request type further restricts via <see cref="RequestType.AllowPendingCustomer"/> — see <see cref="WorkflowCapabilities.Resolve"/>.</summary>
    public bool AllowsPendingCustomer { get; private set; }

    /// <summary>Whether flows on this template may use <c>TicketStatus.PendingThirdParty</c> (internal department / external party). Same further restriction as <see cref="AllowsPendingCustomer"/>.</summary>
    public bool AllowsPendingInternal { get; private set; }

    /// <summary>Whether this flow carries an approval stage (e.g. Accounting approval for Send Receipts, Customer Service approval for Handover) — approval records over the unchanged status machine, phase 3.</summary>
    public bool RequiresApproval { get; private set; }

    public bool IsActive { get; private set; }

    private readonly List<WorkflowTemplateStep> _steps = [];

    /// <summary>The displayable step sequence, ordered by <see cref="WorkflowTemplateStep.Sequence"/>.</summary>
    public IReadOnlyList<WorkflowTemplateStep> Steps => _steps.AsReadOnly();

    private WorkflowTemplate() { }

    public WorkflowTemplate(
        string code,
        string name,
        string? description,
        bool allowsPendingCustomer,
        bool allowsPendingInternal,
        bool requiresApproval,
        bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        Code = code;
        Name = name;
        Description = description;
        AllowsPendingCustomer = allowsPendingCustomer;
        AllowsPendingInternal = allowsPendingInternal;
        RequiresApproval = requiresApproval;
        IsActive = isActive;
    }

    /// <summary>
    /// Appends the next step of the flow. Sequences must strictly increase so
    /// the stored order is the display order and can never be ambiguous.
    /// </summary>
    public WorkflowTemplateStep AddStep(byte sequence, string name, WorkflowStepKind kind, bool isOptional = false)
    {
        if (_steps.Count > 0 && sequence <= _steps[^1].Sequence)
        {
            throw new ArgumentException(
                $"Step sequence {sequence} must be greater than the last step's sequence {_steps[^1].Sequence}.",
                nameof(sequence));
        }

        var step = new WorkflowTemplateStep(this, sequence, name, kind, isOptional);
        _steps.Add(step);
        return step;
    }
}
