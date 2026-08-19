using Microsoft.AspNetCore.Identity;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Infrastructure.Identity;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

/// <summary>
/// Security-Architecture.md §1/§13/§14: no user enumeration (identical
/// generic outcome whether the username or the password was wrong, and for
/// a deactivated account), account lockout via Identity's built-in
/// mechanism, and inactive-employee blocking at login.
/// </summary>
public sealed class IdentityAuthenticator(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IEmployeeRepository employeeRepository)
    : IIdentityAuthenticator
{
    public async Task<CredentialCheckResult> CheckCredentialsAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByNameAsync(username);
        if (user is null)
        {
            return CredentialCheckResult.InvalidCredentials();
        }

        var signInResult = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);

        if (signInResult.IsLockedOut)
        {
            return CredentialCheckResult.Locked();
        }

        if (!signInResult.Succeeded)
        {
            return CredentialCheckResult.InvalidCredentials();
        }

        var employee = await employeeRepository.GetByIdAsync(user.Id, cancellationToken);
        if (employee is null || !employee.IsActive)
        {
            // Deactivated (or missing) Employee record: same generic outcome as a
            // wrong password — do not reveal account status through the error.
            return CredentialCheckResult.InvalidCredentials();
        }

        var roles = await userManager.GetRolesAsync(user);
        return CredentialCheckResult.Success(employee.EmployeeId, employee.DisplayName, roles.ToList());
    }
}
