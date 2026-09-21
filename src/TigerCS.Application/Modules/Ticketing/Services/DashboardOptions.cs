namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Dashboard presentation thresholds, bound from the <c>Dashboard</c>
/// configuration section.
///
/// <para>
/// <b>Everything here is a display threshold, never a business rule.</b> A
/// value in this class changes what the dashboard paints amber; it does not
/// change what the system permits, what it measures, or what it records.
/// Contractual SLA targets live in <c>SlaPolicies</c>, in the database, and
/// are not configurable from here.
/// </para>
/// </summary>
public sealed class DashboardOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Dashboard";

    /// <summary>
    /// The age, in seconds, at which a customer waiting for a human agent is
    /// rendered as at risk. Defaults to
    /// <see cref="DashboardAppService.DefaultHumanWaitRiskThresholdSeconds"/>
    /// (15 minutes). A non-positive value falls back to that default rather
    /// than painting every row at risk.
    /// </summary>
    public int HumanWaitRiskThresholdSeconds { get; set; } = DashboardAppService.DefaultHumanWaitRiskThresholdSeconds;
}
