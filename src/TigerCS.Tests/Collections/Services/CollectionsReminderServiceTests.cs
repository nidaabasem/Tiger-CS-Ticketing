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
/// Candidates, the queue and dispatch against a fixed Dubai business date
/// (2 October 2026 — the OverdueMonthly window is open), with in-memory fakes.
/// </summary>
public class CollectionsReminderServiceTests
{
    private static readonly DateTime WindowDayUtc = new(2026, 10, 2, 6, 0, 0, DateTimeKind.Utc);

    internal sealed class Fixture
    {
        public CollectionsOptions Options { get; } = new()
        {
            Enabled = true,
            Channels = { EmailEnabled = true, SmsEnabled = true, VoiceBotEnabled = true },
        };

        public FakeTimeProvider Time { get; }
        public CollectionsClock Clock { get; }
        public MutableSource Source { get; }
        public InMemoryReminders Reminders { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; }
        public RecordingOutbox Outbox { get; } = new();
        public RecordingAudit Audit { get; } = new();
        public CollectionsReminderAppService Service { get; }
        public CollectionsCaller Supervisor { get; } = new(Guid.NewGuid(), [Roles.CsSupervisor], []);
        public CollectionsCaller Integration { get; }

        public Fixture(DateTime utcNow)
        {
            Time = new FakeTimeProvider(utcNow);
            Clock = new CollectionsClock(Options, Time);
            Source = new MutableSource(FixtureCollectionsFinancialSource.All(Clock.BusinessDate, utcNow).ToList());
            UnitOfWork = new FakeUnitOfWork(Reminders);
            Integration = new CollectionsCaller(Guid.NewGuid(), [Roles.CsAgent], []);
            Options.Authorization.IntegrationEmployeeIds.Add(Integration.EmployeeId);
            Service = new CollectionsReminderAppService(
                Options, new CollectionsAuthorizationService(Options, new FakeDepartmentRepository()), Clock, Source, Reminders, UnitOfWork,
                Outbox, Audit, NullLogger<CollectionsReminderAppService>.Instance);
        }

        public CollectionsReminderDispatchHandler Dispatcher(IReminderDeliveryProvider? provider = null) =>
            new(Reminders, Source, provider is null ? [] : [provider], Clock);

        public async Task<CollectionsReminderCandidateDto> CandidateAsync(string accountId = "ACC-9001-1204") =>
            (await Service.ListCandidatesAsync(Supervisor, "OverdueMonthly", null, 9001, accountId, null, null)).Value!.Items.Single();

        public Task<CollectionsResult<CollectionsReminderJobDto>> QueueAsync(
            CollectionsReminderCandidateDto candidate, string? key = null, CollectionsCaller? caller = null, params string[] channels) =>
            Service.QueueAsync(caller ?? Supervisor, new QueueCollectionsReminderRequestDto(candidate.CandidateId, channels.Length == 0 ? ["Sms", "Email"] : channels, "en"), key);
    }

    // ------------------------------------------------------------------
    // Candidates
    // ------------------------------------------------------------------

    [Fact]
    public async Task Candidates_QuoteQualifyingPrincipalOnly_WithBasisInstalmentsAndExpiry_AndNoContactDetails()
    {
        var f = new Fixture(WindowDayUtc);

        var result = await f.Service.ListCandidatesAsync(f.Supervisor, "OverdueMonthly", new DateOnly(2026, 10, 2), null, null, null, null);

        Assert.Equal(CollectionsOutcome.Success, result.Outcome);
        var body = result.Value!;
        Assert.Equal("2026-10:OverdueMonthly", body.CycleKey);
        Assert.Equal("Asia/Dubai", body.TimeZone);
        Assert.True(body.WindowOpen);

        // ACC-9001-1204: Jul 10 paid; Aug 10 has 4,000 left and is before 2 Sep. Sep 10 is overdue but not by a month.
        var candidate = Assert.Single(body.Items, c => c.AccountId == "ACC-9001-1204");
        Assert.Equal(4_000m, candidate.ReminderAmount);
        Assert.Equal(["INS-1204-02"], candidate.InstalmentIds);
        Assert.Equal("UnpaidPrincipalOlderThanOneCalendarMonth", candidate.AmountBasis);
        Assert.Equal(["VoiceBot", "Sms", "Email"], candidate.AvailableChannels);
        Assert.Equal(WindowDayUtc.AddMinutes(15), candidate.ExpiresAtUtc);
        Assert.DoesNotContain(body.Items, c => c.AccountId is "ACC-9001-0805" or "ACC-9002-0310" or "ACC-9001-1204-P");
    }

    [Fact]
    public async Task Candidates_RequireAReminderTypeAndTodaysBusinessDate_AndAClosedWindowListsNothing()
    {
        var f = new Fixture(WindowDayUtc);

        Assert.Equal(CollectionsOutcome.InvalidRequest, (await f.Service.ListCandidatesAsync(f.Supervisor, null, null, null, null, null, null)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest,
            (await f.Service.ListCandidatesAsync(f.Supervisor, "OverdueMonthly", new DateOnly(2026, 10, 3), null, null, null, null)).Outcome);

        var closed = (await f.Service.ListCandidatesAsync(f.Supervisor, "CurrentMonth", null, null, null, null, null)).Value!;
        Assert.False(closed.WindowOpen);
        Assert.Empty(closed.Items);
    }

    [Fact]
    public async Task Candidates_SkipStaleReads()
    {
        var f = new Fixture(WindowDayUtc);
        f.Source.Replace("ACC-9001-1204", a => a with { AsOfUtc = WindowDayUtc.AddHours(-3) });

        Assert.Empty((await f.Service.ListCandidatesAsync(f.Supervisor, "OverdueMonthly", null, null, null, null, null)).Value!.Items);
    }

    // ------------------------------------------------------------------
    // Queue
    // ------------------------------------------------------------------

    [Fact]
    public async Task Queue_PersistsTheQuotedAmount_PerChannel_AndQueuesTigerCsChannelsInTheOutbox()
    {
        var f = new Fixture(WindowDayUtc);
        var result = await f.QueueAsync(await f.CandidateAsync(), "key-1");

        Assert.Equal(CollectionsOutcome.Accepted, result.Outcome);
        var job = result.Value!;
        Assert.StartsWith("REM-", job.ReminderId, StringComparison.Ordinal);
        Assert.Equal(4_000m, job.ReminderAmount);
        Assert.Equal("Queued", job.Status);
        Assert.Equal(["Sms", "Email"], job.Channels.Select(c => c.Channel));
        Assert.Equal(2, f.Outbox.Messages.Count);
        Assert.Equal(2, f.Audit.EntityIds.Count);
    }

    [Fact]
    public async Task Queue_ReplaysTheSameKeyAndBody_AndRefusesTheSameKeyWithADifferentBody()
    {
        var f = new Fixture(WindowDayUtc);
        var candidate = await f.CandidateAsync();
        var first = await f.QueueAsync(candidate, "key-1", null, "Sms");

        var replay = await f.QueueAsync(candidate, "key-1", null, "Sms");
        Assert.Equal(CollectionsOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Value!.ReminderId, replay.Value!.ReminderId);

        var conflict = await f.QueueAsync(candidate, "key-1", null, "Email");
        Assert.Equal(CollectionsOutcome.IdempotencyConflict, conflict.Outcome);
        Assert.Single(f.Reminders.Committed);
    }

    [Fact]
    public async Task ADifferentKey_CannotBypassCycleDeduplication()
    {
        var f = new Fixture(WindowDayUtc);
        var candidate = await f.CandidateAsync();
        await f.QueueAsync(candidate, "key-1", null, "Sms");

        var again = await f.QueueAsync(candidate, "key-2", null, "Sms");
        Assert.Equal(CollectionsOutcome.CandidateChanged, again.Outcome);
        Assert.Equal(["VoiceBot", "Email"], again.Replacement!.AvailableChannels);

        Assert.Equal(CollectionsOutcome.Accepted, (await f.QueueAsync(candidate, "key-3", null, "Email")).Outcome);
        Assert.Equal(2, f.Reminders.Committed.Count);
    }

    [Fact]
    public async Task APaymentPostedAfterTheCandidate_IsCandidateChanged_WithAReplacement()
    {
        var f = new Fixture(WindowDayUtc);
        var candidate = await f.CandidateAsync();
        f.Source.Replace("ACC-9001-1204", a => a with
        {
            Instalments = a.Instalments.Select(i => i.InstalmentId == "INS-1204-02" ? i with { RemainingAmount = 1_000m } : i).ToList(),
            ReportedOutstandingPrincipal = a.ReportedOutstandingPrincipal - 3_000m
        });

        var result = await f.QueueAsync(candidate);

        Assert.Equal(CollectionsOutcome.CandidateChanged, result.Outcome);
        Assert.Equal(1_000m, result.Replacement!.ReminderAmount);
        Assert.Empty(f.Reminders.Committed);
    }

    [Fact]
    public async Task ASettledAccount_IsSuppressed_NeverQueued()
    {
        var f = new Fixture(WindowDayUtc);
        var candidate = await f.CandidateAsync();
        f.Source.Replace("ACC-9001-1204", a => a with
        {
            Instalments = a.Instalments.Select(i => i with { RemainingAmount = 0m }).ToList(),
            ReportedOutstandingPrincipal = 0m
        });

        var result = await f.QueueAsync(candidate);
        Assert.Equal(CollectionsOutcome.CandidateChanged, result.Outcome);
        Assert.Contains("settled", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Replacement);
    }

    [Fact]
    public async Task AnExpiredCandidate_IsRefused_WithAFreshReplacement()
    {
        var f = new Fixture(WindowDayUtc);
        var candidate = await f.CandidateAsync();
        f.Time.Advance(TimeSpan.FromMinutes(16));
        f.Source.Replace("ACC-9001-1204", a => a with { AsOfUtc = WindowDayUtc.AddMinutes(16) });

        var result = await f.QueueAsync(candidate);
        Assert.Equal(CollectionsOutcome.CandidateChanged, result.Outcome);
        Assert.True(result.Replacement!.ExpiresAtUtc > candidate.ExpiresAtUtc);
    }

    [Fact]
    public async Task AStaleReadNeverAuthorizesASend()
    {
        var f = new Fixture(WindowDayUtc);
        var candidate = await f.CandidateAsync();
        f.Source.Replace("ACC-9001-1204", a => a with { AsOfUtc = WindowDayUtc.AddHours(-2) });

        Assert.Equal(CollectionsOutcome.FinanceUnavailable, (await f.QueueAsync(candidate)).Outcome);
    }

    [Fact]
    public async Task ATamperedCandidate_IsRefused()
    {
        var f = new Fixture(WindowDayUtc);
        var real = await f.CandidateAsync();
        Assert.True(CollectionsCandidate.TryDecode(real.CandidateId, out var decoded));
        var tampered = real with { CandidateId = (decoded! with { Amount = 1m }).Encode() };

        Assert.Equal(CollectionsOutcome.CandidateChanged, (await f.QueueAsync(tampered)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest,
            (await f.Service.QueueAsync(f.Supervisor, new QueueCollectionsReminderRequestDto("CAND-not-base64!", ["Sms"]), null)).Outcome);
    }

    [Fact]
    public async Task VoiceBot_IsQueuedOnlyByTheIntegrationAccount()
    {
        var f = new Fixture(WindowDayUtc);
        var candidate = await f.CandidateAsync();

        Assert.Equal(CollectionsOutcome.Forbidden, (await f.QueueAsync(candidate, null, f.Supervisor, "VoiceBot")).Outcome);
        Assert.Equal(CollectionsOutcome.Accepted, (await f.QueueAsync(candidate, null, f.Integration, "VoiceBot")).Outcome);
        Assert.Empty(f.Outbox.Messages); // Genesys dials; TigerCS dispatches nothing for voice
    }

    [Fact]
    public async Task NoApprovedContact_IsNoEligibleContact()
    {
        var f = new Fixture(WindowDayUtc);
        f.Source.Replace("ACC-9001-1204", a => a with { CustomerEmail = null });
        var candidate = await f.CandidateAsync();
        Assert.DoesNotContain("Email", candidate.AvailableChannels);

        Assert.Equal(CollectionsOutcome.NoEligibleContact, (await f.QueueAsync(candidate, null, null, "Email")).Outcome);
    }

    [Fact]
    public async Task AFailedChannel_IsRetriedOnItsOwnJob_WithinItsAttemptBudget()
    {
        var f = new Fixture(WindowDayUtc);
        var job = (await f.QueueAsync(await f.CandidateAsync(), null, null, "Sms")).Value!;
        f.Reminders.Committed.Single().ChannelFor(ReminderChannel.Sms)!.Apply(ChannelStatus.Failed, WindowDayUtc.AddMinutes(1), null, "bounced");

        var retry = await f.QueueAsync(await f.CandidateAsync(), null, null, "Sms");
        Assert.Equal(CollectionsOutcome.Accepted, retry.Outcome);
        Assert.Equal(job.ReminderId, retry.Value!.ReminderId);
        Assert.Equal(2, retry.Value.Channels.Single().Attempts);
        Assert.Single(f.Reminders.Committed);

        f.Options.MaxDeliveryAttempts = 2;
        f.Reminders.Committed.Single().ChannelFor(ReminderChannel.Sms)!.Apply(ChannelStatus.Failed, WindowDayUtc.AddMinutes(2), null, "bounced");
        Assert.DoesNotContain("Sms", (await f.CandidateAsync()).AvailableChannels);
    }

    [Fact]
    public async Task AConcurrentDuplicate_LosesAtTheUniqueKey()
    {
        var f = new Fixture(WindowDayUtc);
        f.UnitOfWork.FailNextSaveAsDuplicate = true;

        Assert.Equal(CollectionsOutcome.CandidateChanged, (await f.QueueAsync(await f.CandidateAsync(), null, null, "Sms")).Outcome);
        Assert.Empty(f.Reminders.Committed);
    }

    [Fact]
    public async Task FinanceUnavailable_IsNeverSettled()
    {
        var f = new Fixture(WindowDayUtc);
        var candidate = await f.CandidateAsync();
        f.Source.Down = true;

        Assert.Equal(CollectionsOutcome.FinanceUnavailable, (await f.QueueAsync(candidate)).Outcome);
        Assert.Equal(CollectionsOutcome.FinanceUnavailable,
            (await f.Service.ListCandidatesAsync(f.Supervisor, "OverdueMonthly", null, null, null, null, null)).Outcome);
    }

    [Fact]
    public async Task ALongAccountId_StillFitsTheAuditEntityIdColumn()
    {
        var f = new Fixture(WindowDayUtc);
        var longId = new string('A', CollectionsReminder.AccountIdMaxLength);
        f.Source.Add(f.Source.Get("ACC-9001-1204") with { AccountId = longId });

        Assert.Equal(CollectionsOutcome.Accepted, (await f.QueueAsync(await f.CandidateAsync(longId), null, null, "Sms")).Outcome);
        Assert.True(f.Reminders.Committed.Single().Channels.Single().DeduplicationKey.Length > 100);
        Assert.True(Assert.Single(f.Audit.EntityIds)!.Length <= 100);
    }

    // ------------------------------------------------------------------
    // The designated scheduler
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scheduler_StaysOff_UnlessTigerCsOwnsTheCycleAndTheRulesAreConfirmed()
    {
        var f = new Fixture(WindowDayUtc);
        f.Options.BusinessRulesConfirmed = true;          // confirmed, but no owner
        Assert.False((await f.Service.RunScheduledAsync()).Ran);

        f.Options.SchedulerOwner = "Genesys";             // Genesys owns the cycle: TigerCS must not also dispatch
        Assert.False((await f.Service.RunScheduledAsync()).Ran);

        f.Options.SchedulerOwner = "TigerCS";
        f.Options.BusinessRulesConfirmed = false;
        Assert.False((await f.Service.RunScheduledAsync()).Ran);
        Assert.Empty(f.Reminders.Committed);
    }

    [Fact]
    public async Task Scheduler_QueuesOncePerCycle_AndNeverTheVoiceBot()
    {
        var f = new Fixture(WindowDayUtc);
        f.Options.SchedulerOwner = "TigerCS";
        f.Options.BusinessRulesConfirmed = true;

        Assert.Equal(1, (await f.Service.RunScheduledAsync()).Queued);
        Assert.Equal(0, (await f.Service.RunScheduledAsync()).Queued);   // a second run the same day
        f.Time.Advance(TimeSpan.FromDays(1));
        f.Source.Replace("ACC-9001-1204", a => a with { AsOfUtc = WindowDayUtc.AddDays(1) });
        Assert.Equal(0, (await f.Service.RunScheduledAsync()).Queued);   // the next day of the same window

        var job = Assert.Single(f.Reminders.Committed);
        Assert.Equal(ReminderTrigger.Scheduled, job.Trigger);
        Assert.DoesNotContain(job.Channels, c => c.Channel == ReminderChannel.VoiceBot);
    }

    // ------------------------------------------------------------------
    // Dispatch revalidation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Dispatch_SuppressesAnAccountSettledSinceQueueing()
    {
        var f = new Fixture(WindowDayUtc);
        await f.QueueAsync(await f.CandidateAsync(), null, null, "Email");
        f.Source.Replace("ACC-9001-1204", a => a with
        {
            Instalments = a.Instalments.Select(i => i with { RemainingAmount = 0m }).ToList(),
            ReportedOutstandingPrincipal = 0m
        });
        var provider = new RecordingProvider();

        await f.Dispatcher(provider).HandleAsync(f.Outbox.Messages.Single());

        Assert.Empty(provider.Sent);
        var channel = f.Reminders.Committed.Single().Channels.Single();
        Assert.Equal(ChannelStatus.Suppressed, channel.Status);
        Assert.Equal("Settled before dispatch.", channel.StatusReason);
    }

    [Fact]
    public async Task Dispatch_SendsTheRevalidatedAmount_AndRecordsSentNotDelivered()
    {
        var f = new Fixture(WindowDayUtc);
        await f.QueueAsync(await f.CandidateAsync(), null, null, "Email");
        f.Source.Replace("ACC-9001-1204", a => a with
        {
            Instalments = a.Instalments.Select(i => i.InstalmentId == "INS-1204-02" ? i with { RemainingAmount = 1_500m } : i).ToList(),
            ReportedOutstandingPrincipal = a.ReportedOutstandingPrincipal - 2_500m
        });
        var provider = new RecordingProvider();

        await f.Dispatcher(provider).HandleAsync(f.Outbox.Messages.Single());

        var job = f.Reminders.Committed.Single();
        Assert.Equal(4_000m, job.Amount);                          // quoted, never rewritten
        Assert.Equal(1_500m, job.Channels.Single().DispatchAmount);
        Assert.Equal(1_500m, Assert.Single(provider.Sent).Amount);
        Assert.Equal(ChannelStatus.Sent, job.Channels.Single().Status);
    }

    [Fact]
    public async Task Dispatch_WaitsForTheSource_AndFailsClosedWithoutAProvider()
    {
        var f = new Fixture(WindowDayUtc);
        await f.QueueAsync(await f.CandidateAsync(), null, null, "Sms");
        f.Source.Down = true;
        Assert.Equal(OutboxHandlingOutcome.TransientFailure, (await f.Dispatcher().HandleAsync(f.Outbox.Messages.Single())).Outcome);

        f.Source.Down = false;
        await f.Dispatcher(provider: null).HandleAsync(f.Outbox.Messages.Single());
        var channel = f.Reminders.Committed.Single().Channels.Single();
        Assert.Equal(ChannelStatus.Failed, channel.Status);
        Assert.Contains("No approved delivery provider", channel.StatusReason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Fakes
    // ------------------------------------------------------------------

    internal sealed class MutableSource(List<FinancialAccountSnapshot> accounts) : ICollectionsFinancialSource
    {
        public bool Down { get; set; }
        public string SourceName => "Test source";

        public FinancialAccountSnapshot Get(string accountId) => accounts.Single(a => a.AccountId == accountId);

        public void Add(FinancialAccountSnapshot account) => accounts.Add(account);

        public void Replace(string accountId, Func<FinancialAccountSnapshot, FinancialAccountSnapshot> change)
        {
            var index = accounts.FindIndex(a => a.AccountId == accountId);
            accounts[index] = change(accounts[index]);
        }

        public Task<IReadOnlyList<FinancialAccountSnapshot>?> GetCustomerAccountsAsync(long crmCustomerId, CancellationToken cancellationToken = default)
        {
            ThrowIfDown();
            var matches = accounts.Where(a => a.CrmCustomerId == crmCustomerId).ToList();
            return Task.FromResult<IReadOnlyList<FinancialAccountSnapshot>?>(matches.Count == 0 ? null : matches);
        }

        public Task<FinancialAccountPage> ListAccountsWithOutstandingPrincipalAsync(int page, int pageSize, CancellationToken cancellationToken = default)
        {
            ThrowIfDown();
            var open = accounts.Where(a => a.Instalments.Any(i => i.RemainingAmount > 0m)).ToList();
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
            if (reminder.CollectionsReminderId != 0)
            {
                return;
            }

            typeof(CollectionsReminder).GetProperty(nameof(CollectionsReminder.CollectionsReminderId), BindingFlags.Public | BindingFlags.Instance)!
                .SetValue(reminder, _nextId++);
            foreach (var channel in reminder.Channels)
            {
                typeof(CollectionsReminderChannel).GetProperty(nameof(CollectionsReminderChannel.CollectionsReminderId))!
                    .SetValue(channel, reminder.CollectionsReminderId);
            }

            Committed.Add(reminder);
        }

        public Task<CollectionsReminder?> GetByIdAsync(long reminderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Committed.FirstOrDefault(r => r.CollectionsReminderId == reminderId));

        public Task<CollectionsReminder?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Committed.FirstOrDefault(r => r.IdempotencyKey == idempotencyKey));

        public Task<IReadOnlyList<CollectionsReminderChannel>> GetChannelsByDeduplicationKeysAsync(
            IReadOnlyCollection<string> deduplicationKeys, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CollectionsReminderChannel>>(
                Committed.SelectMany(r => r.Channels).Where(c => deduplicationKeys.Contains(c.DeduplicationKey)).ToList());

        public Task AddAsync(CollectionsReminder reminder, CancellationToken cancellationToken = default)
        {
            Pending.Add(reminder);
            return Task.CompletedTask;
        }

        public Task<(IReadOnlyList<CollectionsReminder> Items, bool HasMore)> ListForCustomerAsync(
            long crmCustomerId, string? accountId, int offset, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<CollectionsReminder>, bool)>((Committed.Where(r => r.CrmCustomerId == crmCustomerId).ToList(), false));
    }

    internal sealed class FakeUnitOfWork(InMemoryReminders reminders) : ICollectionsUnitOfWork
    {
        public bool FailNextSaveAsDuplicate { get; set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (FailNextSaveAsDuplicate && reminders.Pending.Count > 0)
            {
                FailNextSaveAsDuplicate = false;
                throw new DuplicateWriteException(new InvalidOperationException("UX_CollectionsReminderChannels_DeduplicationKey"));
            }

            reminders.Pending.ForEach(reminders.Commit);
            reminders.Pending.Clear();
            return Task.CompletedTask;
        }

        public void DiscardPendingChanges() => reminders.Pending.Clear();
    }

    internal sealed class RecordingOutbox : IOutboxWriter
    {
        private readonly HashSet<string> _keys = [];
        public List<OutboxMessage> Messages { get; } = [];

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

    internal sealed class RecordingAudit : IAuditEntryWriter
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
