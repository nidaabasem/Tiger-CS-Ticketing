using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The one <see cref="Ticket"/> → <see cref="TicketResponseDto"/> projection.
/// Extracted so a second creation path (the Genesys inquiry ingestion, which
/// must return the SAME body for a retry as the original call did) reads the
/// ticket back through exactly the mapping ticket creation itself uses —
/// rather than a parallel copy that could drift a field at a time.
/// </summary>
public static class TicketProjection
{
    public static TicketResponseDto ToResponseDto(Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return new TicketResponseDto(
            ticket.TicketId,
            ticket.TicketNumber,
            ticket.OriginatingDepartmentId,
            ticket.CurrentDepartmentId,
            ticket.UnitReferenceId,
            ticket.ContactReferenceId,
            ticket.CategoryId,
            ticket.PriorityId,
            ticket.TicketStatus.ToString(),
            ticket.VerificationStatus.ToString(),
            ticket.EscalationLevel.ToString(),
            ticket.SlaState.ToString(),
            ticket.RequestSummary,
            ticket.CreatedAtUtc,
            Convert.ToBase64String(ticket.RowVersion),
            ticket.CrmBuyerCustomerId,
            ticket.CrmBuyerLeadId,
            ticket.CrmBuyerUnitId,
            ticket.CrmBuyerProjectId,
            ticket.CrmBuyerCustomerName,
            ticket.CrmBuyerProjectName,
            ticket.CrmBuyerUnitNumber,
            ticket.ManualProjectName,
            ticket.ManualUnitNumber,
            ticket.CustomerVerificationSource,
            ticket.ExternalCustomerId,
            ticket.ExternalUnitId);
    }
}
