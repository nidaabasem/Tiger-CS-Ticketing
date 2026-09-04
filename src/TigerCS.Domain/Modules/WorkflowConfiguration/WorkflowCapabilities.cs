namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The effective lifecycle capabilities for tickets of one request type —
/// the template's capability flags combined with the request type's own,
/// each gate an AND (a request type can only narrow what its template
/// allows, never widen it). This is the single place the combination rule
/// lives, so phase-2 transition enforcement and the Ticket Details action
/// list can never disagree on it.
/// </summary>
/// <param name="CanGoPendingCustomer">Whether <c>InProgress → PendingCustomer</c> is available.</param>
/// <param name="CanGoPendingInternal">Whether <c>InProgress → PendingThirdParty</c> is available.</param>
/// <param name="RequiresApproval">Whether the flow carries an approval stage (phase 3 approval records).</param>
/// <param name="CanReopen">Whether Reopen is available at all — when true, the existing <c>ReopenPolicy</c> still decides each actual reopen.</param>
/// <param name="CanChangePriority">Whether the agent may change priority away from the request type's default.</param>
public readonly record struct WorkflowCapabilities(
    bool CanGoPendingCustomer,
    bool CanGoPendingInternal,
    bool RequiresApproval,
    bool CanReopen,
    bool CanChangePriority)
{
    public static WorkflowCapabilities Resolve(WorkflowTemplate template, RequestType requestType)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(requestType);

        if (requestType.WorkflowTemplateId != template.WorkflowTemplateId)
        {
            throw new ArgumentException(
                $"RequestType {requestType.RequestTypeId} uses template {requestType.WorkflowTemplateId}, not {template.WorkflowTemplateId}.",
                nameof(template));
        }

        return new WorkflowCapabilities(
            CanGoPendingCustomer: template.AllowsPendingCustomer && requestType.AllowPendingCustomer,
            CanGoPendingInternal: template.AllowsPendingInternal && requestType.AllowPendingInternal,
            RequiresApproval: template.RequiresApproval,
            CanReopen: requestType.AllowReopen,
            CanChangePriority: requestType.AllowAgentPriorityChange);
    }
}
