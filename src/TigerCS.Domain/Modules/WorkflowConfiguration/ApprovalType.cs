namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The controlled set of approval types (Workflow/Automation phase 3) —
/// never free text. The first two are the ones the Customer Service SLA
/// document supports: Collections' Send Receipts depends on Accounting
/// approval, and Handover's post-approval stage begins at Customer Service
/// approval. No types are invented; each new one is an additive enum value
/// with its own <see cref="RequestTypeApprovalRequirement"/> configuration —
/// which is exactly how <see cref="ReopenApproval"/> was added.
/// </summary>
public enum ApprovalType : byte
{
    /// <summary>Accounting's approval that gates Collections / Send Receipts ("depends on Accounting Approval; once received, Collections has 1 day").</summary>
    AccountingApproval = 1,

    /// <summary>Customer Service's approval that gates the Handover process ("Handover approval takes approximately 1–4 days after Customer Service approval").</summary>
    CustomerServiceApproval = 2,

    /// <summary>
    /// A request to reopen a Closed ticket, raised by someone who does
    /// <b>not</b> hold direct Reopen (<c>TicketRoleSets.Reopen</c> is
    /// CS Agent/Supervisor/Manager, plus System Administrator through
    /// ADR-0024). It is a <b>request</b>, never the reopen itself: granting
    /// it authorizes the ask and changes nothing about the ticket, which
    /// stays Closed until an authorized CS user performs the existing
    /// <c>TicketLifecycleAppService.ReopenAsync</c> with a target department
    /// and a current RowVersion. Every Reopen rule is therefore still
    /// evaluated at execution time, never inherited from the approval.
    ///
    /// <para>
    /// This is the one approval type requestable on a <b>Closed</b> ticket —
    /// the others are refused there, and must stay refused. The carve-out is
    /// keyed on this value in <c>TicketApprovalAppService</c>, and is
    /// additionally gated on the ticket being genuinely reopenable
    /// (<c>ReopenEligibilityService</c>), so a request can never be raised
    /// for a reopen that could not happen.
    /// </para>
    /// </summary>
    ReopenApproval = 3
}

/// <summary>
/// Where each approval type may legitimately be used. One policy, read by
/// every surface that offers or accepts an approval type, so the Workflow
/// Designer and the domain can never disagree about what a workflow step may
/// carry.
///
/// <para>
/// <b>Why this exists rather than a list typed into Razor.</b> Two different
/// questions were being answered by the same enum: "which approvals can a
/// request type require?" (all of them) and "which approvals can a
/// <see cref="WorkflowStepKind.WaitingForApproval"/> step represent?" (only
/// the ones that are a stage of the ticket's forward flow). Naming the
/// eligible types in a view would freeze the answer in the UI and drift the
/// moment a type is added; expressing it as a rule here keeps the two
/// questions distinguishable and the answer in one place.
/// </para>
/// </summary>
public static class ApprovalTypeRules
{
    /// <summary>
    /// Whether <paramref name="type"/> may be attached to a
    /// <see cref="WorkflowStepKind.WaitingForApproval"/> step.
    ///
    /// <para>
    /// <see cref="ApprovalType.ReopenApproval"/> is the one exclusion, and it
    /// is excluded by what it is rather than by name-checking a list: it is a
    /// <b>post-closure action request</b>, raised against a ticket that has
    /// already reached the workflow's terminal step, so it can never be a
    /// stage the ticket passes through on its way there. A designer offering
    /// it would be offering a step that no ticket can ever occupy.
    /// </para>
    ///
    /// <para>
    /// This is an eligibility rule for <i>workflow steps only</i>. It says
    /// nothing about <see cref="RequestTypeApprovalRequirement"/>, which is
    /// the runtime configuration mechanism and accepts every defined type —
    /// including ReopenApproval, which is configured exactly that way.
    /// </para>
    /// </summary>
    public static bool IsWorkflowStepEligible(ApprovalType type) => type is not ApprovalType.ReopenApproval;

    /// <summary>
    /// The approval types a workflow step may carry, in enum order — the
    /// Workflow Designer's dropdown, derived rather than transcribed, so a
    /// type added later appears (or is excluded) by the rule above alone.
    /// </summary>
    public static IReadOnlyList<ApprovalType> WorkflowStepEligible { get; } =
        [.. Enum.GetValues<ApprovalType>().Where(IsWorkflowStepEligible)];
}

/// <summary>
/// How an approval's authorized approver is expressed — configuration,
/// never a hard-coded employee name. Deliberately covers the three shapes
/// the business may settle on, so Accounting's still-provisional status
/// (full department? approval role? external/internal provider?) can be
/// resolved later by re-pointing configuration, not by redesigning the
/// model: an external provider would arrive as a new kind, additively.
/// </summary>
public enum ApprovalTargetKind : byte
{
    /// <summary>An active member of the configured department decides (optionally narrowed further by a role name on the requirement).</summary>
    Department = 1,

    /// <summary>Any holder of the configured role decides.</summary>
    Role = 2,

    /// <summary>One explicitly configured employee decides.</summary>
    Employee = 3
}
