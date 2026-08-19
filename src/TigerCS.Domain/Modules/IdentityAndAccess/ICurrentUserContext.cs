namespace TigerCS.Domain.Modules.IdentityAndAccess;

/// <summary>
/// Reads the acting employee's identity/claims for the current request
/// (Module-Design.md's "Identity and Access" public interface). Implemented
/// in Infrastructure against the framework's ClaimsPrincipal.
/// </summary>
public interface ICurrentUserContext
{
    bool IsAuthenticated { get; }

    Guid EmployeeId { get; }

    IReadOnlyCollection<string> Roles { get; }

    IReadOnlyCollection<int> DepartmentIds { get; }

    bool IsInRole(string role);

    bool HasDepartment(int departmentId);
}
