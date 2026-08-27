using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Business-rule change: a single ticket-creation path for every
/// IntakeRecord — unit-related or not, a customer match found or not. These
/// tests are the app-service half of the required regression coverage: the
/// domain half lives in TicketTests/TicketSlaDimensionTests, and the
/// end-to-end half in TicketingEndpointsTests.
/// </summary>
public class TicketCreationAppServiceTests
{
    private sealed record Fixture(
        TicketCreationAppService Service,
        FakeIntakeRecordRepository IntakeRecords,
        FakeUnitReferenceRepository UnitReferences,
        FakeContactReferenceRepository ContactReferences,
        FakeCategoryRepository Categories,
        FakePriorityRepository Priorities,
        FakeDepartmentRepository Departments,
        FakeTicketRepository Tickets,
        FakeTicketRequesterSnapshotRepository Snapshots,
        FakeTicketStatusHistoryRepository StatusHistory,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork,
        SlaServiceFixture Sla,
        FakeOutboxWriter Outbox);

    private static Fixture CreateService()
    {
        var intakeRecords = new FakeIntakeRecordRepository();
        var unitReferences = new FakeUnitReferenceRepository();
        var contactReferences = new FakeContactReferenceRepository();
        var categories = new FakeCategoryRepository();
        var priorities = new FakePriorityRepository();
        var departments = new FakeDepartmentRepository();
        var tickets = new FakeTicketRepository();
        var snapshots = new FakeTicketRequesterSnapshotRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();

        // ADR-0013: the TicketCreated Outbox message is written in the same
        // transaction as the ticket, so the fake writer's staged rows follow
        // this unit of work's own commit/rollback.
        var outbox = new FakeOutboxWriter();
        unitOfWork.OutboxWriter = outbox;

        // Business-rule change: the SLA clock now starts immediately for
        // every ticket (nothing about customer lookup pauses it any more),
        // so the SLA services are always part of this harness.
        var sla = new SlaServiceFixture(tickets, statusHistory: statusHistory, audit: audit, unitOfWork: unitOfWork);

        var service = new TicketCreationAppService(
            intakeRecords, unitReferences, contactReferences, categories, priorities, departments,
            tickets, snapshots, statusHistory, unitOfWork, audit, outbox, sla.DueDates, TimeProvider.System);

        return new Fixture(
            service, intakeRecords, unitReferences, contactReferences, categories, priorities, departments,
            tickets, snapshots, statusHistory, audit, unitOfWork, sla, outbox);
    }

    private static async Task<(TigerCS.Domain.Modules.Ticketing.IntakeRecord Record, Guid AgentId)> SeedIntakeAsync(
        FakeIntakeRecordRepository repo, bool isUnitRelated = true, string? rawUnitNumberEntered = "1204")
    {
        var agentId = Guid.NewGuid();
        var record = new TigerCS.Domain.Modules.Ticketing.IntakeRecord(
            Channel.Phone, "+971500000001", null, isUnitRelated, isUnitRelated ? rawUnitNumberEntered : null, priorityHint: null, agentId, DateTime.UtcNow);
        await repo.AddAsync(record);
        return (record, agentId);
    }

    // --- Customer found (unit-related, matched via customer lookup) ---

    [Fact]
    public async Task CreateAsync_MatchedUnitAndContact_CreatesVerifiedTicketAndLinksIntake()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords);
        var unit = f.UnitReferences.Seed("CRM-UNIT-1001", "1204", "Tiger Tower A");
        var contact = f.ContactReferences.Seed(unit.UnitReferenceId, "CRM-CONTACT-2001", "Ahmed Al-Farsi");

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, unit.UnitReferenceId, contact.ContactReferenceId, category.CategoryId, (byte)PriorityLevel.High, "AC not cooling"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal("Verified", result.Response!.VerificationStatus);
        Assert.StartsWith("TG-CS-", result.Response.TicketNumber);
        Assert.Equal(unit.UnitReferenceId, result.Response.UnitReferenceId);
        Assert.Equal(contact.ContactReferenceId, result.Response.ContactReferenceId);

        var linkedIntake = await f.IntakeRecords.GetByIdAsync(intake.IntakeRecordId);
        Assert.Equal(result.Response.TicketId, linkedIntake!.LinkedTicketId);
        Assert.Equal(CrmVerificationStatus.Verified, linkedIntake.CrmVerificationStatus);

        Assert.Single(f.Snapshots.Added);
        Assert.Equal("1204", f.Snapshots.Added[0].SnapshotUnitNumber);

        Assert.Equal(4, f.StatusHistory.Added.Count);
        Assert.Contains(f.Audit.Written, w => w.Action == "Create" && w.EntityType == "Ticket");
    }

    // ---- Data-consistency correction: IntakeRecord.IsUnitRelated must never
    // end up false while its linked Ticket carries a real UnitReferenceId.
    // The current New Ticket wizard always creates the IntakeRecord with
    // IsUnitRelated=false (identification is deferred to customer lookup,
    // run after intake) — these prove the classification catches up once a
    // real Unit is actually selected and linked, and never fabricates one
    // when it wasn't. ----

    [Fact]
    public async Task CreateAsync_IntakeCreatedNotUnitRelated_UnitSelectedFromLookup_UpgradesIntakeToUnitRelated()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        // Mirrors what the New Ticket wizard actually sends today: no unit
        // classification known at intake time.
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);
        var unit = f.UnitReferences.Seed("CRM-UNIT-1001", "1205", "Tiger Sky Tower");
        var contact = f.ContactReferences.Seed(unit.UnitReferenceId, "CRM-CONTACT-2001", "Ahmed Ali");

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, unit.UnitReferenceId, contact.ContactReferenceId, category.CategoryId, (byte)PriorityLevel.High, "AC not cooling"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal(unit.UnitReferenceId, result.Response!.UnitReferenceId);

        var linkedIntake = await f.IntakeRecords.GetByIdAsync(intake.IntakeRecordId);
        // The bad state this correction rules out: a linked Ticket with a
        // real UnitReferenceId while its own IntakeRecord still reports
        // IsUnitRelated=false.
        Assert.True(linkedIntake!.IsUnitRelated);
    }

    [Fact]
    public async Task CreateAsync_IntakeCreatedNotUnitRelated_NoUnitSelected_IntakeRemainsNotUnitRelated()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "General question"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Null(result.Response!.UnitReferenceId);

        var linkedIntake = await f.IntakeRecords.GetByIdAsync(intake.IntakeRecordId);
        Assert.False(linkedIntake!.IsUnitRelated);
    }

    [Fact]
    public async Task CreateAsync_MultipleUnitsAvailable_SelectingOneCorrectlyMarksIntakeUnitRelated_UsesThatUnitsOwnReference()
    {
        // Two distinct units/contacts the way a customer-lookup match with
        // several eligible units would resolve to — the agent selects the
        // second, not the first, one.
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);
        var firstUnit = f.UnitReferences.Seed("CRM-UNIT-1001", "1205", "Tiger Sky Tower");
        var firstContact = f.ContactReferences.Seed(firstUnit.UnitReferenceId, "CRM-CONTACT-2001", "Ahmed Ali");
        var secondUnit = f.UnitReferences.Seed("CRM-UNIT-1002", "1403", "Tiger Sky Tower");
        var secondContact = f.ContactReferences.Seed(secondUnit.UnitReferenceId, "CRM-CONTACT-2002", "Ahmed Ali");

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, secondUnit.UnitReferenceId, secondContact.ContactReferenceId, category.CategoryId, (byte)PriorityLevel.High, "Unit 1403 issue"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal(secondUnit.UnitReferenceId, result.Response!.UnitReferenceId);
        Assert.NotEqual(firstUnit.UnitReferenceId, result.Response.UnitReferenceId);

        var linkedIntake = await f.IntakeRecords.GetByIdAsync(intake.IntakeRecordId);
        Assert.True(linkedIntake!.IsUnitRelated);
    }

    [Fact]
    public async Task CreateAsync_SecondTicketNumberSameDayDepartment_IncrementsSequence()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);

        var (intake1, agent1) = await SeedIntakeAsync(f.IntakeRecords);
        var first = await f.Service.CreateAsync(
            agent1, new CreateTicketRequestDto(intake1.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "Issue 1"));

        var (intake2, agent2) = await SeedIntakeAsync(f.IntakeRecords);
        var second = await f.Service.CreateAsync(
            agent2, new CreateTicketRequestDto(intake2.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "Issue 2"));

        Assert.NotEqual(first.Response!.TicketNumber, second.Response!.TicketNumber);
        Assert.EndsWith("0001", first.Response.TicketNumber);
        Assert.EndsWith("0002", second.Response.TicketNumber);
    }

    // ---- Business-rule change: the real CRM Buyer Lookup match path (GET /api/crm/buyers) ----

    [Fact]
    public async Task CreateAsync_CrmBuyerMatchSelected_CreatesVerifiedTicket_WithAllFourCrmIdsAndSnapshot()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "AC not cooling",
                CrmBuyerCustomerId: 5001, CrmBuyerLeadId: 901, CrmBuyerUnitId: 101, CrmBuyerProjectId: 10,
                CrmBuyerCustomerName: "Ahmed Ali", CrmBuyerProjectName: "Tiger Sky Tower", CrmBuyerUnitNumber: "1205"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal("Verified", result.Response!.VerificationStatus);
        Assert.Equal(5001, result.Response.CrmBuyerCustomerId);
        Assert.Equal(901, result.Response.CrmBuyerLeadId);
        Assert.Equal(101, result.Response.CrmBuyerUnitId);
        Assert.Equal(10, result.Response.CrmBuyerProjectId);
        Assert.Equal("Ahmed Ali", result.Response.CrmBuyerCustomerName);
        Assert.Equal("Tiger Sky Tower", result.Response.CrmBuyerProjectName);
        Assert.Equal("1205", result.Response.CrmBuyerUnitNumber);
        // A distinct identifier space from UnitReferenceId/ContactReferenceId.
        Assert.Null(result.Response.UnitReferenceId);
        Assert.Null(result.Response.ContactReferenceId);

        var linkedIntake = await f.IntakeRecords.GetByIdAsync(intake.IntakeRecordId);
        Assert.True(linkedIntake!.IsUnitRelated);
        Assert.Equal(CrmVerificationStatus.Verified, linkedIntake.CrmVerificationStatus);
    }

    [Fact]
    public async Task CreateAsync_OnlySomeCrmBuyerIdsSupplied_ReturnsCrmBuyerReferenceMismatch()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "x",
                CrmBuyerCustomerId: 5001, CrmBuyerLeadId: 901, CrmBuyerUnitId: null, CrmBuyerProjectId: null));

        Assert.Equal(TicketCreationOutcome.CrmBuyerReferenceMismatch, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task CreateAsync_CrmBuyerMatchAndManualProjectUnitBothSupplied_Rejected()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "x",
                CrmBuyerCustomerId: 5001, CrmBuyerLeadId: 901, CrmBuyerUnitId: 101, CrmBuyerProjectId: 10,
                ManualProjectName: "Tiger Tower A", ManualUnitNumber: "1204"));

        Assert.Equal(TicketCreationOutcome.CrmBuyerAndManualProjectUnitBothSupplied, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task CreateAsync_NoCrmMatch_ManualProjectAndUnitNumberSupplied_StoresThemOnTheTicket()
    {
        // No verified CRM unit was selected — Project/Unit Number were
        // manually entered by the agent (never used to run another CRM
        // lookup) and are stored as pass-through fields on the Unverified
        // ticket.
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "x",
                ManualProjectName: "Tiger Tower A", ManualUnitNumber: "1204"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal("Unverified", result.Response!.VerificationStatus);
        Assert.Equal("Tiger Tower A", result.Response.ManualProjectName);
        Assert.Equal("1204", result.Response.ManualUnitNumber);
        Assert.Null(result.Response.CrmBuyerCustomerId);
    }

    // --- Customer not found / non-unit / lookup failed: none of these ever block creation ---

    [Fact]
    public async Task CreateAsync_NonUnitRelatedIntake_NoMatch_CreatesUnverifiedTicket()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "General billing question"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal("Unverified", result.Response!.VerificationStatus);
        Assert.Null(result.Response.UnitReferenceId);
        Assert.Null(result.Response.ContactReferenceId);

        var linkedIntake = await f.IntakeRecords.GetByIdAsync(intake.IntakeRecordId);
        Assert.Equal(CrmVerificationStatus.Unverified, linkedIntake!.CrmVerificationStatus);
        Assert.Empty(f.Snapshots.Added);
    }

    [Fact]
    public async Task CreateAsync_UnitRelatedIntake_NoMatchSelected_StillCreatesUnverifiedTicket()
    {
        // Whether the customer lookup found nothing, a source failed, or the
        // agent simply proceeded without one — the request looks identical
        // to this service (no UnitReferenceId/ContactReferenceId), and it
        // creates the ticket the same way every time.
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: true, rawUnitNumberEntered: "1204");

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Critical, "Flooding reported"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal("Unverified", result.Response!.VerificationStatus);
    }

    [Fact]
    public async Task CreateAsync_HappyPath_OpensInitialSlaPeriodImmediately()
    {
        // Business-rule change: nothing about a missing customer match
        // pauses the SLA clock any more — it always starts at creation.
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Low, "General question"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal("Running", result.Response!.SlaState);
    }

    // --- UnitReferenceId/ContactReferenceId pairing and existence ---

    [Fact]
    public async Task CreateAsync_OnlyUnitReferenceIdSupplied_ReturnsUnitOrContactReferenceMismatch()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords);
        var unit = f.UnitReferences.Seed("CRM-UNIT-1001", "1204");

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, unit.UnitReferenceId, null, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.UnitOrContactReferenceMismatch, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task CreateAsync_OnlyContactReferenceIdSupplied_ReturnsUnitOrContactReferenceMismatch()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords);
        var unit = f.UnitReferences.Seed("CRM-UNIT-1001", "1204");
        var contact = f.ContactReferences.Seed(unit.UnitReferenceId, "CRM-CONTACT-2001", "Ahmed Al-Farsi");

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, null, contact.ContactReferenceId, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.UnitOrContactReferenceMismatch, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_UnitReferenceIdNotFound_ReturnsUnitReferenceNotFound()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords);

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, 999, 999, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.UnitReferenceNotFound, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_ContactReferenceIdNotFound_ReturnsContactReferenceNotFound()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords);
        var unit = f.UnitReferences.Seed("CRM-UNIT-1001", "1204");

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, unit.UnitReferenceId, 999, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.ContactReferenceNotFound, result.Outcome);
    }

    // --- Ticket Category is mandatory for every ticket ---

    [Fact]
    public async Task CreateAsync_CategoryMissing_ReturnsCategoryNotFound_TicketRejected()
    {
        var f = CreateService();
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, CategoryId: 999, (byte)PriorityLevel.Medium, "x"));

        Assert.Equal(TicketCreationOutcome.CategoryNotFound, result.Outcome);
        Assert.Empty(f.Tickets.All);

        var reloadedIntake = await f.IntakeRecords.GetByIdAsync(intake.IntakeRecordId);
        Assert.Null(reloadedIntake!.LinkedTicketId);
    }

    [Fact]
    public async Task CreateAsync_CategoryDepartmentDiffersFromIntakeDepartment_ReturnsCategoryDepartmentMismatch()
    {
        var f = CreateService();
        var customerService = f.Departments.AddDepartment("Customer Service", "CS");
        var facilities = f.Departments.AddDepartment("Facilities Management", "FM");
        var facilitiesCategory = f.Categories.Seed(facilities.DepartmentId, "Corrective Maintenance");

        var agentId = Guid.NewGuid();
        var intake = new TigerCS.Domain.Modules.Ticketing.IntakeRecord(
            Channel.Phone, "+971500009999", customerService.DepartmentId, false, null, priorityHint: null, agentId, DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(intake);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(intake.IntakeRecordId, null, null, facilitiesCategory.CategoryId, (byte)PriorityLevel.Medium, "x"));

        Assert.Equal(TicketCreationOutcome.CategoryDepartmentMismatch, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task CreateAsync_CategoryDepartmentMatchesIntakeDepartment_Succeeds()
    {
        var f = CreateService();
        var facilities = f.Departments.AddDepartment("Facilities Management", "FM");
        var category = f.Categories.Seed(facilities.DepartmentId, "Corrective Maintenance");

        var agentId = Guid.NewGuid();
        var intake = new TigerCS.Domain.Modules.Ticketing.IntakeRecord(
            Channel.Phone, "+971500009999", facilities.DepartmentId, false, null, priorityHint: null, agentId, DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(intake);

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "x"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_IntakeHasNoDepartment_AnyActiveCategorysDepartmentIsAccepted()
    {
        // No Department named on the Intake means the Category dropdown offered
        // every active Category — nothing to mismatch against.
        var f = CreateService();
        var facilities = f.Departments.AddDepartment("Facilities Management", "FM");
        var category = f.Categories.Seed(facilities.DepartmentId, "Corrective Maintenance");
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "x"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_CategoryRoutesToInactiveDepartment_ReturnsDepartmentInactive()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Retiring Department", "OLD");
        var category = f.Categories.Seed(department.DepartmentId);
        department.Deactivate();
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "x"));

        Assert.Equal(TicketCreationOutcome.DepartmentInactive, result.Outcome);
    }

    // --- IntakeRecord state ---

    [Fact]
    public async Task CreateAsync_IntakeRecordAlreadyLinked_ReturnsAlreadyLinked()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords);
        intake.LinkToTicket(1, CrmVerificationStatus.Unverified, hasSelectedUnit: false);

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.IntakeRecordAlreadyLinked, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_IntakeRecordNotFound_ReturnsNotFound()
    {
        var f = CreateService();

        var result = await f.Service.CreateAsync(
            Guid.NewGuid(), new CreateTicketRequestDto(IntakeRecordId: 999, null, null, CategoryId: 1, (byte)PriorityLevel.Medium, "x"));

        Assert.Equal(TicketCreationOutcome.IntakeRecordNotFound, result.Outcome);
    }

    // --- Transactional integrity ---

    [Fact]
    public async Task CreateAsync_TicketNumberCollision_ReturnsTicketNumberCollision()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords);

        f.UnitOfWork.ThrowDuplicateWriteExceptionOnCall = 1;

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.TicketNumberCollision, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_HappyPath_CommitsExactlyOneTransaction()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(0, f.UnitOfWork.TransactionsRolledBack);
    }
}
