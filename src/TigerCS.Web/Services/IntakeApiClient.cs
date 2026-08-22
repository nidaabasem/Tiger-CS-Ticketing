using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Infrastructure;

namespace TigerCS.Web.Services;

/// <summary>
/// <c>POST /api/intake-records</c> - the unconditional first step of intake
/// (MVP-ERD.md §2.9).
/// </summary>
public sealed class IntakeApiClient(TigerCsApiClient api)
{
    /// <summary>
    /// Records the interaction before any verification is attempted, so no
    /// call is lost even if verification never completes.
    /// </summary>
    public Task<ApiResult<IntakeRecordResponseDto>> CreateAsync(
        CreateIntakeRecordRequestDto request, CancellationToken cancellationToken) =>
        api.PostAsync<IntakeRecordResponseDto>("/api/intake-records", request, cancellationToken);
}
