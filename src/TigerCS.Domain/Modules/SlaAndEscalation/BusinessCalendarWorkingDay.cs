namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md §2.20 — one row per <see cref="DayOfWeek"/> value
/// (0–6), exactly seven per calendar (MVP-ERD.md §2.20's app-enforced 1:7
/// cardinality). Seeded per ISSUE-017: Saturday–Thursday working, Friday off.
/// </summary>
public class BusinessCalendarWorkingDay
{
    public int BusinessCalendarWorkingDayId { get; private set; }
    public int BusinessCalendarId { get; private set; }

    /// <summary>0–6, matching <see cref="System.DayOfWeek"/>'s own numbering (Sunday = 0).</summary>
    public byte DayOfWeek { get; private set; }

    public bool IsWorkingDay { get; private set; }

    private BusinessCalendarWorkingDay() { }

    public BusinessCalendarWorkingDay(int businessCalendarId, DayOfWeek dayOfWeek, bool isWorkingDay)
    {
        BusinessCalendarId = businessCalendarId;
        DayOfWeek = (byte)dayOfWeek;
        IsWorkingDay = isWorkingDay;
    }

    public DayOfWeek AsDayOfWeek() => (DayOfWeek)DayOfWeek;
}
