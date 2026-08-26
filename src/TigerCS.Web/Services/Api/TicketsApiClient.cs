using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/tickets</c> endpoints.</summary>
public sealed class TicketsApiClient(HttpClient httpClient, ILogger<TicketsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<TicketListResultDto>> GetQueueAsync(TicketListRequestDto request, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (request.DepartmentId is int departmentId) query["departmentId"] = departmentId.ToString();
        if (request.CategoryId is int categoryId) query["categoryId"] = categoryId.ToString();
        if (request.PriorityId is byte priorityId) query["priorityId"] = priorityId.ToString();
        if (!string.IsNullOrWhiteSpace(request.TicketStatus)) query["ticketStatus"] = request.TicketStatus;
        if (!string.IsNullOrWhiteSpace(request.VerificationStatus)) query["verificationStatus"] = request.VerificationStatus;
        if (request.OwnerEmployeeId is Guid ownerId) query["ownerEmployeeId"] = ownerId.ToString();
        if (!string.IsNullOrWhiteSpace(request.Search)) query["search"] = request.Search;
        if (!string.IsNullOrWhiteSpace(request.SortBy)) query["sortBy"] = request.SortBy;
        if (!string.IsNullOrWhiteSpace(request.SortDir)) query["sortDir"] = request.SortDir;
        query["page"] = request.Page.ToString();
        query["pageSize"] = request.PageSize.ToString();

        return GetAsync<TicketListResultDto>($"api/tickets?{query}", cancellationToken);
    }

    public Task<ApiResult<TicketDetailDto>> GetByIdAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<TicketDetailDto>($"api/tickets/{ticketId}", cancellationToken);

    public Task<ApiResult<TicketDetailDto>> AssignAsync(long ticketId, AssignTicketRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<AssignTicketRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/assignment", request, cancellationToken);

    public Task<ApiResult<TicketDetailDto>> TransferAsync(long ticketId, TransferTicketRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<TransferTicketRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/transfer", request, cancellationToken);

    public Task<ApiResult<TicketDetailDto>> ChangeStatusAsync(long ticketId, ChangeStatusRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<ChangeStatusRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/status", request, cancellationToken);

    public Task<ApiResult<TicketDetailDto>> ResolveAsync(long ticketId, ResolveTicketRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<ResolveTicketRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/resolution", request, cancellationToken);

    public Task<ApiResult<TicketDetailDto>> CloseAsync(long ticketId, CloseTicketRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CloseTicketRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/close", request, cancellationToken);

    public Task<ApiResult<TicketNoteResponseDto>> AddNoteAsync(long ticketId, CreateNoteRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CreateNoteRequestDto, TicketNoteResponseDto>($"api/tickets/{ticketId}/notes", request, cancellationToken);

    public Task<ApiResult<TicketNoteListResultDto>> GetNotesAsync(
        long ticketId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["page"] = page.ToString();
        query["pageSize"] = pageSize.ToString();

        return GetAsync<TicketNoteListResultDto>($"api/tickets/{ticketId}/notes?{query}", cancellationToken);
    }

    public Task<ApiResult<TicketResponseDto>> CreateAsync(
        CreateTicketRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CreateTicketRequestDto, TicketResponseDto>("api/tickets", request, cancellationToken);
}
