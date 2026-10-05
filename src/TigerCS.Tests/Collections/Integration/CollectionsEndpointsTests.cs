using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Integrations.Modules.CollectionsIntegration;
using TigerCS.Tests.IdentityAndAccess.Integration;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Integration;

/// <summary>
/// The real host with Collections on, the Development/Testing fixture source,
/// and Collections' own clock fixed at 2 October 2026, 10:00 Dubai (the
/// OverdueMonthly window). Authentication, tickets and SLA keep real time.
/// </summary>
public sealed class CollectionsApiFixture : IAsyncLifetime
{
    public static readonly DateTime CollectionsNowUtc = new(2026, 10, 2, 6, 0, 0, DateTimeKind.Utc);

    public FakeTimeProvider CollectionsTime { get; } = new(CollectionsNowUtc);

    public TigerCsApiFactory Factory { get; }

    public CollectionsApiFixture()
    {
        Factory = new TigerCsApiFactory
        {
            ExtraConfiguration = new()
            {
                ["Collections:Enabled"] = "true",
                ["CollectionsSource:Provider"] = "Fixture",
                ["Collections:Channels:VoiceBotEnabled"] = "true",
                ["Collections:Channels:EmailEnabled"] = "true",
            },
            ExtraServices = services => services.AddScoped(sp =>
                new CollectionsClock(sp.GetRequiredService<CollectionsOptions>(), CollectionsTime)),
        };
    }

    public string CollectionsDepartmentCode { get; } = "C" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    public int CollectionsDepartmentId { get; private set; }

    public CollectionsOptions Options => Factory.Services.GetRequiredService<CollectionsOptions>();

    public async Task InitializeAsync()
    {
        await Factory.SeedPrioritiesAsync();
        CollectionsDepartmentId = await Factory.CreateDepartmentAsync("Collections " + CollectionsDepartmentCode, CollectionsDepartmentCode);
        Options.CollectionsDepartmentCode = CollectionsDepartmentCode;
        Options.ResponseTickets.DepartmentCode = CollectionsDepartmentCode;
    }

    public Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Every Collections route end to end, on both prefixes: figures straight from
/// the source in the specification's shape, account/unit scoping, explicit
/// financial authorization, the candidate → queue flow with revalidation,
/// idempotency and per-channel duplicate prevention, and customer responses
/// turned into tickets through the existing conversation-id ingestion — with
/// the durable retry when that fails.
/// </summary>
public sealed class CollectionsEndpointsTests(CollectionsApiFixture fixture) : IClassFixture<CollectionsApiFixture>
{
    private const string G = "/api/genesys/collections";
    private const string W = "/api/collections";
    private TigerCsApiFactory Factory => fixture.Factory;

    private async Task<HttpClient> ClientAsync(string role, bool integration = false, bool collectionsMember = false)
    {
        var (username, password, employeeId) = await Factory.SeedEmployeeAsync(role);
        if (integration)
        {
            fixture.Options.Authorization.IntegrationEmployeeIds.Add(employeeId);
        }

        if (collectionsMember)
        {
            await Factory.AssignPrimaryDepartmentAsync(employeeId, fixture.CollectionsDepartmentId);
        }

        var client = Factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        var body = await Read<JsonElement>(response, expected);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("message").GetString()));
        Assert.True(body.TryGetProperty("traceId", out _));
        return body;
    }

    private static HttpRequestMessage Post(string url, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private async Task ResetRemindersAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        db.CollectionsReminderEvents.RemoveRange(db.CollectionsReminderEvents);
        db.CollectionsReminderChannels.RemoveRange(db.CollectionsReminderChannels);
        db.CollectionsReminders.RemoveRange(db.CollectionsReminders);
        await db.SaveChangesAsync();
    }

    private static async Task<CollectionsReminderCandidateDto> CandidateAsync(HttpClient client, string prefix = G) =>
        (await Read<CollectionsReminderCandidatesResponseDto>(
            await client.GetAsync($"{prefix}/reminders/candidates?reminderType=OverdueMonthly&crmCustomerId=9001&accountId=ACC-9001-1204"),
            HttpStatusCode.OK)).Items.Single();

    private async Task<(HttpClient Integration, CollectionsReminderJobDto Job)> VoiceReminderAsync()
    {
        await ResetRemindersAsync();
        var integration = await ClientAsync(Roles.CsAgent, integration: true);
        var candidate = await CandidateAsync(integration);
        var job = await Read<CollectionsReminderJobDto>(await integration.SendAsync(Post($"{G}/reminders",
            new QueueCollectionsReminderRequestDto(candidate.CandidateId, ["VoiceBot"]), Guid.NewGuid().ToString())), HttpStatusCode.Accepted);
        return (integration, job);
    }

    // ------------------------------------------------------------------
    // §3 Outstanding
    // ------------------------------------------------------------------

    [Fact]
    public async Task Outstanding_ReturnsTheSpecificationShape_FromTheSourceFigures()
    {
        var client = await ClientAsync(Roles.CsAgent);

        var response = await client.GetAsync($"{G}/customers/9001/outstanding?accountId=ACC-9001-1204");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"remainingPrincipalAmount\":44000.00", raw, StringComparison.Ordinal);   // two decimal places on the wire
        Assert.Contains("\"crmCustomerId\":9001", raw, StringComparison.Ordinal);                  // a number, as in the specification
        var body = await Read<CollectionsOutstandingResponseDto>(response, HttpStatusCode.OK);

        Assert.Equal(9001, body.CrmCustomerId);
        Assert.Equal(new DateOnly(2026, 10, 2), body.BusinessDate);
        Assert.Equal("Current", body.DataStatus);
        Assert.Null(body.NextCursor);
        var a = Assert.Single(body.Accounts);
        Assert.Equal((9200L, "Tiger Tower A", "1204", "AED"), (a.UnitId!.Value, a.TowerName, a.UnitNumber, a.Currency));
        Assert.Equal(44_000m, a.RemainingPrincipalAmount);
        Assert.Equal(14_000m, a.OverduePrincipalAmount);       // Aug 4,000 (partial) + Sep 10,000
        Assert.Equal(0m, a.DueTodayPrincipalAmount);
        Assert.Equal(30_000m, a.FuturePrincipalAmount);
        Assert.Equal(500m, a.PayablePenaltyAmount);
        Assert.Equal(300m, a.PayableFeeAmount);                // the 250 fee on hold is excluded
        Assert.Equal(200m, a.AppliedCreditAmount);
        Assert.Equal(14_600m, a.AmountDueNow);                 // 14,000 + 500 + 300 − 200
        Assert.Equal(10_000m, a.CurrentMonthRemainingAmount);
        Assert.Equal(new DateOnly(2026, 8, 10), a.OldestUnpaidDueDate);
        Assert.Equal(new CollectionsNextPaymentDto("INS-1204-04", new DateOnly(2026, 10, 10), 10_000m), a.NextPayment);
    }

    [Fact]
    public async Task Outstanding_KeepsSeveralContractsOnOneUnitSeparate_AndPagesAccounts()
    {
        var client = await ClientAsync(Roles.CsAgent);

        var unit = await Read<CollectionsOutstandingResponseDto>(await client.GetAsync($"{W}/customers/9001/outstanding?unitId=9200"), HttpStatusCode.OK);
        Assert.Equal(["ACC-9001-1204", "ACC-9001-1204-P"], unit.Accounts.Select(a => a.AccountId));
        Assert.Equal(1_500m, unit.Accounts[1].OverduePrincipalAmount);

        var first = await Read<CollectionsOutstandingResponseDto>(await client.GetAsync($"{W}/customers/9001/outstanding?pageSize=2"), HttpStatusCode.OK);
        Assert.Equal(2, first.Accounts.Count);
        Assert.NotNull(first.NextCursor);
        var second = await Read<CollectionsOutstandingResponseDto>(await client.GetAsync($"{W}/customers/9001/outstanding?pageSize=2&cursor={first.NextCursor}"), HttpStatusCode.OK);
        Assert.Single(second.Accounts);
        Assert.Null(second.NextCursor);
        Assert.Empty(first.Accounts.Select(a => a.AccountId).Intersect(second.Accounts.Select(a => a.AccountId)));
    }

    [Fact]
    public async Task Outstanding_RefusesWhatIsNotTheCustomers_WithoutRevealingIt()
    {
        var client = await ClientAsync(Roles.CsAgent);

        await Problem(await client.GetAsync($"{G}/customers/9001/outstanding?accountId=ACC-9002-0310"), HttpStatusCode.NotFound, "AccountNotFound");
        await Problem(await client.GetAsync($"{G}/customers/424242/outstanding"), HttpStatusCode.NotFound, "AccountNotFound");
        await Problem(await client.GetAsync($"{G}/customers/abc/outstanding"), HttpStatusCode.BadRequest, "InvalidRequest");
        await Problem(await client.GetAsync($"{G}/customers/9001/outstanding?cursor=bogus"), HttpStatusCode.BadRequest, "InvalidRequest");
        await Problem(await client.GetAsync($"{G}/customers/9001/outstanding?pageSize=101"), HttpStatusCode.BadRequest, "InvalidRequest");
    }

    [Fact]
    public async Task Outstanding_FlagsAnInconsistentSource()
    {
        var client = await ClientAsync(Roles.CsAgent);
        var account = Assert.Single((await Read<CollectionsOutstandingResponseDto>(await client.GetAsync($"{G}/customers/9002/outstanding"), HttpStatusCode.OK)).Accounts);

        Assert.Equal("Inconsistent", account.DataStatus);
        Assert.Equal(12_000m, account.RemainingPrincipalAmount);
        Assert.NotEmpty(account.Problems);
    }

    // ------------------------------------------------------------------
    // §4 Payments
    // ------------------------------------------------------------------

    [Fact]
    public async Task Instalments_ShowScheduledPaidRemaining_AndAPartiallyPaidRowSaysWhetherItIsOverdue()
    {
        var client = await ClientAsync(Roles.CsAgent);

        await Problem(await client.GetAsync($"{G}/customers/9001/payments?view=instalments"), HttpStatusCode.BadRequest, "InvalidRequest"); // several accounts
        var body = await Read<CollectionsInstalmentsResponseDto>(
            await client.GetAsync($"{G}/customers/9001/payments?accountId=ACC-9001-1204&view=instalments&pageSize=50"), HttpStatusCode.OK);

        Assert.Equal(("ACC-9001-1204", "AED", "instalments"), (body.AccountId, body.Currency, body.View));
        Assert.Equal(["Paid", "PartiallyPaid", "Overdue", "Upcoming", "Upcoming", "Upcoming"], body.Items.Select(i => i.Status));
        var partial = body.Items[1];
        Assert.Equal((10_000m, 6_000m, 4_000m, true), (partial.ScheduledAmount, partial.AllocatedPaidAmount, partial.RemainingAmount, partial.IsOverdue));
    }

    [Fact]
    public async Task History_ListsPostedPaymentsOnly_WithReceiptsAndCrossAccountAllocations()
    {
        var client = await ClientAsync(Roles.CsAgent);

        var body = await Read<CollectionsPaymentHistoryResponseDto>(
            await client.GetAsync($"{W}/customers/9001/payments?accountId=ACC-9001-1204&view=history"), HttpStatusCode.OK);

        Assert.Equal("history", body.View);
        Assert.Equal(["PAY-1204-02", "PAY-1204-01"], body.Items.Select(p => p.PaymentId));   // the unverified proof is not listed
        Assert.All(body.Items, p => Assert.Equal("Posted", p.Status));
        var shared = body.Items[0];
        Assert.Equal("RCT-20002", shared.ReceiptNumber);
        Assert.Contains(shared.Allocations, a => a.AccountId == "ACC-9001-1204-P" && a.Amount == 1_500m);

        var filtered = await Read<CollectionsPaymentHistoryResponseDto>(
            await client.GetAsync($"{W}/customers/9001/payments?accountId=ACC-9001-1204&view=history&fromDate=2026-08-01&toDate=2026-10-02"), HttpStatusCode.OK);
        Assert.Equal(["PAY-1204-02"], filtered.Items.Select(p => p.PaymentId));

        await Problem(await client.GetAsync($"{W}/customers/9001/payments?accountId=ACC-9001-1204&view=statement"), HttpStatusCode.BadRequest, "InvalidRequest");
        await Problem(await client.GetAsync($"{W}/customers/9001/payments?accountId=ACC-9001-1204&view=history&fromDate=2026-10-02&toDate=2026-01-01"), HttpStatusCode.BadRequest, "InvalidRequest");
    }

    // ------------------------------------------------------------------
    // Authorization
    // ------------------------------------------------------------------

    [Fact]
    public async Task FinancialRead_IsExplicit()
    {
        var reporting = await ClientAsync(Roles.ReportingUser);
        await Problem(await reporting.GetAsync($"{W}/customers/9001/outstanding"), HttpStatusCode.Forbidden, "Forbidden");

        var otherDepartment = await ClientAsync(Roles.DepartmentEmployee);
        await Problem(await otherDepartment.GetAsync($"{W}/customers/9001/outstanding"), HttpStatusCode.Forbidden, "Forbidden");
        await Problem(await otherDepartment.GetAsync($"{W}/customers/9001/reminders"), HttpStatusCode.Forbidden, "Forbidden");

        var collections = await ClientAsync(Roles.DepartmentEmployee, collectionsMember: true);
        Assert.Equal(HttpStatusCode.OK, (await collections.GetAsync($"{W}/customers/9001/outstanding")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await collections.GetAsync($"{W}/reminders/candidates?reminderType=OverdueMonthly")).StatusCode);
    }

    [Fact]
    public async Task ViewingPaymentsAndSendingRemindersAreSeparateGrants()
    {
        var agent = await ClientAsync(Roles.CsAgent);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync($"{W}/customers/9001/outstanding")).StatusCode);
        await Problem(await agent.GetAsync($"{W}/reminders/candidates?reminderType=OverdueMonthly"), HttpStatusCode.Forbidden, "Forbidden");
        await Problem(await agent.SendAsync(Post($"{W}/reminders", new QueueCollectionsReminderRequestDto("CAND-x", ["Email"]))), HttpStatusCode.Forbidden, "Forbidden");

        var supervisor = await ClientAsync(Roles.CsSupervisor);
        await Problem(await supervisor.SendAsync(Post($"{W}/reminders/REM-1/outcomes", new RecordReminderOutcomeRequestDto("e1", "Sms", DeliveryStatus: "Sent"))),
            HttpStatusCode.Forbidden, "Forbidden");
    }

    [Fact]
    public async Task SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride()
    {
        await ResetRemindersAsync();
        var admin = await ClientAsync(Roles.SystemAdministrator);

        foreach (var prefix in new[] { G, W })
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{prefix}/customers/9001/outstanding")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{prefix}/customers/9001/payments?accountId=ACC-9001-1204")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{prefix}/reminders/candidates?reminderType=OverdueMonthly")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{prefix}/customers/9001/reminders")).StatusCode);
            // Authorized, then refused on substance — never 403.
            await Problem(await admin.SendAsync(Post($"{prefix}/reminders", new QueueCollectionsReminderRequestDto("CAND-x", ["Email"]))), HttpStatusCode.BadRequest, "InvalidRequest");
            await Problem(await admin.SendAsync(Post($"{prefix}/reminders/REM-999999/outcomes", new RecordReminderOutcomeRequestDto("e1", "Sms", DeliveryStatus: "Sent"))),
                HttpStatusCode.NotFound, "ReminderNotFound");
        }
    }

    // ------------------------------------------------------------------
    // §5–6 Candidates and queue
    // ------------------------------------------------------------------

    [Fact]
    public async Task CandidateThenQueue_Revalidates_Replays_Conflicts_DeduplicatesAndDispatchesOnce()
    {
        await ResetRemindersAsync();
        Factory.EmailSender.Clear();
        var supervisor = await ClientAsync(Roles.CsSupervisor);

        var list = await Read<CollectionsReminderCandidatesResponseDto>(
            await supervisor.GetAsync($"{W}/reminders/candidates?reminderType=OverdueMonthly&businessDate=2026-10-02"), HttpStatusCode.OK);
        Assert.Equal(("OverdueMonthly", "2026-10:OverdueMonthly", "Asia/Dubai"), (list.ReminderType, list.CycleKey, list.TimeZone));
        var candidate = Assert.Single(list.Items);
        Assert.Equal((4_000m, "UnpaidPrincipalOlderThanOneCalendarMonth"), (candidate.ReminderAmount, candidate.AmountBasis));

        var request = new QueueCollectionsReminderRequestDto(candidate.CandidateId, ["Email"], "en");
        var job = await Read<CollectionsReminderJobDto>(await supervisor.SendAsync(Post($"{W}/reminders", request, "web-key-1")), HttpStatusCode.Accepted);
        Assert.Equal(("Queued", 4_000m), (job.Status, job.ReminderAmount));

        var replay = await Read<CollectionsReminderJobDto>(await supervisor.SendAsync(Post($"{W}/reminders", request, "web-key-1")), HttpStatusCode.OK);
        Assert.Equal(job.ReminderId, replay.ReminderId);

        await Problem(await supervisor.SendAsync(Post($"{W}/reminders", request with { Language = "ar" }, "web-key-1")), HttpStatusCode.Conflict, "IdempotencyConflict");

        var changed = await Problem(await supervisor.SendAsync(Post($"{W}/reminders", request, "web-key-2")), HttpStatusCode.Conflict, "CandidateChanged");
        Assert.DoesNotContain("Email", changed.GetProperty("replacementCandidate").GetProperty("availableChannels").EnumerateArray().Select(c => c.GetString()));

        await Factory.RunOutboxDispatchAsync();
        await Factory.RunOutboxDispatchAsync();
        var email = Assert.Single(Factory.EmailSender.Recorded);
        Assert.Contains("AED 4,000.00", email.Body, StringComparison.Ordinal);

        var history = await Read<CollectionsReminderHistoryResponseDto>(await supervisor.GetAsync($"{W}/customers/9001/reminders?accountId=ACC-9001-1204"), HttpStatusCode.OK);
        var item = Assert.Single(history.Items);
        Assert.Equal((job.ReminderId, 4_000m, "User"), (item.ReminderId, item.ReminderAmount, item.Trigger));
        Assert.Equal("Sent", Assert.Single(item.Channels).Status);
    }

    [Fact]
    public async Task Queue_RefusesWhatTheCallerMayNotOrCannotSend()
    {
        await ResetRemindersAsync();
        var supervisor = await ClientAsync(Roles.CsSupervisor);
        var candidate = await CandidateAsync(supervisor, W);

        await Problem(await supervisor.SendAsync(Post($"{W}/reminders", new QueueCollectionsReminderRequestDto(candidate.CandidateId, ["Sms"]))),
            HttpStatusCode.UnprocessableEntity, "ChannelNotEnabled");
        await Problem(await supervisor.SendAsync(Post($"{W}/reminders", new QueueCollectionsReminderRequestDto(candidate.CandidateId, ["VoiceBot"]))),
            HttpStatusCode.Forbidden, "Forbidden");
        await Problem(await supervisor.SendAsync(Post($"{W}/reminders", new QueueCollectionsReminderRequestDto(candidate.CandidateId, ["Fax"]))),
            HttpStatusCode.BadRequest, "InvalidRequest");
        await Problem(await supervisor.SendAsync(Post($"{W}/reminders", new QueueCollectionsReminderRequestDto(candidate.CandidateId, ["Email"], "fr"))),
            HttpStatusCode.BadRequest, "InvalidRequest");
        await Problem(await supervisor.GetAsync($"{W}/reminders/candidates"), HttpStatusCode.BadRequest, "InvalidRequest");
        await Problem(await supervisor.GetAsync($"{W}/reminders/candidates?reminderType=LegalNotice"), HttpStatusCode.BadRequest, "InvalidRequest");
    }

    // ------------------------------------------------------------------
    // §7 Outcomes
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeliveryEvents_AreValidatedPerChannel_Ordered_AndIdempotent()
    {
        var (integration, job) = await VoiceReminderAsync();
        var url = $"{G}/reminders/{job.ReminderId}/outcomes";

        var answered = new RecordReminderOutcomeRequestDto("EVT-1", "VoiceBot", "GEN-1", null, CollectionsApiFixture.CollectionsNowUtc.AddMinutes(5), "Answered");
        var first = await Read<RecordReminderOutcomeResponseDto>(await integration.SendAsync(Post(url, answered, "genesys-event-1")), HttpStatusCode.OK);
        Assert.Equal(("Recorded", "Answered", "Answered", "NotRequired", false), (first.Result, first.DeliveryStatus, first.ChannelStatus, first.TicketResult, first.Replayed));
        Assert.Null(first.TicketId);

        var replay = await Read<RecordReminderOutcomeResponseDto>(await integration.SendAsync(Post(url, answered, "genesys-event-1")), HttpStatusCode.OK);
        Assert.True(replay.Replayed);

        await Problem(await integration.SendAsync(Post(url, answered with { DeliveryStatus = "NoAnswer" })), HttpStatusCode.Conflict, "IdempotencyConflict");
        await Problem(await integration.SendAsync(Post(url, answered with { EventId = "EVT-9" }, "genesys-event-1")), HttpStatusCode.Conflict, "IdempotencyConflict");

        // A delayed NoAnswer recorded later never overwrites Answered.
        var late = await Read<RecordReminderOutcomeResponseDto>(await integration.SendAsync(Post(url,
            answered with { EventId = "EVT-0", DeliveryStatus = "NoAnswer", OccurredAtUtc = CollectionsApiFixture.CollectionsNowUtc.AddMinutes(1) })), HttpStatusCode.OK);
        Assert.Equal("Answered", late.ChannelStatus);

        await Problem(await integration.SendAsync(Post(url, answered with { EventId = "EVT-2", DeliveryStatus = "Delivered" })), HttpStatusCode.BadRequest, "InvalidRequest");
        await Problem(await integration.SendAsync(Post(url, answered with { EventId = "EVT-3", Channel = "Sms" })), HttpStatusCode.BadRequest, "InvalidRequest");
        await Problem(await integration.SendAsync(Post(url, new RecordReminderOutcomeRequestDto("EVT-4", "VoiceBot", CustomerResponded: true, CustomerIntent: "AlreadyPaid"))),
            HttpStatusCode.BadRequest, "InvalidRequest"); // a voice response needs its conversationId
    }

    [Fact]
    public async Task AlreadyPaid_CreatesTheCollectionsTicketByConversation_RequestsVerification_AndPostsNothing()
    {
        var (integration, job) = await VoiceReminderAsync();
        var url = $"{G}/reminders/{job.ReminderId}/outcomes";
        var conversationId = Guid.NewGuid().ToString();
        var before = await Read<CollectionsOutstandingResponseDto>(await integration.GetAsync($"{G}/customers/9001/outstanding?accountId=ACC-9001-1204"), HttpStatusCode.OK);

        var request = new RecordReminderOutcomeRequestDto("EVT-90001", "VoiceBot", "GEN-90001", conversationId, null, "Answered",
            CustomerResponded: true, CustomerIntent: "AlreadyPaid", RequiresHumanFollowUp: true, CustomerPhone: "tel:+971500000900");
        var recorded = await Read<RecordReminderOutcomeResponseDto>(await integration.SendAsync(Post(url, request, "genesys-event-evt90001")), HttpStatusCode.OK);

        Assert.Equal(("Created", true), (recorded.TicketResult, recorded.FollowUpRequired));
        var ticket = (await Factory.GetTicketAsync(recorded.TicketId!.Value))!;
        Assert.Equal(fixture.CollectionsDepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CategoryId);                 // Unclassified, as every Genesys ticket
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);

        using (var scope = Factory.Services.CreateScope())
        {
            var handoff = await scope.ServiceProvider.GetRequiredService<TigerCsDbContext>().TicketAgentHandoffs.SingleAsync(h => h.TicketId == ticket.TicketId);
            Assert.True(handoff.IsOpen);
            Assert.Contains("Verify it in the financial source", handoff.RequestReason, StringComparison.Ordinal);
        }

        // Replay: the original result and the same ticket.
        var replay = await Read<RecordReminderOutcomeResponseDto>(await integration.SendAsync(Post(url, request, "genesys-event-evt90001")), HttpStatusCode.OK);
        Assert.Equal((recorded.TicketId, "Created", true), (replay.TicketId, replay.TicketResult, replay.Replayed));

        // A second response in the same conversation reuses the ticket.
        var human = await Read<RecordReminderOutcomeResponseDto>(await integration.SendAsync(Post(url,
            new RecordReminderOutcomeRequestDto("EVT-90002", "VoiceBot", null, conversationId, null, null, true, "RequestedHuman"))), HttpStatusCode.OK);
        Assert.Equal((recorded.TicketId, "Reused"), (human.TicketId, human.TicketResult));
        Assert.Equal(1, await TicketsForConversationAsync(conversationId));

        var after = await Read<CollectionsOutstandingResponseDto>(await integration.GetAsync($"{G}/customers/9001/outstanding?accountId=ACC-9001-1204"), HttpStatusCode.OK);
        Assert.Equal(before.Accounts[0].AmountDueNow, after.Accounts[0].AmountDueNow);
        Assert.Equal(TicketStatus.Open, (await Factory.GetTicketAsync(recorded.TicketId.Value))!.TicketStatus);

        var history = await Read<CollectionsReminderHistoryResponseDto>(await integration.GetAsync($"{G}/customers/9001/reminders"), HttpStatusCode.OK);
        var item = Assert.Single(history.Items);
        Assert.Equal(("RequestedHuman", recorded.TicketId), (item.CustomerIntent, item.TicketId));
        Assert.Equal("Answered", Assert.Single(item.Channels).Status);
    }

    [Fact]
    public async Task AiDisconnection_KeepsHumanFollowUpOutstanding()
    {
        var (integration, job) = await VoiceReminderAsync();

        var recorded = await Read<RecordReminderOutcomeResponseDto>(await integration.SendAsync(Post($"{G}/reminders/{job.ReminderId}/outcomes",
            new RecordReminderOutcomeRequestDto("EVT-DROP", "VoiceBot", null, Guid.NewGuid().ToString(), null, "Failed", true, "AiDisconnected"))), HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var handoff = await scope.ServiceProvider.GetRequiredService<TigerCsDbContext>().TicketAgentHandoffs.SingleAsync(h => h.TicketId == recorded.TicketId);
        Assert.True(handoff.IsOpen);
        Assert.Equal(HandoffTrigger.AiConnectionLost, handoff.Trigger);
    }

    [Fact]
    public async Task ATemporaryTicketFailure_Answers202Pending_AndIsRetriedDurably()
    {
        var (integration, job) = await VoiceReminderAsync();
        var conversationId = Guid.NewGuid().ToString();

        fixture.Options.ResponseTickets.DepartmentCode = "NO-SUCH-DEPT";
        try
        {
            var pending = await Read<RecordReminderOutcomeResponseDto>(await integration.SendAsync(Post($"{G}/reminders/{job.ReminderId}/outcomes",
                new RecordReminderOutcomeRequestDto("EVT-RETRY", "VoiceBot", null, conversationId, null, "Answered", true, "PromiseToPay"))), HttpStatusCode.Accepted);
            Assert.Equal(("Pending", (long?)null), (pending.TicketResult, pending.TicketId));
        }
        finally
        {
            fixture.Options.ResponseTickets.DepartmentCode = fixture.CollectionsDepartmentCode;
        }

        await Factory.RunOutboxDispatchAsync();

        var response = (await Read<CollectionsReminderHistoryResponseDto>(await integration.GetAsync($"{G}/customers/9001/reminders"), HttpStatusCode.OK))
            .Items.Single().Responses.Single(r => r.EventId == "EVT-RETRY");
        Assert.Equal("Created", response.TicketResult);
        Assert.NotNull(response.TicketId);
        Assert.Equal(1, await TicketsForConversationAsync(conversationId));
    }

    private async Task<int> TicketsForConversationAsync(string conversationId)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TigerCsDbContext>().TicketInteractions
            .Where(i => i.GenesysConversationId == conversationId).Select(i => i.TicketId).Distinct().CountAsync();
    }
}

/// <summary>Collections switched off, and switched on with no financial source: 503 both ways, never a zero.</summary>
public sealed class CollectionsUnavailableEndpointsTests
{
    private static async Task<HttpClient> ClientAsync(TigerCsApiFactory factory)
    {
        var (username, password, _) = await factory.SeedEmployeeAsync(Roles.CsSupervisor);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    [Fact]
    public async Task Disabled_Answers503CollectionsDisabled_OnEveryRoute()
    {
        using var factory = new TigerCsApiFactory();
        var client = await ClientAsync(factory);

        foreach (var response in new[]
        {
            await client.GetAsync("/api/collections/customers/9001/outstanding"),
            await client.GetAsync("/api/collections/customers/9001/payments"),
            await client.GetAsync("/api/collections/customers/9001/reminders"),
            await client.GetAsync("/api/genesys/collections/reminders/candidates?reminderType=OverdueMonthly"),
            await client.PostAsJsonAsync("/api/genesys/collections/reminders", new QueueCollectionsReminderRequestDto("CAND-x", ["Email"])),
            await client.PostAsJsonAsync("/api/genesys/collections/reminders/REM-1/outcomes", new RecordReminderOutcomeRequestDto("e", "Sms", DeliveryStatus: "Sent")),
        })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("CollectionsDisabled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task NoFinancialSource_IsFinanceUnavailable_ButReminderHistoryStaysReadable()
    {
        using var factory = new TigerCsApiFactory { ExtraConfiguration = new() { ["Collections:Enabled"] = "true" } };
        var client = await ClientAsync(factory);

        var outstanding = await client.GetAsync("/api/collections/customers/9001/outstanding");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, outstanding.StatusCode);
        var body = await outstanding.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FinanceUnavailable", body.GetProperty("code").GetString());
        Assert.Equal(UnavailableCollectionsFinancialSource.Message, body.GetProperty("message").GetString());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/collections/customers/9001/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/collections/customers/9001/reminders")).StatusCode);
    }

    [Theory]
    [InlineData("Fixture", "Production", true)]
    [InlineData("Fixture", "UAT", true)]
    [InlineData("Fixture", "Development", false)]
    [InlineData("Fixture", "Testing", false)]
    [InlineData("Unavailable", "Production", false)]
    public void FixtureSource_IsRefusedOutsideDevelopmentAndTesting(string provider, string environment, bool unsafeExpected) =>
        Assert.Equal(unsafeExpected, CollectionsSourceSafety.IsUnsafe(provider, environment));
}
