using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// The Genesys <b>agent action</b> entry point: a Genesys agent's session
/// (the Ticketing screen opened from Genesys) identifies itself, and
/// Ticketing answers with exactly which Ticketing user that agent is — and,
/// when the agent is working a conversation, records that user as the one
/// who handled the conversation's interaction.
///
/// <para>
/// <b>Strict, because this represents an agent.</b> Unlike the inquiry and
/// update events (which a queue or system may send before any agent exists),
/// this request <i>is</i> an agent acting, so the Genesys User ID is
/// required, and an unmapped or deactivated agent is refused with a
/// distinct outcome rather than being let through anonymously.
/// </para>
///
/// <para>
/// <b>Ownership is resolved here, on the server, from the Genesys User ID
/// alone.</b> The request carries no Ticketing user id and none would be
/// honoured: <c>HandledByUserId</c> is whatever the mapping resolves to,
/// never what a client claims.
/// </para>
///
/// <para>
/// <b>Records who handled the interaction — nothing else.</b> The ticket's
/// assignee, department, queue, status and workflow are untouched: "handled
/// the conversation" and "owns the ticket" are different facts, and only the
/// first is this service's to write.
/// </para>
/// </summary>
public sealed class GenesysAgentContextAppService(
    GenesysOptions options,
    GenesysAgentResolutionAppService agentResolution,
    IGenesysConversationRepository conversationRepository,
    ITicketRepository ticketRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter)
{
    public async Task<GenesysAgentContextResult> ResolveAsync(
        Guid callerEmployeeId, GenesysAgentContextDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return GenesysAgentContextResult.Failure(GenesysAgentContextOutcome.IntegrationDisabled);
        }

        var resolution = await agentResolution.ResolveByGenesysUserIdAsync(request.GenesysUserId, cancellationToken);
        if (!resolution.IsResolved)
        {
            return resolution.Outcome switch
            {
                GenesysAgentResolutionOutcome.Invalid => GenesysAgentContextResult.Failure(GenesysAgentContextOutcome.AgentIdRequired, resolution.Detail),
                GenesysAgentResolutionOutcome.Inactive => GenesysAgentContextResult.Failure(GenesysAgentContextOutcome.AgentInactive, resolution.Detail),
                _ => GenesysAgentContextResult.Failure(GenesysAgentContextOutcome.AgentNotMapped, resolution.Detail)
            };
        }

        var agent = resolution.Agent!;

        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            // Identity only — the agent opened Ticketing without a
            // conversation in hand. Nothing to record ownership on.
            return new GenesysAgentContextResult(
                GenesysAgentContextOutcome.Resolved, agent, resolution.Roles, resolution.DepartmentIds);
        }

        var conversationId = request.ConversationId.Trim();
        var interaction = await conversationRepository.GetByConversationIdAsync(conversationId, cancellationToken);
        if (interaction is null)
        {
            return GenesysAgentContextResult.Failure(
                GenesysAgentContextOutcome.ConversationNotFound,
                "This conversation never produced a ticket, so there is no interaction to record the agent on.");
        }

        var ticket = await ticketRepository.GetByIdAsync(interaction.TicketId, cancellationToken);

        // Apply-if-absent: the first resolved agent is the recorded handler,
        // consistent with how the verbatim Genesys agent context is kept.
        if (interaction.RecordHandlingAgentIfAbsent(agent.GenesysUserId, agent.UserId))
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

            await auditWriter.WriteAsync(
                callerEmployeeId,
                GenesysAuditActions.InteractionHandlerRecorded,
                nameof(TicketInteraction),
                interaction.TicketInteractionId.ToString(),
                beforeValue: null,
                afterValue:
                    $"ConversationId={conversationId};GenesysUserId={agent.GenesysUserId};HandledByUserId={agent.UserId};"
                    + $"AgentEmail={request.AgentEmail ?? "(none)"}",
                correlationId: Guid.NewGuid(),
                cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return new GenesysAgentContextResult(
            GenesysAgentContextOutcome.Resolved,
            agent,
            resolution.Roles,
            resolution.DepartmentIds,
            ticket?.TicketId ?? interaction.TicketId,
            ticket?.TicketNumber,
            interaction.TicketInteractionId,
            interaction.HandledByUserId);
    }
}
