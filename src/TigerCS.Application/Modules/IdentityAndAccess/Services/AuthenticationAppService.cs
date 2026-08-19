using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Application.Modules.IdentityAndAccess.Services;

/// <summary>MVP-API-Contracts.md §1.1/§1.2.</summary>
public sealed class AuthenticationAppService(
    IIdentityAuthenticator authenticator,
    ITokenService tokenService,
    IUserDepartmentAssignmentRepository assignmentRepository)
{
    public async Task<LoginResult> LoginAsync(LoginRequestDto request, CancellationToken cancellationToken = default)
    {
        var check = await authenticator.CheckCredentialsAsync(request.Username, request.Password, cancellationToken);

        switch (check.Outcome)
        {
            case CredentialCheckOutcome.InvalidCredentials:
                return LoginResult.InvalidCredentials();
            case CredentialCheckOutcome.Locked:
                return LoginResult.Locked();
            case CredentialCheckOutcome.Success:
            default:
                var primary = await assignmentRepository.GetPrimaryAsync(check.EmployeeId, cancellationToken);
                var token = tokenService.CreateAccessToken(check.EmployeeId, check.DisplayName, check.Roles ?? []);
                return LoginResult.Success(new LoginResponseDto(
                    token.AccessToken,
                    token.ExpiresAtUtc,
                    check.EmployeeId,
                    check.DisplayName,
                    check.Roles ?? [],
                    primary?.DepartmentId));
        }
    }

    /// <summary>
    /// Stateless JWT with no revocation list is used at MVP (no refresh-token or
    /// token-revocation mechanism is specified in the approved API contract, and
    /// none is invented here). Logout is a client-side token discard; this method
    /// exists as the seam for the audit entry the contract describes, once the
    /// Audit module (S-05, out of scope for this increment) exists.
    /// </summary>
    public Task LogoutAsync(Guid employeeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
