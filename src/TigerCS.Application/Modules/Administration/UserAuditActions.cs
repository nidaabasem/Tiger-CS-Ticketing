namespace TigerCS.Application.Modules.Administration;

/// <summary>
/// The <c>AuditEntries.Action</c> values user administration writes and the
/// Activity History reads. Existing values keep their spelling — this class
/// names them, it does not rename them.
/// </summary>
public static class UserAuditActions
{
    public const string Created = "AdminCreateUser";
    public const string ProfileUpdated = "AdminUpdateUserProfile";
    public const string RolesChanged = "AdminSetUserRoles";
    public const string DepartmentAdded = "AdminAddDepartmentMembership";
    public const string DepartmentRemoved = "AdminRemoveDepartmentMembership";
    public const string Activated = "AdminActivateUser";
    public const string Deactivated = "AdminDeactivateUser";
    public const string PasswordReset = "AdminResetUserPassword";
    public const string Unlocked = "AdminUnlockUser";

    /// <summary>Written by the sign-in flow when repeated failed attempts trip Identity's lock-out. Actor is null: nobody locked the account.</summary>
    public const string AutomaticLockout = "AutomaticAccountLockout";

    /// <summary>Reserved for an administrative lock. No screen writes it yet; recognised so a future one needs no history change.</summary>
    public const string ManualLockout = "AdminLockUser";

    /// <summary>The user-account audit entries use these entity types (see the writers in AdminUserAppService and IdentityAuthenticator).</summary>
    public static readonly IReadOnlyCollection<string> EntityTypes = ["Employee", "User"];

    public static readonly IReadOnlyCollection<string> All =
    [
        Created, ProfileUpdated, RolesChanged, DepartmentAdded, DepartmentRemoved,
        Activated, Deactivated, PasswordReset, Unlocked, AutomaticLockout, ManualLockout
    ];

    public static readonly IReadOnlyCollection<string> Locks = [AutomaticLockout, ManualLockout];

    /// <summary>Actions that end a lock-out. A password reset lifts it too.</summary>
    public static readonly IReadOnlyCollection<string> LockClears = [Unlocked, PasswordReset];

    public static string Label(string action) => action switch
    {
        Created => "User created",
        ProfileUpdated => "Profile updated",
        RolesChanged => "Roles changed",
        DepartmentAdded or DepartmentRemoved => "Department membership changed",
        Activated => "User activated",
        Deactivated => "User deactivated",
        PasswordReset => "Password reset",
        Unlocked => "Account unlocked",
        AutomaticLockout => "Account locked (automatic)",
        ManualLockout => "Account locked (by an administrator)",
        _ => action
    };
}
