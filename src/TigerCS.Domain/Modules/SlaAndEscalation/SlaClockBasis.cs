namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md §2.6 — <c>SlaPolicies.ClockBasis</c>
/// ("1=24/7, 2=BusinessHours"). Numeric values match that column exactly.
///
/// <para>
/// The split is a fixed rule, not configurable behaviour:
/// SLA-Architecture.md §3 states Critical-tier due timestamps are computed
/// by simple duration addition "with <b>no</b> business-hours or
/// holiday-calendar exclusion", and §4 states High/Medium/Low walk forward
/// counting only time inside the configured business window. Which basis a
/// tier uses is configurable data (this column); what each basis <i>means</i>
/// is not.
/// </para>
/// </summary>
public enum SlaClockBasis : byte
{
    /// <summary>Calendar time. The business calendar and holiday list are not consulted at all (SLA-Architecture.md §3).</summary>
    TwentyFourSeven = 1,

    /// <summary>Only time inside the calendar's working-day window counts (SLA-Architecture.md §4).</summary>
    BusinessHours = 2
}
