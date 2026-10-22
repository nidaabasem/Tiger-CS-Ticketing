namespace TigerCS.Application.Modules.CustomerReviews.Dto;

public enum ReviewLinkStatus
{
    /// <summary>Unknown, malformed or tampered token. Deliberately indistinguishable from each other.</summary>
    Invalid = 0,

    /// <summary>The link is genuine and a review may be submitted.</summary>
    Valid = 1,

    /// <summary>The link is genuine but past its expiry.</summary>
    Expired = 2,

    /// <summary>A review was already submitted for this ticket; it is preserved and cannot be changed.</summary>
    AlreadySubmitted = 3
}

/// <summary>
/// What the public review page may know about a link. The ticket number is the only ticket detail — it is already in the
/// email the customer received — and it is released only for a valid link.
/// </summary>
public sealed record ReviewLinkDto(ReviewLinkStatus Status, string? TicketNumber = null);

public sealed record SubmitReviewRequestDto(int Rating, string? Comment);

public enum SubmitReviewOutcome
{
    Submitted = 0,
    Invalid = 1,
    Expired = 2,
    AlreadySubmitted = 3,
    InvalidRating = 4,
    InvalidComment = 5
}

public sealed record SubmitReviewResultDto(SubmitReviewOutcome Outcome, string? TicketNumber = null);

/// <summary>The links placed in a Resolved/Closed email. Never contain anything but the opaque token (and the rating).</summary>
/// <param name="PageUrl">The review page without a pre-selected rating.</param>
/// <param name="StarUrls">Five links, index 0 = 1 star … index 4 = 5 stars. Opening one only pre-selects the rating; nothing is saved.</param>
/// <param name="ExpiresAtUtc">When the links stop working.</param>
public sealed record ReviewInvitationLinks(string PageUrl, IReadOnlyList<string> StarUrls, DateTime ExpiresAtUtc);

/// <summary>The review token from the email link, sent in a request body so it never appears in a URL or access log.</summary>
public sealed record ReviewTokenRequestDto(string? Token);

/// <summary>The token, the star rating and the optional comment.</summary>
public sealed record SubmitTicketReviewRequestDto(string? Token, int Rating, string? Comment);
