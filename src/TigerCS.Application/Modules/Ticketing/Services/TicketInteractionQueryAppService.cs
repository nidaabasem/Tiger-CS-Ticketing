using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The read model behind Ticket Details' Conversation History: every
/// interaction a ticket accumulated — the originating Genesys call or chat
/// and any later ones — in chronological order, each with its transcript.
///
/// <para>
/// <b>Same visibility rule as the ticket itself.</b> Authorization is
/// delegated to <see cref="TicketQueryAppService.CanViewDepartmentAsync"/>,
/// so a conversation is readable exactly when the ticket it belongs to is —
/// this endpoint can never become a side door onto a department's
/// conversations.
/// </para>
///
/// <para>
/// <b>One query for the transcripts, not one per interaction.</b> Every
/// interaction's messages are fetched in a single batched read
/// (<see cref="IGenesysConversationRepository.ListMessagesForInteractionsAsync"/>),
/// the same no-N+1 discipline the customer-history read follows.
/// </para>
/// </summary>
public sealed class TicketInteractionQueryAppService(
    ITicketRepository ticketRepository,
    ITicketInteractionRepository interactionRepository,
    IGenesysConversationRepository conversationRepository,
    IChannelRepository channelRepository,
    TicketQueryAppService ticketQueryAppService)
{
    public async Task<TicketQueryResultDto<TicketInteractionHistoryDto>> GetForTicketAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketQueryResultDto<TicketInteractionHistoryDto>.Failure(TicketQueryOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewDepartmentAsync(
                callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, cancellationToken))
        {
            return TicketQueryResultDto<TicketInteractionHistoryDto>.Failure(TicketQueryOutcome.Forbidden);
        }

        var interactions = await interactionRepository.ListByTicketIdAsync(ticketId, cancellationToken);
        if (interactions.Count == 0)
        {
            return TicketQueryResultDto<TicketInteractionHistoryDto>.Success(
                new TicketInteractionHistoryDto(ticketId, []));
        }

        var messagesByInteraction = await conversationRepository.ListMessagesForInteractionsAsync(
            interactions.Select(i => i.TicketInteractionId).ToList(), cancellationToken);

        // Channel names come from configuration, resolved by id, so a channel
        // deactivated since the interaction happened still displays its name.
        var channelNames = new Dictionary<byte, string?>();
        foreach (var channelId in interactions.Select(i => i.ChannelId).Distinct())
        {
            channelNames[channelId] = (await channelRepository.GetByIdAsync(channelId, cancellationToken))?.Name;
        }

        var items = interactions
            .Select(interaction => ToDto(
                interaction,
                channelNames.GetValueOrDefault(interaction.ChannelId),
                messagesByInteraction.TryGetValue(interaction.TicketInteractionId, out var messages) ? messages : []))
            // Chronological: Genesys' own start time where it supplied one,
            // otherwise the moment Ticketing recorded the interaction.
            .OrderBy(i => i.StartedAtUtc)
            .ThenBy(i => i.TicketInteractionId)
            .ToList();

        return TicketQueryResultDto<TicketInteractionHistoryDto>.Success(
            new TicketInteractionHistoryDto(ticketId, items));
    }

    private static TicketInteractionDto ToDto(
        TicketInteraction interaction, string? channelName, IReadOnlyList<TicketInteractionMessage> messages) => new(
        interaction.TicketInteractionId,
        interaction.IsOriginatingInteraction,
        interaction.Source.ToString(),
        interaction.ChannelId,
        channelName,
        interaction.CustomerPhone,
        interaction.CustomerName,
        interaction.CustomerEmail,
        interaction.Direction,
        interaction.GenesysConversationId,
        interaction.GenesysQueueId,
        interaction.GenesysQueueName,
        interaction.GenesysAgentName,
        interaction.GenesysAgentId,
        interaction.InteractionStartedAtUtc ?? interaction.CreatedAtUtc,
        interaction.EndedAtUtc,
        interaction.EndReason,
        interaction.IsEnded ? "Ended" : "Active",
        messages
            .OrderBy(m => m.Sequence)
            .Select(m => new TicketInteractionMessageDto(m.Sequence, m.Sender.ToString(), m.SenderName, m.SentAtUtc, m.Body))
            .ToList());
}
