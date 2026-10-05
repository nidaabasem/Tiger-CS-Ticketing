using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Abstractions;

public interface ICollectionsReminderRepository
{
    /// <summary>The reminder job with its channels and events.</summary>
    Task<CollectionsReminder?> GetByIdAsync(long reminderId, CancellationToken cancellationToken = default);

    Task<CollectionsReminder?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>The channel rows already holding any of these de-duplication keys, with their reminders.</summary>
    Task<IReadOnlyList<CollectionsReminderChannel>> GetChannelsByDeduplicationKeysAsync(
        IReadOnlyCollection<string> deduplicationKeys, CancellationToken cancellationToken = default);

    Task AddAsync(CollectionsReminder reminder, CancellationToken cancellationToken = default);

    /// <summary>A customer's reminders, newest first, with channels and events.</summary>
    Task<(IReadOnlyList<CollectionsReminder> Items, bool HasMore)> ListForCustomerAsync(
        long crmCustomerId, string? accountId, int offset, int take, CancellationToken cancellationToken = default);
}

public interface ICollectionsUnitOfWork
{
    /// <exception cref="CustomerVerification.Abstractions.DuplicateWriteException">A unique index (channel de-duplication key, idempotency key, or reminder + event id) was violated by a concurrent write.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets unsaved changes after a lost race, so the winner can be read back cleanly.</summary>
    void DiscardPendingChanges();
}

/// <summary>
/// A channel TigerCS itself sends through (SMS, email). The voice bot is not
/// one: Genesys places those calls and reports back, so TigerCS never dials.
/// Contact details come from the financial source's approved record — never
/// from a caller.
/// </summary>
public interface IReminderDeliveryProvider
{
    ReminderChannel Channel { get; }

    Task<ReminderDeliveryResult> SendAsync(ReminderDeliveryRequest request, CancellationToken cancellationToken = default);
}

public sealed record ReminderDeliveryRequest(
    string ReminderId,
    ReminderType Type,
    string Language,
    string? CustomerName,
    string? Phone,
    string? Email,
    decimal Amount,
    string Currency,
    string? UnitNumber,
    string? TowerName,
    Guid CorrelationId);

public enum ReminderDeliveryOutcome
{
    /// <summary>The provider accepted the message — "Sent". Delivery is only reported by the provider afterwards.</summary>
    Accepted = 1,

    TransientFailure = 2,

    PermanentFailure = 3
}

public sealed record ReminderDeliveryResult(ReminderDeliveryOutcome Outcome, string? ProviderMessageId = null, string? Error = null);
