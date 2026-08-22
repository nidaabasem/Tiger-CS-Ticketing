namespace TigerCS.Application.Modules.Notifications;

/// <summary>
/// The retry, dead-letter and batching policy the dispatcher applies
/// (ADR-0013). A plain value, not an <c>IOptions&lt;T&gt;</c>: the
/// Application project deliberately depends on nothing but the Domain, and
/// configuration binding belongs to the composition root. Infrastructure's
/// <c>OutboxDispatchOptions</c> binds the configuration section and projects
/// it into this.
/// </summary>
/// <param name="MaxAttempts">
/// Attempts before a message dead-letters.
///
/// <para>
/// <b>An assumption carried over, not a new decision.</b> No merged document
/// states a threshold for the general Outbox. The only approved number
/// anywhere is MVP-API-Contracts.md §6.1's <c>[ASSUMPTION]</c> 5 failed
/// processing attempts for Genesys events, and
/// MVP-Design-Review-Checklist.md §12 records that the retry/dead-letter
/// policy is "the same mechanism referenced for both Genesys events and
/// general notifications" — so one threshold across both consumers is what
/// that checklist item asks for.
/// </para>
/// </param>
/// <param name="BaseRetryDelay">
/// Base unit of the exponential backoff
/// (<see cref="Domain.Infrastructure.OutboxMessage.NextEligibleAtUtc"/>).
/// At 30 seconds, retries become eligible 30s / 90s / 210s / 450s after the
/// event — about seven minutes of patience before dead-lettering, enough to
/// absorb a brief provider blip without leaving a customer waiting hours for
/// an acknowledgement. No merged document specifies a curve; this is a tuning
/// value, which is why it is configurable rather than fixed.
/// </param>
/// <param name="BatchSize">Messages examined per pass, so a backlog drains steadily instead of monopolising a worker.</param>
public sealed record OutboxDispatchPolicy(int MaxAttempts, TimeSpan BaseRetryDelay, int BatchSize)
{
    public static OutboxDispatchPolicy Default { get; } = new(MaxAttempts: 5, TimeSpan.FromSeconds(30), BatchSize: 50);
}
