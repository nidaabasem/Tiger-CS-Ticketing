using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Web.Models;

namespace TigerCS.Web.Pages;

public sealed class TicketsModel : PageModel
{
    public IReadOnlyList<TicketRecord> TodaysTickets { get; private set; } = [];

    public IReadOnlyList<TicketRecord> PendingTickets { get; private set; } = [];

    public IReadOnlyList<string> Departments { get; private set; } = [];

    public IReadOnlyList<string> Owners { get; private set; } = [];

    public void OnGet()
    {
        TodaysTickets = TicketMockRepository.TodaysTickets;
        PendingTickets = TicketMockRepository.PendingTickets;

        var all = TodaysTickets.Concat(PendingTickets).ToList();
        Departments = all.Select(t => t.Department).Distinct().OrderBy(d => d).ToList();
        Owners = all.Select(t => t.Owner).Distinct().OrderBy(o => o).ToList();
    }
}
