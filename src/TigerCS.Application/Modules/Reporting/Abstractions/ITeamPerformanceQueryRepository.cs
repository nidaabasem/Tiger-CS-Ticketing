using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Reporting.Abstractions;

/// <summary>
/// The Team Performance report's reads. Every count is a set-based
/// aggregate computed in the database over the employee set the
/// application service hands in; the repository never decides who is
/// eligible — it answers "which employees hold these roles" and "what did
/// these employees do in this window".
/// </summary>
public interface ITeamPerformanceQueryRepository
{
    /// <summary>
    /// Every ACTIVE employee (<c>Employees.DeactivatedAtUtc IS NULL</c>)
    /// holding at least one of <paramref name="roleNames"/> in Identity
    /// (<c>AspNetUserRoles</c>), with their department memberships. One
    /// entry per employee however many roles or departments they hold.
    /// </summary>
    Task<IReadOnlyList<TeamPerformanceEmployeeRow>> GetEmployeesInRolesAsync(
        IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken = default);

    /// <summary>The four metrics for the given employees over [<paramref name="rangeStartUtc"/>, <paramref name="rangeEndUtc"/>). An employee absent from a dictionary has a count of zero.</summary>
    Task<TeamPerformanceMetricsSnapshot> GetMetricsAsync(
        IReadOnlyCollection<Guid> employeeIds, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default);

    /// <summary>The records behind one employee's count, computed with exactly the predicate the count used.</summary>
    Task<IReadOnlyList<TeamPerformanceRecordRow>> GetRecordsAsync(
        Guid employeeId, TeamPerformanceMetric metric, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default);
}

/// <summary>One eligible employee with their department memberships (primary first).</summary>
public sealed record TeamPerformanceEmployeeRow(
    Guid EmployeeId,
    string DisplayName,
    IReadOnlyList<TeamPerformanceDepartmentRow> Departments);

/// <summary>One department membership: id, display name, the department code (the Call Center department is recognised by its code) and whether it is the primary membership.</summary>
public sealed record TeamPerformanceDepartmentRow(int DepartmentId, string Name, string Code, bool IsPrimary);

/// <summary>The four metric counts keyed by employee. Missing key = zero.</summary>
public sealed record TeamPerformanceMetricsSnapshot(
    IReadOnlyDictionary<Guid, int> CurrentlyAssigned,
    IReadOnlyDictionary<Guid, int> TicketsWorked,
    IReadOnlyDictionary<Guid, int> CompletedFollowUps,
    IReadOnlyDictionary<Guid, int> SlaBreaches);

/// <summary>One record row with the ticket's facts resolved in-query, plus the metric-specific instant (breach or completion time).</summary>
public sealed record TeamPerformanceRecordRow(
    long TicketId,
    string TicketNumber,
    string RequestSummary,
    TicketStatus TicketStatus,
    byte? PriorityId,
    string? DepartmentName,
    DateTime CreatedAtUtc,
    DateTime? BreachedAtUtc,
    DateTime? CompletedAtUtc);
