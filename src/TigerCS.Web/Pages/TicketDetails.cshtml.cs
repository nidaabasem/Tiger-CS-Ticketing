using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Web.Models;

namespace TigerCS.Web.Pages;

public sealed class TicketDetailsModel : PageModel
{
    public TicketRecord Ticket { get; private set; } = null!;

    public void OnGet(string id)
    {
        Ticket = TicketMockRepository.GetById(id) ?? TicketMockRepository.GetById("TKT-2026-01842")!;
    }
}
