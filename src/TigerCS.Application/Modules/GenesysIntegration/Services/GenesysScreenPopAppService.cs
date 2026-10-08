using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.GenesysIntegration;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Genesys <b>Secure Screen Pop</b>: Genesys asks for a launch URL for one of
/// its agents, opens it in any browser or WebView, and the agent arrives in
/// TigerCS Web signed in as the Ticketing user their Genesys User ID maps to —
/// with no TigerCS login cookie needed beforehand.
///
/// <para>
/// <b>Issue</b> (<see cref="IssueAsync"/>) runs as the authenticated Genesys
/// service account. The agent is resolved strictly — through the same
/// agent-context flow <c>POST /api/genesys/agent-context</c> uses, so an
/// unmapped or deactivated agent is refused and a conversation's interaction
/// records its handler exactly as it does there. The landing page is chosen
/// in priority order: the conversation's ticket, the named ticket, the
/// customer lookup for the caller's number, the Tickets list. A 256-bit
/// random token is minted and only its SHA-256 is stored.
/// </para>
///
/// <para>
/// <b>Redeem</b> (<see cref="RedeemAsync"/>) is anonymous by nature — the
/// token is the credential. It is refused when unknown, older than one hour,
/// or already used; the mapping is re-resolved and must still point at the
/// same active, unlocked user. The answer is precisely the session a password
/// login returns (<see cref="ITokenService"/>, the user's own roles), so
/// <b>everything after sign-in is authorized exactly as for any other
/// session</b>: the token grants no ticket, department or page access of its
/// own, and a target the user may not see is refused by the target page.
/// </para>
/// </summary>
public sealed class GenesysScreenPopAppService(
    GenesysOptions options,
    GenesysAgentContextAppService agentContext,
    GenesysAgentResolutionAppService agentResolution,
    ITicketRepository ticketRepository,
    IGenesysScreenPopLaunchStore launchStore,
    ITokenService tokenService,
    IUserDepartmentAssignmentRepository departmentAssignments,
    IUserAccountManager accountManager,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    /// <summary>The path TigerCS Web serves the redemption page on.</summary>
    public const string WebLaunchPath = "/ScreenPop";

    /// <summary>A launch token is 32 random bytes, base64url-encoded: 43 characters.</summary>
    private const int TokenBytes = 32;
    private const int MaxAcceptedTokenLength = 128;

    public const string TicketsListPath = "/Tickets";

    public async Task<GenesysScreenPopIssueResult> IssueAsync(
        Guid callerEmployeeId, GenesysScreenPopRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return GenesysScreenPopIssueResult.Failure(GenesysScreenPopIssueOutcome.IntegrationDisabled);
        }

        if (!TryGetWebBaseUrl(out var webBaseUrl))
        {
            return GenesysScreenPopIssueResult.Failure(
                GenesysScreenPopIssueOutcome.NotConfigured,
                "Genesys:ScreenPopWebBaseUrl is not configured with an absolute http(s) address of TigerCS Web.");
        }

        // The conversation first: when it produced a ticket, that ticket is
        // the landing page and its interaction records this agent as the
        // handler (the agent-context rule). A conversation that has not
        // produced a ticket yet is not an error for a screen pop — the
        // landing simply falls through to the next choice.
        var context = await agentContext.ResolveAsync(
            callerEmployeeId,
            new GenesysAgentContextDto(request.GenesysUserId, ConversationId: request.ConversationId),
            cancellationToken);

        if (context.Outcome == GenesysAgentContextOutcome.ConversationNotFound)
        {
            context = await agentContext.ResolveAsync(
                callerEmployeeId, new GenesysAgentContextDto(request.GenesysUserId), cancellationToken);
        }

        switch (context.Outcome)
        {
            case GenesysAgentContextOutcome.Resolved:
                break;
            case GenesysAgentContextOutcome.IntegrationDisabled:
                return GenesysScreenPopIssueResult.Failure(GenesysScreenPopIssueOutcome.IntegrationDisabled);
            case GenesysAgentContextOutcome.AgentIdRequired:
                return GenesysScreenPopIssueResult.Failure(GenesysScreenPopIssueOutcome.AgentIdRequired, context.Detail);
            case GenesysAgentContextOutcome.AgentInactive:
                return GenesysScreenPopIssueResult.Failure(GenesysScreenPopIssueOutcome.AgentInactive, context.Detail);
            default:
                return GenesysScreenPopIssueResult.Failure(GenesysScreenPopIssueOutcome.AgentNotMapped, context.Detail);
        }

        var agent = context.Agent!;
        var (targetPath, ticketId) = await ChooseLandingAsync(context.TicketId, request, cancellationToken);

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var launch = new GenesysScreenPopLaunch(
            HashToken(token), agent.GenesysUserId, agent.UserId, targetPath,
            request.ConversationId, ticketId, callerEmployeeId, now);

        await launchStore.AddAsync(launch, cancellationToken);
        await auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.ScreenPopIssued,
            GenesysAuditActions.ScreenPopEntityType,
            agent.GenesysUserId,
            beforeValue: null,
            // Never the token — only what the launch points at.
            afterValue: $"UserId={agent.UserId};TargetPath={targetPath};ExpiresAtUtc={launch.ExpiresAtUtc:O}",
            correlationId: Guid.NewGuid(),
            cancellationToken);
        await launchStore.SaveChangesAsync(cancellationToken);

        return new GenesysScreenPopIssueResult(
            GenesysScreenPopIssueOutcome.Issued,
            $"{webBaseUrl}{WebLaunchPath}?token={token}",
            launch.ExpiresAtUtc,
            targetPath,
            ticketId);
    }

    public async Task<GenesysScreenPopRedeemResult> RedeemAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return GenesysScreenPopRedeemResult.Failure(GenesysScreenPopRedeemOutcome.IntegrationDisabled);
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxAcceptedTokenLength)
        {
            return GenesysScreenPopRedeemResult.Failure(GenesysScreenPopRedeemOutcome.InvalidToken);
        }

        var launch = await launchStore.FindByTokenHashAsync(HashToken(token.Trim()), cancellationToken);
        if (launch is null)
        {
            return GenesysScreenPopRedeemResult.Failure(GenesysScreenPopRedeemOutcome.InvalidToken);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        switch (launch.Redeem(now))
        {
            case GenesysScreenPopRedemption.Expired:
                return GenesysScreenPopRedeemResult.Failure(GenesysScreenPopRedeemOutcome.Expired);
            case GenesysScreenPopRedemption.AlreadyRedeemed:
                return GenesysScreenPopRedeemResult.Failure(GenesysScreenPopRedeemOutcome.AlreadyUsed);
        }

        // The mapping as it stands NOW, not as it stood at issue: an agent
        // unmapped, re-mapped, deactivated or locked out during the hour
        // cannot use a launch issued before that happened. The token is
        // consumed either way.
        var resolution = await agentResolution.ResolveByGenesysUserIdAsync(launch.GenesysUserId, cancellationToken);
        var refusal = resolution switch
        {
            { Outcome: GenesysAgentResolutionOutcome.Inactive } => GenesysScreenPopRedeemOutcome.AgentInactive,
            { IsResolved: false } => GenesysScreenPopRedeemOutcome.AgentNotMapped,
            _ when resolution.Agent!.UserId != launch.UserId => GenesysScreenPopRedeemOutcome.AgentNotMapped,
            _ => (GenesysScreenPopRedeemOutcome?)null
        };

        if (refusal is null && (await accountManager.GetAccountAsync(launch.UserId, cancellationToken)) is not { IsLockedOut: false })
        {
            refusal = GenesysScreenPopRedeemOutcome.AgentInactive;
        }

        await auditWriter.WriteAsync(
            launch.UserId,
            refusal is null ? GenesysAuditActions.ScreenPopRedeemed : GenesysAuditActions.ScreenPopRefused,
            GenesysAuditActions.ScreenPopEntityType,
            launch.GenesysUserId,
            beforeValue: null,
            afterValue: $"LaunchId={launch.GenesysScreenPopLaunchId};TargetPath={launch.TargetPath};Outcome={refusal?.ToString() ?? "SignedIn"}",
            correlationId: Guid.NewGuid(),
            cancellationToken);

        if (!await launchStore.SaveChangesAsync(cancellationToken))
        {
            // A concurrent redemption of the same token committed first.
            return GenesysScreenPopRedeemResult.Failure(GenesysScreenPopRedeemOutcome.AlreadyUsed);
        }

        if (refusal is { } refused)
        {
            return GenesysScreenPopRedeemResult.Failure(refused);
        }

        // The ordinary TigerCS session — the same token, claims and roles a
        // password login issues (AuthenticationAppService.LoginAsync).
        var agent = resolution.Agent!;
        var displayName = agent.DisplayName ?? agent.UserName ?? agent.GenesysUserId;
        var roles = resolution.Roles ?? [];
        var primary = await departmentAssignments.GetPrimaryAsync(agent.UserId, cancellationToken);
        // The stamp the token is bound to — the same binding a password login
        // gets, so a Screen Pop session also ends when the password changes.
        var securityStamp = await accountManager.GetSecurityStampAsync(agent.UserId, cancellationToken) ?? string.Empty;
        var issued = tokenService.CreateAccessToken(agent.UserId, displayName, roles, securityStamp);

        return new GenesysScreenPopRedeemResult(
            GenesysScreenPopRedeemOutcome.SignedIn,
            new LoginResponseDto(issued.AccessToken, issued.ExpiresAtUtc, agent.UserId, displayName, roles, primary?.DepartmentId),
            launch.TargetPath);
    }

    /// <summary>SHA-256 of the token, lower-case hex — the only form a token is ever stored or looked up in.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Landing priority: the conversation's ticket → the named ticket → the caller's customer lookup → the Tickets list.</summary>
    private async Task<(string Path, long? TicketId)> ChooseLandingAsync(
        long? conversationTicketId, GenesysScreenPopRequestDto request, CancellationToken cancellationToken)
    {
        if (conversationTicketId is { } fromConversation)
        {
            return ($"{TicketsListPath}/{fromConversation}", fromConversation);
        }

        if (request.TicketId is { } requested && requested > 0
            && await ticketRepository.GetByIdAsync(requested, cancellationToken) is not null)
        {
            return ($"{TicketsListPath}/{requested}", requested);
        }

        // Genesys supplies ANI as a telephony address ("tel:+971…"); the lookup page searches by number.
        if (TigerCS.Domain.Modules.Ticketing.CustomerPhoneNumber.FromTelephonyAddress(request.CustomerPhone) is { } screenPopPhone)
        {
            var path = $"/Customers/Lookup?phoneNumber={Uri.EscapeDataString(screenPopPhone)}";
            if (GenesysScreenPopLaunch.IsAppRelativePath(path))
            {
                return (path, null);
            }
        }

        return (TicketsListPath, null);
    }

    private bool TryGetWebBaseUrl(out string baseUrl)
    {
        baseUrl = string.Empty;
        if (!Uri.TryCreate(options.ScreenPopWebBaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }
}
