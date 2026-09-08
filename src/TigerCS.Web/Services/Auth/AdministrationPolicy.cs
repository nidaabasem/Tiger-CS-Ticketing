using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Web.Services.Auth;

/// <summary>The Web-side gate on the /Admin folder — the same fixed role the Api's SystemAdministrator policy requires. Display-only; the Api is the enforcement point.</summary>
public static class AdministrationPolicy
{
    public const string Name = "SystemAdministrator";

    public const string RequiredRole = Roles.SystemAdministrator;

    public static bool AppliesTo(CurrentUser? user) => user is not null && user.Roles.Contains(RequiredRole, StringComparer.Ordinal);
}
