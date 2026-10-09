using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/tickets/{id}/sla</c> and <c>.../escalations</c> endpoints.</summary>
public sealed class TicketSlaApiClient(HttpClient httpClient, ILogger<TicketSlaApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<TicketSlaSummaryResponseDto>> GetSlaAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<TicketSlaSummaryResponseDto>($"api/tickets/{ticketId}/sla", cancellationToken);

    public Task<ApiResult<TicketSlaSummaryResponseDto>> RecordFirstResponseAsync(
        long ticketId, RecordFirstResponseRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<RecordFirstResponseRequestDto, TicketSlaSummaryResponseDto>($"api/tickets/{ticketId}/sla/first-response", request, cancellationToken);

    public Task<ApiResult<TicketEscalationResponseDto>> EscalateAsync(
        long ticketId, ManualEscalationRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<ManualEscalationRequestDto, TicketEscalationResponseDto>($"api/tickets/{ticketId}/escalations", request, cancellationToken);

    // ---- Priority-downgrade requests (MVP-API-Contracts.md section 5.6) ----

    public Task<ApiResult<PriorityDowngradeRequestResponseDto>> RequestPriorityDowngradeAsync(
        long ticketId, CreateDowngradeRequestRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CreateDowngradeRequestRequestDto, PriorityDowngradeRequestResponseDto>(
            $"api/tickets/{ticketId}/sla/priority-downgrade-requests", request, cancellationToken);

    public Task<ApiResult<IReadOnlyList<PriorityDowngradeRequestResponseDto>>> GetPriorityDowngradeRequestsAsync(
        long ticketId, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<PriorityDowngradeRequestResponseDto>>(
            $"api/tickets/{ticketId}/sla/priority-downgrade-requests", cancellationToken);

    public Task<ApiResult<DowngradeDecisionResponseDto>> ApprovePriorityDowngradeAsync(
        long requestId, ApproveDowngradeRequestRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<ApproveDowngradeRequestRequestDto, DowngradeDecisionResponseDto>(
            $"api/priority-downgrade-requests/{requestId}/approve", request, cancellationToken);

    public Task<ApiResult<PriorityDowngradeRequestResponseDto>> RejectPriorityDowngradeAsync(
        long requestId, RejectDowngradeRequestRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<RejectDowngradeRequestRequestDto, PriorityDowngradeRequestResponseDto>(
            $"api/priority-downgrade-requests/{requestId}/reject", request, cancellationToken);

    public Task<ApiResult<IReadOnlyList<TicketEscalationResponseDto>>> GetEscalationsAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<TicketEscalationResponseDto>>($"api/tickets/{ticketId}/escalations", cancellationToken);
}
