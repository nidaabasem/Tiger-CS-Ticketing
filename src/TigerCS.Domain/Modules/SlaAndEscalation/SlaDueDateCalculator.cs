namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// The SLA due-timestamp computation (FR-SLA-01–04, backlog S-09) — a pure
/// function of its arguments, with no clock, repository or ambient
/// <see cref="TimeZoneInfo"/> lookup, so SLA-Architecture.md's worked
/// examples can be asserted against it directly (ADR-0010).
///
/// <para>
/// <b>Two bases, per SLA-Architecture.md §3 and §4.</b>
/// <see cref="SlaClockBasis.TwentyFourSeven"/> (Critical) is simple duration
/// addition with no calendar or holiday exclusion whatsoever.
/// <see cref="SlaClockBasis.BusinessHours"/> (High/Medium/Low) walks forward
/// from the start event counting only minutes inside the calendar's working
/// window, skipping non-working weekdays and every <c>Holiday</c> date — a
/// direct implementation of §4's pseudocode.
/// </para>
///
/// <para>
/// <b>UTC in, UTC out</b> (NFR-UAE-01). The business window is a local-time
/// concept, so the walk converts into the calendar's zone, works there, and
/// converts the answer back; nothing local is ever returned or persisted.
/// Every returned <see cref="DateTime"/> carries
/// <see cref="DateTimeKind.Utc"/>.
/// </para>
/// </summary>
public static class SlaDueDateCalculator
{
    /// <summary>
    /// Bounds the walk so a misconfigured calendar fails loudly instead of
    /// spinning. Ten years of days comfortably exceeds any real target: the
    /// longest approved one is Low's 7 business days.
    /// </summary>
    private const int MaximumDaysWalked = 3_660;

    /// <summary>
    /// Computes one deadline's due timestamp.
    /// </summary>
    /// <param name="clockStartAtUtc">The approved SLA clock-start event (ISSUE-001 Option C — ticket creation). Must be UTC.</param>
    /// <param name="targetMinutes">The tier's target from <see cref="SlaPolicy"/>. Business minutes under <see cref="SlaClockBasis.BusinessHours"/>; calendar minutes under <see cref="SlaClockBasis.TwentyFourSeven"/>.</param>
    /// <param name="clockBasis">Which of §3/§4's two rules applies.</param>
    /// <param name="calendar">Consulted only under <see cref="SlaClockBasis.BusinessHours"/>; may be null for a Critical-tier computation.</param>
    public static DateTime ComputeDueAtUtc(
        DateTime clockStartAtUtc,
        int targetMinutes,
        SlaClockBasis clockBasis,
        BusinessCalendarSnapshot? calendar)
    {
        if (targetMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMinutes), "An SLA target must be a positive number of minutes.");
        }

        var startUtc = AsUtc(clockStartAtUtc);

        if (clockBasis == SlaClockBasis.TwentyFourSeven)
        {
            // SLA-Architecture.md §3, verbatim: "computed by simple duration
            // addition from the start event, with no business-hours or
            // holiday-calendar exclusion". The calendar argument is ignored
            // here even when one is supplied.
            return startUtc.AddMinutes(targetMinutes);
        }

        ArgumentNullException.ThrowIfNull(calendar);
        return ComputeBusinessHoursDueAtUtc(startUtc, targetMinutes, calendar);
    }

    /// <summary>
    /// SLA-Architecture.md §4's walk. A start event outside the window (an
    /// evening, a Friday, a holiday) consumes nothing until the next working
    /// window opens — the behaviour §16's Example B demonstrates for its
    /// First Response figure.
    /// </summary>
    private static DateTime ComputeBusinessHoursDueAtUtc(
        DateTime startUtc, int targetMinutes, BusinessCalendarSnapshot calendar)
    {
        var cursorLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, calendar.TimeZone);
        var remaining = TimeSpan.FromMinutes(targetMinutes);

        for (var daysWalked = 0; daysWalked <= MaximumDaysWalked; daysWalked++)
        {
            var date = DateOnly.FromDateTime(cursorLocal);

            if (calendar.IsWorkingDate(date))
            {
                var dayStart = date.ToDateTime(calendar.BusinessDayStartLocal);
                var dayEnd = date.ToDateTime(calendar.BusinessDayEndLocal);

                if (cursorLocal < dayStart)
                {
                    cursorLocal = dayStart;
                }

                if (cursorLocal < dayEnd)
                {
                    var available = dayEnd - cursorLocal;

                    if (remaining <= available)
                    {
                        return ToUtc(cursorLocal + remaining, calendar.TimeZone);
                    }

                    remaining -= available;
                }
            }

            // Either a non-working date, or the window closed before the
            // remaining target was consumed: resume at the next date's
            // opening moment and let the loop re-test whether it works.
            cursorLocal = date.AddDays(1).ToDateTime(calendar.BusinessDayStartLocal);
        }

        throw new InvalidOperationException(
            $"SLA due-date computation did not converge within {MaximumDaysWalked} days — the business calendar is misconfigured "
            + "(check the working-day mask and the holiday list).");
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,

        // Local would silently import the host machine's zone into an SLA
        // calculation, which NFR-UAE-01 forbids; Unspecified is what EF Core
        // hands back for a datetime2 column, and every such column in this
        // solution stores UTC by convention.
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),

        _ => throw new ArgumentException(
            "SLA timestamps must be UTC (NFR-UAE-01). A DateTimeKind.Local value would import the host's own time zone into the calculation.",
            nameof(value))
    };

    /// <summary>
    /// Converts a calendar-local wall-clock moment back to UTC.
    ///
    /// <para>
    /// The approved pilot calendar is Asia/Dubai, which has no daylight
    /// saving, so no local moment is ever invalid or ambiguous there. The two
    /// guards below exist because the zone is configurable data (ADR-0010) —
    /// a calendar pointed at a DST-observing zone must not throw from inside
    /// an SLA calculation. An invalid (skipped) local time is moved forward
    /// past the gap; an ambiguous (repeated) one takes the earlier offset,
    /// the tighter of the two deadlines.
    /// </para>
    /// </summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(unspecified))
        {
            var adjustment = timeZone.GetUtcOffset(unspecified.AddDays(1)) - timeZone.GetUtcOffset(unspecified.AddDays(-1));
            unspecified = unspecified.Add(adjustment);
        }

        if (timeZone.IsAmbiguousTime(unspecified))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(unspecified);
            return DateTime.SpecifyKind(unspecified - offsets.Max(), DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
    }
}
