using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketRepository(TigerCsDbContext dbContext) : ITicketRepository
{
    public Task<Ticket?> GetByIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.Tickets.FirstOrDefaultAsync(t => t.TicketId == ticketId, cancellationToken);

    public async Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default) =>
        await dbContext.Tickets.AddAsync(ticket, cancellationToken);

    public Task<int> CountByTicketNumberPrefixAsync(string ticketNumberPrefix, CancellationToken cancellationToken = default) =>
        dbContext.Tickets.CountAsync(t => t.TicketNumber.StartsWith(ticketNumberPrefix), cancellationToken);

    public async Task<TicketQueryResult> SearchAsync(TicketQuery query, CancellationToken cancellationToken = default)
    {
        var filtered = dbContext.Tickets.AsQueryable();

        if (query.VisibleDepartmentIds is not null)
        {
            filtered = filtered.Where(t => query.VisibleDepartmentIds.Contains(t.CurrentDepartmentId));
        }

        if (query.DepartmentId is { } departmentId)
        {
            filtered = filtered.Where(t => t.CurrentDepartmentId == departmentId);
        }

        if (query.CategoryId is { } categoryId)
        {
            filtered = filtered.Where(t => t.CategoryId == categoryId);
        }

        if (query.PriorityId is { } priorityId)
        {
            filtered = filtered.Where(t => t.PriorityId == priorityId);
        }

        if (query.TicketStatus is { } ticketStatus)
        {
            filtered = filtered.Where(t => t.TicketStatus == ticketStatus);
        }

        if (query.VerificationStatus is { } verificationStatus)
        {
            filtered = filtered.Where(t => t.VerificationStatus == verificationStatus);
        }

        if (query.OwnerEmployeeId is { } ownerEmployeeId)
        {
            filtered = filtered.Where(t => t.CurrentOwnerEmployeeId == ownerEmployeeId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            filtered = filtered.Where(t =>
                t.TicketNumber.Contains(query.Search) || t.RequestSummary.Contains(query.Search));
        }

        var totalCount = await filtered.CountAsync(cancellationToken);

        filtered = query.SortBy switch
        {
            TicketSortBy.Priority => query.SortDescending
                ? filtered.OrderByDescending(t => t.PriorityId).ThenByDescending(t => t.CreatedAtUtc)
                : filtered.OrderBy(t => t.PriorityId).ThenByDescending(t => t.CreatedAtUtc),
            _ => query.SortDescending
                ? filtered.OrderByDescending(t => t.CreatedAtUtc)
                : filtered.OrderBy(t => t.CreatedAtUtc)
        };

        var items = await filtered
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new TicketQueryResult(items, totalCount);
    }

    public void SetRowVersion(Ticket ticket, byte[] rowVersion) =>
        dbContext.Entry(ticket).Property(t => t.RowVersion).OriginalValue = rowVersion;

    public async Task<CustomerHistoryQueryResult> SearchCustomerHistoryAsync(
        CustomerHistoryQuery query, CancellationToken cancellationToken = default)
    {
        IQueryable<Ticket> filtered;
        if (query.CrmBuyerCustomerId is { } crmBuyerCustomerId)
        {
            filtered = dbContext.Tickets.Where(t => t.CrmBuyerCustomerId == crmBuyerCustomerId);
        }
        else if (query is { ExternalSource: { } externalSource, ExternalCustomerId: { } externalCustomerId })
        {
            // The persisted external verification identity pair — exact
            // match on both fields, never widened by display name or phone.
            filtered = dbContext.Tickets.Where(t =>
                t.CustomerVerificationSource == externalSource && t.ExternalCustomerId == externalCustomerId);
        }
        else if (query.TicketIds is { Count: > 0 } ticketIds)
        {
            filtered = dbContext.Tickets.Where(t => ticketIds.Contains(t.TicketId));
        }
        else
        {
            return new CustomerHistoryQueryResult([], 0, 0, 0);
        }

        if (query.VisibleDepartmentIds is not null)
        {
            filtered = filtered.Where(t => query.VisibleDepartmentIds.Contains(t.CurrentDepartmentId));
        }

        if (query.ExcludeTicketId is { } excludeTicketId)
        {
            filtered = filtered.Where(t => t.TicketId != excludeTicketId);
        }

        if (query.UnitNumber is { } unitNumber)
        {
            // Exact match on the ticket's own unit-number snapshot (CRM Buyer
            // or manual) — the deterministic same-unit rule of Phase E's
            // duplicate awareness; no fuzzy matching.
            filtered = filtered.Where(t => t.CrmBuyerUnitNumber == unitNumber || t.ManualUnitNumber == unitNumber);
        }

        var statusCounts = await filtered
            .GroupBy(t => t.TicketStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var closedCount = statusCounts
            .Where(s => s.Status == TicketStatus.Resolved || s.Status == TicketStatus.Closed)
            .Sum(s => s.Count);
        var totalCount = statusCounts.Sum(s => s.Count);

        var ordered = query.OrderActiveFirst
            ? filtered
                .OrderBy(t => t.TicketStatus == TicketStatus.Resolved || t.TicketStatus == TicketStatus.Closed ? 1 : 0)
                .ThenByDescending(t => t.CreatedAtUtc)
            : filtered.OrderByDescending(t => t.CreatedAtUtc);

        var items = await ordered
            .Take(query.Limit)
            .ToListAsync(cancellationToken);

        return new CustomerHistoryQueryResult(items, totalCount, totalCount - closedCount, closedCount);
    }

    public async Task<DashboardSnapshot> GetDashboardSnapshotAsync(
        DashboardSnapshotQuery query, CancellationToken cancellationToken = default)
    {
        var visible = dbContext.Tickets.AsQueryable();
        if (query.VisibleDepartmentIds is not null)
        {
            visible = visible.Where(t => query.VisibleDepartmentIds.Contains(t.CurrentDepartmentId));
        }

        var active = visible.Where(t =>
            t.TicketStatus == TicketStatus.Open
            || t.TicketStatus == TicketStatus.InProgress
            || t.TicketStatus == TicketStatus.PendingCustomer
            || t.TicketStatus == TicketStatus.PendingThirdParty);

        var atRiskUntil = query.NowUtc + query.AtRiskWindow;
        var currentSla = dbContext.TicketSlaInstances.Where(s => s.PeriodEndAtUtc == null);

        var openTickets = await active.CountAsync(cancellationToken);
        var unassigned = await active.CountAsync(t => t.CurrentOwnerEmployeeId == null, cancellationToken);
        var slaBreached = await active.CountAsync(t => t.SlaState == SlaState.Breached, cancellationToken);
        var criticalOrHigh = await active.CountAsync(t => t.PriorityId <= 2, cancellationToken);
        var pendingCustomer = await visible.CountAsync(t => t.TicketStatus == TicketStatus.PendingCustomer, cancellationToken);
        var reopened = await active.CountAsync(t => t.ReopenCount > 0, cancellationToken);
        var myTickets = await active.CountAsync(t => t.CurrentOwnerEmployeeId == query.CallerEmployeeId, cancellationToken);

        // At risk: a pending, unbreached deadline on the current SLA period
        // falls due within the window — either clock; a First Response
        // deadline only counts while no first human response is recorded.
        var slaAtRisk = await active
            .Join(currentSla, t => t.TicketId, s => s.TicketId, (t, s) => new { t, s })
            .Where(x =>
                (!x.s.ResolutionBreached && x.s.ResolutionDueAtUtc > query.NowUtc && x.s.ResolutionDueAtUtc <= atRiskUntil)
                || (!x.s.FirstResponseBreached && x.t.FirstHumanResponseAtUtc == null
                    && x.s.FirstResponseDueAtUtc > query.NowUtc && x.s.FirstResponseDueAtUtc <= atRiskUntil))
            .Select(x => x.t.TicketId)
            .Distinct()
            .CountAsync(cancellationToken);

        var resolvedToday = await visible
            .Join(
                dbContext.TicketResolutions.Where(r => r.IsCurrent && r.ResolvedAtUtc >= query.ResolvedTodayStartUtc),
                t => t.TicketId, r => r.TicketId, (t, r) => t.TicketId)
            .Distinct()
            .CountAsync(cancellationToken);

        // Tickets Requiring Attention: breached first, then due-soon, then
        // critical, high, unassigned — a bounded, deterministic ranking over
        // active tickets left-joined to their current SLA period.
        var attentionRows = await active
            .GroupJoin(currentSla, t => t.TicketId, s => s.TicketId, (t, slas) => new { t, slas })
            .SelectMany(x => x.slas.DefaultIfEmpty(), (x, s) => new
            {
                Ticket = x.t,
                SlaDueAtUtc = s != null && !s.ResolutionBreached ? s.ResolutionDueAtUtc : (DateTime?)null
            })
            .Where(x =>
                x.Ticket.SlaState == SlaState.Breached
                || x.Ticket.PriorityId <= 2
                || x.Ticket.CurrentOwnerEmployeeId == null
                || (x.SlaDueAtUtc != null && x.SlaDueAtUtc <= atRiskUntil))
            .OrderBy(x =>
                x.Ticket.SlaState == SlaState.Breached ? 0
                : x.SlaDueAtUtc != null && x.SlaDueAtUtc <= atRiskUntil ? 1
                : x.Ticket.PriorityId == 1 ? 2
                : x.Ticket.PriorityId == 2 ? 3
                : 4)
            .ThenBy(x => x.SlaDueAtUtc ?? DateTime.MaxValue)
            .ThenBy(x => x.Ticket.PriorityId)
            .ThenBy(x => x.Ticket.CreatedAtUtc)
            .Take(query.AttentionLimit)
            .ToListAsync(cancellationToken);

        return new DashboardSnapshot(
            openTickets,
            unassigned,
            slaAtRisk,
            slaBreached,
            criticalOrHigh,
            pendingCustomer,
            resolvedToday,
            reopened,
            myTickets,
            attentionRows.Select(x => new DashboardAttentionTicket(x.Ticket, x.SlaDueAtUtc)).ToList());
    }
}
