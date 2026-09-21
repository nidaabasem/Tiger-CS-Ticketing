using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The pending-customer-interaction endpoints, exercised through the REAL
/// HTTP pipeline by callers who are <b>not</b> System Administrators.
///
/// <para>
/// <b>Why this file exists.</b> The controller used to carry
/// <c>[Authorize(Policy = PolicyNames.DepartmentScoped)]</c>, whose
/// requirement is resource-based: <c>DepartmentScopedHandler</c> is an
/// <c>AuthorizationHandler&lt;DepartmentScopedRequirement, int&gt;</c> and so
/// only runs when the authorization call supplies the department id as the
/// resource. Attribute authorization supplies the endpoint, never an
/// <c>int</c>, so that requirement was never satisfied and <i>every</i> caller
/// except the ADR-0024 override role received 403 — including the CS Agent
/// this work list exists for. The whole feature was unusable and no test
/// caught it, because the only coverage these four endpoints had was the
/// System Administrator suite, which passes through the override.
/// </para>
///
/// <para>
/// Every test below therefore uses an ordinary role, and the administrator
/// appears only to prove the override still works.
/// </para>
/// </summary>
public class PendingCustomerInteractionAuthorizationTests(TigerCsApiFactory factory) : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory = factory;

    private async Task<(HttpClient Client, Guid EmployeeId)> ClientForAsync(string role, int? departmentId = null)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        if (departmentId is { } department)
        {
            await _factory.AssignPrimaryDepartmentAsync(employeeId, department);
        }

        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    /// <summary>
    /// An AI website chat that ends with nobody having taken it — the exact
    /// journey the work list exists for. Seeded through the Genesys endpoints
    /// so the handoff is raised by the real code path, not inserted.
    /// </summary>
    private async Task<(long TicketId, long HandoffId, int DepartmentId)> WaitingInteractionAsync(HttpClient genesysClient)
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync(
            "Pending Auth " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var conversationId = "conv-auth-" + Guid.NewGuid().ToString("N")[..12];

        var created = await genesysClient.PostAsJsonAsync(
            "/api/genesys/tickets",
            new GenesysInquiryRequest(
                conversationId, "WebsiteChat",
                CustomerPhone: "+971500000077", CustomerName: "Ahmed Ali", DepartmentId: departmentId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ticket = await created.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>();

        // The bot could not finish and the conversation simply ended. No
        // Handoff block at all — TigerCS must raise the work itself.
        var ended = await genesysClient.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticket!.TicketId}",
            new GenesysTicketUpdateRequest(
                conversationId,
                Ended: new GenesysConversationEndPart(
                    DateTime.UtcNow, "CustomerDisconnect",
                    [
                        new GenesysTranscriptMessageRequest("Customer", DateTime.UtcNow.AddMinutes(-3), "I need an NOC."),
                        new GenesysTranscriptMessageRequest("VirtualAgent", DateTime.UtcNow.AddMinutes(-2), "Which unit?")
                    ])));
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);

        var update = await ended.Content.ReadFromJsonAsync<GenesysTicketUpdateResponse>();
        Assert.Equal("WaitingForAgent", update!.HandoffStatus);

        return (ticket.TicketId, update.TicketAgentHandoffId!.Value, departmentId);
    }

    // =====================================================================
    // The regression itself: ordinary roles must reach these endpoints
    // =====================================================================

    [Fact]
    public async Task CsAgent_CanListPendingInteractions()
    {
        var (genesys, _) = await ClientForAsync(Roles.CsAgent);
        var (_, handoffId, _) = await WaitingInteractionAsync(genesys);

        var (agent, _) = await ClientForAsync(Roles.CsAgent);
        var response = await agent.GetAsync("/api/pending-customer-interactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<AgentHandoffListResultDto>();
        Assert.Contains(list!.Items, i => i.TicketAgentHandoffId == handoffId);
    }

    [Fact]
    public async Task CsAgent_CanStartAndCompleteAPendingInteraction()
    {
        var (genesys, _) = await ClientForAsync(Roles.CsAgent);
        var (_, handoffId, departmentId) = await WaitingInteractionAsync(genesys);

        var (agent, agentId) = await ClientForAsync(Roles.CsAgent, departmentId);

        var started = await agent.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var afterStart = await started.Content.ReadFromJsonAsync<AgentHandoffDto>();
        Assert.Equal("InProgress", afterStart!.Status);
        Assert.Equal(agentId, afterStart.AssignedEmployeeId);

        var completed = await agent.PostAsJsonAsync(
            $"/api/pending-customer-interactions/{handoffId}/complete",
            new CompleteAgentHandoffRequestDto("Called the customer back."));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal("Completed", (await completed.Content.ReadFromJsonAsync<AgentHandoffDto>())!.Status);
    }

    [Fact]
    public async Task CsAgent_CanCancelAPendingInteraction()
    {
        var (genesys, _) = await ClientForAsync(Roles.CsAgent);
        var (_, handoffId, departmentId) = await WaitingInteractionAsync(genesys);

        var (agent, _) = await ClientForAsync(Roles.CsAgent, departmentId);

        var cancelled = await agent.PostAsJsonAsync(
            $"/api/pending-customer-interactions/{handoffId}/cancel",
            new CancelAgentHandoffRequestDto("The customer resolved it themselves."));

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("Cancelled", (await cancelled.Content.ReadFromJsonAsync<AgentHandoffDto>())!.Status);
    }

    [Fact]
    public async Task DepartmentEmployeeOfTheTicketsDepartment_CanActOnTheWork()
    {
        // Not a CS-layer role: this one reaches the work purely through
        // department membership, which is the other half of the visibility
        // rule the controller now relies on.
        var (genesys, _) = await ClientForAsync(Roles.CsAgent);
        var (_, handoffId, departmentId) = await WaitingInteractionAsync(genesys);

        var (worker, workerId) = await ClientForAsync(Roles.DepartmentEmployee, departmentId);

        var started = await worker.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { });

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(workerId, (await started.Content.ReadFromJsonAsync<AgentHandoffDto>())!.AssignedEmployeeId);
    }

    [Fact]
    public async Task DepartmentEmployeeOfAnotherDepartment_IsStillRefused()
    {
        // The fix must not become "everyone can act on everything": the
        // resource-level department check is now the whole gate, so it has to
        // actually refuse.
        var (genesys, _) = await ClientForAsync(Roles.CsAgent);
        var (_, handoffId, _) = await WaitingInteractionAsync(genesys);

        var otherDepartmentId = await _factory.CreateDepartmentAsync(
            "Elsewhere " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var (outsider, _) = await ClientForAsync(Roles.DepartmentEmployee, otherDepartmentId);

        var started = await outsider.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { });

        Assert.Equal(HttpStatusCode.Forbidden, started.StatusCode);
    }

    [Fact]
    public async Task SystemAdministrator_StillReachesTheWorkAcrossDepartments()
    {
        var (genesys, _) = await ClientForAsync(Roles.CsAgent);
        var (_, handoffId, _) = await WaitingInteractionAsync(genesys);

        // No department membership whatsoever — ADR-0024's override carries it.
        var (administrator, administratorId) = await ClientForAsync(Roles.SystemAdministrator);

        var started = await administrator.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { });

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(administratorId, (await started.Content.ReadFromJsonAsync<AgentHandoffDto>())!.AssignedEmployeeId);
    }

    // =====================================================================
    // Exclusive claim, over HTTP
    // =====================================================================

    [Fact]
    public async Task ASecondAgentStarting_Gets409_NamingTheHolder()
    {
        var (genesys, _) = await ClientForAsync(Roles.CsAgent);
        var (_, handoffId, departmentId) = await WaitingInteractionAsync(genesys);

        var (first, firstId) = await ClientForAsync(Roles.CsAgent, departmentId);
        var (second, _) = await ClientForAsync(Roles.CsAgent, departmentId);

        Assert.Equal(
            HttpStatusCode.OK,
            (await first.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { })).StatusCode);

        var loser = await second.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { });

        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        // The refusal names who has it, so the second agent can go and ask
        // them rather than guessing.
        Assert.Contains(firstId.ToString(), await loser.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheSameAgentStartingTwice_IsIdempotent()
    {
        var (genesys, _) = await ClientForAsync(Roles.CsAgent);
        var (_, handoffId, departmentId) = await WaitingInteractionAsync(genesys);

        var (agent, _) = await ClientForAsync(Roles.CsAgent, departmentId);

        var first = await agent.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { });
        var second = await agent.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var firstStartedAt = (await first.Content.ReadFromJsonAsync<AgentHandoffDto>())!.StartedAtUtc;
        var secondStartedAt = (await second.Content.ReadFromJsonAsync<AgentHandoffDto>())!.StartedAtUtc;
        Assert.Equal(firstStartedAt, secondStartedAt);
    }

    // =====================================================================
    // The ticket behind the work
    // =====================================================================

    [Fact]
    public async Task AcceptingTakesTheTicket_AndMovesItOpenToInProgress()
    {
        var (genesys, _) = await ClientForAsync(Roles.CsAgent);
        var (ticketId, handoffId, departmentId) = await WaitingInteractionAsync(genesys);

        var (agent, agentId) = await ClientForAsync(Roles.CsAgent, departmentId);

        // While waiting: Open and unowned (confirmed decision 8).
        var waiting = await (await agent.GetAsync($"/api/tickets/{ticketId}"))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Open", waiting!.TicketStatus);
        Assert.Null(waiting.CurrentOwnerEmployeeId);
        Assert.Equal("WaitingForAgent", waiting.HandoffState);

        Assert.Equal(
            HttpStatusCode.OK,
            (await agent.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { })).StatusCode);

        var accepted = await (await agent.GetAsync($"/api/tickets/{ticketId}"))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        Assert.Equal("InProgress", accepted!.TicketStatus);
        Assert.Equal(agentId, accepted.CurrentOwnerEmployeeId);
        Assert.Equal("InProgress", accepted.HandoffState);

        // Acceptance is not a reply: First Response is still outstanding.
        // Read from the SLA endpoint, which is where that measurement lives.
        var sla = await (await agent.GetAsync($"/api/tickets/{ticketId}/sla"))
            .Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();
        Assert.Null(sla!.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task TheTicketKeepsItsIdentity_AndIsNeitherResolvedNorClosed()
    {
        var (genesys, _) = await ClientForAsync(Roles.CsAgent);
        var (ticketId, handoffId, departmentId) = await WaitingInteractionAsync(genesys);

        var (agent, _) = await ClientForAsync(Roles.CsAgent, departmentId);
        await agent.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { });
        await agent.PostAsJsonAsync(
            $"/api/pending-customer-interactions/{handoffId}/complete",
            new CompleteAgentHandoffRequestDto("Handled."));

        var ticket = await (await agent.GetAsync($"/api/tickets/{ticketId}"))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        Assert.Equal(ticketId, ticket!.TicketId);
        Assert.Equal("InProgress", ticket.TicketStatus);
        Assert.Null(ticket.ResolutionOutcome);
        // Finishing the conversation is not finishing the case.
        Assert.Equal("NotRequired", ticket.HandoffState);
    }
}
