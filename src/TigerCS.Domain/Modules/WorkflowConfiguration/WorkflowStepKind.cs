namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The controlled step kinds a <see cref="WorkflowTemplateStep"/> can carry.
///
/// <para>
/// <b>Deliberately not a second <c>TicketStatus</c>.</b> The values that
/// correspond to lifecycle states (<see cref="Created"/>, <see cref="InProgress"/>,
/// <see cref="PendingCustomer"/>, <see cref="PendingInternal"/>,
/// <see cref="Resolved"/>, <see cref="Closed"/>) describe which existing
/// <c>TicketStatus</c>/lifecycle event a step maps onto; the ones that do
/// not (<see cref="Assigned"/> — the assignment dimension;
/// <see cref="Review"/>/<see cref="WaitingForApproval"/> — the approval
/// concept; <see cref="Prerequisite"/>/<see cref="MaintenanceDependency"/> —
/// the phase-3 typed <c>TicketWorkflowEvents</c>) are workflow-step concepts
/// realized as assignment/approval/pending/event records over the unchanged
/// status machine. Nothing in this enum is written to
/// <c>Tickets.TicketStatus</c>, and the Workflow Designer offers exactly
/// this closed set — never a free-text or scripted step.
/// </para>
/// </summary>
public enum WorkflowStepKind : byte
{
    /// <summary>Ticket created (the Start step) — maps to <c>TicketStatus.Open</c>.</summary>
    Created = 1,

    /// <summary>Assignment to a department queue / employee — the assignment dimension, governed by the existing assignment rules, not a status value.</summary>
    Assigned = 2,

    /// <summary>The responsible team reviews the request before work/approval — an approval-flow concept (Phase 3), not a status value.</summary>
    Review = 3,

    /// <summary>Waiting on a configured approver — an approval-record concept (Phase 3), not a status value. Carries a controlled <see cref="ApprovalType"/> and Approved/Rejected outcome branches.</summary>
    WaitingForApproval = 4,

    /// <summary>Active work by the responsible department — maps to <c>TicketStatus.InProgress</c>.</summary>
    InProgress = 5,

    /// <summary>Waiting on the customer (payment, documents, response) — maps to <c>TicketStatus.PendingCustomer</c> with a structured pending record.</summary>
    PendingCustomer = 6,

    /// <summary>
    /// <b>Legacy only — not offered to the Workflow Designer.</b> Waiting on
    /// another internal department or an external party; it maps to
    /// <c>TicketStatus.PendingThirdParty</c>, which the approved lifecycle
    /// cleanup retired to a legacy-readable status. The value stays at 7 and
    /// <see cref="WorkflowStepKinds.All"/> still describes it, so published
    /// versions that already carry such a step keep validating and rendering;
    /// <see cref="WorkflowStepKinds.Selectable"/> no longer offers it, so no
    /// new version can add one.
    /// </summary>
    PendingInternal = 7,

    /// <summary>The responsible team considers its work completed — maps to <c>TicketStatus.Resolved</c>.</summary>
    Resolved = 8,

    /// <summary>Customer Service considers the case completed — maps to <c>TicketStatus.Closed</c>. Always the last step.</summary>
    Closed = 9,

    /// <summary>Prerequisites must be satisfied before work proceeds (Registration / Register Unit) — anchored to the phase-3 <c>PrerequisitesCompleted</c> typed event; no duration or approval is invented.</summary>
    Prerequisite = 10,

    /// <summary>A maintenance dependency that may or may not be required (Handover) — anchored to the phase-3 <c>MaintenanceRequired</c> / <c>MaintenanceNotRequired</c> / <c>MaintenanceCompleted</c> typed events.</summary>
    MaintenanceDependency = 11
}

/// <summary>
/// Management-facing descriptions of every supported step kind — the single
/// catalog the Workflow Designer, its validation messages and the API's
/// step-type list all read, so a kind can never be offered in one place and
/// rejected in another.
/// </summary>
/// <param name="Kind">The enum value.</param>
/// <param name="Label">Short name shown in the designer ("Approval").</param>
/// <param name="Description">One-sentence explanation for Customer Service management.</param>
/// <param name="RequiresApprovalType">True when the step must carry a controlled <see cref="ApprovalType"/>.</param>
/// <param name="SupportsOutcomeBranches">True when the step may configure Approved/Rejected targets.</param>
/// <param name="IsStart">True for the mandatory first step.</param>
/// <param name="IsTerminal">True for the mandatory last step.</param>
public sealed record WorkflowStepKindInfo(
    WorkflowStepKind Kind,
    string Label,
    string Description,
    bool RequiresApprovalType,
    bool SupportsOutcomeBranches,
    bool IsStart,
    bool IsTerminal);

public static class WorkflowStepKinds
{
    public static readonly IReadOnlyList<WorkflowStepKindInfo> All =
    [
        new(WorkflowStepKind.Created, "Start", "The ticket is created and enters the workflow.", false, false, IsStart: true, IsTerminal: false),
        new(WorkflowStepKind.Assigned, "Department Queue / Assignment", "The ticket is routed to the responsible department's queue and assigned according to the request type's assignment configuration.", false, false, false, false),
        new(WorkflowStepKind.Review, "Review", "The responsible team reviews the request before work or approval.", false, false, false, false),
        new(WorkflowStepKind.WaitingForApproval, "Approval", "Work waits for a controlled approval decision (Accounting or Customer Service). Approved and Rejected can each lead to a configured step.", true, true, false, false),
        new(WorkflowStepKind.InProgress, "Department Work", "The responsible department works the request.", false, false, false, false),
        new(WorkflowStepKind.PendingCustomer, "Pending Customer", "Work waits on the customer (payment, documents, a response).", false, false, false, false),
        new(WorkflowStepKind.PendingInternal, "Pending Internal / Third Party", "Work waits on another internal department or an external party.", false, false, false, false),
        new(WorkflowStepKind.Prerequisite, "Prerequisite", "Work proceeds once prerequisites are recorded as completed.", false, false, false, false),
        new(WorkflowStepKind.MaintenanceDependency, "Maintenance Dependency", "Maintenance may be required before the request can complete; its outcome is recorded as a workflow event.", false, false, false, false),
        new(WorkflowStepKind.Resolved, "Resolve", "The responsible team marks its work completed.", false, false, false, false),
        new(WorkflowStepKind.Closed, "Close", "Customer Service closes the case. Always the final step.", false, false, IsStart: false, IsTerminal: true)
    ];

    /// <summary>
    /// The kinds the Workflow Designer offers when building a version — every
    /// supported kind except the legacy-only ones.
    ///
    /// <para>
    /// <see cref="All"/> deliberately stays complete: an existing version that
    /// already carries a legacy step must still validate, publish and render
    /// its own step list, so <see cref="Describe"/> and
    /// <see cref="IsSupported"/> keep answering for it. Only what is offered
    /// for a <i>new</i> step narrows.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<WorkflowStepKindInfo> Selectable =
        [.. All.Where(i => !IsLegacyOnly(i.Kind))];

    /// <summary>
    /// True for a step kind kept only so historical workflow versions remain
    /// readable. <see cref="WorkflowStepKind.PendingInternal"/> is one because
    /// the <c>TicketStatus.PendingThirdParty</c> it maps onto is.
    /// </summary>
    public static bool IsLegacyOnly(WorkflowStepKind kind) => kind is WorkflowStepKind.PendingInternal;

    private static readonly Dictionary<WorkflowStepKind, WorkflowStepKindInfo> ByKind = All.ToDictionary(i => i.Kind);

    public static WorkflowStepKindInfo Describe(WorkflowStepKind kind) =>
        ByKind.TryGetValue(kind, out var info)
            ? info
            : throw new ArgumentException($"Kind {kind} is not a supported workflow step kind.", nameof(kind));

    public static bool IsSupported(WorkflowStepKind kind) => ByKind.ContainsKey(kind);
}
