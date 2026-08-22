using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Web.Services;

namespace TigerCS.Web.Pages.Account;

/// <summary>
/// Ends the session here and, best-effort, at the API.
///
/// <para>
/// POST only, and so covered by the global anti-forgery filter: a GET sign-out
/// can be triggered by any third-party page embedding an image, which is a
/// nuisance rather than a breach but is trivially avoidable.
/// </para>
/// </summary>
public sealed class LogoutModel(AuthApiClient authApiClient, ILogger<LogoutModel> logger) : PageModel
{
    /// <summary>A stray GET goes back to the queue rather than signing anyone out.</summary>
    public IActionResult OnGet() => RedirectToPage("/Tickets/Index");

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        // Told to the API first, while the cookie still carries the token that
        // authenticates the call. A failure here must not strand the user in a
        // signed-in shell, so the local cookie is cleared regardless.
        var result = await authApiClient.LogoutAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            logger.LogInformation(
                "API sign-out returned {StatusCode}; clearing the local session anyway.",
                (int)result.Problem!.StatusCode);
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        return RedirectToPage("/Account/Login", new { reason = "signedout" });
    }
}
