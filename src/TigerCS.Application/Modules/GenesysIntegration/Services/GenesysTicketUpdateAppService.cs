using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// The one update contract Genesys consumes — a <b>facade</b> over the
/// conversation-end and human-handoff services, not a reimplementation of
/// either.
///
/// <para>
/// <b>Why a facade.</b> Genesys asked for three externally consumable
/// contracts: create, look up, update. The behaviour behind "update" already
/// existed as separate services with their own rules, idempotency and tests;
/// splitting one integration concept across three routes made the external
/// surface wider than the integration actually is. This composes them, so
/// there is exactly one place Genesys sends changes and exactly one place
/// each rule lives.
/// </para>
///
/// <para>
/// <b>Genesys owns the conversation; TigerCS owns the ticket.</b> The only
/// things this accepts are conversation facts — who is handling it, when it
/// ended, what was said, whether it needs a human. Category, request type,
/// priority, status, owner, department, resolution and closure are absent
/// from the contract by design: they move through their own TigerCS
/// operations, with their own authorization and SLA consequences. Genesys
/// cannot reach them from here at all.
/// </para>
///
/// <para>
/// <b>Nothing here closes a ticket.</b> Ending a conversation finalizes the
/// interaction and stores the transcript; the ticket carries on under the
/// existing lifecycle. The response echoes the ticket's unchanged status to
/// make that visible on every call.
/// </para>
///
/// <para>
/// <b>Validated before anything is written.</b> The conversation must resolve
/// to an interaction on the ticket in the route — that cross-check is what
/// stops a transcript being applied to the wrong ticket — and a malformed
/// transcript is refused by the end service before it writes, so a bad
/// update never leaves a half-applied conversation.
/// </para>
/// </summary>
public sealed class GenesysTicketUpdateAppService(
    GenesysOptions options,
    IGenesysConversationRepository conversationRepository,
    ITicketRepository ticketRepository,
    ITicketAgentHandoffRepository handoffRepository,
    ITicketingUnitOfWork unitOfWork,
    GenesysConversationEndAppService conversationEndAppService,
    GenesysAgentHandoffAppService agentHandoffAppService,
    GenesysAgentResolutionAppService agentResolution)
{
    public async Task<GenesysTicketUpdateResult> UpdateAsync(
        Guid callerEmployeeId, long ticketId, GenesysTicketUpdateDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.IntegrationDisabled);
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.ConversationIdRequired);
        }

        var conversationId = request.ConversationId.Trim();

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.TicketNotFound);
        }

        var interaction = await conversationRepository.GetByConversationIdAsync(conversationId, cancellationToken);
        if (interaction is null)
        {
            return GenesysTicketUpdateResult.Failure(
                GenesysTicketUpdateOutcome.ConversationNotFound,
                "This conversation never produced a ticket, so there is nothing to update.");
        }

        // The cross-check. Both identifiers are the caller's; agreeing with
        // one of them and guessing about the other would silently attach a
        // transcript, or pending human work, to somebody else's ticket.
        if (interaction.TicketId != ticketId)
        {
            return GenesysTicketUpdateResult.Failure(
                GenesysTicketUpdateOutcome.ConversationTicketMismatch,
                $"Conversation '{conversationId}' belongs to ticket {interaction.TicketId}, not {ticketId}.");
        }

        // Human handoff first: a conversation that ends in the same update
        // must still leave its pending work outstanding and actionable, and
        // requesting the handoff before the end event is the order that reads
        // the way the real sequence happened.
        var handoffStatus = (string?)null;
        var handoffId = (long?)null;

        if (request.Handoff is { } handoff)
        {
            if (handoff.Required)
            {
                var requested = await agentHandoffAppService.RequestAsync(
                    callerEmployeeId,
                    new GenesysHandoffRequestDto(
                        conversationId, handoff.AgentAvailable, handoff.Mode, handoff.Reason,
                        request.AgentId, request.AgentName, handoff.WorkItemId),
                    cancellationToken);

                if (requested.Outcome is not (GenesysHandoffOutcome.HandoffRecorded or GenesysHandoffOutcome.AlreadyRequested))
                {
                    return Translate(requested);
                }

                handoffStatus = requested.Status;
                handoffId = requested.TicketAgentHandoffId;
            }

            if (!string.IsNullOrWhiteSpace(handoff.AssignedAgentId))
            {
                var assigned = await agentHandoffAppService.UpdateAssignmentAsync(
                    callerEmployeeId,
                    new GenesysHandoffAssignmentDto(conversationId, handoff.AssignedAgentId, request.AgentName),
                    cancellationToken);

                if (assigned.Outcome != GenesysHandoffOutcome.AssignmentRecorded)
                {
                    return Translate(assigned);
                }

                handoffStatus = assigned.Status;
                handoffId = assigned.TicketAgentHandoffId;
            }
        }

        // The conversation's own ending — transcript included. Write-once in
        // the domain, so a redelivered end answers AlreadyEnded without
        // moving the recorded time or re-storing the transcript.
        var transcriptCount = await conversationRepository.CountMessagesAsync(interaction.TicketInteractionId, cancellationToken);

        if (request.Ended is { } ended)
        {
            var endResult = await conversationEndAppService.EndAsync(
                callerEmployeeId,
                new GenesysConversationEndDto(
                    conversationId, ended.EndedAtUtc, ended.EndReason,
                    request.AgentId, request.AgentName, ended.Transcript),
                cancellationToken);

            if (endResult.Outcome is not (GenesysConversationEndOutcome.Ended or GenesysConversationEndOutcome.AlreadyEnded))
            {
                return Translate(endResult);
            }

            transcriptCount = endResult.TranscriptMessageCount;
        }
        else if (!string.IsNullOrWhiteSpace(request.AgentId) || !string.IsNullOrWhiteSpace(request.AgentName))
        {
            // Agent context without an ending: apply-if-absent on the
            // interaction, so a later, different agent never overwrites the
            // one who actually took the conversation.
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            interaction.RecordAgentIfAbsent(request.AgentId, request.AgentName);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Interaction ownership from the agent this update names — resolved
        // server-side from the Genesys User ID (agentId), apply-if-absent
        // like the verbatim agent context above. An update may name no agent
        // at all, and an unmapped one changes nothing here: the verbatim
        // agentId is still kept, and ownership stays null. The strict path
        // for an agent acting is the agent-context endpoint.
        if (!string.IsNullOrWhiteSpace(request.AgentId)
            && interaction.HandledByUserId is null
            && await agentResolution.ResolveByGenesysUserIdAsync(request.AgentId, cancellationToken) is { IsResolved: true } agent
            && interaction.RecordHandlingAgentIfAbsent(agent.Agent!.GenesysUserId, agent.Agent.UserId))
        {
            await using var ownershipTransaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await ownershipTransaction.CommitAsync(cancellationToken);
        }

        // The handoff state is reported on EVERY update, not only on one that
        // touched it. Genesys ending a conversation needs to see that the
        // pending human work is still outstanding — that is exactly the
        // customer-disconnected case, where the live session is over and the
        // work is not.
        if (handoffStatus is null
            && await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken) is { } openHandoff)
        {
            handoffStatus = openHandoff.Status.ToString();
            handoffId = openHandoff.TicketAgentHandoffId;
        }

        return new GenesysTicketUpdateResult(
            GenesysTicketUpdateOutcome.Applied,
            ticket.TicketId,
            ticket.TicketNumber,
            // Echoed on every response: the proof that nothing in this
            // contract closed, resolved or otherwise moved the ticket.
            ticket.TicketStatus.ToString(),
            interaction.IsEnded,
            transcriptCount,
            handoffStatus,
            handoffId);
    }

    private static GenesysTicketUpdateResult Translate(GenesysHandoffResult result) => result.Outcome switch
    {
        GenesysHandoffOutcome.IntegrationDisabled => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.IntegrationDisabled),
        GenesysHandoffOutcome.InvalidMode => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.InvalidHandoffMode, result.Detail),
        GenesysHandoffOutcome.NoOpenHandoff => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.NoOpenHandoff, result.Detail),
        GenesysHandoffOutcome.ConversationNotFound => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.ConversationNotFound, result.Detail),
        _ => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.NoOpenHandoff, result.Detail)
    };

    private static GenesysTicketUpdateResult Translate(GenesysConversationEndResult result) => result.Outcome switch
    {
        GenesysConversationEndOutcome.IntegrationDisabled => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.IntegrationDisabled),
        GenesysConversationEndOutcome.InvalidTranscript => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.InvalidTranscript, result.Detail),
        _ => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.ConversationNotFound, result.Detail)
    };
}
