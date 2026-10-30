using Microsoft.AspNetCore.Identity;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Administration;
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
    IEmployeeRepository employeeRepository,
    IAuditEntryWriter auditWriter,
    IIdentityUnitOfWork unitOfWork)
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

        // Asked BEFORE the attempt so the lock-out is recorded once, when this
        // attempt causes it — later attempts against an already-locked account
        // find it locked here and write nothing.
        var wasLockedOut = await userManager.IsLockedOutAsync(user);

        var signInResult = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);

        if (signInResult.IsLockedOut)
        {
            if (!wasLockedOut)
            {
                await RecordAutomaticLockoutAsync(user, cancellationToken);
            }

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
    /// The sign-in flow itself tripped Identity's lock-out. There is no acting
    /// administrator (actor null), the policy values are the configured ones
    /// and nothing about the attempted password is recorded.
    /// </summary>
    private async Task RecordAutomaticLockoutAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var options = userManager.Options.Lockout;
        var lockoutEnd = (await userManager.GetLockoutEndDateAsync(user))?.UtcDateTime;
        await auditWriter.WriteAsync(
            actorEmployeeId: null, UserAuditActions.AutomaticLockout, "User", user.Id.ToString(),
            beforeValue: null,
            afterValue: $"Threshold={options.MaxFailedAccessAttempts};LockoutMinutes={(int)options.DefaultLockoutTimeSpan.TotalMinutes};LockoutEndUtc={lockoutEnd:O};Reason=Repeated failed sign-in attempts",
            Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
