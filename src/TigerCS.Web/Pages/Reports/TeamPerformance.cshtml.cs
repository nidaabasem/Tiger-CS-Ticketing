using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages.Reports;

/// <summary>
/// The CS Manager's Team Performance report: a GET filter form (employee,
/// agent type, department, period), one row per eligible agent with four
/// counts, a totals row, and — when a count is clicked — a records panel
/// listing the tickets behind that count. Every number comes from
/// <c>GET /api/reports/team-performance</c> and every record list from
/// <c>GET /api/reports/team-performance/records</c>; the page computes
/// nothing of its own, and a 403 from the Api renders as a "no permission"
/// state rather than an empty table.
/// </summary>
public sealed class TeamPerformanceModel(ReportsApiClient reportsApiClient) : PageModel
{
    // ---- bound filter state (query string) ----
    public DateOnly? DateFrom { get; private set; }
    public DateOnly? DateTo { get; private set; }
    public Guid? EmployeeId { get; private set; }
    public string? AgentType { get; private set; }
    public int? DepartmentId { get; private set; }

    /// <summary>The count being opened, when the URL names one (<c>?employeeId=..&amp;metric=..</c>); null otherwise.</summary>
    public string? Metric { get; private set; }

    public CurrentUser? Viewer { get; private set; }
    public TeamPerformanceReportDto? Report { get; private set; }
    public ApiOutcome Outcome { get; private set; }

    public TeamPerformanceRecordsDto? Records { get; private set; }
    public ApiOutcome RecordsOutcome { get; private set; }

    /// <summary>True when the user narrowed anything — the filter bar then offers "Clear".</summary>
    public bool HasFilters =>
        DateFrom is not null || DateTo is not null || EmployeeId is not null || !string.IsNullOrWhiteSpace(AgentType) || DepartmentId is not null;

    /// <summary>The effective period, as the Api applied it ("Aug 12 – Sep 10, 2026").</summary>
    public string PeriodLabel => Report is null
        ? string.Empty
        : $"{Report.Filters.DateFrom:MMM d} – {Report.Filters.DateTo:MMM d, yyyy}";

    public async Task OnGetAsync(
        DateOnly? dateFrom, DateOnly? dateTo, Guid? employeeId, string? agentType, int? departmentId, string? metric,
        CancellationToken cancellationToken)
    {
        Viewer = CurrentUser.FromPrincipal(User);
        DateFrom = dateFrom;
        DateTo = dateTo;
        EmployeeId = employeeId;
        AgentType = string.IsNullOrWhiteSpace(agentType) ? null : agentType;
        DepartmentId = departmentId;
        Metric = string.IsNullOrWhiteSpace(metric) ? null : metric;

        // Opening a count keeps the whole report on screen, filtered to that
        // one employee, with the records panel beneath it — so the row the
        // number came from stays visible beside its list.
        var result = await reportsApiClient.GetTeamPerformanceAsync(
            new TeamPerformanceRequestDto(DateFrom, DateTo, EmployeeId, AgentType, DepartmentId), cancellationToken);
        Outcome = result.Outcome;
        if (!result.IsSuccess || result.Value is null)
        {
            return;
        }

        Report = result.Value;

        if (EmployeeId is { } openedEmployee && Metric is { } openedMetric)
        {
            var records = await reportsApiClient.GetTeamPerformanceRecordsAsync(
                new TeamPerformanceRecordsRequestDto(openedEmployee, openedMetric, DateFrom, DateTo), cancellationToken);
            RecordsOutcome = records.Outcome;
            Records = records.IsSuccess ? records.Value : null;
        }
    }

    /// <summary>The URL that opens one employee's count — the same page, with the period kept so the list matches the number.</summary>
    public string RecordsHref(Guid employeeId, TeamPerformanceMetric metric) =>
        BuildHref(employeeId, metric.ToString(), keepAgentTypeAndDepartment: false);

    /// <summary>The URL back to the full report under the current period, role and department filters, with no count open and every agent shown.</summary>
    public string ReportHref => BuildHref(null, null, keepAgentTypeAndDepartment: true);

    private string BuildHref(Guid? employeeId, string? metric, bool keepAgentTypeAndDepartment)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (DateFrom is DateOnly from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateTo is DateOnly to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (keepAgentTypeAndDepartment && !string.IsNullOrWhiteSpace(AgentType)) query["agentType"] = AgentType;
        if (keepAgentTypeAndDepartment && DepartmentId is int departmentId) query["departmentId"] = departmentId.ToString(CultureInfo.InvariantCulture);
        if (employeeId is Guid employee) query["employeeId"] = employee.ToString();
        if (metric is not null) query["metric"] = metric;
        return query.Count == 0 ? "/Reports/TeamPerformance" : $"/Reports/TeamPerformance?{query}";
    }

    /// <summary>The column heading for a metric, as shown on the table and on the records panel.</summary>
    public static string MetricLabel(string? metric) =>
        Enum.TryParse<TeamPerformanceMetric>(metric, ignoreCase: true, out var parsed)
            ? parsed switch
            {
                TeamPerformanceMetric.CurrentlyAssigned => "Currently Assigned",
                TeamPerformanceMetric.TicketsWorked => "Tickets Worked",
                TeamPerformanceMetric.CompletedFollowUps => "Completed Follow-ups",
                TeamPerformanceMetric.SlaBreaches => "SLA Breaches",
                _ => parsed.ToString()
            }
            : metric ?? string.Empty;

    /// <summary>The departments cell: a comma-joined list, primary first (the Api already orders it).</summary>
    public static string DepartmentsText(IReadOnlyList<TeamPerformanceDepartmentDto> departments) =>
        departments.Count == 0 ? "—" : string.Join(", ", departments.Select(d => d.Name));

    /// <summary>The message for a report that could not be loaded — a 403 is a permission answer, never a technical one.</summary>
    public static string LoadFailureMessage(ApiOutcome outcome) => outcome switch
    {
        ApiOutcome.Forbidden => "You do not have permission to view this report. It is available to CS Managers, General Managers and the Chairman/CEO.",
        ApiOutcome.Unauthorized => "Your session is no longer valid. Sign in again.",
        ApiOutcome.Unreachable or ApiOutcome.BadGateway => "Tiger Ticketing System could not reach the ticketing service.",
        _ => "The report could not be loaded right now."
    };
}
