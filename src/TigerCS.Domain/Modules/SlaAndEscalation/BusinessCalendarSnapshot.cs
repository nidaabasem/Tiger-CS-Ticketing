namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// An immutable, already-resolved view of one <see cref="BusinessCalendar"/>
/// plus its seven working-day rows and its holiday dates — the exact input
/// <see cref="SlaDueDateCalculator"/> needs and nothing else.
///
/// <para>
/// It exists so the due-date calculation stays a pure function of its
/// arguments (ADR-0010: "a pure function <c>IsBusinessMoment(dateTimeUtc)</c>
/// consuming this reference data is trivially unit-testable"). The
/// calculator never touches a repository, a clock, or a
/// <see cref="TimeZoneInfo"/> lookup, so every worked example in
/// SLA-Architecture.md can be asserted directly against it.
/// </para>
/// </summary>
public sealed class BusinessCalendarSnapshot
{
    private readonly bool[] _isWorkingDayOfWeek = new bool[7];
    private readonly HashSet<DateOnly> _holidays;

    /// <summary>The zone the window and holiday dates are expressed in. Resolved once by the caller, so this type performs no environment-dependent lookup.</summary>
    public TimeZoneInfo TimeZone { get; }

    public TimeOnly BusinessDayStartLocal { get; }

    public TimeOnly BusinessDayEndLocal { get; }

    /// <summary>Length of one full working day, used to express a "business day" target in minutes and to bound the walk in <see cref="SlaDueDateCalculator"/>.</summary>
    public TimeSpan BusinessDayLength => BusinessDayEndLocal - BusinessDayStartLocal;

    public BusinessCalendarSnapshot(
        TimeZoneInfo timeZone,
        TimeOnly businessDayStartLocal,
        TimeOnly businessDayEndLocal,
        IEnumerable<DayOfWeek> workingDays,
        IEnumerable<DateOnly> holidays)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(workingDays);
        ArgumentNullException.ThrowIfNull(holidays);

        if (businessDayEndLocal <= businessDayStartLocal)
        {
            throw new ArgumentException(
                "BusinessDayEndLocal must be later than BusinessDayStartLocal.", nameof(businessDayEndLocal));
        }

        foreach (var day in workingDays)
        {
            _isWorkingDayOfWeek[(int)day] = true;
        }

        if (Array.TrueForAll(_isWorkingDayOfWeek, working => !working))
        {
            // Without this the walk in SlaDueDateCalculator could never
            // consume a single minute and would spin to its iteration guard.
            // Failing here instead names the real cause — a calendar with no
            // working day at all is misconfigured reference data.
            throw new ArgumentException("A business calendar must have at least one working day.", nameof(workingDays));
        }

        TimeZone = timeZone;
        BusinessDayStartLocal = businessDayStartLocal;
        BusinessDayEndLocal = businessDayEndLocal;
        _holidays = [.. holidays];
    }

    /// <summary>True when <paramref name="localDate"/> is a working weekday for this calendar and is not one of its holiday dates (SLA-Architecture.md §4).</summary>
    public bool IsWorkingDate(DateOnly localDate) =>
        _isWorkingDayOfWeek[(int)localDate.DayOfWeek] && !_holidays.Contains(localDate);
}
