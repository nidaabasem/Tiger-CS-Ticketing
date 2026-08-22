using TigerCS.Application.Modules.Notifications;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// Configuration for ADR-0013's Outbox dispatcher, bound from the
/// <c>Notifications:Outbox</c> section. Sits beside
/// <see cref="BackgroundJobOptions"/> in Infrastructure for the same reason:
/// configuration binding belongs to the composition root, not to the
/// Application layer, which depends on nothing but the Domain.
/// </summary>
public sealed class OutboxDispatchOptions
{
    public const string SectionName = "Notifications:Outbox";

    /// <summary>See <see cref="OutboxDispatchPolicy.MaxAttempts"/> for why the default is 5 and where that number comes from.</summary>
    public int MaxAttempts { get; set; } = OutboxDispatchPolicy.Default.MaxAttempts;

    /// <summary>See <see cref="OutboxDispatchPolicy.BaseRetryDelay"/>.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = OutboxDispatchPolicy.Default.BaseRetryDelay;

    /// <summary>See <see cref="OutboxDispatchPolicy.BatchSize"/>.</summary>
    public int BatchSize { get; set; } = OutboxDispatchPolicy.Default.BatchSize;

    /// <summary>
    /// How often the recurring dispatcher runs — ADR-0015's "a recurring job
    /// for the Outbox dispatcher". Clamped at registration, since a
    /// misconfigured interval would silently delay every customer-facing
    /// acknowledgement in the system.
    /// </summary>
    public int PollIntervalMinutes { get; set; } = 1;

    /// <summary>Projects the configured values into the Application layer's plain policy value, clamped so a misconfiguration cannot disable the safety properties.</summary>
    public OutboxDispatchPolicy ToPolicy() => new(
        // At least one attempt, or nothing would ever be dispatched at all;
        // capped so a typo cannot turn "retry" into "retry approximately
        // forever" and defeat the dead-letter path FR-NOT-05 requires.
        MaxAttempts: Math.Clamp(MaxAttempts, 1, 20),
        BaseRetryDelay: BaseRetryDelay < TimeSpan.Zero ? OutboxDispatchPolicy.Default.BaseRetryDelay : BaseRetryDelay,
        BatchSize: Math.Clamp(BatchSize, 1, 500));
}
