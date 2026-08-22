using System.Globalization;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Web.Infrastructure;
using TigerCS.Web.Services;

namespace TigerCS.Web.Pages.Account;

/// <summary>
/// Sign-in against <c>POST /api/auth/login</c> (MVP-API-Contracts.md §1.1).
///
/// <para>
/// On success the returned JWT is placed inside this application's own
/// encrypted, HttpOnly authentication cookie rather than being handed to the
/// browser as a readable value. See <c>Program.cs</c> for why that is the
/// chosen model and what it implies for CSRF.
/// </para>
/// </summary>
[AllowAnonymous]
public sealed class LoginModel(
    AuthApiClient authApiClient,
    UsersApiClient usersApiClient,
    ILogger<LoginModel> logger) : PageModel
{
    /// <summary>A banner shown above the form. Severity matches the stylesheet's notice variants.</summary>
    public readonly record struct BannerSpec(string Severity, string Glyph, string Title, string Message);

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string? UsernameError { get; private set; }

    public string? PasswordError { get; private set; }

    public BannerSpec? Banner { get; private set; }

    public IActionResult OnGet(string? returnUrl = null, string? reason = null)
    {
        // Already signed in and arriving at the login page: go where they were
        // headed rather than showing a form they do not need.
        if (User.Identity?.IsAuthenticated == true && reason is null)
        {
            return RedirectToSafeReturnUrl(returnUrl);
        }

        Banner = reason switch
        {
            "expired" => new BannerSpec(
                "warn", "⚠", "Session expired",
                "Your session has ended. Please sign in again to continue."),
            "signedout" => new BannerSpec(
                "info", "✓", "Signed out", "You have been signed out."),
            _ => null
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        // Client-side validation is a convenience; this is the check that counts.
        if (string.IsNullOrWhiteSpace(Username))
        {
            UsernameError = "Enter your username or email.";
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            PasswordError = "Enter your password.";
        }

        if (UsernameError is not null || PasswordError is not null)
        {
            return Page();
        }

        var login = await authApiClient.LoginAsync(Username, Password, cancellationToken);

        if (!login.IsSuccess)
        {
            Banner = ToBanner(login.Problem!);

            // The username is echoed back so the user does not retype it; the
            // password never is.
            Password = string.Empty;

            // Logged without the username: a failed-login log line naming an
            // account is itself an account-enumeration surface for anyone with
            // log access. The status code is what operations needs.
            logger.LogInformation(
                "Sign-in rejected with status {StatusCode}.", (int)login.Problem!.StatusCode);

            return Page();
        }

        var response = login.Required;

        // The profile call supplies department memberships, which the shell
        // needs to label departments and which the action policy needs to
        // decide what to offer. It is not authorization - it is display data
        // the API already vouched for.
        var profile = await usersApiClient.GetCurrentUserAsync(cancellationToken);

        await SignInAsync(response, profile.Value, cancellationToken);

        return RedirectToSafeReturnUrl(returnUrl);
    }

    private async Task SignInAsync(
        LoginResponseDto response, CurrentUserResponseDto? profile, CancellationToken cancellationToken)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, response.EmployeeId.ToString()),
            new(ClaimTypes.Name, response.DisplayName),

            // The access token lives here and nowhere else. This claim is
            // serialised into the authentication cookie, which Data Protection
            // encrypts and signs before it reaches the browser.
            new(TigerCsClaimTypes.AccessToken, response.AccessToken),
            new(
                TigerCsClaimTypes.AccessTokenExpiresAtUtc,
                new DateTimeOffset(DateTime.SpecifyKind(response.ExpiresAtUtc, DateTimeKind.Utc))
                    .ToString("o", CultureInfo.InvariantCulture))
        };

        claims.AddRange(response.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        if (response.PrimaryDepartmentId is int primaryDepartmentId)
        {
            claims.Add(new Claim(
                TigerCsClaimTypes.PrimaryDepartmentId,
                primaryDepartmentId.ToString(CultureInfo.InvariantCulture)));
        }

        if (profile is not null)
        {
            foreach (var department in profile.Departments)
            {
                claims.Add(new Claim(
                    TigerCsClaimTypes.DepartmentId,
                    department.DepartmentId.ToString(CultureInfo.InvariantCulture)));
                claims.Add(new Claim(
                    TigerCsClaimTypes.DepartmentName,
                    $"{department.DepartmentId.ToString(CultureInfo.InvariantCulture)}:{department.Name}"));
            }
        }
        else
        {
            // The sign-in still succeeds: the token is valid and the API is the
            // authority regardless. Department names simply fall back to ids
            // until the next sign-in, which is a cosmetic degradation, not a
            // permission one.
            logger.LogWarning("Signed in but could not read the caller's profile; department labels will show ids.");
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = false,

                // The cookie cannot outlive the credential it carries. Without
                // this, a user would keep a signed-in shell whose every call
                // 401s the moment the token expires.
                ExpiresUtc = new DateTimeOffset(DateTime.SpecifyKind(response.ExpiresAtUtc, DateTimeKind.Utc)),
                AllowRefresh = false
            });

        // Best-effort, and deliberately not awaited into the user's path in a
        // way that could fail the sign-in.
        await Task.CompletedTask;
        _ = cancellationToken;
    }

    /// <summary>
    /// Maps the API's sign-in failures onto the three states the brief calls
    /// for. A locked account is <c>423</c>; a deactivated one is refused as
    /// <c>401</c> by the API, which does not distinguish it on the wire, so
    /// the invalid-credentials wording covers both and names the remedy.
    /// </summary>
    private static BannerSpec ToBanner(ApiProblem problem) => problem.StatusCode switch
    {
        HttpStatusCode.Locked => new BannerSpec(
            "error", "⚠", "Account locked",
            "This account is locked. Contact your administrator to unlock it."),

        HttpStatusCode.Unauthorized => new BannerSpec(
            "error", "⚠", "Sign-in failed",
            "That username and password combination was not accepted. "
            + "If your account has been deactivated, contact your administrator."),

        HttpStatusCode.BadRequest => new BannerSpec(
            "error", "⚠", "Sign-in failed", "Enter both your username and your password."),

        HttpStatusCode.BadGateway => new BannerSpec(
            "warn", "⚠", "Service unavailable",
            "The ticketing service could not be reached. Please try again shortly."),

        _ => new BannerSpec(
            "error", "⚠", "Sign-in failed", "Sign-in could not be completed. Please try again.")
    };

    /// <summary>
    /// Only ever redirects within this application.
    ///
    /// <para>
    /// <c>returnUrl</c> arrives from the query string, so treating it as
    /// trustworthy would make this an open redirect - a phishing primitive
    /// that is especially attractive on a login page, since the victim has
    /// just typed a password. <c>IsLocalUrl</c> rejects absolute URLs,
    /// protocol-relative URLs and anything else that leaves this origin.
    /// </para>
    /// </summary>
    private IActionResult RedirectToSafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl)
            : RedirectToPage("/Tickets/Index");
}
