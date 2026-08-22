using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;

namespace TigerCS.Tests.Notifications.Fakes;

/// <summary>
/// In-memory <see cref="IOutboxWriter"/> that behaves like the real one in the
/// two ways the tests actually depend on: it stages rather than saves, and it
/// enforces ADR-0014's idempotency key so a duplicate enqueue returns
/// <c>null</c> instead of a second message.
///
/// <para>
/// <see cref="Staged"/> versus <see cref="Committed"/> is what makes the
/// atomicity tests meaningful: a rolled-back unit of work must leave the
/// staged message uncommitted, exactly as a real transaction would.
/// </para>
/// </summary>
public sealed class FakeOutboxWriter : IOutboxWriter
{
    private long _nextIdempotencyRecordId = 1;

    private readonly List<(string Key, OutboxMessage Message)> _staged = [];

    public IReadOnlyList<OutboxMessage> Staged => _staged.Select(e => e.Message).ToList();

    public List<OutboxMessage> Committed { get; } = [];

    public HashSet<string> ReservedKeys { get; } = new(StringComparer.Ordinal);

    public Task<OutboxMessage?> WriteAsync(
        string eventType,
        string payload,
        Guid correlationId,
        string idempotencyKey,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (!ReservedKeys.Add(idempotencyKey))
        {
            return Task.FromResult<OutboxMessage?>(null);
        }

        var record = new IdempotencyRecord(idempotencyKey, IdempotencyScopes.OutboxDispatch, occurredAtUtc);
        typeof(IdempotencyRecord)
            .GetProperty(nameof(IdempotencyRecord.IdempotencyRecordId))!
            .SetValue(record, _nextIdempotencyRecordId++);

        var message = new OutboxMessage(Guid.NewGuid(), eventType, payload, correlationId, record, occurredAtUtc);
        _staged.Add((idempotencyKey, message));
        return Task.FromResult<OutboxMessage?>(message);
    }

    /// <summary>Called by <c>FakeTicketingUnitOfWork</c> on a successful save — the fake equivalent of the rows reaching the database.</summary>
    public void Commit()
    {
        Committed.AddRange(_staged.Select(e => e.Message));
        _staged.Clear();
    }

    /// <summary>
    /// Called when the unit of work fails or its transaction rolls back.
    /// Also releases the reserved keys, matching the real behaviour: the
    /// reservation lives in the same transaction, so a rollback un-reserves
    /// it and a genuine retry can enqueue the event.
    /// </summary>
    public void Rollback()
    {
        foreach (var (key, _) in _staged)
        {
            ReservedKeys.Remove(key);
        }

        _staged.Clear();
    }
}

public sealed class FakeOutboxMessageRepository : IOutboxMessageRepository
{
    public List<OutboxMessage> Messages { get; } = [];

    public Task<IReadOnlyList<OutboxMessage>> GetDispatchableAsync(
        DateTime nowUtc, int maxAttempts, TimeSpan baseRetryDelay, int batchSize, CancellationToken cancellationToken = default)
    {
        // Deliberately mirrors the real repository's eligibility rule rather
        // than returning everything pending — a dispatcher test that ignored
        // backoff would prove nothing about the backoff.
        var eligible = Messages
            .Where(m => m.Status == OutboxMessageStatus.Pending)
            .Where(m => m.Attempts < maxAttempts)
            .Where(m => nowUtc >= OutboxMessage.NextEligibleAtUtc(m.OccurredAtUtc, m.Attempts, baseRetryDelay))
            .OrderBy(m => m.OccurredAtUtc)
            .Take(batchSize)
            .ToList();

        return Task.FromResult<IReadOnlyList<OutboxMessage>>(eligible);
    }

    public Task<OutboxMessage?> GetByIdAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Messages.FirstOrDefault(m => m.OutboxMessageId == outboxMessageId));
}

public sealed class FakeNotificationRepository : INotificationRepository
{
    public List<Notification> Notifications { get; } = [];

    public Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        Notifications.Add(notification);
        return Task.CompletedTask;
    }

    public Task<Notification?> GetByOutboxMessageAsync(
        Guid outboxMessageId, NotificationType notificationType, CancellationToken cancellationToken = default) =>
        Task.FromResult(Notifications.FirstOrDefault(n =>
            n.OutboxMessageId == outboxMessageId && n.NotificationType == notificationType));
}

/// <summary>
/// Unit of work for dispatcher tests.
///
/// <para>
/// <see cref="ClaimsToLose"/> simulates the concurrency-token race a second
/// worker loses, and <see cref="SavesToFail"/> the database failure that can
/// follow a successful send, without needing two real database connections.
/// </para>
///
/// <para>
/// <b>Models EF's reload-on-discard faithfully, which matters.</b> The real
/// <c>DiscardPendingChangesAsync</c> calls <c>ReloadAsync</c>, so an entity
/// mutated in memory before a failed save is reset to its committed values. A
/// fake that merely counted the call would let a test observe a
/// <c>Processed</c> message whose save actually failed — the exact state the
/// production code does not leave behind. <see cref="Track"/> registers the
/// messages whose committed state should be restored.
/// </para>
/// </summary>
public sealed class FakeNotificationsUnitOfWork : INotificationsUnitOfWork
{
    private readonly List<OutboxMessage> _tracked = [];
    private readonly Dictionary<Guid, OutboxMessageSnapshot> _committed = [];

    public int SaveCount { get; private set; }

    public int ClaimCount { get; private set; }

    public int DiscardCount { get; private set; }

    /// <summary>Number of subsequent claim attempts that report a lost race.</summary>
    public int ClaimsToLose { get; set; }

    /// <summary>Number of subsequent outcome saves that throw, simulating a database failure after a send.</summary>
    public int SavesToFail { get; set; }

    /// <summary>Registers messages whose committed state this unit of work should be able to restore on discard.</summary>
    public void Track(IEnumerable<OutboxMessage> messages)
    {
        foreach (var message in messages)
        {
            if (!_tracked.Contains(message))
            {
                _tracked.Add(message);
                _committed[message.OutboxMessageId] = OutboxMessageSnapshot.Of(message);
            }
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;

        if (SavesToFail > 0)
        {
            SavesToFail--;
            throw new InvalidOperationException("Simulated failure persisting the dispatch outcome.");
        }

        Commit();
        return Task.CompletedTask;
    }

    public Task<bool> TrySaveClaimAsync(CancellationToken cancellationToken = default)
    {
        ClaimCount++;

        if (ClaimsToLose > 0)
        {
            ClaimsToLose--;

            // The loser's UPDATE matched zero rows, so its in-memory claim is
            // rolled back to the committed state — what ReloadAsync does.
            Restore();
            return Task.FromResult(false);
        }

        Commit();
        return Task.FromResult(true);
    }

    public Task DiscardPendingChangesAsync(CancellationToken cancellationToken = default)
    {
        DiscardCount++;
        Restore();
        return Task.CompletedTask;
    }

    private void Commit()
    {
        foreach (var message in _tracked)
        {
            _committed[message.OutboxMessageId] = OutboxMessageSnapshot.Of(message);
        }
    }

    private void Restore()
    {
        foreach (var message in _tracked)
        {
            if (_committed.TryGetValue(message.OutboxMessageId, out var snapshot))
            {
                snapshot.RestoreTo(message);
            }
        }
    }

    /// <summary>The four mutable columns of <c>OutboxMessages</c>, captured so a discard can put them back exactly as a reload would.</summary>
    private sealed record OutboxMessageSnapshot(OutboxMessageStatus Status, int Attempts, string? LastError, DateTime? ProcessedAtUtc)
    {
        public static OutboxMessageSnapshot Of(OutboxMessage message) =>
            new(message.Status, message.Attempts, message.LastError, message.ProcessedAtUtc);

        public void RestoreTo(OutboxMessage message)
        {
            Set(message, nameof(OutboxMessage.Status), Status);
            Set(message, nameof(OutboxMessage.Attempts), Attempts);
            Set(message, nameof(OutboxMessage.LastError), LastError);
            Set(message, nameof(OutboxMessage.ProcessedAtUtc), ProcessedAtUtc);
        }

        private static void Set(OutboxMessage message, string property, object? value) =>
            typeof(OutboxMessage).GetProperty(property)!.SetValue(message, value);
    }
}
