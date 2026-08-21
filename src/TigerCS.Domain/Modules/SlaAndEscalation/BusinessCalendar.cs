namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md §2.20 / MVP-ERD.md §2.20 / ADR-0010 — the weekly
/// working pattern and business-day window consulted by every non-Critical
/// due-date computation.
///
/// <para>
/// Reference data, deliberately not hardcoded: ISSUE-017 approved
/// Saturday–Thursday, 08:00–18:00, Friday off, but approved it "explicitly
/// still built as configurable data" (ADR-0010) so the working week and
/// window can be corrected without a redeploy. A retired calendar is
/// superseded by a new effective-dated row, never deleted (MVP-ERD.md §2.20).
/// </para>
/// </summary>
public class BusinessCalendar
{
    public int BusinessCalendarId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public TimeOnly BusinessDayStartLocal { get; private set; }
    public TimeOnly BusinessDayEndLocal { get; private set; }

    /// <summary>
    /// The zone <see cref="BusinessDayStartLocal"/>/<see cref="BusinessDayEndLocal"/>
    /// and every <see cref="Holiday.HolidayDate"/> are expressed in. Persisted
    /// SLA timestamps remain UTC (NFR-UAE-01); this is what the calculation
    /// converts through, and the only place a local-time concept exists at all.
    /// </summary>
    public string TimeZone { get; private set; } = string.Empty;

    public DateTime EffectiveFromUtc { get; private set; }
    public bool IsActive { get; private set; }

    private BusinessCalendar() { }

    public BusinessCalendar(
        string name,
        TimeOnly businessDayStartLocal,
        TimeOnly businessDayEndLocal,
        string timeZone,
        DateTime effectiveFromUtc,
        bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(timeZone))
        {
            throw new ArgumentException("TimeZone is required.", nameof(timeZone));
        }

        if (businessDayEndLocal <= businessDayStartLocal)
        {
            throw new ArgumentException(
                "BusinessDayEndLocal must be later than BusinessDayStartLocal — an overnight business window is not part of the approved calendar model (MVP-Data-Dictionary.md §2.20).",
                nameof(businessDayEndLocal));
        }

        Name = name;
        BusinessDayStartLocal = businessDayStartLocal;
        BusinessDayEndLocal = businessDayEndLocal;
        TimeZone = timeZone;
        EffectiveFromUtc = effectiveFromUtc;
        IsActive = isActive;
    }
}
