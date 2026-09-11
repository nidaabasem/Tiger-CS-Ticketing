using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.GenesysIntegration.Integration;

/// <summary>
/// The Genesys agent identity mapping end-to-end against the real host:
/// <c>POST /api/genesys/agent-context</c>, and the interaction ownership the
/// existing Create Ticket contract records when it names a mapped agent.
///
/// <para>
/// What matters operationally is proven here and nowhere else: an unmapped
/// agent gets a <c>403</c> with a stable code rather than a provisioned
/// account, a missing id gets the ordinary validation answer, and nothing a
/// client puts in a body can choose which Ticketing user is recorded as the
/// handler.
/// </para>
/// </summary>
public sealed class GenesysAgentMappingEndpointsTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public GenesysAgentMappingEndpointsTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid EmployeeId)> CreateClientAsync(string role = Roles.CsAgent)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    /// <summary>A department Genesys can route to, and a conversation ingested into it — the state an agent then opens from Genesys.</summary>
    private async Task<(int DepartmentId, string ConversationId, long TicketId)> IngestConversationAsync(HttpClient serviceAccount, string? agentId = null)
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Genesys CS " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var conversationId = "conv-" + Guid.NewGuid().ToString("N");

        var response = await serviceAccount.PostAsJsonAsync(
            "/api/genesys/tickets",
            new GenesysInquiryRequest(conversationId, "Phone", CustomerPhone: "+971500000001", DepartmentId: departmentId, AgentId: agentId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>();
        return (departmentId, conversationId, created!.TicketId);
    }

    private static string NewGenesysUserId() => Guid.NewGuid().ToString();

    [Fact]
    public async Task MappedAgent_ResolvesToTheTicketingUser_AndRecordsInteractionOwnership()
    {
        var (service, _) = await CreateClientAsync();
        var (_, agentEmployeeId) = await CreateClientAsync();
        var genesysUserId = NewGenesysUserId();
        await _factory.MapGenesysAgentAsync(agentEmployeeId, genesysUserId, "agent@tigerproperties.ae");
        var (_, conversationId, ticketId) = await IngestConversationAsync(service);

        var response = await service.PostAsJsonAsync(
            "/api/genesys/agent-context",
            new GenesysAgentContextRequest(genesysUserId, "agent@tigerproperties.ae", conversationId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<GenesysAgentContextResponse>();
        Assert.Equal("Resolved", body!.Outcome);
        Assert.Equal(agentEmployeeId, body.UserId);
        Assert.Equal(genesysUserId, body.GenesysUserId);
        Assert.Contains(Roles.CsAgent, body.Roles);
        Assert.Equal(ticketId, body.TicketId);
        Assert.Equal(agentEmployeeId, body.HandledByUserId);

        // Stored, not merely echoed — and the ticket's own assignment is
        // untouched by it.
        var interaction = await _factory.GetInteractionByConversationAsync(conversationId);
        Assert.Equal(genesysUserId, interaction!.GenesysAgentUserId);
        Assert.Equal(agentEmployeeId, interaction.HandledByUserId);
        Assert.Equal(conversationId, interaction.GenesysConversationId);
        Assert.Null(await _factory.GetTicketCurrentOwnerAsync(ticketId));
    }

    [Fact]
    public async Task UnmappedAgent_Returns403_WithTheStableCode_AndProvisionsNoUser()
    {
        var (service, _) = await CreateClientAsync();
        var (_, conversationId, _) = await IngestConversationAsync(service);
        var unmapped = NewGenesysUserId();

        var response = await service.PostAsJsonAsync(
            "/api/genesys/agent-context",
            new GenesysAgentContextRequest(unmapped, "nobody@tigerproperties.ae", conversationId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(GenesysController.ErrorCodes.AgentNotMapped, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("GENESYS_AGENT_NOT_MAPPED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("Genesys agent is not mapped to a Ticketing user.", problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(403, problem.RootElement.GetProperty("status").GetInt32());

        // Nothing was created or recorded.
        Assert.Null((await _factory.GetInteractionByConversationAsync(conversationId))!.HandledByUserId);
        var users = await _factory.CountUsersMappedToAsync(unmapped);
        Assert.Equal(0, users);
    }

    [Fact]
    public async Task DeactivatedMappedAgent_Returns403_Inactive()
    {
        var (service, _) = await CreateClientAsync();
        var (_, agentEmployeeId) = await CreateClientAsync();
        var genesysUserId = NewGenesysUserId();
        await _factory.MapGenesysAgentAsync(agentEmployeeId, genesysUserId);
        await _factory.DeactivateEmployeeAsync(agentEmployeeId);

        var response = await service.PostAsJsonAsync("/api/genesys/agent-context", new GenesysAgentContextRequest(genesysUserId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(GenesysController.ErrorCodes.AgentInactive, problem.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{\"agentEmail\":\"agent@tigerproperties.ae\",\"conversationId\":\"conv-x\"}")]
    [InlineData("{\"genesysUserId\":\"\",\"conversationId\":\"conv-x\"}")]
    [InlineData("{\"genesysUserId\":\"   \"}")]
    public async Task MissingGenesysUserId_ReturnsTheOrdinaryValidationProblem(string json)
    {
        var (service, _) = await CreateClientAsync();

        var response = await service.PostAsync(
            "/api/genesys/agent-context", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = problem.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("GenesysUserId", out var messages), errors.ToString());
        Assert.Contains("genesysUserId is required.", messages.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Client_CannotSpoofHandledByUserId_OnTheAgentContextEndpoint()
    {
        var (service, _) = await CreateClientAsync();
        var (_, mappedEmployeeId) = await CreateClientAsync();
        var (_, victimEmployeeId) = await CreateClientAsync(Roles.CsSupervisor);
        var genesysUserId = NewGenesysUserId();
        await _factory.MapGenesysAgentAsync(mappedEmployeeId, genesysUserId);
        var (_, conversationId, _) = await IngestConversationAsync(service);

        // Every plausible spelling of "record this other user instead".
        var spoof = new Dictionary<string, object?>
        {
            ["genesysUserId"] = genesysUserId,
            ["conversationId"] = conversationId,
            ["handledByUserId"] = victimEmployeeId,
            ["userId"] = victimEmployeeId,
            ["employeeId"] = victimEmployeeId,
            ["agentEmail"] = "victim@tigerproperties.ae"
        };

        var response = await service.PostAsJsonAsync("/api/genesys/agent-context", spoof);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<GenesysAgentContextResponse>();
        Assert.Equal(mappedEmployeeId, body!.UserId);
        Assert.Equal(mappedEmployeeId, body.HandledByUserId);

        var interaction = await _factory.GetInteractionByConversationAsync(conversationId);
        Assert.Equal(mappedEmployeeId, interaction!.HandledByUserId);
        Assert.NotEqual(victimEmployeeId, interaction.HandledByUserId);
    }

    [Fact]
    public async Task Client_CannotSpoofHandledByUserId_OnTheCreateTicketContract()
    {
        var (service, _) = await CreateClientAsync();
        var (_, victimEmployeeId) = await CreateClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Genesys CS " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var conversationId = "conv-" + Guid.NewGuid().ToString("N");

        var spoof = new Dictionary<string, object?>
        {
            ["conversationId"] = conversationId,
            ["channel"] = "Phone",
            ["customerPhone"] = "+971500000001",
            ["departmentId"] = departmentId,
            ["handledByUserId"] = victimEmployeeId,
            ["genesysAgentUserId"] = "spoofed"
        };

        var response = await service.PostAsJsonAsync("/api/genesys/tickets", spoof);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var interaction = await _factory.GetInteractionByConversationAsync(conversationId);
        Assert.Null(interaction!.HandledByUserId);
        Assert.Null(interaction.GenesysAgentUserId);
    }

    [Fact]
    public async Task CreateTicket_NamingAMappedAgent_RecordsTheHandler_AndAnUnmappedOneDoesNot()
    {
        var (service, _) = await CreateClientAsync();
        var (_, agentEmployeeId) = await CreateClientAsync();
        var genesysUserId = NewGenesysUserId();
        await _factory.MapGenesysAgentAsync(agentEmployeeId, genesysUserId);

        var (_, mappedConversation, mappedTicketId) = await IngestConversationAsync(service, agentId: genesysUserId);
        var (_, unmappedConversation, _) = await IngestConversationAsync(service, agentId: NewGenesysUserId());

        var mapped = await _factory.GetInteractionByConversationAsync(mappedConversation);
        Assert.Equal(agentEmployeeId, mapped!.HandledByUserId);
        Assert.Equal(genesysUserId, mapped.GenesysAgentUserId);
        Assert.Equal(genesysUserId, mapped.GenesysAgentId);
        Assert.Null(await _factory.GetTicketCurrentOwnerAsync(mappedTicketId));

        // The inquiry is never lost over a mapping gap; ownership just stays
        // null (the verbatim agent id is still kept).
        var unmapped = await _factory.GetInteractionByConversationAsync(unmappedConversation);
        Assert.NotNull(unmapped!.GenesysAgentId);
        Assert.Null(unmapped.HandledByUserId);
        Assert.Null(unmapped.GenesysAgentUserId);
    }

    [Fact]
    public async Task UnknownConversation_Returns404()
    {
        var (service, _) = await CreateClientAsync();
        var (_, agentEmployeeId) = await CreateClientAsync();
        var genesysUserId = NewGenesysUserId();
        await _factory.MapGenesysAgentAsync(agentEmployeeId, genesysUserId);

        var response = await service.PostAsJsonAsync(
            "/api/genesys/agent-context", new GenesysAgentContextRequest(genesysUserId, ConversationId: "conv-never-ingested"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AgentContext_RequiresAuthentication()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/api/genesys/agent-context", new GenesysAgentContextRequest(NewGenesysUserId()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
