using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Reporting.Abstractions;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Reporting.Repositories;

/// <summary>
/// The Team Performance report's read model. Every count is a SQL-side
/// aggregate read with <c>AsNoTracking</c>; the records behind a count are
/// produced by the very same predicate the count used, so a number and
/// its list can never disagree.
///
/// <para>
/// Eligibility is read from the Identity tables (<c>AspNetRoles</c> /
/// <c>AspNetUserRoles</c>) joined to <c>Employees</c>; the activity metrics
/// are read from the append-only history tables — assignments, status
/// history, notes, resolutions, workflow events and customer follow-ups —
/// never from the ticket's current owner, which only says who holds a
/// ticket now.
/// </para>
/// </summary>
public sealed class TeamPerformanceQueryRepository(TigerCsDbContext dbContext) : ITeamPerformanceQueryRepository
{
    // The three projection shapes below are plain classes built with object
    // initializers, not records with constructors: EF Core translates a
    // member-init projection into SQL and can apply set operations (UNION)
    // and further GROUP BY to it, whereas a constructor call is a client
    // projection it refuses to union over.

    /// <summary>A (employee, ticket) pair — the unit "tickets worked" is counted in, after de-duplication across the activity sources.</summary>
    private sealed class ActorTicket
    {
        public Guid EmployeeId { get; init; }
        public long TicketId { get; init; }
    }

    /// <summary>One SLA breach with the employee who held the ticket when it was recorded (null when the ticket was unassigned at that moment).</summary>
    private sealed class AttributedBreach
    {
        public long TicketId { get; init; }
        public DateTime OccurredAtUtc { get; init; }
        public Guid? HolderEmployeeId { get; init; }
    }

    /// <summary>A record's key before the ticket facts are joined on: the ticket and the metric-specific instant (breach or completion time), null for the two ticket-only metrics.</summary>
    private sealed class RecordKey
    {
        public long TicketId { get; init; }
        public DateTime? Instant { get; init; }
    }

    public async Task<IReadOnlyList<TeamPerformanceEmployeeRow>> GetEmployeesInRolesAsync(
        IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken = default)
    {
        var roleIds = dbContext.Roles.AsNoTracking()
            .Where(r => r.Name != null && roleNames.Contains(r.Name))
            .Select(r => r.Id);

        var employees = await dbContext.Employees.AsNoTracking()
            .Where(e => e.DeactivatedAtUtc == null
                && dbContext.UserRoles.Any(ur => ur.UserId == e.EmployeeId && roleIds.Contains(ur.RoleId)))
            .Select(e => new { e.EmployeeId, e.DisplayName })
            .ToListAsync(cancellationToken);

        if (employees.Count == 0)
        {
            return [];
        }

        var employeeIds = employees.Select(e => e.EmployeeId).ToList();
        var memberships = await dbContext.UserDepartmentAssignments.AsNoTracking()
            .Where(a => employeeIds.Contains(a.EmployeeId))
            .Join(dbContext.Departments.AsNoTracking(), a => a.DepartmentId, d => d.DepartmentId,
                (a, d) => new { a.EmployeeId, d.DepartmentId, d.Name, d.Code, a.IsPrimary })
            .ToListAsync(cancellationToken);

        var membershipsByEmployee = memberships
            .GroupBy(m => m.EmployeeId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TeamPerformanceDepartmentRow>)g
                    .OrderByDescending(m => m.IsPrimary)
                    .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new TeamPerformanceDepartmentRow(m.DepartmentId, m.Name, m.Code, m.IsPrimary))
                    .ToList());

        return employees
            .Select(e => new TeamPerformanceEmployeeRow(
                e.EmployeeId, e.DisplayName, membershipsByEmployee.GetValueOrDefault(e.EmployeeId, [])))
            .ToList();
    }

    public async Task<TeamPerformanceMetricsSnapshot> GetMetricsAsync(
        IReadOnlyCollection<Guid> employeeIds, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default)
    {
        var ids = employeeIds.ToList();

        var currentlyAssigned = await CurrentlyAssigned(ids)
            .GroupBy(t => t.CurrentOwnerEmployeeId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var ticketsWorked = await WorkedPairs(ids, rangeStartUtc, rangeEndUtc)
            .GroupBy(x => x.EmployeeId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var completedFollowUps = await CompletedFollowUps(ids, rangeStartUtc, rangeEndUtc)
            .GroupBy(h => h.AssignedEmployeeId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var slaBreaches = await AttributedBreaches(rangeStartUtc, rangeEndUtc)
            .Where(b => b.HolderEmployeeId != null && ids.Contains(b.HolderEmployeeId!.Value))
            .Select(b => new { EmployeeId = b.HolderEmployeeId!.Value, b.TicketId })
            .Distinct()
            .GroupBy(x => x.EmployeeId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return new TeamPerformanceMetricsSnapshot(currentlyAssigned, ticketsWorked, completedFollowUps, slaBreaches);
    }

    public async Task<IReadOnlyList<TeamPerformanceRecordRow>> GetRecordsAsync(
        Guid employeeId, TeamPerformanceMetric metric, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default)
    {
        List<Guid> one = [employeeId];

        return metric switch
        {
            TeamPerformanceMetric.CurrentlyAssigned => await WithTicketFacts(
                    CurrentlyAssigned(one).Select(t => new RecordKey { TicketId = t.TicketId, Instant = null }))
                .ToListAsync(cancellationToken),

            TeamPerformanceMetric.TicketsWorked => await WithTicketFacts(
                    WorkedPairs(one, rangeStartUtc, rangeEndUtc).Select(p => new RecordKey { TicketId = p.TicketId, Instant = null }))
                .ToListAsync(cancellationToken),

            // One row per completed follow-up — the count is of work items,
            // so a ticket with two completed follow-ups appears twice, each
            // with its own completion time.
            TeamPerformanceMetric.CompletedFollowUps => await WithTicketFacts(
                    CompletedFollowUps(one, rangeStartUtc, rangeEndUtc).Select(h => new RecordKey { TicketId = h.TicketId, Instant = h.CompletedAtUtc }),
                    completedAt: true)
                .ToListAsync(cancellationToken),

            // Distinct tickets, each with the earliest breach recorded in the
            // period while this employee held it.
            TeamPerformanceMetric.SlaBreaches => await WithTicketFacts(
                    AttributedBreaches(rangeStartUtc, rangeEndUtc)
                        .Where(b => b.HolderEmployeeId == employeeId)
                        .GroupBy(b => b.TicketId)
                        .Select(g => new RecordKey { TicketId = g.Key, Instant = g.Min(b => b.OccurredAtUtc) }),
                    breachedAt: true)
                .ToListAsync(cancellationToken),

            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unknown Team Performance metric.")
        };
    }

    // ---- The shared predicates: each count and its records use exactly one of these. ----

    /// <summary>Current state: tickets the employee holds now that are not Closed.</summary>
    private IQueryable<Ticket> CurrentlyAssigned(List<Guid> ids) =>
        dbContext.Tickets.AsNoTracking()
            .Where(t => t.CurrentOwnerEmployeeId != null
                && ids.Contains(t.CurrentOwnerEmployeeId!.Value)
                && t.TicketStatus != TicketStatus.Closed);

    /// <summary>
    /// Distinct (employee, ticket) pairs with at least one recorded action
    /// in the window, across every activity source: an assignment to the
    /// employee, a lifecycle-dimension change they made, a note they wrote,
    /// a resolution they recorded, a workflow event they raised, or a
    /// customer follow-up assigned to or completed by them in the window.
    /// </summary>
    private IQueryable<ActorTicket> WorkedPairs(List<Guid> ids, DateTime start, DateTime end)
    {
        var assignments = dbContext.TicketAssignments.AsNoTracking()
            .Where(a => ids.Contains(a.AssignedEmployeeId) && a.AssignedAtUtc >= start && a.AssignedAtUtc < end)
            .Select(a => new ActorTicket { EmployeeId = a.AssignedEmployeeId, TicketId = a.TicketId });

        var statusChanges = dbContext.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => h.ActorEmployeeId != null && ids.Contains(h.ActorEmployeeId!.Value) && h.OccurredAtUtc >= start && h.OccurredAtUtc < end)
            .Select(h => new ActorTicket { EmployeeId = h.ActorEmployeeId!.Value, TicketId = h.TicketId });

        var notes = dbContext.TicketNotes.AsNoTracking()
            .Where(n => ids.Contains(n.AuthorEmployeeId) && n.CreatedAtUtc >= start && n.CreatedAtUtc < end)
            .Select(n => new ActorTicket { EmployeeId = n.AuthorEmployeeId, TicketId = n.TicketId });

        var resolutions = dbContext.TicketResolutions.AsNoTracking()
            .Where(r => ids.Contains(r.ResolvingEmployeeId) && r.ResolvedAtUtc >= start && r.ResolvedAtUtc < end)
            .Select(r => new ActorTicket { EmployeeId = r.ResolvingEmployeeId, TicketId = r.TicketId });

        var workflowEvents = dbContext.TicketWorkflowEvents.AsNoTracking()
            .Where(e => e.ActorEmployeeId != null && ids.Contains(e.ActorEmployeeId!.Value) && e.OccurredAtUtc >= start && e.OccurredAtUtc < end)
            .Select(e => new ActorTicket { EmployeeId = e.ActorEmployeeId!.Value, TicketId = e.TicketId });

        var handoffs = dbContext.TicketAgentHandoffs.AsNoTracking()
            .Where(h => h.AssignedEmployeeId != null && ids.Contains(h.AssignedEmployeeId!.Value)
                && ((h.AssignedAtUtc != null && h.AssignedAtUtc >= start && h.AssignedAtUtc < end)
                    || (h.CompletedAtUtc != null && h.CompletedAtUtc >= start && h.CompletedAtUtc < end)))
            .Select(h => new ActorTicket { EmployeeId = h.AssignedEmployeeId!.Value, TicketId = h.TicketId });

        // Union (not Concat) is the de-duplication: one pair however many
        // actions the employee took on the ticket.
        return assignments
            .Union(statusChanges)
            .Union(notes)
            .Union(resolutions)
            .Union(workflowEvents)
            .Union(handoffs);
    }

    /// <summary>Customer follow-ups assigned to the employee and completed in the window.</summary>
    private IQueryable<TicketAgentHandoff> CompletedFollowUps(List<Guid> ids, DateTime start, DateTime end) =>
        dbContext.TicketAgentHandoffs.AsNoTracking()
            .Where(h => h.Status == AgentHandoffStatus.Completed
                && h.AssignedEmployeeId != null && ids.Contains(h.AssignedEmployeeId!.Value)
                && h.CompletedAtUtc != null && h.CompletedAtUtc >= start && h.CompletedAtUtc < end);

    /// <summary>
    /// Every SLA breach recorded in the window (a <c>TicketStatusHistory</c>
    /// row on the SlaState dimension whose new value is Breached), each
    /// attributed to the employee named by the latest assignment at or
    /// before the moment of the breach — the person who held the ticket
    /// when it breached, not whoever holds it now.
    /// </summary>
    private IQueryable<AttributedBreach> AttributedBreaches(DateTime start, DateTime end)
    {
        const byte breached = (byte)SlaState.Breached;
        return dbContext.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => h.Dimension == TicketStatusDimension.SlaState && h.NewValue == breached
                && h.OccurredAtUtc >= start && h.OccurredAtUtc < end)
            .Select(h => new AttributedBreach
            {
                TicketId = h.TicketId,
                OccurredAtUtc = h.OccurredAtUtc,
                HolderEmployeeId = dbContext.TicketAssignments
                    .Where(a => a.TicketId == h.TicketId && a.AssignedAtUtc <= h.OccurredAtUtc)
                    .OrderByDescending(a => a.AssignedAtUtc)
                    .ThenByDescending(a => a.TicketAssignmentId)
                    .Select(a => (Guid?)a.AssignedEmployeeId)
                    .FirstOrDefault()
            });
    }

    /// <summary>
    /// Joins record keys to the ticket's facts and its current department's
    /// name, in one query, newest first: by the metric's own instant when it
    /// has one (breach or completion time), otherwise by ticket creation.
    /// Ordering happens before the final projection so it is SQL, not a
    /// client sort.
    /// </summary>
    private IQueryable<TeamPerformanceRecordRow> WithTicketFacts(IQueryable<RecordKey> keys, bool breachedAt = false, bool completedAt = false)
    {
        var byInstant = breachedAt || completedAt;
        return keys
            .Join(dbContext.Tickets.AsNoTracking(), k => k.TicketId, t => t.TicketId, (k, t) => new { k.Instant, Ticket = t })
            .GroupJoin(dbContext.Departments.AsNoTracking(), x => x.Ticket.CurrentDepartmentId, d => d.DepartmentId, (x, departments) => new { x, departments })
            .SelectMany(y => y.departments.DefaultIfEmpty(), (y, d) => new { y.x.Instant, y.x.Ticket, DepartmentName = d != null ? d.Name : null })
            .OrderByDescending(x => byInstant ? x.Instant : x.Ticket.CreatedAtUtc)
            .ThenByDescending(x => x.Ticket.TicketId)
            .Select(x => new TeamPerformanceRecordRow(
                x.Ticket.TicketId,
                x.Ticket.TicketNumber,
                x.Ticket.RequestSummary,
                x.Ticket.TicketStatus,
                x.Ticket.PriorityId,
                x.DepartmentName,
                x.Ticket.CreatedAtUtc,
                breachedAt ? x.Instant : null,
                completedAt ? x.Instant : null));
    }
}
