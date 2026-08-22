using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TigerCS.Web.Pages;

/// <summary>
/// Where cookie authentication sends an authenticated user who fails an
/// authorization requirement.
///
/// <para>
/// Distinct from the sign-in page on purpose: sending a signed-in user back to
/// a login form implies that signing in again would help, which it would not.
/// </para>
/// </summary>
public sealed class ForbiddenModel : PageModel
{
    public void OnGet()
    {
    }
}
