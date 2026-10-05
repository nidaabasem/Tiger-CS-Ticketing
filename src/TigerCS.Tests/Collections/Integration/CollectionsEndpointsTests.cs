using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Integrations.Modules.CollectionsIntegration;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Collections.Integration;

/// <summary>The real host with Collections switched on over the Development/Testing fixture source.</summary>
public sealed class CollectionsApiFixture : IAsyncLifetime
{
    public TigerCsApiFactory Factory { get; } = new()
    {
        ExtraConfiguration = new()
        {
            ["Collections:Enabled"] = "true",
            ["CollectionsSource:Provider"] = "Fixture",
            ["Collections:Channels:VoiceBotEnabled"] = "true",
            ["Collections:Channels:EmailEnabled"] = "true",
        }
    };

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
/// Every Collections route end to end: figures straight from the source,
/// customer/account/unit scoping, explicit financial authorization, duplicate
/// prevention, idempotent callbacks, and customer responses turned into
/// tickets through the existing Genesys conversation-id ingestion — with the
/// durable retry when that fails.
/// </summary>
public sealed class CollectionsEndpointsTests(CollectionsApiFixture fixture) : IClassFixture<CollectionsApiFixture>
{
    private const string Base = "/api/genesys/collections";
    private TigerCsApiFactory Factory => fixture.Factory;

    private async Task<(HttpClient Client, Guid EmployeeId)> ClientAsync(string role, bool integration = false, bool collectionsMember = false)
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
        return (client, employeeId);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<CollectionsReminderDto> CreateVoiceReminderAsync(HttpClient integration, string accountId = "ACC-9001-1204")
    {
        // Each test needs its own reminder; a manual voice reminder is daily, so
        // remove any earlier one for this key to keep tests independent.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var existing = await db.CollectionsReminders.Include(r => r.Events).Where(r => r.AccountId == accountId && r.Channel == ReminderChannel.VoiceBot).ToListAsync();
            db.CollectionsReminderEvents.RemoveRange(existing.SelectMany(r => r.Events));
            db.CollectionsReminders.RemoveRange(existing);
            await db.SaveChangesAsync();
        }

        var created = await Read<CreateCollectionsReminderResponseDto>(
            await integration.PostAsJsonAsync($"{Base}/reminders", new CreateCollectionsReminderRequestDto("9001", accountId, "VoiceBot")),
            HttpStatusCode.Created);
        return created.Reminder;
    }

    // ------------------------------------------------------------------
    // Outstanding / payments
    // ------------------------------------------------------------------

    [Fact]
    public async Task Outstanding_ReturnsTheSourceFigures_PerAccount_InItsCurrency()
    {
        var (client, _) = await ClientAsync(Roles.CsAgent);

        var body = await Read<CollectionsOutstandingResponseDto>(await client.GetAsync($"{Base}/customers/9001/outstanding"), HttpStatusCode.OK);

        Assert.Equal("9001", body.CrmCustomerId);
        Assert.StartsWith("Fixture", body.Source, StringComparison.Ordinal);
        Assert.False(body.Viewer.CanSendReminder);
        Assert.False(body.Documents.ReceiptDownloadAvailable);
        Assert.False(body.Documents.StatementDownloadAvailable);

        var arrears = body.Accounts.Single(a => a.AccountId == "ACC-9001-1204");
        Assert.Equal("AED", arrears.Currency);
        Assert.Equal("Consistent", arrears.Consistency);
        Assert.Equal(44_000m, arrears.Balance!.RemainingUnpaidPrincipal);
        Assert.Equal(arrears.Balance.OverduePrincipal + arrears.Balance.PrincipalDueToday + arrears.Balance.FuturePrincipal, arrears.Balance.RemainingUnpaidPrincipal);
        Assert.Equal(500m, arrears.Balance.PayableFinesAndFees); // the held fee is not payable
        Assert.Equal(arrears.Balance.OverduePrincipal + arrears.Balance.PrincipalDueToday + 500m, arrears.Balance.AmountDueNow);
        Assert.Equal(10_000m, arrears.Balance.CurrentMonthRemaining);
        Assert.NotNull(arrears.Balance.NextPayment);
        Assert.Contains(arrears.Instalments, i => i.Status == "Paid" && i.PrincipalPaid == 10_000m);
        Assert.Contains(arrears.Instalments, i => i.PrincipalPaid == 6_000m && i.PrincipalOutstanding == 4_000m);

        var settled = body.Accounts.Single(a => a.AccountId == "ACC-9001-0805");
        Assert.Equal(0m, settled.Balance!.AmountDueNow);
        Assert.False(settled.ReminderEligibility.Eligible);
        Assert.Equal("Settled", settled.ReminderEligibility.Reason);
    }

    [Fact]
    public async Task Outstanding_ScopesToTheAccountOrUnit_AndRefusesOneThatIsNotTheCustomers()
    {
        var (client, _) = await ClientAsync(Roles.CsAgent);

        var byAccount = await Read<CollectionsOutstandingResponseDto>(await client.GetAsync($"{Base}/customers/9001/outstanding?accountId=ACC-9001-0805"), HttpStatusCode.OK);
        Assert.Equal(["ACC-9001-0805"], byAccount.Accounts.Select(a => a.AccountId));

        var byUnit = await Read<CollectionsOutstandingResponseDto>(await client.GetAsync($"{Base}/customers/9001/outstanding?crmUnitId=9200"), HttpStatusCode.OK);
        Assert.Equal(["ACC-9001-1204"], byUnit.Accounts.Select(a => a.AccountId));

        // Another customer's account, or an unknown customer: 404, never an empty "nothing owed".
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/customers/9001/outstanding?accountId=ACC-9002-0310")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/customers/424242/outstanding")).StatusCode);
    }

    [Fact]
    public async Task Outstanding_FlagsASourceMismatch_AndRefusesRemindersOnIt()
    {
        var (client, _) = await ClientAsync(Roles.CsSupervisor);

        var body = await Read<CollectionsOutstandingResponseDto>(await client.GetAsync($"{Base}/customers/9002/outstanding"), HttpStatusCode.OK);
        var account = Assert.Single(body.Accounts);
        Assert.Equal("Mismatch", account.Consistency);
        Assert.Equal(12_000m, account.Balance!.RemainingUnpaidPrincipal);
        Assert.False(account.ReminderEligibility.Eligible);

        var refused = await client.PostAsJsonAsync($"{Base}/reminders", new CreateCollectionsReminderRequestDto("9002", "ACC-9002-0310", "Email"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    [Fact]
    public async Task Payments_PostedOnlyByDefault_UnpostedNeverCountTowardTheBalance_AndArePaged()
    {
        var (client, _) = await ClientAsync(Roles.CsAgent);

        var posted = await Read<CollectionsPaymentsResponseDto>(await client.GetAsync($"{Base}/customers/9001/payments?accountId=ACC-9001-1204"), HttpStatusCode.OK);
        Assert.Equal(2, posted.TotalCount);
        Assert.All(posted.Items, p => Assert.True(p.CountsTowardBalance));

        var all = await Read<CollectionsPaymentsResponseDto>(await client.GetAsync($"{Base}/customers/9001/payments?accountId=ACC-9001-1204&includeUnposted=true&pageSize=2"), HttpStatusCode.OK);
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(2, all.Items.Count);
        var page2 = await Read<CollectionsPaymentsResponseDto>(await client.GetAsync($"{Base}/customers/9001/payments?accountId=ACC-9001-1204&includeUnposted=true&pageSize=2&page=2"), HttpStatusCode.OK);
        var unverified = Assert.Single(all.Items.Concat(page2.Items), p => p.Status == "PendingVerification");
        Assert.False(unverified.CountsTowardBalance);
        Assert.False(unverified.ReceiptAvailable);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Base}/customers/9001/payments?pageSize=101")).StatusCode);
    }

    // ------------------------------------------------------------------
    // Authorization
    // ------------------------------------------------------------------

    [Fact]
    public async Task FinancialRead_IsExplicit_DepartmentEmployeesOutsideCollectionsAndReportingUsersAreRefused()
    {
        var (reporting, _) = await ClientAsync(Roles.ReportingUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporting.GetAsync($"{Base}/customers/9001/outstanding")).StatusCode);

        var (otherDepartment, _) = await ClientAsync(Roles.DepartmentEmployee);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherDepartment.GetAsync($"{Base}/customers/9001/outstanding")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherDepartment.GetAsync($"{Base}/customers/9001/reminders")).StatusCode);

        var (collections, _) = await ClientAsync(Roles.DepartmentEmployee, collectionsMember: true);
        var body = await Read<CollectionsOutstandingResponseDto>(await collections.GetAsync($"{Base}/customers/9001/outstanding"), HttpStatusCode.OK);
        Assert.True(body.Viewer.CanSendReminder);
    }

    [Fact]
    public async Task SendingAndReportingOutcomes_NeedTheirOwnGrants()
    {
        var (agent, _) = await ClientAsync(Roles.CsAgent);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync($"{Base}/reminders", new CreateCollectionsReminderRequestDto("9001", "ACC-9001-1204", "Email"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync($"{Base}/reminders/candidates")).StatusCode);

        // A supervisor may send, but only an integration account reports delivery.
        var (supervisor, _) = await ClientAsync(Roles.CsSupervisor);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await supervisor.PostAsJsonAsync($"{Base}/reminders/1/outcomes", new RecordReminderOutcomeRequestDto("e1", "Delivered"))).StatusCode);
    }

    [Fact]
    public async Task SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride()
    {
        var (admin, _) = await ClientAsync(Roles.SystemAdministrator);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Base}/customers/9001/outstanding")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Base}/customers/9001/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Base}/reminders/candidates")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Base}/customers/9001/reminders")).StatusCode);
        // Authorized, then refused on substance — not 403.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync($"{Base}/reminders", new CreateCollectionsReminderRequestDto("9001", "ACC-9001-0805", "Email"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync($"{Base}/reminders/999999/outcomes", new RecordReminderOutcomeRequestDto("e1", "Delivered"))).StatusCode);
    }

    // ------------------------------------------------------------------
    // Reminders
    // ------------------------------------------------------------------

    [Fact]
    public async Task ManualEmailReminder_IsRevalidated_PersistedWithItsAmount_DeduplicatedAndDispatchedOnce()
    {
        var (collections, employeeId) = await ClientAsync(Roles.DepartmentHead, collectionsMember: true);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            db.CollectionsReminders.RemoveRange(db.CollectionsReminders.Where(r => r.Channel == ReminderChannel.Email && r.AccountId == "ACC-9001-1204"));
            await db.SaveChangesAsync();
        }

        Factory.EmailSender.Clear();

        var request = new CreateCollectionsReminderRequestDto("9001", "ACC-9001-1204", "Email");
        var first = await Read<CreateCollectionsReminderResponseDto>(await collections.PostAsJsonAsync($"{Base}/reminders", request), HttpStatusCode.Created);
        Assert.Equal("Created", first.Outcome);
        Assert.Equal("Queued", first.Reminder.Status);
        Assert.Equal("Manual", first.Reminder.ReminderType);
        Assert.Equal("Manual", first.Reminder.Trigger);
        Assert.Equal("AED", first.Reminder.Currency);
        Assert.True(first.Reminder.Amount > 0m);

        // Same account / type / cycle / channel: the existing reminder, nothing new.
        var second = await Read<CreateCollectionsReminderResponseDto>(await collections.PostAsJsonAsync($"{Base}/reminders", request), HttpStatusCode.OK);
        Assert.Equal("AlreadyExists", second.Outcome);
        Assert.Equal(first.Reminder.ReminderId, second.Reminder.ReminderId);

        // The outbox sends it once, after re-reading the account.
        await Factory.RunOutboxDispatchAsync();
        await Factory.RunOutboxDispatchAsync();
        var sent = Assert.Single(Factory.EmailSender.Recorded, e => e.ToAddress == "buyer@example.test");
        Assert.Contains("AED", sent.Body, StringComparison.Ordinal);

        var history = await Read<CollectionsReminderListResultDto>(await collections.GetAsync($"{Base}/customers/9001/reminders?accountId=ACC-9001-1204"), HttpStatusCode.OK);
        var stored = history.Items.Single(r => r.ReminderId == first.Reminder.ReminderId);
        Assert.Equal("Sent", stored.Status);
        Assert.Equal(first.Reminder.Amount, stored.DispatchAmount);
        Assert.Equal(["Queued", "Sent"], stored.Events.Select(e => e.EventType));
        Assert.Contains(await Factory.GetAuditEntriesAsync($"ACC-9001-1204|Manual|{stored.CycleKey}|Email"),
            a => a.Action == "CollectionsReminderQueued" && a.ActorEmployeeId == employeeId);
    }

    [Fact]
    public async Task SettledAccount_IsNeverReminded()
    {
        var (supervisor, _) = await ClientAsync(Roles.CsSupervisor);
        var refused = await supervisor.PostAsJsonAsync($"{Base}/reminders", new CreateCollectionsReminderRequestDto("9001", "ACC-9001-0805", "Email"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("settled", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Fax", null, HttpStatusCode.BadRequest)]
    [InlineData("1", null, HttpStatusCode.BadRequest)]
    [InlineData("Email", "LegalNotice", HttpStatusCode.BadRequest)]
    [InlineData("Sms", null, HttpStatusCode.UnprocessableEntity)]       // channel not enabled
    [InlineData("VoiceBot", null, HttpStatusCode.BadRequest)]           // Genesys dials, not a supervisor
    public async Task CreateReminder_RefusesWhatIsNotAnOrdinaryReminderOnAnEnabledChannel(string channel, string? type, HttpStatusCode expected)
    {
        var (supervisor, _) = await ClientAsync(Roles.CsSupervisor);
        var response = await supervisor.PostAsJsonAsync($"{Base}/reminders", new CreateCollectionsReminderRequestDto("9001", "ACC-9001-1204", channel, type));
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Candidates_ListOnlyWindowsOpenToday()
    {
        var (integration, _) = await ClientAsync(Roles.CsAgent, integration: true);
        var body = await Read<CollectionsReminderCandidatesResponseDto>(await integration.GetAsync($"{Base}/reminders/candidates?channel=VoiceBot"), HttpStatusCode.OK);

        var expected = ReminderPolicy.OpenWindows(body.BusinessDate, new ReminderRuleSettings()).Select(w => w.Type.ToString());
        Assert.Equal(expected, body.OpenWindows);
        Assert.All(body.Items, c => Assert.Contains(c.ReminderType, body.OpenWindows));
        Assert.DoesNotContain(body.Items, c => c.AccountId is "ACC-9001-0805" or "ACC-9002-0310");
        Assert.Equal(HttpStatusCode.BadRequest, (await integration.GetAsync($"{Base}/reminders/candidates?reminderType=Manual")).StatusCode);
    }

    // ------------------------------------------------------------------
    // Outcomes and customer responses
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeliveryOutcomes_AreIdempotentPerEventId_AndStayDistinctFromResponses()
    {
        var (integration, _) = await ClientAsync(Roles.CsAgent, integration: true);
        var reminder = await CreateVoiceReminderAsync(integration);
        Assert.Equal("Integration", reminder.Trigger);

        var sent = await Read<RecordReminderOutcomeResponseDto>(
            await integration.PostAsJsonAsync($"{Base}/reminders/{reminder.ReminderId}/outcomes", new RecordReminderOutcomeRequestDto("call-1-dialed", "Sent", ProviderReference: "conv-x")), HttpStatusCode.OK);
        Assert.Equal("Sent", sent.ReminderStatus);

        var delivered = new RecordReminderOutcomeRequestDto("call-1-answered", "Delivered");
        Assert.Equal("Recorded", (await Read<RecordReminderOutcomeResponseDto>(await integration.PostAsJsonAsync($"{Base}/reminders/{reminder.ReminderId}/outcomes", delivered), HttpStatusCode.OK)).Outcome);
        var repeat = await Read<RecordReminderOutcomeResponseDto>(await integration.PostAsJsonAsync($"{Base}/reminders/{reminder.ReminderId}/outcomes", delivered), HttpStatusCode.OK);
        Assert.Equal("AlreadyRecorded", repeat.Outcome);
        Assert.Equal("Delivered", repeat.ReminderStatus);

        // A response without a conversation is recorded and owes no ticket; the status stays Delivered.
        var sms = await Read<RecordReminderOutcomeResponseDto>(await integration.PostAsJsonAsync($"{Base}/reminders/{reminder.ReminderId}/outcomes",
            new RecordReminderOutcomeRequestDto("resp-sms", "CustomerResponded", Response: new CollectionsCustomerResponseDto("PromiseToPay", PromisedPaymentDate: new DateOnly(2026, 12, 1), PromisedAmount: 5_000m))), HttpStatusCode.OK);
        Assert.Equal("Delivered", sms.ReminderStatus);
        Assert.Equal("NotApplicable", sms.Event.TicketStatus);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.Equal(1, await db.CollectionsReminderEvents.CountAsync(e => e.CollectionsReminderId == reminder.ReminderId && e.ExternalEventId == "call-1-answered"));
    }

    [Fact]
    public async Task AlreadyPaid_CreatesACollectionsTicketByConversation_RaisesVerification_AndPostsNothing()
    {
        var (integration, _) = await ClientAsync(Roles.CsAgent, integration: true);
        var reminder = await CreateVoiceReminderAsync(integration);
        var conversationId = Guid.NewGuid().ToString();

        var before = await Read<CollectionsOutstandingResponseDto>(await integration.GetAsync($"{Base}/customers/9001/outstanding?accountId=ACC-9001-1204"), HttpStatusCode.OK);

        var request = new RecordReminderOutcomeRequestDto("resp-paid", "CustomerResponded",
            Response: new CollectionsCustomerResponseDto("AlreadyPaid", conversationId, "tel:+971500000900", "Paid by transfer yesterday"));
        var recorded = await Read<RecordReminderOutcomeResponseDto>(
            await integration.PostAsJsonAsync($"{Base}/reminders/{reminder.ReminderId}/outcomes", request), HttpStatusCode.OK);

        Assert.Equal("Linked", recorded.Event.TicketStatus);
        Assert.True(recorded.Event.VerificationFollowUpRequired);
        var ticketId = recorded.Event.TicketId!.Value;

        var ticket = (await Factory.GetTicketAsync(ticketId))!;
        Assert.Equal(fixture.CollectionsDepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CategoryId);           // Unclassified, as every Genesys ticket
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var handoff = await db.TicketAgentHandoffs.SingleAsync(h => h.TicketId == ticketId);
            Assert.True(handoff.IsOpen);
            Assert.Contains("verify", handoff.RequestReason, StringComparison.OrdinalIgnoreCase);
        }

        // The repeated callback records nothing twice and keeps the same ticket.
        var repeat = await Read<RecordReminderOutcomeResponseDto>(await integration.PostAsJsonAsync($"{Base}/reminders/{reminder.ReminderId}/outcomes", request), HttpStatusCode.OK);
        Assert.Equal("AlreadyRecorded", repeat.Outcome);
        Assert.Equal(ticketId, repeat.Event.TicketId);

        // A second response in the same conversation reuses the ticket.
        var human = await Read<RecordReminderOutcomeResponseDto>(await integration.PostAsJsonAsync($"{Base}/reminders/{reminder.ReminderId}/outcomes",
            new RecordReminderOutcomeRequestDto("resp-human", "CustomerResponded", Response: new CollectionsCustomerResponseDto("RequestedHuman", conversationId))), HttpStatusCode.OK);
        Assert.Equal(ticketId, human.Event.TicketId);
        Assert.Equal(1, await CountTicketsForConversationAsync(conversationId));

        // Nothing was posted: the balance is exactly what the source said before.
        var after = await Read<CollectionsOutstandingResponseDto>(await integration.GetAsync($"{Base}/customers/9001/outstanding?accountId=ACC-9001-1204"), HttpStatusCode.OK);
        Assert.Equal(before.Accounts[0].Balance, after.Accounts[0].Balance);
        Assert.Equal(TicketStatus.Open, (await Factory.GetTicketAsync(ticketId))!.TicketStatus);
    }

    [Fact]
    public async Task AiDisconnection_KeepsHumanFollowUpOutstanding()
    {
        var (integration, _) = await ClientAsync(Roles.CsAgent, integration: true);
        var reminder = await CreateVoiceReminderAsync(integration);

        var recorded = await Read<RecordReminderOutcomeResponseDto>(await integration.PostAsJsonAsync($"{Base}/reminders/{reminder.ReminderId}/outcomes",
            new RecordReminderOutcomeRequestDto("resp-drop", "CustomerResponded", Response: new CollectionsCustomerResponseDto("AiDisconnected", Guid.NewGuid().ToString()))), HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var handoff = await db.TicketAgentHandoffs.SingleAsync(h => h.TicketId == recorded.Event.TicketId);
        Assert.True(handoff.IsOpen);
        Assert.Equal(HandoffTrigger.AiConnectionLost, handoff.Trigger);
    }

    [Fact]
    public async Task TicketCreationFailure_IsAccepted_AndRetriedDurablyThroughTheOutbox()
    {
        var (integration, _) = await ClientAsync(Roles.CsAgent, integration: true);
        var reminder = await CreateVoiceReminderAsync(integration);
        var conversationId = Guid.NewGuid().ToString();

        fixture.Options.ResponseTickets.DepartmentCode = "NO-SUCH-DEPT";
        try
        {
            var accepted = await Read<RecordReminderOutcomeResponseDto>(await integration.PostAsJsonAsync($"{Base}/reminders/{reminder.ReminderId}/outcomes",
                new RecordReminderOutcomeRequestDto("resp-retry", "CustomerResponded", Response: new CollectionsCustomerResponseDto("PromiseToPay", conversationId))), HttpStatusCode.Accepted);
            Assert.Equal("Pending", accepted.Event.TicketStatus);
            Assert.Null(accepted.Event.TicketId);
        }
        finally
        {
            fixture.Options.ResponseTickets.DepartmentCode = fixture.CollectionsDepartmentCode;
        }

        await Factory.RunOutboxDispatchAsync();

        var history = await Read<CollectionsReminderListResultDto>(await integration.GetAsync($"{Base}/customers/9001/reminders?accountId=ACC-9001-1204"), HttpStatusCode.OK);
        var response = history.Items.Single(r => r.ReminderId == reminder.ReminderId).Events.Single(e => e.EventId == "resp-retry");
        Assert.Equal("Linked", response.TicketStatus);
        Assert.NotNull(response.TicketId);
        Assert.Equal(1, await CountTicketsForConversationAsync(conversationId));
    }

    [Fact]
    public async Task Outcomes_ValidateTheirInput()
    {
        var (integration, _) = await ClientAsync(Roles.CsAgent, integration: true);
        var reminder = await CreateVoiceReminderAsync(integration);
        var url = $"{Base}/reminders/{reminder.ReminderId}/outcomes";

        Assert.Equal(HttpStatusCode.BadRequest, (await integration.PostAsJsonAsync(url, new RecordReminderOutcomeRequestDto(null, "Delivered"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await integration.PostAsJsonAsync(url, new RecordReminderOutcomeRequestDto("tigercs:sent", "Sent"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await integration.PostAsJsonAsync(url, new RecordReminderOutcomeRequestDto("e", "Queued"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await integration.PostAsJsonAsync(url, new RecordReminderOutcomeRequestDto("e", "CustomerResponded"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await integration.PostAsJsonAsync($"{Base}/reminders/987654/outcomes", new RecordReminderOutcomeRequestDto("e", "Sent"))).StatusCode);
    }

    private async Task<int> CountTicketsForConversationAsync(string conversationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.TicketInteractions.Where(i => i.GenesysConversationId == conversationId).Select(i => i.TicketId).Distinct().CountAsync();
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
    public async Task Disabled_Answers503OnEveryRoute()
    {
        using var factory = new TigerCsApiFactory();
        var client = await ClientAsync(factory);

        foreach (var response in new[]
        {
            await client.GetAsync("/api/genesys/collections/customers/9001/outstanding"),
            await client.GetAsync("/api/genesys/collections/customers/9001/payments"),
            await client.GetAsync("/api/genesys/collections/customers/9001/reminders"),
            await client.GetAsync("/api/genesys/collections/reminders/candidates"),
            await client.PostAsJsonAsync("/api/genesys/collections/reminders", new CreateCollectionsReminderRequestDto("9001", "A", "Email")),
            await client.PostAsJsonAsync("/api/genesys/collections/reminders/1/outcomes", new RecordReminderOutcomeRequestDto("e", "Sent")),
        })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("collections-disabled", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NoFinancialSource_Answers503_ButReminderHistoryStaysReadable()
    {
        using var factory = new TigerCsApiFactory { ExtraConfiguration = new() { ["Collections:Enabled"] = "true" } };
        var client = await ClientAsync(factory);

        var outstanding = await client.GetAsync("/api/genesys/collections/customers/9001/outstanding");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, outstanding.StatusCode);
        var body = await outstanding.Content.ReadAsStringAsync();
        Assert.Contains("collections-source-unavailable", body, StringComparison.Ordinal);
        Assert.Contains(UnavailableCollectionsFinancialSource.Message[..40], body, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/genesys/collections/customers/9001/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/genesys/collections/customers/9001/reminders")).StatusCode);
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
