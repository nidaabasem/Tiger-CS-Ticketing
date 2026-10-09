using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.SlaAndEscalation.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Domain;

/// <summary>
/// The approved pause rules (ISSUE-018a–d) and the pause due-date math, as
/// pure functions. Calendar: Sat–Thu 08:00–18:00 Asia/Dubai (UTC+4), Friday
/// off — so 08:00 local is 04:00Z and 18:00 local is 14:00Z.
/// </summary>
public class SlaPauseDomainTests
{
    private static DateTime Utc(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    // ---- SlaPauseRules ----

    [Fact]
    public void FirstResponseNeverPauses()
    {
        Assert.False(SlaPauseRules.FirstResponseCanPause);
    }

    [Theory]
    [InlineData(PriorityLevel.High)]
    [InlineData(PriorityLevel.Medium)]
    [InlineData(PriorityLevel.Low)]
    public void NonCritical_PausesOnPendingCustomer_ByDefault(PriorityLevel priority)
    {
        Assert.True(SlaPauseRules.ResolutionPauses((byte)priority, TicketStatus.PendingCustomer, requestTypePausesOnPendingCustomer: null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void Critical_NeverPauses_EvenWhenTheRequestTypeSaysYes(bool? requestTypeFlag)
    {
        Assert.False(SlaPauseRules.ResolutionPauses((byte)PriorityLevel.Critical, TicketStatus.PendingCustomer, requestTypeFlag));
        Assert.False(SlaPauseRules.ResolutionPauses((byte)PriorityLevel.Critical, TicketStatus.PendingThirdParty, requestTypeFlag));
    }

    [Fact]
    public void RequestTypeOverride_FalseSuppressesAndTrueKeepsThePause()
    {
        Assert.False(SlaPauseRules.ResolutionPauses((byte)PriorityLevel.Medium, TicketStatus.PendingCustomer, false));
        Assert.True(SlaPauseRules.ResolutionPauses((byte)PriorityLevel.Medium, TicketStatus.PendingCustomer, true));
    }

    [Fact]
    public void LegacyPendingThirdParty_Pauses_AndIsNotGovernedByThePendingCustomerFlag()
    {
        Assert.True(SlaPauseRules.IsPausingStatus(TicketStatus.PendingThirdParty));
        Assert.True(SlaPauseRules.ResolutionPauses((byte)PriorityLevel.High, TicketStatus.PendingThirdParty, requestTypePausesOnPendingCustomer: false));
        Assert.Equal(SlaPauseReason.PendingThirdPartyLegacy, SlaPauseRules.ReasonFor(TicketStatus.PendingThirdParty));
    }

    [Theory]
    [InlineData(TicketStatus.Open)]
    [InlineData(TicketStatus.InProgress)]
    [InlineData(TicketStatus.Resolved)]
    [InlineData(TicketStatus.Closed)]
    public void OtherStatuses_DoNotPause(TicketStatus status)
    {
        Assert.False(SlaPauseRules.IsPausingStatus(status));
        Assert.False(SlaPauseRules.ResolutionPauses((byte)PriorityLevel.High, status, null));
    }

    // ---- SlaPauseCalculator ----

    [Fact]
    public void TwentyFourSeven_ExtendsByTheWallClockPausedDuration()
    {
        var due = Utc(8, 23, 10);

        var extended = SlaPauseCalculator.ExtendedDueAtUtc(
            due, pausedAtUtc: Utc(8, 23, 7), resumedAtUtc: Utc(8, 23, 9, 30), SlaClockBasis.TwentyFourSeven, calendar: null);

        Assert.Equal(Utc(8, 23, 12, 30), extended);
    }

    [Fact]
    public void BusinessHours_PauseInsideWorkingHours_ExtendsByThePausedDuration()
    {
        var calendar = SlaTestCalendar.Default();

        // Due Tue Aug 25 09:00Z; paused Sun 06:00Z-08:00Z (2h, all inside 04:00-14:00Z).
        var extended = SlaPauseCalculator.ExtendedDueAtUtc(
            Utc(8, 25, 9), Utc(8, 23, 6), Utc(8, 23, 8), SlaClockBasis.BusinessHours, calendar);

        Assert.Equal(Utc(8, 25, 11), extended);
    }

    [Fact]
    public void BusinessHours_PauseAcrossTheWeekend_DoesNotOverCreditTheNonBusinessTime()
    {
        var calendar = SlaTestCalendar.Default();

        // Due Sat Aug 29 06:00Z (10:00 local). Paused Thu Aug 27 10:00Z (14:00 local):
        // 4h left on Thursday + 2h on Saturday = 6h unspent. Friday is off.
        // Resumed Sat Aug 29 05:00Z (09:00 local) -> 6h more business time from
        // 09:00 local Saturday ends 15:00 local = 11:00Z. Adding the raw 43h
        // wall-clock pause would instead land on Sun Aug 30.
        var extended = SlaPauseCalculator.ExtendedDueAtUtc(
            Utc(8, 29, 6), Utc(8, 27, 10), Utc(8, 29, 5), SlaClockBasis.BusinessHours, calendar);

        Assert.Equal(Utc(8, 29, 11), extended);
    }

    [Fact]
    public void BusinessHours_PauseEntirelyOutsideWorkingTime_ChangesNothing()
    {
        var calendar = SlaTestCalendar.Default();

        // Friday is off: paused 06:00Z and resumed 20:00Z the same Friday; due Sat 10:00Z.
        var extended = SlaPauseCalculator.ExtendedDueAtUtc(
            Utc(8, 29, 6), Utc(8, 28, 6), Utc(8, 28, 20), SlaClockBasis.BusinessHours, calendar);

        Assert.Equal(Utc(8, 29, 6), extended);
    }

    [Fact]
    public void BusinessHours_DeadlineAlreadyPassedAtPauseStart_IsLeftAlone()
    {
        var calendar = SlaTestCalendar.Default();

        var extended = SlaPauseCalculator.ExtendedDueAtUtc(
            Utc(8, 23, 6), Utc(8, 23, 8), Utc(8, 23, 10), SlaClockBasis.BusinessHours, calendar);

        Assert.Equal(Utc(8, 23, 6), extended);
    }

    [Fact]
    public void ResumeNotAfterPause_NeverMovesTheDeadline()
    {
        var due = Utc(8, 23, 10);

        Assert.Equal(due, SlaPauseCalculator.ExtendedDueAtUtc(
            due, Utc(8, 23, 7), Utc(8, 23, 7), SlaClockBasis.TwentyFourSeven, null));
    }

    [Fact]
    public void BusinessTimeBetween_CountsOnlyWorkingWindows()
    {
        var calendar = SlaTestCalendar.Default();

        // Thu 10:00Z (14:00 local) -> Sat 06:00Z (10:00 local): 4h + 2h across the off Friday.
        Assert.Equal(TimeSpan.FromHours(6), SlaDueDateCalculator.BusinessTimeBetween(Utc(8, 27, 10), Utc(8, 29, 6), calendar));
        Assert.Equal(TimeSpan.Zero, SlaDueDateCalculator.BusinessTimeBetween(Utc(8, 28, 0), Utc(8, 28, 23), calendar));
        Assert.Equal(TimeSpan.Zero, SlaDueDateCalculator.BusinessTimeBetween(Utc(8, 29, 6), Utc(8, 27, 10), calendar));
    }

    // ---- TicketSlaPausePeriod ----

    [Fact]
    public void PausePeriod_ClosesOnce_AndReportsItsDuration()
    {
        var pause = new TicketSlaPausePeriod(1, 2, SlaPauseReason.PendingCustomer, Utc(8, 23, 6), Utc(8, 23, 10));

        Assert.True(pause.IsOpen);
        Assert.Equal(TimeSpan.FromHours(1), pause.DurationAsOf(Utc(8, 23, 7)));

        pause.Close(Utc(8, 23, 8), Utc(8, 23, 12), endedByResolution: false);

        Assert.False(pause.IsOpen);
        Assert.Equal(TimeSpan.FromHours(2), pause.DurationAsOf(Utc(8, 23, 20)));
        Assert.Throws<InvalidOperationException>(() => pause.Close(Utc(8, 23, 9), Utc(8, 23, 13), false));
    }

    [Fact]
    public void PausePeriod_CannotEndBeforeItStarted()
    {
        var pause = new TicketSlaPausePeriod(1, 2, SlaPauseReason.PendingCustomer, Utc(8, 23, 6), Utc(8, 23, 10));

        Assert.Throws<ArgumentException>(() => pause.Close(Utc(8, 23, 5), Utc(8, 23, 10), false));
    }

    // ---- TicketSlaInstance.ExtendResolutionDue ----

    [Fact]
    public void ExtendResolutionDue_OnlyMovesLater_AndNeverAfterABreach()
    {
        var instance = TicketSlaInstance.OpenInitialPeriod(1, (byte)PriorityLevel.High, Utc(8, 23, 5), Utc(8, 23, 6), Utc(8, 23, 10));

        Assert.Throws<ArgumentException>(() => instance.ExtendResolutionDue(Utc(8, 23, 9)));

        instance.ExtendResolutionDue(Utc(8, 23, 12));
        Assert.Equal(Utc(8, 23, 12), instance.ResolutionDueAtUtc);
        Assert.Equal(Utc(8, 23, 6), instance.FirstResponseDueAtUtc);

        instance.MarkBreached(SlaDeadlineType.Resolution);
        Assert.Throws<InvalidOperationException>(() => instance.ExtendResolutionDue(Utc(8, 23, 15)));
        Assert.True(instance.ResolutionBreached);
    }

    // ---- Ticket SlaState ----

    [Fact]
    public void Ticket_PauseAndResume_AreNarrow()
    {
        var ticket = Ticket.CreateVerified("TG-CS-20260823-0001", 1, 10, 20, 5, (byte)PriorityLevel.High, "x", Utc(8, 23, 5));

        Assert.True(ticket.PauseSla());
        Assert.Equal(SlaState.Paused, ticket.SlaState);
        Assert.False(ticket.PauseSla());
        Assert.True(ticket.ResumeSla());
        Assert.Equal(SlaState.Running, ticket.SlaState);
        Assert.False(ticket.ResumeSla());

        // A recorded breach is never hidden by a pause or un-done by a resume.
        ticket.MarkSlaBreached();
        Assert.False(ticket.PauseSla());
        Assert.False(ticket.ResumeSla());
        Assert.Equal(SlaState.Breached, ticket.SlaState);
    }

    // ---- RequestTypeSlaEnforcement ----

    private static readonly TimeSpan BusinessDay = TimeSpan.FromHours(10);

    private static RequestTypeSlaPolicy Policy(
        int? resolutionTarget = 3,
        int? resolutionMax = null,
        SlaDurationUnit unit = SlaDurationUnit.Days,
        SlaClockBasis? clock = SlaClockBasis.BusinessHours,
        SlaTriggerType trigger = SlaTriggerType.TicketCreated,
        bool isImmediate = false,
        int? firstResponseTarget = null,
        int? firstResponseMax = null,
        bool? pausesOnPendingCustomer = null,
        bool isActive = true) =>
        new(requestTypeId: 7, (byte)PriorityLevel.Medium, trigger, unit,
            firstResponseTarget, firstResponseMax, isImmediate ? null : resolutionTarget, isImmediate ? null : resolutionMax,
            isImmediate, clock, pausesOnPendingCustomer, pausesOnPendingInternal: null, warningThresholdPercent: null, isActive);

    [Fact]
    public void Enforcement_SingleValueWithExplicitClock_IsApplied()
    {
        var result = RequestTypeSlaEnforcement.Evaluate(Policy(3, 3), BusinessDay);

        Assert.True(result.IsApplied);
        Assert.Null(result.Reason);
        Assert.Equal(TimeSpan.FromHours(30), result.Resolution);
        Assert.Equal(SlaClockBasis.BusinessHours, result.ClockBasis);
        Assert.Null(result.FirstResponse);
    }

    [Theory]
    [InlineData(3, null)]
    [InlineData(null, 3)]
    [InlineData(3, 3)]
    public void Enforcement_OnlyOneBoundOrEqualBounds_IsASingleValue(int? target, int? maximum)
    {
        Assert.True(RequestTypeSlaEnforcement.Evaluate(Policy(target, maximum), BusinessDay).IsApplied);
    }

    [Fact]
    public void Enforcement_Range_IsNotApplied_WithTheRangeReason()
    {
        var result = RequestTypeSlaEnforcement.Evaluate(Policy(10, 12), BusinessDay);

        Assert.Equal(RequestTypeSlaStatus.NotApplied, result.Status);
        Assert.StartsWith("Request-type SLA not applied:", result.Reason, StringComparison.Ordinal);
        Assert.Contains("range", result.Reason, StringComparison.Ordinal);
        Assert.Contains("10–12 Days", result.Reason, StringComparison.Ordinal);
        Assert.Contains("undecided", result.Reason, StringComparison.Ordinal);
        Assert.Contains("per-priority policy", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.Resolution);
    }

    [Fact]
    public void Enforcement_NullClockBasis_IsNotApplied_WithTheCalendarVsBusinessReason()
    {
        var result = RequestTypeSlaEnforcement.Evaluate(Policy(clock: null), BusinessDay);

        Assert.Equal(RequestTypeSlaStatus.NotApplied, result.Status);
        Assert.Contains("business days vs. calendar days", result.Reason, StringComparison.Ordinal);
        Assert.Contains("undecided", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SlaTriggerType.Assigned)]
    [InlineData(SlaTriggerType.ApprovalReceived)]
    [InlineData(SlaTriggerType.CustomerServiceApproved)]
    [InlineData(SlaTriggerType.PrerequisitesCompleted)]
    public void Enforcement_NonCreationTrigger_IsNotApplied_NamingTheTrigger(SlaTriggerType trigger)
    {
        var result = RequestTypeSlaEnforcement.Evaluate(Policy(trigger: trigger), BusinessDay);

        Assert.Equal(RequestTypeSlaStatus.NotApplied, result.Status);
        Assert.Contains(trigger.ToString(), result.Reason, StringComparison.Ordinal);
        Assert.Contains("not at ticket creation", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Enforcement_InactiveOrMissingPolicy_IsNoPolicy()
    {
        Assert.Equal(RequestTypeSlaStatus.NoPolicy, RequestTypeSlaEnforcement.Evaluate(null, BusinessDay).Status);
        Assert.Equal(RequestTypeSlaStatus.NoPolicy, RequestTypeSlaEnforcement.Evaluate(Policy(isActive: false), BusinessDay).Status);
    }

    [Fact]
    public void Enforcement_BusinessHoursClockWithoutACalendar_IsNotApplied()
    {
        var result = RequestTypeSlaEnforcement.Evaluate(Policy(clock: SlaClockBasis.BusinessHours), businessDayLength: null);

        Assert.Equal(RequestTypeSlaStatus.NotApplied, result.Status);
        Assert.Contains("no active business calendar", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SlaDurationUnit.Minutes, SlaClockBasis.TwentyFourSeven, 90)]
    [InlineData(SlaDurationUnit.Hours, SlaClockBasis.TwentyFourSeven, 90 * 60)]
    [InlineData(SlaDurationUnit.Days, SlaClockBasis.TwentyFourSeven, 90 * 24 * 60)]
    [InlineData(SlaDurationUnit.Days, SlaClockBasis.BusinessHours, 90 * 10 * 60)]
    [InlineData(SlaDurationUnit.Hours, SlaClockBasis.BusinessHours, 90 * 60)]
    public void Enforcement_ConvertsUnits(SlaDurationUnit unit, SlaClockBasis clock, int expectedMinutes)
    {
        var result = RequestTypeSlaEnforcement.Evaluate(Policy(90, 90, unit, clock), BusinessDay);

        Assert.True(result.IsApplied);
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), result.Resolution);
    }

    [Fact]
    public void Enforcement_Immediate_AppliesWithAZeroResolutionDuration()
    {
        var result = RequestTypeSlaEnforcement.Evaluate(Policy(isImmediate: true, clock: SlaClockBasis.TwentyFourSeven), BusinessDay);

        Assert.True(result.IsApplied);
        Assert.True(result.ResolutionIsImmediate);
        Assert.Equal(TimeSpan.Zero, result.Resolution);
    }

    [Fact]
    public void Enforcement_FirstResponseRange_IsNotApplied_ButASingleFirstResponseValueIs()
    {
        var range = RequestTypeSlaEnforcement.Evaluate(Policy(3, 3, firstResponseTarget: 1, firstResponseMax: 2), BusinessDay);
        Assert.Equal(RequestTypeSlaStatus.NotApplied, range.Status);
        Assert.Contains("First Response is a range", range.Reason, StringComparison.Ordinal);

        var single = RequestTypeSlaEnforcement.Evaluate(Policy(3, 3, firstResponseTarget: 4, firstResponseMax: null), BusinessDay);
        Assert.True(single.IsApplied);
        Assert.Equal(TimeSpan.FromHours(40), single.FirstResponse);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void Enforcement_ReportsThePauseFlag_EvenWhenTheDurationsAreNotEnforced(bool? flag)
    {
        var applied = RequestTypeSlaEnforcement.Evaluate(Policy(3, 3, pausesOnPendingCustomer: flag), BusinessDay);
        var notApplied = RequestTypeSlaEnforcement.Evaluate(Policy(10, 12, pausesOnPendingCustomer: flag), BusinessDay);

        Assert.Equal(flag, applied.PausesOnPendingCustomerOverride);
        Assert.Equal(flag, notApplied.PausesOnPendingCustomerOverride);
    }
}
