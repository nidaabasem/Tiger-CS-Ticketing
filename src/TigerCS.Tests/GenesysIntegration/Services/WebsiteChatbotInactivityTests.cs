using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CrmDocuments;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The Website/Chatbot rule: a ticket closes once the customer has done nothing for MORE than 10 minutes, counted from their LAST
/// interaction (not ticket creation), every customer message restarts it, and Call Center tickets, human-managed tickets, voice calls,
/// ended conversations and tickets already closed (e.g. by document delivery) are left alone. Real ingestion, update facade, job service
/// and lifecycle over fakes with a controllable clock.
/// </summary>
public class WebsiteChatbotInactivityTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string Reason10 = "Automatically closed — customer inactive for more than 10 minutes.";
    private const string Phone = "+971500000001";

    private static async Task<(GenesysServiceFixture F, long TicketId, string Conv)> IngestAsync(
        string code = "CS", GenesysChannel channel = GenesysChannel.WebsiteChat, string conv = "conv-web")
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment(code == "CC" ? "Call Center" : "Customer Service", code);
        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(conv, channel, CustomerPhone: Phone, DepartmentId: department.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return (f, result.Ticket!.TicketId, conv);
    }

    private static Task<GenesysTicketUpdateResult> CustomerMessageAsync(GenesysServiceFixture f, long ticketId, string conv) =>
        f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, CustomerActive: true));

    private static async Task<ChatbotInactivityCloseOutcome> RunJobAsync(GenesysServiceFixture f)
    {
        var outcome = ChatbotInactivityCloseOutcome.NotDue;
        foreach (var id in await f.InactivityClose.ListDueAsync())
        {
            outcome = await f.InactivityClose.TryCloseAsync(id);
        }

        return outcome;
    }

    // ---- 10-minute threshold, measured from the last interaction ----

    [Fact]
    public async Task ConfiguredDefault_IsTenMinutes_AndTheOriginalPromptRuleStaysAtFive()
    {
        var options = new TigerCS.Application.Modules.GenesysIntegration.GenesysOptions();
        Assert.Equal(TimeSpan.FromMinutes(10), options.CustomerInactivityTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), options.AwaitingReplyTimeout);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ATicket_IsTrackedFromCreation_ButOnlyClosesAfterMoreThanTenMinutes()
    {
        var (f, _, conv) = await IngestAsync();
        var ticket = f.Tickets.All.Single();
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime, f.InteractionFor(conv)!.LastCustomerInteractionAtUtc);

        f.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f)); // exactly 10:00 is not "more than"
        Assert.NotEqual(TicketStatus.Closed, ticket.TicketStatus);

        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await RunJobAsync(f));

        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        var resolution = Assert.Single(f.Resolutions.Added);
        Assert.Equal(Reason10, resolution.ResolutionNote);
        Assert.Equal(ResolutionOutcome.Cancelled, resolution.ResolutionOutcome);
        var audit = Assert.Single(f.Audit.Entries, e => e.Action == "AutoCloseCustomerInactivity");
        Assert.Contains(Reason10, audit.AfterValue);
        Assert.NotNull(f.InteractionFor(conv)!.InactivityClosedAtUtc);
    }

    [Fact]
    public async Task FiveMinutesOfSilence_ClosesNothing_OnAWebsiteChatTicket()
    {
        var (f, _, _) = await IngestAsync();

        f.Clock.Advance(TimeSpan.FromMinutes(7));

        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f));
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    // ---- reset ----

    [Fact]
    public async Task EachCustomerMessage_RestartsTheClock_FromTheLastInteractionNotFromCreation()
    {
        var (f, ticketId, conv) = await IngestAsync();
        var ticket = f.Tickets.All.Single();

        f.Clock.Advance(TimeSpan.FromMinutes(9));
        var reported = await CustomerMessageAsync(f, ticketId, conv);
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, reported.Outcome);
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime.AddMinutes(10), reported.InactivityDeadlineUtc);

        // 18 minutes after creation, 9 after the message: still within the window.
        f.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f));

        await CustomerMessageAsync(f, ticketId, conv);
        f.Clock.Advance(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await RunJobAsync(f));
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
    }

    [Fact]
    public async Task AwaitingCustomerReplyFalse_CountsAsACustomerReply_AndRestartsTheClock()
    {
        var (f, ticketId, conv) = await IngestAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(8));

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, AwaitingCustomerReply: false));

        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime, f.InteractionFor(conv)!.LastCustomerInteractionAtUtc);
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f));
    }

    [Fact]
    public async Task AnUpdateThatReportsNoCustomerActivity_DoesNotRestartTheClock()
    {
        var (f, ticketId, conv) = await IngestAsync();
        var created = f.InteractionFor(conv)!.LastCustomerInteractionAtUtc;
        f.Clock.Advance(TimeSpan.FromMinutes(4));

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, AgentName: "Bot"));
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, CustomerActive: false));
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, AwaitingCustomerReply: true));

        Assert.Equal(created, f.InteractionFor(conv)!.LastCustomerInteractionAtUtc);
    }

    [Fact]
    public async Task ACustomerMessage_RacingTheClosure_MakesTheClosureLose()
    {
        var (f, _, _) = await IngestAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(11));
        var candidate = Assert.Single(await f.InactivityClose.ListDueAsync());
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = f.UnitOfWork.SaveChangesCallCount + 1;

        Assert.Equal(ChatbotInactivityCloseOutcome.LostRace, await f.InactivityClose.TryCloseAsync(candidate));
        Assert.DoesNotContain(f.Outbox.Committed, m => m.EventType == "TicketClosed");
    }

    // ---- eligibility ----

    [Fact]
    public async Task ACallCenterTicket_IsNeverTracked_AndNeverClosedByTheRule()
    {
        var (f, _, conv) = await IngestAsync(code: "CC");

        Assert.Null(f.InteractionFor(conv)!.LastCustomerInteractionAtUtc);
        f.Clock.Advance(TimeSpan.FromHours(2));

        Assert.Empty(await f.InactivityClose.ListDueAsync());
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task ATicketTransferredToTheCallCenter_IsExcluded_AndStopsBeingTracked()
    {
        var (f, _, conv) = await IngestAsync();
        var callCenter = f.Departments.AddDepartment("Call Center", "CC");
        var ticket = f.Tickets.All.Single();
        ticket.TransferToDepartment(callCenter.DepartmentId);
        f.Clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(ChatbotInactivityCloseOutcome.Excluded, await RunJobAsync(f));

        Assert.NotEqual(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Null(f.InteractionFor(conv)!.LastCustomerInteractionAtUtc);
    }

    [Fact]
    public async Task AVoiceCall_IsNotTracked()
    {
        var (f, _, conv) = await IngestAsync(channel: GenesysChannel.Phone);

        Assert.Null(f.InteractionFor(conv)!.LastCustomerInteractionAtUtc);
        f.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(await f.InactivityClose.ListDueAsync());
    }

    [Fact]
    public async Task AHumanManagedTicket_IsExcluded_AndStopsBeingTracked()
    {
        var (f, _, conv) = await IngestAsync();
        var ticket = f.Tickets.All.Single();
        ticket.AssignTo(Guid.NewGuid());
        f.Clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(ChatbotInactivityCloseOutcome.Excluded, await RunJobAsync(f));

        Assert.NotEqual(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Empty(f.Resolutions.Added);
        Assert.Null(f.InteractionFor(conv)!.LastCustomerInteractionAtUtc);
        Assert.Empty(await f.InactivityClose.ListDueAsync()); // not re-examined every run
    }

    [Fact]
    public async Task ARequestedHumanHandoff_StopsTheClock()
    {
        var (f, ticketId, conv) = await IngestAsync();

        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, Handoff: new GenesysHandoffUpdateDto(Required: true, Reason: "wants a person")));
        f.Clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Null(f.InteractionFor(conv)!.LastCustomerInteractionAtUtc);
        Assert.Empty(await f.InactivityClose.ListDueAsync());
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task AnEndedConversation_IsNotClosed_AndAnActiveOneIsNot()
    {
        var (f, ticketId, conv) = await IngestAsync();
        var ticket = f.Tickets.All.Single();

        // Active: the customer wrote 1 minute ago.
        f.Clock.Advance(TimeSpan.FromMinutes(20));
        await CustomerMessageAsync(f, ticketId, conv);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f));

        // Ended: the conversation finishing never lets the silence rule close the ticket.
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, Ended: new GenesysConversationEndUpdateDto(null, "CustomerDisconnect", null)));
        f.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(await f.InactivityClose.ListDueAsync());
        Assert.NotEqual(TicketStatus.Closed, ticket.TicketStatus);
    }

    [Fact]
    public async Task ATicketAlreadyClosed_IsNeverClosedAgain()
    {
        var (f, _, _) = await IngestAsync();
        var ticket = f.Tickets.All.Single();
        ticket.CloseForCustomerInactivity();
        f.Clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(ChatbotInactivityCloseOutcome.Excluded, await RunJobAsync(f));

        Assert.Empty(f.Resolutions.Added);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoCloseCustomerInactivity");
    }

    [Fact]
    public async Task RepeatedJobRuns_CloseOnce()
    {
        var (f, _, _) = await IngestAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(11));

        await RunJobAsync(f);
        await RunJobAsync(f);

        Assert.Single(f.Resolutions.Added);
        Assert.Single(f.Audit.Entries, e => e.Action == "AutoCloseCustomerInactivity");
    }

    // ---- concurrent document delivery ----

    private static (DocumentDeliveryTicketService Docs, FakeCrmDocumentDeliveryRepository Deliveries) DocumentsFor(GenesysServiceFixture f)
    {
        var deliveries = new FakeCrmDocumentDeliveryRepository();
        return (new DocumentDeliveryTicketService(
            new CrmDocumentOptions { Enabled = true }, f.Conversations, f.Tickets, deliveries, f.Lifecycle, f.Audit,
            new CommittingUnitOfWork(deliveries), f.Clock, NullLogger<DocumentDeliveryTicketService>.Instance), deliveries);
    }

    private static DocumentDeliveryTicketContext DocContext(string conv) =>
        new(ServiceAccount, Guid.NewGuid(), conv, null, DocumentServiceFixture.CustomerId, Phone);

    [Fact]
    public async Task ADocumentRequest_IsCustomerActivity_SoTheTicketIsNotClosedForSilenceWhileItIsProcessed()
    {
        var (f, _, conv) = await IngestAsync();
        var (docs, _) = DocumentsFor(f);

        f.Clock.Advance(TimeSpan.FromMinutes(9));
        await docs.RecordCustomerActivityAsync(DocContext(conv));
        f.Clock.Advance(TimeSpan.FromMinutes(5)); // 14 minutes after creation, 5 after the request

        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f));
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task DocumentDeliveryClosure_ThenTheJob_DoesNotCloseOrAuditTwice_AndKeepsTheDeliveryReason()
    {
        var (f, _, _) = await IngestAsync();
        var (docs, _) = DocumentsFor(f);
        var ticket = f.Tickets.All.Single();

        await docs.HandleAsync(
            DocContext("conv-web"),
            new DocumentDeliveryTicketUpdate(DocumentDeliveryTicketEvent.Sent, "Contract", "REC-1", null, "sent", 1));
        f.Clock.Advance(TimeSpan.FromMinutes(30));
        await RunJobAsync(f);

        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        var resolution = Assert.Single(f.Resolutions.Added);
        Assert.Equal("Requested document successfully sent via email.", resolution.ResolutionNote);
        Assert.Equal(ResolutionOutcome.Resolved, resolution.ResolutionOutcome);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoCloseCustomerInactivity");
    }

    [Fact]
    public async Task TheJobClosingFirst_LeavesTheLaterDocumentOutcomeAsANoOp()
    {
        var (f, _, _) = await IngestAsync();
        var (docs, _) = DocumentsFor(f);
        f.Clock.Advance(TimeSpan.FromMinutes(11));
        await RunJobAsync(f);

        await docs.HandleAsync(
            DocContext("conv-web"),
            new DocumentDeliveryTicketUpdate(DocumentDeliveryTicketEvent.Sent, "Contract", "REC-1", null, "sent", 1));

        Assert.Single(f.Resolutions.Added);
        Assert.Equal(Reason10, f.Resolutions.Added[0].ResolutionNote);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoCloseDocumentDelivery");
    }

    // ---- domain ----

    [Fact]
    public void Domain_RecordInteraction_NeverMovesBackwards_AndIsRefusedOnceEndedOrClosed()
    {
        var t0 = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        var interaction = TicketInteraction.CreateFromGenesys(1, 7, Phone, "c-1", null, null, null, null, null, null, null, t0);

        Assert.True(interaction.RecordCustomerInteraction(t0, ServiceAccount));
        Assert.False(interaction.RecordCustomerInteraction(t0, ServiceAccount));
        Assert.False(interaction.RecordCustomerInteraction(t0.AddMinutes(-1), ServiceAccount));
        Assert.True(interaction.RecordCustomerInteraction(t0.AddMinutes(3), ServiceAccount));
        Assert.False(interaction.IsCustomerInactive(t0.AddMinutes(13), TimeSpan.FromMinutes(10)));
        Assert.True(interaction.IsCustomerInactive(t0.AddMinutes(13).AddTicks(1), TimeSpan.FromMinutes(10)));

        interaction.End(t0.AddMinutes(4), "CustomerDisconnect");
        Assert.Null(interaction.LastCustomerInteractionAtUtc);
        Assert.False(interaction.RecordCustomerInteraction(t0.AddMinutes(5), ServiceAccount));
    }
}
