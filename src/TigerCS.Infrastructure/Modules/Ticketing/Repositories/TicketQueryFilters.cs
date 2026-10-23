using System.Linq.Expressions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>
/// The ticket predicates the ticket queue (<see cref="TicketRepository.SearchAsync"/>)
/// and the Operational Dashboard (<see cref="DashboardQueryRepository"/>)
/// share, so a dashboard number and its drill-down list are computed by
/// the very same SQL. Every method composes onto an <see cref="IQueryable{T}"/>
/// — nothing here materializes rows.
/// </summary>
internal static class TicketQueryFilters
{
    /// <summary>The department-visibility scope: null = no restriction, otherwise the ticket's CURRENT department must be one of them.</summary>
    public static IQueryable<Ticket> InScope(this IQueryable<Ticket> tickets, IReadOnlyCollection<int>? departmentIds) =>
        departmentIds is null ? tickets : tickets.Where(t => departmentIds.Contains(t.CurrentDepartmentId));

    /// <summary>
    /// "Active" throughout: the non-terminal lifecycle statuses — Open,
    /// InProgress, PendingCustomer, and the legacy PendingThirdParty.
    /// Resolved and Closed are the terminal ones.
    ///
    /// <para>
    /// <b>The legacy status stays in this set deliberately.</b> It is retired
    /// as a target, not as a state: a ticket that was already PendingThirdParty
    /// is unfinished work, and dropping it from "active" would hide it from
    /// every queue and KPI — the one place someone would go to return it to
    /// InProgress.
    /// </para>
    /// </summary>
    public static IQueryable<Ticket> Active(this IQueryable<Ticket> tickets) =>
        tickets.Where(t =>
            t.TicketStatus == TicketStatus.Open
            || t.TicketStatus == TicketStatus.InProgress
            || t.TicketStatus == TicketStatus.PendingCustomer
            || t.TicketStatus == TicketStatus.PendingThirdParty);

    /// <summary>Tickets whose ORIGINATING interaction (the channel the ticket entered the system on; at most one per ticket) arrived on <paramref name="channelId"/>. Later interactions on other channels never change it.</summary>
    public static IQueryable<Ticket> WithOriginatingChannel(this IQueryable<Ticket> tickets, TigerCsDbContext dbContext, byte channelId) =>
        tickets.Where(t => dbContext.TicketInteractions.Any(i =>
            i.TicketId == t.TicketId && i.IsOriginatingInteraction && i.ChannelId == channelId));

    /// <summary>Tickets in one Open Backlog Ageing bucket at <paramref name="nowUtc"/> — the explicit half-open boundaries of <see cref="BacklogAgeBoundaries"/>, applied as CreatedAtUtc comparisons.</summary>
    public static IQueryable<Ticket> InBacklogAgeBucket(this IQueryable<Ticket> tickets, BacklogAgeBucket bucket, DateTime nowUtc)
    {
        var (createdAfter, createdAtOrBefore) = BacklogAgeBoundaries.CreatedAtWindow(bucket, nowUtc);
        if (createdAfter is { } after)
        {
            tickets = tickets.Where(t => t.CreatedAtUtc > after);
        }

        if (createdAtOrBefore is { } atOrBefore)
        {
            tickets = tickets.Where(t => t.CreatedAtUtc <= atOrBefore);
        }

        return tickets;
    }

    /// <summary>
    /// Closed tickets in one Dashboard closure KPI. Written as correlated
    /// <c>EXISTS</c> predicates, so each ticket is counted once however many
    /// reviews, invitations or audit rows it has — never a join that could
    /// multiply rows.
    /// </summary>
    public static IQueryable<Ticket> InClosureCategory(
        this IQueryable<Ticket> tickets, TigerCsDbContext dbContext, TicketClosureCategory category)
    {
        var closed = tickets.Where(t => t.TicketStatus == TicketStatus.Closed);
        return category switch
        {
            // A submitted, non-test review for the ticket's current cycle (a
            // reopen starts a new cycle that needs its own review).
            TicketClosureCategory.Reviewed => closed.Where(t => dbContext.TicketReviews.Any(r =>
                r.TicketId == t.TicketId && r.ReopenCycle == t.ReopenCount && !r.IsTest)),
            TicketClosureCategory.NotReviewed => closed.Where(t => !dbContext.TicketReviews.Any(r =>
                r.TicketId == t.TicketId && r.ReopenCycle == t.ReopenCount && !r.IsTest)),
            TicketClosureCategory.ClosedByAi => closed.Where(LatestClosureIs(dbContext, TicketClosureAuditActions.ClosedByAi)),
            TicketClosureCategory.ClosedByAgent => closed.Where(LatestClosureIs(dbContext, TicketClosureAuditActions.ClosedByAgent)),
            _ => closed.Where(_ => false)
        };
    }

    /// <summary>
    /// The ticket's most recent closing audit entry (highest id — the audit
    /// trail is append-only) has this action. A reopened ticket closed again
    /// is judged by its last closure; an inactivity or document-delivery
    /// closure is the latest closure of its own, matching neither AI nor agent.
    /// </summary>
    private static Expression<Func<Ticket, bool>> LatestClosureIs(TigerCsDbContext dbContext, string action)
    {
        var closingActions = TicketClosureAuditActions.AllClosures.ToList();
        return t => dbContext.AuditEntries.Any(a =>
            a.EntityType == TicketClosureAuditActions.EntityType
            && a.EntityId == t.TicketId.ToString()
            && a.Action == action
            && !dbContext.AuditEntries.Any(later =>
                later.EntityType == TicketClosureAuditActions.EntityType
                && later.EntityId == a.EntityId
                && later.AuditEntryId > a.AuditEntryId
                && closingActions.Contains(later.Action)));
    }

    /// <summary>The current SLA period of every ticket — the one row per ticket with no period end.</summary>
    public static IQueryable<Domain.Modules.SlaAndEscalation.TicketSlaInstance> CurrentSlaPeriods(TigerCsDbContext dbContext) =>
        dbContext.TicketSlaInstances.Where(s => s.PeriodEndAtUtc == null);

    /// <summary>
    /// Tickets whose current SLA period's resolution deadline falls within
    /// the UTC calendar day starting at <paramref name="dayStartUtc"/> and
    /// has not already breached. The resolution clock is the ticket's
    /// applicable due date (the same deadline the attention/recent lists
    /// show); a breached clock is "SLA Breached", not "due today".
    /// </summary>
    public static IQueryable<Ticket> DueOnUtcDay(this IQueryable<Ticket> tickets, TigerCsDbContext dbContext, DateTime dayStartUtc)
    {
        var dayEndUtc = dayStartUtc.AddDays(1);
        return tickets.Where(t => CurrentSlaPeriods(dbContext).Any(s =>
            s.TicketId == t.TicketId
            && !s.ResolutionBreached
            && s.ResolutionDueAtUtc >= dayStartUtc
            && s.ResolutionDueAtUtc < dayEndUtc));
    }

    /// <summary>Tickets carrying at least one Pending approval that <paramref name="scope"/>'s caller is authorized to action.</summary>
    public static IQueryable<Ticket> WithPendingApprovalActionableBy(
        this IQueryable<Ticket> tickets, TigerCsDbContext dbContext, ApprovalApproverScope scope)
    {
        var actionable = dbContext.TicketApprovals
            .Where(a => a.Status == ApprovalStatus.Pending)
            .Where(ActionableBy(scope))
            .Select(a => a.TicketId);

        return tickets.Where(t => actionable.Contains(t.TicketId));
    }

    /// <summary>
    /// The set-based form of <c>TicketApprovalAppService.IsAuthorizedApproverAsync</c>
    /// (see <see cref="ApprovalApproverScope"/> for the rule, target kind by
    /// target kind). Written as one expression so it translates to a single
    /// WHERE clause; the override short-circuits to "every Pending approval"
    /// exactly as <c>AuthorizationGate</c> would answer per record.
    /// </summary>
    public static Expression<Func<TicketApproval, bool>> ActionableBy(ApprovalApproverScope scope)
    {
        if (scope.Override)
        {
            return _ => true;
        }

        var callerId = scope.CallerEmployeeId;
        var roles = scope.CallerRoles.ToList();
        var departmentIds = scope.MemberDepartmentIds.ToList();
        var holdsDefaultRole = scope.HoldsDepartmentTargetDefaultApproverRole;

        return a =>
            (a.TargetKind == Domain.Modules.WorkflowConfiguration.ApprovalTargetKind.Employee
                && a.TargetEmployeeId == callerId)
            || (a.TargetKind == Domain.Modules.WorkflowConfiguration.ApprovalTargetKind.Role
                && a.TargetRoleName != null && roles.Contains(a.TargetRoleName))
            || (a.TargetKind == Domain.Modules.WorkflowConfiguration.ApprovalTargetKind.Department
                && a.TargetDepartmentId != null && departmentIds.Contains(a.TargetDepartmentId.Value)
                && ((a.TargetRoleName == null && holdsDefaultRole)
                    || (a.TargetRoleName != null && roles.Contains(a.TargetRoleName))));
    }
}
