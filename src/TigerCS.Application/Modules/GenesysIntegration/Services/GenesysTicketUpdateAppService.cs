using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

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
    GenesysAgentResolutionAppService agentResolution,
    IAuditEntryWriter auditWriter)
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
            if (handoff.Required == true)
            {
                var requested = await agentHandoffAppService.RequestAsync(
                    callerEmployeeId,
                    new GenesysHandoffRequestDto(
                        conversationId, handoff.AgentAvailable, handoff.Mode, handoff.Reason, handoff.Trigger,
                        request.AgentId, request.AgentName, handoff.WorkItemId),
                    cancellationToken);

                if (requested.Outcome is not (GenesysHandoffOutcome.HandoffRecorded or GenesysHandoffOutcome.AlreadyRequested))
                {
                    return Translate(requested);
                }

                handoffStatus = requested.Status;
                handoffId = requested.TicketAgentHandoffId;
            }
            else if (handoff.Required == false)
            {
                // An EXPLICIT false stands outstanding work down: the AI
                // resumed, or the human is no longer needed. Omitted (null)
                // never reaches here — an update that carries only
                // AssignedAgentId must not cancel the very work it is
                // reporting an assignment for. Nothing outstanding answers
                // AlreadyResolved, which is the idempotent case and not a
                // failure — so the update as a whole still succeeds and
                // simply reports the work's state.
                var cancelled = await agentHandoffAppService.CancelAsync(
                    callerEmployeeId,
                    new GenesysHandoffCancellationDto(conversationId, handoff.Reason),
                    cancellationToken);

                if (cancelled.Outcome is not (GenesysHandoffOutcome.HandoffCancelled or GenesysHandoffOutcome.AlreadyResolved))
                {
                    return Translate(cancelled);
                }

                if (cancelled.Outcome == GenesysHandoffOutcome.HandoffCancelled)
                {
                    handoffStatus = cancelled.Status;
                    handoffId = cancelled.TicketAgentHandoffId;
                }
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

        // Routing: the queue the conversation is in now and the agent it is
        // connected to — a queue change, an agent connecting, a transfer.
        // Same interaction, same ticket; the ticket itself is not touched.
        if (request.Routing is not null || request.StartedAtUtc is not null)
        {
            await RecordRoutingAsync(callerEmployeeId, interaction, request, cancellationToken);
        }

        // An agent connecting to a conversation whose human work is still
        // waiting IS the human taking it: recorded on that same work item,
        // through the same assignment path as handoff.assignedAgentId. Only
        // while it is WaitingForAgent — work a TigerCS agent is already
        // handling is never reassigned by a Genesys routing event — and never
        // when this same update states the handoff explicitly.
        if (NullIfBlank(request.Routing?.AgentId) is { } connectedAgentId
            && request.Handoff is not { Required: true } && NullIfBlank(request.Handoff?.AssignedAgentId) is null
            && await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken)
                is { Status: AgentHandoffStatus.WaitingForAgent })
        {
            var taken = await agentHandoffAppService.UpdateAssignmentAsync(
                callerEmployeeId,
                new GenesysHandoffAssignmentDto(conversationId, connectedAgentId, request.Routing!.AgentName),
                cancellationToken);

            if (taken.Outcome == GenesysHandoffOutcome.AssignmentRecorded)
            {
                handoffStatus = taken.Status;
                handoffId = taken.TicketAgentHandoffId;
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
        // for an agent acting is the agent-context endpoint. The agent a
        // routing change connected counts too: on a voice call that is how
        // Genesys first names the agent at all.
        if ((NullIfBlank(request.AgentId) ?? NullIfBlank(request.Routing?.AgentId)) is { } reportedAgentId
            && interaction.HandledByUserId is null
            && await agentResolution.ResolveByGenesysUserIdAsync(reportedAgentId, cancellationToken) is { IsResolved: true } agent
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

    /// <summary>
    /// Applies a routing change (and a late start time) to the interaction,
    /// and audits it with the before and after values — the audit trail is
    /// where the history of every queue and agent the conversation passed
    /// through is kept, since the interaction itself names only the current
    /// one. A redelivered, unchanged routing event writes nothing at all.
    /// </summary>
    private async Task RecordRoutingAsync(
        Guid callerEmployeeId, TicketInteraction interaction, GenesysTicketUpdateDto request, CancellationToken cancellationToken)
    {
        var before = DescribeRouting(interaction);

        var routingChanged = request.Routing is { } routing
            && interaction.RecordRouting(routing.QueueId, routing.QueueName, routing.AgentId, routing.AgentName);
        var startRecorded = interaction.RecordStartedAtIfAbsent(request.StartedAtUtc);

        if (!routingChanged && !startRecorded)
        {
            return;
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.RoutingChanged,
            GenesysAuditActions.ConversationEntityType,
            interaction.GenesysConversationId!,
            beforeValue: before,
            afterValue: DescribeRouting(interaction),
            correlationId: Guid.NewGuid(),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string DescribeRouting(TicketInteraction interaction) =>
        $"TicketId={interaction.TicketId};QueueId={interaction.GenesysQueueId ?? "(none)"};QueueName={interaction.GenesysQueueName ?? "(none)"};"
        + $"AgentId={interaction.GenesysAgentId ?? "(none)"};AgentName={interaction.GenesysAgentName ?? "(none)"};"
        + $"StartedAtUtc={interaction.InteractionStartedAtUtc?.ToString("O") ?? "(none)"}";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static GenesysTicketUpdateResult Translate(GenesysHandoffResult result) => result.Outcome switch
    {
        GenesysHandoffOutcome.IntegrationDisabled => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.IntegrationDisabled),
        GenesysHandoffOutcome.InvalidMode => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.InvalidHandoffMode, result.Detail),
        GenesysHandoffOutcome.InvalidTrigger => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.InvalidHandoffTrigger, result.Detail),
        GenesysHandoffOutcome.ReasonRequired => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.HandoffReasonRequired, result.Detail),
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
