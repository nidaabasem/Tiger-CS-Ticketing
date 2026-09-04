namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The unit a <see cref="RequestTypeSlaPolicy"/>'s duration values are
/// expressed in — kept in the source document's own unit (the SLA document
/// speaks in days) rather than converted to minutes at seed time, so the
/// stored configuration stays recognizably the approved value.
///
/// <para>
/// <b><see cref="Days"/> is deliberately uninterpreted here.</b> Whether a
/// "day" means a calendar day or a business day (and how weekends/UAE public
/// holidays count) is an explicitly pending business decision
/// (docs/Workflow-SLA-Configuration-Phase1.md); the phase-4 calculation will
/// resolve it via <see cref="RequestTypeSlaPolicy.ClockBasis"/> and the
/// existing business-calendar infrastructure once decided.
/// </para>
/// </summary>
public enum SlaDurationUnit : byte
{
    Minutes = 1,
    Hours = 2,
    Days = 3
}
