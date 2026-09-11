using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The operational Dashboard read (Customer Workspace phase): KPI counts
/// and the Tickets Requiring Attention list, in one scoped repository
/// aggregate. Department visibility is resolved through the exact same
/// primitive as the ticket queue
/// (<see cref="TicketQueryAppService.ResolveVisibleDepartmentIdsAsync"/>) —
/// a department user's dashboard covers their own departments only, never
/// widened, and the caller never supplies the scope.
/// </summary>
/// <remarks>
/// Dashboard Phase 1 adds <see cref="GetOverviewAsync"/> — the Operational
/// Dashboard (filters, six KPIs, six breakdowns, Recent/Critical list) over
/// <see cref="IDashboardQueryRepository"/>. It applies the same scope
/// primitive, then intersects any requested department filter with it, so
/// a filter parameter can narrow the caller's view but never widen it.
/// </remarks>
public sealed class DashboardAppService(
    ITicketRepository ticketRepository,
    TicketQueryAppService ticketQueryAppService,
    TimeProvider timeProvider,
    IDashboardQueryRepository dashboardQueryRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository)
{
    /// <summary>The default volume period when no date range is requested: the last 30 UTC calendar days, today included.</summary>
    public const int DefaultPeriodDays = 30;

    /// <summary>The Recent/Critical list stays a short, scannable queue — it links to the full ticket queue for everything else.</summary>
    public const int RecentLimit = 10;

    /// <summary>How far ahead of a pending SLA deadline counts as "at risk" — a presentation threshold, not an SLA rule; the SLA clocks themselves are untouched by it.</summary>
    public static readonly TimeSpan AtRiskWindow = TimeSpan.FromHours(4);

    /// <summary>The attention list stays a short, scannable queue — it links to the full ticket queue for everything else.</summary>
    public const int AttentionLimit = 10;

    public async Task<DashboardSummaryDto> GetSummaryAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        CancellationToken cancellationToken = default)
    {
        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(
            callerEmployeeId, callerRoles, cancellationToken);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var snapshot = await ticketRepository.GetDashboardSnapshotAsync(
            new DashboardSnapshotQuery(
                visibleDepartmentIds,
                callerEmployeeId,
                nowUtc,
                // "Today" is the UTC calendar day — a deliberate, documented
                // simplification until a business-timezone day boundary is an
                // approved requirement.
                nowUtc.Date,
                AtRiskWindow,
                AttentionLimit),
            cancellationToken);

        return new DashboardSummaryDto(
            snapshot.OpenTickets,
            snapshot.Unassigned,
            snapshot.SlaAtRisk,
            snapshot.SlaBreached,
            snapshot.CriticalOrHigh,
            snapshot.PendingCustomer,
            snapshot.ResolvedToday,
            snapshot.Reopened,
            snapshot.MyTickets,
            snapshot.AttentionTickets.Select(ToAttentionDto).ToList());
    }

    public async Task<DashboardOverviewDto> GetOverviewAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        DashboardOverviewRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(
            callerEmployeeId, callerRoles, cancellationToken);

        // The approver scope needs the caller's actual memberships even for a
        // cross-department view role: a department-targeted approval is
        // decided by membership, not by view rights.
        var memberships = await userDepartmentAssignmentRepository.GetByEmployeeIdAsync(callerEmployeeId, cancellationToken);
        var approverScope = ApprovalApproverScope.Resolve(
            callerEmployeeId, callerRoles, memberships.Select(m => m.DepartmentId),
            TicketApprovalAppService.DepartmentTargetDefaultApproverRoles);

        // A requested department can only narrow the visible scope. Outside
        // it, the scope collapses to nothing — the filter never widens what
        // the caller may see, and it never errors in a way that would reveal
        // whether the department exists.
        var scope = ResolveScope(visibleDepartmentIds, request.DepartmentId);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(nowUtc);
        var (dateFrom, dateTo) = ResolvePeriod(request.DateFrom, request.DateTo, today);
        var ticketStatus = ParseStatus(request.TicketStatus);

        var query = new DashboardOverviewQuery(
            scope,
            callerEmployeeId,
            approverScope,
            request.OwnerEmployeeId,
            request.ChannelId,
            request.RequestTypeId,
            ticketStatus,
            request.PriorityId,
            dateFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            dateTo.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            nowUtc,
            // "Today" is the UTC calendar day — the existing dashboard
            // convention; no separate timezone strategy is introduced here.
            nowUtc.Date,
            AtRiskWindow,
            RecentLimit);

        var snapshot = await dashboardQueryRepository.GetOverviewAsync(query, cancellationToken);

        // Agent and request-type pickers follow the department picker: with
        // a department selected they offer only that department's people
        // and request types; otherwise the whole visible scope's.
        var options = await dashboardQueryRepository.GetFilterOptionsAsync(
            visibleDepartmentIds,
            agentDepartmentIds: scope,
            requestTypeDepartmentIds: scope,
            cancellationToken);

        return new DashboardOverviewDto(
            new DashboardAppliedFiltersDto(
                dateFrom, dateTo, request.DepartmentId, request.OwnerEmployeeId, request.ChannelId,
                request.RequestTypeId, ticketStatus?.ToString(), request.PriorityId),
            new DashboardKpisDto(
                snapshot.OpenTickets,
                snapshot.MyTickets,
                snapshot.InDepartmentQueue,
                snapshot.SlaBreached,
                snapshot.DueToday,
                snapshot.PendingApproval),
            snapshot.VolumeTotal,
            ToBreakdown(snapshot.VolumeByChannel, snapshot.VolumeTotal, k => k.ToString()),
            ToBreakdown(snapshot.VolumeByRequestType, snapshot.VolumeTotal, k => k.ToString()),
            ToBreakdown(snapshot.VolumeByDepartment, snapshot.VolumeTotal, k => k.ToString()),
            ToBreakdown(snapshot.StatusBreakdown, snapshot.VolumeTotal, k => k.ToString()),
            ToBreakdown(snapshot.PriorityBreakdown, snapshot.VolumeTotal, k => k.ToString()),
            ToBreakdown(snapshot.BacklogAgeing, snapshot.OpenTickets, k => k.ToString()),
            snapshot.RecentTickets.Select(ToRecentDto).ToList(),
            new DashboardFilterOptionsDto(
                options.Departments.Select(o => new DashboardFilterOptionDto(o.Id.ToString(), o.Label, o.GroupId, o.IsActive)).ToList(),
                options.Agents.Select(o => new DashboardFilterOptionDto(o.Id.ToString(), o.Label, o.GroupId, o.IsActive)).ToList(),
                options.Channels.Select(o => new DashboardFilterOptionDto(o.Id.ToString(), o.Label, o.GroupId, o.IsActive)).ToList(),
                options.RequestTypes.Select(o => new DashboardFilterOptionDto(o.Id.ToString(), o.Label, o.GroupId, o.IsActive)).ToList(),
                // The lifecycle's own statuses, exactly as the domain defines
                // them — nothing invented, nothing omitted.
                Enum.GetValues<TicketStatus>().Select(s => new DashboardFilterOptionDto(s.ToString(), s.ToString())).ToList(),
                options.Priorities.Select(o => new DashboardFilterOptionDto(o.Id.ToString(), o.Label, o.GroupId, o.IsActive)).ToList()));
    }

    /// <summary>Null visible scope (cross-department) narrows to the requested department alone; a restricted scope narrows to the requested department only if it is already visible, otherwise to nothing.</summary>
    public static IReadOnlyCollection<int>? ResolveScope(IReadOnlyCollection<int>? visibleDepartmentIds, int? requestedDepartmentId)
    {
        if (requestedDepartmentId is not { } departmentId)
        {
            return visibleDepartmentIds;
        }

        if (visibleDepartmentIds is null || visibleDepartmentIds.Contains(departmentId))
        {
            return [departmentId];
        }

        return [];
    }

    /// <summary>Defaults an open-ended range to the last <see cref="DefaultPeriodDays"/> days ending today, and keeps a reversed range usable by swapping it.</summary>
    public static (DateOnly From, DateOnly To) ResolvePeriod(DateOnly? from, DateOnly? to, DateOnly today)
    {
        var resolvedTo = to ?? today;
        var resolvedFrom = from ?? resolvedTo.AddDays(-(DefaultPeriodDays - 1));
        return resolvedFrom <= resolvedTo ? (resolvedFrom, resolvedTo) : (resolvedTo, resolvedFrom);
    }

    private static TicketStatus? ParseStatus(string? value) =>
        value is not null && Enum.TryParse<TicketStatus>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;

    /// <summary>Stable percentage: count / total × 100, one decimal, never NaN — a zero total yields 0 for every row.</summary>
    public static double Percentage(int count, int total) =>
        total <= 0 ? 0 : Math.Round(count * 100.0 / total, 1, MidpointRounding.AwayFromZero);

    private static IReadOnlyList<DashboardBreakdownItemDto> ToBreakdown<TKey>(
        IReadOnlyList<DashboardCountRow<TKey>> rows, int total, Func<TKey, string> keyText) where TKey : struct =>
        rows.Select(r => new DashboardBreakdownItemDto(
            r.Key is { } key ? keyText(key) : null, r.Label, r.Count, Percentage(r.Count, total))).ToList();

    private static DashboardRecentTicketDto ToRecentDto(DashboardRecentTicketRow row) => new(
        row.TicketId,
        row.TicketNumber,
        row.CustomerName,
        row.UnitNumber,
        row.ProjectName,
        row.CurrentDepartmentId,
        row.DepartmentName,
        row.RequestTypeId,
        row.RequestTypeName,
        row.PriorityId,
        row.TicketStatus.ToString(),
        row.SlaState.ToString(),
        row.SlaDueAtUtc,
        row.CurrentOwnerEmployeeId,
        row.OwnerName,
        row.CreatedAtUtc);

    private static DashboardAttentionTicketDto ToAttentionDto(DashboardAttentionTicket row) => new(
        row.Ticket.TicketId,
        row.Ticket.TicketNumber,
        row.Ticket.CrmBuyerCustomerName,
        row.Ticket.CrmBuyerUnitNumber ?? row.Ticket.ManualUnitNumber,
        row.Ticket.PriorityId,
        row.Ticket.TicketStatus.ToString(),
        row.Ticket.SlaState.ToString(),
        row.SlaDueAtUtc,
        row.Ticket.CurrentOwnerEmployeeId,
        row.Ticket.CurrentDepartmentId,
        row.Ticket.RequestSummary,
        row.Ticket.CreatedAtUtc);
}
