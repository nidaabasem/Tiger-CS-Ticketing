namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The controlled set of approval types (Workflow/Automation phase 3) —
/// never free text. Only the two the Customer Service SLA document actually
/// supports exist: Collections' Send Receipts depends on Accounting
/// approval, and Handover's post-approval stage begins at Customer Service
/// approval. No further types are invented; new ones are additive enum
/// values with their own <see cref="RequestTypeApprovalRequirement"/>
/// configuration.
/// </summary>
public enum ApprovalType : byte
{
    /// <summary>Accounting's approval that gates Collections / Send Receipts ("depends on Accounting Approval; once received, Collections has 1 day").</summary>
    AccountingApproval = 1,

    /// <summary>Customer Service's approval that gates the Handover process ("Handover approval takes approximately 1–4 days after Customer Service approval").</summary>
    CustomerServiceApproval = 2
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
