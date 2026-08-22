using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TigerCS.Web.Infrastructure;

namespace TigerCS.Web.Services;

/// <summary>
/// Turns employee ids into display names.
///
/// <para>
/// <b>Why this class has to exist.</b> Tickets carry
/// <c>CurrentOwnerEmployeeId</c> as a bare <c>Guid</c>, and the only endpoint
/// in the API that maps an employee id to a name is
/// <c>GET /api/departments/{id}/users</c>. So a name is resolved by fetching
/// the roster of the department the ticket currently sits in. For a queue page
/// that means one roster fetch per <i>distinct department on that page</i> -
/// not one per row - and the rosters are cached briefly, since staff names
/// change far more slowly than tickets do.
/// </para>
///
/// <para>
/// The cache is keyed by department and is a cache of <b>public directory
/// data</b> - names and roles of staff in a department, which any
/// authenticated staff member may read from §1.4 without further restriction.
/// It holds no ticket data and no per-caller authorization decision, so a
/// shared cache cannot leak one caller's visibility to another. A caller who
/// may not read a department's roster simply gets no entry: the failed fetch
/// is not cached as an empty roster, so a later authorized caller still
/// resolves names.
/// </para>
/// </summary>
public sealed class DirectoryService(
    UsersApiClient usersApiClient,
    IMemoryCache cache,
    IOptions<ApiClientOptions> options)
{
    private const int RosterPageSize = 100;

    private readonly ApiClientOptions _options = options.Value;

    /// <summary>
    /// Display names for every employee in the given departments, keyed by
    /// employee id. Departments that cannot be read contribute nothing rather
    /// than failing the whole lookup - a missing name degrades one cell, and a
    /// queue that renders with an unresolved owner is better than one that
    /// does not render.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetEmployeeNamesAsync(
        IEnumerable<int> departmentIds, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();

        foreach (var departmentId in departmentIds.Distinct())
        {
            foreach (var member in await GetRosterAsync(departmentId, cancellationToken))
            {
                names[member.EmployeeId] = member.DisplayName;
            }
        }

        return names;
    }

    /// <summary>
    /// One department's active members - the eligible set for assignment,
    /// since the API refuses an assignee who is not an active member of the
    /// ticket's current department.
    /// </summary>
    public async Task<IReadOnlyList<DirectoryMember>> GetAssignableMembersAsync(
        int departmentId, CancellationToken cancellationToken) =>
        await GetRosterAsync(departmentId, cancellationToken);

    private async Task<IReadOnlyList<DirectoryMember>> GetRosterAsync(
        int departmentId, CancellationToken cancellationToken)
    {
        var cacheKey = $"tigercs:roster:{departmentId}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<DirectoryMember>? cached) && cached is not null)
        {
            return cached;
        }

        var result = await usersApiClient.GetDepartmentUsersAsync(
            departmentId, page: 1, pageSize: RosterPageSize, cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            // Deliberately not cached. A 403/404/502 here is about this caller
            // or this moment, not about the department, and caching it would
            // deny names to a later caller who is entitled to them.
            return [];
        }

        var roster = result.Value.Items
            .Select(u => new DirectoryMember(u.EmployeeId, u.DisplayName, u.Roles))
            .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        cache.Set(cacheKey, (IReadOnlyList<DirectoryMember>)roster, _options.DirectoryCacheDuration);
        return roster;
    }
}

/// <summary>One staff member as the directory knows them.</summary>
public sealed record DirectoryMember(Guid EmployeeId, string DisplayName, IReadOnlyCollection<string> Roles);
