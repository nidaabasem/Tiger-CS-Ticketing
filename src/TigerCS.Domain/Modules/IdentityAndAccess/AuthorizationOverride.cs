namespace TigerCS.Domain.Modules.IdentityAndAccess;

/// <summary>
/// The single, solution-wide definition of the platform-administration
/// authorization override.
///
/// <para>
/// <b>Confirmed management decision (2026-08-21), superseding the previous
/// exclusion.</b> Solution-Analysis.md §4.1's permission matrix originally
/// granted <see cref="Roles.SystemAdministrator"/> only "View: All
/// (technical)", "Export: All" and "Admin: Full" — every operational column
/// (Create/Edit/Assign/Transfer/Escalate/Resolve/Close) was a dash. That
/// exclusion is superseded: the System Administrator role now passes every
/// application authorization policy. See ADR-0024 and
/// Security-Architecture.md §2.1.
/// </para>
///
/// <para>
/// <b>This is an authorization override, and nothing else.</b> It answers
/// only "may this caller perform this action at all?" — the question
/// ADR-0005's policies exist to answer. It never suppresses request
/// validation, ticket status-transition rules, closed-ticket immutability,
/// optimistic-concurrency checks, database constraints, required business
/// data, or audit writes: those are enforced downstream of every
/// authorization decision and are reached, unchanged, by an overridden
/// caller. Two further carve-outs are deliberate and documented where they
/// live: session validity (<c>ActiveEmployeeRequirement</c>, an
/// <c>IIdentityGateRequirement</c> — a deactivated administrator is still
/// rejected, per Security-Architecture.md §14/FR-ADM-02) and per-record
/// verification-session ownership (MVP-ERD.md §2.24 — see ADR-0024's
/// "Business rules the override does not reach").
/// </para>
///
/// <para>
/// <b>Why a constant and a predicate rather than a role name typed at each
/// call site.</b> Requirement 4 of the correction: the override must live in
/// one central mechanism so future policies include it automatically and
/// safely. Everything that applies the override — the policy-layer
/// <c>SystemAdministratorOverrideHandler</c> and the application-layer
/// <c>AuthorizationGate</c> — reads it from here, so the set of overridden
/// roles is changed in exactly one place.
/// </para>
/// </summary>
public static class AuthorizationOverride
{
    /// <summary>The one role that overrides application authorization policies.</summary>
    public const string OverrideRole = Roles.SystemAdministrator;

    /// <summary>
    /// True when the caller holds <see cref="OverrideRole"/>. Ordinal
    /// comparison, matching how ASP.NET Core Identity persists and compares
    /// <c>AspNetRoles.Name</c> and how <c>ClaimsPrincipal.IsInRole</c>
    /// compares the resulting role claims — a case-insensitive match here
    /// would authorize a role name the identity store itself treats as
    /// different.
    /// </summary>
    public static bool AppliesTo(IEnumerable<string> callerRoles)
    {
        ArgumentNullException.ThrowIfNull(callerRoles);
        return callerRoles.Contains(OverrideRole, StringComparer.Ordinal);
    }
}
