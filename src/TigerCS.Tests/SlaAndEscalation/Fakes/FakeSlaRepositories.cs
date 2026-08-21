using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Seed;

namespace TigerCS.Tests.SlaAndEscalation.Fakes;

/// <summary>
/// Serves the same approved policies a real deployment is seeded with
/// (<see cref="SlaReferenceData.Policies"/>), so a unit test can never assert
/// against target values that differ from production's.
/// </summary>
public sealed class FakeSlaPolicyRepository : ISlaPolicyRepository
{
    private readonly Dictionary<byte, SlaPolicy> _policies =
        SlaReferenceData.Policies().ToDictionary(p => p.PriorityId);

    /// <summary>Replaces one tier's policy, for tests that need a target the approved set does not contain.</summary>
    public void Override(SlaPolicy policy) => _policies[policy.PriorityId] = policy;

    /// <summary>Removes every policy, to exercise the "no policy seeded" failure path.</summary>
    public void Clear() => _policies.Clear();

    public Task<SlaPolicy?> GetByPriorityIdAsync(byte priorityId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_policies.GetValueOrDefault(priorityId));
}

/// <summary>Serves the approved default calendar (ISSUE-017 Option A) unless a test supplies another.</summary>
public sealed class FakeBusinessCalendarRepository : IBusinessCalendarRepository
{
    private BusinessCalendarSnapshot? _snapshot = SlaTestCalendar.Default();

    public void Use(BusinessCalendarSnapshot? snapshot) => _snapshot = snapshot;

    public Task<BusinessCalendarSnapshot?> GetActiveSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_snapshot);
}

public sealed class FakeTicketSlaInstanceRepository : ITicketSlaInstanceRepository
{
    private readonly List<TicketSlaInstance> _instances = [];
    private long _nextId = 1;

    public IReadOnlyList<TicketSlaInstance> All => _instances;

    public Task<TicketSlaInstance?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_instances.FirstOrDefault(i => i.TicketId == ticketId && i.PeriodEndAtUtc is null));

    public Task<IReadOnlyList<TicketSlaInstance>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketSlaInstance>>(
            _instances.Where(i => i.TicketId == ticketId).OrderBy(i => i.PeriodStartAtUtc).ToList());

    public Task AddAsync(TicketSlaInstance instance, CancellationToken cancellationToken = default)
    {
        typeof(TicketSlaInstance).GetProperty(nameof(TicketSlaInstance.TicketSlaInstanceId))!.SetValue(instance, _nextId++);
        _instances.Add(instance);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<long>> ListTicketIdsWithDeadlineDueAsync(
        SlaDeadlineType deadlineType, DateTime asOfUtc, int maxResults, CancellationToken cancellationToken = default)
    {
        var due = _instances
            .Where(i => i.PeriodEndAtUtc is null
                        && !i.IsBreached(deadlineType)
                        && i.DueAtUtcFor(deadlineType) <= asOfUtc)
            .OrderBy(i => i.TicketSlaInstanceId)
            .Take(maxResults)
            .Select(i => i.TicketId)
            .ToList();

        return Task.FromResult<IReadOnlyList<long>>(due);
    }
}

public sealed class FakeTicketEscalationRepository : ITicketEscalationRepository
{
    private readonly List<TicketEscalation> _escalations = [];
    private long _nextId = 1;

    public IReadOnlyList<TicketEscalation> All => _escalations;

    public Task AddAsync(TicketEscalation escalation, CancellationToken cancellationToken = default)
    {
        typeof(TicketEscalation).GetProperty(nameof(TicketEscalation.TicketEscalationId))!.SetValue(escalation, _nextId++);
        _escalations.Add(escalation);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TicketEscalation>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketEscalation>>(
            _escalations.Where(e => e.TicketId == ticketId).OrderByDescending(e => e.RaisedAtUtc).ToList());

    public Task<bool> HasAutomaticBreachEscalationAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_escalations.Any(e => e.TicketId == ticketId && e.TriggerType == EscalationTriggerType.AutoBreach));
}

/// <summary>
/// In-memory stand-in for <c>IdempotencyRecords</c>, with the same
/// claim-once semantics the unique index gives the real store.
/// </summary>
public sealed class FakeIdempotencyRecordStore : IIdempotencyRecordStore
{
    private readonly HashSet<string> _claimed = [];

    /// <summary>Every key claimed so far — lets a test assert the exact composition of SLA-Architecture.md §15's key.</summary>
    public IReadOnlyCollection<string> ClaimedKeys => _claimed;

    /// <summary>How many times a key was offered, claimed or not, so a test can prove the second attempt really reached the store.</summary>
    public int ReserveAttempts { get; private set; }

    public Task<bool> TryReserveAsync(
        string idempotencyKey, string scope, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ReserveAttempts++;
        return Task.FromResult(_claimed.Add(idempotencyKey));
    }
}

/// <summary>Records what the scheduled-job path was asked to enqueue (SLA-Architecture.md §13), without needing Hangfire.</summary>
public sealed class FakeSlaDeadlineScheduler : ISlaDeadlineScheduler
{
    public List<(long TicketId, SlaDeadlineType DeadlineType, DateTime DueAtUtc)> Scheduled { get; } = [];

    public void ScheduleDeadlineCheck(long ticketId, SlaDeadlineType deadlineType, DateTime dueAtUtc) =>
        Scheduled.Add((ticketId, deadlineType, dueAtUtc));
}

/// <summary>The approved pilot calendar, built once for tests that do not need a custom one.</summary>
public static class SlaTestCalendar
{
    /// <summary>Asia/Dubai — UTC+04:00 year-round (no daylight saving), which is what makes the UTC arithmetic in these tests exact.</summary>
    public static TimeZoneInfo Dubai => TimeZoneInfo.FindSystemTimeZoneById(SlaReferenceData.DefaultTimeZoneId);

    /// <summary>ISSUE-017 Option A: Saturday–Thursday, 08:00–18:00, Friday off, no holidays.</summary>
    public static BusinessCalendarSnapshot Default(params DateOnly[] holidays) =>
        new(Dubai,
            SlaReferenceData.DefaultBusinessDayStartLocal,
            SlaReferenceData.DefaultBusinessDayEndLocal,
            SlaReferenceData.DefaultWorkingDays,
            holidays);
}
