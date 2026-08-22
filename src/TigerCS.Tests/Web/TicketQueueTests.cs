using System.Net;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>Filtering, sorting, pagination and the queue's status indicators.</summary>
public sealed class TicketQueueTests
{
    private static readonly Guid OwnerId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static TigerCsWebFactory SignedInAs(params string[] roles)
    {
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(roles))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(roles));

        return factory;
    }

    private static async Task<HttpClient> SignInAsync(TigerCsWebFactory factory)
    {
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");
        return client;
    }

    [Fact]
    public async Task Queue_RendersTicketsWithTheirNumberAndSummary()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.OK, TestData.Sla())
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(1, TestData.Summary()));

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets");

        await WebTestHelpers.AssertContainsAsync(response, "CS-2026-0001");
        await WebTestHelpers.AssertContainsAsync(response, "Air conditioning");
    }

    [Fact]
    public async Task Queue_PassesEveryFilterThroughToTheApi()
    {
        using var factory = SignedInAs(Roles.CsManager);
        factory.Api.On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(0));

        var client = await SignInAsync(factory);

        await client.GetAsync(
            "/Tickets?search=cooling&ticketStatus=Open&priorityId=1"
            + "&verificationStatus=PendingCrmVerification&departmentId=3&categoryId=7"
            + $"&ownerEmployeeId={OwnerId}");

        var queueCall = factory.Api.Requests.Last(r => r.Path == "/api/tickets");
        var query = queueCall.Query ?? string.Empty;

        Assert.Contains("search=cooling", query, StringComparison.Ordinal);
        Assert.Contains("ticketStatus=Open", query, StringComparison.Ordinal);
        Assert.Contains("priorityId=1", query, StringComparison.Ordinal);
        Assert.Contains("verificationStatus=PendingCrmVerification", query, StringComparison.Ordinal);
        Assert.Contains("departmentId=3", query, StringComparison.Ordinal);
        Assert.Contains("categoryId=7", query, StringComparison.Ordinal);
        Assert.Contains($"ownerEmployeeId={OwnerId}", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Queue_PagesServerSide()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api.On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(0));

        var client = await SignInAsync(factory);

        await client.GetAsync("/Tickets?pageNumber=3&pageSize=50");

        var query = factory.Api.Requests.Last(r => r.Path == "/api/tickets").Query ?? string.Empty;
        Assert.Contains("page=3", query, StringComparison.Ordinal);
        Assert.Contains("pageSize=50", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queue_ClampsAnUnsupportedPageSizeRatherThanForwardingIt()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api.On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(0));

        var client = await SignInAsync(factory);

        await client.GetAsync("/Tickets?pageSize=100000");

        var query = factory.Api.Requests.Last(r => r.Path == "/api/tickets").Query ?? string.Empty;
        Assert.Contains("pageSize=25", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queue_SortsThroughTheApiAndReportsSortStateForScreenReaders()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.OK, TestData.Sla())
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(1, TestData.Summary()));

        var client = await SignInAsync(factory);

        var response = await client.GetAsync("/Tickets?sortBy=priority&sortDir=asc");

        var query = factory.Api.Requests.Last(r => r.Path == "/api/tickets").Query ?? string.Empty;
        Assert.Contains("sortBy=priority", query, StringComparison.Ordinal);
        Assert.Contains("sortDir=asc", query, StringComparison.Ordinal);

        await WebTestHelpers.AssertContainsAsync(response, "aria-sort=\"ascending\"");
    }

    [Fact]
    public async Task Queue_ShowsPendingCrmWithTextAndAGlyphNotColourAlone()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.OK, TestData.Sla())
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK,
                TestData.Queue(1, TestData.Summary(verification: "PendingCrmVerification")));

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets");

        await WebTestHelpers.AssertContainsAsync(response, "Pending CRM");
        await WebTestHelpers.AssertContainsAsync(response, "awaiting CRM reconciliation");
        await WebTestHelpers.AssertContainsAsync(response, "◑");
    }

    [Fact]
    public async Task Queue_ShowsABreachedSlaFromTheBreachFlags()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.OK,
                TestData.Sla(slaState: "Running", firstResponseBreached: true))
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(1, TestData.Summary()));

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets");

        // The flags are immutable once set, so a breach is reported from them
        // even while SlaState still reads Running.
        await WebTestHelpers.AssertContainsAsync(response, "SLA breached");
        await WebTestHelpers.AssertContainsAsync(response, "First response deadline breached");
    }

    [Fact]
    public async Task Queue_ShowsSlaUnknownRatherThanHealthyWhenTheSlaCallFails()
    {
        // The one failure mode that actually matters here: a breached ticket
        // must never render as fine because a lookup failed.
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.Forbidden)
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(1, TestData.Summary()));

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets");

        await WebTestHelpers.AssertContainsAsync(response, "SLA unknown");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "SLA met");
    }

    [Fact]
    public async Task Queue_MarksAClosedTicketAsClosed()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.OK, TestData.Sla(slaState: "Met"))
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK,
                TestData.Queue(1, TestData.Summary(status: "Closed")));

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets");

        await WebTestHelpers.AssertContainsAsync(response, "is-closed");
        await WebTestHelpers.AssertContainsAsync(response, "read-only");
    }

    [Fact]
    public async Task Queue_ResolvesOwnerNamesFromTheDepartmentRoster()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.OK, TestData.Sla())
            .On(HttpMethod.Get, "/api/departments/1/users", HttpStatusCode.OK,
                TestData.Roster(TestData.Member(OwnerId, "Layla Nasser", Roles.DepartmentEmployee)))
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK,
                TestData.Queue(1, TestData.Summary(owner: OwnerId)));

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets");

        await WebTestHelpers.AssertContainsAsync(response, "Layla Nasser");
    }

    [Fact]
    public async Task Queue_ShowsUnassignedRatherThanABlankOwnerCell()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.OK, TestData.Sla())
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(1, TestData.Summary()));

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets");

        await WebTestHelpers.AssertContainsAsync(response, "Unassigned");
    }

    [Fact]
    public async Task Queue_WithNoResults_OffersToClearTheFilters()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api.On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(0));

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets?search=nothing-matches-this");

        await WebTestHelpers.AssertContainsAsync(response, "No tickets match these filters");
        await WebTestHelpers.AssertContainsAsync(response, "Clear filters");
    }

    [Fact]
    public async Task Queue_WhenTheListCallFails_ShowsAFullPanelErrorWithRetry()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api.On(HttpMethod.Get, "/api/tickets", HttpStatusCode.BadGateway);

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "The queue could not be loaded");
        await WebTestHelpers.AssertContainsAsync(response, "Try again");
    }

    [Fact]
    public async Task Queue_PutsTheWholeFilterStateOnEveryTicketLinkSoReturningPreservesIt()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.OK, TestData.Sla())
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(1, TestData.Summary()));

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets?ticketStatus=Open&priorityId=1&pageNumber=2");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("returnUrl=", html, StringComparison.Ordinal);
        Assert.Contains("ticketStatus%3DOpen", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("priorityId%3D1", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Queue_UsesRealTableSemanticsForScreenReaders()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.OK, TestData.Sla())
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(1, TestData.Summary()));

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("<table", html, StringComparison.Ordinal);
        Assert.Contains("<caption", html, StringComparison.Ordinal);
        Assert.Contains("scope=\"col\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queue_WhenTheTokenIsRejected_SendsTheUserBackToSignIn()
    {
        using var factory = SignedInAs(Roles.CsAgent);
        factory.Api.On(HttpMethod.Get, "/api/tickets", HttpStatusCode.Unauthorized);

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets");

        WebTestHelpers.AssertRedirectTo(response, "/Account/Login");
        Assert.Contains("expired", response.Headers.Location?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }
}
