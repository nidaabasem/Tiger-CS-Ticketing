namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// The facts about which SLA policy produced a period's due timestamps,
/// snapshotted onto <see cref="TicketSlaInstance"/> so that pause/resume math
/// and the explanation panel keep describing what actually happened even if
/// the configuration is edited afterwards.
/// </summary>
/// <param name="ResolutionClockBasis">The basis the Resolution due timestamp was computed on — what the pause extension must use.</param>
/// <param name="RequestTypeSlaPolicyId">The request-type row that governed the due dates; null when the per-priority policy did.</param>
/// <param name="PausesOnPendingCustomerOverride">The request type's explicit pause flag (null = inherit the global rule).</param>
/// <param name="RequestTypeSlaNote">Why a configured request-type SLA was NOT applied (exact reason), or null.</param>
/// <param name="AppliedFirstResponseTargetMinutes">The effective First Response target in minutes (display only).</param>
/// <param name="AppliedResolutionTargetMinutes">The effective Resolution target in minutes (display only); 0 for an immediate SLA.</param>
public sealed record AppliedSlaPolicy(
    SlaClockBasis ResolutionClockBasis,
    int? RequestTypeSlaPolicyId = null,
    bool? PausesOnPendingCustomerOverride = null,
    string? RequestTypeSlaNote = null,
    int? AppliedFirstResponseTargetMinutes = null,
    int? AppliedResolutionTargetMinutes = null);
