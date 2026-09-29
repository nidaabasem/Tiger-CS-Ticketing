namespace TigerCS.Domain.Modules.GenesysIntegration;

/// <summary>
/// One Genesys <b>Secure Screen Pop</b> launch: the single-use, short-lived
/// credential that lets a Genesys agent open TigerCS Web in any browser or
/// WebView — with no prior TigerCS login cookie — as the Ticketing user their
/// Genesys User ID is mapped to, landing on the page the launch names.
///
/// <para>
/// <b>Only a hash is stored.</b> The launch token itself is a 256-bit random
/// value handed to Genesys exactly once (inside the launch URL) and never
/// persisted; <see cref="TokenHash"/> is its SHA-256. A database read of this
/// table yields nothing that can be replayed. The token carries no username,
/// password or user data — it is an opaque random reference to this row.
/// </para>
///
/// <para>
/// <b>Valid for at most <see cref="Lifetime"/>, and exactly once.</b>
/// <see cref="Redeem"/> refuses a launch past <see cref="ExpiresAtUtc"/> or
/// one already redeemed; the database treats <see cref="RedeemedAtUtc"/> as a
/// concurrency token, so two simultaneous redemptions of the same token
/// cannot both succeed.
/// </para>
///
/// <para>
/// <b>Authentication, never authorization.</b> Redeeming a launch signs the
/// mapped user in exactly as a password login would — same access token, same
/// roles — and nothing more. <see cref="TargetPath"/> is only where the
/// browser is sent next; the target page is authorized under the ordinary
/// department-visibility rules like any other request.
/// </para>
/// </summary>
public class GenesysScreenPopLaunch
{
    /// <summary>How long a launch stays redeemable: one hour.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>Hex-encoded SHA-256 of the launch token.</summary>
    public const int TokenHashLength = 64;

    public const int GenesysUserIdMaxLength = 64;
    public const int TargetPathMaxLength = 512;
    public const int ConversationIdMaxLength = 128;

    public long GenesysScreenPopLaunchId { get; private set; }

    /// <summary>SHA-256 of the launch token, lower-case hex. The token itself is never stored.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    /// <summary>The immutable Genesys User ID the launch was issued for.</summary>
    public string GenesysUserId { get; private set; } = string.Empty;

    /// <summary>The Ticketing user that Genesys User ID was mapped to at issue time.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The app-relative TigerCS Web path the agent lands on (always starts with a single '/').</summary>
    public string TargetPath { get; private set; } = string.Empty;

    /// <summary>The Genesys conversation the launch was issued for, when one was named.</summary>
    public string? ConversationId { get; private set; }

    /// <summary>The ticket the launch lands on, when it lands on one.</summary>
    public long? TicketId { get; private set; }

    /// <summary>The authenticated TigerCS caller (the Genesys service account) that requested the launch.</summary>
    public Guid IssuedByEmployeeId { get; private set; }

    public DateTime IssuedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }

    /// <summary>When the launch was redeemed; null while unused. The single-use marker, and a concurrency token.</summary>
    public DateTime? RedeemedAtUtc { get; private set; }

    private GenesysScreenPopLaunch() { }

    public GenesysScreenPopLaunch(
        string tokenHash,
        string genesysUserId,
        Guid userId,
        string targetPath,
        string? conversationId,
        long? ticketId,
        Guid issuedByEmployeeId,
        DateTime issuedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(tokenHash) || tokenHash.Length != TokenHashLength)
        {
            throw new ArgumentException($"TokenHash must be a {TokenHashLength}-character hex SHA-256.", nameof(tokenHash));
        }

        if (string.IsNullOrWhiteSpace(genesysUserId) || genesysUserId.Trim().Length > GenesysUserIdMaxLength)
        {
            throw new ArgumentException($"GenesysUserId is required and at most {GenesysUserIdMaxLength} characters.", nameof(genesysUserId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("UserId must identify a Ticketing user.", nameof(userId));
        }

        if (!IsAppRelativePath(targetPath))
        {
            throw new ArgumentException("TargetPath must be an app-relative path starting with a single '/'.", nameof(targetPath));
        }

        var conversation = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId.Trim();
        if (conversation is { Length: > ConversationIdMaxLength })
        {
            conversation = conversation[..ConversationIdMaxLength];
        }

        TokenHash = tokenHash;
        GenesysUserId = genesysUserId.Trim();
        UserId = userId;
        TargetPath = targetPath;
        ConversationId = conversation;
        TicketId = ticketId;
        IssuedByEmployeeId = issuedByEmployeeId;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = issuedAtUtc.Add(Lifetime);
    }

    /// <summary>
    /// Consumes the launch. Expiry is checked first, so an expired launch
    /// reports <see cref="GenesysScreenPopRedemption.Expired"/> even if it was
    /// also used. A launch is redeemable up to and including
    /// <see cref="ExpiresAtUtc"/>.
    /// </summary>
    public GenesysScreenPopRedemption Redeem(DateTime nowUtc)
    {
        if (nowUtc > ExpiresAtUtc)
        {
            return GenesysScreenPopRedemption.Expired;
        }

        if (RedeemedAtUtc is not null)
        {
            return GenesysScreenPopRedemption.AlreadyRedeemed;
        }

        RedeemedAtUtc = nowUtc;
        return GenesysScreenPopRedemption.Redeemed;
    }

    /// <summary>
    /// App-relative only: a launch can never send the agent off-site.
    /// "//host" and "/\host" are protocol-relative in browsers, so both are
    /// refused along with anything absolute.
    /// </summary>
    public static bool IsAppRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Length <= TargetPathMaxLength
        && path[0] == '/'
        && !(path.Length > 1 && (path[1] == '/' || path[1] == '\\'));
}

/// <summary>What <see cref="GenesysScreenPopLaunch.Redeem"/> decided.</summary>
public enum GenesysScreenPopRedemption
{
    Redeemed,
    Expired,
    AlreadyRedeemed
}
