using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// The one place a Genesys-supplied request type is validated and applied to a
/// ticket — at initial ingestion and when the bot identifies it later — plus
/// the "nobody could classify this" path into the existing human follow-up
/// queue. Everything it does to the ticket is delegated to
/// <see cref="TicketRequestTypeRoutingService"/> (transfer semantics,
/// automatic assignment, SLA policy) and <see cref="GenesysAgentHandoffAppService"/>
/// (the human follow-up queue); nothing here is a second implementation.
///
/// <para>
/// <b>Idempotent and conservative.</b> Re-sending the request type a ticket
/// already has changes nothing; a <i>different</i> one is refused rather than
/// silently reclassifying the ticket; an invalid one is refused before any
/// write. Classification never touches the first-human-response measurement.
/// </para>
/// </summary>
public sealed class GenesysRequestTypeClassificationAppService(
    GenesysOptions options,
    ITicketRepository ticketRepository,
    IGenesysConversationRepository conversationRepository,
    ITicketAgentHandoffRepository handoffRepository,
    TicketRequestTypeRoutingService routingService,
    GenesysDefaultPriorityResolver defaultPriorityResolver,
    GenesysAgentHandoffAppService agentHandoffAppService,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>The handoff reason prefix that marks human work as "this ticket is waiting to be classified" — what lets a later classification stand exactly that work down and nothing else.</summary>
    public const string AwaitingClassificationReason = "Awaiting classification";

    /// <summary>
    /// Read-only check of an explicitly supplied request type, used BEFORE ingestion writes anything: an invalid one (unknown id, unknown or
    /// ambiguous name, inactive, or one that cannot route) is a client error, never a ticket in the human queue.
    /// </summary>
    public Task<RequestTypeResolution> ValidateAsync(GenesysRequestTypeDto requestType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        return routingService.ResolveAsync(requestType.RequestTypeId, requestType.Name, cancellationToken);
    }

    public async Task<GenesysClassificationResult> ClassifyAsync(
        Guid callerEmployeeId,
        long ticketId,
        string conversationId,
        GenesysRequestTypeDto requestType,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return GenesysClassificationResult.Failure(GenesysClassificationOutcome.TicketNotFound);
        }

        var resolution = await routingService.ResolveAsync(requestType.RequestTypeId, requestType.Name, cancellationToken);
        if (!resolution.IsValid)
        {
            return GenesysClassificationResult.Failure(GenesysClassificationOutcome.RequestTypeInvalid, resolution.Detail);
        }

        var resolved = resolution.RequestType!;

        if (ticket.RequestTypeId is { } existing)
        {
            return existing == resolved.RequestTypeId
                ? new GenesysClassificationResult(
                    GenesysClassificationOutcome.AlreadyClassified, resolved.RequestTypeId, resolved.Name, ticket.CurrentDepartmentId)
                : GenesysClassificationResult.Failure(
                    GenesysClassificationOutcome.RequestTypeConflict,
                    $"Ticket {ticket.TicketNumber} is already classified as request type {existing}; it is not reclassified by Genesys.");
        }

        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return GenesysClassificationResult.Failure(GenesysClassificationOutcome.TicketClosed);
        }

        // Only a ticket that predates default priorities needs the default
        // here; a ticket that already has a priority is never touched.
        var defaultPriority = ticket.PriorityId is null
            ? await defaultPriorityResolver.ResolveAsync(cancellationToken)
            : null;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();

        RequestTypeRoutingResult routed;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                routed = await routingService.ApplyAsync(
                    ticket, resolution, callerEmployeeId, source, defaultPriority, now, correlationId, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (TicketConcurrentlyModifiedException)
            {
                return GenesysClassificationResult.Failure(
                    GenesysClassificationOutcome.ConcurrencyConflict,
                    "The ticket was changed by someone else while it was being classified. Nothing was applied — retry.");
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await StandDownAwaitingClassificationWorkAsync(callerEmployeeId, conversationId, routed.RequestTypeName, cancellationToken);

        return new GenesysClassificationResult(
            GenesysClassificationOutcome.Classified, routed.RequestTypeId, routed.RequestTypeName, routed.DepartmentId,
            routed.Transferred, routed.AssignedEmployeeId, routed.AssignmentOutcome, routed.SlaOutcome);
    }

    /// <summary>
    /// The request type is missing or could not be resolved: the ticket stays
    /// awaiting classification and joins the existing human follow-up queue
    /// (the same pending work an agent accepts from Pending Interactions),
    /// with the reason recorded on the work item.
    /// </summary>
    public async Task<GenesysClassificationResult> AwaitClassificationAsync(
        Guid callerEmployeeId, string conversationId, string reason, CancellationToken cancellationToken = default)
    {
        if (!options.HumanQueueForUnclassified)
        {
            return new GenesysClassificationResult(
                GenesysClassificationOutcome.AwaitingClassification,
                Detail: $"{reason} The human follow-up queue is switched off (Genesys:HumanQueueForUnclassified).");
        }

        var handoff = await agentHandoffAppService.RequestAsync(
            callerEmployeeId,
            new GenesysHandoffRequestDto(
                conversationId,
                AgentAvailable: false,
                Reason: $"{AwaitingClassificationReason}: {reason}",
                Trigger: nameof(HandoffTrigger.RoutingDecision)),
            cancellationToken);

        var recorded = handoff.Outcome is GenesysHandoffOutcome.HandoffRecorded or GenesysHandoffOutcome.AlreadyRequested;
        return new GenesysClassificationResult(
            GenesysClassificationOutcome.AwaitingClassification,
            HandoffStatus: recorded ? handoff.Status : null,
            Detail: recorded ? reason : $"{reason} (human follow-up could not be queued: {handoff.Outcome})");
    }

    private async Task StandDownAwaitingClassificationWorkAsync(
        Guid callerEmployeeId, string conversationId, string requestTypeName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId)
            || await conversationRepository.GetByConversationIdAsync(conversationId.Trim(), cancellationToken) is not { } interaction
            || await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken) is not { } open
            || open.RequestReason?.StartsWith(AwaitingClassificationReason, StringComparison.Ordinal) != true)
        {
            return;
        }

        // Only the work raised BECAUSE the ticket was unclassified is stood
        // down; a customer's own request for a human is left outstanding.
        await agentHandoffAppService.CancelAsync(
            callerEmployeeId,
            new GenesysHandoffCancellationDto(conversationId.Trim(), $"Request type '{requestTypeName}' was classified."),
            cancellationToken);
    }
}
