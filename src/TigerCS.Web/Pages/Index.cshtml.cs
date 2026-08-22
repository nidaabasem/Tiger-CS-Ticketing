using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TigerCS.Web.Pages;

/// <summary>
/// The root path. There is no dashboard in this increment (executive
/// dashboards are explicitly out of scope), so the queue is the landing screen
/// and this simply forwards to it. Unauthenticated callers are redirected to
/// sign in first by the fallback authorization policy.
/// </summary>
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Tickets/Index");
}
