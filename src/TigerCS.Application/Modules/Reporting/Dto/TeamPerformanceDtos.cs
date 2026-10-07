namespace TigerCS.Application.Modules.Reporting.Dto;

// ---- Team Performance report (CS Manager): GET /api/reports/team-performance ----

/// <summary>
/// The Team Performance report's filters, bound from the query string.
/// Every filter is optional. Dates are UTC calendar days (the same "UTC
/// day" convention the Dashboard uses) and default to the last 30 days
/// when both are omitted.
/// </summary>
/// <param name="DateFrom">First UTC calendar day of the period (inclusive).</param>
/// <param name="DateTo">Last UTC calendar day of the period (inclusive).</param>
/// <param name="EmployeeId">Narrow to one eligible employee.</param>
/// <param name="AgentType">Narrow to one agent type: <c>CS Agent</c> or <c>Call Center Agent</c> (see <see cref="TeamPerformanceAgentTypes"/>).</param>
/// <param name="DepartmentId">Narrow to employees who are members of this department.</param>
public sealed record TeamPerformanceRequestDto(
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    Guid? EmployeeId = null,
    string? AgentType = null,
    int? DepartmentId = null);

/// <summary>
/// The Team Performance report: one row per eligible employee (active, in
/// the CS Agent role), sorted by display name, with the four metrics and a
/// totals row. Employees with no activity in the period are included with
/// zeros — a manager sees the whole team, never only the busy part of it.
/// </summary>
/// <param name="Filters">The filters as actually applied — including the defaulted period.</param>
/// <param name="Rows">One row per employee, sorted by display name; never a duplicate row for an employee with several roles or departments.</param>
/// <param name="Totals">The sum of every row shown (after filters).</param>
/// <param name="FilterOptions">The picker options: every eligible employee, the departments they belong to, and the two agent types.</param>
public sealed record TeamPerformanceReportDto(
    TeamPerformanceAppliedFiltersDto Filters,
    IReadOnlyList<TeamPerformanceRowDto> Rows,
    TeamPerformanceTotalsDto Totals,
    TeamPerformanceFilterOptionsDto FilterOptions);

/// <summary>The filters as applied, echoed back so the page can render the effective state (e.g. the defaulted period).</summary>
public sealed record TeamPerformanceAppliedFiltersDto(
    DateOnly DateFrom,
    DateOnly DateTo,
    Guid? EmployeeId,
    string? AgentType,
    int? DepartmentId);

/// <summary>
/// One employee's row. The metric definitions are the business rules of
/// this report and are restated on the page's help block and in
/// <c>docs/Team-Performance-Report.md</c>.
/// </summary>
/// <param name="EmployeeId">The employee (AspNetUsers.Id / Employees.EmployeeId).</param>
/// <param name="DisplayName">The employee's display name.</param>
/// <param name="AgentType"><c>Call Center Agent</c> when the employee is a member of the Call Center department (code <c>CC</c>); otherwise <c>CS Agent</c>.</param>
/// <param name="Departments">The employee's department memberships, primary first.</param>
/// <param name="CurrentlyAssigned">Current state, not period-bound: tickets whose <c>CurrentOwnerEmployeeId</c> is this employee and whose status is not Closed.</param>
/// <param name="TicketsWorked">Distinct tickets on which the employee performed at least one recorded action within the period (assignment to them, a status change, a note, a resolution, a workflow event, or a customer follow-up assigned to or completed by them). Measured from history, not from current ownership.</param>
/// <param name="CompletedFollowUps">Customer follow-ups (<c>TicketAgentHandoffs</c>) assigned to the employee and completed within the period. Counts work items, not tickets.</param>
/// <param name="SlaBreaches">Distinct tickets whose SLA breach was recorded within the period while this employee held the ticket (the latest assignment at or before the breach). Each breach is attributed to at most one employee.</param>
public sealed record TeamPerformanceRowDto(
    Guid EmployeeId,
    string DisplayName,
    string AgentType,
    IReadOnlyList<TeamPerformanceDepartmentDto> Departments,
    int CurrentlyAssigned,
    int TicketsWorked,
    int CompletedFollowUps,
    int SlaBreaches);

/// <summary>One department membership of an employee on the report.</summary>
public sealed record TeamPerformanceDepartmentDto(int DepartmentId, string Name, bool IsPrimary);

/// <summary>The totals row: every figure is the sum of the rows shown. Tickets worked and SLA breaches are summed per employee, so a ticket two agents both worked counts twice here — by design, this is the team's effort, not a ticket count.</summary>
public sealed record TeamPerformanceTotalsDto(
    int Employees,
    int CurrentlyAssigned,
    int TicketsWorked,
    int CompletedFollowUps,
    int SlaBreaches);

/// <summary>A filter picker option; <see cref="GroupLabel"/> carries the employee's agent type on the employee picker.</summary>
public sealed record TeamPerformanceOptionDto(string Id, string Label, string? GroupLabel = null);

/// <summary>The picker options — every eligible employee (not only the filtered ones), the departments any of them belongs to, and the fixed agent types.</summary>
public sealed record TeamPerformanceFilterOptionsDto(
    IReadOnlyList<TeamPerformanceOptionDto> Employees,
    IReadOnlyList<TeamPerformanceOptionDto> Departments,
    IReadOnlyList<string> AgentTypes);

/// <summary>The two agent types the report distinguishes. Both hold the same Identity role (CS Agent); what tells them apart is Call Center department membership.</summary>
public static class TeamPerformanceAgentTypes
{
    public const string CsAgent = "CS Agent";
    public const string CallCenterAgent = "Call Center Agent";

    public static readonly IReadOnlyList<string> All = [CsAgent, CallCenterAgent];

    /// <summary>Case-insensitive match to one of the known agent types, or null when the value is not one of them.</summary>
    public static string? Normalize(string? value) =>
        All.FirstOrDefault(t => string.Equals(t, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

// ---- Records behind one count: GET /api/reports/team-performance/records ----

/// <summary>Which of the four counts a records request opens.</summary>
public enum TeamPerformanceMetric
{
    CurrentlyAssigned,
    TicketsWorked,
    CompletedFollowUps,
    SlaBreaches
}

/// <summary>
/// The records behind one count on the report, for one employee. The
/// period is interpreted exactly as on the report (UTC days, inclusive,
/// defaulted the same way), so the list always matches the number.
/// </summary>
/// <param name="EmployeeId">The employee whose count is being opened.</param>
/// <param name="Metric">One of CurrentlyAssigned, TicketsWorked, CompletedFollowUps, SlaBreaches.</param>
/// <param name="DateFrom">First UTC calendar day of the period (inclusive); ignored for CurrentlyAssigned.</param>
/// <param name="DateTo">Last UTC calendar day of the period (inclusive); ignored for CurrentlyAssigned.</param>
public sealed record TeamPerformanceRecordsRequestDto(
    Guid EmployeeId,
    string Metric,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null);

/// <summary>The records behind one count — the employee, the metric and the period they were computed for, and the ticket rows.</summary>
public sealed record TeamPerformanceRecordsDto(
    Guid EmployeeId,
    string DisplayName,
    string AgentType,
    string Metric,
    DateOnly DateFrom,
    DateOnly DateTo,
    IReadOnlyList<TeamPerformanceRecordDto> Records);

/// <summary>One record: the ticket's compact facts plus, for SLA breaches, when the breach was recorded and, for completed follow-ups, when the follow-up was completed.</summary>
/// <param name="TicketId">Links to Ticket Details.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="RequestSummary">The ticket's one-line summary.</param>
/// <param name="TicketStatus">The ticket's current lifecycle status.</param>
/// <param name="PriorityId">1=Critical, 2=High, 3=Medium, 4=Low, or null while Unclassified.</param>
/// <param name="DepartmentName">The department that currently holds the ticket.</param>
/// <param name="CreatedAtUtc">When the ticket was created.</param>
/// <param name="BreachedAtUtc">SlaBreaches only: when the breach was recorded (the earliest breach in the period for this ticket).</param>
/// <param name="CompletedAtUtc">CompletedFollowUps only: when the follow-up was completed.</param>
public sealed record TeamPerformanceRecordDto(
    long TicketId,
    string TicketNumber,
    string RequestSummary,
    string TicketStatus,
    byte? PriorityId,
    string? DepartmentName,
    DateTime CreatedAtUtc,
    DateTime? BreachedAtUtc = null,
    DateTime? CompletedAtUtc = null);

/// <summary>How a records request landed — the controller maps each to a status code; no exception carries a business outcome.</summary>
public enum TeamPerformanceRecordsOutcome
{
    Success,

    /// <summary>The metric name is not one of the four counts.</summary>
    UnknownMetric,

    /// <summary>The employee is not an eligible (active, CS Agent) employee — the report never shows a row for them, so there are no records to open.</summary>
    EmployeeNotEligible
}

/// <summary>The records call's result: the outcome and, on success, the records.</summary>
public sealed record TeamPerformanceRecordsResult(TeamPerformanceRecordsOutcome Outcome, TeamPerformanceRecordsDto? Records = null);
