using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Application.Modules.GenesysIntegration.Dto;

/// <summary>
/// A request for a Secure Screen Pop launch. Only <paramref name="GenesysUserId"/>
/// is required; the rest choose where the agent lands, in priority order:
/// the conversation's ticket, then <paramref name="TicketId"/>, then the
/// customer lookup for <paramref name="CustomerPhone"/>, then the Tickets list.
/// </summary>
/// <param name="GenesysUserId">Required. The agent's immutable Genesys User ID — the only value that identifies the agent.</param>
/// <param name="ConversationId">The Genesys conversation being worked. When it has produced a ticket, the agent lands on that ticket.</param>
/// <param name="TicketId">A TigerCS ticket to open when no conversation ticket applies.</param>
/// <param name="CustomerPhone">A caller number to open the customer lookup with when no ticket applies.</param>
public sealed record GenesysScreenPopRequestDto(
    string? GenesysUserId,
    string? ConversationId = null,
    long? TicketId = null,
    string? CustomerPhone = null);

/// <summary>How a Screen Pop launch request landed.</summary>
public enum GenesysScreenPopIssueOutcome
{
    Issued,
    IntegrationDisabled,

    /// <summary><c>Genesys:ScreenPopWebBaseUrl</c> is not configured, so no launch URL can be built.</summary>
    NotConfigured,

    AgentIdRequired,
    AgentNotMapped,
    AgentInactive
}

/// <summary>An issued launch. <see cref="LaunchUrl"/> carries the one-time token and is returned exactly once — only its hash is stored.</summary>
public sealed record GenesysScreenPopIssueResult(
    GenesysScreenPopIssueOutcome Outcome,
    string? LaunchUrl = null,
    DateTime? ExpiresAtUtc = null,
    string? TargetPath = null,
    long? TicketId = null,
    string? Detail = null)
{
    public static GenesysScreenPopIssueResult Failure(GenesysScreenPopIssueOutcome outcome, string? detail = null) =>
        new(outcome, Detail: detail);
}

/// <summary>How redeeming a Screen Pop token landed.</summary>
public enum GenesysScreenPopRedeemOutcome
{
    /// <summary>The token was valid; a normal TigerCS session for the mapped user was issued.</summary>
    SignedIn,

    IntegrationDisabled,

    /// <summary>Blank, malformed, or never issued.</summary>
    InvalidToken,

    /// <summary>Issued more than an hour ago.</summary>
    Expired,

    /// <summary>Already redeemed once.</summary>
    AlreadyUsed,

    /// <summary>The Genesys User ID is no longer mapped to the user the launch was issued for.</summary>
    AgentNotMapped,

    /// <summary>The mapped user has been deactivated, or the account is locked out.</summary>
    AgentInactive
}

/// <summary>The body of <c>POST /api/auth/screen-pop/redeem</c>.</summary>
/// <param name="Token">Required. The one-time token from the Screen Pop launch URL's <c>token</c> query parameter.</param>
public sealed record ScreenPopRedeemRequestDto(string? Token);

/// <summary>
/// A redeemed Screen Pop: exactly the session <c>POST /api/auth/login</c>
/// returns for the mapped user, plus where TigerCS Web sends the browser.
/// </summary>
/// <param name="AccessToken">The mapped user's ordinary TigerCS JWT — the same token a password login issues.</param>
/// <param name="ExpiresAtUtc">When that token stops being accepted, in UTC.</param>
/// <param name="EmployeeId">The Ticketing user the Genesys agent is mapped to.</param>
/// <param name="DisplayName">That user's display name.</param>
/// <param name="Roles">That user's roles — unchanged by Screen Pop.</param>
/// <param name="PrimaryDepartmentId">That user's primary department, when they have one.</param>
/// <param name="TargetPath">The app-relative TigerCS Web page to open. Authorized like any other request once opened.</param>
public sealed record ScreenPopSessionResponseDto(
    string AccessToken,
    DateTime ExpiresAtUtc,
    Guid EmployeeId,
    string DisplayName,
    IReadOnlyCollection<string> Roles,
    int? PrimaryDepartmentId,
    string TargetPath);

/// <summary>
/// The redemption answer: on success, the very same session a password login
/// returns (<see cref="LoginResponseDto"/>) plus where to send the browser.
/// </summary>
public sealed record GenesysScreenPopRedeemResult(
    GenesysScreenPopRedeemOutcome Outcome,
    LoginResponseDto? Session = null,
    string? TargetPath = null)
{
    public static GenesysScreenPopRedeemResult Failure(GenesysScreenPopRedeemOutcome outcome) => new(outcome);
}
