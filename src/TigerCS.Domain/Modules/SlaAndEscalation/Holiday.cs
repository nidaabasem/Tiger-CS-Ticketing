namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md §2.20 / ADR-0010 — a single non-working date
/// excluded from every non-Critical SLA computation, unique per calendar.
///
/// <para>
/// <b><see cref="ConfirmedByEmployeeId"/> is nullable, and this increment
/// treats an unconfirmed holiday as effective.</b> ISSUE-012 splits
/// ownership (a System Administrator enters the date; Customer Service/HR
/// confirms it), and MVP-API-Contracts.md §5.11 explicitly records that
/// "whether an unconfirmed holiday already affects SLA math is itself
/// unresolved and called out again here rather than silently decided." No
/// holiday-administration endpoint ships in this increment
/// (MVP-Implementation-Backlog.md §0.2 puts the live holiday-administration
/// UI in stretch), so no row can reach the database unconfirmed except by
/// seed or direct operator action — but the calculation still has to answer
/// the question for any row it reads. It counts every seeded/entered row,
/// which is the safer direction: a genuine public holiday that HR has not
/// yet countersigned still stops the clock, rather than silently running an
/// SLA through a day the business was closed. Flagged, not decided
/// unilaterally: if management resolves ISSUE-012's open question the other
/// way, the change is one predicate in
/// <c>BusinessCalendarSnapshot.IsWorkingDate</c>.
/// </para>
/// </summary>
public class Holiday
{
    public int HolidayId { get; private set; }
    public int BusinessCalendarId { get; private set; }

    /// <summary>A calendar-local date (never a UTC instant) — the whole local day is excluded.</summary>
    public DateOnly HolidayDate { get; private set; }

    public string? Description { get; private set; }

    /// <summary>The technical administrator who entered the date (ISSUE-012).</summary>
    public Guid EnteredByEmployeeId { get; private set; }

    /// <summary>The business owner who confirmed it, if the eventual ISSUE-012 answer requires confirmation — see this type's remarks.</summary>
    public Guid? ConfirmedByEmployeeId { get; private set; }

    public DateTime? ConfirmedAtUtc { get; private set; }

    private Holiday() { }

    public Holiday(int businessCalendarId, DateOnly holidayDate, string? description, Guid enteredByEmployeeId)
    {
        if (enteredByEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("EnteredByEmployeeId is required (ISSUE-012).", nameof(enteredByEmployeeId));
        }

        BusinessCalendarId = businessCalendarId;
        HolidayDate = holidayDate;
        Description = description;
        EnteredByEmployeeId = enteredByEmployeeId;
    }
}
