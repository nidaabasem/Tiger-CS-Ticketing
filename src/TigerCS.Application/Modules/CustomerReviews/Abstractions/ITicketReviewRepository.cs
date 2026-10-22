using TigerCS.Domain.Modules.CustomerReviews;

namespace TigerCS.Application.Modules.CustomerReviews.Abstractions;

/// <summary>Storage for review links and the reviews submitted through them.</summary>
public interface ITicketReviewRepository
{
    /// <summary>Stages a new link. Committed by the caller's unit of work (the notification dispatcher commits once per message).</summary>
    Task AddInvitationAsync(TicketReviewInvitation invitation, CancellationToken cancellationToken = default);

    /// <summary>The link whose token hashes to <paramref name="tokenHash"/>, or <c>null</c> — a tampered or unknown token matches nothing.</summary>
    Task<TicketReviewInvitation?> GetInvitationByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default);

    /// <summary>The submitted review for this ticket cycle and environment kind, if any.</summary>
    Task<TicketReview?> GetReviewAsync(long ticketId, int reopenCycle, bool isTest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the review immediately. Returns <c>false</c> — storing nothing — when a review for the same ticket cycle already
    /// exists (the unique index, so two simultaneous submissions cannot both win).
    /// </summary>
    Task<bool> TryAddReviewAsync(TicketReview review, CancellationToken cancellationToken = default);

    /// <summary>Production satisfaction figures. Test reviews (<c>IsTest</c>) are never included.</summary>
    Task<TicketReviewSummary> GetProductionSummaryAsync(CancellationToken cancellationToken = default);
}

public sealed record TicketReviewSummary(int ReviewCount, double? AverageRating);
