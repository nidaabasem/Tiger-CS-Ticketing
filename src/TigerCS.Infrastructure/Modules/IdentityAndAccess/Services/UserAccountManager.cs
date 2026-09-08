using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

/// <summary>
/// <see cref="IUserAccountManager"/> over ASP.NET Core Identity's own
/// <see cref="UserManager{TUser}"/>. Every credential and role change goes
/// through Identity (its validators, its hashing, its normalized keys);
/// nothing here reads or writes a password hash.
/// </summary>
public sealed class UserAccountManager(UserManager<ApplicationUser> userManager, TigerCsDbContext dbContext) : IUserAccountManager
{
    public async Task<UserAccountInfo?> GetAccountAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(employeeId.ToString());
        return user is null ? null : await ToInfoAsync(user);
    }

    public async Task<IReadOnlyDictionary<Guid, UserAccountInfo>> GetAccountsAsync(
        IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default)
    {
        var users = await dbContext.Users
            .Where(u => employeeIds.Contains(u.Id))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<Guid, UserAccountInfo>();
        foreach (var user in users)
        {
            result[user.Id] = await ToInfoAsync(user);
        }

        return result;
    }

    public async Task<UserAccountResult> CreateAccountAsync(
        string userName, string? email, string initialPassword, CancellationToken cancellationToken = default)
    {
        var user = new ApplicationUser { UserName = userName, Email = string.IsNullOrWhiteSpace(email) ? null : email };
        var result = await userManager.CreateAsync(user, initialPassword);
        return result.Succeeded
            ? UserAccountResult.Success(user.Id)
            : UserAccountResult.Failure(result.Errors.Select(e => e.Description).ToList());
    }

    public async Task<UserAccountResult> UpdateEmailAsync(Guid employeeId, string? email, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(employeeId.ToString());
        if (user is null)
        {
            return UserAccountResult.Failure(["The user account was not found."]);
        }

        var result = await userManager.SetEmailAsync(user, string.IsNullOrWhiteSpace(email) ? null : email);
        return result.Succeeded
            ? UserAccountResult.Success(user.Id)
            : UserAccountResult.Failure(result.Errors.Select(e => e.Description).ToList());
    }

    public async Task<UserAccountResult> SetRolesAsync(
        Guid employeeId, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(employeeId.ToString());
        if (user is null)
        {
            return UserAccountResult.Failure(["The user account was not found."]);
        }

        var unknown = roleNames.Where(r => !Roles.All.Contains(r, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            return UserAccountResult.Failure([$"Unknown role(s): {string.Join(", ", unknown)}."]);
        }

        var current = await userManager.GetRolesAsync(user);
        var toAdd = roleNames.Except(current, StringComparer.Ordinal).ToList();
        var toRemove = current.Except(roleNames, StringComparer.Ordinal).ToList();

        if (toAdd.Count > 0)
        {
            var addResult = await userManager.AddToRolesAsync(user, toAdd);
            if (!addResult.Succeeded)
            {
                return UserAccountResult.Failure(addResult.Errors.Select(e => e.Description).ToList());
            }
        }

        if (toRemove.Count > 0)
        {
            var removeResult = await userManager.RemoveFromRolesAsync(user, toRemove);
            if (!removeResult.Succeeded)
            {
                return UserAccountResult.Failure(removeResult.Errors.Select(e => e.Description).ToList());
            }
        }

        return UserAccountResult.Success(user.Id);
    }

    public async Task<IReadOnlyCollection<Guid>> SearchAccountIdsAsync(string search, CancellationToken cancellationToken = default)
    {
        var normalized = search.Trim().ToUpperInvariant();
        return await dbContext.Users
            .Where(u => (u.NormalizedUserName != null && u.NormalizedUserName.Contains(normalized))
                || (u.NormalizedEmail != null && u.NormalizedEmail.Contains(normalized)))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<UserAccountInfo> ToInfoAsync(ApplicationUser user) =>
        new(user.Id, user.UserName ?? string.Empty, user.Email, await userManager.IsLockedOutAsync(user));
}
