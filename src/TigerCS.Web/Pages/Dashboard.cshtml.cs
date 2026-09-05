using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

/// <summary>One KPI card on the Dashboard. <see cref="Href"/> links to a matching pre-filtered queue view when one exists; emphasis keys map to the fixed palette (critical for breach, gold for attention).</summary>
public sealed record KpiCard(string Label, int Value, string? Emphasis = null, string? Href = null);

/// <summary>
/// The operational landing page (Customer Workspace phase): a prominent
/// customer search, role-appropriate KPI cards, and the Tickets Requiring
/// Attention queue. All numbers come from <c>GET /api/dashboard</c>, which
/// scopes them server-side to the caller's own visible departments — this
/// page only chooses which of those numbers each role benefits from seeing;
/// it never widens or computes data of its own. Card visibility here is a
/// presentation choice, not an authorization boundary (the Api enforces
/// those).
/// </summary>
public sealed class DashboardModel(
    DashboardApiClient dashboardApiClient,
    TicketNameResolver nameResolver) : PageModel
{
    private static readonly string[] SupervisoryRoles =
    [
        Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo, Roles.SystemAdministrator
    ];

    public CurrentUser? Viewer { get; private set; }
    public DashboardSummaryDto? Summary { get; private set; }
    public ApiOutcome Outcome { get; private set; }
    public IReadOnlyList<KpiCard> Cards { get; private set; } = [];
    public IReadOnlyList<(DashboardAttentionTicketDto Ticket, string? DepartmentName, string? OwnerName)> AttentionRows { get; private set; } = [];
    public TicketNameResolver NameResolver => nameResolver;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Viewer = CurrentUser.FromPrincipal(User);
        await nameResolver.PrimeDepartmentsAsync(cancellationToken);

        var result = await dashboardApiClient.GetSummaryAsync(cancellationToken);
        Outcome = result.Outcome;
        if (!result.IsSuccess || result.Value is null)
        {
            return;
        }

        Summary = result.Value;
        Cards = BuildCards(Summary, Viewer);

        var rows = new List<(DashboardAttentionTicketDto, string?, string?)>();
        foreach (var ticket in Summary.AttentionTickets)
        {
            // The responsible department is resolved for every row, assigned
            // or not: an employee-less ticket still shows its department
            // queue as the accountable destination.
            var departmentName = nameResolver.TryGetDepartmentName(ticket.CurrentDepartmentId);
            var ownerName = ticket.CurrentOwnerEmployeeId is Guid ownerId
                ? await nameResolver.ResolveOwnerNameAsync(ticket.CurrentDepartmentId, ownerId, cancellationToken)
                : null;
            rows.Add((ticket, departmentName, ownerName));
        }

        AttentionRows = rows;
    }

    /// <summary>
    /// The role-appropriate card set — the phase's approved recommendation:
    /// supervisors/managers watch the queue's health (unassigned, SLA,
    /// severity, reopens), agents watch their own workload, department users
    /// their department queue (already scoped server-side).
    /// </summary>
    private static IReadOnlyList<KpiCard> BuildCards(DashboardSummaryDto s, CurrentUser? viewer)
    {
        var roles = viewer?.Roles ?? [];
        var myTicketsHref = viewer is null ? "/Tickets" : $"/Tickets?ownerEmployeeId={viewer.EmployeeId}";

        if (roles.Any(SupervisoryRoles.Contains))
        {
            return
            [
                new KpiCard("Open Tickets", s.OpenTickets),
                // Label only — the count is unchanged: tickets that have a
                // responsible department but no CurrentOwnerEmployeeId. Those
                // tickets are not ownerless; they sit in their department's
                // queue, so the card is named for where they actually are.
                new KpiCard("In Department Queue", s.Unassigned, s.Unassigned > 0 ? "attention" : null),
                new KpiCard("SLA At Risk", s.SlaAtRisk, s.SlaAtRisk > 0 ? "attention" : null),
                new KpiCard("SLA Breached", s.SlaBreached, s.SlaBreached > 0 ? "critical" : null),
                new KpiCard("Critical / High", s.CriticalOrHigh),
                new KpiCard("Reopened", s.Reopened),
                new KpiCard("Resolved Today", s.ResolvedToday)
            ];
        }

        if (roles.Contains(Roles.CsAgent))
        {
            return
            [
                new KpiCard("My Tickets", s.MyTickets, null, myTicketsHref),
                new KpiCard("Open Tickets", s.OpenTickets),
                new KpiCard("SLA At Risk", s.SlaAtRisk, s.SlaAtRisk > 0 ? "attention" : null),
                new KpiCard("Pending Customer", s.PendingCustomer, null, "/Tickets?ticketStatus=PendingCustomer")
            ];
        }

        // Department users: their departments' queue only — the counts are
        // already department-scoped by the Api.
        return
        [
            new KpiCard("My Tickets", s.MyTickets, null, myTicketsHref),
            new KpiCard("Open Tickets", s.OpenTickets),
            new KpiCard("SLA At Risk", s.SlaAtRisk, s.SlaAtRisk > 0 ? "attention" : null),
            new KpiCard("SLA Breached", s.SlaBreached, s.SlaBreached > 0 ? "critical" : null)
        ];
    }
}
