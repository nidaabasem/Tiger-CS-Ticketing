using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Web.Services.Auth;

/// <summary>
/// The Web-side gate on the /Reports folder and the "Team Performance" nav
/// item — the same role set the Api's <c>CsManagerOrGeneralManager</c>
/// policy requires (CS Manager, General Manager, Chairman/CEO), plus
/// System Administrator, who passes every Api policy through the ADR-0024
/// override. Display-only; the Api is the enforcement point, and a page
/// that reaches it without the role shows the Api's 403 as a
/// "no permission" state rather than an empty table.
/// </summary>
public static class ReportsPolicy
{
    public const string Name = "Reports";

    public static readonly string[] AllowedRoles =
    [
        Roles.CsManager,
        Roles.GeneralManager,
        Roles.ChairmanCeo,
        Roles.SystemAdministrator
    ];

    public static bool AppliesTo(CurrentUser? user) =>
        user is not null && user.Roles.Any(r => AllowedRoles.Contains(r, StringComparer.Ordinal));
}
