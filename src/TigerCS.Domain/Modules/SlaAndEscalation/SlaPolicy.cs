namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md §2.6 (the <c>SlaPolicies</c> half) / MVP-ERD.md
/// §2.6 — the per-priority SLA targets, one row per <see cref="Priority"/>
/// (1:1, identifying, seeded at setup, never deleted).
///
/// <para>
/// <b>Configuration, not code</b> (FR-SLA-01: "Targets are configuration, not
/// code"). Nothing in the due-date calculation hardcodes a tier's minutes;
/// <see cref="SlaDueDateCalculator"/> receives them from here.
/// </para>
///
/// <para>
/// <b>Two columns are seeded but not consumed by this increment</b>, and are
/// present because MVP-Data-Dictionary.md §2.6 defines them as part of this
/// table's approved shape — omitting them would only force a second
/// migration later. <see cref="WarningThresholdPercent"/> drives the
/// pre-breach warning event of SLA-Architecture.md §10, and
/// <see cref="Level2ToGmWindowValue"/>/<see cref="Level2ToGmWindowUnit"/>
/// drive the timed Level 2 → Level 3 advance of §12 — both outside this
/// increment's scope (MVP-Implementation-Backlog.md §0.2 and S-11). They
/// carry the approved defaults so that the work which does consume them
/// needs no data change.
/// </para>
/// </summary>
public class SlaPolicy
{
    public byte PriorityId { get; private set; }

    /// <summary>The First Response target. Interpreted under <see cref="ClockBasis"/> — for <see cref="SlaClockBasis.BusinessHours"/> these are minutes <i>of business time</i>, not calendar minutes.</summary>
    public int FirstResponseTargetMinutes { get; private set; }

    /// <summary>The Resolution target, interpreted under <see cref="ClockBasis"/> exactly as <see cref="FirstResponseTargetMinutes"/> is.</summary>
    public int ResolutionTargetMinutes { get; private set; }

    public SlaClockBasis ClockBasis { get; private set; }

    /// <summary>SLA-Architecture.md §10's warning threshold (approved defaults: Critical 50.00, others 75.00). Seeded, not consumed by this increment — see this type's remarks.</summary>
    public decimal WarningThresholdPercent { get; private set; }

    /// <summary>SLA-Architecture.md §12's Level 2 → GM window magnitude. Seeded, not consumed by this increment — see this type's remarks.</summary>
    public int Level2ToGmWindowValue { get; private set; }

    /// <summary>The unit <see cref="Level2ToGmWindowValue"/> is expressed in (MVP-Data-Dictionary.md §2.6: "1=Minutes, 2=Hours, 3=BusinessDays"). Seeded, not consumed by this increment.</summary>
    public byte Level2ToGmWindowUnit { get; private set; }

    public bool IsActive { get; private set; }

    private SlaPolicy() { }

    public SlaPolicy(
        byte priorityId,
        int firstResponseTargetMinutes,
        int resolutionTargetMinutes,
        SlaClockBasis clockBasis,
        decimal warningThresholdPercent,
        int level2ToGmWindowValue,
        byte level2ToGmWindowUnit,
        bool isActive = true)
    {
        if (!Enum.IsDefined(typeof(PriorityLevel), priorityId))
        {
            throw new ArgumentException($"PriorityId {priorityId} is not one of the fixed MVP priorities.", nameof(priorityId));
        }

        if (firstResponseTargetMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstResponseTargetMinutes), "FirstResponseTargetMinutes must be positive.");
        }

        if (resolutionTargetMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolutionTargetMinutes), "ResolutionTargetMinutes must be positive.");
        }

        if (!Enum.IsDefined(typeof(SlaClockBasis), clockBasis))
        {
            throw new ArgumentException($"ClockBasis {clockBasis} is not a defined basis.", nameof(clockBasis));
        }

        if (warningThresholdPercent is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(warningThresholdPercent), "WarningThresholdPercent must be within (0, 100].");
        }

        PriorityId = priorityId;
        FirstResponseTargetMinutes = firstResponseTargetMinutes;
        ResolutionTargetMinutes = resolutionTargetMinutes;
        ClockBasis = clockBasis;
        WarningThresholdPercent = warningThresholdPercent;
        Level2ToGmWindowValue = level2ToGmWindowValue;
        Level2ToGmWindowUnit = level2ToGmWindowUnit;
        IsActive = isActive;
    }

    /// <summary>The target for one deadline type, so callers select by <see cref="SlaDeadlineType"/> rather than by remembering which property is which.</summary>
    public int TargetMinutesFor(SlaDeadlineType deadlineType) => deadlineType switch
    {
        SlaDeadlineType.FirstResponse => FirstResponseTargetMinutes,
        SlaDeadlineType.Resolution => ResolutionTargetMinutes,
        _ => throw new ArgumentOutOfRangeException(nameof(deadlineType), deadlineType, "Unknown SLA deadline type.")
    };
}
