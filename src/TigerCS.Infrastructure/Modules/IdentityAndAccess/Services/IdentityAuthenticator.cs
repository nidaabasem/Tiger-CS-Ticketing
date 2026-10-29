using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
        var normalizedKeysNeedRepair = false;
        if (user is null)
        {
            // Identity finds users by NormalizedUserName only. A row written outside Identity (direct SQL, an import) can
            // carry a wrong or empty normalized key and would never be found. Look it up by UserName instead; the keys are
            // rewritten below, but only once the password has been proven.
            user = await userManager.Users.FirstOrDefaultAsync(u => u.UserName == username, cancellationToken);
            normalizedKeysNeedRepair = user is not null;
        }

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

        if (normalizedKeysNeedRepair)
        {
            // UpdateAsync recomputes NormalizedUserName and NormalizedEmail, so the next sign-in takes the normal path.
            await userManager.UpdateAsync(user);
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
}
