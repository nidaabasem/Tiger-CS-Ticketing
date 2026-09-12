using System.Globalization;
using System.Web;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Models;

/// <summary>
/// Builds the Operational Dashboard's drill-down links (Dashboard Phase 1).
/// Every KPI and every bar row is a shortcut into the EXISTING ticket queue
/// (<c>/Tickets</c>) with the matching query-string filters — never a
/// one-off page. The dashboard's own dimension filters (department, agent,
/// channel, request type, status, priority) travel with each link so the
/// list shows exactly what the number counted; the date range travels only
/// with the volume breakdowns, because KPIs and backlog are current-state.
/// </summary>
public static class DashboardLinks
{
    public const string TicketsPath = "/Tickets";

    /// <summary>Open Tickets: every active ticket in the filter context.</summary>
    public static string OpenTickets(DashboardAppliedFiltersDto f) => Build(f, includeDates: false, ("activeOnly", "true"));

    /// <summary>My Tickets: active tickets the signed-in user owns — the agent filter is replaced by the viewer themself.</summary>
    public static string MyTickets(DashboardAppliedFiltersDto f, Guid viewerEmployeeId) =>
        Build(f with { OwnerEmployeeId = viewerEmployeeId }, includeDates: false, ("activeOnly", "true"));

    public static string InDepartmentQueue(DashboardAppliedFiltersDto f) => Build(f, includeDates: false, ("inDepartmentQueue", "true"));

    public static string SlaBreached(DashboardAppliedFiltersDto f) => Build(f, includeDates: false, ("slaBreached", "true"));

    public static string DueToday(DashboardAppliedFiltersDto f) => Build(f, includeDates: false, ("dueToday", "true"));

    public static string PendingApproval(DashboardAppliedFiltersDto f) => Build(f, includeDates: false, ("pendingApproval", "true"));

    public static string Channel(DashboardAppliedFiltersDto f, string channelId) =>
        Build(f with { ChannelId = byte.Parse(channelId, CultureInfo.InvariantCulture) }, includeDates: true);

    public static string RequestType(DashboardAppliedFiltersDto f, string requestTypeId) =>
        Build(f with { RequestTypeId = int.Parse(requestTypeId, CultureInfo.InvariantCulture) }, includeDates: true);

    public static string Department(DashboardAppliedFiltersDto f, string departmentId) =>
        Build(f with { DepartmentId = int.Parse(departmentId, CultureInfo.InvariantCulture) }, includeDates: true);

    public static string Status(DashboardAppliedFiltersDto f, string ticketStatus) =>
        Build(f with { TicketStatus = ticketStatus }, includeDates: true);

    public static string Priority(DashboardAppliedFiltersDto f, string priorityId) =>
        Build(f with { PriorityId = byte.Parse(priorityId, CultureInfo.InvariantCulture) }, includeDates: true);

    /// <summary>A backlog age bucket: active tickets of that age — current-state, so no date range.</summary>
    public static string BacklogAge(DashboardAppliedFiltersDto f, string bucket) => Build(f, includeDates: false, ("backlogAge", bucket));

    /// <summary>The "View all" link for the Recent/Critical list: the active backlog in the filter context.</summary>
    public static string ViewAll(DashboardAppliedFiltersDto f) => OpenTickets(f);

    /// <summary>Every ticket created in the range, in the filter context — the volume breakdowns' own total.</summary>
    public static string VolumeTotal(DashboardAppliedFiltersDto f) => Build(f, includeDates: true);

    private static string Build(DashboardAppliedFiltersDto f, bool includeDates, params (string Name, string Value)[] extra)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (f.DepartmentId is int departmentId) query["departmentId"] = departmentId.ToString(CultureInfo.InvariantCulture);
        if (f.OwnerEmployeeId is Guid ownerId) query["ownerEmployeeId"] = ownerId.ToString();
        if (f.ChannelId is byte channelId) query["channelId"] = channelId.ToString(CultureInfo.InvariantCulture);
        if (f.RequestTypeId is int requestTypeId) query["requestTypeId"] = requestTypeId.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(f.TicketStatus)) query["ticketStatus"] = f.TicketStatus;
        if (f.PriorityId is byte priorityId) query["priorityId"] = priorityId.ToString(CultureInfo.InvariantCulture);
        if (includeDates)
        {
            query["createdFrom"] = f.DateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            query["createdTo"] = f.DateTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        foreach (var (name, value) in extra)
        {
            query[name] = value;
        }

        return query.Count == 0 ? TicketsPath : $"{TicketsPath}?{query}";
    }
}
