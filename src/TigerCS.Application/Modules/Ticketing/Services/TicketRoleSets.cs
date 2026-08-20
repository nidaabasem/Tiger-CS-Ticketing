using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Per-action role sets for ticket-operations authorization
/// (Solution-Analysis.md §4.1's permission matrix, ISSUE-022's approved
/// Resolve/Close split, Security-Architecture.md §3's CS-layer
/// cross-department scoping). Each action gets its own set rather than
/// reusing one blanket list — <see cref="TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization.DepartmentScopedRequirement"/>
/// was already built constructor-parameterized for exactly this reason, per
/// its own remarks ("flagged for confirmation before a real ticket endpoint
/// consumes this policy") — this is that endpoint, and this is the
/// resolution.
/// </summary>
public static class TicketRoleSets
{
    /// <summary>
    /// View (queue/detail): cross-department for CS-layer roles
    /// (Security-Architecture.md §3, already the exact role list
    /// PolicyNames.DepartmentScoped was defined with) plus GM/Chairman/
    /// SysAdmin, who hold "View: All tickets"/"All (technical)" in
    /// Solution-Analysis.md §4.1. Department Employee/Head are scoped to
    /// their own department membership(s), not included here.
    /// </summary>
    public static readonly IReadOnlyCollection<string> CrossDepartmentView =
    [
        Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager,
        Roles.GeneralManager, Roles.ChairmanCeo, Roles.SystemAdministrator
    ];

    /// <summary>
    /// Assign-someone-else / Transfer: cross-department for Supervisor+
    /// (Solution-Analysis.md §4.1's A/T columns). Department Head is
    /// department-scoped instead (checked separately, against the specific
    /// department), not cross-department.
    /// </summary>
    public static readonly IReadOnlyCollection<string> CrossDepartmentSupervisory =
    [
        Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo, Roles.SystemAdministrator
    ];

    /// <summary>Resolve: Department Employee/Head only (ISSUE-022, Management-Decisions.md — ticket task's explicit instruction). Never CS-layer, never cross-department — the resolving actor must also be department-scoped to the ticket's current department.</summary>
    public static readonly IReadOnlyCollection<string> Resolve = [Roles.DepartmentEmployee, Roles.DepartmentHead];

    /// <summary>Close: CS Agent/CS Supervisor/CS Manager only (ISSUE-022, Management-Decisions.md — task's explicit instruction). Cross-department — CS is inherently cross-department (Security-Architecture.md §3).</summary>
    public static readonly IReadOnlyCollection<string> Close = [Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager];
}
