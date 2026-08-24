using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

public sealed class LoginModel(AuthApiClient authApiClient) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public bool SessionExpired { get; private set; }

    public string? ReturnUrl { get; private set; }

    public void OnGet(bool sessionExpired = false, string? returnUrl = null)
    {
        SessionExpired = sessionExpired;
        ReturnUrl = returnUrl;
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await authApiClient.LoginAsync(new LoginRequestDto(Input.Identifier, Input.Password), cancellationToken);

        if (result.IsSuccess && result.Value is not null)
        {
            var response = result.Value;

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, response.EmployeeId.ToString()),
                new(ClaimTypes.Name, response.DisplayName),
                new(TigerCsClaimTypes.AccessToken, response.AccessToken),
            };
            claims.AddRange(response.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
            if (response.PrimaryDepartmentId is int departmentId)
            {
                claims.Add(new Claim(TigerCsClaimTypes.PrimaryDepartmentId, departmentId.ToString()));
            }

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    IsPersistent = Input.RememberMe,
                    ExpiresUtc = response.ExpiresAtUtc
                });

            return LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/Tickets");
        }

        ErrorMessage = result.Outcome switch
        {
            ApiOutcome.Locked => "This account is locked. Contact your administrator.",
            ApiOutcome.ValidationError => "Enter your email or employee ID and password to continue.",
            ApiOutcome.Unreachable => "Tiger Ticketing System could not be reached. Try again in a moment.",
            _ => "The email/employee ID or password was not accepted."
        };
        return Page();
    }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Enter your email or employee ID.")]
        public string Identifier { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter your password.")]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }
    }
}
