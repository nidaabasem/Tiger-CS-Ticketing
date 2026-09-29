using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

/// <summary>
/// The landing point of a Genesys <b>Secure Screen Pop</b> launch URL
/// (<c>/ScreenPop?token=…</c>). Anonymous by necessity: Genesys opens it in a
/// fresh browser or WebView that has no TigerCS cookie. The one-time token is
/// redeemed with TigerCS.Api, the mapped agent is signed in with exactly the
/// session a password login creates (<see cref="WebSessionPrincipal"/>), and
/// the browser is redirected to the requested page.
///
/// <para>
/// <b>Any existing session in this browser is replaced</b>, so a shared
/// machine never keeps showing the previous user once a different agent's
/// Screen Pop opens. The target page is then authorized exactly like any
/// other request — the token grants no access of its own.
/// </para>
/// </summary>
public sealed class ScreenPopModel(AuthApiClient authApiClient) : PageModel
{
    public string Title { get; private set; } = "Screen Pop link not valid";

    public string Message { get; private set; } = "This Screen Pop link is not valid. Open the interaction again from Genesys.";

    public async Task<IActionResult> OnGetAsync(string? token, CancellationToken cancellationToken)
    {
        // The token is in the query string: never cache this response, and
        // never leak the URL onward in a Referer header.
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";

        if (string.IsNullOrWhiteSpace(token))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }

        var result = await authApiClient.RedeemScreenPopAsync(token, cancellationToken);

        if (result.IsSuccess && result.Value is { } session)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                WebSessionPrincipal.Create(
                    session.EmployeeId, session.DisplayName, session.AccessToken, session.Roles, session.PrimaryDepartmentId),
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    ExpiresUtc = session.ExpiresAtUtc
                });

            return LocalRedirect(Url.IsLocalUrl(session.TargetPath) ? session.TargetPath : "/Tickets");
        }

        (Response.StatusCode, Title, Message) = result.Outcome switch
        {
            ApiOutcome.Unauthorized => (StatusCodes.Status401Unauthorized, "Screen Pop link can't be used",
                result.Detail ?? "This Screen Pop link has expired or was already used. Open the interaction again from Genesys."),
            ApiOutcome.Forbidden => (StatusCodes.Status403Forbidden, "Genesys account not linked",
                result.Detail ?? "Your Genesys account is not linked to an active TigerCS user. Contact a System Administrator."),
            ApiOutcome.Unreachable => (StatusCodes.Status503ServiceUnavailable, "Tiger Ticketing unavailable",
                "Tiger Ticketing System could not be reached. Try again in a moment."),
            ApiOutcome.ValidationError => (StatusCodes.Status400BadRequest, Title, Message),
            _ => (StatusCodes.Status503ServiceUnavailable, "Screen Pop unavailable",
                "Screen Pop is not available right now. Sign in to Tiger Ticketing directly.")
        };

        return Page();
    }
}
