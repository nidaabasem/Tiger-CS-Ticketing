namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

/// <summary>The Identity account behind an employee — the fields ASP.NET Core Identity owns (never the password hash).</summary>
public sealed record UserAccountInfo(Guid EmployeeId, string UserName, string? Email, bool IsLockedOut);

/// <summary>Outcome of an Identity operation, in Identity's own error wording (password policy, duplicate user name, …).</summary>
public sealed record UserAccountResult(bool Succeeded, IReadOnlyList<string> Errors, Guid EmployeeId = default)
{
    public static UserAccountResult Success(Guid employeeId) => new(true, [], employeeId);

    public static UserAccountResult Failure(IReadOnlyList<string> errors) => new(false, errors);
}

/// <summary>
/// The Administration phase's boundary to ASP.NET Core Identity. Every
/// credential/role operation goes through Identity's own managers behind
/// this interface — the application layer never sees or manipulates a
/// password hash, and no custom password handling exists.
/// </summary>
public interface IUserAccountManager
{
    Task<UserAccountInfo?> GetAccountAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, UserAccountInfo>> GetAccountsAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default);

    /// <summary>Creates the Identity user with Identity's password validators; the initial password is set exactly as the development seed sets one.</summary>
    Task<UserAccountResult> CreateAccountAsync(string userName, string? email, string initialPassword, CancellationToken cancellationToken = default);

    Task<UserAccountResult> UpdateEmailAsync(Guid employeeId, string? email, CancellationToken cancellationToken = default);

    /// <summary>Replaces the user's role set with exactly the given fixed roles (adds the missing, removes the extra).</summary>
    Task<UserAccountResult> SetRolesAsync(Guid employeeId, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken = default);

    /// <summary>Employee ids whose user name or email contains the search text (case-insensitive) — for the administration user search.</summary>
    Task<IReadOnlyCollection<Guid>> SearchAccountIdsAsync(string search, CancellationToken cancellationToken = default);
}
