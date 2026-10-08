namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// The pause/resume due-timestamp math (SLA-Architecture.md §8: "due
/// timestamps extended by the paused duration"), as a pure function.
///
/// <list type="bullet">
///   <item><description>
///     <b>24/7 clock</b> — due + the wall-clock paused duration. Exactly the
///     approved wording.
///   </description></item>
///   <item><description>
///     <b>Business-hours clock</b> — the business time still unspent at the
///     moment of the pause is measured, then re-walked from the resume
///     moment. A pause that only spanned a night, a weekend or a holiday
///     therefore does not push the deadline out by time the clock was not
///     counting anyway; a pause wholly inside working hours equals the
///     wall-clock paused duration. (Adding the raw wall-clock pause to a
///     business-hours due date would over-credit the customer-wait.)
///   </description></item>
/// </list>
///
/// <para>
/// The result is never earlier than the original due, so a pause can only
/// loosen a deadline by the time it stopped the clock — it cannot rewind one,
/// and it cannot un-breach anything (breach flags are one-way elsewhere).
/// </para>
/// </summary>
public static class SlaPauseCalculator
{
    public static DateTime ExtendedDueAtUtc(
        DateTime dueAtUtc,
        DateTime pausedAtUtc,
        DateTime resumedAtUtc,
        SlaClockBasis clockBasis,
        BusinessCalendarSnapshot? calendar)
    {
        if (resumedAtUtc <= pausedAtUtc)
        {
            return dueAtUtc;
        }

        if (clockBasis == SlaClockBasis.TwentyFourSeven)
        {
            return dueAtUtc + (resumedAtUtc - pausedAtUtc);
        }

        ArgumentNullException.ThrowIfNull(calendar);

        // The deadline had already passed when the pause began: nothing was
        // left to protect. (The application records that breach before it
        // ever opens a pause.)
        if (pausedAtUtc >= dueAtUtc)
        {
            return dueAtUtc;
        }

        var remaining = SlaDueDateCalculator.BusinessTimeBetween(pausedAtUtc, dueAtUtc, calendar);
        if (remaining <= TimeSpan.Zero)
        {
            // No business time was left to spend; the clock simply could not
            // expire before the pause ended.
            return resumedAtUtc > dueAtUtc ? resumedAtUtc : dueAtUtc;
        }

        var recomputed = SlaDueDateCalculator.ComputeDueAtUtc(resumedAtUtc, remaining, clockBasis, calendar);
        return recomputed > dueAtUtc ? recomputed : dueAtUtc;
    }
}
