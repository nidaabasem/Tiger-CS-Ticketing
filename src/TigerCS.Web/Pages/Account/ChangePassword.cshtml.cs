using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages.Account;

/// <summary>
/// "Change my password" for the signed-in user. The Api verifies the current
/// password and applies the policy; a success rotates the account's security
/// stamp, which makes the token inside this session's cookie invalid — so the
/// page signs the cookie session out itself and sends the user to sign in
/// again, rather than leaving them on a session whose next Api call would 403.
/// </summary>
public sealed class ChangePasswordModel(AuthApiClient authApi) : PageModel
{
    /// <summary>The policy stated beside the new-password field — the pilot defaults in docs/DEV-SETUP.md §3.</summary>
    public const string PasswordPolicyText =
        "At least 8 characters, with an uppercase letter, a lowercase letter, a digit and a symbol.";

    /// <summary>The query flag Login reads to show "Your password was changed. Please sign in again."</summary>
    public const string LoginQueryFlag = "passwordChanged";

    [BindProperty] public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public CurrentUser? Viewer => CurrentUser.FromPrincipal(User);

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (Input.NewPassword != Input.ConfirmPassword)
        {
            ModelState.AddModelError("Input.ConfirmPassword", "The two new passwords do not match.");
            return Page();
        }

        var result = await authApi.ChangePasswordAsync(
            new ChangePasswordRequestDto(Input.CurrentPassword, Input.NewPassword), cancellationToken);

        if (result.IsSuccess)
        {
            // The token in this cookie no longer passes the Api's security-stamp
            // check; end the Web session cleanly and ask for a fresh sign-in.
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToPage("/Login", new Dictionary<string, string> { [LoginQueryFlag] = "true" });
        }

        ErrorMessage = result.Outcome switch
        {
            ApiOutcome.UnprocessableEntity or ApiOutcome.ValidationError => result.Detail ?? "The password could not be changed.",
            ApiOutcome.Unauthorized or ApiOutcome.Forbidden => "Your session is no longer valid. Sign out and sign in again.",
            ApiOutcome.Unreachable or ApiOutcome.BadGateway => "Tiger Ticketing System could not reach the ticketing service.",
            _ => result.Detail ?? "The password could not be changed."
        };
        return Page();
    }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Enter your current password.")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter a new password.")]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Confirm the new password.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
