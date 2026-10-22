namespace TigerCS.Domain.Modules.CustomerReviews;

/// <summary>
/// One secure review link issued to a customer in a Resolved or Closed email. The link carries a random token; only its
/// SHA-256 hash is stored, so a database read never yields a usable link, and a tampered or guessed token simply matches no
/// row. The token is bound to one ticket and one reopen cycle and expires.
///
/// <para>
/// An invitation never records a rating itself — opening the link (a GET, which email security scanners also do) changes
/// nothing. The rating is committed only by an explicit submission, which creates the single <see cref="TicketReview"/> for
/// the ticket's cycle.
/// </para>
/// </summary>
public class TicketReviewInvitation
{
    /// <summary>SHA-256 digest length in bytes.</summary>
    public const int TokenHashLength = 32;

    public long TicketReviewInvitationId { get; private set; }

    public long TicketId { get; private set; }

    /// <summary>The ticket's <c>ReopenCount</c> when the link was issued. A reopened ticket is reviewed again for its new cycle.</summary>
    public int ReopenCycle { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    /// <summary>True when issued by a Test-mode notification: the resulting review is kept out of production statistics.</summary>
    public bool IsTest { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    private TicketReviewInvitation()
    {
    }

    public TicketReviewInvitation(long ticketId, int reopenCycle, byte[] tokenHash, bool isTest, DateTime createdAtUtc, DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        if (tokenHash.Length != TokenHashLength)
        {
            throw new ArgumentException("A review token hash must be a SHA-256 digest.", nameof(tokenHash));
        }

        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentException("A review link must expire after it is issued.", nameof(expiresAtUtc));
        }

        TicketId = ticketId;
        ReopenCycle = reopenCycle;
        TokenHash = tokenHash;
        IsTest = isTest;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public bool IsExpiredAt(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;
}
