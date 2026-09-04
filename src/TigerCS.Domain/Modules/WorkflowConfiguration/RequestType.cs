using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The operational configuration unit of the Department → Request Type →
/// Workflow → SLA model (Workflow/SLA Configuration phase 1). Each request
/// type belongs to exactly one department, selects one reusable
/// <see cref="WorkflowTemplate"/>, and carries the business flags that
/// gate lifecycle actions for tickets of that type (enforcement is phase 2).
///
/// <para>
/// <b>Deliberately distinct from <c>Category</c>.</b> Category remains the
/// intake classification/routing taxonomy (every category routes to one
/// department, FR-CLS-01); RequestType is the workflow/SLA configuration
/// layer. How a ticket acquires its request type (a direct
/// <c>Tickets.RequestTypeId</c>, or a Category → RequestType mapping) is a
/// phase-2 wiring decision recorded in
/// docs/Workflow-SLA-Configuration-Phase1.md — not silently decided here.
/// </para>
///
/// <para>
/// <b>Urgency is priority, not a second request type.</b> "NOC for Resale
/// URGENT" is NOT a request type: it is NOC for Resale at the Urgent
/// priority, with its own <see cref="RequestTypeSlaPolicy"/> row. The
/// Normal/Urgent ↔ Medium/High mapping decision is documented in
/// docs/Workflow-SLA-Configuration-Phase1.md.
/// </para>
/// </summary>
public class RequestType
{
    public int RequestTypeId { get; private set; }
    public int DepartmentId { get; private set; }

    /// <summary>Unique within the department (e.g. "Ticketing System" exists under both Customer Service and Collections).</summary>
    public string Name { get; private set; } = string.Empty;

    public int WorkflowTemplateId { get; private set; }

    /// <summary>The priority a new ticket of this type starts at, from the existing fixed Priorities set — no second priority model.</summary>
    public byte DefaultPriorityId { get; private set; }

    /// <summary>Whether the agent may change the priority away from <see cref="DefaultPriorityId"/> (e.g. raising a NOC for Resale to Urgent).</summary>
    public bool AllowAgentPriorityChange { get; private set; }

    /// <summary>Request-type-level gate on Pending Customer — effective only where the template also allows it (<see cref="WorkflowCapabilities.Resolve"/>).</summary>
    public bool AllowPendingCustomer { get; private set; }

    /// <summary>Request-type-level gate on Pending Internal / Third Party — same combination rule as <see cref="AllowPendingCustomer"/>.</summary>
    public bool AllowPendingInternal { get; private set; }

    /// <summary>
    /// Whether tickets of this type may be reopened at all. When true, the
    /// existing <c>ReopenPolicy</c> (window + role authorization) remains the
    /// final enforcement point — this flag can only remove the capability,
    /// never widen or bypass that policy.
    /// </summary>
    public bool AllowReopen { get; private set; }

    /// <summary>
    /// JSON array of intake field keys required for this request type (e.g.
    /// <c>["UnitNumber","BuyerName"]</c>) — provisional representation until
    /// the required-fields feature is built; null means no extra requirement.
    /// Required attachments are deliberately not modeled yet (attachments are
    /// a later increment).
    /// </summary>
    public string? RequiredFieldsJson { get; private set; }

    public bool IsActive { get; private set; }

    private RequestType() { }

    public RequestType(
        int departmentId,
        string name,
        int workflowTemplateId,
        byte defaultPriorityId,
        bool allowAgentPriorityChange,
        bool allowPendingCustomer,
        bool allowPendingInternal,
        bool allowReopen,
        string? requiredFieldsJson = null,
        bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (!Enum.IsDefined(typeof(PriorityLevel), defaultPriorityId))
        {
            throw new ArgumentException(
                $"DefaultPriorityId {defaultPriorityId} is not one of the fixed priorities.", nameof(defaultPriorityId));
        }

        DepartmentId = departmentId;
        Name = name;
        WorkflowTemplateId = workflowTemplateId;
        DefaultPriorityId = defaultPriorityId;
        AllowAgentPriorityChange = allowAgentPriorityChange;
        AllowPendingCustomer = allowPendingCustomer;
        AllowPendingInternal = allowPendingInternal;
        AllowReopen = allowReopen;
        RequiredFieldsJson = requiredFieldsJson;
        IsActive = isActive;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
