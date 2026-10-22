using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CustomerReviews.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's anonymous <c>api/public/ticket-reviews</c> endpoints for the public customer review page. No bearer token
/// is attached — the customer has no session, and the review token in the body is the only credential.
/// </summary>
public sealed class ReviewsApiClient(HttpClient httpClient, ILogger<ReviewsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<ReviewLinkDto>> LookupAsync(string? token, CancellationToken cancellationToken) =>
        PostAsync<ReviewTokenRequestDto, ReviewLinkDto>("api/public/ticket-reviews/lookup", new ReviewTokenRequestDto(token), cancellationToken);

    public Task<ApiResult<SubmitReviewResultDto>> SubmitAsync(string? token, int rating, string? comment, CancellationToken cancellationToken) =>
        PostAsync<SubmitTicketReviewRequestDto, SubmitReviewResultDto>(
            "api/public/ticket-reviews/submit", new SubmitTicketReviewRequestDto(token, rating, comment), cancellationToken);
}
