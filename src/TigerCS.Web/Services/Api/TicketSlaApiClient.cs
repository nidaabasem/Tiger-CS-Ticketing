using TigerCS.Application.Modules.SlaAndEscalation.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/tickets/{id}/sla</c> and <c>.../escalations</c> endpoints.</summary>
public sealed class TicketSlaApiClient(HttpClient httpClient) : ApiClientBase(httpClient)
{
    public Task<ApiResult<TicketSlaSummaryResponseDto>> GetSlaAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<TicketSlaSummaryResponseDto>($"api/tickets/{ticketId}/sla", cancellationToken);

    public Task<ApiResult<TicketSlaSummaryResponseDto>> RecordFirstResponseAsync(
        long ticketId, RecordFirstResponseRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<RecordFirstResponseRequestDto, TicketSlaSummaryResponseDto>($"api/tickets/{ticketId}/sla/first-response", request, cancellationToken);

    public Task<ApiResult<TicketEscalationResponseDto>> EscalateAsync(
        long ticketId, ManualEscalationRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<ManualEscalationRequestDto, TicketEscalationResponseDto>($"api/tickets/{ticketId}/escalations", request, cancellationToken);

    public Task<ApiResult<IReadOnlyList<TicketEscalationResponseDto>>> GetEscalationsAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<TicketEscalationResponseDto>>($"api/tickets/{ticketId}/escalations", cancellationToken);
}
