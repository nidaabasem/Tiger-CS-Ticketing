namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

/// <summary>
/// The policy catalog requested for this increment. Role-set membership for
/// the tiered policies below is the literal, minimal reading of
/// Solution-Analysis.md §4's permission matrix and is not otherwise
/// exhaustively specified in the approved documents beyond that matrix —
/// flagged here, and in the PR description, as an assumption to confirm
/// before ticket-level endpoints (which will consume these same policies)
/// are built in the next increment.
/// </summary>
public static class PolicyNames
{
    /// <summary>Any authenticated, active staff member — no role restriction.</summary>
    public const string AuthenticatedStaff = "AuthenticatedStaff";

    /// <summary>Resource-scoped: caller must be assigned to the target department, or hold a cross-department role.</summary>
    public const string DepartmentScoped = "DepartmentScoped";

    /// <summary>CS Supervisor, CS Manager, General Manager, or Chairman/CEO.</summary>
    public const string SupervisorOrAbove = "SupervisorOrAbove";

    /// <summary>Department Head, CS Manager, General Manager, or Chairman/CEO.</summary>
    public const string DepartmentHeadOrAbove = "DepartmentHeadOrAbove";

    /// <summary>CS Manager or General Manager (Chairman/CEO included as the top organizational tier).</summary>
    public const string CsManagerOrGeneralManager = "CsManagerOrGeneralManager";

    /// <summary>System Administrator only.</summary>
    public const string SystemAdministrator = "SystemAdministrator";
}
