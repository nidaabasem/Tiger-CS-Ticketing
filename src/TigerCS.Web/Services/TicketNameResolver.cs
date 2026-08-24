using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Services;

/// <summary>
/// Best-effort id → name resolution for the fields TigerCS.Api returns as
/// raw ids with no dedicated lookup endpoint (see the PR description's
/// "Contract gaps" section). Every resolution here is backed by a real,
/// contract-guaranteed fact — never a guess:
///
/// <list type="bullet">
/// <item>Department names are known only for the signed-in caller's own
/// memberships (<c>GET /api/users/me</c> is the only endpoint that ever
/// returns a department name). Any other department id is shown as-is.</item>
/// <item>An assigned owner is guaranteed by the Assign endpoint's own
/// business rule to be an active member of the ticket's current department
/// (<c>EmployeeNotInDepartment</c> outcome), so paging that department's
/// member list (<c>GET /api/departments/{id}/users</c>) to match the id is a
/// correct resolution, not a guess. Bounded to 100 members; beyond that the
/// id is shown as-is rather than paging indefinitely.</item>
/// <item>A note's author is resolved only when it is the signed-in caller
/// themself (a real, already-known fact) — there is no membership guarantee
/// for note authorship, so any other author id is shown as-is.</item>
/// </list>
///
/// Category names have no lookup endpoint at all (no CategoriesController
/// exists) and are always shown as-is by callers of this service.
/// Registered scoped, so its per-request caches live for one page render.
/// </summary>
public sealed class TicketNameResolver(UsersApiClient usersApiClient)
{
    private readonly Dictionary<int, string> _departmentNames = new();
    private readonly Dictionary<(int DepartmentId, Guid EmployeeId), string?> _employeeNames = new();
    private bool _ownDepartmentsLoaded;

    /// <summary>The caller's own department memberships — the only set of department id→name pairs the Api exposes.</summary>
    public IReadOnlyCollection<DepartmentMembershipDto> OwnDepartments { get; private set; } = [];

    public async Task PrimeOwnDepartmentsAsync(CancellationToken cancellationToken)
    {
        if (_ownDepartmentsLoaded)
        {
            return;
        }

        _ownDepartmentsLoaded = true;

        var me = await usersApiClient.GetMeAsync(cancellationToken);
        if (!me.IsSuccess || me.Value is null)
        {
            return;
        }

        OwnDepartments = me.Value.Departments;
        foreach (var department in me.Value.Departments)
        {
            _departmentNames[department.DepartmentId] = department.Name;
        }
    }

    /// <summary>The department's name if known (the caller's own department), otherwise null.</summary>
    public string? TryGetDepartmentName(int departmentId) =>
        _departmentNames.GetValueOrDefault(departmentId);

    /// <summary>
    /// The owner's display name if it could be resolved by paging the
    /// ticket's own department (guaranteed membership), otherwise null.
    /// </summary>
    public async Task<string?> ResolveOwnerNameAsync(int currentDepartmentId, Guid ownerEmployeeId, CancellationToken cancellationToken)
    {
        var key = (currentDepartmentId, ownerEmployeeId);
        if (_employeeNames.TryGetValue(key, out var cached))
        {
            return cached;
        }

        const int pageSize = 100;
        for (var page = 1; page <= 2; page++)
        {
            var result = await usersApiClient.GetDepartmentUsersAsync(currentDepartmentId, page, pageSize, cancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                break;
            }

            var match = result.Value.Items.FirstOrDefault(u => u.EmployeeId == ownerEmployeeId);
            if (match is not null)
            {
                _employeeNames[key] = match.DisplayName;
                return match.DisplayName;
            }

            if (result.Value.Items.Count < pageSize)
            {
                break;
            }
        }

        _employeeNames[key] = null;
        return null;
    }

    /// <summary>The note author's display name if it is the signed-in caller themself, otherwise null.</summary>
    public static string? ResolveSelfAuthorName(Guid authorEmployeeId, CurrentUser? currentUser) =>
        currentUser is not null && currentUser.EmployeeId == authorEmployeeId ? currentUser.DisplayName : null;
}
