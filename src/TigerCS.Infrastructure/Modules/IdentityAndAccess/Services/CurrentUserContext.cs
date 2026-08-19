using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

/// <summary>Reads the acting employee's claims from the current HTTP request.</summary>
public sealed class CurrentUserContext(IHttpContextAccessor httpContextAccessor) : ICurrentUserContext
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid EmployeeId
    {
        get
        {
            var value = Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            return value is not null && Guid.TryParse(value, out var id) ? id : Guid.Empty;
        }
    }

    public IReadOnlyCollection<string> Roles =>
        Principal?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList() ?? [];

    public IReadOnlyCollection<int> DepartmentIds =>
        Principal?.FindAll(TigerCsClaimTypes.DepartmentId)
            .Select(c => int.TryParse(c.Value, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList() ?? [];

    public bool IsInRole(string role) => Principal?.IsInRole(role) ?? false;

    public bool HasDepartment(int departmentId) => DepartmentIds.Contains(departmentId);
}

public static class TigerCsClaimTypes
{
    public const string DepartmentId = "tigercs:department_id";
}
