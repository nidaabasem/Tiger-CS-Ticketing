using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Authorization;

/// <summary>
/// The single point through which every <b>application-layer</b>
/// authorization decision in this solution is evaluated, and the one place
/// the <see cref="AuthorizationOverride"/> is applied to them (ADR-0024 — a
/// confirmed management decision superseding Solution-Analysis.md §4.1's
/// previous exclusion of System Administrator from operational actions).
///
/// <para>
/// <b>Why a second mechanism exists at all.</b> Authorization in Tiger CS
/// Ticketing is enforced at two levels, by design: the ASP.NET Core policy
/// catalog (ADR-0005) decides whether a caller may reach an endpoint, and
/// the application services decide the resource-scoped part the policy
/// cannot see — whether this caller may act on <i>this</i> ticket, given its
/// current department and owner (Security-Architecture.md §3: "evaluated
/// against the ticket's current data, not cached or assumed from the user's
/// session alone"). <c>SystemAdministratorOverrideHandler</c> covers the
/// first level for every present and future policy; this gate covers the
/// second for every present and future application service. Together they
/// are the "one central authorization mechanism" of the correction's
/// requirement 4 — the override role itself is defined once, in
/// <see cref="AuthorizationOverride"/>, and neither level names it inline.
/// </para>
///
/// <para>
/// <b>Usage rule.</b> An application service must not branch on the override
/// itself. It passes its own resource-scoped rule to
/// <see cref="Evaluate"/>/<see cref="EvaluateAsync"/> and uses the answer;
/// the override is applied here, before the rule is consulted (and, for the
/// async overload, without running it — an overridden caller costs no
/// department-membership round trip). A new application service written this
/// way includes the override automatically, which is exactly the property
/// requirement 4 asks for.
/// </para>
///
/// <para>
/// <b>What this gate is not.</b> It decides permission only. Everything a
/// service checks that is not a permission — request validation, ticket
/// status-transition rules, closed-ticket immutability, optimistic
/// concurrency, required business data, audit writes — sits outside the gate
/// and is reached unchanged by an overridden caller. Per-record
/// verification-session ownership (MVP-ERD.md §2.24) is deliberately outside
/// it too; see ADR-0024's "Business rules the override does not reach".
/// </para>
/// </summary>
public static class AuthorizationGate
{
    /// <summary>
    /// Evaluates a synchronous resource-scoped authorization rule under the
    /// override.
    /// </summary>
    /// <param name="callerRoles">The acting employee's roles, as read from the request's validated claims.</param>
    /// <param name="rule">The service's own rule. Not invoked when the override applies.</param>
    public static bool Evaluate(IReadOnlyCollection<string> callerRoles, Func<bool> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return AuthorizationOverride.AppliesTo(callerRoles) || rule();
    }

    /// <summary>
    /// Evaluates an asynchronous resource-scoped authorization rule — one
    /// that needs to read the ticket's department membership or similar —
    /// under the override.
    /// </summary>
    /// <param name="callerRoles">The acting employee's roles, as read from the request's validated claims.</param>
    /// <param name="rule">The service's own rule. Not invoked when the override applies.</param>
    public static async Task<bool> EvaluateAsync(IReadOnlyCollection<string> callerRoles, Func<Task<bool>> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return AuthorizationOverride.AppliesTo(callerRoles) || await rule();
    }
}
