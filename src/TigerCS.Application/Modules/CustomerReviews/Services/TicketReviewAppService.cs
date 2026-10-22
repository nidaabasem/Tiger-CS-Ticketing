using TigerCS.Application.Modules.CustomerReviews.Abstractions;
using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.CustomerReviews;

namespace TigerCS.Application.Modules.CustomerReviews.Services;

/// <summary>
/// The server side of the public review page. Reached without a login, so the token is the only credential and every
/// decision is made here: the page and the email links are never trusted.
///
/// <list type="bullet">
///   <item><b>Looking at a link changes nothing.</b> <see cref="GetLinkAsync"/> is read-only — email security scanners and
///   link previews that open the rating links cannot submit a review.</item>
///   <item><b>Submitting is explicit and once.</b> The review is committed only by <see cref="SubmitAsync"/>; the unique
///   index on ticket cycle makes a second or simultaneous submission fail without touching the first.</item>
///   <item><b>Bad tokens look alike.</b> Malformed, unknown and altered tokens all answer <c>Invalid</c>.</item>
/// </list>
/// </summary>
public sealed class TicketReviewAppService(
    ITicketReviewRepository repository,
    ITicketRepository ticketRepository,
    TimeProvider timeProvider)
{
    public async Task<ReviewLinkDto> GetLinkAsync(string? token, CancellationToken cancellationToken = default)
    {
        var (state, invitation, _) = await ResolveAsync(token, cancellationToken);
        if (state != ReviewLinkStatus.Valid && state != ReviewLinkStatus.AlreadySubmitted || invitation is null)
        {
            return new ReviewLinkDto(state);
        }

        // The ticket number is revealed for a genuine (valid or already-used) link only; an expired one reveals nothing.
        var ticket = await ticketRepository.GetByIdAsync(invitation.TicketId, cancellationToken);
        return new ReviewLinkDto(state, ticket?.TicketNumber);
    }

    public async Task<SubmitReviewResultDto> SubmitAsync(string? token, SubmitReviewRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (state, invitation, _) = await ResolveAsync(token, cancellationToken);
        switch (state)
        {
            case ReviewLinkStatus.Invalid:
                return new SubmitReviewResultDto(SubmitReviewOutcome.Invalid);
            case ReviewLinkStatus.Expired:
                return new SubmitReviewResultDto(SubmitReviewOutcome.Expired);
            case ReviewLinkStatus.AlreadySubmitted:
                return new SubmitReviewResultDto(SubmitReviewOutcome.AlreadySubmitted);
        }

        if (request.Rating is < TicketReview.MinRating or > TicketReview.MaxRating)
        {
            return new SubmitReviewResultDto(SubmitReviewOutcome.InvalidRating);
        }

        var comment = NormalizeComment(request.Comment);
        if (comment is { Length: > TicketReview.CommentMaxLength })
        {
            return new SubmitReviewResultDto(SubmitReviewOutcome.InvalidComment);
        }

        var review = new TicketReview(invitation!, request.Rating, comment, timeProvider.GetUtcNow().UtcDateTime);
        if (!await repository.TryAddReviewAsync(review, cancellationToken))
        {
            return new SubmitReviewResultDto(SubmitReviewOutcome.AlreadySubmitted);
        }

        var ticket = await ticketRepository.GetByIdAsync(invitation!.TicketId, cancellationToken);
        return new SubmitReviewResultDto(SubmitReviewOutcome.Submitted, ticket?.TicketNumber);
    }

    private async Task<(ReviewLinkStatus State, TicketReviewInvitation? Invitation, TicketReview? Review)> ResolveAsync(
        string? token, CancellationToken cancellationToken)
    {
        var hash = ReviewToken.Hash(token);
        if (hash is null)
        {
            return (ReviewLinkStatus.Invalid, null, null);
        }

        var invitation = await repository.GetInvitationByTokenHashAsync(hash, cancellationToken);
        if (invitation is null)
        {
            return (ReviewLinkStatus.Invalid, null, null);
        }

        // A review already given is preserved and reported even after the link expired.
        var existing = await repository.GetReviewAsync(invitation.TicketId, invitation.ReopenCycle, invitation.IsTest, cancellationToken);
        if (existing is not null)
        {
            return (ReviewLinkStatus.AlreadySubmitted, invitation, existing);
        }

        return invitation.IsExpiredAt(timeProvider.GetUtcNow().UtcDateTime)
            ? (ReviewLinkStatus.Expired, null, null)
            : (ReviewLinkStatus.Valid, invitation, null);
    }

    /// <summary>Trims, unifies line breaks and drops control characters. <c>null</c> for an empty comment.</summary>
    internal static string? NormalizeComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return null;
        }

        var cleaned = new string(comment.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Where(c => !char.IsControl(c) || c is '\n' or '\t')
            .ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
