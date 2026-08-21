using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Seed;
using TigerCS.Tests.SlaAndEscalation.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// Opening a ticket's initial SLA period (backlog S-08/S-09): the clock-start
/// event, persistence of both due timestamps, the audit record of the
/// computation, and the scheduled deadline checks of ADR-0015.
/// </summary>
public class SlaDueDateServiceTests
{
    /// <summary>Sunday 2026-08-23 09:00 Asia/Dubai — a working day, inside the 08:00–18:00 window.</summary>
    private static readonly DateTime ClockStart = new DateTime(2026, 8, 23, 9, 0, 0, DateTimeKind.Utc).AddHours(-4);

    private static async Task<(SlaServiceFixture Sla, Ticket Ticket)> CreateAsync(PriorityLevel priority)
    {
        var sla = new SlaServiceFixture();
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260823-0001", departmentId: 2, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, (byte)priority, "AC not cooling", ClockStart);
        await sla.Tickets.AddAsync(ticket);
        return (sla, ticket);
    }

    /// <summary>
    /// ISSUE-001 Option C, the approved clock-start event: the clock starts at
    /// ticket creation, and the stored period start is exactly that moment —
    /// not the assignment moment, and not "now".
    /// </summary>
    [Fact]
    public async Task InitialPeriod_StartsAtTheApprovedClockStartEvent()
    {
        var (sla, ticket) = await CreateAsync(PriorityLevel.High);

        var instance = await sla.DueDates.OpenInitialPeriodAsync(ticket, ClockStart, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(ClockStart, instance.PeriodStartAtUtc);
        Assert.Equal(ticket.CreatedAtUtc, instance.PeriodStartAtUtc);
        Assert.Equal(SlaChangeReason.InitialCreation, instance.ChangeReason);
        Assert.Null(instance.PeriodEndAtUtc);
    }

    /// <summary>Both due timestamps are computed from the seeded policy and persisted as the ticket's current period (FR-SLA-04).</summary>
    [Theory]
    [InlineData(PriorityLevel.Critical)]
    [InlineData(PriorityLevel.High)]
    [InlineData(PriorityLevel.Medium)]
    [InlineData(PriorityLevel.Low)]
    public async Task InitialPeriod_PersistsBothDueTimestampsForEveryPriority(PriorityLevel priority)
    {
        var (sla, ticket) = await CreateAsync(priority);

        await sla.DueDates.OpenInitialPeriodAsync(ticket, ClockStart, Guid.NewGuid(), Guid.NewGuid());

        var stored = Assert.Single(sla.SlaInstances.All);
        var (expectedFirstResponse, expectedResolution) = await sla.DueDates.ComputeDueDatesAsync((byte)priority, ClockStart);

        Assert.Equal(expectedFirstResponse, stored.FirstResponseDueAtUtc);
        Assert.Equal(expectedResolution, stored.ResolutionDueAtUtc);
        Assert.True(stored.FirstResponseDueAtUtc > ClockStart);
        Assert.True(stored.ResolutionDueAtUtc > stored.FirstResponseDueAtUtc);
        Assert.Equal(DateTimeKind.Utc, stored.FirstResponseDueAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, stored.ResolutionDueAtUtc.Kind);
    }

    /// <summary>A newly opened period has breached nothing.</summary>
    [Fact]
    public async Task InitialPeriod_StartsUnbreached()
    {
        var (sla, ticket) = await CreateAsync(PriorityLevel.Medium);

        var instance = await sla.DueDates.OpenInitialPeriodAsync(ticket, ClockStart, Guid.NewGuid(), Guid.NewGuid());

        Assert.False(instance.FirstResponseBreached);
        Assert.False(instance.ResolutionBreached);
    }

    /// <summary>
    /// SLA-Architecture.md §13 — a scheduled deadline check is enqueued per
    /// due timestamp at the moment it is computed, one per clock.
    /// </summary>
    [Fact]
    public async Task InitialPeriod_SchedulesADeadlineCheckForEachClock()
    {
        var (sla, ticket) = await CreateAsync(PriorityLevel.High);

        var instance = await sla.DueDates.OpenInitialPeriodAsync(ticket, ClockStart, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(2, sla.Scheduler.Scheduled.Count);
        Assert.Contains(sla.Scheduler.Scheduled,
            s => s.DeadlineType == SlaDeadlineType.FirstResponse && s.DueAtUtc == instance.FirstResponseDueAtUtc);
        Assert.Contains(sla.Scheduler.Scheduled,
            s => s.DeadlineType == SlaDeadlineType.Resolution && s.DueAtUtc == instance.ResolutionDueAtUtc);
        Assert.All(sla.Scheduler.Scheduled, s => Assert.Equal(ticket.TicketId, s.TicketId));
    }

    /// <summary>
    /// NFR-LOG-01 requires an SLA calculation be reconstructable "for audit or
    /// dispute", so the audit entry carries the inputs and both outputs, not
    /// just the fact that a computation happened.
    /// </summary>
    [Fact]
    public async Task InitialPeriod_AuditsTheComputationWithItsInputsAndOutputs()
    {
        var (sla, ticket) = await CreateAsync(PriorityLevel.High);
        var actorId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var instance = await sla.DueDates.OpenInitialPeriodAsync(ticket, ClockStart, actorId, correlationId);

        var audit = Assert.Single(sla.Audit.Entries, e => e.Action == "ComputeSlaDueDates");
        Assert.Equal(actorId, audit.ActorEmployeeId);
        Assert.Equal(correlationId, audit.CorrelationId);
        Assert.Equal(ticket.TicketId.ToString(), audit.EntityId);
        Assert.Contains(ClockStart.ToString("O"), audit.AfterValue!, StringComparison.Ordinal);
        Assert.Contains(instance.FirstResponseDueAtUtc.ToString("O"), audit.AfterValue!, StringComparison.Ordinal);
        Assert.Contains(instance.ResolutionDueAtUtc.ToString("O"), audit.AfterValue!, StringComparison.Ordinal);
    }

    /// <summary>A system-driven clock start (no acting employee) is audited as such rather than attributed to nobody in particular.</summary>
    [Fact]
    public async Task InitialPeriod_SupportsASystemActor()
    {
        var (sla, ticket) = await CreateAsync(PriorityLevel.High);

        await sla.DueDates.OpenInitialPeriodAsync(ticket, ClockStart, actorEmployeeId: null, Guid.NewGuid());

        var audit = Assert.Single(sla.Audit.Entries, e => e.Action == "ComputeSlaDueDates");
        Assert.Null(audit.ActorEmployeeId);
    }

    // -----------------------------------------------------------------
    // Reference-data failures fail loudly
    // -----------------------------------------------------------------

    /// <summary>MVP-ERD.md §2.6 requires exactly one policy row per priority; a missing one is a misconfiguration, not a reason to invent a deadline.</summary>
    [Fact]
    public async Task MissingSlaPolicy_FailsLoudly()
    {
        var (sla, ticket) = await CreateAsync(PriorityLevel.High);
        sla.Policies.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sla.DueDates.OpenInitialPeriodAsync(ticket, ClockStart, Guid.NewGuid(), Guid.NewGuid()));
    }

    /// <summary>A business-hours tier cannot be computed without a calendar (ADR-0010).</summary>
    [Fact]
    public async Task MissingBusinessCalendar_FailsLoudlyForABusinessHoursTier()
    {
        var (sla, ticket) = await CreateAsync(PriorityLevel.High);
        sla.Calendar.Use(null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sla.DueDates.OpenInitialPeriodAsync(ticket, ClockStart, Guid.NewGuid(), Guid.NewGuid()));
    }

    /// <summary>
    /// SLA-Architecture.md §3 has Critical bypass the calendar entirely, so
    /// the most urgent tier must keep working even when calendar reference
    /// data is missing — the calendar is not loaded at all for it.
    /// </summary>
    [Fact]
    public async Task Critical_ComputesEvenWithNoBusinessCalendarSeeded()
    {
        var (sla, ticket) = await CreateAsync(PriorityLevel.Critical);
        sla.Calendar.Use(null);

        var instance = await sla.DueDates.OpenInitialPeriodAsync(ticket, ClockStart, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(ClockStart.AddMinutes(15), instance.FirstResponseDueAtUtc);
        Assert.Equal(ClockStart.AddHours(4), instance.ResolutionDueAtUtc);
    }

    // -----------------------------------------------------------------
    // Seeded reference data (backlog S-04's Definition of Done)
    // -----------------------------------------------------------------

    /// <summary>ISSUE-017 Option A: Saturday–Thursday working, Friday off, 08:00–18:00, UAE.</summary>
    [Fact]
    public void SeededCalendar_MatchesTheApprovedWorkingWeek()
    {
        Assert.Equal(new TimeOnly(8, 0), SlaReferenceData.DefaultBusinessDayStartLocal);
        Assert.Equal(new TimeOnly(18, 0), SlaReferenceData.DefaultBusinessDayEndLocal);
        Assert.Equal("Asia/Dubai", SlaReferenceData.DefaultTimeZoneId);

        Assert.DoesNotContain(DayOfWeek.Friday, SlaReferenceData.DefaultWorkingDays);
        Assert.Equal(6, SlaReferenceData.DefaultWorkingDays.Count);
        foreach (var day in new[]
                 {
                     DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday,
                     DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday
                 })
        {
            Assert.Contains(day, SlaReferenceData.DefaultWorkingDays);
        }
    }

    /// <summary>One policy per priority, all four, all active (MVP-ERD.md §2.6's required 1:1).</summary>
    [Fact]
    public void SeededPolicies_CoverEveryPriorityExactlyOnce()
    {
        var policies = SlaReferenceData.Policies();

        Assert.Equal(4, policies.Count);
        Assert.Equal(
            Enum.GetValues<PriorityLevel>().Select(p => (byte)p).Order(),
            policies.Select(p => p.PriorityId).Order());
        Assert.All(policies, p => Assert.True(p.IsActive));
    }

    /// <summary>The warning thresholds of SLA-Architecture.md §10 — seeded, though this increment does not consume them.</summary>
    [Theory]
    [InlineData(PriorityLevel.Critical, 50.00)]
    [InlineData(PriorityLevel.High, 75.00)]
    [InlineData(PriorityLevel.Medium, 75.00)]
    [InlineData(PriorityLevel.Low, 75.00)]
    public void SeededPolicies_CarryTheApprovedWarningThresholds(PriorityLevel priority, double expected)
    {
        var policy = SlaReferenceData.Policies().Single(p => p.PriorityId == (byte)priority);

        Assert.Equal((decimal)expected, policy.WarningThresholdPercent);
    }

    /// <summary>The Level 2 → GM windows of §12 — seeded, and deliberately not consumed: the timed advance is out of scope for this pilot.</summary>
    [Theory]
    [InlineData(PriorityLevel.Critical, 30, SlaWindowUnit.Minutes)]
    [InlineData(PriorityLevel.High, 2, SlaWindowUnit.Hours)]
    [InlineData(PriorityLevel.Medium, 1, SlaWindowUnit.BusinessDays)]
    [InlineData(PriorityLevel.Low, 2, SlaWindowUnit.BusinessDays)]
    public void SeededPolicies_CarryTheApprovedEscalationWindows(PriorityLevel priority, int value, SlaWindowUnit unit)
    {
        var policy = SlaReferenceData.Policies().Single(p => p.PriorityId == (byte)priority);

        Assert.Equal(value, policy.Level2ToGmWindowValue);
        Assert.Equal((byte)unit, policy.Level2ToGmWindowUnit);
    }

    /// <summary>
    /// The conversion the source's two business-day targets rest on: with the
    /// approved 08:00–18:00 window a business day is 600 minutes, so Medium's
    /// "3 business days" is 1&#160;800 and Low's "7 business days" is
    /// 4&#160;200. If the seeded window ever changes, this is the assertion
    /// that says those two numbers must be revisited.
    /// </summary>
    [Fact]
    public void BusinessDayConversion_MatchesTheSeededWindow()
    {
        var windowMinutes =
            (SlaReferenceData.DefaultBusinessDayEndLocal - SlaReferenceData.DefaultBusinessDayStartLocal).TotalMinutes;

        Assert.Equal(SlaReferenceData.BusinessMinutesPerDay, (int)windowMinutes);

        var policies = SlaReferenceData.Policies();
        Assert.Equal(
            3 * SlaReferenceData.BusinessMinutesPerDay,
            policies.Single(p => p.PriorityId == (byte)PriorityLevel.Medium).ResolutionTargetMinutes);
        Assert.Equal(
            7 * SlaReferenceData.BusinessMinutesPerDay,
            policies.Single(p => p.PriorityId == (byte)PriorityLevel.Low).ResolutionTargetMinutes);
    }
}
