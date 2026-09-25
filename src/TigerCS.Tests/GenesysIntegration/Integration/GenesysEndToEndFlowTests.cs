using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.GenesysIntegration.Integration;

/// <summary>
/// The approved Genesys Cloud business flow, end-to-end against the real host
/// — exactly the three calls the Genesys Data Actions make, in the order the
/// Architect flows make them, with the payloads Genesys actually produces
/// (<c>Call.Ani</c> as "tel:+971…", <c>channel: "LiveChat"</c>).
///
/// <para>
/// Every assertion that matters is made on what was <b>stored</b>, not only
/// on what was answered: exactly one ticket and one interaction per
/// conversation, the same TicketId through every transfer and handoff, and
/// a ticket whose department, owner and status Genesys routing never moved.
/// </para>
/// </summary>
public sealed class GenesysEndToEndFlowTests : IClassFixture<TigerCsApiFactory>
{
    /// <summary>The number the host's mock PACT gateway knows (Fatima Noor, Tiger Marina Residences 0304).</summary>
    private const string PactKnownNumber = "+971500000002";

    private readonly TigerCsApiFactory _factory;

    public GenesysEndToEndFlowTests(TigerCsApiFactory factory) => _factory = factory;

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

    /// <summary>A department with a Genesys queue mapped to it — the configuration an administrator performs once per queue.</summary>
    private async Task<(int DepartmentId, string QueueId)> SeedMappedQueueAsync()
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Call Center " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var queueId = "queue-" + Guid.NewGuid().ToString("N");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        db.GenesysQueueMappings.Add(new GenesysQueueMapping(queueId, "Customer Service", departmentId, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return (departmentId, queueId);
    }

    private static string NewConversationId() => Guid.NewGuid().ToString();

    private void CrmReturnsNothing() => _factory.CrmBuyerLookupGateway.Returns(CrmBuyerLookupResult.NotFound());

    private void CrmReturnsBuyer() => _factory.CrmBuyerLookupGateway.Returns(
        CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(
                new CrmCustomerDto(9001, "Test Buyer", null, "+971500000900", "buyer@example.test"),
                [
                    new CrmBuyerUnitDto(
                        LeadId: 9100, LeadStatus: 8, LeadStatusName: "Sold", UnitId: 9200,
                        UnitNumber: "1204", UnitStatus: 3, UnitType: 2, FloorNumber: 12, ProjectId: 79,
                        ProjectName: "Tiger Tower", ProjectArabicName: null, CustomerType: 1,
                        CustomerTypeName: "Buyer")
                ])
        ]));

    private async Task<(int Tickets, int Interactions, int IntakeRecords)> CountStoredForConversationAsync(string conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var interactions = await db.TicketInteractions.AsNoTracking()
            .Where(i => i.GenesysConversationId == conversationId).ToListAsync();
        var ticketIds = interactions.Select(i => i.TicketId).Distinct().ToList();
        var tickets = await db.Tickets.AsNoTracking().CountAsync(t => ticketIds.Contains(t.TicketId));
        var intakes = await db.IntakeRecords.AsNoTracking().CountAsync(i => i.LinkedTicketId != null && ticketIds.Contains(i.LinkedTicketId.Value));
        return (tickets, interactions.Count, intakes);
    }

    private async Task<Ticket> GetTicketAsync(long ticketId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.Tickets.AsNoTracking().FirstAsync(t => t.TicketId == ticketId);
    }

    private async Task<TicketAgentHandoff?> GetOpenHandoffAsync(long ticketId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.TicketAgentHandoffs.AsNoTracking().FirstOrDefaultAsync(h => h.TicketId == ticketId);
    }

    private async Task<List<string?>> GetRoutingAuditAsync(string conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.AuditEntries.AsNoTracking()
            .Where(a => a.Action == GenesysAuditActions.RoutingChanged && a.EntityId == conversationId)
            .OrderBy(a => a.AuditEntryId)
            .Select(a => a.AfterValue)
            .ToListAsync();
    }

    private static async Task<GenesysCustomerLookupResultDto> LookUpAsync(HttpClient client, string ani)
    {
        var response = await client.GetAsync($"/api/genesys/customers/lookup?phoneNumber={Uri.EscapeDataString(ani)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GenesysCustomerLookupResultDto>())!;
    }

    private static async Task<(HttpStatusCode Status, GenesysInquiryAcceptedResponse Body)> CreateAsync(
        HttpClient client, GenesysInquiryRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/genesys/tickets", request);
        var body = await response.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>();
        return (response.StatusCode, body!);
    }

    private static async Task<GenesysTicketUpdateResponse> PatchAsync(HttpClient client, long ticketId, GenesysTicketUpdateRequest request)
    {
        var response = await client.PatchAsJsonAsync($"/api/genesys/tickets/{ticketId}", request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GenesysTicketUpdateResponse>())!;
    }

    // ── 1. Voice ANI lookup: tel:+971, +971 and 971 ───────────────────────

    [Theory]
    [InlineData("tel:+971500000002")]
    [InlineData("+971500000002")]
    [InlineData("971500000002")]
    [InlineData("sip:+971500000002@tigergroup.pure.cloud;user=phone")]
    public async Task Lookup_EveryGenesysAniFormat_IsSearchedAsTheSameCanonicalNumber(string ani)
    {
        var (client, _) = await CreateClientAsync();
        CrmReturnsNothing();

        var result = await LookUpAsync(client, ani);

        // The sources were asked with TigerCS' own "+971…" form — never the
        // telephony URI — so PACT (which matches on the number it stores)
        // finds the customer whatever format Genesys reported.
        Assert.Equal(PactKnownNumber, result.PhoneNumber);
        Assert.Equal(PactKnownNumber, _factory.CrmBuyerLookupGateway.LastSearchedPhoneNumber);
        Assert.True(result.Found);
        Assert.Equal("Fatima Noor", result.ScreenPop.CustomerName);
        Assert.Equal("Pact", result.ScreenPop.VerificationSource);
        Assert.Contains("Tiger Marina Residences - 0304", result.ScreenPop.Units);
    }

    // ── 2. Known customer: customer + units + recent tickets ─────────────

    [Fact]
    public async Task Lookup_KnownCustomer_ReturnsCustomerUnitsAndRecentTickets_InTheScreenPop()
    {
        var (client, _) = await CreateClientAsync();
        var (_, queueId) = await SeedMappedQueueAsync();
        CrmReturnsBuyer();
        var ani = "tel:+97150" + Random.Shared.Next(1000000, 9999999);

        // An earlier call from the same number left a ticket behind.
        var (_, earlier) = await CreateAsync(client, new GenesysInquiryRequest(
            NewConversationId(), "Phone", CustomerPhone: ani, QueueId: queueId));

        var result = await LookUpAsync(client, ani);

        Assert.True(result.Found);
        Assert.Equal("Found", result.CrmStatus);
        var buyer = Assert.Single(result.CrmBuyers);
        Assert.Equal("1204", Assert.Single(buyer.Units).UnitNumber);

        Assert.Equal("Test Buyer", result.ScreenPop.CustomerName);
        Assert.Equal("buyer@example.test", result.ScreenPop.CustomerEmail);
        Assert.Equal("Crm", result.ScreenPop.VerificationSource);
        Assert.Equal("9001", result.ScreenPop.ExternalCustomerId);
        Assert.Equal(["Tiger Tower - 1204"], result.ScreenPop.Units);
        Assert.Equal("Tiger Tower - 1204", result.ScreenPop.UnitsText);

        var ticket = Assert.Single(result.Tickets);
        Assert.Equal(earlier.TicketId, ticket.TicketId);
        Assert.True(ticket.IsOpen);
        Assert.Equal([earlier.TicketNumber], result.ScreenPop.RecentTicketNumbers);
        Assert.Equal([earlier.TicketNumber], result.ScreenPop.OpenTicketNumbers);
        Assert.Equal(1, result.OpenTicketCount);
    }

    [Fact]
    public async Task Lookup_RecentTickets_MatchTheSameNumberHoweverItWasTyped()
    {
        var (client, _) = await CreateClientAsync();
        var (departmentId, _) = await SeedMappedQueueAsync();
        CrmReturnsNothing();
        var local = Random.Shared.Next(1000000, 9999999).ToString();

        // An agent typed the number with spaces on an earlier ticket (the New
        // Ticket wizard stores it verbatim).
        var intake = await client.PostAsJsonAsync("/api/intake-records", new CreateIntakeRecordRequestDto(
            "Phone", $"+971 50 {local[..3]} {local[3..]}", departmentId, IsUnitRelated: false,
            RawUnitNumberEntered: null, PriorityHint: null));
        Assert.Equal(HttpStatusCode.Created, intake.StatusCode);
        var intakeBody = await intake.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        var typed = await client.PostAsJsonAsync("/api/tickets", new CreateTicketRequestDto(
            intakeBody!.IntakeRecordId, null, null, null, null, "Typed by an agent",
            ManualProjectName: null, ManualUnitNumber: null, DepartmentId: departmentId));
        Assert.Equal(HttpStatusCode.Created, typed.StatusCode);
        var typedTicket = await typed.Content.ReadFromJsonAsync<TicketResponseDto>();

        var result = await LookUpAsync(client, $"tel:+97150{local}");

        Assert.Contains(result.Tickets, t => t.TicketId == typedTicket!.TicketId);
    }

    // ── 3. Unknown phone: a valid empty answer, never a 500 ──────────────

    [Theory]
    [InlineData("tel:+971509999999")]
    [InlineData("tel:anonymous")]
    public async Task Lookup_UnknownOrWithheldNumber_IsA200WithAnEmptyScreenPop(string ani)
    {
        var (client, _) = await CreateClientAsync();
        CrmReturnsNothing();

        var result = await LookUpAsync(client, ani);

        Assert.False(result.Found);
        Assert.Empty(result.CrmBuyers);
        Assert.Empty(result.Tickets);
        Assert.Equal(string.Empty, result.ScreenPop.CustomerName);
        Assert.Equal(string.Empty, result.ScreenPop.VerificationSource);
        Assert.Equal(0, result.ScreenPop.MatchedCustomerCount);
        Assert.Empty(result.ScreenPop.Units);
    }

    // ── 4 + 5. Voice: exactly one ticket, and a retry returns it ─────────

    [Fact]
    public async Task Voice_ConversationCreatesExactlyOneTicket_AndEveryRetryReturnsTheSameTicketId()
    {
        var (client, _) = await CreateClientAsync();
        var (departmentId, queueId) = await SeedMappedQueueAsync();
        CrmReturnsNothing();
        var conversationId = NewConversationId();

        // What the inbound call flow's data action sends: Call.Ani verbatim.
        var request = new GenesysInquiryRequest(
            conversationId, "Phone", Direction: "Inbound", CustomerPhone: "tel:+971509999999",
            CalledNumber: "tel:+97180084437", QueueId: queueId, QueueName: "Customer Service",
            StartedAtUtc: new DateTime(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc));

        var (firstStatus, first) = await CreateAsync(client, request);
        var (retryStatus, retry) = await CreateAsync(client, request);
        var (thirdStatus, third) = await CreateAsync(client, request with { QueueId = "a-different-queue" });

        Assert.Equal(HttpStatusCode.Created, firstStatus);
        Assert.Equal("TicketCreated", first.Outcome);
        Assert.Equal(HttpStatusCode.OK, retryStatus);
        Assert.Equal("AlreadyIngested", retry.Outcome);
        Assert.Equal(HttpStatusCode.OK, thirdStatus);
        Assert.Equal(first.TicketId, retry.TicketId);
        Assert.Equal(first.TicketNumber, retry.TicketNumber);
        Assert.Equal(first.TicketId, third.TicketId);

        Assert.Equal((1, 1, 1), await CountStoredForConversationAsync(conversationId));

        // Unknown caller: the ticket exists anyway, with the number stored in
        // TigerCS' own form, and the customer left to identify later.
        var interaction = await _factory.GetInteractionByConversationAsync(conversationId);
        Assert.Equal("+971509999999", interaction!.CustomerPhone);
        Assert.Equal(WellKnownChannels.Phone, interaction.ChannelId);
        Assert.Equal(queueId, interaction.GenesysQueueId);
        Assert.Equal("Inbound", interaction.Direction);
        Assert.Equal(new DateTime(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc), interaction.InteractionStartedAtUtc);
        var ticket = await GetTicketAsync(first.TicketId);
        Assert.Equal(departmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CrmBuyerCustomerId);
    }

    // ── 6 + 7. Live Chat: one ticket, correct channel, retries reuse it ──

    [Theory]
    [InlineData("LiveChat")]
    [InlineData("WebMessaging")]
    [InlineData("WebsiteChat")]
    public async Task LiveChat_StartCreatesExactlyOneLiveChatTicket_AndRetriesNeverDuplicateIt(string channel)
    {
        var (client, _) = await CreateClientAsync();
        var (_, queueId) = await SeedMappedQueueAsync();
        CrmReturnsNothing();
        var conversationId = NewConversationId();

        var request = new GenesysInquiryRequest(
            conversationId, channel, Direction: "Inbound", CustomerName: "Web Visitor",
            CustomerEmail: "visitor@example.test", QueueId: queueId, QueueName: "Web Chat",
            AgentId: "genesys-agent-unmapped", StartedAtUtc: DateTime.UtcNow);

        var (firstStatus, first) = await CreateAsync(client, request);
        var retries = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => CreateAsync(client, request)));

        Assert.Equal(HttpStatusCode.Created, firstStatus);
        Assert.All(retries, r =>
        {
            Assert.Equal(HttpStatusCode.OK, r.Status);
            Assert.Equal(first.TicketId, r.Body.TicketId);
        });
        Assert.Equal((1, 1, 1), await CountStoredForConversationAsync(conversationId));

        var interaction = await _factory.GetInteractionByConversationAsync(conversationId);
        Assert.Equal(WellKnownChannels.LiveChat, interaction!.ChannelId);
        Assert.Equal("Web Visitor", interaction.CustomerName);
        Assert.Equal("visitor@example.test", interaction.CustomerEmail);
        Assert.Equal(queueId, interaction.GenesysQueueId);
        Assert.Equal("genesys-agent-unmapped", interaction.GenesysAgentId);
        Assert.Equal("Inbound", interaction.Direction);
        Assert.NotNull(interaction.InteractionStartedAtUtc);
        Assert.Equal(string.Empty, interaction.CustomerPhone);
    }

    // ── 8. Queue / agent changes update the same interaction ─────────────

    [Fact]
    public async Task Routing_QueueAndAgentChanges_UpdateTheSameInteraction_AndNeverMoveTheTicket()
    {
        var (service, _) = await CreateClientAsync();
        var (_, firstAgentEmployeeId) = await CreateClientAsync();
        var (departmentId, queueId) = await SeedMappedQueueAsync();
        await _factory.MapGenesysAgentAsync(firstAgentEmployeeId, "genesys-user-first");
        CrmReturnsNothing();
        var conversationId = NewConversationId();

        var (_, created) = await CreateAsync(service, new GenesysInquiryRequest(
            conversationId, "Phone", CustomerPhone: "tel:+971501112222", QueueId: queueId));

        // Agent connects (the agent script's data action on load).
        var connected = await PatchAsync(service, created.TicketId, new GenesysTicketUpdateRequest(
            conversationId, Routing: new GenesysRoutingPart(queueId, "Customer Service", "genesys-user-first", "First Agent")));
        // Transferred to another queue, then another agent.
        await PatchAsync(service, created.TicketId, new GenesysTicketUpdateRequest(
            conversationId, Routing: new GenesysRoutingPart("queue-leasing", "Leasing")));
        var transferred = await PatchAsync(service, created.TicketId, new GenesysTicketUpdateRequest(
            conversationId, Routing: new GenesysRoutingPart(AgentId: "genesys-user-second", AgentName: "Second Agent")));
        // A redelivery of the same event changes nothing.
        await PatchAsync(service, created.TicketId, new GenesysTicketUpdateRequest(
            conversationId, Routing: new GenesysRoutingPart(AgentId: "genesys-user-second", AgentName: "Second Agent")));

        Assert.Equal(created.TicketId, connected.TicketId);
        Assert.Equal(created.TicketId, transferred.TicketId);
        Assert.Equal((1, 1, 1), await CountStoredForConversationAsync(conversationId));

        var interaction = await _factory.GetInteractionByConversationAsync(conversationId);
        Assert.Equal("queue-leasing", interaction!.GenesysQueueId);
        Assert.Equal("Leasing", interaction.GenesysQueueName);
        Assert.Equal("genesys-user-second", interaction.GenesysAgentId);
        Assert.Equal("Second Agent", interaction.GenesysAgentName);
        // The first agent who handled it stays recorded as the handler.
        Assert.Equal(firstAgentEmployeeId, interaction.HandledByUserId);
        Assert.Equal("genesys-user-first", interaction.GenesysAgentUserId);

        // Every step is on the audit trail; the redelivery added nothing.
        var audit = await GetRoutingAuditAsync(conversationId);
        Assert.Equal(3, audit.Count);
        Assert.Contains("AgentId=genesys-user-first", audit[0]);
        Assert.Contains("QueueId=queue-leasing", audit[1]);
        Assert.Contains("AgentId=genesys-user-second", audit[2]);

        // The ticket itself: same department, no owner, status untouched.
        var ticket = await GetTicketAsync(created.TicketId);
        Assert.Equal(departmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
    }

    [Fact]
    public async Task StartedAt_IsFilledInWhenMissing_AndNeverMovesOnceKnown()
    {
        var (client, _) = await CreateClientAsync();
        var (_, queueId) = await SeedMappedQueueAsync();
        CrmReturnsNothing();
        var conversationId = NewConversationId();
        var (_, created) = await CreateAsync(client, new GenesysInquiryRequest(conversationId, "LiveChat", QueueId: queueId));
        var started = new DateTime(2026, 9, 25, 9, 30, 0, DateTimeKind.Utc);

        await PatchAsync(client, created.TicketId, new GenesysTicketUpdateRequest(conversationId, StartedAtUtc: started));
        await PatchAsync(client, created.TicketId, new GenesysTicketUpdateRequest(conversationId, StartedAtUtc: started.AddHours(1)));

        Assert.Equal(started, (await _factory.GetInteractionByConversationAsync(conversationId))!.InteractionStartedAtUtc);
    }

    // ── 9. Conversation end updates the same interaction ─────────────────

    [Fact]
    public async Task ConversationEnd_UpdatesTheSameInteraction_AndLeavesTheTicketOpen()
    {
        var (client, _) = await CreateClientAsync();
        var (_, queueId) = await SeedMappedQueueAsync();
        CrmReturnsNothing();
        var conversationId = NewConversationId();
        var (_, created) = await CreateAsync(client, new GenesysInquiryRequest(
            conversationId, "Phone", CustomerPhone: "tel:+971503334444", QueueId: queueId));
        var endedAt = new DateTime(2026, 9, 25, 10, 15, 0, DateTimeKind.Utc);

        var ended = await PatchAsync(client, created.TicketId, new GenesysTicketUpdateRequest(
            conversationId, Ended: new GenesysConversationEndPart(endedAt, "CustomerDisconnect")));
        var redelivered = await PatchAsync(client, created.TicketId, new GenesysTicketUpdateRequest(
            conversationId, Ended: new GenesysConversationEndPart(endedAt.AddMinutes(5), "Timeout")));

        Assert.Equal(created.TicketId, ended.TicketId);
        Assert.True(ended.ConversationEnded);
        Assert.Equal("Open", ended.TicketStatus);
        Assert.Equal(created.TicketId, redelivered.TicketId);

        var interaction = await _factory.GetInteractionByConversationAsync(conversationId);
        Assert.Equal(endedAt, interaction!.EndedAtUtc);
        Assert.Equal("CustomerDisconnect", interaction.EndReason);
        Assert.Equal((1, 1, 1), await CountStoredForConversationAsync(conversationId));
    }

    // ── 10. AI → human handoff keeps the same TicketId ───────────────────

    [Fact]
    public async Task AiToHumanHandoff_KeepsTheSameTicket_AgentConnectTakesTheWaitingWork_AndAcceptIsNotAFirstResponse()
    {
        var (service, _) = await CreateClientAsync();
        var (departmentId, queueId) = await SeedMappedQueueAsync();
        var (agent, agentEmployeeId) = await CreateClientAsync();
        await _factory.AssignPrimaryDepartmentAsync(agentEmployeeId, departmentId);
        await _factory.MapGenesysAgentAsync(agentEmployeeId, "genesys-user-human");
        CrmReturnsNothing();
        var conversationId = NewConversationId();

        // The bot conversation starts: ticket created.
        var (_, created) = await CreateAsync(service, new GenesysInquiryRequest(
            conversationId, "LiveChat", CustomerName: "Web Visitor", QueueId: queueId));

        // The customer asks for a human → WaitingForAgent, same ticket.
        var requested = await PatchAsync(service, created.TicketId, new GenesysTicketUpdateRequest(
            conversationId,
            Handoff: new GenesysHandoffPart(Required: true, Trigger: "CustomerRequestedHuman", Reason: "Customer asked for an agent")));
        Assert.Equal(created.TicketId, requested.TicketId);
        Assert.Equal("WaitingForAgent", requested.HandoffStatus);

        // Genesys connects a human agent: routing names the agent, and the
        // waiting work is recorded as taken — still the same ticket.
        var connected = await PatchAsync(service, created.TicketId, new GenesysTicketUpdateRequest(
            conversationId, Routing: new GenesysRoutingPart(queueId, "Web Chat", "genesys-user-human", "Human Agent")));
        Assert.Equal(created.TicketId, connected.TicketId);
        Assert.Equal("Assigned", connected.HandoffStatus);
        Assert.Equal(requested.TicketAgentHandoffId, connected.TicketAgentHandoffId);

        // The TigerCS agent accepts in Ticketing: owner assigned, Open → InProgress.
        var accept = await agent.PostAsync($"/api/pending-customer-interactions/{connected.TicketAgentHandoffId}/start", null);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        var ticket = await GetTicketAsync(created.TicketId);
        Assert.Equal(agentEmployeeId, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        // Accepting is not a response to the customer.
        Assert.Null(ticket.FirstHumanResponseAtUtc);

        // A later transfer never reassigns work a TigerCS agent is handling.
        await PatchAsync(service, created.TicketId, new GenesysTicketUpdateRequest(
            conversationId, Routing: new GenesysRoutingPart(AgentId: "genesys-user-someone-else")));
        var handoff = await GetOpenHandoffAsync(created.TicketId);
        Assert.Equal(agentEmployeeId, handoff!.AssignedEmployeeId);
        Assert.Equal(agentEmployeeId, (await GetTicketAsync(created.TicketId)).CurrentOwnerEmployeeId);

        Assert.Equal((1, 1, 1), await CountStoredForConversationAsync(conversationId));
    }

    [Fact]
    public async Task AgentConnecting_WithNoPendingHumanWork_IsJustARoutingChange()
    {
        var (client, _) = await CreateClientAsync();
        var (_, queueId) = await SeedMappedQueueAsync();
        CrmReturnsNothing();
        var conversationId = NewConversationId();
        var (_, created) = await CreateAsync(client, new GenesysInquiryRequest(conversationId, "Phone", QueueId: queueId));

        var response = await PatchAsync(client, created.TicketId, new GenesysTicketUpdateRequest(
            conversationId, Routing: new GenesysRoutingPart(AgentId: "genesys-user-voice")));

        Assert.Equal("Applied", response.Outcome);
        Assert.Null(response.HandoffStatus);
        Assert.Null(await GetOpenHandoffAsync(created.TicketId));
    }
    /// <summary>
    /// A Genesys data action cannot omit a field from its request template:
    /// an unset Architect variable arrives as "". That is "not supplied" —
    /// the ticket is still created, and the customer name CRM found is not
    /// overwritten by an empty one.
    /// </summary>
    [Fact]
    public async Task DataActionPayload_WithBlankUnsetFields_IsAcceptedAsAbsent()
    {
        var (client, _) = await CreateClientAsync();
        var (_, queueId) = await SeedMappedQueueAsync();
        CrmReturnsBuyer();
        var conversationId = NewConversationId();

        var response = await client.PostAsync("/api/genesys/tickets", JsonContent.Create(new Dictionary<string, object?>
        {
            ["conversationId"] = conversationId,
            ["channel"] = "Phone",
            ["customerPhone"] = "tel:+971507770000",
            ["customerName"] = "",
            ["customerEmail"] = "",
            ["departmentCode"] = "",
            ["queueId"] = queueId,
            ["queueName"] = "",
            ["agentId"] = "",
            ["agentName"] = "",
            ["calledNumber"] = "",
            ["direction"] = "Inbound",
            ["startedAtUtc"] = null,
            ["subject"] = ""
        }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>();

        var interaction = await _factory.GetInteractionByConversationAsync(conversationId);
        Assert.Equal("Test Buyer", interaction!.CustomerName);
        Assert.Null(interaction.GenesysAgentId);

        // The update data actions send blanks the same way.
        var patch = await client.PatchAsync($"/api/genesys/tickets/{created!.TicketId}", JsonContent.Create(new Dictionary<string, object?>
        {
            ["conversationId"] = conversationId,
            ["routing"] = new Dictionary<string, object?> { ["queueId"] = "", ["queueName"] = "", ["agentId"] = "genesys-user-x", ["agentName"] = "" },
            ["handoff"] = null,
            ["ended"] = null
        }));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        interaction = await _factory.GetInteractionByConversationAsync(conversationId);
        Assert.Equal(queueId, interaction!.GenesysQueueId);
        Assert.Equal("genesys-user-x", interaction.GenesysAgentId);
    }
}
