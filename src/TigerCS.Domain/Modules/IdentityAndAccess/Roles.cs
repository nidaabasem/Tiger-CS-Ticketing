namespace TigerCS.Domain.Modules.IdentityAndAccess;

/// <summary>
/// The fixed MVP role set (Solution-Analysis.md §4). Role names below use the
/// exact display strings approved for this pilot's Identity module: "CS Agent"
/// and "CS Supervisor" (rather than the design documents' "Geyness Agent" and
/// "Supervisor") per an explicit naming decision made when this increment was
/// scoped — recorded formally in ADR-0004's "Pilot Role-Naming Decision"
/// section, not only here. All other names match the documents verbatim.
/// </summary>
public static class Roles
{
    public const string CsAgent = "CS Agent";
    public const string CsSupervisor = "CS Supervisor";
    public const string DepartmentEmployee = "Department Employee";
    public const string DepartmentHead = "Department Head";
    public const string CsManager = "CS Manager";
    public const string GeneralManager = "General Manager";
    public const string ChairmanCeo = "Chairman/CEO";
    public const string SystemAdministrator = "System Administrator";
    public const string ReportingUser = "Reporting User";

    public static readonly IReadOnlyList<string> All =
    [
        CsAgent,
        CsSupervisor,
        DepartmentEmployee,
        DepartmentHead,
        CsManager,
        GeneralManager,
        ChairmanCeo,
        SystemAdministrator,
        ReportingUser
    ];

    /// <summary>
    /// Roles that are read-only by agreed management decision: Chairman/CEO
    /// (views all tickets, dashboard and executive reports; receives Level 4
    /// escalations but initiates nothing) and Reporting User ("no ticket
    /// actions"). A caller holding <i>only</i> these roles may never mutate
    /// anything; a caller who additionally holds a working role keeps that
    /// role's authority (a user may hold several roles).
    /// </summary>
    public static readonly IReadOnlyCollection<string> ReadOnly = [ChairmanCeo, ReportingUser];

    /// <summary>True when the caller holds at least one role and every role it holds is read-only.</summary>
    public static bool IsReadOnlyCaller(IReadOnlyCollection<string> callerRoles)
    {
        ArgumentNullException.ThrowIfNull(callerRoles);
        return callerRoles.Count > 0 && callerRoles.All(ReadOnly.Contains);
    }

    /// <summary>High-level descriptions for GET /api/roles (MVP-API-Contracts.md §1.5).</summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [CsAgent] = "Front-line call handling: verifies callers, creates and works own-department/own tickets, flags Level 1 escalation.",
        [CsSupervisor] = "Oversees a CS team queue; can assign within team, close/reopen tickets, view team reports.",
        [DepartmentEmployee] = "Works and resolves tickets assigned to their department; cannot close a ticket (resolve only).",
        [DepartmentHead] = "Manages all of a department's tickets; can assign/transfer and approve escalations for that department.",
        [CsManager] = "Cross-department oversight of all tickets; can close/reopen tickets and manage user/role assignment.",
        [GeneralManager] = "Receives Level 3 escalations and initiates Level 4; views all reports/dashboards.",
        [ChairmanCeo] = "Receives Level 4 escalations only; read-only access to all tickets and executive reports.",
        [SystemAdministrator] = "Full technical administration: user activation/deactivation, role assignment, system configuration.",
        [ReportingUser] = "Read-only access to reports and dashboards; no ticket actions."
    };
}
