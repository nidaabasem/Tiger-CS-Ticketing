using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

namespace TigerCS.Tests.Administration.Services;

/// <summary>
/// Stands in for ASP.NET Core Identity in service tests: accounts, roles
/// and a minimal password policy (8+ characters), so the administration
/// services can be exercised without the Identity store. Also serves as the
/// role reader, exactly as Identity does for the real services.
/// </summary>
public sealed class FakeUserAccountManager : IUserAccountManager, IUserRoleReader
{
    private sealed class Account
    {
        public required Guid Id { get; init; }
        public required string UserName { get; set; }
        public string? Email { get; set; }
        public List<string> Roles { get; } = [];
    }

    private readonly Dictionary<Guid, Account> _accounts = [];

    public FakeUserAccountManager Add(Guid employeeId, string userName, params string[] roles)
    {
        var account = new Account { Id = employeeId, UserName = userName };
        account.Roles.AddRange(roles);
        _accounts[employeeId] = account;
        return this;
    }

    public Task<UserAccountInfo?> GetAccountAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.TryGetValue(employeeId, out var a) ? new UserAccountInfo(a.Id, a.UserName, a.Email, false) : null);

    public Task<IReadOnlyDictionary<Guid, UserAccountInfo>> GetAccountsAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, UserAccountInfo>>(_accounts.Values
            .Where(a => employeeIds.Contains(a.Id))
            .ToDictionary(a => a.Id, a => new UserAccountInfo(a.Id, a.UserName, a.Email, false)));

    public Task<UserAccountResult> CreateAccountAsync(string userName, string? email, string initialPassword, CancellationToken cancellationToken = default)
    {
        if (_accounts.Values.Any(a => a.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(UserAccountResult.Failure([$"Username '{userName}' is already taken."]));
        }

        if (initialPassword.Length < 8)
        {
            return Task.FromResult(UserAccountResult.Failure(["Passwords must be at least 8 characters."]));
        }

        var id = Guid.NewGuid();
        _accounts[id] = new Account { Id = id, UserName = userName, Email = email };
        return Task.FromResult(UserAccountResult.Success(id));
    }

    public Task<UserAccountResult> UpdateEmailAsync(Guid employeeId, string? email, CancellationToken cancellationToken = default)
    {
        if (!_accounts.TryGetValue(employeeId, out var account))
        {
            return Task.FromResult(UserAccountResult.Failure(["The user account was not found."]));
        }

        account.Email = email;
        return Task.FromResult(UserAccountResult.Success(employeeId));
    }

    public Task<UserAccountResult> SetRolesAsync(Guid employeeId, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken = default)
    {
        if (!_accounts.TryGetValue(employeeId, out var account))
        {
            return Task.FromResult(UserAccountResult.Failure(["The user account was not found."]));
        }

        account.Roles.Clear();
        account.Roles.AddRange(roleNames);
        return Task.FromResult(UserAccountResult.Success(employeeId));
    }

    public Task<IReadOnlyCollection<Guid>> SearchAccountIdsAsync(string search, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<Guid>>(_accounts.Values
            .Where(a => a.UserName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (a.Email?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(a => a.Id)
            .ToList());

    public Task<IReadOnlyCollection<string>> GetRolesAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<string>>(_accounts.TryGetValue(employeeId, out var a) ? a.Roles.ToList() : []);
}
