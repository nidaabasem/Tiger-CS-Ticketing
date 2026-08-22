using System.Net;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// Every status code this API answers with, and what the UI does about it.
///
/// <para>
/// The point of this class is that none of these is treated as an unexpected
/// fault. A 403, 409, 410, 422 or 423 is a meaningful answer that the user has
/// to be able to act on, and a 502 is an operational condition, not a bug. All
/// of them must produce a readable page rather than an error screen or a
/// stack trace.
/// </para>
/// </summary>
public sealed class ApiErrorHandlingTests
{
    private static TigerCsWebFactory WithTicketAndAction(HttpStatusCode actionStatus, string problemSlug, string title)
    {
        var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsManager))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsManager))
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.OK, TestData.Sla())
            .On(HttpMethod.Get, "/api/tickets/1/escalations", HttpStatusCode.OK,
                Array.Empty<TicketEscalationResponseDto>())
            .On(HttpMethod.Get, "/api/tickets/1/notes", HttpStatusCode.OK, TestData.Notes())
            .On(HttpMethod.Get, "/api/departments/1/users", HttpStatusCode.OK, TestData.Roster())
            .On(HttpMethod.Get, "/api/tickets/1", HttpStatusCode.OK, TestData.Detail(status: "Resolved"))
            .OnProblem(HttpMethod.Post, "/api/tickets/1/close", actionStatus, problemSlug, title);

        return factory;
    }

    private static async Task<HttpClient> SignInAsync(TigerCsWebFactory factory)
    {
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "manager", "Test-Password-1!");
        return client;
    }

    [Fact]
    public async Task Api401_OnAPageLoad_SendsTheUserBackToSignIn()
    {
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/tickets/1", HttpStatusCode.Unauthorized);

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets/Details/1");

        WebTestHelpers.AssertRedirectTo(response, "/Account/Login");
    }

    [Fact]
    public async Task Api403_OnATicket_ExplainsAccessRatherThanShowingABlankPage()
    {
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.DepartmentEmployee))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.DepartmentEmployee))
            .On(HttpMethod.Get, "/api/tickets/1", HttpStatusCode.Forbidden);

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets/Details/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "don't have access");
        await WebTestHelpers.AssertContainsAsync(response, "roles and");
    }

    [Fact]
    public async Task Api404_OnATicket_IsPresentedTheSameWayAsNoAccess()
    {
        // The API deliberately conflates the two so a 404 does not confirm that
        // a ticket exists. This tier must not undo that by wording them apart.
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/tickets/1", HttpStatusCode.NotFound);

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "not found, or you don't have access");
    }

    [Fact]
    public async Task Api409_OnAnAction_ExplainsTheConflictAndReloadsTheTicket()
    {
        // A concurrency conflict is recoverable only if the user gets fresh
        // state back, including a fresh rowVersion to retry against.
        using var factory = WithTicketAndAction(
            HttpStatusCode.Conflict, "ticket-concurrently-modified", "Ticket concurrently modified");

        var client = await SignInAsync(factory);

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/Details/1", "/Tickets/Details/1?handler=Close",
            new KeyValuePair<string, string>("rowVersion", TestData.RowVersion));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "Ticket concurrently modified");
        await WebTestHelpers.AssertContainsAsync(response, "has been reloaded");
        await WebTestHelpers.AssertContainsAsync(response, "CS-2026-0001");
    }

    [Fact]
    public async Task Api410_OnTicketCreation_TellsTheAgentToVerifyAgain()
    {
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent))
            .On(HttpMethod.Post, "/api/intake-records", HttpStatusCode.Created, TestData.Intake())
            .On(HttpMethod.Get, "/api/crm/units/search", HttpStatusCode.OK, new[] { TestData.Unit() })
            .On(HttpMethod.Get, "/api/crm/units/CRM-UNIT-42/contacts", HttpStatusCode.OK,
                new[] { TestData.Contact() })
            .On(HttpMethod.Post, "/api/verification-sessions", HttpStatusCode.Created, TestData.Session())
            .OnProblem(HttpMethod.Post, "/api/tickets", HttpStatusCode.Gone,
                "verification-session-expired", "Verification session expired");

        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");

        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Intake", "/Tickets/New/Intake",
            new KeyValuePair<string, string>("RawUnitNumberEntered", "A-1402"));
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Unit", "/Tickets/New/Unit?handler=Select",
            new KeyValuePair<string, string>("unitReferenceId", "42"),
            new KeyValuePair<string, string>("crmUnitId", "CRM-UNIT-42"),
            new KeyValuePair<string, string>("unitLabel", "A-1402"));
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Contact", "/Tickets/New/Contact?handler=Confirm",
            new KeyValuePair<string, string>("contactReferenceId", "84"),
            new KeyValuePair<string, string>("confirmed", "true"));

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Review", "/Tickets/New/Review",
            new KeyValuePair<string, string>("CategoryId", "7"),
            new KeyValuePair<string, string>("PriorityId", "3"),
            new KeyValuePair<string, string>("RequestSummary", "Not cooling."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The API's own title is shown, and the recovery is spelled out:
        // retrying is pointless once a session is gone, so the only useful
        // next step is to verify again.
        await WebTestHelpers.AssertContainsAsync(response, "Verification session expired");
        await WebTestHelpers.AssertContainsAsync(response, "Verify the caller again");
    }

    [Fact]
    public async Task Api422_OnAClosedTicketAction_ExplainsTheStateRuleWithoutFailingThePage()
    {
        using var factory = WithTicketAndAction(
            HttpStatusCode.UnprocessableEntity, "ticket-closed", "Ticket is closed");

        var client = await SignInAsync(factory);

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/Details/1", "/Tickets/Details/1?handler=Close",
            new KeyValuePair<string, string>("rowVersion", TestData.RowVersion));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "Ticket is closed");
    }

    [Fact]
    public async Task Api403_OnAnAction_ShowsAPermissionMessageAndKeepsTheTicketReadable()
    {
        // The action bar filters by role, but the server decides again - and a
        // hidden button is not authorization. When it refuses, the user must
        // see why and still be looking at the ticket.
        using var factory = WithTicketAndAction(HttpStatusCode.Forbidden, string.Empty, string.Empty);

        var client = await SignInAsync(factory);

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/Details/1", "/Tickets/Details/1?handler=Close",
            new KeyValuePair<string, string>("rowVersion", TestData.RowVersion));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "do not have permission");
        await WebTestHelpers.AssertContainsAsync(response, "CS-2026-0001");
    }

    [Fact]
    public async Task Api423_OnSignIn_ShowsTheLockedState()
    {
        using var factory = new TigerCsWebFactory();
        factory.Api.OnProblem(
            HttpMethod.Post, "/api/auth/login", HttpStatusCode.Locked, "account-locked", "Account locked");

        var client = factory.CreateWebClient();
        var response = await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");

        await WebTestHelpers.AssertContainsAsync(response, "Account locked");
    }

    [Fact]
    public async Task Api502_OnTheQueue_ShowsServiceUnavailableNotAStackTrace()
    {
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent))
            .OnProblem(HttpMethod.Get, "/api/tickets", HttpStatusCode.BadGateway,
                "crm-unavailable", "CRM is currently unavailable");

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "could not be loaded");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "Exception");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "at TigerCS.");
    }

    [Fact]
    public async Task WhenTheApiCannotBeReachedAtAll_TheUserGetsAReadablePageNotAnException()
    {
        // No route is registered, so the stub answers 501 - standing in for any
        // response this application cannot interpret.
        using var factory = new TigerCsWebFactory();
        factory.Api.On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent));

        var client = factory.CreateWebClient();
        var response = await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");

        // Sign-in still succeeds: the profile call is not allowed to break it,
        // because the token is valid regardless and the API remains the authority.
        WebTestHelpers.AssertRedirectTo(response, "/Tickets");
    }

    [Fact]
    public async Task AnErrorPage_NeverLeaksExceptionDetailToTheBrowser()
    {
        using var factory = new TigerCsWebFactory();
        var client = factory.CreateWebClient();

        var response = await client.GetAsync("/Error?status=500");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "Something went wrong");

        // A correlation id is offered instead, so support can find the log line.
        await WebTestHelpers.AssertContainsAsync(response, "Reference");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "StackTrace");
    }

    [Fact]
    public async Task NoResponseEverContainsTheAccessToken()
    {
        // The whole point of the cookie model. If this ever fails, the token is
        // reaching the browser somewhere it can be read.
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.OK, TestData.Sla())
            .On(HttpMethod.Get, "/api/tickets/1/escalations", HttpStatusCode.OK,
                Array.Empty<TicketEscalationResponseDto>())
            .On(HttpMethod.Get, "/api/tickets/1/notes", HttpStatusCode.OK, TestData.Notes())
            .On(HttpMethod.Get, "/api/departments/1/users", HttpStatusCode.OK, TestData.Roster())
            .On(HttpMethod.Get, "/api/tickets/1", HttpStatusCode.OK, TestData.Detail())
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(1, TestData.Summary()));

        var client = await SignInAsync(factory);

        foreach (var path in new[] { "/Tickets", "/Tickets/Details/1", "/Tickets/New/Intake" })
        {
            var response = await client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain(
                "header.payload.signature", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryOutboundApiCall_CarriesTheBearerTokenAndACorrelationId()
    {
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(0));

        var client = await SignInAsync(factory);
        await client.GetAsync("/Tickets");

        // The queue call is made on behalf of a signed-in user, so it must be
        // authenticated - the delegating handler is what guarantees that,
        // rather than each typed client remembering to do it.
        var queueCall = Assert.Single(factory.Api.Requests, r => r.Path == "/api/tickets");

        Assert.Equal("Bearer header.payload.signature", queueCall.Authorization);
        Assert.False(string.IsNullOrWhiteSpace(queueCall.CorrelationId));

        // The sign-in call itself is the one that must NOT carry a token:
        // there is no session yet.
        var loginCall = Assert.Single(factory.Api.Requests, r => r.Path == "/api/auth/login");
        Assert.Null(loginCall.Authorization);
    }
}
