using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Infrastructure;

namespace TigerCS.Web.Services;

/// <summary>
/// Everything under <c>/api/tickets</c> (MVP-API-Contracts.md §3 and §4).
///
/// <para>
/// <b>No authorization decision is made here.</b> The API decides, from the
/// caller's own token, which tickets are visible and which actions are
/// permitted; this client just calls and reports what came back. In
/// particular <c>departmentId</c> below narrows what the caller may already
/// see and can never widen it - <c>TicketQueryAppService</c> resolves the
/// visible set from the caller's roles and department assignments server-side.
/// </para>
/// </summary>
public sealed class TicketsApiClient(TigerCsApiClient api)
{
    /// <summary>The ticket queue (§3.2).</summary>
    public Task<ApiResult<TicketListResultDto>> GetQueueAsync(
        TicketListRequestDto request, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>
        {
            ["page"] = request.Page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = request.PageSize.ToString(CultureInfo.InvariantCulture)
        };

        AddIfPresent(query, "departmentId", request.DepartmentId);
        AddIfPresent(query, "categoryId", request.CategoryId);
        AddIfPresent(query, "priorityId", request.PriorityId);
        AddIfPresent(query, "ticketStatus", request.TicketStatus);
        AddIfPresent(query, "verificationStatus", request.VerificationStatus);
        AddIfPresent(query, "ownerEmployeeId", request.OwnerEmployeeId?.ToString());
        AddIfPresent(query, "search", request.Search);
        AddIfPresent(query, "sortBy", request.SortBy);
        AddIfPresent(query, "sortDir", request.SortDir);

        return api.GetAsync<TicketListResultDto>(
            QueryHelpers.AddQueryString("/api/tickets", query), cancellationToken);
    }

    /// <summary>One ticket's full detail (§3.3). 404 also covers "not visible to you".</summary>
    public Task<ApiResult<TicketDetailDto>> GetDetailAsync(long ticketId, CancellationToken cancellationToken) =>
        api.GetAsync<TicketDetailDto>($"/api/tickets/{ticketId}", cancellationToken);

    /// <summary>Assign or reassign (§3.5).</summary>
    public Task<ApiResult<TicketDetailDto>> AssignAsync(
        long ticketId, Guid assignedEmployeeId, byte[] rowVersion, CancellationToken cancellationToken) =>
        api.PostAsync<TicketDetailDto>(
            $"/api/tickets/{ticketId}/assignment",
            new AssignTicketRequestDto(assignedEmployeeId, rowVersion),
            cancellationToken);

    /// <summary>Transfer to another department (§3.6). Clears the current assignment server-side.</summary>
    public Task<ApiResult<TicketDetailDto>> TransferAsync(
        long ticketId, int targetDepartmentId, string reason, byte[] rowVersion, CancellationToken cancellationToken) =>
        api.PostAsync<TicketDetailDto>(
            $"/api/tickets/{ticketId}/transfer",
            new TransferTicketRequestDto(targetDepartmentId, reason, rowVersion),
            cancellationToken);

    /// <summary>Move within the working sub-machine (§3.7).</summary>
    public Task<ApiResult<TicketDetailDto>> ChangeStatusAsync(
        long ticketId, string newStatus, byte[] rowVersion, CancellationToken cancellationToken) =>
        api.PostAsync<TicketDetailDto>(
            $"/api/tickets/{ticketId}/status",
            new ChangeStatusRequestDto(newStatus, rowVersion),
            cancellationToken);

    /// <summary>Resolve (§3.9).</summary>
    public Task<ApiResult<TicketDetailDto>> ResolveAsync(
        long ticketId,
        string resolutionOutcome,
        string resolutionNote,
        long? duplicateOfTicketId,
        byte[] rowVersion,
        CancellationToken cancellationToken) =>
        api.PostAsync<TicketDetailDto>(
            $"/api/tickets/{ticketId}/resolution",
            new ResolveTicketRequestDto(resolutionOutcome, resolutionNote, null, duplicateOfTicketId, rowVersion),
            cancellationToken);

    /// <summary>Close a resolved ticket (§3.10).</summary>
    public Task<ApiResult<TicketDetailDto>> CloseAsync(
        long ticketId, byte[] rowVersion, CancellationToken cancellationToken) =>
        api.PostAsync<TicketDetailDto>(
            $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(rowVersion), cancellationToken);

    /// <summary>Reconcile a provisional ticket against a confirmed verification session.</summary>
    public Task<ApiResult<TicketDetailDto>> ReconcileAsync(
        long ticketId, Guid verificationSessionId, byte[] rowVersion, CancellationToken cancellationToken) =>
        api.PostAsync<TicketDetailDto>(
            $"/api/tickets/{ticketId}/reconciliation",
            new ReconcileTicketRequestDto(verificationSessionId, rowVersion),
            cancellationToken);

    /// <summary>A ticket's notes, newest page first (§4.2).</summary>
    public Task<ApiResult<TicketNoteListResultDto>> GetNotesAsync(
        long ticketId, int page, int pageSize, CancellationToken cancellationToken) =>
        api.GetAsync<TicketNoteListResultDto>(
            $"/api/tickets/{ticketId}/notes?page={page}&pageSize={pageSize}", cancellationToken);

    /// <summary>Add a note (§4.1).</summary>
    public Task<ApiResult<TicketNoteResponseDto>> AddNoteAsync(
        long ticketId, string noteText, CancellationToken cancellationToken) =>
        api.PostAsync<TicketNoteResponseDto>(
            $"/api/tickets/{ticketId}/notes", new CreateNoteRequestDto(noteText), cancellationToken);

    /// <summary>Create a ticket from a confirmed verification session (§3.1).</summary>
    public Task<ApiResult<TicketResponseDto>> CreateAsync(
        CreateTicketFromVerificationRequestDto request, CancellationToken cancellationToken) =>
        api.PostAsync<TicketResponseDto>("/api/tickets", request, cancellationToken);

    /// <summary>
    /// ISSUE-006's CRM-outage fallback.
    ///
    /// <para>
    /// Two success shapes, and the difference is the whole point: Critical and
    /// High answer <b>201 with a ticket</b>, Medium and Low answer <b>200 with
    /// the updated intake record and no ticket at all</b>. The second is not an
    /// error and must never be presented as a created ticket - the interaction
    /// stays queued for CRM reconciliation.
    /// </para>
    /// </summary>
    public Task<ApiResult<OneOf<TicketResponseDto, IntakeRecordResponseDto>>> CreateProvisionalAsync(
        CreateProvisionalTicketRequestDto request, CancellationToken cancellationToken) =>
        api.PostEitherAsync<TicketResponseDto, IntakeRecordResponseDto>(
            "/api/tickets/provisional", request, cancellationToken);

    private static void AddIfPresent(IDictionary<string, string?> query, string key, int? value)
    {
        if (value.HasValue)
        {
            query[key] = value.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void AddIfPresent(IDictionary<string, string?> query, string key, byte? value)
    {
        if (value.HasValue)
        {
            query[key] = value.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void AddIfPresent(IDictionary<string, string?> query, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query[key] = value;
        }
    }
}
