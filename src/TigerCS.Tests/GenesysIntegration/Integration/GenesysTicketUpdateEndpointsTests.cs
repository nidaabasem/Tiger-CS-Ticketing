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
/// <c>PATCH /api/genesys/tickets/{ticketId}</c> against the real host: every
/// refusal the update service can return reaches Genesys as a
/// <c>400</c> ProblemDetails naming what was wrong — never as a bare
/// <c>500</c> from an unmapped outcome.
/// </summary>
public sealed class GenesysTicketUpdateEndpointsTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public GenesysTicketUpdateEndpointsTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<HttpClient> CreateServiceAccountAsync()
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private async Task<(string ConversationId, long TicketId)> IngestConversationAsync(HttpClient serviceAccount)
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Genesys CS " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var conversationId = "conv-" + Guid.NewGuid().ToString("N");

        var response = await serviceAccount.PostAsJsonAsync(
            "/api/genesys/tickets",
            new GenesysInquiryRequest(conversationId, "WebsiteChat", CustomerPhone: "+971500000001", DepartmentId: departmentId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>();
        return (conversationId, created!.TicketId);
    }

    private static async Task AssertBadRequestProblemAsync(HttpResponseMessage response, string expectedType)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedType, problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task UnrecognizedHandoffTrigger_Returns400_NotA500()
    {
        var service = await CreateServiceAccountAsync();
        var (conversationId, ticketId) = await IngestConversationAsync(service);

        var response = await service.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticketId}",
            new GenesysTicketUpdateRequest(conversationId, Handoff: new GenesysHandoffPart(Required: true, Trigger: "CustomerGotBored")));

        await AssertBadRequestProblemAsync(response, "https://tigercs.internal/problems/genesys-invalid-handoff-trigger");
    }

    [Fact]
    public async Task StandDownWithoutAReason_Returns400_NotA500()
    {
        var service = await CreateServiceAccountAsync();
        var (conversationId, ticketId) = await IngestConversationAsync(service);
        var raised = await service.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticketId}",
            new GenesysTicketUpdateRequest(conversationId, Handoff: new GenesysHandoffPart(Required: true)));
        Assert.Equal(HttpStatusCode.OK, raised.StatusCode);

        var response = await service.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticketId}",
            new GenesysTicketUpdateRequest(conversationId, Handoff: new GenesysHandoffPart(Required: false)));

        await AssertBadRequestProblemAsync(response, "https://tigercs.internal/problems/genesys-handoff-reason-required");
    }

    [Fact]
    public async Task TranscriptMessageIdLongerThan64Characters_Returns400_InvalidTranscript()
    {
        var service = await CreateServiceAccountAsync();
        var (conversationId, ticketId) = await IngestConversationAsync(service);

        var response = await service.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticketId}",
            new GenesysTicketUpdateRequest(
                conversationId,
                Ended: new GenesysConversationEndPart(
                    DateTime.UtcNow, "AgentDisconnect",
                    [new GenesysTranscriptMessageRequest("Customer", DateTime.UtcNow, "Hello", ExternalMessageId: new string('m', 65))])));

        await AssertBadRequestProblemAsync(response, "https://tigercs.internal/problems/genesys-invalid-transcript");
    }
}
