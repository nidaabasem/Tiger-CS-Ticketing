using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Genesys integration phase 1 — finalizing a conversation and preserving its
/// transcript.
///
/// <para>
/// <b>Ending a conversation never closes a ticket.</b> This is the phase's
/// load-bearing business rule and the reason this service touches the
/// <see cref="Ticket"/> aggregate only to read it: the chat ends, the
/// interaction is marked ended, and the ticket carries on under the existing
/// TigerCS lifecycle — a customer who asked for an NOC has an open workflow
/// long after the chat window closed. No status, SLA, resolution or
/// escalation state is written here.
/// </para>
///
/// <para>
/// <b>Every disconnection is an ending.</b> The customer closing the browser,
/// losing signal, the agent ending the interaction, or Genesys terminating
/// it are all the same call: whatever end time and reason are supplied are
/// recorded, and the transcript available up to that moment is stored. A
/// conversation that ended abruptly still leaves a complete record of what
/// was said.
/// </para>
///
/// <para>
/// <b>Idempotent.</b> <see cref="TicketInteraction.End"/> is write-once, so a
/// redelivered end event answers
/// <see cref="GenesysConversationEndOutcome.AlreadyEnded"/> without moving
/// the recorded end time and without appending the transcript a second time.
/// The transcript is validated in full <i>before</i> anything is written, so
/// a malformed message can never leave a half-stored conversation.
/// </para>
/// </summary>
public sealed class GenesysConversationEndAppService(
    GenesysOptions options,
    IGenesysConversationRepository conversationRepository,
    ITicketRepository ticketRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<GenesysConversationEndResult> EndAsync(
        Guid callerEmployeeId, GenesysConversationEndDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return GenesysConversationEndResult.Failure(GenesysConversationEndOutcome.IntegrationDisabled);
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return GenesysConversationEndResult.Failure(GenesysConversationEndOutcome.ConversationIdRequired);
        }

        var conversationId = request.ConversationId.Trim();

        var interaction = await conversationRepository.GetByConversationIdAsync(conversationId, cancellationToken);
        if (interaction is null)
        {
            // A conversation that never produced a ticket — most commonly a
            // call that rang and was never answered. Reported plainly rather
            // than fabricating an interaction to hang the end event on.
            return GenesysConversationEndResult.Failure(GenesysConversationEndOutcome.ConversationNotFound);
        }

        var ticket = await ticketRepository.GetByIdAsync(interaction.TicketId, cancellationToken);

        // Validate the whole transcript before writing any of it.
        if (!TryNormalizeTranscript(request.Transcript, out var messages, out var transcriptError))
        {
            return GenesysConversationEndResult.Failure(GenesysConversationEndOutcome.InvalidTranscript, transcriptError);
        }

        if (interaction.IsEnded)
        {
            return new GenesysConversationEndResult(
                GenesysConversationEndOutcome.AlreadyEnded,
                interaction.TicketId,
                ticket?.TicketNumber,
                ticket?.TicketStatus.ToString(),
                await conversationRepository.CountMessagesAsync(interaction.TicketInteractionId, cancellationToken));
        }

        var endedAtUtc = request.EndedAtUtc ?? timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        interaction.End(endedAtUtc, request.EndReason);
        interaction.RecordAgentIfAbsent(request.AgentId, request.AgentName);

        // Sequence continues from whatever is already stored, so a transcript
        // delivered in parts appends rather than restarting at 1.
        var nextSequence = await conversationRepository.CountMessagesAsync(interaction.TicketInteractionId, cancellationToken) + 1;
        foreach (var message in messages)
        {
            await conversationRepository.AddMessageAsync(
                new TicketInteractionMessage(
                    interaction.TicketInteractionId,
                    nextSequence++,
                    message.Sender,
                    message.SenderName,
                    message.SenderId,
                    message.SentAtUtc,
                    message.Body,
                    message.ExternalMessageId),
                cancellationToken);
        }

        await auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.ConversationEnded,
            GenesysAuditActions.ConversationEntityType,
            conversationId,
            beforeValue: null,
            afterValue:
                $"TicketId={interaction.TicketId};EndedAtUtc={endedAtUtc:O};"
                + $"EndReason={request.EndReason ?? "(none)"};TranscriptMessages={messages.Count};"
                + $"TicketStatus={ticket?.TicketStatus.ToString() ?? "(unknown)"}",
            correlationId: Guid.NewGuid(),
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new GenesysConversationEndResult(
            GenesysConversationEndOutcome.Ended,
            interaction.TicketId,
            ticket?.TicketNumber,
            // Read back from the ticket, deliberately: the whole point is
            // that this is whatever the workflow says, untouched by the chat
            // ending.
            ticket?.TicketStatus.ToString(),
            messages.Count);
    }

    /// <summary>
    /// Validates and normalizes every supplied message before a single row is
    /// written: an unknown sender or an empty body fails the whole request
    /// rather than being silently dropped or attributed to the wrong party.
    /// Order is the delivered order — never re-sorted by timestamp, because a
    /// provider clock is not guaranteed monotonic and two messages may share
    /// a timestamp.
    /// </summary>
    private static bool TryNormalizeTranscript(
        IReadOnlyList<GenesysTranscriptMessageDto>? transcript,
        out IReadOnlyList<NormalizedMessage> messages,
        out string? error)
    {
        messages = [];
        error = null;

        if (transcript is null || transcript.Count == 0)
        {
            return true;
        }

        var normalized = new List<NormalizedMessage>(transcript.Count);
        for (var index = 0; index < transcript.Count; index++)
        {
            var message = transcript[index];

            if (!Enum.TryParse<InteractionMessageSender>(message.Sender, ignoreCase: true, out var sender)
                || !Enum.IsDefined(sender))
            {
                error = $"Transcript message {index + 1} has an unrecognized sender '{message.Sender}'. Expected Customer, Agent or System.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(message.Body))
            {
                error = $"Transcript message {index + 1} has an empty body.";
                return false;
            }

            normalized.Add(new NormalizedMessage(
                sender, message.SenderName, message.SenderId, message.SentAtUtc, message.Body, message.ExternalMessageId));
        }

        messages = normalized;
        return true;
    }

    private sealed record NormalizedMessage(
        InteractionMessageSender Sender,
        string? SenderName,
        string? SenderId,
        DateTime SentAtUtc,
        string Body,
        string? ExternalMessageId);
}
