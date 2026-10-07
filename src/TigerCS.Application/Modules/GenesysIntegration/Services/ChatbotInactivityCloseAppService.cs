using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>What one attempt to close an interaction's ticket for customer inactivity did.</summary>
public enum ChatbotInactivityCloseOutcome
{
    /// <summary>The ticket was closed by this call.</summary>
    Closed,

    /// <summary>Nothing to do: the integration/timeout is off, the interaction is unknown, or it is no longer (or not yet) due — including "already closed by an earlier run".</summary>
    NotDue,

    /// <summary>The re-check found the interaction should not be waiting any more (human assigned or requested, conversation ended, ticket not closable); the timer was cancelled and nothing was closed.</summary>
    Excluded,

    /// <summary>A customer reply, another worker or a ticket edit won the race; this call changed nothing. Safe — the other writer's outcome stands.</summary>
    LostRace
}

/// <summary>
/// Closes a ticket whose chatbot has been waiting on the customer for longer
/// than the configured time (<see cref="GenesysOptions.CustomerInactivityTimeout"/>,
/// default 5 minutes). Driven by a recurring server-side job — never by a
/// client — reading <c>TicketInteractions.AwaitingCustomerReplySinceUtc</c>,
/// so the timeout survives restarts and deploys.
///
/// <para>
/// <b>Re-checked at fire time, in this order, before anything is written:</b>
/// the integration is on and a timeout is configured; the interaction still
/// carries a timer that is strictly older than the timeout (a customer reply
/// clears it; a repeated webhook never moved it); the conversation has not
/// ended; no human follow-up is open (requested handoff, bot connection
/// failure — both raise a <see cref="TicketAgentHandoff"/>); no person owns the
/// ticket (a human assignment); the ticket is still open for work. Any
/// exclusion cancels the timer so it stops being a candidate, and closes
/// nothing.
/// </para>
///
/// <para>
/// <b>Race with a customer reply.</b> The timer column is a concurrency
/// token. The interaction is loaded, checked, and its timer cleared in the
/// same SaveChanges as the ticket closure; if a reply cleared the timer in
/// between, that write affects zero rows and the whole closure rolls back
/// (<see cref="ChatbotInactivityCloseOutcome.LostRace"/>). The reply wins.
/// </para>
///
/// <para>
/// <b>Idempotent.</b> The interaction's <c>InactivityClosedAtUtc</c> is
/// write-once and the Outbox lifecycle event has its own idempotency key, so a
/// repeated run, an overlapping run or a Hangfire retry closes once and
/// notifies once.
/// </para>
///
/// <para>
/// The closure goes through <see cref="TicketLifecycleAppService.CloseForCustomerInactivityAsync"/>:
/// the lifecycle's Resolved→Closed transitions, with outcome
/// <c>Cancelled</c> — never "Resolved" — and the audit reason below.
/// </para>
/// </summary>
public sealed class ChatbotInactivityCloseAppService(
    GenesysOptions options,
    IGenesysConversationRepository conversationRepository,
    ITicketRepository ticketRepository,
    ITicketAgentHandoffRepository handoffRepository,
    TicketLifecycleAppService lifecycleAppService,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>Upper bound on candidates examined per job run; the next run takes the rest.</summary>
    public const int BatchSize = 100;

    /// <summary>The audit/resolution reason, with the configured minutes (5 by default).</summary>
    public static string ReasonFor(int timeoutMinutes) =>
        $"Automatically closed — customer did not respond for more than {timeoutMinutes} minutes.";

    /// <summary>Ids of interactions whose timer is already past the timeout — candidates only; each is re-checked by <see cref="TryCloseAsync"/>.</summary>
    public async Task<IReadOnlyList<long>> ListDueAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || options.CustomerInactivityTimeout is not { } timeout)
        {
            return [];
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime - timeout;
        return await conversationRepository.ListAwaitingReplyOlderThanAsync(cutoff, BatchSize, cancellationToken);
    }

    public async Task<ChatbotInactivityCloseOutcome> TryCloseAsync(long ticketInteractionId, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || options.CustomerInactivityTimeout is not { } timeout)
        {
            return ChatbotInactivityCloseOutcome.NotDue;
        }

        var interaction = await conversationRepository.GetByIdAsync(ticketInteractionId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Not waiting, not yet due, ended (End clears the timer) or already
        // closed by an earlier run: nothing to do, and nothing to write.
        if (interaction is null || !interaction.IsInactivityTimeoutDue(now, timeout) || interaction.IsEnded)
        {
            return ChatbotInactivityCloseOutcome.NotDue;
        }

        var reporter = interaction.AwaitingCustomerReplyReportedByEmployeeId;
        var ticket = await ticketRepository.GetByIdAsync(interaction.TicketId, cancellationToken);

        var excluded = reporter is null
            || ticket is null
            || ticket.TicketStatus is not (TicketStatus.Open or TicketStatus.InProgress or TicketStatus.PendingCustomer)
            || ticket.CurrentOwnerEmployeeId is not null
            || await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken) is not null;

        if (excluded)
        {
            // Stop re-examining it every run; a later chatbot prompt starts a new timer.
            return await CancelTimerAsync(interaction, cancellationToken);
        }

        var reason = ReasonFor((int)timeout.TotalMinutes);

        TicketMutationResult result;
        try
        {
            result = await lifecycleAppService.CloseForCustomerInactivityAsync(
                reporter!.Value, ticket!.TicketId, reason,
                beforeCommit: closedAtUtc => interaction.RecordInactivityClosure(closedAtUtc),
                cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return ChatbotInactivityCloseOutcome.LostRace;
        }

        return result.Outcome switch
        {
            TicketMutationOutcome.Success => ChatbotInactivityCloseOutcome.Closed,
            TicketMutationOutcome.ConcurrencyConflict => ChatbotInactivityCloseOutcome.LostRace,
            // Closed meanwhile / not eligible: the same exclusion as above.
            _ => await CancelTimerAsync(interaction, cancellationToken)
        };
    }

    private async Task<ChatbotInactivityCloseOutcome> CancelTimerAsync(
        Domain.Modules.Ticketing.TicketInteraction interaction, CancellationToken cancellationToken)
    {
        interaction.CancelAwaitingCustomerReply();
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return ChatbotInactivityCloseOutcome.LostRace;
        }

        return ChatbotInactivityCloseOutcome.Excluded;
    }
}
