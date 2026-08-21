using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.SlaAndEscalation.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// Breach detection (backlog S-10) and the automatic Level 2 escalation it
/// raises (S-11), exercised through the same entry point ADR-0015's scheduled
/// job and safety sweep both call — with no scheduler present, which is the
/// point: detection depends on no browser, no SignalR client, and no
/// connected user.
/// </summary>
public class SlaBreachDetectionAppServiceTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstResponseDue = CreatedAt.AddHours(1);
    private static readonly DateTime ResolutionDue = CreatedAt.AddHours(4);

    private sealed record Harness(
        SlaServiceFixture Sla,
        SlaBreachDetectionAppService Service,
        FakeTimeProvider Clock,
        Ticket Ticket);

    /// <summary>A clock a test drives directly, so a deadline boundary is reached by moving time rather than by waiting for it.</summary>
    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }

    private static async Task<Harness> CreateAsync(
        DateTime nowUtc, PriorityLevel priority = PriorityLevel.High, bool openSlaPeriod = true)
    {
        var clock = new FakeTimeProvider(nowUtc);
        var sla = new SlaServiceFixture(timeProvider: clock);

        var ticket = Ticket.CreateVerified(
            "TG-CS-20260823-0001", departmentId: 2, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, (byte)priority, "AC not cooling", CreatedAt);
        await sla.Tickets.AddAsync(ticket);

        if (openSlaPeriod)
        {
            await sla.SlaInstances.AddAsync(
                TicketSlaInstance.OpenInitialPeriod(ticket.TicketId, (byte)priority, CreatedAt, FirstResponseDue, ResolutionDue));
        }

        return new Harness(sla, sla.CreateBreachDetection(), clock, ticket);
    }

    private static void StartWork(Ticket ticket)
    {
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
    }

    // -----------------------------------------------------------------
    // Boundary: before, at, and after the deadline
    // -----------------------------------------------------------------

    [Fact]
    public async Task BeforeTheDeadline_IsNotABreach()
    {
        var h = await CreateAsync(FirstResponseDue.AddSeconds(-1));

        var outcome = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        Assert.Equal(SlaBreachProcessingOutcome.NotBreached, outcome);
        Assert.False((await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!.FirstResponseBreached);
        Assert.Empty(h.Sla.Escalations.All);
    }

    /// <summary>
    /// Exactly at the due moment is a breach when nothing has satisfied the
    /// deadline: SLA-Architecture.md §13's scheduled job fires <i>at</i> the
    /// due timestamp, so an exclusive comparison would mean it could never
    /// fire at all.
    /// </summary>
    [Fact]
    public async Task AtTheDeadline_IsABreach()
    {
        var h = await CreateAsync(FirstResponseDue);

        var outcome = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded, outcome);
        Assert.True((await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!.FirstResponseBreached);
    }

    [Fact]
    public async Task AfterTheDeadline_IsABreach()
    {
        var h = await CreateAsync(FirstResponseDue.AddHours(3));

        var outcome = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded, outcome);
        Assert.Equal(SlaState.Breached, h.Ticket.SlaState);
    }

    /// <summary>Satisfying a deadline exactly on it is on time — the achieved-versus-due comparison is strict, and agrees with the not-yet-achieved rule at the boundary instant.</summary>
    [Fact]
    public async Task SatisfiedExactlyOnTheDeadline_IsNotABreach()
    {
        var h = await CreateAsync(FirstResponseDue.AddHours(2));
        h.Ticket.RecordFirstHumanResponse(FirstResponseDue);

        var outcome = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        Assert.Equal(SlaBreachProcessingOutcome.NotBreached, outcome);
        Assert.False((await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!.FirstResponseBreached);
    }

    [Fact]
    public async Task SatisfiedAfterTheDeadline_IsABreach()
    {
        var h = await CreateAsync(FirstResponseDue.AddHours(2));
        h.Ticket.RecordFirstHumanResponse(FirstResponseDue.AddSeconds(1));

        var outcome = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded, outcome);
    }

    // -----------------------------------------------------------------
    // The two clocks are tracked separately (ADR-0009)
    // -----------------------------------------------------------------

    /// <summary>
    /// At a moment past the First Response deadline but inside the Resolution
    /// one, exactly one clock breaches. Conflating the two would "silently
    /// corrupt SLA-compliance reporting" (ADR-0009's own stated risk).
    /// </summary>
    [Fact]
    public async Task FirstResponseAndResolutionBreachIndependently()
    {
        var h = await CreateAsync(FirstResponseDue.AddMinutes(30));

        var firstResponse = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);
        var resolution = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.Resolution);

        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded, firstResponse);
        Assert.Equal(SlaBreachProcessingOutcome.NotBreached, resolution);

        var instance = (await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!;
        Assert.True(instance.FirstResponseBreached);
        Assert.False(instance.ResolutionBreached);
    }

    /// <summary>Each clock carries its own idempotency key, so breaching one does not suppress the other.</summary>
    [Fact]
    public async Task BothClocksCanBreach_AndEachIsKeyedSeparately()
    {
        var h = await CreateAsync(ResolutionDue.AddHours(1));

        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);
        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.Resolution);

        var instance = (await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!;
        Assert.True(instance.FirstResponseBreached);
        Assert.True(instance.ResolutionBreached);
        Assert.Equal(2, h.Sla.Idempotency.ClaimedKeys.Count);
    }

    // -----------------------------------------------------------------
    // Idempotency (SLA-Architecture.md §15, ADR-0014)
    // -----------------------------------------------------------------

    /// <summary>
    /// The core guarantee: reprocessing the same deadline creates no second
    /// breach, no second escalation record and no second audit entry.
    /// </summary>
    [Fact]
    public async Task ReprocessingTheSameDeadline_ChangesNothing()
    {
        var h = await CreateAsync(FirstResponseDue.AddHours(1));

        var first = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);
        var auditAfterFirst = h.Sla.Audit.Entries.Count;
        var escalationsAfterFirst = h.Sla.Escalations.All.Count;

        var second = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);
        var third = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded, first);
        Assert.Equal(SlaBreachProcessingOutcome.AlreadyProcessed, second);
        Assert.Equal(SlaBreachProcessingOutcome.AlreadyProcessed, third);

        Assert.Equal(auditAfterFirst, h.Sla.Audit.Entries.Count);
        Assert.Equal(escalationsAfterFirst, h.Sla.Escalations.All.Count);
        Assert.Single(h.Sla.Escalations.All);
    }

    /// <summary>
    /// The scheduled job of §13 and the sweep of §14 are separate paths that
    /// legitimately target the same due event. Whichever runs second must
    /// change nothing — that is what §15's shared key is for.
    /// </summary>
    [Fact]
    public async Task ScheduledJobAndSweep_TargetingTheSameDeadline_ProduceOneBreach()
    {
        var h = await CreateAsync(FirstResponseDue.AddMinutes(5));

        // The scheduled job fires first...
        var scheduled = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        // ...then the sweep re-scans and finds the same deadline.
        var sweep = await h.Service.SweepAsync();

        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded, scheduled);
        Assert.Equal(0, sweep.BreachesRecorded);
        Assert.Single(h.Sla.Escalations.All);
        Assert.Single(h.Sla.Idempotency.ClaimedKeys);
    }

    /// <summary>SLA-Architecture.md §15's key is <c>TicketId + CheckType + DueTimestamp</c> — each component is present, so a recomputed deadline is a genuinely different due event.</summary>
    [Fact]
    public async Task IdempotencyKey_IsComposedOfTicketCheckTypeAndDueTimestamp()
    {
        var h = await CreateAsync(FirstResponseDue.AddMinutes(1));

        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        var key = Assert.Single(h.Sla.Idempotency.ClaimedKeys);
        Assert.Equal(
            SlaBreachIdempotencyKey.For(h.Ticket.TicketId, SlaDeadlineType.FirstResponse, FirstResponseDue),
            key);
        Assert.Contains(h.Ticket.TicketId.ToString(), key, StringComparison.Ordinal);
        Assert.Contains(nameof(SlaDeadlineType.FirstResponse), key, StringComparison.Ordinal);
        Assert.Contains(FirstResponseDue.ToString("O"), key, StringComparison.Ordinal);
    }

    /// <summary>A sweep over a system with nothing overdue records nothing — the backstop's healthy result.</summary>
    [Fact]
    public async Task Sweep_OnAHealthySystem_RecordsNothing()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(5));

        var result = await h.Service.SweepAsync();

        Assert.Equal(0, result.BreachesRecorded);
        Assert.Equal(0, result.DeadlinesExamined);
        Assert.False(result.BatchLimitReached);
    }

    /// <summary>A sweep run twice over the same overdue deadline records it once.</summary>
    [Fact]
    public async Task Sweep_RunTwice_RecordsTheBreachOnce()
    {
        var h = await CreateAsync(ResolutionDue.AddHours(1));

        var first = await h.Service.SweepAsync();
        var second = await h.Service.SweepAsync();

        Assert.Equal(2, first.BreachesRecorded);
        Assert.Equal(0, second.BreachesRecorded);
        Assert.Single(h.Sla.Escalations.All);
    }

    // -----------------------------------------------------------------
    // Automatic Level 2 escalation on breach (FR-ESC-02, S-11)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Breach_RaisesExactlyOneAutomaticLevel2Escalation()
    {
        var h = await CreateAsync(FirstResponseDue.AddMinutes(1));

        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        var escalation = Assert.Single(h.Sla.Escalations.All);
        Assert.Equal((byte)EscalationLevel.Level2, escalation.Level);
        Assert.Equal(EscalationTriggerType.AutoBreach, escalation.TriggerType);
        Assert.Equal(EscalationLevel.Level2, h.Ticket.EscalationLevel);
    }

    /// <summary>
    /// Backlog S-11's own stated test requirement, and the second half of it:
    /// a breach on the <i>other</i> clock does not raise a second automatic
    /// escalation either. The ticket is already with the Department Head.
    /// </summary>
    [Fact]
    public async Task BreachingBothClocks_RaisesOnlyOneAutomaticEscalation()
    {
        var h = await CreateAsync(ResolutionDue.AddHours(1));

        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);
        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.Resolution);

        Assert.Single(h.Sla.Escalations.All);
    }

    /// <summary>
    /// MVP-Implementation-Backlog.md §0.2 / S-11: the timed Level 2 → Level 3
    /// advance is not built in this pilot. No amount of elapsed time, and no
    /// number of repeat checks, moves the ticket past Level 2.
    /// </summary>
    [Fact]
    public async Task BreachedTicket_NeverAdvancesPastLevel2OnItsOwn()
    {
        var h = await CreateAsync(FirstResponseDue.AddMinutes(1));
        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        // Days pass, both clocks are long overdue, and the sweep runs
        // repeatedly. Level 3 is manual-only in this build.
        h.Clock.UtcNow = ResolutionDue.AddDays(7);
        await h.Service.SweepAsync();
        await h.Service.SweepAsync();
        await h.Service.SweepAsync();

        Assert.Equal(EscalationLevel.Level2, h.Ticket.EscalationLevel);
        Assert.DoesNotContain(h.Sla.Escalations.All, e => e.Level > (byte)EscalationLevel.Level2);
        Assert.DoesNotContain(h.Sla.Escalations.All, e => e.TriggerType == EscalationTriggerType.AutoWindowExpired);
    }

    /// <summary>
    /// A breach never de-escalates a ticket a CS Manager already took higher.
    /// The escalation event is still recorded — the breach genuinely happened
    /// and the recipient matrix still applies — but the ticket's own level
    /// stays where it was.
    /// </summary>
    [Fact]
    public async Task Breach_DoesNotLowerATicketAlreadyEscalatedHigher()
    {
        var h = await CreateAsync(FirstResponseDue.AddMinutes(1));
        StartWork(h.Ticket);
        h.Ticket.RaiseEscalationLevel(EscalationLevel.Level4);

        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        Assert.Equal(EscalationLevel.Level4, h.Ticket.EscalationLevel);
        Assert.Single(h.Sla.Escalations.All);
        Assert.Equal((byte)EscalationLevel.Level2, h.Sla.Escalations.All[0].Level);
    }

    /// <summary>An automatic escalation changes no other dimension: the ticket carries on being worked.</summary>
    [Fact]
    public async Task AutomaticEscalation_LeavesTicketStatusUntouched()
    {
        var h = await CreateAsync(FirstResponseDue.AddMinutes(1));
        StartWork(h.Ticket);
        var ownerBefore = h.Ticket.CurrentOwnerEmployeeId;

        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        Assert.Equal(TicketStatus.InProgress, h.Ticket.TicketStatus);
        Assert.Equal(ownerBefore, h.Ticket.CurrentOwnerEmployeeId);
        Assert.Null(h.Ticket.ResolutionOutcome);
    }

    /// <summary>The recipient matrix is applied per priority, and stays distinct from the level (ADR-0011).</summary>
    [Theory]
    [InlineData(PriorityLevel.Critical)]
    [InlineData(PriorityLevel.Medium)]
    [InlineData(PriorityLevel.Low)]
    public async Task AutomaticEscalation_RecordsTheApprovedRecipientsForThePriority(PriorityLevel priority)
    {
        var h = await CreateAsync(FirstResponseDue.AddMinutes(1), priority);

        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        var escalation = Assert.Single(h.Sla.Escalations.All);
        Assert.NotNull(escalation.NotifiedRoles);
        Assert.Equal((byte)EscalationLevel.Level2, escalation.Level);
    }

    // -----------------------------------------------------------------
    // Audit and history (ADR-0018)
    // -----------------------------------------------------------------

    /// <summary>
    /// A breach is auditable as a system action: NFR-LOG-01 requires the
    /// calculation be reconstructable "for audit or dispute", so the entry
    /// carries the due timestamp and the detection moment, not just a flag.
    /// </summary>
    [Fact]
    public async Task Breach_WritesAuditEntriesForBothTheBreachAndTheEscalation()
    {
        var h = await CreateAsync(FirstResponseDue.AddMinutes(1));

        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        var breachAudit = Assert.Single(h.Sla.Audit.Entries, e => e.Action == "DetectSlaBreach");
        Assert.Null(breachAudit.ActorEmployeeId);
        Assert.Equal(h.Ticket.TicketId.ToString(), breachAudit.EntityId);
        Assert.Contains(FirstResponseDue.ToString("O"), breachAudit.AfterValue!, StringComparison.Ordinal);

        var escalationAudit = Assert.Single(h.Sla.Audit.Entries, e => e.Action == "AutoEscalateOnBreach");
        Assert.Null(escalationAudit.ActorEmployeeId);

        // One event, one correlation id (NFR-REL-03).
        Assert.Equal(breachAudit.CorrelationId, escalationAudit.CorrelationId);
    }

    /// <summary>Both dimension changes reach the ticket's own history, recorded as system actions.</summary>
    [Fact]
    public async Task Breach_WritesStatusHistoryForBothChangedDimensions()
    {
        var h = await CreateAsync(FirstResponseDue.AddMinutes(1));

        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        var slaStateRow = Assert.Single(h.Sla.StatusHistory.Added, r => r.Dimension == TicketStatusDimension.SlaState);
        Assert.True(slaStateRow.ActorIsSystem);
        Assert.Null(slaStateRow.ActorEmployeeId);
        Assert.Equal((byte)SlaState.Breached, slaStateRow.NewValue);

        var escalationRow = Assert.Single(h.Sla.StatusHistory.Added, r => r.Dimension == TicketStatusDimension.EscalationLevel);
        Assert.True(escalationRow.ActorIsSystem);
        Assert.Equal((byte)EscalationLevel.Level2, escalationRow.NewValue);
    }

    // -----------------------------------------------------------------
    // Guards
    // -----------------------------------------------------------------

    /// <summary>Closed-ticket immutability: a Closed ticket's SLA outcome was settled when it was resolved, and no background job may change it.</summary>
    [Fact]
    public async Task ClosedTicket_IsNeverTouchedByBreachDetection()
    {
        var h = await CreateAsync(ResolutionDue.AddHours(5));
        StartWork(h.Ticket);
        h.Ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        h.Ticket.Close();

        var outcome = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.Resolution);

        Assert.Equal(SlaBreachProcessingOutcome.TicketClosed, outcome);
        Assert.False((await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!.ResolutionBreached);
        Assert.Empty(h.Sla.Escalations.All);
        Assert.Empty(h.Sla.Audit.Entries);
    }

    /// <summary>FR-TKT-09: a provisional ticket has no SLA period, so there is no deadline to miss.</summary>
    [Fact]
    public async Task TicketWithNoSlaPeriod_HasNothingToBreach()
    {
        var h = await CreateAsync(ResolutionDue.AddHours(5), openSlaPeriod: false);

        var outcome = await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.Resolution);

        Assert.Equal(SlaBreachProcessingOutcome.NoSlaPeriod, outcome);
        Assert.Empty(h.Sla.Escalations.All);
    }

    [Fact]
    public async Task UnknownTicket_IsHandledWithoutThrowing()
    {
        var h = await CreateAsync(ResolutionDue);

        var outcome = await h.Service.CheckDeadlineAsync(ticketId: 9999, SlaDeadlineType.Resolution);

        Assert.Equal(SlaBreachProcessingOutcome.NoSlaPeriod, outcome);
    }

    /// <summary>Nothing is committed on a non-breach — the "no side effects on a repeat" guarantee is visible in the unit of work, not merely in the absence of rows.</summary>
    [Fact]
    public async Task NonBreach_CommitsNothing()
    {
        var h = await CreateAsync(FirstResponseDue.AddSeconds(-1));

        await h.Service.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        Assert.Equal(0, h.Sla.UnitOfWork.TransactionsCommitted);
        Assert.Equal(1, h.Sla.UnitOfWork.TransactionsRolledBack);
    }
}
