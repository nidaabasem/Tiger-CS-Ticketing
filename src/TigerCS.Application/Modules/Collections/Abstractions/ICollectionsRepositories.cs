using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Abstractions;

public interface ICollectionsReminderRepository
{
    /// <summary>The reminder with its events.</summary>
    Task<CollectionsReminder?> GetByIdAsync(long reminderId, CancellationToken cancellationToken = default);

    Task<CollectionsReminder?> GetByDeduplicationKeyAsync(string deduplicationKey, CancellationToken cancellationToken = default);

    /// <summary>Which of these de-duplication keys already have a reminder.</summary>
    Task<IReadOnlySet<string>> GetExistingDeduplicationKeysAsync(IReadOnlyCollection<string> deduplicationKeys, CancellationToken cancellationToken = default);

    Task AddAsync(CollectionsReminder reminder, CancellationToken cancellationToken = default);

    /// <summary>A customer's reminders, newest first, with their events.</summary>
    Task<(IReadOnlyList<CollectionsReminder> Items, int TotalCount)> ListForCustomerAsync(
        string crmCustomerId, string? accountId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>One event with its reminder.</summary>
    Task<CollectionsReminderEvent?> GetEventAsync(long reminderEventId, CancellationToken cancellationToken = default);
}

public interface ICollectionsUnitOfWork
{
    /// <exception cref="CustomerVerification.Abstractions.DuplicateWriteException">A unique index (reminder de-duplication key, or reminder + event id) was violated by a concurrent write.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets unsaved changes after a lost race, so the winner can be read back cleanly.</summary>
    void DiscardPendingChanges();
}

/// <summary>
/// A channel TigerCS itself sends through (SMS, email). The voice bot is not
/// one: Genesys places those calls and reports back, so TigerCS never dials.
/// </summary>
public interface IReminderDeliveryProvider
{
    ReminderChannel Channel { get; }

    Task<ReminderDeliveryResult> SendAsync(ReminderDeliveryRequest request, CancellationToken cancellationToken = default);
}

public sealed record ReminderDeliveryRequest(
    long ReminderId,
    ReminderType Type,
    string? CustomerName,
    string? Phone,
    string? Email,
    decimal Amount,
    string Currency,
    string? UnitNumber,
    string? ProjectName,
    Guid CorrelationId);

public enum ReminderDeliveryOutcome
{
    /// <summary>The provider accepted the message. That is "Sent" — delivery is only ever reported by the provider afterwards.</summary>
    Accepted = 1,

    TransientFailure = 2,

    PermanentFailure = 3
}

public sealed record ReminderDeliveryResult(ReminderDeliveryOutcome Outcome, string? ProviderReference = null, string? Error = null);
