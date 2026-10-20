using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// What a document request's outcome does to its ticket: Sent closes it (Resolved → Closed), a failure closes it with the actual
/// reason, an unconfirmed outcome and a selection prompt never close it, a second request still in flight holds the closure, and
/// no ticket the existing conversation/customer associations do not name is ever touched. Runs the REAL lifecycle over fakes.
/// </summary>
public class DocumentDeliveryTicketTests
{
    private static readonly Guid ServiceAccount = DocumentServiceFixture.Caller;
    private static readonly Guid SessionId = Guid.NewGuid();
    private const string Phone = "+971501234567";

    private sealed class Rig
    {
        public required GenesysServiceFixture F { get; init; }
        public required FakeCrmDocumentDeliveryRepository Deliveries { get; init; }
        public required DocumentDeliveryTicketService Service { get; init; }
        public required Ticket Ticket { get; init; }
        public required string ConversationId { get; init; }

        public DocumentDeliveryTicketContext Context(string? conversation = "conv-doc", long? consumed = null, int customerId = DocumentServiceFixture.CustomerId, string? phone = Phone) =>
            new(ServiceAccount, SessionId, conversation, consumed, customerId, phone);

        public CrmDocumentDeliveryRequest AddRequest(string key, bool commit = true)
        {
            var row = new CrmDocumentDeliveryRequest(
                ServiceAccount, key, key, SessionId, CrmDocumentType.Contract, "REC-" + key, DocumentDeliveryChannel.Email, "c***@e***.com",
                F.Clock.GetUtcNow().UtcDateTime);
            Deliveries.AddAsync(row);
            if (commit)
            {
                Deliveries.Commit();
            }

            return row;
        }
    }

    private static async Task<Rig> NewRigAsync(string departmentCode = "CS", string conversationId = "conv-doc", string phone = Phone)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", departmentCode);
        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(conversationId, GenesysChannel.WebsiteChat, CustomerPhone: phone, DepartmentId: department.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);

        var deliveries = new FakeCrmDocumentDeliveryRepository();
        var service = new DocumentDeliveryTicketService(
            new CrmDocumentOptions { Enabled = true }, f.Conversations, f.Tickets, deliveries, f.Lifecycle, f.Audit,
            new CommittingUnitOfWork(deliveries), f.Clock, NullLogger<DocumentDeliveryTicketService>.Instance);

        return new Rig { F = f, Deliveries = deliveries, Service = service, Ticket = f.Tickets.All.Single(), ConversationId = conversationId };
    }

    private static DocumentDeliveryTicketUpdate Sent(long? id = 1) =>
        new(DocumentDeliveryTicketEvent.Sent, "Contract", "REC-1", null, "The document was sent to the customer's email on record.", id);

    private static DocumentDeliveryTicketUpdate Failed(long? id = 1) =>
        new(DocumentDeliveryTicketEvent.Failed, "Contract", "REC-1", "DELIVERY_FAILED", "The delivery channel rejected the document.", id);

    private static DocumentDeliveryTicketUpdate Unconfirmed(long? id = 1) =>
        new(DocumentDeliveryTicketEvent.Unconfirmed, "Contract", "REC-1", "DELIVERY_UNCONFIRMED", "May already be in the mailbox.", id);

    // ---- success ----

    [Fact]
    public async Task Sent_ClosesTheTicketThroughResolved_WithTheSuccessReason()
    {
        var r = await NewRigAsync();

        await r.Service.HandleAsync(r.Context(), Sent());

        Assert.Equal(TicketStatus.Closed, r.Ticket.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Resolved, r.Ticket.ResolutionOutcome);
        var resolution = Assert.Single(r.F.Resolutions.Added);
        Assert.Equal("Requested document successfully sent via email.", resolution.ResolutionNote);
        // Open → Resolved, outcome, Resolved → Closed — all carrying the reason.
        Assert.Equal(3, r.F.StatusHistory.Added.Count(h => h.TicketId == r.Ticket.TicketId && h.Note == resolution.ResolutionNote));
        Assert.Contains(r.F.StatusHistory.Added, h =>
            h.Dimension == TicketStatusDimension.TicketStatus && h.OldValue == (byte)TicketStatus.Resolved && h.NewValue == (byte)TicketStatus.Closed);
        Assert.Single(r.F.Audit.Entries, e => e.Action == "AutoCloseDocumentDelivery");
    }

    [Fact]
    public async Task Sent_WhileAnotherRequestOfTheSessionIsStillBeingProcessed_DoesNotCloseYet_AndTheLastOneDoes()
    {
        var r = await NewRigAsync();
        var first = r.AddRequest("a");
        var second = r.AddRequest("b");
        first.MarkDispatching(r.F.Clock.GetUtcNow().UtcDateTime);
        first.MarkSent(r.F.Clock.GetUtcNow().UtcDateTime);

        // First finishes while the second is still in progress: not complete.
        await r.Service.HandleAsync(r.Context(), Sent(first.CrmDocumentDeliveryRequestId));
        Assert.NotEqual(TicketStatus.Closed, r.Ticket.TicketStatus);
        Assert.DoesNotContain(r.F.Audit.Entries, e => e.Action == "AutoCloseDocumentDelivery");

        second.MarkDispatching(r.F.Clock.GetUtcNow().UtcDateTime);
        second.MarkSent(r.F.Clock.GetUtcNow().UtcDateTime);
        await r.Service.HandleAsync(r.Context(), Sent(second.CrmDocumentDeliveryRequestId));

        Assert.Equal(TicketStatus.Closed, r.Ticket.TicketStatus);
        Assert.Single(r.F.Audit.Entries, e => e.Action == "AutoCloseDocumentDelivery");
    }

    [Fact]
    public async Task AnAbandonedRequest_IsNotInFlight_AndDoesNotHoldTheClosureForever()
    {
        var r = await NewRigAsync();
        var abandoned = r.AddRequest("old");
        var current = r.AddRequest("now");
        r.F.Clock.Advance(TimeSpan.FromMinutes(6)); // past InProgressStaleAfterMinutes (5)
        current.MarkDispatching(r.F.Clock.GetUtcNow().UtcDateTime);
        current.MarkSent(r.F.Clock.GetUtcNow().UtcDateTime);

        await r.Service.HandleAsync(r.Context(), Sent(current.CrmDocumentDeliveryRequestId));

        Assert.NotNull(abandoned);
        Assert.Equal(TicketStatus.Closed, r.Ticket.TicketStatus);
    }

    [Fact]
    public async Task ReportingTheSameOutcomeTwice_ClosesAndAuditsOnce()
    {
        var r = await NewRigAsync();

        await r.Service.HandleAsync(r.Context(), Sent());
        await r.Service.HandleAsync(r.Context(), Sent());
        await r.Service.HandleAsync(r.Context(), Failed());

        Assert.Single(r.F.Resolutions.Added);
        Assert.Single(r.F.Audit.Entries, e => e.Action == "AutoCloseDocumentDelivery");
        Assert.Equal((byte)ResolutionOutcome.Resolved, r.Ticket.ResolutionOutcome);
    }

    // ---- failure ----

    [Fact]
    public async Task Failed_ClosesTheTicketWithTheActualReason_Code_DocumentType_AndTime_NeverAsSuccess_AndWithoutTransfer()
    {
        var r = await NewRigAsync();
        var departmentBefore = r.Ticket.CurrentDepartmentId;
        var context = r.Context();
        context.ProviderFailureCode = "SMTP_550";

        await r.Service.HandleAsync(context, Failed());

        Assert.Equal(TicketStatus.Closed, r.Ticket.TicketStatus);
        // Not "Resolved": nothing was delivered.
        Assert.Equal((byte)ResolutionOutcome.Cancelled, r.Ticket.ResolutionOutcome);
        var resolution = Assert.Single(r.F.Resolutions.Added);
        Assert.Equal(
            "Document delivery failed — The delivery channel rejected the document. (provider code SMTP_550)", resolution.ResolutionNote);
        Assert.DoesNotContain("successfully", resolution.ResolutionNote);
        var audit = Assert.Single(r.F.Audit.Entries, e => e.Action == "AutoCloseDocumentDelivery");
        Assert.Contains("DocumentType=Contract", audit.AfterValue);
        Assert.Contains("ErrorCode=DELIVERY_FAILED", audit.AfterValue);
        Assert.Contains($"FailedAtUtc={r.F.Clock.GetUtcNow().UtcDateTime:O}", audit.AfterValue);
        Assert.Contains("Document delivery failed — The delivery channel rejected", audit.AfterValue);
        // Never handed to the Call Center (or anyone): same department, no assignment.
        Assert.Equal(departmentBefore, r.Ticket.CurrentDepartmentId);
        Assert.Equal(departmentBefore, r.Ticket.OriginatingDepartmentId);
        Assert.Empty(r.F.TicketAssignments.Added);
    }

    [Fact]
    public async Task Failed_WhileAnotherRequestIsInFlight_IsRecordedOnTheTicket_ButDoesNotCloseIt()
    {
        var r = await NewRigAsync();
        r.AddRequest("a");
        r.AddRequest("b");

        await r.Service.HandleAsync(r.Context(), Failed(1));

        Assert.NotEqual(TicketStatus.Closed, r.Ticket.TicketStatus);
        var entry = Assert.Single(r.F.Audit.Entries, e => e.Action == "DocumentDeliveryFailed");
        Assert.Contains("ErrorCode=DELIVERY_FAILED", entry.AfterValue);
    }

    // ---- uncertain ----

    [Fact]
    public async Task Unconfirmed_NeverClosesTheTicket_AndIsRecordedAsUncertain()
    {
        var r = await NewRigAsync();

        await r.Service.HandleAsync(r.Context(), Unconfirmed());

        Assert.NotEqual(TicketStatus.Closed, r.Ticket.TicketStatus);
        Assert.Empty(r.F.Resolutions.Added);
        var entry = Assert.Single(r.F.Audit.Entries, e => e.Action == "DocumentDeliveryUnconfirmed");
        Assert.Contains("may already be in the customer's mailbox", entry.AfterValue);
        Assert.Contains("not resent", entry.AfterValue);
        Assert.DoesNotContain(r.F.Audit.Entries, e => e.Action == "AutoCloseDocumentDelivery");
    }

    // ---- the right ticket only ----

    [Fact]
    public async Task AnotherConversationsTicket_IsNeverClosed()
    {
        var r = await NewRigAsync();
        var other = await r.F.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-other", GenesysChannel.WebsiteChat, CustomerPhone: "+971509999999",
                DepartmentId: r.Ticket.CurrentDepartmentId));
        var otherTicket = r.F.Tickets.All.Single(t => t.TicketId == other.Ticket!.TicketId);

        await r.Service.HandleAsync(r.Context(), Sent());

        Assert.Equal(TicketStatus.Closed, r.Ticket.TicketStatus);
        Assert.NotEqual(TicketStatus.Closed, otherTicket.TicketStatus);
    }

    [Theory]
    [InlineData("unknown-conversation")]
    [InlineData(null)]
    public async Task NoTicketNamedByTheAssociations_MeansNothingIsClosed(string? conversation)
    {
        var r = await NewRigAsync();

        await r.Service.HandleAsync(r.Context(conversation), Sent());

        Assert.NotEqual(TicketStatus.Closed, r.Ticket.TicketStatus);
        Assert.DoesNotContain(r.F.Audit.Entries, e => e.Action == "AutoCloseDocumentDelivery");
    }

    [Fact]
    public async Task AConversationFromAnotherCaller_IsNotClosedByThisCustomersRequest()
    {
        var r = await NewRigAsync(phone: "+971500000099");

        await r.Service.HandleAsync(r.Context(phone: Phone), Sent());

        Assert.NotEqual(TicketStatus.Closed, r.Ticket.TicketStatus);
    }

    [Fact]
    public async Task ATicketAlreadyBoundToAnotherCrmCustomer_IsNotClosed()
    {
        var r = await NewRigAsync();
        typeof(Ticket).GetProperty(nameof(Ticket.CrmBuyerCustomerId))!.SetValue(r.Ticket, 4242);

        await r.Service.HandleAsync(r.Context(), Sent());

        Assert.NotEqual(TicketStatus.Closed, r.Ticket.TicketStatus);
    }

    [Fact]
    public async Task WithoutAConversationId_TheTicketThatConsumedTheSession_IsUsed()
    {
        var r = await NewRigAsync();

        await r.Service.HandleAsync(r.Context(conversation: null, consumed: r.Ticket.TicketId), Sent());

        Assert.Equal(TicketStatus.Closed, r.Ticket.TicketStatus);
    }

    [Fact]
    public async Task ATicketAlreadyClosedByInactivity_IsLeftAlone()
    {
        var r = await NewRigAsync();
        r.Ticket.CloseForCustomerInactivity();
        var resolutionsBefore = r.F.Resolutions.Added.Count;

        await r.Service.HandleAsync(r.Context(), Sent());

        Assert.Equal(resolutionsBefore, r.F.Resolutions.Added.Count);
        Assert.DoesNotContain(r.F.Audit.Entries, e => e.Action == "AutoCloseDocumentDelivery");
    }

    // ---- the document service reports exactly the right events ----

    private sealed class RecordingHandler : IDocumentDeliveryTicketHandler
    {
        public List<(DocumentDeliveryTicketContext Context, DocumentDeliveryTicketUpdate Update)> Calls { get; } = [];

        public int Activity { get; private set; }

        public Task RecordCustomerActivityAsync(DocumentDeliveryTicketContext context, CancellationToken cancellationToken = default)
        {
            Activity++;
            return Task.CompletedTask;
        }

        public Task HandleAsync(DocumentDeliveryTicketContext context, DocumentDeliveryTicketUpdate update, CancellationToken cancellationToken = default)
        {
            Calls.Add((context, update));
            return Task.CompletedTask;
        }
    }

    private static (DocumentServiceFixture F, RecordingHandler Handler) Wired()
    {
        var handler = new RecordingHandler();
        return (new DocumentServiceFixture(ticketHandler: handler), handler);
    }

    private static CrmDocumentCopyRequestDto Request(DocumentServiceFixture f, string? recordId = null) =>
        new(f.SessionId, "Contract", null, recordId) { ConversationId = "conv-123" };

    [Fact]
    public async Task ASentDocument_ReportsSent_WithTheConversation()
    {
        var (f, handler) = Wired();
        f.Own("REC-1");

        var result = await f.Service.SendAsync(DocumentServiceFixture.Caller, Request(f), "k1");

        Assert.Equal(CrmDocumentCopyStatus.Sent, result.Status);
        var (context, update) = Assert.Single(handler.Calls);
        Assert.Equal(DocumentDeliveryTicketEvent.Sent, update.Event);
        Assert.Equal("conv-123", context.ConversationId);
        Assert.Equal(f.SessionId, context.VerificationSessionId);
        Assert.Equal(DocumentServiceFixture.CustomerId, context.CrmCustomerId);
    }

    [Fact]
    public async Task SelectionRequired_IsNotAFailure_AndTheWorkflowStaysOpen_UntilTheChosenOneIsSent()
    {
        var (f, handler) = Wired();
        f.Own("REC-1");
        f.Own("REC-2");

        var prompt = await f.Service.SendAsync(DocumentServiceFixture.Caller, Request(f), "k1");
        Assert.Equal(CrmDocumentCopyStatus.SelectionRequired, prompt.Status);
        Assert.Empty(handler.Calls);

        var chosen = await f.Service.SendAsync(DocumentServiceFixture.Caller, Request(f, "REC-2"), "k2");
        Assert.Equal(CrmDocumentCopyStatus.Sent, chosen.Status);
        Assert.Equal(DocumentDeliveryTicketEvent.Sent, Assert.Single(handler.Calls).Update.Event);
    }

    [Fact]
    public async Task ARejectedDelivery_ReportsFailed_WithTheProvidersCode()
    {
        var (f, handler) = Wired();
        f.Own("REC-1");
        f.Email.NextOutcome = DocumentDeliveryOutcome.PermanentFailure;

        var result = await f.Service.SendAsync(DocumentServiceFixture.Caller, Request(f), "k1");

        Assert.Equal(CrmDocumentCopyStatus.DeliveryFailed, result.Status);
        var (context, update) = Assert.Single(handler.Calls);
        Assert.Equal(DocumentDeliveryTicketEvent.Failed, update.Event);
        Assert.Equal(CrmDocumentCodes.DeliveryFailed, update.Code);
        Assert.Equal("Contract", update.DocumentType);
        Assert.Equal("FAKE_FAILURE", context.ProviderFailureCode);
        Assert.Contains("rejected", update.Reason);
    }

    [Fact]
    public async Task ADocumentThatIsNotOnRecord_ReportsFailed()
    {
        var (f, handler) = Wired();

        var result = await f.Service.SendAsync(DocumentServiceFixture.Caller, Request(f), "k1");

        Assert.Equal(CrmDocumentCopyStatus.DocumentUnavailable, result.Status);
        Assert.Equal(DocumentDeliveryTicketEvent.Failed, Assert.Single(handler.Calls).Update.Event);
    }

    [Fact]
    public async Task AnUnknownProviderOutcome_ReportsUnconfirmedOnce_NeverResends_AndAReplayAddsNothing()
    {
        var (f, handler) = Wired();
        f.Own("REC-1");
        f.Email.ThrowOnSend = new InvalidOperationException("connection reset after DATA");

        var first = await f.Service.SendAsync(DocumentServiceFixture.Caller, Request(f), "k1");
        var replay = await f.Service.SendAsync(DocumentServiceFixture.Caller, Request(f), "k1");

        Assert.Equal(CrmDocumentCopyStatus.DeliveryUnconfirmed, first.Status);
        // Inside the stale window the same key just reports 'still being processed' — and never resends.
        Assert.Equal(CrmDocumentCopyStatus.Queued, replay.Status);
        Assert.Empty(f.Email.Sent);
        Assert.Equal(DocumentDeliveryTicketEvent.Unconfirmed, Assert.Single(handler.Calls).Update.Event);
    }

    [Fact]
    public async Task ARequestThatIsRefusedBeforeAnyDelivery_TouchesNoTicket()
    {
        var (f, handler) = Wired();
        f.Own("REC-1");

        await f.Service.SendAsync(DocumentServiceFixture.Caller, new CrmDocumentCopyRequestDto(Guid.NewGuid(), "Contract") { ConversationId = "conv-123" }, "k1");
        await f.Service.SendAsync(DocumentServiceFixture.Caller, Request(f), null);

        Assert.Empty(handler.Calls);
    }
}
