using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;

namespace TigerCS.Application.Modules.Notifications.Abstractions;

/// <summary>
/// The dispatcher's view of <c>OutboxMessages</c> (ADR-0013). Reads and
/// mutations only — nothing here saves; the unit of work commits.
/// </summary>
public interface IOutboxMessageRepository
{
    /// <summary>
    /// Messages eligible for a dispatch attempt right now: still
    /// <see cref="OutboxMessageStatus.Pending"/>, under the dead-letter
    /// attempt threshold, and past their derived backoff point
    /// (<see cref="OutboxMessage.NextEligibleAtUtc"/>). Oldest first, so a
    /// message can never be starved by newer traffic.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> GetDispatchableAsync(
        DateTime nowUtc, int maxAttempts, TimeSpan baseRetryDelay, int batchSize, CancellationToken cancellationToken = default);

    Task<OutboxMessage?> GetByIdAsync(Guid outboxMessageId, CancellationToken cancellationToken = default);
}

/// <summary>The Notifications module's own table (MVP-Data-Dictionary.md §2.21).</summary>
public interface INotificationRepository
{
    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// The delivery row already recorded for this Outbox message, if any.
    /// A retry finds and reuses it rather than creating a second row for one
    /// logical notification — which is also what the unique index behind it
    /// enforces at the database.
    /// </summary>
    Task<Notification?> GetByOutboxMessageAsync(
        Guid outboxMessageId, NotificationType notificationType, CancellationToken cancellationToken = default);
}

/// <summary>Commits everything added or changed through this module's repositories. Mirrors <c>ITicketingUnitOfWork</c>'s shape.</summary>
public interface INotificationsUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a dispatch claim, reporting whether this caller won it.
    ///
    /// <para>
    /// Separate from <see cref="SaveChangesAsync"/> because losing the claim
    /// race is a routine, expected outcome — two workers polling the same
    /// batch — not an exception the dispatcher should have to catch per
    /// message. <c>false</c> means another worker already owns the message.
    /// </para>
    /// </summary>
    Task<bool> TrySaveClaimAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Abandons everything staged but not yet committed, returning the unit of
    /// work to a clean state.
    ///
    /// <para>
    /// The dispatcher processes a batch through one unit of work, so a
    /// message whose handler threw — or whose outcome failed to save — must
    /// not leave half-written entities behind for the <i>next</i> message's
    /// commit to pick up and persist. Without this, one poisoned message
    /// could corrupt the message after it.
    /// </para>
    /// </summary>
    Task DiscardPendingChangesAsync(CancellationToken cancellationToken = default);
}
