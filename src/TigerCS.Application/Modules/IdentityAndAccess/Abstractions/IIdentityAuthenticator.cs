namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

public enum CredentialCheckOutcome
{
    Success,
    InvalidCredentials,
    Locked
}

/// <summary>
/// The outcome of a credential check. On success it carries, besides the
/// identity and roles, the account's Identity security stamp at sign-in —
/// issued into the token's <c>sst</c> claim, so the token dies when the
/// stamp is rotated by a password reset or change.
/// </summary>
public sealed record CredentialCheckResult(
    CredentialCheckOutcome Outcome,
    Guid EmployeeId = default,
    string DisplayName = "",
    IReadOnlyCollection<string>? Roles = null,
    string SecurityStamp = "")
{
    public static CredentialCheckResult Success(Guid employeeId, string displayName, IReadOnlyCollection<string> roles, string securityStamp) =>
        new(CredentialCheckOutcome.Success, employeeId, displayName, roles, securityStamp);

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
