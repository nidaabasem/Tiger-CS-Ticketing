using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

/// <summary>
/// The central policy-layer half of the System Administrator authorization
/// override (<see cref="AuthorizationOverride"/>, ADR-0024 — a confirmed
/// management decision superseding Solution-Analysis.md §4.1's previous
/// exclusion).
///
/// <para>
/// <b>Why a bare <see cref="IAuthorizationHandler"/> rather than a handler
/// for one requirement type.</b> ASP.NET Core invokes every registered
/// <see cref="IAuthorizationHandler"/> once per authorization evaluation,
/// handing it the whole <see cref="AuthorizationHandlerContext"/> — not one
/// requirement at a time, as the <c>AuthorizationHandler&lt;TRequirement&gt;</c>
/// base class does. Succeeding the context's pending requirements from here
/// therefore satisfies <i>every</i> policy in the catalog, including
/// requirement types that do not exist yet, with no per-policy or
/// per-controller registration. That is requirement 4 of the correction:
/// future SLA, escalation, reporting and administration policies pick the
/// override up automatically, rather than each having to remember to list
/// the role.
/// </para>
///
/// <para>
/// <b>Two categories are never overridden</b>, so "authorized for
/// everything" cannot decay into "authenticated as nobody":
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="IIdentityGateRequirement"/> — session validity, currently
///     <see cref="ActiveEmployeeRequirement"/>. A deactivated administrator
///     is still refused (Security-Architecture.md §14, FR-ADM-02).
///   </description></item>
///   <item><description>
///     <see cref="DenyAnonymousAuthorizationRequirement"/> — the framework's
///     own authenticated-user gate. Unreachable in practice (an anonymous
///     principal holds no role claim, so this handler returns before the
///     loop), but skipped explicitly rather than left to that coincidence.
///   </description></item>
/// </list>
///
/// <para>
/// Resource-based requirements are covered too:
/// <c>DepartmentScopedRequirement</c> is evaluated through the same context,
/// so an overridden caller passes it for any department without the
/// requirement's own cross-department role list having to name the role.
/// </para>
/// </summary>
public sealed class SystemAdministratorOverrideHandler : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // An unauthenticated request never carries the override role, but
        // check identity first regardless: IsInRole is the only thing
        // standing between "no principal" and "succeed everything".
        if (context.User.Identity?.IsAuthenticated is not true ||
            !context.User.IsInRole(AuthorizationOverride.OverrideRole))
        {
            return Task.CompletedTask;
        }

        // Materialized before iterating: Succeed() mutates the pending set
        // that PendingRequirements enumerates lazily.
        foreach (var requirement in context.PendingRequirements.ToList())
        {
            if (requirement is IIdentityGateRequirement or DenyAnonymousAuthorizationRequirement)
            {
                continue;
            }

            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
