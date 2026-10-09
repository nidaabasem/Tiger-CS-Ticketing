using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.Reporting.Abstractions;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Reporting.Services;

/// <summary>
/// The CS Manager's Team Performance report: one row per eligible agent
/// with four counts, and the records behind any one count.
///
/// <para>
/// <b>Who is on the report.</b> Every ACTIVE employee holding one of
/// <see cref="EligibleRoles"/> in Identity — today exactly the "CS Agent"
/// role. There is no separate "Call Center Agent" role: per
/// <c>docs/Genesys/Genesys-Cloud-Configuration.md</c> §4, call-centre agents
/// are CS Agents who belong to the Call Center department (code
/// <see cref="CallCenterDepartmentCode"/>). The report therefore derives an
/// <b>agent type</b> per employee from department membership rather than
/// from a role, and shows one row per employee however many roles or
/// departments they hold. Employees with no activity in the period are
/// included with zeros.
/// </para>
///
/// <para>
/// <b>Period.</b> UTC calendar days, inclusive, defaulting to the last 30
/// days exactly as the Dashboard does
/// (<see cref="DashboardAppService.ResolvePeriod"/>). Currently Assigned is
/// the one current-state figure and ignores the period.
/// </para>
///
/// <para>
/// Read-only: nothing here changes state, so no audit entry is written.
/// Authorization (CS Manager / General Manager / Chairman-CEO, plus the
/// System Administrator override) is the Api's policy, not decided here.
/// </para>
/// </summary>
public sealed class TeamPerformanceAppService(
    ITeamPerformanceQueryRepository repository,
    TimeProvider timeProvider,
    IServiceIdentityRegistry? serviceIdentities = null)
{
    /// <summary>
    /// The Identity roles whose holders appear on the report. A single
    /// place to extend (e.g. to add CS Supervisor) — deliberately only
    /// "CS Agent" for now, because the report is about the agents a CS
    /// Manager supervises, and supervisors are not measured on the same
    /// four counts.
    /// </summary>
    public static readonly IReadOnlyList<string> EligibleRoles = [Roles.CsAgent];

    /// <summary>
    /// The Call Center department's code — the same code
    /// <c>WorkflowReferenceData.CallCenterCode</c> seeds. Membership of the
    /// department with this code is what makes a CS Agent a "Call Center
    /// Agent" on this report; a rename of the department keeps the code,
    /// so the classification survives it.
    /// </summary>
    public const string CallCenterDepartmentCode = "CC";

    public async Task<TeamPerformanceReportDto> GetReportAsync(
        TeamPerformanceRequestDto request, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var (dateFrom, dateTo) = DashboardAppService.ResolvePeriod(request.DateFrom, request.DateTo, today);
        var agentType = TeamPerformanceAgentTypes.Normalize(request.AgentType);

        // A configured service identity (Genesys integration account) holds the CS Agent role
        // but is not a person to measure.
        var everyone = (await repository.GetEmployeesInRolesAsync(EligibleRoles, cancellationToken))
            .Where(e => serviceIdentities is null || !serviceIdentities.IsServiceIdentity(e.EmployeeId))
            .Select(e => (Employee: e, AgentType: AgentTypeOf(e)))
            .OrderBy(e => e.Employee.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Employee.EmployeeId)
            .ToList();

        // Filters narrow the rows; the picker options always offer the whole
        // eligible team so a manager can move between filters freely.
        var selected = everyone
            .Where(e => request.EmployeeId is not { } employeeId || e.Employee.EmployeeId == employeeId)
            .Where(e => agentType is null || e.AgentType == agentType)
            .Where(e => request.DepartmentId is not { } departmentId || e.Employee.Departments.Any(d => d.DepartmentId == departmentId))
            .ToList();

        var metrics = selected.Count == 0
            ? EmptyMetrics
            : await repository.GetMetricsAsync(
                selected.Select(e => e.Employee.EmployeeId).ToList(),
                RangeStartUtc(dateFrom),
                RangeEndUtc(dateTo),
                cancellationToken);

        var rows = selected.Select(e => new TeamPerformanceRowDto(
            e.Employee.EmployeeId,
            e.Employee.DisplayName,
            e.AgentType,
            e.Employee.Departments.Select(d => new TeamPerformanceDepartmentDto(d.DepartmentId, d.Name, d.IsPrimary)).ToList(),
            metrics.CurrentlyAssigned.GetValueOrDefault(e.Employee.EmployeeId),
            metrics.TicketsWorked.GetValueOrDefault(e.Employee.EmployeeId),
            metrics.CompletedFollowUps.GetValueOrDefault(e.Employee.EmployeeId),
            metrics.SlaBreaches.GetValueOrDefault(e.Employee.EmployeeId))).ToList();

        var departments = everyone
            .SelectMany(e => e.Employee.Departments)
            .GroupBy(d => d.DepartmentId)
            .Select(g => new TeamPerformanceOptionDto(g.Key.ToString(), g.First().Name))
            .OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TeamPerformanceReportDto(
            new TeamPerformanceAppliedFiltersDto(dateFrom, dateTo, request.EmployeeId, agentType, request.DepartmentId),
            rows,
            new TeamPerformanceTotalsDto(
                rows.Count,
                rows.Sum(r => r.CurrentlyAssigned),
                rows.Sum(r => r.TicketsWorked),
                rows.Sum(r => r.CompletedFollowUps),
                rows.Sum(r => r.SlaBreaches)),
            new TeamPerformanceFilterOptionsDto(
                everyone.Select(e => new TeamPerformanceOptionDto(e.Employee.EmployeeId.ToString(), e.Employee.DisplayName, e.AgentType)).ToList(),
                departments,
                TeamPerformanceAgentTypes.All));
    }

    public async Task<TeamPerformanceRecordsResult> GetRecordsAsync(
        TeamPerformanceRecordsRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<TeamPerformanceMetric>(request.Metric, ignoreCase: true, out var metric) || !Enum.IsDefined(metric))
        {
            return new TeamPerformanceRecordsResult(TeamPerformanceRecordsOutcome.UnknownMetric);
        }

        // The same eligibility rule as the report: a count can only be
        // opened for an employee who has a row.
        var employee = (await repository.GetEmployeesInRolesAsync(EligibleRoles, cancellationToken))
            .FirstOrDefault(e => e.EmployeeId == request.EmployeeId
                && (serviceIdentities is null || !serviceIdentities.IsServiceIdentity(e.EmployeeId)));
        if (employee is null)
        {
            return new TeamPerformanceRecordsResult(TeamPerformanceRecordsOutcome.EmployeeNotEligible);
        }

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var (dateFrom, dateTo) = DashboardAppService.ResolvePeriod(request.DateFrom, request.DateTo, today);

        var rows = await repository.GetRecordsAsync(
            employee.EmployeeId, metric, RangeStartUtc(dateFrom), RangeEndUtc(dateTo), cancellationToken);

        return new TeamPerformanceRecordsResult(
            TeamPerformanceRecordsOutcome.Success,
            new TeamPerformanceRecordsDto(
                employee.EmployeeId,
                employee.DisplayName,
                AgentTypeOf(employee),
                metric.ToString(),
                dateFrom,
                dateTo,
                rows.Select(r => new TeamPerformanceRecordDto(
                    r.TicketId, r.TicketNumber, r.RequestSummary, r.TicketStatus.ToString(), r.PriorityId,
                    r.DepartmentName, r.CreatedAtUtc, r.BreachedAtUtc, r.CompletedAtUtc)).ToList()));
    }

    /// <summary>"Call Center Agent" when any membership is the department whose code is <see cref="CallCenterDepartmentCode"/>; otherwise "CS Agent".</summary>
    public static string AgentTypeOf(TeamPerformanceEmployeeRow employee) =>
        employee.Departments.Any(d => string.Equals(d.Code, CallCenterDepartmentCode, StringComparison.OrdinalIgnoreCase))
            ? TeamPerformanceAgentTypes.CallCenterAgent
            : TeamPerformanceAgentTypes.CsAgent;

    /// <summary>The inclusive start of the first period day, as a UTC instant.</summary>
    public static DateTime RangeStartUtc(DateOnly dateFrom) => dateFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>The exclusive end of the last period day: midnight of the day after, as a UTC instant.</summary>
    public static DateTime RangeEndUtc(DateOnly dateTo) => dateTo.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static readonly TeamPerformanceMetricsSnapshot EmptyMetrics = new(
        new Dictionary<Guid, int>(), new Dictionary<Guid, int>(), new Dictionary<Guid, int>(), new Dictionary<Guid, int>());
}
