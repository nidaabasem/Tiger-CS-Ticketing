using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// The ticket's record of which Genesys agents actually handled the customer, kept in the existing typed workflow
/// event history (no table of its own).
///
/// <para>
/// <b>Handling is not escalation.</b> A Genesys call transfer says an agent passed the conversation on, not that they
/// failed to resolve it, so these events carry no transfer reason: they are never escalations and never count as ticket
/// transfers. Only an explicit TigerCS transfer or reassignment, with a reason chosen by a person, records that.
/// </para>
///
/// <para>
/// <b>Everyone who handled it is preserved.</b> The first mapped agent is also <c>HandledByUserId</c> on the
/// interaction (apply-if-absent, unchanged). Each later agent is the receiving side of a
/// <see cref="WorkflowEventType.GenesysCallTransferred"/> event, and an agent who cannot be mapped to a TigerCS user is
/// kept verbatim (Genesys id and name) in the event note. Nothing here changes ticket ownership or visibility — who
/// handled the call and who owns the ticket stay different facts.
/// </para>
/// </summary>
public sealed class GenesysAgentHandlingHistory(
    ITicketWorkflowEventRepository workflowEventRepository,
    GenesysAgentResolutionAppService agentResolution,
    TimeProvider timeProvider)
{
    private const int NoteMaxLength = 500;

    /// <summary>The first mapped agent took the interaction. Call only when <c>RecordHandlingAgentIfAbsent</c> actually recorded them.</summary>
    public async Task RecordFirstHandlerAsync(
        long ticketId, string? conversationId, GenesysMappedAgent agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        await workflowEventRepository.AddAsync(
            new TicketWorkflowEvent(
                ticketId, WorkflowEventType.GenesysAgentConnected, timeProvider.GetUtcNow().UtcDateTime,
                actorEmployeeId: null, ticketApprovalId: null,
                Truncate($"Genesys conversation {conversationId ?? "(unknown)"}: agent {agent.GenesysUserId} answered."),
                Guid.NewGuid())
                .WithOwnership(fromDepartmentId: null, toDepartmentId: null, toEmployeeId: agent.UserId),
            cancellationToken);
    }

    /// <summary>
    /// Genesys moved the conversation from one agent to another (both Genesys user ids known, and different). Either side
    /// that maps to an active TigerCS user is recorded as that user; either that does not is preserved in the note.
    /// </summary>
    public async Task RecordCallTransferAsync(
        long ticketId,
        string? conversationId,
        string fromGenesysAgentId,
        string? fromAgentName,
        string toGenesysAgentId,
        string? toAgentName,
        string? toQueueName,
        CancellationToken cancellationToken = default)
    {
        var from = await agentResolution.ResolveByGenesysUserIdAsync(fromGenesysAgentId, cancellationToken);
        var to = await agentResolution.ResolveByGenesysUserIdAsync(toGenesysAgentId, cancellationToken);

        static string Describe(string genesysId, string? name, GenesysAgentResolutionResult resolved) =>
            $"{(string.IsNullOrWhiteSpace(name) ? genesysId : $"{name.Trim()} ({genesysId})")}{(resolved.IsResolved ? string.Empty : " [not mapped to a TigerCS user]")}";

        var note = $"Genesys conversation {conversationId ?? "(unknown)"}: call transferred from {Describe(fromGenesysAgentId, fromAgentName, from)}"
            + $" to {Describe(toGenesysAgentId, toAgentName, to)}"
            + (string.IsNullOrWhiteSpace(toQueueName) ? "." : $" (queue {toQueueName.Trim()}).");

        await workflowEventRepository.AddAsync(
            new TicketWorkflowEvent(
                ticketId, WorkflowEventType.GenesysCallTransferred, timeProvider.GetUtcNow().UtcDateTime,
                actorEmployeeId: from.IsResolved ? from.Agent!.UserId : null, ticketApprovalId: null,
                Truncate(note), Guid.NewGuid())
                .WithOwnership(fromDepartmentId: null, toDepartmentId: null, toEmployeeId: to.IsResolved ? to.Agent!.UserId : null),
            cancellationToken);
    }

    private static string Truncate(string note) => note.Length <= NoteMaxLength ? note : note[..NoteMaxLength];
}
