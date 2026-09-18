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
