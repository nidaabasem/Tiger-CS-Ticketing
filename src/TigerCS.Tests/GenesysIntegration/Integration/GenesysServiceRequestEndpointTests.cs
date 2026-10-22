using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.GenesysIntegration.Integration;

/// <summary>
/// Several tickets under one Genesys conversation, through the real host: <c>serviceRequestId</c> on <c>POST /api/genesys/tickets</c>
/// makes each distinct request its own idempotent ticket (201, then 200 on a retry), a caller that sends none keeps one ticket per
/// conversation, and <c>PATCH</c> is addressed by (ticket, conversation).
/// </summary>
public sealed class GenesysServiceRequestEndpointTests : IDisposable
{
    private readonly TigerCsApiFactory factory = new();

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
        var departmentId = await factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var queueId = "queue-" + Guid.NewGuid().ToString("N");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        db.GenesysQueueMappings.Add(new GenesysQueueMapping(queueId, "Customer Service", departmentId, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return queueId;
    }

    private static GenesysInquiryRequest Inquiry(string conversationId, string queueId, string? serviceRequestId, bool? humanFollowUp = null) =>
        new(conversationId, "LiveChat", CustomerName: "Visitor", QueueId: queueId, StartedAtUtc: DateTime.UtcNow,
            ServiceRequestId: serviceRequestId, HumanFollowUpRequired: humanFollowUp);

    [Fact]
    public async Task TwoServiceRequests_InOneConversation_AreTwoTickets_AndEachRetryIsIdempotent()
    {
        var client = await ClientAsync();
        var queueId = await SeedQueueAsync();
        var conversationId = Guid.NewGuid().ToString();

        var documents = await client.PostAsJsonAsync("/api/genesys/tickets", Inquiry(conversationId, queueId, "documents"));
        var leasing = await client.PostAsJsonAsync("/api/genesys/tickets", Inquiry(conversationId, queueId, "leasing", humanFollowUp: true));
        var documentsRetry = await client.PostAsJsonAsync("/api/genesys/tickets", Inquiry(conversationId, queueId, "documents"));
        var leasingRetry = await client.PostAsJsonAsync("/api/genesys/tickets", Inquiry(conversationId, queueId, "leasing", humanFollowUp: true));

        Assert.Equal(HttpStatusCode.Created, documents.StatusCode);
        Assert.Equal(HttpStatusCode.Created, leasing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, documentsRetry.StatusCode);
        Assert.Equal(HttpStatusCode.OK, leasingRetry.StatusCode);

        var documentsBody = (await documents.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;
        var leasingBody = (await leasing.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;
        var documentsRetryBody = (await documentsRetry.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;
        var leasingRetryBody = (await leasingRetry.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;

        Assert.NotEqual(documentsBody.TicketId, leasingBody.TicketId);
        Assert.Equal("documents", documentsBody.ServiceRequestId);
        Assert.Equal("leasing", leasingBody.ServiceRequestId);
        Assert.Equal("AlreadyIngested", documentsRetryBody.Outcome);
        Assert.Equal(documentsBody.TicketId, documentsRetryBody.TicketId);
        Assert.Equal(leasingBody.TicketId, leasingRetryBody.TicketId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var rows = await db.TicketInteractions.AsNoTracking().Where(i => i.GenesysConversationId == conversationId).OrderBy(i => i.TicketInteractionId).ToListAsync();
        Assert.Equal(["documents", "leasing"], rows.Select(r => r.GenesysServiceRequestId));
        Assert.Equal([false, true], rows.Select(r => r.RequiresHumanFollowUp));
    }

    [Fact]
    public async Task WithoutAServiceRequestId_TheConversationIsStillOneTicket_ForExistingIntegrations()
    {
        var client = await ClientAsync();
        var queueId = await SeedQueueAsync();
        var conversationId = Guid.NewGuid().ToString();

        var first = await client.PostAsJsonAsync("/api/genesys/tickets", Inquiry(conversationId, queueId, null));
        var second = await client.PostAsJsonAsync("/api/genesys/tickets", Inquiry(conversationId, queueId, null, humanFollowUp: true));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!.TicketId,
            (await second.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!.TicketId);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"true\"")]
    public async Task Patch_HumanFollowUpRequired_IsAccepted_AsABooleanOrAnArchitectString_AndFlagsOnlyThatTicket(string value)
    {
        var client = await ClientAsync();
        var queueId = await SeedQueueAsync();
        var conversationId = Guid.NewGuid().ToString();
        var documents = (await (await client.PostAsJsonAsync("/api/genesys/tickets", Inquiry(conversationId, queueId, "documents")))
            .Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;
        var leasing = (await (await client.PostAsJsonAsync("/api/genesys/tickets", Inquiry(conversationId, queueId, "leasing")))
            .Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;

        var response = await client.PatchAsync(
            $"/api/genesys/tickets/{leasing.TicketId}",
            new StringContent($"{{\"conversationId\":\"{conversationId}\",\"humanFollowUpRequired\":{value}}}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.True((await db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == leasing.TicketId)).RequiresHumanFollowUp);
        Assert.False((await db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == documents.TicketId)).RequiresHumanFollowUp);
    }

    [Fact]
    public async Task Patch_ForATicketOfAnotherConversation_Is409_AndAnUnknownConversationIs404()
    {
        var client = await ClientAsync();
        var queueId = await SeedQueueAsync();
        var conversationA = Guid.NewGuid().ToString();
        var conversationB = Guid.NewGuid().ToString();
        await client.PostAsJsonAsync("/api/genesys/tickets", Inquiry(conversationA, queueId, "documents"));
        var other = (await (await client.PostAsJsonAsync("/api/genesys/tickets", Inquiry(conversationB, queueId, "documents")))
            .Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;

        var mismatch = await client.PatchAsync(
            $"/api/genesys/tickets/{other.TicketId}",
            new StringContent($"{{\"conversationId\":\"{conversationA}\",\"customerActive\":true}}", Encoding.UTF8, "application/json"));
        var unknown = await client.PatchAsync(
            $"/api/genesys/tickets/{other.TicketId}",
            new StringContent("{\"conversationId\":\"never-ingested\",\"customerActive\":true}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
