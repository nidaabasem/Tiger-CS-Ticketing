using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>Whether a request-type SLA row governs a ticket's due dates.</summary>
public enum RequestTypeSlaStatus
{
    /// <summary>No active request-type row exists for this (request type, priority) — the per-priority policy applies, and there is nothing to explain.</summary>
    NoPolicy = 0,

    /// <summary>The row is unambiguous and its durations replace the per-priority ones.</summary>
    Applied = 1,

    /// <summary>A row exists but cannot be enforced yet; the per-priority policy applies and <see cref="RequestTypeSlaEvaluation.Reason"/> says exactly why.</summary>
    NotApplied = 2
}

/// <summary>
/// The outcome of evaluating one <see cref="RequestTypeSlaPolicy"/> row.
/// </summary>
/// <param name="Status">Applied, NotApplied (with a reason) or NoPolicy.</param>
/// <param name="Reason">When <see cref="RequestTypeSlaStatus.NotApplied"/>: the exact, user-facing reason. Null otherwise.</param>
/// <param name="PolicyId">The row evaluated; null for <see cref="RequestTypeSlaStatus.NoPolicy"/>.</param>
/// <param name="ClockBasis">The row's explicit clock basis when applied.</param>
/// <param name="FirstResponse">Applied First Response duration, or null (priority policy supplies it). Never set from a range.</param>
/// <param name="Resolution">Applied Resolution duration; <see cref="TimeSpan.Zero"/> when <paramref name="ResolutionIsImmediate"/>.</param>
/// <param name="ResolutionIsImmediate">The row is the document's "Immediately": resolution due = clock start.</param>
/// <param name="PausesOnPendingCustomerOverride">The row's explicit pause flag (true/false), or null = inherit the global rule. Reported for any active row, applied or not — see <see cref="RequestTypeSlaEnforcement"/>.</param>
public sealed record RequestTypeSlaEvaluation(
    RequestTypeSlaStatus Status,
    string? Reason,
    int? PolicyId,
    SlaClockBasis? ClockBasis,
    TimeSpan? FirstResponse,
    TimeSpan? Resolution,
    bool ResolutionIsImmediate,
    bool? PausesOnPendingCustomerOverride)
{
    public bool IsApplied => Status == RequestTypeSlaStatus.Applied;

    public static RequestTypeSlaEvaluation None { get; } =
        new(RequestTypeSlaStatus.NoPolicy, null, null, null, null, null, false, null);
}

/// <summary>
/// Decides, as a pure function, which request-type SLA rows are enforced.
///
/// <para>
/// <b>Only the unambiguous subset is enforced</b>; everything else falls back
/// to the per-priority policy and says why (never a silent guess). A row is
/// enforced only when ALL of these hold:
/// </para>
/// <list type="number">
///   <item><description>it is active;</description></item>
///   <item><description><see cref="RequestTypeSlaPolicy.Trigger"/> is <see cref="SlaTriggerType.TicketCreated"/> (the other triggers need event plumbing that does not exist);</description></item>
///   <item><description><see cref="RequestTypeSlaPolicy.ClockBasis"/> is explicitly set (null = business vs. calendar days, weekend and holiday treatment all undecided);</description></item>
///   <item><description>every duration it uses is a single value — Target equal to Maximum, or only one of them set. A range (Target &lt; Maximum) is NOT enforced: which bound is "target" and which is "breach" is undecided;</description></item>
///   <item><description>for a business-hours basis, an active calendar exists to count against.</description></item>
/// </list>
///
/// <para>
/// <b>Units.</b> Minutes and Hours are what they say, counted on the row's
/// clock (business minutes under BusinessHours, calendar minutes under
/// TwentyFourSeven). Days under TwentyFourSeven is 24 h x n. Days under
/// BusinessHours is n x the calendar's business-day length of business time —
/// the same convention the per-priority policy uses for "3 business days"
/// (<c>SlaReferenceData.BusinessMinutesPerDay</c>), walked by
/// <see cref="SlaDueDateCalculator"/> so non-working days and holidays are
/// skipped.
/// </para>
///
/// <para>
/// <b>Immediate.</b> <c>IsImmediate</c> ("Immediately") yields a resolution
/// due equal to the clock start. It is documented, deterministic, and means
/// the deadline is already due: the next check flags it unless the ticket is
/// resolved at that instant.
/// </para>
///
/// <para>
/// <b>Pause flag.</b> <see cref="RequestTypeSlaPolicy.PausesOnPendingCustomer"/>
/// <c>true</c>/<c>false</c> is explicit configuration independent of how the
/// durations are interpreted, so it is reported for every active row, even one
/// whose durations are not enforced. <c>null</c> inherits the approved global
/// rule. <c>PausesOnPendingInternal</c> is never read (undecided).
/// </para>
/// </summary>
public static class RequestTypeSlaEnforcement
{
    /// <param name="policy">The (request type, priority) row, or null.</param>
    /// <param name="businessDayLength">The active calendar's working-day length, or null when no calendar is configured.</param>
    public static RequestTypeSlaEvaluation Evaluate(RequestTypeSlaPolicy? policy, TimeSpan? businessDayLength)
    {
        if (policy is not { IsActive: true })
        {
            return RequestTypeSlaEvaluation.None;
        }

        RequestTypeSlaEvaluation NotApplied(string detail) => new(
            RequestTypeSlaStatus.NotApplied,
            "Request-type SLA not applied: " + detail + " The per-priority policy is used instead.",
            policy.RequestTypeSlaPolicyId, null, null, null, false, policy.PausesOnPendingCustomer);

        if (policy.Trigger != SlaTriggerType.TicketCreated)
        {
            return NotApplied(
                $"this SLA starts on '{policy.Trigger}', not at ticket creation, and non-creation triggers are not supported yet (undecided).");
        }

        if (policy.ClockBasis is not { } basis)
        {
            return NotApplied(
                "business days vs. calendar days (and weekend and holiday treatment) is undecided, so no clock basis is configured.");
        }

        if (basis == SlaClockBasis.BusinessHours && businessDayLength is null)
        {
            return NotApplied("the row uses a business-hours clock but no active business calendar is configured.");
        }

        // First Response: null in the seeded data (priority policy stays). A
        // present single value is honoured; a present range is not.
        TimeSpan? firstResponse = null;
        if (HasValue(policy.FirstResponseTargetValue, policy.FirstResponseMaximumValue))
        {
            if (!TrySingle(policy.FirstResponseTargetValue, policy.FirstResponseMaximumValue, out var frValue))
            {
                return NotApplied(RangeDetail("First Response", policy.FirstResponseTargetValue, policy.FirstResponseMaximumValue, policy.Unit));
            }

            firstResponse = ToDuration(frValue, policy.Unit, basis, businessDayLength);
        }

        TimeSpan resolution;
        if (policy.IsImmediate)
        {
            resolution = TimeSpan.Zero;
        }
        else if (HasValue(policy.ResolutionTargetValue, policy.ResolutionMaximumValue))
        {
            if (!TrySingle(policy.ResolutionTargetValue, policy.ResolutionMaximumValue, out var resValue))
            {
                return NotApplied(RangeDetail("Resolution", policy.ResolutionTargetValue, policy.ResolutionMaximumValue, policy.Unit));
            }

            resolution = ToDuration(resValue, policy.Unit, basis, businessDayLength);
        }
        else
        {
            return NotApplied("no resolution duration is configured.");
        }

        return new RequestTypeSlaEvaluation(
            RequestTypeSlaStatus.Applied, null, policy.RequestTypeSlaPolicyId, basis, firstResponse, resolution,
            policy.IsImmediate, policy.PausesOnPendingCustomer);
    }

    private static bool HasValue(int? target, int? maximum) => target is not null || maximum is not null;

    /// <summary>A single value: only one bound set, or both set and equal. Target &lt; Maximum is a range.</summary>
    private static bool TrySingle(int? target, int? maximum, out int value)
    {
        if (target is { } t && maximum is { } m)
        {
            value = t;
            return t == m;
        }

        value = (target ?? maximum)!.Value;
        return true;
    }

    private static string RangeDetail(string deadline, int? target, int? maximum, SlaDurationUnit unit) =>
        $"{deadline} is a range ({target}–{maximum} {unit}) and the range interpretation (which bound is the target and which the breach) is undecided.";

    private static TimeSpan ToDuration(int value, SlaDurationUnit unit, SlaClockBasis basis, TimeSpan? businessDayLength) => unit switch
    {
        SlaDurationUnit.Minutes => TimeSpan.FromMinutes(value),
        SlaDurationUnit.Hours => TimeSpan.FromHours(value),
        SlaDurationUnit.Days => basis == SlaClockBasis.BusinessHours
            ? businessDayLength!.Value * value
            : TimeSpan.FromHours(24d * value),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown SLA duration unit.")
    };
}
