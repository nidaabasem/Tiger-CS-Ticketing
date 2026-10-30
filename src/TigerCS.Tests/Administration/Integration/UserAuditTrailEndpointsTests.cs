using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Administration.Integration;

/// <summary>
/// Activity History and lock-out facts through the real Api host: every
/// user-administration action lands in the existing audit trail with the
/// signed-in administrator's id, the automatic lock-out is recorded once and
/// attributed to nobody, accounts with no history say "not recorded" instead
/// of guessing, and only System Administrators can read any of it.
/// </summary>
public class UserAuditTrailEndpointsTests : IClassFixture<TigerCsApiFactory>
{
    private const string NewPassword = "Brand-New-Pass-7!";
    private readonly TigerCsApiFactory _factory;

    public UserAuditTrailEndpointsTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<HttpClient> SignInAsync(string username, string password)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private async Task<(HttpClient Client, Guid EmployeeId)> AdminAsync()
    {
        var (name, password, id) = await _factory.SeedEmployeeAsync(Roles.SystemAdministrator);
        return (await SignInAsync(name, password), id);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri}: {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<int> LockoutThresholdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().Options.Lockout.MaxFailedAccessAttempts;
    }

    private async Task<HttpStatusCode> LoginAsync(string username, string password) =>
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).StatusCode;

    // ------------------------------------------------- every admin action

    [Fact]
    public async Task EveryUserAdministrationAction_IsInTheActivityHistory_WithTheAdministratorsId_AndNoCredentials()
    {
        var (admin, adminId) = await AdminAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Audit " + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid().ToString("N")[..8]);
        var otherDepartmentId = await _factory.CreateDepartmentAsync("Audit2 " + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid().ToString("N")[..8]);

        var created = await ReadAsync<AdminUserDto>(await admin.PostAsJsonAsync("/api/admin/users",
            new CreateUserRequestDto($"a{Guid.NewGuid():N}"[..20], "old@example.test", "Audited User", false, "Initial-Pass-1!", [Roles.CsAgent], departmentId)));
        var id = created.EmployeeId;
        await ReadAsync<AdminUserDto>(await admin.PutAsJsonAsync($"/api/admin/users/{id}/profile", new UpdateUserProfileRequestDto("Audited Renamed", "new@example.test", false)));
        await ReadAsync<AdminUserDto>(await admin.PutAsJsonAsync($"/api/admin/users/{id}/roles", new SetUserRolesRequestDto([Roles.ReportingUser])));
        await ReadAsync<AdminUserDto>(await admin.PostAsJsonAsync($"/api/admin/users/{id}/departments", new AddDepartmentMembershipRequestDto(otherDepartmentId, false)));
        await ReadAsync<AdminUserDto>(await admin.DeleteAsync($"/api/admin/users/{id}/departments/{otherDepartmentId}"));
        await ReadAsync<AdminUserDto>(await admin.PatchAsJsonAsync($"/api/admin/users/{id}/activation", new SetActiveRequestDto(false, "left the company")));
        await ReadAsync<AdminUserDto>(await admin.PatchAsJsonAsync($"/api/admin/users/{id}/activation", new SetActiveRequestDto(true, "came back")));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/users/{id}/password", new ResetUserPasswordRequestDto(NewPassword, "forgot it"))).StatusCode);

        var page = await ReadAsync<UserActivityPageDto>(await admin.GetAsync($"/api/admin/users/{id}/activity?pageSize=50"));

        var actions = page.Items.Select(i => i.Action).ToList();
        foreach (var expected in new[]
        {
            "AdminCreateUser", "AdminUpdateUserProfile", "AdminSetUserRoles", "AdminAddDepartmentMembership",
            "AdminRemoveDepartmentMembership", "AdminDeactivateUser", "AdminActivateUser", "AdminResetUserPassword"
        })
        {
            Assert.Contains(expected, actions);
        }

        // Newest first; every one names the signed-in administrator, not a guess.
        Assert.Equal("AdminResetUserPassword", page.Items[0].Action);
        Assert.All(page.Items, i => Assert.Equal(adminId, i.PerformedByEmployeeId));
        Assert.All(page.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Label)));

        // Old -> new where it is safe, and the reason shown separately.
        var profile = page.Items.Single(i => i.Action == "AdminUpdateUserProfile");
        Assert.Contains("Email=old@example.test", profile.PreviousValue);
        Assert.Contains("Email=new@example.test", profile.NewValue);
        var deactivated = page.Items.Single(i => i.Action == "AdminDeactivateUser");
        Assert.Equal("IsActive=True", deactivated.PreviousValue);
        Assert.Equal("IsActive=False", deactivated.NewValue);
        Assert.Equal("left the company", deactivated.Reason);
        Assert.Equal("forgot it", page.Items[0].Reason);
        var roles = page.Items.Single(i => i.Action == "AdminSetUserRoles");
        Assert.Equal(Roles.CsAgent, roles.PreviousValue);
        Assert.Equal(Roles.ReportingUser, roles.NewValue);

        // Nothing credential-shaped anywhere in what the endpoint returns, or what was stored.
        var everything = string.Join('\n', page.Items.SelectMany(i => new[] { i.PreviousValue, i.NewValue, i.Reason }));
        Assert.DoesNotContain(NewPassword, everything);
        Assert.DoesNotContain("Initial-Pass-1!", everything);
        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<TigerCsDbContext>().AuditEntries
            .Where(a => a.EntityId == id.ToString()).ToListAsync();
        Assert.All(stored, a =>
        {
            Assert.DoesNotContain(NewPassword, a.BeforeValue + a.AfterValue);
            Assert.DoesNotContain("Initial-Pass-1!", a.BeforeValue + a.AfterValue);
            Assert.Equal(DateTimeKind.Utc, DateTime.SpecifyKind(a.OccurredAtUtc, DateTimeKind.Utc).Kind);
        });
    }

    [Fact]
    public async Task ActivityHistory_IsPaged_NewestFirst()
    {
        var (admin, _) = await AdminAsync();
        var (_, _, targetId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        for (var i = 0; i < 12; i++)
        {
            await ReadAsync<AdminUserDto>(await admin.PutAsJsonAsync($"/api/admin/users/{targetId}/profile", new UpdateUserProfileRequestDto($"Name {i}", null, false)));
        }

        var first = await ReadAsync<UserActivityPageDto>(await admin.GetAsync($"/api/admin/users/{targetId}/activity?page=1&pageSize=10"));
        var second = await ReadAsync<UserActivityPageDto>(await admin.GetAsync($"/api/admin/users/{targetId}/activity?page=2&pageSize=10"));

        Assert.Equal(12, first.TotalCount);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Contains("DisplayName=Name 11", first.Items[0].NewValue);
        Assert.Contains("DisplayName=Name 0", second.Items[^1].NewValue);
    }

    // --------------------------------------------------- automatic lock-out

    [Fact]
    public async Task RepeatedFailedSignIns_RecordOneAutomaticLockout_WithThePolicy_AndNoActor_ThenUnlockIsAttributedToTheAdmin()
    {
        var (admin, adminId) = await AdminAsync();
        var (name, password, targetId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var threshold = await LockoutThresholdAsync();

        // Fewer failures than the threshold: no lock-out, nothing recorded, the count is visible.
        for (var i = 0; i < threshold - 1; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(name, "wrong-" + i));
        }

        var before = await ReadAsync<UserLockoutDto>(await admin.GetAsync($"/api/admin/users/{targetId}/lockout"));
        Assert.False(before.IsLockedOut);
        Assert.Equal(threshold - 1, before.FailedAttemptCount);
        Assert.Equal(threshold, before.MaxFailedAccessAttempts);
        Assert.Empty((await ReadAsync<UserActivityPageDto>(await admin.GetAsync($"/api/admin/users/{targetId}/activity"))).Items);

        // The attempt that crosses the threshold locks the account — and keep failing afterwards.
        Assert.Equal(HttpStatusCode.Locked, await LoginAsync(name, "wrong-final"));
        Assert.Equal(HttpStatusCode.Locked, await LoginAsync(name, "wrong-again"));
        Assert.Equal(HttpStatusCode.Locked, await LoginAsync(name, password));

        using (var scope = _factory.Services.CreateScope())
        {
            var automatic = await scope.ServiceProvider.GetRequiredService<TigerCsDbContext>().AuditEntries
                .Where(a => a.EntityId == targetId.ToString() && a.Action == "AutomaticAccountLockout").ToListAsync();
            var entry = Assert.Single(automatic);   // exactly one, however many attempts followed
            Assert.Null(entry.ActorEmployeeId);     // nobody locked it
            Assert.Contains($"Threshold={threshold}", entry.AfterValue);
            Assert.Contains("LockoutMinutes=", entry.AfterValue);
            Assert.DoesNotContain("wrong-", entry.AfterValue);
        }

        var locked = await ReadAsync<UserLockoutDto>(await admin.GetAsync($"/api/admin/users/{targetId}/lockout"));
        Assert.True(locked.IsLockedOut);
        Assert.Equal("Automatic", locked.Reason);
        Assert.NotNull(locked.LockoutStartUtc);
        Assert.NotNull(locked.LockoutEndUtc);
        Assert.True(locked.LockoutEndUtc > locked.LockoutStartUtc);
        Assert.Null(locked.LastUnlockedAtUtc);

        var activity = await ReadAsync<UserActivityPageDto>(await admin.GetAsync($"/api/admin/users/{targetId}/activity"));
        var row = Assert.Single(activity.Items);
        Assert.Equal("Account locked (automatic)", row.Label);
        Assert.Null(row.PerformedByEmployeeId);
        Assert.Equal("System (automatic)", row.PerformedByName);
        Assert.Equal("Repeated failed sign-in attempts", row.Reason);

        // Unlock: audited with the administrator, and the lock-out is no longer attributed to the old cause.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/users/{targetId}/unlock", null)).StatusCode);
        var unlocked = await ReadAsync<UserLockoutDto>(await admin.GetAsync($"/api/admin/users/{targetId}/lockout"));
        Assert.False(unlocked.IsLockedOut);
        Assert.NotNull(unlocked.LastUnlockedAtUtc);
        Assert.False(string.IsNullOrEmpty(unlocked.LastUnlockedByName));
        Assert.Equal(0, unlocked.FailedAttemptCount);
        var after = await ReadAsync<UserActivityPageDto>(await admin.GetAsync($"/api/admin/users/{targetId}/activity"));
        Assert.Equal(["AdminUnlockUser", "AutomaticAccountLockout"], after.Items.Select(i => i.Action).ToArray());
        Assert.Equal(adminId, after.Items[0].PerformedByEmployeeId);

        // And a later lock-out is a new event.
        for (var i = 0; i < threshold; i++)
        {
            await LoginAsync(name, "wrong-second-" + i);
        }

        using var scope2 = _factory.Services.CreateScope();
        Assert.Equal(2, await scope2.ServiceProvider.GetRequiredService<TigerCsDbContext>().AuditEntries
            .CountAsync(a => a.EntityId == targetId.ToString() && a.Action == "AutomaticAccountLockout"));
    }

    [Fact]
    public async Task AnOldAccountWithNoAuditHistory_ShowsNotRecorded_NeverAGuessedCauseOrActor()
    {
        var (admin, _) = await AdminAsync();
        var (_, _, targetId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);

        // Locked directly in the store, as an account locked before this feature existed would be.
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await userManager.FindByIdAsync(targetId.ToString()))!;
            await userManager.SetLockoutEnabledAsync(user, true);
            await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1));
        }

        var lockout = await ReadAsync<UserLockoutDto>(await admin.GetAsync($"/api/admin/users/{targetId}/lockout"));
        Assert.True(lockout.IsLockedOut);
        Assert.Equal("Unknown", lockout.Reason);
        Assert.Null(lockout.LockoutStartUtc);
        Assert.NotNull(lockout.LockoutEndUtc);
        Assert.Null(lockout.LastUnlockedAtUtc);
        Assert.Null(lockout.LastUnlockedByName);

        var activity = await ReadAsync<UserActivityPageDto>(await admin.GetAsync($"/api/admin/users/{targetId}/activity"));
        Assert.Empty(activity.Items);
        Assert.Equal(0, activity.TotalCount);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/users/{Guid.NewGuid()}/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/users/{Guid.NewGuid()}/lockout")).StatusCode);
    }

    [Fact]
    public async Task APasswordReset_AfterAnAutomaticLockout_ClearsTheAttributedCause()
    {
        var (admin, _) = await AdminAsync();
        var (name, _, targetId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        for (var i = 0; i < await LockoutThresholdAsync(); i++)
        {
            await LoginAsync(name, "wrong-" + i);
        }

        Assert.True((await ReadAsync<UserLockoutDto>(await admin.GetAsync($"/api/admin/users/{targetId}/lockout"))).IsLockedOut);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/users/{targetId}/password", new ResetUserPasswordRequestDto(NewPassword, null))).StatusCode);

        var lockout = await ReadAsync<UserLockoutDto>(await admin.GetAsync($"/api/admin/users/{targetId}/lockout"));
        Assert.False(lockout.IsLockedOut);
        Assert.Equal("Unknown", lockout.Reason);
    }

    // ---------------------------------------------------------- authorization

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.ReportingUser)]
    public async Task ActivityAndLockout_AreRefusedToEveryNonAdministrator_AndToAnonymousCallers(string role)
    {
        var (name, password, _) = await _factory.SeedEmployeeAsync(role);
        var caller = await SignInAsync(name, password);
        var (_, _, targetId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);

        Assert.Equal(HttpStatusCode.Forbidden, (await caller.GetAsync($"/api/admin/users/{targetId}/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await caller.GetAsync($"/api/admin/users/{targetId}/lockout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/api/admin/users/{targetId}/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/api/admin/users/{targetId}/lockout")).StatusCode);
    }
}
