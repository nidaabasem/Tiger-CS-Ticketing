using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages;

public sealed class LogoutModel(AuthApiClient authApiClient) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        // Best-effort: sign the local session out regardless of whether the
        // Api call succeeds (e.g. the token already expired server-side).
        await authApiClient.LogoutAsync(cancellationToken);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}
