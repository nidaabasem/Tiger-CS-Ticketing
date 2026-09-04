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
        FakeOutboxWriter Outbox,
        FakeRequestTypeRepository RequestTypes,
        FakeWorkflowTemplateRepository WorkflowTemplates,
        FakeTicketInteractionRepository Interactions,
        FakeRequestTypeAssignmentRuleRepository AssignmentRules,
        FakeDepartmentWorkflowSettingsRepository WorkflowSettings,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeTicketAssignmentRepository TicketAssignments);

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

        var requestTypes = new FakeRequestTypeRepository();
        var workflowTemplates = new FakeWorkflowTemplateRepository();
        var interactions = new FakeTicketInteractionRepository();
        var assignmentRules = new FakeRequestTypeAssignmentRuleRepository();
        var workflowSettings = new FakeDepartmentWorkflowSettingsRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var ticketAssignments = new FakeTicketAssignmentRepository();
        var autoAssignment = new TicketAutoAssignmentService(
            assignmentRules, workflowSettings, departmentAssignments, ticketAssignments, audit);

        var service = new TicketCreationAppService(
            intakeRecords, unitReferences, contactReferences, categories, priorities, departments,
            tickets, snapshots, statusHistory, unitOfWork, audit, outbox, sla.DueDates, TimeProvider.System,
            requestTypes, interactions, autoAssignment);

        return new Fixture(
            service, intakeRecords, unitReferences, contactReferences, categories, priorities, departments,
            tickets, snapshots, statusHistory, audit, unitOfWork, sla, outbox,
            requestTypes, workflowTemplates, interactions, assignmentRules, workflowSettings,
            departmentAssignments, ticketAssignments);
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

    // --- External-lookup verification (PACT/Tasleeh): generic source + external ids persist alongside the manual snapshot ---

    [Fact]
    public async Task CreateAsync_ExternalVerification_PersistsSourceExternalIdsAndSnapshotOnTheTicket()
    {
        // The agent selected a matched PACT customer/unit: the ticket records
        // the generic external verification identity (source, the source's
        // own tenant/unit ids — external identifiers only, no local cache
        // row) plus the human-readable Project/Unit snapshot. The CRM-scoped
        // VerificationStatus stays Unverified (see
        // Ticket.CreateFromExternalLookup's remarks) — "Verified via PACT" is
        // derived from the source fields, never from that enum.
        var f = CreateService();
        var department = f.Departments.AddDepartment("Leasing", "LS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "AC fault",
                ManualProjectName: "Tiger Bay Towers", ManualUnitNumber: "1105",
                CustomerVerificationSource: "Pact", ExternalCustomerId: "7001", ExternalUnitId: "701"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal("Pact", result.Response!.CustomerVerificationSource);
        Assert.Equal("7001", result.Response.ExternalCustomerId);
        Assert.Equal("701", result.Response.ExternalUnitId);
        Assert.Equal("Tiger Bay Towers", result.Response.ManualProjectName);
        Assert.Equal("1105", result.Response.ManualUnitNumber);
        Assert.Equal("Unverified", result.Response.VerificationStatus);
        Assert.Null(result.Response.CrmBuyerCustomerId);
        Assert.Null(result.Response.UnitReferenceId);

        // Persisted on the domain entity itself, not just echoed back.
        var stored = Assert.Single(f.Tickets.All);
        Assert.Equal("Pact", stored.CustomerVerificationSource);
        Assert.Equal("7001", stored.ExternalCustomerId);
        Assert.Equal("701", stored.ExternalUnitId);
        Assert.Equal("Tiger Bay Towers", stored.ManualProjectName);
        Assert.Equal("1105", stored.ManualUnitNumber);
    }

    [Fact]
    public async Task CreateAsync_ExternalIdsWithoutSource_Rejected()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Leasing", "LS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "x",
                ExternalCustomerId: "7001", ExternalUnitId: "701"));

        // External identifiers never travel without their source — orphaned
        // ids are useless for audit/reconciliation and are rejected.
        Assert.Equal(TicketCreationOutcome.ExternalVerificationSourceMissing, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task CreateAsync_CrmBuyerMatchAndExternalVerificationBothSupplied_Rejected()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Leasing", "LS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "x",
                CrmBuyerCustomerId: 5001, CrmBuyerLeadId: 901, CrmBuyerUnitId: 101, CrmBuyerProjectId: 10,
                CustomerVerificationSource: "Pact", ExternalCustomerId: "7001"));

        // A ticket records one verified identity — a CRM Buyer match or an
        // external-lookup verification, never both.
        Assert.Equal(TicketCreationOutcome.CrmBuyerAndExternalVerificationBothSupplied, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task CreateAsync_ManualEntryWithoutExternalIds_StoresNoVerificationSource()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "x",
                ManualProjectName: "Tiger Tower A", ManualUnitNumber: "1204"));

        // Plain manual entry is not externally verified — no source, no ids.
        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Null(result.Response!.CustomerVerificationSource);
        Assert.Null(result.Response.ExternalCustomerId);
        Assert.Null(result.Response.ExternalUnitId);
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

    // ---- Workflow/Automation phase 2: request type, interaction context, auto-assignment ----

    private TigerCS.Domain.Modules.WorkflowConfiguration.RequestType SeedRequestType(
        Fixture f, int departmentId, string name = "AC Issue")
    {
        var template = f.WorkflowTemplates.Add(new TigerCS.Domain.Modules.WorkflowConfiguration.WorkflowTemplate(
            "PENDING", "Request With Pending", null, true, true, false));
        return f.RequestTypes.Add(new TigerCS.Domain.Modules.WorkflowConfiguration.RequestType(
            departmentId, name, template.WorkflowTemplateId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true));
    }

    [Fact]
    public async Task CreateAsync_WithRequestTypeAndRule_ClassifiesAndAutoAssigns()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Facility Management", "FM");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);
        var requestType = SeedRequestType(f, department.DepartmentId);

        var acAgent = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new TigerCS.Domain.Modules.IdentityAndAccess.UserDepartmentAssignment(
            acAgent, department.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        f.AssignmentRules.Add(TigerCS.Domain.Modules.WorkflowConfiguration.RequestTypeAssignmentRule.ForSpecificEmployee(
            requestType.RequestTypeId, acAgent));

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "AC not cooling",
                RequestTypeId: requestType.RequestTypeId));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);
        Assert.Equal(requestType.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(acAgent, ticket.CurrentOwnerEmployeeId);

        // The automatic assignment is recorded as a system action.
        var assignment = Assert.Single(f.TicketAssignments.Added);
        Assert.Null(assignment.AssigningActorEmployeeId);
    }

    [Fact]
    public async Task CreateAsync_WithRequestTypeButNoRule_StaysInTheDepartmentQueue()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Facility Management", "FM");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);
        var requestType = SeedRequestType(f, department.DepartmentId);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "AC not cooling",
                RequestTypeId: requestType.RequestTypeId));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(f.TicketAssignments.Added);
    }

    [Fact]
    public async Task CreateAsync_RequestTypeFromAnotherDepartment_IsRejected()
    {
        var f = CreateService();
        var facilities = f.Departments.AddDepartment("Facility Management", "FM");
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var category = f.Categories.Seed(facilities.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);
        var foreignRequestType = SeedRequestType(f, collections.DepartmentId, "Send Receipts");

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "AC not cooling",
                RequestTypeId: foreignRequestType.RequestTypeId));

        Assert.Equal(TicketCreationOutcome.RequestTypeDepartmentMismatch, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task CreateAsync_UnknownRequestType_IsRejected()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Facility Management", "FM");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "AC not cooling",
                RequestTypeId: 999));

        Assert.Equal(TicketCreationOutcome.RequestTypeNotFound, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task CreateAsync_WithGenesysContext_PersistsAGenesysSourcedInteractionContext()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "Complaint",
                GenesysContext: new TigerCS.Application.Modules.GenesysIntegration.Dto.GenesysInteractionContextDto(
                    ConversationId: "conv-8842", CalledNumber: "+97142223333",
                    QueueId: "q-77", QueueName: "CS Main Queue", AgentId: "ga-5", AgentName: "Line Agent")));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);
        var context = await f.Interactions.GetOriginatingAsync(ticket.TicketId);

        Assert.NotNull(context);
        Assert.True(context.IsOriginatingInteraction);
        Assert.Equal(InteractionContextSource.Genesys, context.Source);
        Assert.Equal("conv-8842", context.GenesysConversationId);
        Assert.Equal("q-77", context.GenesysQueueId);
        Assert.Equal(intake.PhoneNumber, context.CustomerPhone);
        Assert.Equal(intake.ChannelId, context.ChannelId);
    }

    [Fact]
    public async Task CreateAsync_WithoutGenesysContext_PersistsALocalContext_FaceToFaceStyle()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Facility Management", "FM");
        var category = f.Categories.Seed(department.DepartmentId);

        // A Face-to-Face walk-in: local channel, phone captured by the agent
        // (still the CRM/PACT/Tasleeh verification input), no Genesys at all.
        var agentId = Guid.NewGuid();
        var intake = new TigerCS.Domain.Modules.Ticketing.IntakeRecord(
            Channel.FaceToFaceKiosk, "+971500000009", null, false, null, null, agentId, DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(intake);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "AC issue"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);
        var context = await f.Interactions.GetOriginatingAsync(ticket.TicketId);

        Assert.NotNull(context);
        Assert.True(context.IsOriginatingInteraction);
        Assert.Equal(InteractionContextSource.Ticketing, context.Source);
        Assert.Equal(Channel.FaceToFaceKiosk, context.ChannelId);
        Assert.Equal("+971500000009", context.CustomerPhone);
        Assert.Null(context.GenesysConversationId);
        Assert.Null(context.GenesysQueueId);
        Assert.Null(context.CalledNumber);
    }

    [Fact]
    public async Task CreateAsync_GenesysContextWithoutConversationId_IsRejected()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedIntakeAsync(f.IntakeRecords, isUnitRelated: false, rawUnitNumberEntered: null);

        var result = await f.Service.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "Complaint",
                GenesysContext: new TigerCS.Application.Modules.GenesysIntegration.Dto.GenesysInteractionContextDto(" ")));

        Assert.Equal(TicketCreationOutcome.GenesysConversationIdRequired, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }
}
