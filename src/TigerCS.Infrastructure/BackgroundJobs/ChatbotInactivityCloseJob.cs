using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.GenesysIntegration.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// The recurring server-side job behind "close the ticket when the customer
/// stops answering the chatbot". It only schedules and isolates:
/// <see cref="ChatbotInactivityCloseAppService"/> owns every rule.
///
/// <para>
/// <b>One DI scope (one DbContext) per candidate.</b> A lost concurrency race
/// leaves the loser's tracked entities dirty; sharing a context across
/// candidates would let that poison the next ticket. A failure on one
/// candidate is logged and does not stop the rest — it is retried next run,
/// because the persisted timer is still there. Counts only are logged; no
/// ticket content, customer data or identifiers beyond ids.
/// </para>
/// </summary>
public sealed class ChatbotInactivityCloseJob(IServiceScopeFactory scopeFactory, ILogger<ChatbotInactivityCloseJob> logger)
{
    /// <summary>The Hangfire recurring-job identifier, named once so registration and any later reconfiguration cannot drift apart.</summary>
    public const string RecurringJobId = "chatbot-inactivity-close";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<long> due;
        await using (var listScope = scopeFactory.CreateAsyncScope())
        {
            due = await listScope.ServiceProvider
                .GetRequiredService<ChatbotInactivityCloseAppService>()
                .ListDueAsync(cancellationToken);
        }

        if (due.Count == 0)
        {
            logger.LogDebug("Chatbot inactivity job found no interactions past the timeout.");
            return;
        }

        int closed = 0, excluded = 0, lostRace = 0, notDue = 0, failed = 0;
        foreach (var interactionId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var outcome = await scope.ServiceProvider
                    .GetRequiredService<ChatbotInactivityCloseAppService>()
                    .TryCloseAsync(interactionId, cancellationToken);

                switch (outcome)
                {
                    case ChatbotInactivityCloseOutcome.Closed: closed++; break;
                    case ChatbotInactivityCloseOutcome.Excluded: excluded++; break;
                    case ChatbotInactivityCloseOutcome.LostRace: lostRace++; break;
                    default: notDue++; break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Chatbot inactivity closure failed for interaction {TicketInteractionId}; it will be retried on the next run.", interactionId);
            }
        }

        logger.LogInformation(
            "Chatbot inactivity pass: {Closed} closed, {Excluded} excluded (human follow-up/ended/not closable), {LostRace} lost to a concurrent writer, {NotDue} no longer due, {Failed} failed.",
            closed, excluded, lostRace, notDue, failed);
    }
}
