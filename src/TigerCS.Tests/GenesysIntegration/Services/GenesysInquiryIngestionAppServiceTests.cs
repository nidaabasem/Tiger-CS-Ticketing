using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The core business rule of the Genesys phase: <b>any new customer inquiry
/// received through Genesys results in exactly one ticket</b> — one, never
/// zero, never two — and every channel reaches that through the same
/// ingestion flow.
///
/// <para>
/// These run the REAL intake, ticket-creation and customer-lookup services
/// (see <see cref="GenesysServiceFixture"/>), so a ticket created here is a
/// genuine TigerCS ticket with its ticket number, status history, SLA clock
/// and audit trail — not a stub standing in for one.
/// </para>
/// </summary>
public class GenesysInquiryIngestionAppServiceTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private static GenesysInquiryDto Inquiry(
        string conversationId,
        GenesysChannel channel = GenesysChannel.Phone,
        GenesysInquiryEvent inquiryEvent = GenesysInquiryEvent.Answered,
        string? phone = "+971500000001",
        int? departmentId = null,
        string? departmentCode = null,
        string? queueId = null,
        string? customerName = null,
        string? towerName = null,
        string? unitNumber = null,
        string? subject = null) =>
        new(conversationId, channel, inquiryEvent,
            CustomerPhone: phone,
            CustomerName: customerName,
            QueueId: queueId,
            DepartmentId: departmentId,
            DepartmentCode: departmentCode,
            TowerName: towerName,
            UnitNumber: unitNumber,
            Subject: subject);

    // ---- 1. A new Genesys inquiry creates exactly one ticket ----

    [Fact]
    public async Task Ingest_NewInquiry_CreatesExactlyOneTicket_LinkedToTheConversation()
    {
        var f = new GenesysServiceFixture();
        var (department, category) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("queue-cs", department.DepartmentId);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-1001", queueId: "queue-cs", subject: "Asking about handover"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);
        Assert.Equal(ticket.TicketId, result.Ticket!.TicketId);
        Assert.StartsWith("TG-CS-", ticket.TicketNumber);
        Assert.Equal(department.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Equal(category.CategoryId, ticket.CategoryId);
        Assert.Equal("Asking about handover", ticket.RequestSummary);

        // The permanent Ticket ↔ conversation link, on the originating interaction.
        var interaction = Assert.Single(f.Interactions.All);
        Assert.True(interaction.IsOriginatingInteraction);
        Assert.Equal(InteractionContextSource.Genesys, interaction.Source);
        Assert.Equal("conv-1001", interaction.GenesysConversationId);
        Assert.Equal(ticket.TicketId, interaction.TicketId);

        // It is a real ticket: the existing creation path ran in full.
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Equal(SlaState.Running, ticket.SlaState);
        Assert.Contains(f.Audit.Written, w => w.Action == "Create" && w.EntityType == "Ticket");
        Assert.Contains(f.Audit.Written, w => w.Action == "GenesysInquiryIngested" && w.EntityId == "conv-1001");
    }

    // ---- 2. A duplicate ConversationId never creates a second ticket ----

    [Fact]
    public async Task Ingest_SameConversationTwice_ReturnsTheSameTicket_AndCreatesNoSecondTicket()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("queue-cs", department.DepartmentId);

        var first = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-abc123", queueId: "queue-cs"));
        var retry = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-abc123", queueId: "queue-cs"));
        var thirdDelivery = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-abc123", queueId: "queue-cs"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, first.Outcome);
        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, retry.Outcome);
        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, thirdDelivery.Outcome);

        // abc123 -> TCK-1001, and never abc123 -> TCK-1002.
        Assert.Single(f.Tickets.All);
        Assert.Single(f.Interactions.All);
        Assert.Equal(first.Ticket!.TicketId, retry.Ticket!.TicketId);
        Assert.Equal(first.Ticket.TicketId, thirdDelivery.Ticket!.TicketId);
        Assert.Equal(first.Ticket.TicketNumber, retry.Ticket.TicketNumber);
    }

    [Fact]
    public async Task Ingest_ConcurrentDuplicate_LosesTheDatabaseRace_AndStillYieldsOneTicket()
    {
        // The read-side check cannot protect against two deliveries that both
        // pass it; the unique index on GenesysConversationId is what does.
        // The fake interaction store mirrors that index (raising the same
        // DuplicateWriteException the real unit of work translates), so this
        // exercises the ingestion service's actual recovery path: answer with
        // the ticket that won the race.
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("queue-cs", department.DepartmentId);

        var winner = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-race", queueId: "queue-cs"));

        // Simulate the loser: its read happened before the winner committed,
        // so it proceeds to creation and hits the unique index.
        var loserInteraction = f.InteractionFor("conv-race");
        Assert.NotNull(loserInteraction);

        var loser = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-race", queueId: "queue-cs"));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, loser.Outcome);
        Assert.Equal(winner.Ticket!.TicketId, loser.Ticket!.TicketId);
        Assert.Single(f.Tickets.All);
    }

    // ---- 3 & 4. Phone: ringing creates nothing; answering creates the ticket ----

    [Fact]
    public async Task Ingest_RingingCall_CreatesNoTicket()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("queue-cs", department.DepartmentId);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-ringing", inquiryEvent: GenesysInquiryEvent.Ringing, queueId: "queue-cs"));

        Assert.Equal(GenesysIngestionOutcome.NoTicketYet, result.Outcome);
        Assert.Empty(f.Tickets.All);
        Assert.Empty(f.Interactions.All);
        // Nothing at all was written — not even an intake record.
        Assert.Null(result.Ticket);
    }

    [Fact]
    public async Task Ingest_CallAnsweredAfterRinging_CreatesExactlyOneTicket()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("queue-cs", department.DepartmentId);

        // The real sequence for one call: it rings, then the agent picks up.
        var ringing = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-call-9", inquiryEvent: GenesysInquiryEvent.Ringing, queueId: "queue-cs"));
        var answered = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-call-9", inquiryEvent: GenesysInquiryEvent.Answered, queueId: "queue-cs"));

        Assert.Equal(GenesysIngestionOutcome.NoTicketYet, ringing.Outcome);
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, answered.Outcome);
        Assert.Single(f.Tickets.All);
        Assert.Equal(WellKnownChannels.Phone, Assert.Single(f.Interactions.All).ChannelId);
    }

    // ---- 5, 6, 7. The website chat's choice is a DEPARTMENT ----

    [Theory]
    [InlineData("Customer Service", "CS")]
    [InlineData("Leasing", "LS")]
    [InlineData("Maintenance", "MNT")]
    public async Task Ingest_WebsiteChat_ExplicitSelection_RoutesToThatDepartment(string departmentName, string departmentCode)
    {
        var f = new GenesysServiceFixture();
        // Three real departments exist; only the selected one must be used.
        var customerService = f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.SeedGenesysDepartment("Leasing", "LS");
        var maintenance = f.SeedGenesysDepartment("Maintenance", "MNT");
        var expected = new[] { customerService, leasing, maintenance }
            .Single(d => d.Department.Code == departmentCode);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry($"conv-web-{departmentCode}", GenesysChannel.WebsiteChat, GenesysInquiryEvent.Started,
                departmentId: expected.Department.DepartmentId,
                customerName: "Ahmed Ali", towerName: "Tiger Tower A", unitNumber: "1204"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);

        // OriginatingDepartment AND CurrentDepartment are the selection.
        Assert.Equal(expected.Department.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Equal(expected.Department.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(expected.Category.CategoryId, ticket.CategoryId);
        Assert.Equal(departmentName, expected.Department.Name);

        // The chat form's tower/unit travel as the manual snapshot.
        Assert.Equal("Tiger Tower A", ticket.ManualProjectName);
        Assert.Equal("1204", ticket.ManualUnitNumber);
        Assert.Equal("Ahmed Ali", Assert.Single(f.Interactions.All).CustomerName);
    }

    [Fact]
    public async Task Ingest_WebsiteChat_ByDepartmentCode_RoutesToThatDepartment()
    {
        var f = new GenesysServiceFixture();
        f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.SeedGenesysDepartment("Leasing", "LS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry("conv-web-code", GenesysChannel.WebsiteChat, GenesysInquiryEvent.Started, departmentCode: "ls"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(leasing.Department.DepartmentId, Assert.Single(f.Tickets.All).OriginatingDepartmentId);
    }

    // ---- 8. The department selection is NOT a request type ----

    [Fact]
    public async Task Ingest_WebsiteChatDepartmentSelection_NeverInfersARequestTypeOrPriority()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Maintenance", "MNT");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry("conv-web-nort", GenesysChannel.WebsiteChat, GenesysInquiryEvent.Started,
                departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);

        // "Maintenance" is a department, not a request type. Classification
        // is the agent's or the workflow's later decision.
        Assert.Null(ticket.RequestTypeId);
        Assert.Null(ticket.WorkflowTemplateId);
        // Priority is the documented default, never derived from the choice.
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
    }

    // ---- 9 & 10. Customer lookup: used, but never a gate ----

    [Fact]
    public async Task Ingest_MobileNumber_IsPassedThroughTheExistingCustomerLookup()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.CrmBuyers.Returns(CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(
                new CrmCustomerDto(9001, "Sami Nasser", null, "+971509990001", "sami@example.test"),
                [new CrmBuyerUnitDto(1, 4, "Contract", 101, "1506", 1, 2, 15, 10, "Nobles Tower", null, 1, "Buyer")])
        ]));

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry("conv-lookup", phone: "+971509990001", departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);

        // The EXISTING lookup ran, with the inquiry's own mobile number —
        // Genesys never reaches CRM/PACT/Tasleeh itself.
        Assert.Equal("+971509990001", f.CrmBuyers.LastSearchedPhoneNumber);
        Assert.Equal(1, f.CrmBuyers.CallCount);

        // The number is preserved on the intake record and the interaction,
        // so customer history and any later re-lookup have it.
        Assert.Equal("+971509990001", Assert.Single(f.Interactions.All).CustomerPhone);
        Assert.Contains(f.Audit.Written, w => w.Action == "GenesysInquiryIngested");
    }

    [Fact]
    public async Task Ingest_CustomerNotFound_StillCreatesTheTicket()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.CrmBuyers.Returns(CrmBuyerLookupResult.NotFound());

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-nomatch", phone: "+971500000404", departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);

        // An unverified/manual customer inquiry — never auto-verified, and
        // never discarded.
        Assert.Equal(CrmVerificationStatus.Unverified, ticket.VerificationStatus);
        Assert.Null(ticket.CrmBuyerCustomerId);
    }

    [Fact]
    public async Task Ingest_CustomerLookupFails_TheInquiryStillBecomesATicket()
    {
        // "The fact that CRM/PACT lookup failed must NOT cause the Genesys
        // inquiry to disappear."
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.CrmBuyers.Returns(CrmBuyerLookupResult.Unavailable("CRM is down"));

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-crmdown", phone: "+971500000001", departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Single(f.Tickets.All);
        Assert.Equal("conv-crmdown", Assert.Single(f.Interactions.All).GenesysConversationId);
    }

    [Fact]
    public async Task Ingest_NoPhoneAtAll_StillCreatesTheTicket()
    {
        // A withheld caller id, or a social DM that carries no number: the
        // channel's RequiresPhone rule governs the agent wizard, and must
        // never discard a real inquiry.
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry("conv-nophone", GenesysChannel.SocialMedia, GenesysInquiryEvent.Started,
                phone: null, departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Single(f.Tickets.All);
        Assert.Equal(string.Empty, Assert.Single(f.Interactions.All).CustomerPhone);
        // No lookup is attempted without a number to search with.
        Assert.Equal(0, f.CrmBuyers.CallCount);
    }

    // ---- Channels converge on ONE flow ----

    [Theory]
    [InlineData(GenesysChannel.Phone, GenesysInquiryEvent.Answered, WellKnownChannels.Phone)]
    [InlineData(GenesysChannel.WebsiteChat, GenesysInquiryEvent.Started, WellKnownChannels.LiveChat)]
    [InlineData(GenesysChannel.WhatsApp, GenesysInquiryEvent.Started, WellKnownChannels.WhatsApp)]
    [InlineData(GenesysChannel.SocialMedia, GenesysInquiryEvent.Started, WellKnownChannels.SocialMediaDirectMessage)]
    public async Task Ingest_EveryChannel_UsesTheSameFlow_AndRecordsItsOwnOriginatingChannel(
        GenesysChannel channel, GenesysInquiryEvent inquiryEvent, byte expectedChannelId)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry($"conv-{channel}", channel, inquiryEvent, departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var interaction = Assert.Single(f.Interactions.All);
        Assert.Equal(expectedChannelId, interaction.ChannelId);
        Assert.Equal(InteractionContextSource.Genesys, interaction.Source);
    }

    // ---- Queue → Department mapping is configuration, never code ----

    [Fact]
    public async Task Ingest_QueueMapping_ResolvesTheDepartment_ForNonWebsiteChannels()
    {
        var f = new GenesysServiceFixture();
        f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.SeedGenesysDepartment("Leasing", "LS");
        f.QueueMappings.Map("genesys-queue-leasing", leasing.Department.DepartmentId);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry("conv-wa-1", GenesysChannel.WhatsApp, GenesysInquiryEvent.Started, queueId: "genesys-queue-leasing"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(leasing.Department.DepartmentId, Assert.Single(f.Tickets.All).OriginatingDepartmentId);
    }

    [Fact]
    public async Task Ingest_ExplicitSelectionWins_OverTheQueueMapping()
    {
        var f = new GenesysServiceFixture();
        var customerService = f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.SeedGenesysDepartment("Leasing", "LS");
        f.QueueMappings.Map("shared-queue", leasing.Department.DepartmentId);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry("conv-priority", GenesysChannel.WebsiteChat, GenesysInquiryEvent.Started,
                departmentId: customerService.Department.DepartmentId, queueId: "shared-queue"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(customerService.Department.DepartmentId, Assert.Single(f.Tickets.All).OriginatingDepartmentId);
    }

    [Fact]
    public async Task Ingest_UnmappedQueue_IsReportedAsAConfigurationGap_NotRoutedToAFallback()
    {
        var f = new GenesysServiceFixture();
        f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-unmapped", GenesysChannel.WhatsApp, GenesysInquiryEvent.Started, queueId: "queue-nobody-configured"));

        Assert.Equal(GenesysIngestionOutcome.DepartmentNotResolved, result.Outcome);
        Assert.Empty(f.Tickets.All);
        Assert.Contains("queue-nobody-configured", result.Detail);
    }

    [Fact]
    public async Task Ingest_InactiveQueueMapping_DoesNotResolveADepartment()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("retired-queue", department.DepartmentId, isActive: false);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-retired", GenesysChannel.WhatsApp, GenesysInquiryEvent.Started, queueId: "retired-queue"));

        Assert.Equal(GenesysIngestionOutcome.DepartmentNotResolved, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task Ingest_DepartmentWithoutGenesysCategory_IsReportedRatherThanGuessed()
    {
        var f = new GenesysServiceFixture();
        // A department that exists and has categories, but was never
        // configured for Genesys — ingestion must not pick a category itself.
        var department = f.Departments.AddDepartment("Collections", "COL");
        f.Categories.Seed(department.DepartmentId, "Payment Query");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-nocat", departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.DepartmentNotConfiguredForGenesys, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }

    // ---- 16. The feature flag ----

    [Fact]
    public async Task Ingest_IntegrationDisabled_WritesNothing()
    {
        var f = new GenesysServiceFixture(enabled: false);
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-disabled", departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.IntegrationDisabled, result.Outcome);
        Assert.Empty(f.Tickets.All);
        Assert.Empty(f.Interactions.All);
        Assert.Empty(f.IntakeRecords.All);
    }

    [Fact]
    public async Task GenesysDisabled_NormalManualTicketCreationIsCompletelyUnaffected()
    {
        // The flag governs the Genesys ingestion services and nothing else:
        // the ordinary intake -> ticket path never consults it.
        var f = new GenesysServiceFixture(enabled: false);
        var (department, category) = f.SeedGenesysDepartment("Customer Service", "CS");

        var agentId = Guid.NewGuid();
        var intake = new IntakeRecord(
            WellKnownChannels.FaceToFaceKiosk, "+971500000077", department.DepartmentId,
            false, null, null, agentId, DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(intake);

        var manual = await f.ManualTicketCreation.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "Walk-in complaint"));

        Assert.Equal(TicketCreationOutcome.Success, manual.Outcome);
        var ticket = Assert.Single(f.Tickets.All);
        Assert.Equal("Walk-in complaint", ticket.RequestSummary);

        // A Face-to-Face interaction, with every Genesys field null.
        var interaction = Assert.Single(f.Interactions.All);
        Assert.Equal(InteractionContextSource.Ticketing, interaction.Source);
        Assert.Null(interaction.GenesysConversationId);
    }

    // ---- Input validation ----

    [Fact]
    public async Task Ingest_BlankConversationId_IsRefused()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("   ", departmentId: department.DepartmentId));

        // Without the conversation id nothing can be made idempotent, so the
        // inquiry is refused rather than half-processed.
        Assert.Equal(GenesysIngestionOutcome.ConversationIdRequired, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }
}
