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
/// <b>Idempotent, without losing anything.</b>
/// <see cref="TicketInteraction.End"/> is write-once, so a redelivered end
/// answers <see cref="GenesysConversationEndOutcome.AlreadyEnded"/> and never
/// moves the recorded end time or reason. Its transcript is still read,
/// though, and any message not already stored is appended — deduplicated on
/// Genesys' own message id where one is supplied, and otherwise on
/// sender+timestamp+body. That is what makes "the COMPLETE transcript is
/// persisted" true rather than "whatever the first delivery happened to
/// carry": a chat stored from a truncated first delivery is completed by the
/// retry instead of staying permanently short.
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

        var alreadyEnded = interaction.IsEnded;
        var endedAtUtc = request.EndedAtUtc ?? timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // The end time and reason are write-once: a redelivered end never
        // moves them. The TRANSCRIPT is not skipped along with them, though —
        // a redelivery that carries messages the first one did not must still
        // land them, or a conversation stored from a truncated first delivery
        // would stay permanently incomplete.
        if (!alreadyEnded)
        {
            interaction.End(endedAtUtc, request.EndReason);
        }

        interaction.RecordAgentIfAbsent(request.AgentId, request.AgentName);

        var stored = await conversationRepository.ListMessagesAsync(interaction.TicketInteractionId, cancellationToken);
        var nextSequence = stored.Count + 1;
        var appended = 0;

        foreach (var message in messages)
        {
            if (IsAlreadyStored(message, stored))
            {
                continue;
            }

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
            appended++;
        }

        await auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.ConversationEnded,
            GenesysAuditActions.ConversationEntityType,
            conversationId,
            beforeValue: null,
            afterValue:
                $"TicketId={interaction.TicketId};EndedAtUtc={endedAtUtc:O};"
                + $"EndReason={interaction.EndReason ?? "(none)"};TranscriptMessagesAppended={appended};"
                + $"TicketStatus={ticket?.TicketStatus.ToString() ?? "(unknown)"}",
            correlationId: Guid.NewGuid(),
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new GenesysConversationEndResult(
            alreadyEnded ? GenesysConversationEndOutcome.AlreadyEnded : GenesysConversationEndOutcome.Ended,
            interaction.TicketId,
            ticket?.TicketNumber,
            // Read back from the ticket, deliberately: the whole point is
            // that this is whatever the workflow says, untouched by the chat
            // ending.
            ticket?.TicketStatus.ToString(),
            // The conversation's WHOLE transcript, not just this delivery's
            // share of it — what the caller wants to know is how much is
            // stored.
            stored.Count + appended);
    }

    /// <summary>
    /// Whether this message is already in the stored transcript.
    ///
    /// <para>
    /// Genesys' own message id decides it whenever one is supplied — that is
    /// what <c>messageId</c> is for. Without one there is no identifier to
    /// trust, so the fallback compares the facts that together identify a
    /// line of conversation: same sender, same instant, same body. Two
    /// genuinely distinct messages matching all three would be the same
    /// person saying the same thing at the same timestamp, which is a
    /// redelivery in every case that matters.
    /// </para>
    /// </summary>
    private static bool IsAlreadyStored(NormalizedMessage message, IReadOnlyList<TicketInteractionMessage> stored)
    {
        if (message.ExternalMessageId is { } externalId)
        {
            return stored.Any(m => string.Equals(m.ExternalMessageId, externalId, StringComparison.Ordinal));
        }

        return stored.Any(m =>
            m.ExternalMessageId is null
            && m.Sender == message.Sender
            && m.SentAtUtc == message.SentAtUtc
            && string.Equals(m.Body, message.Body, StringComparison.Ordinal));
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
                error =
                    $"Transcript message {index + 1} has an unrecognized sender '{message.Sender}'. "
                    + "Expected Customer, VirtualAgent, HumanAgent or System.";
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
