using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.GenesysIntegration.Integration;

/// <summary>
/// The request-type contract of <c>POST /api/genesys/tickets</c> through the real host.
/// <list type="bullet">
/// <item>No request type: <b>201</b>, ticket on Normal priority, awaiting classification in the human queue.</item>
/// <item>An explicit request type that is invalid: <b>422</b>, and nothing is stored.</item>
/// </list>
/// </summary>
public sealed class GenesysRequestTypeEndpointTests : IDisposable
{
    // The shared test host turns the human classification queue off; this contract is about exactly that queue, so it is switched on.
    private readonly TigerCsApiFactory factory = new() { ExtraConfiguration = new() { ["Genesys:HumanQueueForUnclassified"] = "true" } };

    public void Dispose() => factory.Dispose();

    private async Task<HttpClient> ClientAsync()
    {
        var (username, password, _) = await factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private async Task<string> SeedQueueAsync()
    {
        await factory.SeedPrioritiesAsync();
        var departmentId = await factory.CreateDepartmentAsync("Call Center " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var queueId = "queue-" + Guid.NewGuid().ToString("N");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        db.GenesysQueueMappings.Add(new GenesysQueueMapping(queueId, "Customer Service", departmentId, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return queueId;
    }

    private async Task<(int Interactions, int Tickets, int Intakes, int Audits)> StoredAsync(string conversationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var interactions = await db.TicketInteractions.AsNoTracking().CountAsync(i => i.GenesysConversationId == conversationId);
        return (interactions, await db.Tickets.AsNoTracking().CountAsync(), await db.IntakeRecords.AsNoTracking().CountAsync(),
            await db.AuditEntries.AsNoTracking().CountAsync(a => a.EntityId == conversationId));
    }

    [Fact]
    public async Task MissingRequestType_Returns201_OnNormalPriority_AwaitingClassificationInTheHumanQueue()
    {
        var client = await ClientAsync();
        var queueId = await SeedQueueAsync();
        var conversationId = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync("/api/genesys/tickets",
            new GenesysInquiryRequest(conversationId, "LiveChat", CustomerName: "Visitor", QueueId: queueId, StartedAtUtc: DateTime.UtcNow));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;
        Assert.Equal("AwaitingClassification", body.ClassificationStatus);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == body.TicketId);
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);                  // Normal
        Assert.Null(ticket.RequestTypeId);
        Assert.Contains(await db.TicketAgentHandoffs.AsNoTracking().Where(h => h.TicketId == body.TicketId).ToListAsync(),
            h => h.RequestReason!.StartsWith("Awaiting classification", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(987654, null)]
    [InlineData(null, "No Such Request Type")]
    public async Task ExplicitlyInvalidRequestType_Returns422_AndWritesNothing(int? requestTypeId, string? name)
    {
        var client = await ClientAsync();
        var queueId = await SeedQueueAsync();
        var conversationId = Guid.NewGuid().ToString();
        var before = await StoredAsync(conversationId);

        var response = await client.PostAsJsonAsync("/api/genesys/tickets",
            new GenesysInquiryRequest(conversationId, "LiveChat", CustomerName: "Visitor", QueueId: queueId, StartedAtUtc: DateTime.UtcNow,
                RequestType: new GenesysRequestTypePart(requestTypeId, name)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("genesys-request-type-invalid", problem);
        Assert.Contains("No ticket was created", problem);
        Assert.Equal(before, await StoredAsync(conversationId));                      // no interaction, ticket, intake record or audit entry

        // The corrected call (no request type) then succeeds for the same conversation.
        var retry = await client.PostAsJsonAsync("/api/genesys/tickets",
            new GenesysInquiryRequest(conversationId, "LiveChat", CustomerName: "Visitor", QueueId: queueId, StartedAtUtc: DateTime.UtcNow));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }
}
