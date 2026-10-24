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
/// The WhatsApp "Ownership Transfer and NOC" parent menu offers five submenu options, each of which is a real configured request type
/// (four Customer Service NOC types and one Registration type). The ids are created here the way an environment would have them — never
/// fixed numbers — and each selection must land with the right request type, its paired category and its responsible department.
/// The parent menu itself is not a request type and is never mapped.
/// </summary>
public sealed class GenesysWhatsAppRequestTypeMappingEndpointTests : IDisposable
{
    private const string Parent = "Ownership Transfer and NOC";
    private static readonly string[] CustomerServiceChildren = ["NOC for Resale", "NOC for Handover", "NOC for Mortgage", "NOC for Golden Visa"];
    private const string RegistrationChild = "Ownership Transfer / Title Deed Inquiry";

    private readonly TigerCsApiFactory factory = new();

    public void Dispose() => factory.Dispose();

    private sealed record Catalogue(
        int CustomerService, int Registration, string QueueId, Dictionary<string, (int RequestTypeId, int CategoryId, int DepartmentId)> Options);

    private async Task<HttpClient> ClientAsync()
    {
        var (username, password, _) = await factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private async Task<Catalogue> SeedAsync()
    {
        await factory.SeedPrioritiesAsync();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var cs = await factory.CreateDepartmentAsync("Customer Service " + suffix, "CS" + suffix);
        var reg = await factory.CreateDepartmentAsync("Registration " + suffix, "RG" + suffix);

        var options = new Dictionary<string, (int, int, int)>();
        foreach (var name in CustomerServiceChildren)
        {
            options[name] = (await factory.CreateRequestTypeAsync(name, cs), await factory.CreateCategoryAsync(name, cs), cs);
        }

        options[RegistrationChild] = (
            await factory.CreateRequestTypeAsync(RegistrationChild, reg), await factory.CreateCategoryAsync(RegistrationChild, reg), reg);

        var queueId = "wa-queue-" + suffix;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        db.GenesysQueueMappings.Add(new GenesysQueueMapping(queueId, "WhatsApp Customer Service", cs, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return new Catalogue(cs, reg, queueId, options);
    }

    private static GenesysInquiryRequest Inquiry(Catalogue c, string conversationId, GenesysRequestTypePart? requestType, string? subject = null) =>
        new(conversationId, "WhatsApp", CustomerPhone: "+971500000001", CustomerName: "Ahmed Ali", QueueId: c.QueueId,
            StartedAtUtc: DateTime.UtcNow, Subject: subject ?? $"Chat received via Genesys - WhatsApp - CS - {Parent}", RequestType: requestType);

    public static TheoryData<string> AllFiveOptions() => new(CustomerServiceChildren.Append(RegistrationChild));

    [Theory, MemberData(nameof(AllFiveOptions))]
    public async Task EachSelection_CreatesAClassifiedTicket_WithItsRequestType_Category_AndResponsibleDepartment(string option)
    {
        var client = await ClientAsync();
        var c = await SeedAsync();
        var expected = c.Options[option];

        var response = await client.PostAsJsonAsync("/api/genesys/tickets",
            Inquiry(c, Guid.NewGuid().ToString(), new GenesysRequestTypePart(RequestTypeId: expected.RequestTypeId)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;
        Assert.Equal("Classified", body.ClassificationStatus);
        Assert.Equal(expected.RequestTypeId, body.RequestTypeId);
        Assert.Equal(expected.DepartmentId, body.DepartmentId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == body.TicketId);
        Assert.Equal(expected.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(expected.CategoryId, ticket.CategoryId);
        Assert.Equal(expected.DepartmentId, ticket.CurrentDepartmentId);       // a Registration request is never left in Customer Service
        Assert.Equal(c.CustomerService, ticket.OriginatingDepartmentId);       // where it arrived is preserved
        Assert.Contains(Parent, ticket.RequestSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheParentMenuLabel_IsNeverAcceptedAsARequestType()
    {
        var client = await ClientAsync();
        var c = await SeedAsync();

        var response = await client.PostAsJsonAsync("/api/genesys/tickets",
            Inquiry(c, Guid.NewGuid().ToString(), new GenesysRequestTypePart(Name: Parent)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("genesys-request-type-invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Selection_ByExactName_StillWorks()
    {
        var client = await ClientAsync();
        var c = await SeedAsync();

        var response = await client.PostAsJsonAsync("/api/genesys/tickets",
            Inquiry(c, Guid.NewGuid().ToString(), new GenesysRequestTypePart(Name: RegistrationChild)));

        var body = (await response.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;
        Assert.Equal(c.Options[RegistrationChild].RequestTypeId, body.RequestTypeId);
        Assert.Equal(c.Registration, body.DepartmentId);
    }

    [Fact]
    public async Task AnIdWithADifferentName_IsRefused_NothingIsRoutedOnAGuess()
    {
        var client = await ClientAsync();
        var c = await SeedAsync();
        var conversationId = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync("/api/genesys/tickets",
            Inquiry(c, conversationId, new GenesysRequestTypePart(c.Options[RegistrationChild].RequestTypeId, "NOC for Resale")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.Empty(await db.Tickets.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AnIdWithItsOwnName_IsAccepted()
    {
        var client = await ClientAsync();
        var c = await SeedAsync();
        var option = c.Options["NOC for Mortgage"];

        var response = await client.PostAsJsonAsync("/api/genesys/tickets",
            Inquiry(c, Guid.NewGuid().ToString(), new GenesysRequestTypePart(option.RequestTypeId, " noc for mortgage ")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task SubjectNamingTheOption_WithoutAStructuredRequestType_StaysUnclassified_OnNormalPriority_InTheQueueDepartment()
    {
        var client = await ClientAsync();
        var c = await SeedAsync();

        var response = await client.PostAsJsonAsync("/api/genesys/tickets",
            Inquiry(c, Guid.NewGuid().ToString(), requestType: null, subject: $"Chat received via Genesys - WhatsApp - CS - {RegistrationChild}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;
        Assert.Equal("AwaitingClassification", body.ClassificationStatus);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == body.TicketId);
        Assert.Null(ticket.RequestTypeId);
        Assert.Null(ticket.CategoryId);
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
        Assert.Equal(c.CustomerService, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task CreateReuse_ARepeatedDelivery_ReturnsTheSameTicket_AndChangesNothing()
    {
        var client = await ClientAsync();
        var c = await SeedAsync();
        var conversationId = Guid.NewGuid().ToString();
        var option = c.Options[RegistrationChild];
        var request = Inquiry(c, conversationId, new GenesysRequestTypePart(RequestTypeId: option.RequestTypeId));

        var first = await client.PostAsJsonAsync("/api/genesys/tickets", request);
        var firstBody = (await first.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;
        var again = await client.PostAsJsonAsync("/api/genesys/tickets", request);
        var againBody = (await again.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("AlreadyIngested", againBody.Outcome);
        Assert.Equal(firstBody.TicketId, againBody.TicketId);

        // A retry that carries a different request type does not re-route the existing ticket either.
        var other = await client.PostAsJsonAsync("/api/genesys/tickets",
            Inquiry(c, conversationId, new GenesysRequestTypePart(RequestTypeId: c.Options["NOC for Resale"].RequestTypeId)));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync();
        Assert.Equal(option.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(option.CategoryId, ticket.CategoryId);
        Assert.Equal(c.Registration, ticket.CurrentDepartmentId);
    }

    [Theory, MemberData(nameof(AllFiveOptions))]
    public async Task Patch_ClassifiesAnUnclassifiedTicket_WithCategoryAndDepartment_AndIsIdempotent(string option)
    {
        var client = await ClientAsync();
        var c = await SeedAsync();
        var expected = c.Options[option];
        var conversationId = Guid.NewGuid().ToString();

        var created = await client.PostAsJsonAsync("/api/genesys/tickets", Inquiry(c, conversationId, requestType: null));
        var ticketId = (await created.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!.TicketId;

        var patch = new GenesysTicketUpdateRequest(conversationId, RequestType: new GenesysRequestTypePart(RequestTypeId: expected.RequestTypeId));
        var first = await client.PatchAsJsonAsync($"/api/genesys/tickets/{ticketId}", patch);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = (await first.Content.ReadFromJsonAsync<GenesysTicketUpdateResponse>())!;
        Assert.Equal("Classified", firstBody.ClassificationStatus);
        Assert.Equal(expected.RequestTypeId, firstBody.RequestTypeId);
        Assert.Equal(expected.DepartmentId, firstBody.DepartmentId);

        var second = await client.PatchAsJsonAsync($"/api/genesys/tickets/{ticketId}", patch);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
        Assert.Equal(expected.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(expected.CategoryId, ticket.CategoryId);
        Assert.Equal(expected.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(c.CustomerService, ticket.OriginatingDepartmentId);
        Assert.Equal(1, await db.AuditEntries.AsNoTracking().CountAsync(a => a.Action == "ClassifyRequestType" && a.EntityId == ticketId.ToString()));
    }

    [Fact]
    public async Task Patch_WithAnIdAndADifferentName_IsRefused_AndLeavesTheTicketUnclassified()
    {
        var client = await ClientAsync();
        var c = await SeedAsync();
        var conversationId = Guid.NewGuid().ToString();
        var created = await client.PostAsJsonAsync("/api/genesys/tickets", Inquiry(c, conversationId, requestType: null));
        var ticketId = (await created.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!.TicketId;

        var response = await client.PatchAsJsonAsync($"/api/genesys/tickets/{ticketId}",
            new GenesysTicketUpdateRequest(conversationId,
                RequestType: new GenesysRequestTypePart(c.Options[RegistrationChild].RequestTypeId, "NOC for Resale")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
        Assert.Null(ticket.RequestTypeId);
        Assert.Null(ticket.CategoryId);
    }
}
