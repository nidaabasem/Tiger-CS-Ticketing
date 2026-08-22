using System.Net;
using System.Text.RegularExpressions;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Content-Security-Policy this application sends is strict - no
/// <c>'unsafe-inline'</c> for either scripts or styles - and these tests keep
/// the markup compatible with it.
///
/// <para>
/// <b>Why this needs a test at all.</b> A browser silently drops an inline
/// <c>style="…"</c> attribute under <c>style-src 'self'</c>: nothing errors,
/// nothing 500s, the page just renders wrong. That is exactly the kind of
/// defect that survives a code review and a passing test suite, so it is
/// asserted here rather than left to discipline. The same applies to inline
/// <c>&lt;script&gt;</c> blocks and <c>on*</c> handlers under
/// <c>script-src 'self'</c>.
/// </para>
/// </summary>
public sealed partial class ContentSecurityPolicyTests
{
    [GeneratedRegex(@"<[^>]+\sstyle\s*=\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex InlineStyleAttributeRegex();

    [GeneratedRegex(@"<[^>]+\s(on[a-z]+)\s*=\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex InlineEventHandlerRegex();

    [GeneratedRegex("<script(?![^>]*\\ssrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScriptBlockRegex();

    private static TigerCsWebFactory SignedIn()
    {
        var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsSupervisor))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsSupervisor))
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.OK,
                TestData.Sla(slaState: "Breached", firstResponseBreached: true, escalationLevel: "Level2"))
            .On(HttpMethod.Get, "/api/tickets/1/escalations", HttpStatusCode.OK,
                Array.Empty<TicketEscalationResponseDto>())
            .On(HttpMethod.Get, "/api/tickets/1/notes", HttpStatusCode.OK, TestData.Notes())
            .On(HttpMethod.Get, "/api/departments/1/users", HttpStatusCode.OK, TestData.Roster())
            .On(HttpMethod.Get, "/api/tickets/1", HttpStatusCode.OK, TestData.Detail())
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(1, TestData.Summary()))
            .On(HttpMethod.Post, "/api/intake-records", HttpStatusCode.Created, TestData.Intake())
            .On(HttpMethod.Get, "/api/crm/units/search", HttpStatusCode.OK, new[] { TestData.Unit() });

        return factory;
    }

    public static TheoryData<string> Pages() =>
    [
        "/Account/Login",
        "/Tickets",
        "/Tickets/Details/1",
        "/Tickets/New/Intake",
        "/Error?status=500",
        "/Forbidden"
    ];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task NoPageUsesAnInlineStyleAttribute(string path)
    {
        using var factory = SignedIn();
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "supervisor", "Test-Password-1!");

        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        var matches = InlineStyleAttributeRegex().Matches(html)
            .Select(m => m.Value)
            .ToArray();

        Assert.True(
            matches.Length == 0,
            $"'{path}' renders inline style attributes, which style-src 'self' silently drops. "
            + $"Move them into tiger.css. Offending: {string.Join(" | ", matches.Take(5))}");
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task NoPageUsesAnInlineScriptOrEventHandler(string path)
    {
        using var factory = SignedIn();
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "supervisor", "Test-Password-1!");

        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(
            InlineScriptBlockRegex().Matches(html).Count == 0,
            $"'{path}' renders an inline <script> block, which script-src 'self' blocks.");

        var handlers = InlineEventHandlerRegex().Matches(html)
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.True(
            handlers.Length == 0,
            $"'{path}' renders inline event handlers ({string.Join(", ", handlers)}), which script-src 'self' blocks.");
    }

    [Fact]
    public async Task ThePolicyItselfStaysStrict()
    {
        using var factory = SignedIn();
        var client = factory.CreateWebClient();

        var response = await client.GetAsync("/Account/Login");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        // If a future change needs 'unsafe-inline', that is a decision to make
        // deliberately - not one to arrive at by a test being relaxed.
        Assert.Contains("style-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
    }
}
