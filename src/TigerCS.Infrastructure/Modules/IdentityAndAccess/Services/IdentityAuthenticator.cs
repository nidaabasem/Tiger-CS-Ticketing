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
        var user = await FindUserAsync(username);
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
        // The stamp the issued token is bound to: Identity rotates it on every
        // password reset/change, and ActiveEmployeeHandler compares it on
        // every request — see TigerCsTokenClaims.SecurityStamp.
        var securityStamp = await userManager.GetSecurityStampAsync(user);
        return CredentialCheckResult.Success(employee.EmployeeId, employee.DisplayName, roles.ToList(), securityStamp);
    }

    /// <summary>
    /// The login page asks for "Email or Employee ID", so the identifier is
    /// resolved by Identity's user-name key first and, failing that, by its
    /// email key. Both go through Identity's own normalized lookups. An email
    /// shared by several accounts (Identity does not require it to be unique
    /// here) is ambiguous and resolves to no account rather than guessing.
    /// </summary>
    private async Task<ApplicationUser?> FindUserAsync(string identifier)
    {
        var trimmed = identifier.Trim();

        var user = await userManager.FindByNameAsync(trimmed);
        if (user is not null || !trimmed.Contains('@'))
        {
            return user;
        }

        try
        {
            return await userManager.FindByEmailAsync(trimmed);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
