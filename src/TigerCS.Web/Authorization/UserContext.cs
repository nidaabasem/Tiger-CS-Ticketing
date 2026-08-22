using Domain_Roles = TigerCS.Domain.Modules.IdentityAndAccess.Roles;

namespace TigerCS.Web.Authorization;

/// <summary>
/// The signed-in user as the presentation layer needs them: identity, roles,
/// and department memberships.
///
/// <para>
/// Every value here comes from the user's own authentication cookie, which was
/// populated from <c>POST /api/auth/login</c> and <c>GET /api/users/me</c> at
/// sign-in. None of it is ever read from a form field, a query string or a
/// route value, so a user cannot present themselves as holding a role or a
/// department they do not hold - and even if the cookie were forged, which
/// Data Protection's signature prevents, the API would still refuse the call.
/// </para>
/// </summary>
public sealed class UserContext
{
    public UserContext(
        Guid? employeeId,
        string displayName,
        IReadOnlyCollection<string> roles,
        IReadOnlyDictionary<int, string> departments)
    {
        EmployeeId = employeeId;
        DisplayName = displayName;
        Roles = roles;
        Departments = departments;
    }

    public static UserContext Anonymous { get; } =
        new(null, string.Empty, [], new Dictionary<int, string>());

    public Guid? EmployeeId { get; }

    public string DisplayName { get; }

    public IReadOnlyCollection<string> Roles { get; }

    /// <summary>Department id to display name, for the departments this user belongs to.</summary>
    public IReadOnlyDictionary<int, string> Departments { get; }

    public bool IsAuthenticated => EmployeeId.HasValue;

    public bool IsSystemAdministrator => HasRole(Domain_Roles.SystemAdministrator);

    /// <summary>
    /// The single role to show beside the user's name in the command bar. A
    /// user can hold several; the most operationally significant one is shown,
    /// with the rest available as a title attribute rather than crowding the bar.
    /// </summary>
    public string PrimaryRole
    {
        get
        {
            string[] byPrecedence =
            [
                Domain_Roles.ChairmanCeo,
                Domain_Roles.GeneralManager,
                Domain_Roles.CsManager,
                Domain_Roles.DepartmentHead,
                Domain_Roles.CsSupervisor,
                Domain_Roles.SystemAdministrator,
                Domain_Roles.CsAgent,
                Domain_Roles.DepartmentEmployee,
                Domain_Roles.ReportingUser
            ];

            return byPrecedence.FirstOrDefault(HasRole) ?? Roles.FirstOrDefault() ?? "No role";
        }
    }

    public bool HasRole(string roleName) => Roles.Contains(roleName, StringComparer.Ordinal);

    public bool HasAnyRole(params string[] roleNames) => roleNames.Any(HasRole);

    public bool BelongsToDepartment(int departmentId) => Departments.ContainsKey(departmentId);

    /// <summary>
    /// A department's name when this user belongs to it.
    ///
    /// <para>
    /// For any other department this returns <c>null</c>, and the caller shows
    /// the numeric id. That is a real, disclosed limitation rather than a
    /// styling choice: the API exposes no endpoint that lists departments or
    /// resolves one by id, and <c>GET /api/users/me</c> is the only source of a
    /// department name anywhere in the contract. Inventing a name table here
    /// would be fabricating reference data.
    /// </para>
    /// </summary>
    public string? DepartmentName(int departmentId) =>
        Departments.TryGetValue(departmentId, out var name) ? name : null;

    /// <summary>The department name if known, otherwise a plain, honest "Department {id}".</summary>
    public string DepartmentLabel(int departmentId) =>
        DepartmentName(departmentId) ?? $"Department {departmentId}";
}
