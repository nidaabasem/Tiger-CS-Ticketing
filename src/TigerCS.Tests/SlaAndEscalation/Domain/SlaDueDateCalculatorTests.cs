using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Seed;
using TigerCS.Tests.SlaAndEscalation.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Domain;

/// <summary>
/// The due-date computation of FR-SLA-01–04 / SLA-Architecture.md §3–§4,
/// asserted against the approved calendar (ISSUE-017 Option A: Saturday–
/// Thursday, 08:00–18:00 Asia/Dubai, Friday off) and the approved per-tier
/// targets.
///
/// <para>
/// Dates are chosen so the weekday is unambiguous: 2026-08-20 is a Thursday,
/// 2026-08-21 the Friday the calendar excludes, and 2026-08-22/23/24 the
/// Saturday/Sunday/Monday that follow. Asia/Dubai observes no daylight
/// saving, so every local↔UTC conversion below is a flat +04:00 and the
/// arithmetic is exact.
/// </para>
/// </summary>
public class SlaDueDateCalculatorTests
{
    private static readonly BusinessCalendarSnapshot Calendar = SlaTestCalendar.Default();

    /// <summary>Local Dubai wall-clock time expressed as the UTC instant the calculation actually works in.</summary>
    private static DateTime DubaiLocal(int year, int month, int day, int hour, int minute) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-4);

    private static SlaPolicy PolicyFor(PriorityLevel level) =>
        SlaReferenceData.Policies().Single(p => p.PriorityId == (byte)level);

    private static DateTime Due(SlaPolicy policy, SlaDeadlineType deadline, DateTime startUtc) =>
        SlaDueDateCalculator.ComputeDueAtUtc(
            startUtc,
            policy.TargetMinutesFor(deadline),
            policy.ClockBasis,
            policy.ClockBasis == SlaClockBasis.BusinessHours ? Calendar : null);

    // -----------------------------------------------------------------
    // Every priority's approved targets (Solution-Analysis.md §7.1)
    // -----------------------------------------------------------------

    /// <summary>
    /// The approved target table, in one place: Critical 15 min/4 h on a 24/7
    /// clock; High 1 h/24 h, Medium 4 h/3 business days, Low 24 h/7 business
    /// days on the business-hours clock. A change to any of these six numbers
    /// is a change to a contractual commitment, so it fails here first.
    /// </summary>
    [Theory]
    [InlineData(PriorityLevel.Critical, 15, 240, SlaClockBasis.TwentyFourSeven)]
    [InlineData(PriorityLevel.High, 60, 1440, SlaClockBasis.BusinessHours)]
    [InlineData(PriorityLevel.Medium, 240, 1800, SlaClockBasis.BusinessHours)]
    [InlineData(PriorityLevel.Low, 1440, 4200, SlaClockBasis.BusinessHours)]
    public void SeededPolicy_MatchesTheApprovedTargetTable(
        PriorityLevel level, int firstResponseMinutes, int resolutionMinutes, SlaClockBasis expectedBasis)
    {
        var policy = PolicyFor(level);

        Assert.Equal(firstResponseMinutes, policy.FirstResponseTargetMinutes);
        Assert.Equal(resolutionMinutes, policy.ResolutionTargetMinutes);
        Assert.Equal(expectedBasis, policy.ClockBasis);
    }

    /// <summary>
    /// Each tier's due dates from one mid-window start, so the four tiers can
    /// be compared side by side. Sunday 2026-08-23 09:00 local is inside the
    /// business window on a working day.
    ///
    /// <para>
    /// Critical is pure duration addition. High's 24 business hours span
    /// Sunday's remaining 9 h, Monday's 10 h and 5 h of Tuesday — which is
    /// also, exactly, where Low's 24-business-hour First Response target
    /// lands, since the two targets are the same length. Medium's 3 business
    /// days (1&#160;800 min) land at the same clock time three working days
    /// on. Low's 7 business days (4&#160;200 min) cross the Friday the
    /// calendar excludes, so they land eight calendar days later, not seven.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(PriorityLevel.Critical, 2026, 8, 23, 9, 15, 2026, 8, 23, 13, 0)]
    [InlineData(PriorityLevel.High, 2026, 8, 23, 10, 0, 2026, 8, 25, 13, 0)]
    [InlineData(PriorityLevel.Medium, 2026, 8, 23, 13, 0, 2026, 8, 26, 9, 0)]
    [InlineData(PriorityLevel.Low, 2026, 8, 25, 13, 0, 2026, 8, 31, 9, 0)]
    public void DueDates_ForEveryPriority_FromAMidWindowStart(
        PriorityLevel level,
        int frYear, int frMonth, int frDay, int frHour, int frMinute,
        int resYear, int resMonth, int resDay, int resHour, int resMinute)
    {
        var start = DubaiLocal(2026, 8, 23, 9, 0);
        var policy = PolicyFor(level);

        Assert.Equal(DubaiLocal(frYear, frMonth, frDay, frHour, frMinute), Due(policy, SlaDeadlineType.FirstResponse, start));
        Assert.Equal(DubaiLocal(resYear, resMonth, resDay, resHour, resMinute), Due(policy, SlaDeadlineType.Resolution, start));
    }

    // -----------------------------------------------------------------
    // Critical: 24/7, calendar never consulted (SLA-Architecture.md §3)
    // -----------------------------------------------------------------

    /// <summary>SLA-Architecture.md §16 Example A, verbatim: "Created Tuesday 22:40. First Response due 22:55 (15 min). Resolution due Wednesday 02:40 (4h)."</summary>
    [Fact]
    public void Critical_WorkedExampleA_IsSimpleDurationAddition()
    {
        var createdAtUtc = new DateTime(2026, 8, 18, 22, 40, 0, DateTimeKind.Utc);
        var policy = PolicyFor(PriorityLevel.Critical);

        Assert.Equal(new DateTime(2026, 8, 18, 22, 55, 0, DateTimeKind.Utc), Due(policy, SlaDeadlineType.FirstResponse, createdAtUtc));
        Assert.Equal(new DateTime(2026, 8, 19, 2, 40, 0, DateTimeKind.Utc), Due(policy, SlaDeadlineType.Resolution, createdAtUtc));
    }

    /// <summary>
    /// §3: Critical is computed "with <b>no</b> business-hours or
    /// holiday-calendar exclusion". Started on the excluded Friday, in the
    /// middle of the night, on a date that is also a holiday — and it still
    /// just adds the duration.
    /// </summary>
    [Fact]
    public void Critical_IgnoresWeekendsHolidaysAndTheBusinessWindowEntirely()
    {
        // Friday 2026-08-21 — a non-working day — and seeded as a holiday too.
        var startUtc = DubaiLocal(2026, 8, 21, 3, 0);
        var policy = PolicyFor(PriorityLevel.Critical);

        var withHoliday = SlaDueDateCalculator.ComputeDueAtUtc(
            startUtc, policy.ResolutionTargetMinutes, policy.ClockBasis,
            SlaTestCalendar.Default(new DateOnly(2026, 8, 21)));

        Assert.Equal(DubaiLocal(2026, 8, 21, 7, 0), withHoliday);
    }

    /// <summary>A Critical computation must not need a calendar at all — §3 says it never consults one, and a missing calendar must not be able to stop the most urgent tier.</summary>
    [Fact]
    public void Critical_ComputesWithoutABusinessCalendar()
    {
        var startUtc = new DateTime(2026, 8, 21, 3, 0, 0, DateTimeKind.Utc);

        var due = SlaDueDateCalculator.ComputeDueAtUtc(startUtc, 240, SlaClockBasis.TwentyFourSeven, calendar: null);

        Assert.Equal(new DateTime(2026, 8, 21, 7, 0, 0, DateTimeKind.Utc), due);
    }

    // -----------------------------------------------------------------
    // Business hours, weekends and the approved non-working day
    // -----------------------------------------------------------------

    /// <summary>
    /// SLA-Architecture.md §16 Example B's First Response half, verbatim:
    /// "Created Thursday 17:30 ... 30 min elapsed Thursday; Friday excluded
    /// entirely; clock resumes Saturday 08:00. First Response (1h) due
    /// Saturday 08:30."
    /// </summary>
    [Fact]
    public void High_WorkedExampleB_FirstResponseCrossesTheExcludedFriday()
    {
        var start = DubaiLocal(2026, 8, 20, 17, 30);

        var due = Due(PolicyFor(PriorityLevel.High), SlaDeadlineType.FirstResponse, start);

        Assert.Equal(DubaiLocal(2026, 8, 22, 8, 30), due);
    }

    /// <summary>
    /// The Resolution half of the same example, computed by §4's normative
    /// algorithm.
    ///
    /// <para>
    /// <b>This deliberately does not match the figure printed in §16.</b> That
    /// example states "Resolution (24h) due Sunday 13:30", which does not
    /// follow from §4's own pseudocode: 24 business hours from Thursday 17:30
    /// is 30 minutes on Thursday, 600 on Saturday, 600 on Sunday and the
    /// remaining 210 on Monday — Monday 11:30. Sunday 13:30 accounts for only
    /// 16 business hours. §4's algorithm is the normative rule and the First
    /// Response half of the same example agrees with it, so the example's
    /// resolution figure is an arithmetic slip in the document; it is reported
    /// in the PR description for correction rather than encoded here.
    /// </para>
    /// </summary>
    [Fact]
    public void High_WorkedExampleB_ResolutionFollowsTheNormativeAlgorithmNotThePrintedFigure()
    {
        var start = DubaiLocal(2026, 8, 20, 17, 30);

        var due = Due(PolicyFor(PriorityLevel.High), SlaDeadlineType.Resolution, start);

        Assert.Equal(DubaiLocal(2026, 8, 24, 11, 30), due);
        Assert.NotEqual(DubaiLocal(2026, 8, 23, 13, 30), due);
    }

    /// <summary>A clock starting after the window has closed consumes nothing that evening — it resumes at the next working day's opening moment.</summary>
    [Fact]
    public void BusinessHours_StartAfterTheWindowCloses_ResumesAtTheNextWorkingDayOpen()
    {
        // Sunday 2026-08-23 19:00 local — after the 18:00 close.
        var start = DubaiLocal(2026, 8, 23, 19, 0);

        var due = SlaDueDateCalculator.ComputeDueAtUtc(start, 60, SlaClockBasis.BusinessHours, Calendar);

        Assert.Equal(DubaiLocal(2026, 8, 24, 9, 0), due);
    }

    /// <summary>A clock starting before the window opens waits for it rather than counting the early hours.</summary>
    [Fact]
    public void BusinessHours_StartBeforeTheWindowOpens_WaitsForIt()
    {
        var start = DubaiLocal(2026, 8, 23, 5, 0);

        var due = SlaDueDateCalculator.ComputeDueAtUtc(start, 90, SlaClockBasis.BusinessHours, Calendar);

        Assert.Equal(DubaiLocal(2026, 8, 23, 9, 30), due);
    }

    /// <summary>Starting exactly at the close boundary consumes nothing that day — 18:00 is the end of the window, not a minute of it.</summary>
    [Fact]
    public void BusinessHours_StartExactlyAtTheCloseBoundary_ConsumesNothingThatDay()
    {
        var start = DubaiLocal(2026, 8, 23, 18, 0);

        var due = SlaDueDateCalculator.ComputeDueAtUtc(start, 30, SlaClockBasis.BusinessHours, Calendar);

        Assert.Equal(DubaiLocal(2026, 8, 24, 8, 30), due);
    }

    /// <summary>Starting exactly at the open boundary counts from that instant.</summary>
    [Fact]
    public void BusinessHours_StartExactlyAtTheOpenBoundary_CountsImmediately()
    {
        var start = DubaiLocal(2026, 8, 23, 8, 0);

        var due = SlaDueDateCalculator.ComputeDueAtUtc(start, 30, SlaClockBasis.BusinessHours, Calendar);

        Assert.Equal(DubaiLocal(2026, 8, 23, 8, 30), due);
    }

    /// <summary>A target that exactly fills the remaining window lands on the close boundary itself, not on the next day.</summary>
    [Fact]
    public void BusinessHours_TargetExactlyFillingTheDay_LandsOnTheCloseBoundary()
    {
        var start = DubaiLocal(2026, 8, 23, 8, 0);

        var due = SlaDueDateCalculator.ComputeDueAtUtc(
            start, SlaReferenceData.BusinessMinutesPerDay, SlaClockBasis.BusinessHours, Calendar);

        Assert.Equal(DubaiLocal(2026, 8, 23, 18, 0), due);
    }

    /// <summary>The approved week's only non-working day. Started Thursday, a target of just over one business day cannot land on Friday.</summary>
    [Fact]
    public void BusinessHours_FridayIsNeverConsumed()
    {
        var start = DubaiLocal(2026, 8, 20, 8, 0);

        var due = SlaDueDateCalculator.ComputeDueAtUtc(
            start, SlaReferenceData.BusinessMinutesPerDay + 60, SlaClockBasis.BusinessHours, Calendar);

        // Thursday's full 10 h, then straight over Friday to Saturday.
        Assert.Equal(DubaiLocal(2026, 8, 22, 9, 0), due);
        Assert.NotEqual(DayOfWeek.Friday, TimeZoneInfo.ConvertTimeFromUtc(due, SlaTestCalendar.Dubai).DayOfWeek);
    }

    // -----------------------------------------------------------------
    // Holidays (ADR-0010, ISSUE-012)
    // -----------------------------------------------------------------

    /// <summary>A single holiday on an otherwise-working day is skipped whole.</summary>
    [Fact]
    public void BusinessHours_ExcludesASingleHoliday()
    {
        var calendarWithHoliday = SlaTestCalendar.Default(new DateOnly(2026, 8, 24));
        var start = DubaiLocal(2026, 8, 23, 16, 0);

        var due = SlaDueDateCalculator.ComputeDueAtUtc(start, 180, SlaClockBasis.BusinessHours, calendarWithHoliday);

        // 2 h on Sunday, Monday 24th is the holiday, remaining 1 h on Tuesday.
        Assert.Equal(DubaiLocal(2026, 8, 25, 9, 0), due);
    }

    /// <summary>Consecutive holidays are skipped one after another, not merely the first.</summary>
    [Fact]
    public void BusinessHours_ExcludesConsecutiveHolidays()
    {
        var calendarWithHolidays = SlaTestCalendar.Default(
            new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 25), new DateOnly(2026, 8, 26));
        var start = DubaiLocal(2026, 8, 23, 17, 0);

        var due = SlaDueDateCalculator.ComputeDueAtUtc(start, 120, SlaClockBasis.BusinessHours, calendarWithHolidays);

        // 1 h on Sunday, three holidays skipped, remaining 1 h on Thursday.
        Assert.Equal(DubaiLocal(2026, 8, 27, 9, 0), due);
    }

    /// <summary>A holiday landing next to the excluded Friday extends the gap rather than overlapping with it.</summary>
    [Fact]
    public void BusinessHours_HolidayAdjacentToTheNonWorkingDay_ExtendsTheGap()
    {
        // Thursday 20th is a holiday; Friday 21st is already non-working.
        var calendarWithHoliday = SlaTestCalendar.Default(new DateOnly(2026, 8, 20));
        var start = DubaiLocal(2026, 8, 19, 17, 30);

        var due = SlaDueDateCalculator.ComputeDueAtUtc(start, 60, SlaClockBasis.BusinessHours, calendarWithHoliday);

        // 30 min on Wednesday, Thursday and Friday both skipped, remaining
        // 30 min on Saturday.
        Assert.Equal(DubaiLocal(2026, 8, 22, 8, 30), due);
    }

    /// <summary>A holiday on the start date itself pushes the whole clock to the next working day.</summary>
    [Fact]
    public void BusinessHours_HolidayOnTheStartDate_PushesTheWholeClock()
    {
        var calendarWithHoliday = SlaTestCalendar.Default(new DateOnly(2026, 8, 23));
        var start = DubaiLocal(2026, 8, 23, 9, 0);

        var due = SlaDueDateCalculator.ComputeDueAtUtc(start, 60, SlaClockBasis.BusinessHours, calendarWithHoliday);

        Assert.Equal(DubaiLocal(2026, 8, 24, 9, 0), due);
    }

    // -----------------------------------------------------------------
    // UTC handling (NFR-UAE-01)
    // -----------------------------------------------------------------

    /// <summary>Every result is UTC-kinded, on both clock bases — a persisted SLA timestamp is never local.</summary>
    [Theory]
    [InlineData(SlaClockBasis.TwentyFourSeven)]
    [InlineData(SlaClockBasis.BusinessHours)]
    public void ComputedDueDate_IsAlwaysUtcKinded(SlaClockBasis basis)
    {
        var start = DubaiLocal(2026, 8, 23, 9, 0);

        var due = SlaDueDateCalculator.ComputeDueAtUtc(
            start, 60, basis, basis == SlaClockBasis.BusinessHours ? Calendar : null);

        Assert.Equal(DateTimeKind.Utc, due.Kind);
    }

    /// <summary>
    /// EF Core hands back <see cref="DateTimeKind.Unspecified"/> for a
    /// <c>datetime2</c> column, and every such column in this solution stores
    /// UTC. A round-tripped start moment must therefore produce the same
    /// answer as the UTC-kinded one it was read from.
    /// </summary>
    [Fact]
    public void UnspecifiedKindStart_IsTreatedAsUtc()
    {
        var utcStart = DubaiLocal(2026, 8, 23, 9, 0);
        var asReadFromDatabase = DateTime.SpecifyKind(utcStart, DateTimeKind.Unspecified);

        Assert.Equal(
            SlaDueDateCalculator.ComputeDueAtUtc(utcStart, 240, SlaClockBasis.BusinessHours, Calendar),
            SlaDueDateCalculator.ComputeDueAtUtc(asReadFromDatabase, 240, SlaClockBasis.BusinessHours, Calendar));
    }

    /// <summary>
    /// A local-kinded start is refused rather than silently converted: doing
    /// so would import whichever zone the host happens to run in into a
    /// contractual calculation, which NFR-UAE-01 exists to prevent.
    /// </summary>
    [Fact]
    public void LocalKindStart_IsRejected()
    {
        var local = new DateTime(2026, 8, 23, 9, 0, 0, DateTimeKind.Local);

        Assert.Throws<ArgumentException>(() =>
            SlaDueDateCalculator.ComputeDueAtUtc(local, 60, SlaClockBasis.BusinessHours, Calendar));
    }

    /// <summary>
    /// The same wall-clock local moment on two hosts is the same UTC instant,
    /// so the answer cannot depend on where the calculation ran. Asserted by
    /// feeding the identical UTC instant twice while the calendar's own zone
    /// stays fixed — the only zone the computation is allowed to know about.
    /// </summary>
    [Fact]
    public void ComputationDependsOnlyOnTheCalendarsZone_NotTheHosts()
    {
        var start = new DateTime(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);

        var first = SlaDueDateCalculator.ComputeDueAtUtc(start, 300, SlaClockBasis.BusinessHours, Calendar);

        // A second snapshot over the same reference data, constructed
        // independently — nothing is cached across calls, and no ambient
        // TimeZoneInfo.Local is consulted.
        var second = SlaDueDateCalculator.ComputeDueAtUtc(start, 300, SlaClockBasis.BusinessHours, SlaTestCalendar.Default());

        Assert.Equal(first, second);
    }

    // -----------------------------------------------------------------
    // Guards
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTarget_IsRejected(int targetMinutes)
    {
        var start = DubaiLocal(2026, 8, 23, 9, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SlaDueDateCalculator.ComputeDueAtUtc(start, targetMinutes, SlaClockBasis.BusinessHours, Calendar));
    }

    /// <summary>A business-hours tier cannot be computed without a calendar — failing loudly beats inventing a 24/7 answer for a tier that is not 24/7.</summary>
    [Fact]
    public void BusinessHoursWithoutACalendar_IsRejected()
    {
        var start = DubaiLocal(2026, 8, 23, 9, 0);

        Assert.Throws<ArgumentNullException>(() =>
            SlaDueDateCalculator.ComputeDueAtUtc(start, 60, SlaClockBasis.BusinessHours, calendar: null));
    }

    /// <summary>A calendar with no working day would never consume a minute; it is rejected at construction rather than spinning to the walk's iteration guard.</summary>
    [Fact]
    public void CalendarWithNoWorkingDays_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new BusinessCalendarSnapshot(SlaTestCalendar.Dubai, new TimeOnly(8, 0), new TimeOnly(18, 0), [], []));
    }

    /// <summary>An inverted window is rejected — a negative-length business day would consume time backwards.</summary>
    [Fact]
    public void CalendarWithAnInvertedWindow_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new BusinessCalendarSnapshot(
                SlaTestCalendar.Dubai, new TimeOnly(18, 0), new TimeOnly(8, 0), SlaReferenceData.DefaultWorkingDays, []));
    }
}
