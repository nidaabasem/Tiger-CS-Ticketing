using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.GenesysIntegration;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Fakes;

/// <summary>
/// Wires the Genesys ingestion and conversation-end services over the REAL
/// intake, ticket-creation and customer-lookup application services — only
/// the repositories and gateways are doubles.
///
/// <para>
/// That is deliberate, and is what these tests are for: the phase's central
/// claim is that Genesys reuses the existing ticket flow rather than
/// reimplementing it, so a fixture that stubbed
/// <c>TicketCreationAppService</c> would assert nothing about the claim. A
/// Genesys ticket here goes through the same ticket-number generation,
/// status history, SLA clock, auto-assignment, outbox and audit path an
/// agent-created ticket does.
/// </para>
/// </summary>
public sealed class GenesysServiceFixture
{
    public GenesysOptions Options { get; }
    public GenesysInquiryIngestionAppService Ingestion { get; }
    public GenesysConversationEndAppService ConversationEnd { get; }
    public TicketInteractionQueryAppService InteractionQuery { get; }

    /// <summary>
    /// The ordinary, non-Genesys ticket-creation service — the very same
    /// instance the Genesys ingestion composes. Exposed so a test can prove
    /// that manual/Face-to-Face creation still works with the Genesys feature
    /// flag off, through the real path rather than a parallel one.
    /// </summary>
    public TicketCreationAppService ManualTicketCreation { get; }

    /// <summary>The Unclassified → Classified transition an agent performs after reading the inquiry.</summary>
    public TicketClassificationAppService Classification { get; }

    public FakeChannelRepository Channels { get; } = new FakeChannelRepository().SeedWellKnown();
    public FakeDepartmentRepository Departments { get; } = new();
    public FakeCategoryRepository Categories { get; } = new();
    public FakeTicketRepository Tickets { get; } = new();
    public FakeIntakeRecordRepository IntakeRecords { get; } = new();
    public FakeTicketInteractionRepository Interactions { get; } = new();
    public FakeGenesysQueueMappingRepository QueueMappings { get; } = new();
    public FakeGenesysConversationRepository Conversations { get; }
    public FakeAuditEntryWriter Audit { get; } = new();
    public FakeTicketingUnitOfWork UnitOfWork { get; } = new();
    public FakeCrmBuyerLookupGateway CrmBuyers { get; } = new();
    public FakePactCustomerLookupGateway Pact { get; } = new();
    public FakeTasleehGateway Tasleeh { get; } = new();
    public FakeUserDepartmentAssignmentRepository DepartmentAssignments { get; } = new();
    public FakeTicketAssignmentRepository TicketAssignments { get; } = new();
    public FakeTicketStatusHistoryRepository StatusHistory { get; } = new();

    /// <summary>The SLA services ticket creation runs through — exposed so a test can assert that an unclassified inquiry opened no period at all.</summary>
    public SlaServiceFixture Sla { get; }

    public GenesysServiceFixture(bool enabled = true)
    {
        Options = new GenesysOptions { Enabled = enabled };
        Conversations = new FakeGenesysConversationRepository(Interactions);

        var outbox = new FakeOutboxWriter();
        UnitOfWork.OutboxWriter = outbox;
        var sla = new SlaServiceFixture(Tickets, statusHistory: StatusHistory, audit: Audit, unitOfWork: UnitOfWork);
        Sla = sla;

        var intakeRecordAppService = new IntakeRecordAppService(
            IntakeRecords, Departments, Channels, UnitOfWork, Audit, TimeProvider.System);

        var ticketCreationAppService = new TicketCreationAppService(
            IntakeRecords, new FakeUnitReferenceRepository(), new FakeContactReferenceRepository(),
            Categories, new FakePriorityRepository(), Departments, Tickets,
            new FakeTicketRequesterSnapshotRepository(), StatusHistory, UnitOfWork, Audit, outbox,
            sla.DueDates, TimeProvider.System, new FakeRequestTypeRepository(), Interactions,
            new TicketAutoAssignmentService(
                new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(),
                DepartmentAssignments, TicketAssignments, Audit),
            new FakeWorkflowTemplateRepository());
        ManualTicketCreation = ticketCreationAppService;

        // The same customer lookup the New Ticket wizard uses — composed
        // exactly as CustomerSearchAppServiceTests composes it.
        var customerLookup = new CustomerLookupAppService(
            IntakeRecords, new FakeDepartmentCustomerLookupSourceRepository(), new FakeCrmCustomerLookupGateway(),
            Pact, Tasleeh,
            new CrmUnitLookupAppService(
                new FakeCrmGateway(), new FakeUnitReferenceRepository(), new FakeContactReferenceRepository(),
                new FakeCustomerVerificationUnitOfWork(), TimeProvider.System));
        var customerSearch = new CustomerSearchAppService(
            new CrmBuyerLookupAppService(CrmBuyers, NullLogger<CrmBuyerLookupAppService>.Instance), customerLookup);

        Ingestion = new GenesysInquiryIngestionAppService(
            Options, Conversations, QueueMappings, Departments, Channels,
            Tickets, intakeRecordAppService, ticketCreationAppService, customerSearch, Audit, UnitOfWork);

        Classification = new TicketClassificationAppService(
            Tickets, Categories, new FakePriorityRepository(), new FakeRequestTypeRepository(),
            new FakeWorkflowTemplateRepository(), DepartmentAssignments, StatusHistory, UnitOfWork, Audit,
            sla.DueDates, TimeProvider.System);

        ConversationEnd = new GenesysConversationEndAppService(
            Options, Conversations, Tickets, UnitOfWork, Audit, TimeProvider.System);

        InteractionQuery = new TicketInteractionQueryAppService(
            Tickets, Interactions, Conversations, Channels,
            new TicketQueryAppService(
                Tickets, DepartmentAssignments, new FakeTicketResolutionRepository(),
                ReopenPolicy.Default, TimeProvider.System));
    }

    /// <summary>
    /// A department that can receive Genesys inquiries, plus one real
    /// category of that department for the classification step.
    ///
    /// <para>
    /// The category is <b>not</b> configuration the integration reads — the
    /// integration has no category configuration at all any more. It exists
    /// here only so a test can classify the ticket afterwards, exactly as an
    /// agent would.
    /// </para>
    /// </summary>
    public (Department Department, Category Category) SeedGenesysDepartment(string name, string code)
    {
        var department = Departments.AddDepartment(name, code);
        var category = Categories.Seed(department.DepartmentId, $"{name} Enquiry");
        return (department, category);
    }

    /// <summary>The originating interaction recorded for one Genesys conversation, or null.</summary>
    public TicketInteraction? InteractionFor(string conversationId) =>
        Interactions.All.FirstOrDefault(i => i.GenesysConversationId == conversationId);
}
