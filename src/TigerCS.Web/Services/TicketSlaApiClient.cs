using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Web.Infrastructure;

namespace TigerCS.Web.Services;

/// <summary>
/// The SLA and escalation views under <c>/api/tickets/{id}</c>
/// (MVP-API-Contracts.md §5.1/§5.9).
///
/// <para>
/// Read-only in this increment. Recording a first response (§5.2) and raising
/// a manual escalation (§5.7) are live endpoints, but neither is in this
/// increment's scope, so neither is called.
/// </para>
/// </summary>
public sealed class TicketSlaApiClient(TigerCsApiClient api)
{
    /// <summary>
    /// One ticket's SLA panel (§5.1): both deadlines, both breach flags, the
    /// ticket-level state and the escalation level.
    /// </summary>
    public Task<ApiResult<TicketSlaSummaryResponseDto>> GetSlaAsync(
        long ticketId, CancellationToken cancellationToken) =>
        api.GetAsync<TicketSlaSummaryResponseDto>($"/api/tickets/{ticketId}/sla", cancellationToken);

    /// <summary>A ticket's escalation history, most recent first (§5.9).</summary>
    public Task<ApiResult<IReadOnlyList<TicketEscalationResponseDto>>> GetEscalationsAsync(
        long ticketId, CancellationToken cancellationToken) =>
        api.GetAsync<IReadOnlyList<TicketEscalationResponseDto>>(
            $"/api/tickets/{ticketId}/escalations", cancellationToken);
}
