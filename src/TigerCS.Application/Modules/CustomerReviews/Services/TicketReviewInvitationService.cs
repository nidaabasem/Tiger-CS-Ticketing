using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CustomerReviews.Abstractions;
using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Domain.Modules.CustomerReviews;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.CustomerReviews.Services;

/// <summary>
/// Issues the secure review links that go into Resolved and Closed emails.
///
/// <para>
/// A link is issued only when the customer has not already reviewed this ticket cycle — so the Closed email never asks again
/// after a review was given, and an existing review is never put at risk — and only when a public base URL is configured to
/// build the link from. Otherwise the email is sent without a review section.
/// </para>
///
/// <para>
/// Staged, not saved: the notification dispatcher commits the invitation together with the delivery record. A retried
/// delivery therefore issues a fresh link; earlier unused ones simply expire, and either can submit the one review.
/// </para>
/// </summary>
public sealed class TicketReviewInvitationService(
    ITicketReviewRepository repository,
    CustomerNotificationModePolicy mode,
    TimeProvider timeProvider,
    ILogger<TicketReviewInvitationService> logger)
{
    /// <summary>The links to embed, or <c>null</c> when no review should be requested.</summary>
    public async Task<ReviewInvitationLinks?> PrepareAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (mode.PublicBaseUrl is null)
        {
            logger.LogWarning(
                "CustomerNotifications:PublicBaseUrl is not configured; the email for ticket {TicketNumber} is sent without a review section.",
                ticket.TicketNumber);
            return null;
        }

        var isTest = mode.IsTest;
        if (await repository.GetReviewAsync(ticket.TicketId, ticket.ReopenCount, isTest, cancellationToken) is not null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var token = ReviewToken.Generate();
        var invitation = new TicketReviewInvitation(
            ticket.TicketId, ticket.ReopenCount, ReviewToken.Hash(token)!, isTest, now, now + mode.ReviewLinkLifetime);
        await repository.AddInvitationAsync(invitation, cancellationToken);

        var stars = Enumerable.Range(1, 5).Select(r => mode.BuildReviewUrl(token, r)!).ToList();
        return new ReviewInvitationLinks(mode.BuildReviewUrl(token)!, stars, invitation.ExpiresAtUtc);
    }
}
