using System.Security.Claims;

namespace TigerCS.Web.Services.Auth;

/// <summary>Typed access to the signed-in user's own claims — read-only, never a source of authorization decisions (the Api enforces those).</summary>
public sealed record CurrentUser(Guid EmployeeId, string DisplayName, IReadOnlyCollection<string> Roles, int? PrimaryDepartmentId)
{
    public static CurrentUser? FromPrincipal(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var idValue = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is null || !Guid.TryParse(idValue, out var employeeId))
        {
            return null;
        }

        var displayName = principal.FindFirstValue(ClaimTypes.Name) ?? "Unknown";
        var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
        var departmentIdValue = principal.FindFirstValue(TigerCsClaimTypes.PrimaryDepartmentId);
        var departmentId = int.TryParse(departmentIdValue, out var deptId) ? deptId : (int?)null;

        return new CurrentUser(employeeId, displayName, roles, departmentId);
    }

    public string Initials
    {
        get
        {
            var parts = DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
                _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
            };
        }
    }
}
