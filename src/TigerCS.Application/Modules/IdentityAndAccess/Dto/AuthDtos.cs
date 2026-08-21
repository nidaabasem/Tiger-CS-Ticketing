namespace TigerCS.Application.Modules.IdentityAndAccess.Dto;

/// <summary>Credentials for POST /api/auth/login (MVP-API-Contracts.md §1.1).</summary>
/// <param name="Username">Required. Rejected with 400 when blank.</param>
/// <param name="Password">Required. Rejected with 400 when blank.</param>
public sealed record LoginRequestDto(string Username, string Password);

/// <summary>A successful sign-in (MVP-API-Contracts.md §1.1).</summary>
/// <param name="AccessToken">The JWT to send as <c>Authorization: Bearer {token}</c>. In Swagger UI, paste this value alone into the Authorize box.</param>
/// <param name="ExpiresAtUtc">When the token stops being accepted, in UTC.</param>
/// <param name="EmployeeId">The signed-in employee.</param>
/// <param name="DisplayName">The employee's display name.</param>
/// <param name="Roles">Every role the employee holds, by name (e.g. "CS Agent").</param>
/// <param name="PrimaryDepartmentId">The employee's primary department, or null when they have no primary assignment.</param>
public sealed record LoginResponseDto(
    string AccessToken,
    DateTime ExpiresAtUtc,
    Guid EmployeeId,
    string DisplayName,
    IReadOnlyCollection<string> Roles,
    int? PrimaryDepartmentId);

public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    Locked
}

public sealed record LoginResult(LoginOutcome Outcome, LoginResponseDto? Response = null)
{
    public static LoginResult Success(LoginResponseDto response) => new(LoginOutcome.Success, response);
    public static LoginResult InvalidCredentials() => new(LoginOutcome.InvalidCredentials);
    public static LoginResult Locked() => new(LoginOutcome.Locked);
}
