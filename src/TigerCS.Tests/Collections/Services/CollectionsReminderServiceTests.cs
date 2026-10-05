using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Integrations.Modules.CollectionsIntegration;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Services;

/// <summary>
/// The reminder service against a fixed business date, so the FAQ windows
/// are deterministic: candidates, the designated scheduler and its gating,
/// revalidation immediately before dispatch, and duplicate prevention under a
/// concurrent write.
/// </summary>
public class CollectionsReminderServiceTests
{
    // 2 Oct 2026, 10:00 in Dubai — inside the days 1–4 overdue window.
    private static readonly DateTime WindowDayUtc = new(2026, 10, 2, 6, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture
    {
        public CollectionsOptions Options { get; } = new()
        {
            Enabled = true,
            Channels = { EmailEnabled = true, VoiceBotEnabled = true, ScheduledChannels = [ReminderChannel.Email, ReminderChannel.VoiceBot] },
        };

        public FakeTimeProvider Time { get; }
        public CollectionsClock Clock { get; }
        public MutableSource Source { get; }
        public InMemoryReminders Reminders { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; }
        public RecordingOutbox Outbox { get; } = new();
        public NoAudit Audit { get; } = new();
        public CollectionsReminderAppService Service { get; }
        public CollectionsCaller Supervisor { get; } = new(Guid.NewGuid(), [Roles.CsSupervisor], []);

        public Fixture(DateTime utcNow)
        {
            Time = new FakeTimeProvider(utcNow);
            Clock = new CollectionsClock(Options, Time);
            Source = new MutableSource(FixtureCollectionsFinancialSource.All(Clock.Today, utcNow).ToList());
            UnitOfWork = new FakeUnitOfWork(Reminders);
            Service = new CollectionsReminderAppService(
                Options, new CollectionsAuthorizationService(Options, new FakeDepartmentRepository()), Clock, Source, Reminders, UnitOfWork,
                Outbox, Audit, NullLogger<CollectionsReminderAppService>.Instance);
        }

        public CollectionsReminderDispatchHandler Dispatcher(IReminderDeliveryProvider? provider = null) =>
            new(Reminders, Source, provider is null ? [] : [provider], Clock);
    }

    [Fact]
    public async Task Candidates_OnTheOverdueWindowDay_ListTheAccountInArrears_WithPendingChannels()
    {
        var f = new Fixture(WindowDayUtc);

        var result = await f.Service.ListCandidatesAsync(f.Supervisor, null, null, 1, 50);

        Assert.Equal(CollectionsOutcome.Success, result.Outcome);
        Assert.Equal(new DateOnly(2026, 10, 2), result.Value!.BusinessDate);
        Assert.Equal(["OverdueMoreThanOneMonth"], result.Value.OpenWindows);
        var candidate = Assert.Single(result.Value.Items);
        Assert.Equal("ACC-9001-1204", candidate.AccountId);
        Assert.Equal("2026-10", candidate.CycleKey);
        Assert.Equal(14_000m, candidate.Amount); // 4,000 partial + 10,000 last month — everything overdue
        Assert.Equal(["VoiceBot", "Email"], candidate.PendingChannels.Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public async Task Candidates_DropAChannelOnceItWasUsedThisCycle()
    {
        var f = new Fixture(WindowDayUtc);
        var created = await f.Service.CreateAsync(f.Supervisor, new CreateCollectionsReminderRequestDto("9001", "ACC-9001-1204", "Email", "OverdueMoreThanOneMonth"));
        Assert.Equal(CollectionsOutcome.Created, created.Outcome);

        var candidate = Assert.Single((await f.Service.ListCandidatesAsync(f.Supervisor, null, null, 1, 50)).Value!.Items);
        Assert.Equal(["VoiceBot"], candidate.PendingChannels);
    }

    [Fact]
    public async Task AScheduledTypeOutsideItsWindow_IsRefused()
    {
        var f = new Fixture(WindowDayUtc);
        var result = await f.Service.CreateAsync(f.Supervisor, new CreateCollectionsReminderRequestDto("9001", "ACC-9001-1204", "Email", "CurrentMonthDue"));
        Assert.Equal(CollectionsOutcome.NotEligible, result.Outcome);
    }

    [Fact]
    public async Task Scheduler_StaysOff_UntilCollectionsConfirmsTheRules()
    {
        var f = new Fixture(WindowDayUtc);
        f.Options.AutomaticSchedulingEnabled = true; // switched on, but not confirmed

        var result = await f.Service.RunScheduledAsync();

        Assert.False(result.Ran);
        Assert.Empty(f.Reminders.Committed);
    }

    [Fact]
    public async Task Scheduler_QueuesOncePerAccountTypeCycleChannel_AndNeverDialsTheVoiceBot()
    {
        var f = new Fixture(WindowDayUtc);
        f.Options.AutomaticSchedulingEnabled = true;
        f.Options.BusinessRulesConfirmed = true;

        var first = await f.Service.RunScheduledAsync();
        Assert.True(first.Ran);
        Assert.Equal(1, first.Created);
        var reminder = Assert.Single(f.Reminders.Committed);
        Assert.Equal(ReminderChannel.Email, reminder.Channel);
        Assert.Equal(ReminderTrigger.Scheduled, reminder.Trigger);
        Assert.Single(f.Outbox.Messages);

        // The same day again (a retry, a second server): nothing new.
        var second = await f.Service.RunScheduledAsync();
        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.AlreadyExisted);

        // The next day of the same window: once-per-window means still nothing new.
        f.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, (await f.Service.RunScheduledAsync()).Created);
        Assert.Single(f.Reminders.Committed);
    }

    [Fact]
    public async Task Scheduler_DailyFrequency_SendsOncePerDay()
    {
        var f = new Fixture(WindowDayUtc);
        f.Options.AutomaticSchedulingEnabled = true;
        f.Options.BusinessRulesConfirmed = true;
        f.Options.Rules.SendFrequency = ReminderSendFrequency.Daily;

        await f.Service.RunScheduledAsync();
        f.Time.Advance(TimeSpan.FromDays(1));
        await f.Service.RunScheduledAsync();
        await f.Service.RunScheduledAsync();

        Assert.Equal(["2026-10-02", "2026-10-03"], f.Reminders.Committed.Select(r => r.CycleKey).Order());
    }

    [Fact]
    public async Task Dispatch_RevalidatesTheBalance_AndSuppressesAnAccountSettledSinceQueueing()
    {
        var f = new Fixture(WindowDayUtc);
        await f.Service.CreateAsync(f.Supervisor, new CreateCollectionsReminderRequestDto("9001", "ACC-9001-1204", "Email"));
        var provider = new RecordingProvider();

        // The customer pays everything before the outbox runs.
        f.Source.Replace("ACC-9001-1204", a => a with
        {
            Instalments = a.Instalments.Select(i => i with { PrincipalOutstanding = 0m }).ToList(),
            ReportedOutstandingPrincipal = 0m
        });

        var handled = await f.Dispatcher(provider).HandleAsync(f.Outbox.Messages.Single());

        Assert.Equal(OutboxHandlingOutcome.Succeeded, handled.Outcome);
        Assert.Empty(provider.Sent);
        var reminder = f.Reminders.Committed.Single();
        Assert.Equal(ReminderStatus.Suppressed, reminder.Status);
        Assert.Equal("Settled before dispatch.", reminder.StatusReason);
    }

    [Fact]
    public async Task Dispatch_SendsTheRevalidatedAmount_AndRecordsSentNotDelivered()
    {
        var f = new Fixture(WindowDayUtc);
        await f.Service.CreateAsync(f.Supervisor, new CreateCollectionsReminderRequestDto("9001", "ACC-9001-1204", "Email"));
        var queued = f.Reminders.Committed.Single().Amount;

        // A partial payment posts before dispatch.
        f.Source.Replace("ACC-9001-1204", a => a with
        {
            Instalments = a.Instalments.Select(i => i.InstalmentId == "INS-1204-03" ? i with { PrincipalOutstanding = 2_500m } : i).ToList(),
            ReportedOutstandingPrincipal = a.ReportedOutstandingPrincipal - 7_500m
        });
        var provider = new RecordingProvider();

        await f.Dispatcher(provider).HandleAsync(f.Outbox.Messages.Single());

        var reminder = f.Reminders.Committed.Single();
        Assert.Equal(ReminderStatus.Sent, reminder.Status);
        Assert.Equal(queued, reminder.Amount);
        Assert.Equal(queued - 7_500m, reminder.DispatchAmount);
        Assert.Equal(queued - 7_500m, Assert.Single(provider.Sent).Amount);
    }

    [Fact]
    public async Task Dispatch_WaitsForTheSource_RatherThanSendingOnStaleFigures()
    {
        var f = new Fixture(WindowDayUtc);
        await f.Service.CreateAsync(f.Supervisor, new CreateCollectionsReminderRequestDto("9001", "ACC-9001-1204", "Email"));
        f.Source.Down = true;
        var provider = new RecordingProvider();

        var handled = await f.Dispatcher(provider).HandleAsync(f.Outbox.Messages.Single());

        Assert.Equal(OutboxHandlingOutcome.TransientFailure, handled.Outcome);
        Assert.Empty(provider.Sent);
        Assert.Equal(ReminderStatus.Queued, f.Reminders.Committed.Single().Status);
    }

    [Fact]
    public async Task Dispatch_WithoutAnApprovedProvider_FailsClosed()
    {
        var f = new Fixture(WindowDayUtc);
        await f.Service.CreateAsync(f.Supervisor, new CreateCollectionsReminderRequestDto("9001", "ACC-9001-1204", "Email"));

        await f.Dispatcher(provider: null).HandleAsync(f.Outbox.Messages.Single());

        var reminder = f.Reminders.Committed.Single();
        Assert.Equal(ReminderStatus.Failed, reminder.Status);
        Assert.Contains("No approved delivery provider", reminder.StatusReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentDuplicate_ResolvesToTheWinningReminder()
    {
        var f = new Fixture(WindowDayUtc);
        f.UnitOfWork.LoseNextRaceTo = winner => f.Reminders.Commit(winner);

        var result = await f.Service.CreateAsync(f.Supervisor, new CreateCollectionsReminderRequestDto("9001", "ACC-9001-1204", "Email"));

        Assert.Equal(CollectionsOutcome.AlreadyExists, result.Outcome);
        Assert.Single(f.Reminders.Committed);
        Assert.Equal(f.Reminders.Committed.Single().CollectionsReminderId, result.Value!.Reminder.ReminderId);
    }

    [Fact]
    public async Task SourceDown_IsReportedAsUnavailable_NeverAsSettled()
    {
        var f = new Fixture(WindowDayUtc);
        f.Source.Down = true;

        Assert.Equal(CollectionsOutcome.SourceUnavailable,
            (await f.Service.CreateAsync(f.Supervisor, new CreateCollectionsReminderRequestDto("9001", "ACC-9001-1204", "Email"))).Outcome);
        Assert.Equal(CollectionsOutcome.SourceUnavailable, (await f.Service.ListCandidatesAsync(f.Supervisor, null, null, 1, 50)).Outcome);
        Assert.Empty(f.Reminders.Committed);
    }

    [Fact]
    public async Task ALongAccountId_StillFitsTheAuditEntityIdColumn()
    {
        var f = new Fixture(WindowDayUtc);
        var longId = new string('A', CollectionsReminder.IdentifierMaxLength);
        var template = (await f.Source.GetCustomerAccountsAsync("9001"))!.First(a => a.AccountId == "ACC-9001-1204");
        f.Source.Add(template with { AccountId = longId });

        var result = await f.Service.CreateAsync(f.Supervisor, new CreateCollectionsReminderRequestDto("9001", longId, "Email", "OverdueMoreThanOneMonth"));

        Assert.Equal(CollectionsOutcome.Created, result.Outcome);
        Assert.True(f.Reminders.Committed.Single().DeduplicationKey.Length > 100);
        var audited = Assert.Single(f.Audit.EntityIds);
        Assert.True(audited!.Length <= 100);
        Assert.StartsWith(longId, audited, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Fakes
    // ------------------------------------------------------------------

    internal sealed class MutableSource(List<FinancialAccountSnapshot> accounts) : ICollectionsFinancialSource
    {
        public bool Down { get; set; }
        public string SourceName => "Test source";

        public void Add(FinancialAccountSnapshot account) => accounts.Add(account);

        public void Replace(string accountId, Func<FinancialAccountSnapshot, FinancialAccountSnapshot> change)
        {
            var index = accounts.FindIndex(a => a.AccountId == accountId);
            accounts[index] = change(accounts[index]);
        }

        public Task<IReadOnlyList<FinancialAccountSnapshot>?> GetCustomerAccountsAsync(string crmCustomerId, CancellationToken cancellationToken = default)
        {
            ThrowIfDown();
            var matches = accounts.Where(a => a.CrmCustomerId == crmCustomerId).ToList();
            return Task.FromResult<IReadOnlyList<FinancialAccountSnapshot>?>(matches.Count == 0 ? null : matches);
        }

        public Task<FinancialAccountPage> ListAccountsWithOutstandingPrincipalAsync(int page, int pageSize, CancellationToken cancellationToken = default)
        {
            ThrowIfDown();
            var open = accounts.Where(a => a.Instalments.Any(i => i.PrincipalOutstanding > 0m)).ToList();
            return Task.FromResult(new FinancialAccountPage(open.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page * pageSize < open.Count));
        }

        private void ThrowIfDown()
        {
            if (Down)
            {
                throw new CollectionsFinancialSourceUnavailableException("down");
            }
        }
    }

    internal sealed class InMemoryReminders : ICollectionsReminderRepository
    {
        private long _nextId = 1;
        public List<CollectionsReminder> Committed { get; } = [];
        public List<CollectionsReminder> Pending { get; } = [];

        public void Commit(CollectionsReminder reminder)
        {
            typeof(CollectionsReminder).GetProperty(nameof(CollectionsReminder.CollectionsReminderId), BindingFlags.Public | BindingFlags.Instance)!
                .SetValue(reminder, _nextId++);
            Committed.Add(reminder);
        }

        public Task<CollectionsReminder?> GetByIdAsync(long reminderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Committed.FirstOrDefault(r => r.CollectionsReminderId == reminderId));

        public Task<CollectionsReminder?> GetByDeduplicationKeyAsync(string deduplicationKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Committed.FirstOrDefault(r => r.DeduplicationKey == deduplicationKey));

        public Task<IReadOnlySet<string>> GetExistingDeduplicationKeysAsync(IReadOnlyCollection<string> deduplicationKeys, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(Committed.Select(r => r.DeduplicationKey).Where(deduplicationKeys.Contains).ToHashSet());

        public Task AddAsync(CollectionsReminder reminder, CancellationToken cancellationToken = default)
        {
            Pending.Add(reminder);
            return Task.CompletedTask;
        }

        public Task<(IReadOnlyList<CollectionsReminder> Items, int TotalCount)> ListForCustomerAsync(
            string crmCustomerId, string? accountId, int page, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<CollectionsReminder>, int)>((Committed.Where(r => r.CrmCustomerId == crmCustomerId).ToList(), Committed.Count));

        public Task<CollectionsReminderEvent?> GetEventAsync(long reminderEventId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CollectionsReminderEvent?>(null);
    }

    internal sealed class FakeUnitOfWork(InMemoryReminders reminders) : ICollectionsUnitOfWork
    {
        /// <summary>Simulates a concurrent writer committing the same de-duplication key first.</summary>
        public Action<CollectionsReminder>? LoseNextRaceTo { get; set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (LoseNextRaceTo is { } race && reminders.Pending.Count > 0)
            {
                LoseNextRaceTo = null;
                var mine = reminders.Pending[0];
                race(new CollectionsReminder(mine.CrmCustomerId, mine.AccountId, mine.CrmUnitId, mine.Type, mine.Channel, mine.CycleKey,
                    mine.Currency, mine.Amount, mine.AmountIncludesFines, mine.SourceAsOfUtc, mine.Trigger, null, mine.CreatedAtUtc));
                throw new DuplicateWriteException(new InvalidOperationException("UX_CollectionsReminders_DeduplicationKey"));
            }

            foreach (var reminder in reminders.Pending)
            {
                reminders.Commit(reminder);
            }

            reminders.Pending.Clear();
            return Task.CompletedTask;
        }

        public void DiscardPendingChanges() => reminders.Pending.Clear();
    }

    internal sealed class RecordingOutbox : IOutboxWriter
    {
        public List<OutboxMessage> Messages { get; } = [];
        private readonly HashSet<string> _keys = [];

        public Task<OutboxMessage?> WriteAsync(string eventType, string payload, Guid correlationId, string idempotencyKey, DateTime occurredAtUtc, CancellationToken cancellationToken = default)
        {
            if (!_keys.Add(idempotencyKey))
            {
                return Task.FromResult<OutboxMessage?>(null);
            }

            var message = new OutboxMessage(Guid.NewGuid(), eventType, payload, correlationId,
                new IdempotencyRecord(idempotencyKey, IdempotencyScopes.OutboxDispatch, occurredAtUtc, null), occurredAtUtc);
            Messages.Add(message);
            return Task.FromResult<OutboxMessage?>(message);
        }
    }

    internal sealed class NoAudit : IAuditEntryWriter
    {
        public List<string?> EntityIds { get; } = [];

        public Task WriteAsync(Guid? actorEmployeeId, string action, string entityType, string? entityId, string? beforeValue, string? afterValue, Guid correlationId, CancellationToken cancellationToken = default)
        {
            EntityIds.Add(entityId);
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingProvider : IReminderDeliveryProvider
    {
        public List<ReminderDeliveryRequest> Sent { get; } = [];
        public ReminderChannel Channel => ReminderChannel.Email;

        public Task<ReminderDeliveryResult> SendAsync(ReminderDeliveryRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(new ReminderDeliveryResult(ReminderDeliveryOutcome.Accepted, "msg-1"));
        }
    }
}
