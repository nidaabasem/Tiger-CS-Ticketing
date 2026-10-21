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
/// <c>closedByAi: true</c> on the existing PATCH: the AI finished the conversation, so the ticket closes (Resolved → Closed) with the exact
/// reason "Closed by AI" — only while the AI still owns it, never twice, never over an earlier closure, never a Call Center or
/// person-owned ticket. And it stays separate from the customer-interaction signal that drives the inactivity fallback.
/// </summary>
public class ClosedByAiTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string Phone = "+971500000001";

    private static async Task<(GenesysServiceFixture F, long TicketId, string Conv)> IngestAsync(
        string code = "CS", GenesysChannel channel = GenesysChannel.WebsiteChat, string conv = "conv-ai")
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment(code == "CC" ? "Call Center" : "Customer Service", code);
        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(conv, channel, CustomerPhone: Phone, DepartmentId: department.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return (f, result.Ticket!.TicketId, conv);
    }

    private static Task<GenesysTicketUpdateResult> CloseByAiAsync(GenesysServiceFixture f, long ticketId, string conv) =>
        f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, ClosedByAi: true));

    // ---- the AI finishes ----

    [Fact]
    public async Task AiCompletion_ClosesThroughTheLifecycle_WithTheExactReason()
    {
        var (f, ticketId, conv) = await IngestAsync();
        var ticket = f.Tickets.All.Single();

        var result = await CloseByAiAsync(f, ticketId, conv);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.True(result.ClosedByAi);
        Assert.Null(result.ClosedByAiNote);
        Assert.Equal("Closed", result.TicketStatus);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Resolved, ticket.ResolutionOutcome);

        var resolution = Assert.Single(f.Resolutions.Added);
        Assert.Equal("Closed by AI", resolution.ResolutionNote);
        // Open → Resolved, outcome, Resolved → Closed — system actions carrying the reason.
        Assert.Equal(3, f.StatusHistory.Added.Count(h => h.TicketId == ticketId && h.Note == "Closed by AI" && h.ActorIsSystem));
        var audit = Assert.Single(f.Audit.Entries, e => e.Action == "AutoCloseByAi");
        Assert.Contains("Reason=Closed by AI", audit.AfterValue);
        Assert.Contains($"ConversationId={conv}", audit.AfterValue);
        Assert.Contains(f.Outbox.Committed, m => m.EventType == "TicketClosed");
    }

    [Fact]
    public async Task AiCompletion_StopsBothInactivityClocks_SoTheFallbackNeverFiresOnAFinishedConversation()
    {
        var (f, ticketId, conv) = await IngestAsync();
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, AwaitingCustomerReply: true));

        await CloseByAiAsync(f, ticketId, conv);

        var interaction = f.InteractionFor(conv)!;
        Assert.Null(interaction.LastCustomerInteractionAtUtc);
        Assert.Null(interaction.AwaitingCustomerReplySinceUtc);
        f.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(await f.InactivityClose.ListDueAsync());
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoCloseCustomerInactivity");
    }

    [Fact]
    public async Task AiCompletion_AlsoClosesAVoicebotConversation()
    {
        var (f, ticketId, conv) = await IngestAsync(channel: GenesysChannel.Phone, conv: "conv-voice");

        var result = await CloseByAiAsync(f, ticketId, conv);

        Assert.True(result.ClosedByAi);
        Assert.Equal(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task AiCompletion_IsAppliedAfterTheRestOfTheSameUpdate_TheTranscriptIsStoredFirst()
    {
        var (f, ticketId, conv) = await IngestAsync();

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                conv, Ended: new GenesysConversationEndUpdateDto(
                    null, "AiCompleted", [new GenesysTranscriptMessageDto("Customer", DateTime.UtcNow, "Thanks")]),
                ClosedByAi: true));

        Assert.True(result.ClosedByAi);
        Assert.True(result.ConversationEnded);
        Assert.Equal(1, result.TranscriptMessageCount);
    }

    // ---- idempotent / already closed ----

    [Fact]
    public async Task ARepeatedSignal_ClosesAndAuditsOnce_AndAnswersWithoutError()
    {
        var (f, ticketId, conv) = await IngestAsync();

        var first = await CloseByAiAsync(f, ticketId, conv);
        var second = await CloseByAiAsync(f, ticketId, conv);

        Assert.True(first.ClosedByAi);
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, second.Outcome);
        Assert.False(second.ClosedByAi);
        Assert.Contains("already closed", second.ClosedByAiNote);
        Assert.Single(f.Resolutions.Added);
        Assert.Single(f.Audit.Entries, e => e.Action == "AutoCloseByAi");
        Assert.Single(f.StatusHistory.Added, h => h.Dimension == TicketStatusDimension.TicketStatus && h.NewValue == (byte)TicketStatus.Closed);
    }

    [Fact]
    public async Task ATicketClosedByAPerson_IsNotOverwritten()
    {
        var (f, ticketId, conv) = await IngestAsync();
        var ticket = f.Tickets.All.Single();
        ticket.CloseForCustomerInactivity();

        var result = await CloseByAiAsync(f, ticketId, conv);

        Assert.False(result.ClosedByAi);
        Assert.Contains("already closed", result.ClosedByAiNote);
        Assert.Empty(f.Resolutions.Added);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoCloseByAi");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task WithoutTheSignal_NothingCloses_AndOtherUpdatesBehaveAsBefore(bool? closedByAi)
    {
        var (f, ticketId, conv) = await IngestAsync();

        var result = await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, AgentName: "Bot", ClosedByAi: closedByAi));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.False(result.ClosedByAi);
        Assert.Null(result.ClosedByAiNote);
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    // ---- the AI does not own it ----

    [Fact]
    public async Task HandedOverToAHuman_IsNeverClosedByTheAi()
    {
        var (f, ticketId, conv) = await IngestAsync();
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, Handoff: new GenesysHandoffUpdateDto(Required: true, Reason: "wants a person")));

        var result = await CloseByAiAsync(f, ticketId, conv);

        Assert.False(result.ClosedByAi);
        Assert.Contains("human follow-up", result.ClosedByAiNote);
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
        Assert.Empty(f.Resolutions.Added);
    }

    [Fact]
    public async Task AHandoffRequestedInTheSameUpdate_BeatsTheClosure()
    {
        var (f, ticketId, conv) = await IngestAsync();

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(conv, Handoff: new GenesysHandoffUpdateDto(Required: true, Reason: "escalated"), ClosedByAi: true));

        Assert.False(result.ClosedByAi);
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
        Assert.NotNull(result.HandoffStatus);
    }

    [Fact]
    public async Task APersonOwningTheTicket_IsNeverClosedByTheAi()
    {
        var (f, ticketId, conv) = await IngestAsync();
        f.Tickets.All.Single().AssignTo(Guid.NewGuid());

        var result = await CloseByAiAsync(f, ticketId, conv);

        Assert.False(result.ClosedByAi);
        Assert.Contains("person owns", result.ClosedByAiNote);
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task AnAgentConnectingInTheSameUpdate_IsNotClosedByTheAi()
    {
        var (f, ticketId, conv) = await IngestAsync();

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(conv, Routing: new GenesysRoutingUpdateDto("q1", "Support", "agent-9", "Sara"), ClosedByAi: true));

        Assert.False(result.ClosedByAi);
        Assert.Contains("human agent", result.ClosedByAiNote);
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task ACallCenterTicket_IsNeverClosedByTheAi()
    {
        var (f, ticketId, conv) = await IngestAsync(code: "CC");

        var result = await CloseByAiAsync(f, ticketId, conv);

        Assert.False(result.ClosedByAi);
        Assert.Contains("Call Center", result.ClosedByAiNote);
        Assert.Equal(TicketStatus.InProgress, f.Tickets.All.Single().TicketStatus);
        Assert.Empty(f.Resolutions.Added);
    }

    [Fact]
    public async Task ATicketTransferredToTheCallCenter_IsNeverClosedByTheAi()
    {
        var (f, ticketId, conv) = await IngestAsync();
        var callCenter = f.Departments.AddDepartment("Call Center", "CC");
        f.Tickets.All.Single().TransferToDepartment(callCenter.DepartmentId);

        var result = await CloseByAiAsync(f, ticketId, conv);

        Assert.False(result.ClosedByAi);
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task AWrongConversation_IsRefusedByTheExistingCrossCheck_AndClosesNothing()
    {
        var (f, ticketId, _) = await IngestAsync();

        var result = await CloseByAiAsync(f, ticketId, "some-other-conversation");

        Assert.Equal(GenesysTicketUpdateOutcome.ConversationNotFound, result.Outcome);
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    // ---- the two signals are separate ----

    [Fact]
    public async Task CustomerInteraction_NeverClosesAnything_AndTheAiSignalIsNotACustomerInteraction()
    {
        var (f, ticketId, conv) = await IngestAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        var before = f.InteractionFor(conv)!.LastCustomerInteractionAtUtc;

        // awaitingCustomerReply:false = the customer interacted: restarts the clock, closes nothing.
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, AwaitingCustomerReply: false));
        var afterCustomer = f.InteractionFor(conv)!.LastCustomerInteractionAtUtc;
        Assert.True(afterCustomer > before);
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);

        // closedByAi:true = the AI finished: closes, and does not count as the customer doing anything.
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        await CloseByAiAsync(f, ticketId, conv);
        Assert.Equal(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
        Assert.NotEqual(f.Clock.GetUtcNow().UtcDateTime, afterCustomer);
    }

    [Fact]
    public async Task WithoutTheAiSignal_TheTenMinuteInactivityFallbackStillClosesTheTicket()
    {
        var (f, _, _) = await IngestAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1)));

        foreach (var id in await f.InactivityClose.ListDueAsync())
        {
            Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await f.InactivityClose.TryCloseAsync(id));
        }

        Assert.Equal("Automatically closed — customer inactive for more than 10 minutes.", Assert.Single(f.Resolutions.Added).ResolutionNote);
    }

    // ---- document delivery ----

    private static DocumentDeliveryTicketService DocumentsFor(GenesysServiceFixture f)
    {
        var deliveries = new FakeCrmDocumentDeliveryRepository();
        return new DocumentDeliveryTicketService(
            new CrmDocumentOptions { Enabled = true }, f.Conversations, f.Tickets, deliveries, f.Lifecycle, f.Audit,
            new CommittingUnitOfWork(deliveries), f.Clock, NullLogger<DocumentDeliveryTicketService>.Instance);
    }

    private static DocumentDeliveryTicketContext DocContext(string conv) =>
        new(ServiceAccount, Guid.NewGuid(), conv, null, DocumentServiceFixture.CustomerId, Phone);

    [Theory]
    [InlineData(DocumentDeliveryTicketEvent.Sent, "Requested document successfully sent via email.")]
    [InlineData(DocumentDeliveryTicketEvent.Failed, "Document delivery failed — The channel rejected it.")]
    public async Task AnEarlierDocumentDeliveryClosure_IsNeverOverwrittenByTheAiSignal(DocumentDeliveryTicketEvent outcome, string expectedReason)
    {
        var (f, ticketId, conv) = await IngestAsync();
        await DocumentsFor(f).HandleAsync(
            DocContext(conv), new DocumentDeliveryTicketUpdate(outcome, "Contract", "REC-1", "DELIVERY_FAILED", "The channel rejected it.", 1));

        var result = await CloseByAiAsync(f, ticketId, conv);

        Assert.False(result.ClosedByAi);
        Assert.Contains("already closed", result.ClosedByAiNote);
        Assert.Equal(expectedReason, Assert.Single(f.Resolutions.Added).ResolutionNote);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoCloseByAi");
    }

    [Fact]
    public async Task TheAiClosingFirst_LeavesALateDocumentOutcomeAsANoOp_WithoutAnotherClosure()
    {
        var (f, ticketId, conv) = await IngestAsync();
        await CloseByAiAsync(f, ticketId, conv);

        await DocumentsFor(f).HandleAsync(
            DocContext(conv), new DocumentDeliveryTicketUpdate(DocumentDeliveryTicketEvent.Sent, "Contract", "REC-1", null, "sent", 1));

        Assert.Equal("Closed by AI", Assert.Single(f.Resolutions.Added).ResolutionNote);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoCloseDocumentDelivery");
    }

    [Fact]
    public async Task AClosureLosingTheRaceToADocumentOutcome_ReportsItAsANote_AndWritesNothing()
    {
        var (f, ticketId, conv) = await IngestAsync();
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = f.UnitOfWork.SaveChangesCallCount + 1;

        var result = await CloseByAiAsync(f, ticketId, conv);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.False(result.ClosedByAi);
        Assert.Contains("Send closedByAi again", result.ClosedByAiNote);
        Assert.DoesNotContain(f.Outbox.Committed, m => m.EventType == "TicketClosed");
    }

    // ---- wire format (Data Action 10) ----

    [Theory]
    [InlineData("{\"conversationId\":\"c\",\"closedByAi\":true}", null, true)]
    [InlineData("{\"conversationId\":\"c\",\"awaitingCustomerReply\":\"\",\"closedByAi\":\"true\"}", null, true)]
    [InlineData("{\"conversationId\":\"c\",\"awaitingCustomerReply\":\"false\",\"closedByAi\":\"\"}", false, null)]
    [InlineData("{\"conversationId\":\"c\",\"awaitingCustomerReply\":true}", true, null)]
    [InlineData("{\"conversationId\":\"c\"}", null, null)]
    public void TheRequest_ReadsBooleansAndTheBlanksADataActionSends(string json, bool? awaiting, bool? closedByAi)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<TigerCS.Api.Controllers.GenesysTicketUpdateRequest>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Equal(awaiting, request.AwaitingCustomerReply);
        Assert.Equal(closedByAi, request.ClosedByAi);
        Assert.Equal(closedByAi, TigerCS.Api.Controllers.GenesysContractMapper.Map(request).ClosedByAi);
    }

    [Fact]
    public void AMalformedFlag_IsRejected_NotSilentlyTreatedAsFalse()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<TigerCS.Api.Controllers.GenesysTicketUpdateRequest>(
            "{\"conversationId\":\"c\",\"closedByAi\":\"maybe\"}", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }

    // ---- domain ----

    [Fact]
    public void Domain_CloseForAiCompletion_RefusesAnOwnedOrClosedTicket()
    {
        var owned = Ticket.CreateUnclassified("TG-CS-1", 1, "x", DateTime.UtcNow);
        owned.AssignTo(Guid.NewGuid());
        Assert.Throws<TicketNotEligibleForResolutionException>(() => owned.CloseForAiCompletion());

        var open = Ticket.CreateUnclassified("TG-CS-2", 1, "x", DateTime.UtcNow);
        Assert.Equal(TicketStatus.Open, open.CloseForAiCompletion());
        Assert.Equal(TicketStatus.Closed, open.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Resolved, open.ResolutionOutcome);
        Assert.Throws<TicketClosedException>(() => open.CloseForAiCompletion());
    }
}
