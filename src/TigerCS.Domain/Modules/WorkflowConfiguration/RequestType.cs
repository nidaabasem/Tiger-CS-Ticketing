using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The operational configuration unit of the Department → Request Type →
/// Workflow → SLA model (Workflow/SLA Configuration phase 1). Each request
/// type belongs to exactly one department, selects one logical
/// <see cref="Workflow"/> (whose currently Published version — a
/// <see cref="WorkflowTemplate"/> — is what new tickets are pinned to), and
/// carries the business flags that gate lifecycle actions for tickets of
/// that type (enforcement is phase 2).
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
    /// <summary>Maximum length of <see cref="Code"/> (e.g. "LCS-TEN-001").</summary>
    public const int CodeMaxLength = 24;

    public int RequestTypeId { get; private set; }
    public int DepartmentId { get; private set; }

    /// <summary>
    /// The business's stable request code from the request-type catalog
    /// workbook (e.g. "CS-GEN-001") — the identity a catalog import keys on,
    /// so re-running it never creates a duplicate. Null for request types
    /// created before the catalog existed (or by hand in Administration);
    /// unique when present.
    /// </summary>
    public string? Code { get; private set; }

    /// <summary>The catalog's grouping label (e.g. "General Inquiries") — display/reporting text only, never a routing key.</summary>
    public string? RequestGroup { get; private set; }

    /// <summary>The catalog's business description of when this request type applies.</summary>
    public string? Description { get; private set; }

    /// <summary>Unique within the department (e.g. "Ticketing System" exists under both Customer Service and Collections).</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// The logical workflow this request type follows (Administration /
    /// Workflow Designer phase; before it, this was a direct template id).
    /// Which VERSION applies is resolved per ticket: new tickets pin the
    /// workflow's currently Published version at creation, existing tickets
    /// keep theirs.
    /// </summary>
    public int WorkflowId { get; private set; }

    /// <summary>The priority a new ticket of this type starts at, from the existing fixed Priorities set — no second priority model.</summary>
    public byte DefaultPriorityId { get; private set; }

    /// <summary>Whether the agent may change the priority away from <see cref="DefaultPriorityId"/> (e.g. raising a NOC for Resale to Urgent).</summary>
    public bool AllowAgentPriorityChange { get; private set; }

    /// <summary>Request-type-level gate on Pending Customer — effective only where the template also allows it (<see cref="WorkflowCapabilities.Resolve"/>).</summary>
    public bool AllowPendingCustomer { get; private set; }

    /// <summary>Request-type-level gate on Pending Internal / Third Party — same combination rule as <see cref="AllowPendingCustomer"/>.</summary>
    /// <summary>
    /// <b>Deprecated — retained for compatibility, consulted by nothing.</b>
    /// The request type's half of the retired <c>PendingThirdParty</c> gate;
    /// see <see cref="WorkflowTemplate.AllowsPendingInternal"/>. Still stored
    /// and round-tripped so no migration and no data loss is involved.
    /// </summary>
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

    /// <summary>
    /// JSON array of the supporting documents the catalog lists for this
    /// request type, as the business worded them (e.g.
    /// <c>["Cheque copy / bank return document"]</c>). Informational
    /// configuration only: attachments are a later increment, so nothing
    /// enforces it yet. Null means none are listed.
    /// </summary>
    public string? RequiredDocumentsJson { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>
    /// Whether tickets of this type run under its configuration at runtime:
    /// the pinned workflow version's steps are tracked and progression is
    /// validated (<see cref="WorkflowProgression"/>), and this type's
    /// <see cref="RequestTypeSlaPolicy"/> rows take precedence over the
    /// per-priority SLA policy. Off for every existing request type — their
    /// behaviour is unchanged until an administrator explicitly enables it,
    /// which is refused while <see cref="ConfiguredRuntimeReadiness"/>
    /// reports any issue.
    /// </summary>
    public bool ConfigurationEnforced { get; private set; }

    private RequestType() { }

    public RequestType(
        int departmentId,
        string name,
        int workflowId,
        byte defaultPriorityId,
        bool allowAgentPriorityChange,
        bool allowPendingCustomer,
        bool allowPendingInternal,
        bool allowReopen,
        string? requiredFieldsJson = null,
        bool isActive = true)
    {
        DepartmentId = departmentId;
        IsActive = isActive;
        Update(name, workflowId, defaultPriorityId, allowAgentPriorityChange, allowPendingCustomer, allowPendingInternal, allowReopen, requiredFieldsJson);
    }

    /// <summary>Administration edit of the configurable fields. The department is deliberately not here — see <see cref="ChangeDepartment"/>.</summary>
    public void Update(
        string name,
        int workflowId,
        byte defaultPriorityId,
        bool allowAgentPriorityChange,
        bool allowPendingCustomer,
        bool allowPendingInternal,
        bool allowReopen,
        string? requiredFieldsJson)
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

        Name = name.Trim();
        WorkflowId = workflowId;
        DefaultPriorityId = defaultPriorityId;
        AllowAgentPriorityChange = allowAgentPriorityChange;
        AllowPendingCustomer = allowPendingCustomer;
        AllowPendingInternal = allowPendingInternal;
        AllowReopen = allowReopen;
        RequiredFieldsJson = string.IsNullOrWhiteSpace(requiredFieldsJson) ? null : requiredFieldsJson;
    }

    /// <summary>
    /// Moves the request type to another responsible department. The
    /// application service only allows this while no ticket references the
    /// request type: a request type that governed tickets historically keeps
    /// its department so those tickets' configuration stays truthful.
    /// </summary>
    public void ChangeDepartment(int departmentId) => DepartmentId = departmentId;

    /// <summary>
    /// Records the catalog identity and descriptive fields. Deliberately
    /// separate from <see cref="Update"/>: an Administration edit never
    /// touches the catalog identity, so a later re-import still recognizes
    /// the row.
    /// </summary>
    public void SetCatalogDetails(string code, string? requestGroup, string? description, string? requiredDocumentsJson)
    {
        AssignCode(code);
        RequestGroup = string.IsNullOrWhiteSpace(requestGroup) ? null : requestGroup.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        RequiredDocumentsJson = string.IsNullOrWhiteSpace(requiredDocumentsJson) ? null : requiredDocumentsJson;
    }

    /// <summary>
    /// Links this request type to its catalog code without changing any of
    /// its configuration — how an import adopts a request type that already
    /// existed under the same department and name. A code, once set, never
    /// changes: it is the identity re-imports key on.
    /// </summary>
    public void AssignCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code is required.", nameof(code));
        }

        var trimmed = code.Trim();
        if (trimmed.Length > CodeMaxLength)
        {
            throw new ArgumentException($"Code must be at most {CodeMaxLength} characters.", nameof(code));
        }

        if (Code is not null && !string.Equals(Code, trimmed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Request type {RequestTypeId} already has code '{Code}'; a catalog code never changes.");
        }

        Code = trimmed;
    }

    /// <summary>Turns runtime enforcement on. The caller has already checked <see cref="ConfiguredRuntimeReadiness"/>.</summary>
    public void EnableConfigurationEnforcement() => ConfigurationEnforced = true;

    /// <summary>Turns runtime enforcement off: tickets of this type go back to the unrestricted pre-existing behaviour.</summary>
    public void DisableConfigurationEnforcement() => ConfigurationEnforced = false;

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
