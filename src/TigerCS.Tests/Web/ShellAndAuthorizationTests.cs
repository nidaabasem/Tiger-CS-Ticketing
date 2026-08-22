using System.Net;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// The authenticated shell: who gets redirected, what each role is offered,
/// and what the command bar shows.
/// </summary>
public sealed class ShellAndAuthorizationTests
{
    private static TigerCsWebFactory SignedInAs(params string[] roles)
    {
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(roles))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(roles))
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(0));

        return factory;
    }

    [Theory]
    [InlineData("/Tickets")]
    [InlineData("/Tickets/Details/1")]
    [InlineData("/Tickets/New/Intake")]
    [InlineData("/")]
    public async Task AnonymousUser_IsRedirectedToLogin(string path)
    {
        using var factory = new TigerCsWebFactory();
        var client = factory.CreateWebClient();

        var response = await client.GetAsync(path);

        WebTestHelpers.AssertRedirectTo(response, "/Account/Login");
    }

    [Fact]
    public async Task AnonymousRedirect_CarriesTheOriginalPathSoSignInReturnsThere()
    {
        using var factory = new TigerCsWebFactory();
        var client = factory.CreateWebClient();

        var response = await client.GetAsync("/Tickets/Details/42");

        var location = response.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains("returnUrl", location, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Details", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Shell_ShowsTheSignedInUsersNameAndRole()
    {
        using var factory = SignedInAs(Roles.CsSupervisor);
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "supervisor", "Test-Password-1!");

        var response = await client.GetAsync("/Tickets");

        await WebTestHelpers.AssertContainsAsync(response, "Test Agent");
        await WebTestHelpers.AssertContainsAsync(response, "CS Supervisor");
    }

    [Fact]
    public async Task Shell_OffersSignOut()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");

        var response = await client.GetAsync("/Tickets");

        await WebTestHelpers.AssertContainsAsync(response, "Sign out");
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.SystemAdministrator)]
    public async Task NewTicket_IsOfferedToRolesTheApiPermitsToCreate(string role)
    {
        using var factory = SignedInAs(role);
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "user", "Test-Password-1!");

        var response = await client.GetAsync("/Tickets");

        await WebTestHelpers.AssertContainsAsync(response, "New ticket");
    }

    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task NewTicket_IsNotOfferedToRolesTheApiRefuses(string role)
    {
        // The API's CustomerVerification policy is CS Agent and CS Supervisor
        // only (plus the System Administrator override). Offering the button to
        // anyone else would produce a 403 at the first step.
        using var factory = SignedInAs(role);
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "user", "Test-Password-1!");

        var response = await client.GetAsync("/Tickets");

        await WebTestHelpers.AssertDoesNotContainAsync(response, "+ New ticket");
    }

    [Fact]
    public async Task NewTicket_NavigatedToDirectlyByAnIneligibleRole_ExplainsRatherThanShowingAForm()
    {
        // Hiding the nav item is not authorization. A user who types the URL
        // still lands here, and the honest answer is to say why the action will
        // be refused - the API would refuse it regardless.
        using var factory = SignedInAs(Roles.DepartmentEmployee);
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "employee", "Test-Password-1!");

        var response = await client.GetAsync("/Tickets/New/Intake");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "You cannot create tickets");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "Record and continue");
    }

    [Fact]
    public async Task Shell_DoesNotOfferABreachedSlaFilter()
    {
        // GET /api/tickets has no slaState filter and TicketSummaryDto carries
        // no SLA field, so a "Breached SLA" destination could only filter one
        // page client-side and would silently under-report every other page.
        // This test is what stops one being added without the API support.
        using var factory = SignedInAs(Roles.CsManager);
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "manager", "Test-Password-1!");

        var response = await client.GetAsync("/Tickets");

        await WebTestHelpers.AssertDoesNotContainAsync(response, "Breached SLA");
    }

    [Fact]
    public async Task EveryPage_SendsConservativeSecurityHeaders()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");

        var response = await client.GetAsync("/Tickets");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);

        // No 'unsafe-inline': this application ships no inline script, and
        // allowing it would give away most of what a CSP is for.
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shell_MarksUpIsAccessible()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");

        var response = await client.GetAsync("/Tickets");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("<html lang=\"en\">", html, StringComparison.Ordinal);
        Assert.Contains("skip-link", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Primary\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"main\"", html, StringComparison.Ordinal);
    }
}
