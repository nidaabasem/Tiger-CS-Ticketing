using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Seed;

/// <summary>
/// The approved SLA reference data — the per-priority policies and the
/// default business calendar (backlog S-04's Definition of Done: "seed data
/// for <c>Priorities</c>/<c>SlaPolicies</c>/<c>Departments</c>/default
/// <c>BusinessCalendars</c> loads").
///
/// <para>
/// Defined once, here, and consumed by both the development seed and the
/// integration-test host, so the contractually-load-bearing target values
/// exist in exactly one place and a test can never pass against numbers that
/// differ from the ones a real deployment gets.
/// </para>
/// </summary>
public static class SlaReferenceData
{
    /// <summary>
    /// ISSUE-017, approved Option A: Saturday–Thursday working, Friday off,
    /// 08:00–18:00, UAE. Stored as configurable data rather than hardcoded
    /// logic (ADR-0010) — this is the seeded default, not a constant the
    /// calculation reads.
    /// </summary>
    public const string DefaultCalendarName = "Default Pilot Calendar (UAE)";

    /// <summary>IANA identifier for the UAE. Asia/Dubai observes no daylight saving, so its offset is a fixed UTC+04:00.</summary>
    public const string DefaultTimeZoneId = "Asia/Dubai";

    public static readonly TimeOnly DefaultBusinessDayStartLocal = new(8, 0);
    public static readonly TimeOnly DefaultBusinessDayEndLocal = new(18, 0);

    /// <summary>Friday is the only non-working day of the approved week (ISSUE-017 Option A).</summary>
    public static readonly IReadOnlyList<DayOfWeek> DefaultWorkingDays =
    [
        DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday,
        DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday
    ];

    /// <summary>Minutes in one full working day under the approved 08:00–18:00 window — the conversion factor below.</summary>
    public const int BusinessMinutesPerDay = 600;

    /// <summary>
    /// The approved per-tier targets (Solution-Analysis.md §7.1,
    /// SLA-Architecture.md §1/§2), with the two thresholds of §10/§12 that
    /// this increment seeds but does not consume.
    ///
    /// <para>
    /// <b>Two of the six targets are stated in the source as business days
    /// and are stored here in minutes,</b> because
    /// MVP-Data-Dictionary.md §2.6 types both target columns as
    /// <c>int</c> minutes. Under the approved 08:00–18:00 window a business
    /// day is <see cref="BusinessMinutesPerDay"/> minutes, so Medium's "3
    /// business days" is 1&#160;800 and Low's "7 business days" is 4&#160;200.
    /// That is arithmetic on approved values, not a new business rule — and
    /// it is exact only because the window itself is seeded here too; a
    /// deployment that edits the window must revisit these two numbers.
    /// </para>
    ///
    /// <para>
    /// <b>High's "24 hours" is 1&#160;440 minutes <i>of business time</i></b>,
    /// not calendar time, because §7.1 puts High on the "Business hours only"
    /// clock basis. Every non-Critical target is consumed the same way, so
    /// the unit is consistent across the tier.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SlaPolicy> Policies() =>
    [
        // Critical: 15 min / 4 h, 24/7 — never touches the calendar
        // (SLA-Architecture.md §3). Warning at 50%; Level 2→GM window 30
        // minutes (§10/§12 — seeded, not consumed here).
        new((byte)PriorityLevel.Critical, 15, 4 * 60, SlaClockBasis.TwentyFourSeven, 50.00m, 30, (byte)SlaWindowUnit.Minutes),

        // High: 1 h / 24 h of business time. Warning at 75%; window 2 hours.
        new((byte)PriorityLevel.High, 60, 24 * 60, SlaClockBasis.BusinessHours, 75.00m, 2, (byte)SlaWindowUnit.Hours),

        // Medium: 4 h / 3 business days of business time. Window 1 business day.
        new((byte)PriorityLevel.Medium, 4 * 60, 3 * BusinessMinutesPerDay, SlaClockBasis.BusinessHours, 75.00m, 1, (byte)SlaWindowUnit.BusinessDays),

        // Low: 24 h / 7 business days of business time. Window 2 business days.
        new((byte)PriorityLevel.Low, 24 * 60, 7 * BusinessMinutesPerDay, SlaClockBasis.BusinessHours, 75.00m, 2, (byte)SlaWindowUnit.BusinessDays)
    ];

    /// <summary>
    /// Seeds the SLA policies and the default business calendar if they are
    /// not already present. Idempotent, and safe to call on every startup.
    ///
    /// <para>
    /// No <c>Holidays</c> rows are seeded. ADR-0010 makes holiday entry a
    /// manual annual process with a named business owner (ISSUE-012), and
    /// every row requires an <c>EnteredByEmployeeId</c> — inventing dates
    /// under a fabricated actor would put unverified data behind a
    /// contractual calculation.
    /// </para>
    /// </summary>
    public static async Task SeedAsync(TigerCsDbContext dbContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        if (!await dbContext.SlaPolicies.AnyAsync(cancellationToken))
        {
            dbContext.SlaPolicies.AddRange(Policies());
        }

        if (!await dbContext.BusinessCalendars.AnyAsync(cancellationToken))
        {
            var calendar = new BusinessCalendar(
                DefaultCalendarName,
                DefaultBusinessDayStartLocal,
                DefaultBusinessDayEndLocal,
                DefaultTimeZoneId,
                // Effective from the Unix epoch so no ticket created during
                // the pilot can predate the only calendar there is.
                DateTime.UnixEpoch);

            dbContext.BusinessCalendars.Add(calendar);

            // Saved before the working days so the calendar's generated key
            // exists to reference.
            await dbContext.SaveChangesAsync(cancellationToken);

            // All seven days, working and non-working alike — MVP-ERD.md
            // §2.20's "exactly 7 rows (one per DayOfWeek) per calendar".
            foreach (var dayOfWeek in Enum.GetValues<DayOfWeek>())
            {
                dbContext.BusinessCalendarWorkingDays.Add(
                    new BusinessCalendarWorkingDay(
                        calendar.BusinessCalendarId, dayOfWeek, DefaultWorkingDays.Contains(dayOfWeek)));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>MVP-Data-Dictionary.md §2.6 — <c>SlaPolicies.Level2ToGmWindowUnit</c> ("1=Minutes, 2=Hours, 3=BusinessDays"). Seeded, not consumed by this increment; the timed Level 2→3 advance it belongs to is out of scope (MVP-Implementation-Backlog.md §0.2).</summary>
public enum SlaWindowUnit : byte
{
    Minutes = 1,
    Hours = 2,
    BusinessDays = 3
}
