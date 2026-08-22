using System.Net;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// The ticket detail screen: what it renders, which actions each role is
/// offered, and how it behaves when a ticket is closed or provisional.
/// </summary>
public sealed class TicketDetailsTests
{
    private static readonly Guid SelfId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static TigerCsWebFactory WithTicket(
        TicketDetailDto ticket,
        TicketSlaSummaryResponseDto? sla = null,
        params string[] roles)
    {
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(roles))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(roles))
            .On(HttpMethod.Get, $"/api/tickets/{ticket.TicketId}/sla", HttpStatusCode.OK, sla ?? TestData.Sla())
            .On(HttpMethod.Get, $"/api/tickets/{ticket.TicketId}/escalations", HttpStatusCode.OK,
                Array.Empty<TicketEscalationResponseDto>())
            .On(HttpMethod.Get, $"/api/tickets/{ticket.TicketId}/notes", HttpStatusCode.OK, TestData.Notes())
            .On(HttpMethod.Get, $"/api/departments/{ticket.CurrentDepartmentId}/users", HttpStatusCode.OK,
                TestData.Roster(
                    TestData.Member(SelfId, "Test Agent", roles),
                    TestData.Member(OtherId, "Layla Nasser", Roles.DepartmentEmployee)))
            .On(HttpMethod.Get, $"/api/tickets/{ticket.TicketId}", HttpStatusCode.OK, ticket);

        return factory;
    }

    private static async Task<HttpClient> SignInAsync(TigerCsWebFactory factory)
    {
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "user", "Test-Password-1!");
        return client;
    }

    [Fact]
    public async Task Details_RendersTheTicketsCoreFields()
    {
        using var factory = WithTicket(TestData.Detail(), roles: Roles.CsAgent);
        var client = await SignInAsync(factory);

        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "CS-2026-0001");
        await WebTestHelpers.AssertContainsAsync(response, "Air conditioning");
        await WebTestHelpers.AssertContainsAsync(response, "Medium");
        await WebTestHelpers.AssertContainsAsync(response, "Open");
        await WebTestHelpers.AssertContainsAsync(response, "Customer Service");
    }

    [Fact]
    public async Task Details_RendersTheSlaPanelWithBothDeadlines()
    {
        using var factory = WithTicket(TestData.Detail(), roles: Roles.CsAgent);
        var client = await SignInAsync(factory);

        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "Service level");
        await WebTestHelpers.AssertContainsAsync(response, "First response");
        await WebTestHelpers.AssertContainsAsync(response, "Resolution");
        await WebTestHelpers.AssertContainsAsync(response, "2026-08-20 11:15");
    }

    [Fact]
    public async Task Details_ShowsABreachedSlaProminently()
    {
        using var factory = WithTicket(
            TestData.Detail(slaState: "Breached"),
            TestData.Sla(slaState: "Breached", resolutionBreached: true),
            Roles.CsAgent);

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "SLA breached");
        await WebTestHelpers.AssertContainsAsync(response, "resolution deadline was missed");
    }

    [Fact]
    public async Task Details_ShowsAPendingCrmBannerOnAProvisionalTicket()
    {
        using var factory = WithTicket(
            TestData.Detail(verification: "PendingCrmVerification"), roles: Roles.CsAgent);

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "pending CRM verification");
        await WebTestHelpers.AssertContainsAsync(response, "unverified");
    }

    [Fact]
    public async Task Details_ShowsTheEscalationLevelWhenTheTicketIsEscalated()
    {
        using var factory = WithTicket(
            TestData.Detail(escalationLevel: "Level2"),
            TestData.Sla(escalationLevel: "Level2"),
            Roles.CsAgent);

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "Esc L2");
        await WebTestHelpers.AssertContainsAsync(response, "Escalated to level 2");
    }

    [Fact]
    public async Task Details_ShowsNotesInTheActivityArea()
    {
        using var factory = WithTicket(TestData.Detail(), roles: Roles.CsAgent);
        factory.Api.Once(
            HttpMethod.Get, "/api/tickets/1/notes", HttpStatusCode.OK,
            TestData.Notes(new TicketNoteResponseDto(
                9, 1, "Technician dispatched, ETA 20 minutes.", OtherId,
                new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc))));

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "Technician dispatched");
        await WebTestHelpers.AssertContainsAsync(response, "Layla Nasser");
    }

    [Fact]
    public async Task Details_StillRendersWhenTheSlaPanelCannotBeRead()
    {
        // Each side panel fails independently: a ticket that renders without
        // its SLA panel is far better than one that does not render.
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/tickets/1/sla", HttpStatusCode.BadGateway)
            .On(HttpMethod.Get, "/api/tickets/1/escalations", HttpStatusCode.BadGateway)
            .On(HttpMethod.Get, "/api/tickets/1/notes", HttpStatusCode.BadGateway)
            .On(HttpMethod.Get, "/api/departments/1/users", HttpStatusCode.BadGateway)
            .On(HttpMethod.Get, "/api/tickets/1", HttpStatusCode.OK, TestData.Detail());

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets/Details/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "CS-2026-0001");
        await WebTestHelpers.AssertContainsAsync(response, "could not be loaded for this ticket");
    }

    // ---------------------------------------------------------- closed ticket

    [Fact]
    public async Task ClosedTicket_IsMarkedReadOnly()
    {
        using var factory = WithTicket(TestData.Detail(status: "Closed"), roles: Roles.CsManager);
        var client = await SignInAsync(factory);

        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "closed and read-only");
    }

    [Fact]
    public async Task ClosedTicket_OffersNoActionsAtAll()
    {
        // CS Manager holds assign, transfer, status-change and close authority,
        // so this is the strongest case: every one of them must disappear once
        // the ticket is Closed, because the API refuses all of them with 422.
        using var factory = WithTicket(TestData.Detail(status: "Closed"), roles: Roles.CsManager);
        var client = await SignInAsync(factory);

        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertDoesNotContainAsync(response, "data-dialog-open=\"dlg-assign\"");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "data-dialog-open=\"dlg-transfer\"");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "data-dialog-open=\"dlg-status\"");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "data-dialog-open=\"dlg-close\"");
    }

    [Fact]
    public async Task ClosedTicket_OffersNoNoteComposer()
    {
        using var factory = WithTicket(TestData.Detail(status: "Closed"), roles: Roles.CsAgent);
        var client = await SignInAsync(factory);

        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertDoesNotContainAsync(response, "Add a note");
    }

    // ------------------------------------------------------ role visibility

    [Fact]
    public async Task CsManager_IsOfferedTransfer()
    {
        using var factory = WithTicket(TestData.Detail(), roles: Roles.CsManager);
        var client = await SignInAsync(factory);

        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "dlg-transfer");
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.DepartmentEmployee)]
    public async Task OnlyCsManager_IsOfferedTransfer(string role)
    {
        // TicketRoleSets.Transfer is CS Manager only - every other role,
        // including the two that keep Assign authority, gets 403.
        using var factory = WithTicket(TestData.Detail(), roles: role);
        var client = await SignInAsync(factory);

        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertDoesNotContainAsync(response, "dlg-transfer");
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.DepartmentEmployee)]
    public async Task RolesWithoutAssignmentAuthority_AreNotOfferedAssign(string role)
    {
        // The PR correction removed self-claim from CS Agent and Department
        // Employee entirely: they hold no assignment capability at all.
        using var factory = WithTicket(TestData.Detail(), roles: role);
        var client = await SignInAsync(factory);

        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertDoesNotContainAsync(response, "dlg-assign");
    }

    [Theory]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.CsManager)]
    public async Task RolesWithAssignmentAuthority_AreOfferedAssign(string role)
    {
        using var factory = WithTicket(TestData.Detail(), roles: role);
        var client = await SignInAsync(factory);

        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "dlg-assign");
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    public async Task CsLayerRoles_AreOfferedCloseButNotResolve(string role)
    {
        // ISSUE-022's split: CS closes, the department resolves.
        using var factory = WithTicket(TestData.Detail(), roles: role);
        var client = await SignInAsync(factory);

        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "dlg-close");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "dlg-resolve");
    }

    [Fact]
    public async Task DepartmentHead_IsOfferedResolveButNotClose()
    {
        using var factory = WithTicket(TestData.Detail(), roles: Roles.DepartmentHead);
        var client = await SignInAsync(factory);

        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "dlg-resolve");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "dlg-close");
    }

    [Fact]
    public async Task DepartmentEmployee_IsOfferedResolveOnlyOnTheirOwnTicket()
    {
        using var owned = WithTicket(TestData.Detail(owner: SelfId), roles: Roles.DepartmentEmployee);
        var ownedResponse = await (await SignInAsync(owned)).GetAsync("/Tickets/Details/1");
        await WebTestHelpers.AssertContainsAsync(ownedResponse, "dlg-resolve");

        using var someoneElses = WithTicket(TestData.Detail(owner: OtherId), roles: Roles.DepartmentEmployee);
        var otherResponse = await (await SignInAsync(someoneElses)).GetAsync("/Tickets/Details/1");
        await WebTestHelpers.AssertDoesNotContainAsync(otherResponse, "dlg-resolve");
    }

    [Fact]
    public async Task Reconcile_IsOfferedOnlyOnAPendingCrmTicketAndOnlyToCreatingRoles()
    {
        using var provisional = WithTicket(
            TestData.Detail(verification: "PendingCrmVerification"), roles: Roles.CsAgent);
        var provisionalResponse = await (await SignInAsync(provisional)).GetAsync("/Tickets/Details/1");
        await WebTestHelpers.AssertContainsAsync(provisionalResponse, "dlg-reconcile");

        using var verified = WithTicket(TestData.Detail(), roles: Roles.CsAgent);
        var verifiedResponse = await (await SignInAsync(verified)).GetAsync("/Tickets/Details/1");
        await WebTestHelpers.AssertDoesNotContainAsync(verifiedResponse, "dlg-reconcile");

        using var wrongRole = WithTicket(
            TestData.Detail(verification: "PendingCrmVerification"), roles: Roles.DepartmentEmployee);
        var wrongRoleResponse = await (await SignInAsync(wrongRole)).GetAsync("/Tickets/Details/1");
        await WebTestHelpers.AssertDoesNotContainAsync(wrongRoleResponse, "dlg-reconcile");
    }

    [Fact]
    public async Task SystemAdministrator_IsOfferedEveryAction()
    {
        // ADR-0024's central override: the role appears in none of the server's
        // operational role sets and passes all of them anyway.
        using var factory = WithTicket(
            TestData.Detail(verification: "PendingCrmVerification"), roles: Roles.SystemAdministrator);

        var client = await SignInAsync(factory);
        var response = await client.GetAsync("/Tickets/Details/1");

        await WebTestHelpers.AssertContainsAsync(response, "dlg-assign");
        await WebTestHelpers.AssertContainsAsync(response, "dlg-transfer");
        await WebTestHelpers.AssertContainsAsync(response, "dlg-status");
        await WebTestHelpers.AssertContainsAsync(response, "dlg-resolve");
        await WebTestHelpers.AssertContainsAsync(response, "dlg-close");
        await WebTestHelpers.AssertContainsAsync(response, "dlg-reconcile");
    }

    // --------------------------------------------------------------- actions

    [Fact]
    public async Task AddNote_PostsToTheApiAndConfirms()
    {
        using var factory = WithTicket(TestData.Detail(), roles: Roles.CsAgent);
        factory.Api.On(
            HttpMethod.Post, "/api/tickets/1/notes", HttpStatusCode.Created,
            new TicketNoteResponseDto(10, 1, "Called the customer back.", SelfId, DateTime.UtcNow));

        var client = await SignInAsync(factory);

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/Details/1", "/Tickets/Details/1?handler=AddNote",
            new KeyValuePair<string, string>("noteText", "Called the customer back."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "Note added");
    }

    [Fact]
    public async Task AddNote_WithEmptyText_IsRejectedWithoutCallingTheApi()
    {
        using var factory = WithTicket(TestData.Detail(), roles: Roles.CsAgent);
        var client = await SignInAsync(factory);

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/Details/1", "/Tickets/Details/1?handler=AddNote",
            new KeyValuePair<string, string>("noteText", "   "));

        await WebTestHelpers.AssertContainsAsync(response, "Write something");
        Assert.Equal(0, factory.Api.CountFor(HttpMethod.Post, "/api/tickets/1/notes"));
    }

    [Fact]
    public async Task Close_SendsTheRowVersionBackToTheApi()
    {
        using var factory = WithTicket(TestData.Detail(status: "Resolved"), roles: Roles.CsManager);
        factory.Api.On(
            HttpMethod.Post, "/api/tickets/1/close", HttpStatusCode.OK,
            TestData.Detail(status: "Closed"));

        var client = await SignInAsync(factory);

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/Details/1", "/Tickets/Details/1?handler=Close",
            new KeyValuePair<string, string>("rowVersion", TestData.RowVersion));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var closeCall = factory.Api.Requests.Last(r => r.Path == "/api/tickets/1/close");
        Assert.NotNull(closeCall.Body);
        Assert.Contains("rowVersion", closeCall.Body!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Details_PostWithoutAnAntiforgeryToken_IsRejected()
    {
        using var factory = WithTicket(TestData.Detail(), roles: Roles.CsAgent);
        var client = await SignInAsync(factory);

        var response = await client.PostAsync(
            "/Tickets/Details/1?handler=AddNote",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("noteText", "forged")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Api.CountFor(HttpMethod.Post, "/api/tickets/1/notes"));
    }
}
