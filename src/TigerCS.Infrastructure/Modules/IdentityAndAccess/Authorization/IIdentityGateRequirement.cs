using Microsoft.AspNetCore.Authorization;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

/// <summary>
/// Marks a requirement that establishes <i>who the caller is</i> — whether
/// this request carries a valid, still-live session at all — rather than
/// <i>what the caller may do</i>.
///
/// <para>
/// <see cref="SystemAdministratorOverrideHandler"/> satisfies every pending
/// requirement <b>except</b> these. That distinction is what makes the
/// override safe to apply automatically to policies that do not exist yet
/// (ADR-0024, requirement 4): a future permission requirement needs no
/// change to be covered, while a future identity gate opts out by
/// implementing this interface and keeps its teeth against every role,
/// System Administrator included.
/// </para>
///
/// <para>
/// Only <see cref="ActiveEmployeeRequirement"/> implements this today.
/// Bypassing it would let a deactivated administrator keep full access until
/// their token expired, contradicting Security-Architecture.md §14 ("a
/// deactivated Employee cannot obtain a new session even if their prior
/// token has not yet expired — deactivation is checked on every request")
/// and FR-ADM-02's 24-hour revocation requirement. The identity store
/// already guarantees the override role never disappears entirely:
/// <c>UserActivationAppService</c> refuses to deactivate the last active
/// System Administrator.
/// </para>
/// </summary>
public interface IIdentityGateRequirement : IAuthorizationRequirement;
