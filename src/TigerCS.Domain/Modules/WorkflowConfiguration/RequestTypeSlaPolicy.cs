using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The configurable SLA for one (Request Type, Priority) pair — the layer the
/// Customer Service SLA document's per-request-type durations live in.
/// Complements, and does not replace, the existing per-priority
/// <see cref="SlaPolicy"/>: how the two combine (this layer overriding the
/// priority default where a row exists, or another precedence rule) is a
/// phase-4 calculation decision, not silently decided here.
///
/// <para>
/// <b>Ranges are stored as the source gives them.</b> "10–12 Days" is
/// <see cref="ResolutionTargetValue"/> 10 / <see cref="ResolutionMaximumValue"/>
/// 12 in <see cref="SlaDurationUnit.Days"/> — deliberately NOT collapsed to
/// one number. Whether the lower bound is officially "target" and the upper
/// officially "breach" is a documented-as-pending business interpretation
/// (docs/Workflow-SLA-Configuration-Phase1.md); the property names describe
/// the stored bounds, not an approved breach rule.
/// </para>
///
/// <para>
/// <b>The pause flags are three-valued on purpose.</b> The SLA document
/// confirms customer-caused delays may be placed on Pending, but whether
/// Pending pauses the SLA clock is awaiting business approval — <c>null</c>
/// means "not yet decided" and phase 4 must treat it as no-pause rather than
/// silently choosing; <c>true</c>/<c>false</c> is an explicit configuration.
/// </para>
/// </summary>
public class RequestTypeSlaPolicy
{
    public int RequestTypeSlaPolicyId { get; private set; }
    public int RequestTypeId { get; private set; }

    /// <summary>The priority tier this row applies to, from the existing fixed set — the Normal/Urgent urgency of the SLA document maps onto these (documented mapping decision), never onto a second priority model.</summary>
    public byte PriorityId { get; private set; }

    /// <summary>When this SLA's clock starts — see <see cref="SlaTriggerType"/>. Send Receipts and Handover deliberately do not start at ticket creation.</summary>
    public SlaTriggerType Trigger { get; private set; }

    /// <summary>The unit every duration value below is expressed in.</summary>
    public SlaDurationUnit Unit { get; private set; }

    /// <summary>First Response target, in <see cref="Unit"/>. Null where the source document gives no first-response figure (a documented pending decision — most rows), never a guessed value.</summary>
    public int? FirstResponseTargetValue { get; private set; }

    /// <summary>Upper bound of a First Response range, in <see cref="Unit"/>. Null when the source gives a single value or none.</summary>
    public int? FirstResponseMaximumValue { get; private set; }

    /// <summary>Resolution lower bound / single value, in <see cref="Unit"/> (e.g. the 10 of "10–12 Days").</summary>
    public int? ResolutionTargetValue { get; private set; }

    /// <summary>Resolution upper bound, in <see cref="Unit"/> (e.g. the 12 of "10–12 Days"). Null when the source gives a single value.</summary>
    public int? ResolutionMaximumValue { get; private set; }

    /// <summary>True for the document's "Immediately" entries (Urgent Ticketing System tickets) — represented as a flag, not as a fabricated zero-duration.</summary>
    public bool IsImmediate { get; private set; }

    /// <summary>
    /// How duration values are counted against a clock. Null = the business
    /// has not yet decided (business vs. calendar days, weekend/holiday
    /// treatment — pending decisions 1–3); phase 4 must surface that rather
    /// than assume. Non-null is an explicit configuration using the existing
    /// <see cref="SlaClockBasis"/> semantics.
    /// </summary>
    public SlaClockBasis? ClockBasis { get; private set; }

    /// <summary>Whether Pending Customer pauses this SLA's clock. Null = pending business approval — see this type's remarks.</summary>
    public bool? PausesOnPendingCustomer { get; private set; }

    /// <summary>Whether Pending Internal / Third Party pauses this SLA's clock. Null = pending business approval; internal delays never pause implicitly.</summary>
    public bool? PausesOnPendingInternal { get; private set; }

    /// <summary>Pre-breach warning threshold in percent of the target elapsed, mirroring <see cref="SlaPolicy.WarningThresholdPercent"/>. Null = use the escalation configuration's default (phase 5).</summary>
    public decimal? WarningThresholdPercent { get; private set; }

    public bool IsActive { get; private set; }

    private RequestTypeSlaPolicy() { }

    public RequestTypeSlaPolicy(
        int requestTypeId,
        byte priorityId,
        SlaTriggerType trigger,
        SlaDurationUnit unit,
        int? firstResponseTargetValue,
        int? firstResponseMaximumValue,
        int? resolutionTargetValue,
        int? resolutionMaximumValue,
        bool isImmediate = false,
        SlaClockBasis? clockBasis = null,
        bool? pausesOnPendingCustomer = null,
        bool? pausesOnPendingInternal = null,
        decimal? warningThresholdPercent = null,
        bool isActive = true)
    {
        if (!Enum.IsDefined(typeof(PriorityLevel), priorityId))
        {
            throw new ArgumentException($"PriorityId {priorityId} is not one of the fixed priorities.", nameof(priorityId));
        }

        if (!Enum.IsDefined(trigger))
        {
            throw new ArgumentException($"Trigger {trigger} is not a defined SLA trigger.", nameof(trigger));
        }

        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentException($"Unit {unit} is not a defined SLA duration unit.", nameof(unit));
        }

        if (clockBasis is { } basis && !Enum.IsDefined(basis))
        {
            throw new ArgumentException($"ClockBasis {basis} is not a defined basis.", nameof(clockBasis));
        }

        ValidateRange(firstResponseTargetValue, firstResponseMaximumValue, "FirstResponse");
        ValidateRange(resolutionTargetValue, resolutionMaximumValue, "Resolution");

        if (isImmediate && (resolutionTargetValue is not null || resolutionMaximumValue is not null))
        {
            throw new ArgumentException(
                "An immediate SLA carries no resolution duration — IsImmediate and resolution values are mutually exclusive.",
                nameof(isImmediate));
        }

        if (!isImmediate && resolutionTargetValue is null && resolutionMaximumValue is null)
        {
            throw new ArgumentException(
                "A non-immediate SLA must carry at least a resolution target or maximum.", nameof(resolutionTargetValue));
        }

        if (warningThresholdPercent is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(warningThresholdPercent), "WarningThresholdPercent must be within (0, 100].");
        }

        RequestTypeId = requestTypeId;
        PriorityId = priorityId;
        Trigger = trigger;
        Unit = unit;
        FirstResponseTargetValue = firstResponseTargetValue;
        FirstResponseMaximumValue = firstResponseMaximumValue;
        ResolutionTargetValue = resolutionTargetValue;
        ResolutionMaximumValue = resolutionMaximumValue;
        IsImmediate = isImmediate;
        ClockBasis = clockBasis;
        PausesOnPendingCustomer = pausesOnPendingCustomer;
        PausesOnPendingInternal = pausesOnPendingInternal;
        WarningThresholdPercent = warningThresholdPercent;
        IsActive = isActive;
    }

    private static void ValidateRange(int? targetValue, int? maximumValue, string deadlineName)
    {
        if (targetValue is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetValue), $"{deadlineName} target must be positive when set.");
        }

        if (maximumValue is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumValue), $"{deadlineName} maximum must be positive when set.");
        }

        if (targetValue is { } target && maximumValue is { } maximum && maximum < target)
        {
            throw new ArgumentException(
                $"{deadlineName} maximum {maximum} cannot be below its target {target} — a range's upper bound never precedes its lower.",
                nameof(maximumValue));
        }
    }
}
