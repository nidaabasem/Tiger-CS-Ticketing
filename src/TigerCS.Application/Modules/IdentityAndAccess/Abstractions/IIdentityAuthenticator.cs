namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

public enum CredentialCheckOutcome
{
    Success,
    InvalidCredentials,
    Locked
}

public sealed record CredentialCheckResult(
    CredentialCheckOutcome Outcome,
    Guid EmployeeId = default,
    string DisplayName = "",
    IReadOnlyCollection<string>? Roles = null)
{
    public static CredentialCheckResult Success(Guid employeeId, string displayName, IReadOnlyCollection<string> roles) =>
        new(CredentialCheckOutcome.Success, employeeId, displayName, roles);

    public static CredentialCheckResult InvalidCredentials() => new(CredentialCheckOutcome.InvalidCredentials);

    public static CredentialCheckResult Locked() => new(CredentialCheckOutcome.Locked);
}

/// <summary>
/// Verifies staff credentials and reports lockout, without exposing which
/// field (username vs. password) was wrong (Security-Architecture.md §1,
/// no user enumeration). Implemented in Infrastructure against ASP.NET Core
/// Identity's UserManager/SignInManager.
/// </summary>
public interface IIdentityAuthenticator
{
    Task<CredentialCheckResult> CheckCredentialsAsync(
        string username, string password, CancellationToken cancellationToken = default);
}
