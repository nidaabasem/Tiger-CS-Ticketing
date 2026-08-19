namespace TigerCS.Application.Modules.IdentityAndAccess.Dto;

// MVP-API-Contracts.md §1.1 POST /api/auth/login
public sealed record LoginRequestDto(string Username, string Password);

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
