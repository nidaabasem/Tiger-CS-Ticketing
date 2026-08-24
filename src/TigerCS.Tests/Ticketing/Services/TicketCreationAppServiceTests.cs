using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

public class TicketCreationAppServiceTests
{
    private sealed record Fixture(
        TicketCreationAppService Service,
        FakeIntakeRecordRepository IntakeRecords,
        FakeVerificationSessionRepository Sessions,
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
        var sessions = new FakeVerificationSessionRepository();
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

        // Ticket creation now opens the ticket's initial SLA period
        // (backlog S-08's corrected acceptance criterion), so the SLA
        // services are part of this harness rather than a separate concern.
        var sla = new SlaServiceFixture(tickets, statusHistory: statusHistory, audit: audit, unitOfWork: unitOfWork);

        var service = new TicketCreationAppService(
            intakeRecords, sessions, categories, priorities, departments,
            tickets, snapshots, statusHistory, unitOfWork, audit, outbox, sla.DueDates, TimeProvider.System);

        return new Fixture(service, intakeRecords, sessions, categories, priorities, departments, tickets, snapshots, statusHistory, audit, unitOfWork, sla, outbox);
    }

    private static async Task<(TigerCS.Domain.Modules.Ticketing.IntakeRecord Record, Guid AgentId)> SeedUnitRelatedIntakeAsync(
        FakeIntakeRecordRepository repo)
    {
        var agentId = Guid.NewGuid();
        var record = new TigerCS.Domain.Modules.Ticketing.IntakeRecord(
            Channel.Phone, isUnitRelated: true, "1204", priorityHint: null, agentId, DateTime.UtcNow);
        await repo.AddAsync(record);
        return (record, agentId);
    }

    private static VerificationSession ConfirmedSession(Guid agentId, int unitReferenceId, int contactReferenceId)
    {
        var session = new VerificationSession(
            Guid.NewGuid(), agentId, unitReferenceId, contactReferenceId,
            "1204", "Tiger Tower A", "Tower A", "Residential", "Ahmed Al-Farsi", "email",
            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(30), idempotencyKey: null);
        session.Confirm(DateTime.UtcNow, VerificationMethod.ManualAgentConfirmation);
        return session;
    }

    [Fact]
    public async Task CreateFromVerificationSessionAsync_HappyPath_CreatesVerifiedTicketAndLinksIntake()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session = ConfirmedSession(agentId, unitReferenceId: 10, contactReferenceId: 20);
        await f.Sessions.AddAsync(session);

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId,
            new CreateTicketFromVerificationRequestDto(
                intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "AC not cooling"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal("Verified", result.Response!.VerificationStatus);
        Assert.StartsWith("TG-CS-", result.Response.TicketNumber);
        Assert.Equal(10, result.Response.UnitReferenceId);
        Assert.Equal(20, result.Response.ContactReferenceId);

        var linkedIntake = await f.IntakeRecords.GetByIdAsync(intake.IntakeRecordId);
        Assert.Equal(result.Response.TicketId, linkedIntake!.LinkedTicketId);
        Assert.Equal(CrmVerificationStatus.Verified, linkedIntake.CrmVerificationStatus);

        Assert.Single(f.Snapshots.Added);
        Assert.Equal("1204", f.Snapshots.Added[0].SnapshotUnitNumber);

        // Four dimensions seeded (TicketStatus/VerificationStatus/EscalationLevel/SlaState) — ResolutionOutcome excluded (null at creation, NewValue is NOT NULL).
        Assert.Equal(4, f.StatusHistory.Added.Count);

        Assert.Contains(f.Audit.Written, w => w.Action == "Create" && w.EntityType == "Ticket");
        Assert.Contains(f.Audit.Written, w => w.Action == "ConsumeVerificationSession");
    }

    [Fact]
    public async Task CreateFromVerificationSessionAsync_SecondTicketNumberSameDayDepartment_IncrementsSequence()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);

        var (intake1, agent1) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session1 = ConfirmedSession(agent1, 10, 20);
        await f.Sessions.AddAsync(session1);
        var first = await f.Service.CreateFromVerificationSessionAsync(
            agent1, new CreateTicketFromVerificationRequestDto(intake1.IntakeRecordId, session1.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "Issue 1"));

        var (intake2, agent2) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session2 = ConfirmedSession(agent2, 11, 21);
        await f.Sessions.AddAsync(session2);
        var second = await f.Service.CreateFromVerificationSessionAsync(
            agent2, new CreateTicketFromVerificationRequestDto(intake2.IntakeRecordId, session2.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "Issue 2"));

        Assert.NotEqual(first.Response!.TicketNumber, second.Response!.TicketNumber);
        Assert.EndsWith("0001", first.Response.TicketNumber);
        Assert.EndsWith("0002", second.Response.TicketNumber);
    }

    [Fact]
    public async Task CreateFromVerificationSessionAsync_SessionOwnedByDifferentAgent_ReturnsForbidden()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, owningAgent) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session = ConfirmedSession(owningAgent, 10, 20);
        await f.Sessions.AddAsync(session);

        var result = await f.Service.CreateFromVerificationSessionAsync(
            Guid.NewGuid(),
            new CreateTicketFromVerificationRequestDto(intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "AC not cooling"));

        Assert.Equal(TicketCreationOutcome.VerificationSessionForbidden, result.Outcome);
    }

    [Fact]
    public async Task CreateFromVerificationSessionAsync_UnconfirmedSession_ReturnsNotConfirmed()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session = new VerificationSession(
            Guid.NewGuid(), agentId, 10, 20, "1204", null, null, null, null, null,
            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(30), null);
        await f.Sessions.AddAsync(session);

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId, new CreateTicketFromVerificationRequestDto(intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.VerificationSessionNotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task CreateFromVerificationSessionAsync_AlreadyConsumedSession_ReturnsAlreadyConsumed()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session = ConfirmedSession(agentId, 10, 20);
        session.Consume(999, DateTime.UtcNow);
        await f.Sessions.AddAsync(session);

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId, new CreateTicketFromVerificationRequestDto(intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.VerificationSessionAlreadyConsumed, result.Outcome);
    }

    [Fact]
    public async Task CreateFromVerificationSessionAsync_ExpiredSession_ReturnsExpired()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var past = DateTime.UtcNow.AddHours(-2);
        var session = new VerificationSession(Guid.NewGuid(), agentId, 10, 20, "1204", null, null, null, null, null, past, past.AddMinutes(30), null);
        session.Confirm(past.AddMinutes(5), VerificationMethod.ManualAgentConfirmation);
        await f.Sessions.AddAsync(session);

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId, new CreateTicketFromVerificationRequestDto(intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.VerificationSessionExpired, result.Outcome);
    }

    [Fact]
    public async Task CreateFromVerificationSessionAsync_IntakeRecordAlreadyLinked_ReturnsAlreadyLinked()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        intake.LinkToTicket(1, CrmVerificationStatus.Verified);
        var session = ConfirmedSession(agentId, 10, 20);
        await f.Sessions.AddAsync(session);

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId, new CreateTicketFromVerificationRequestDto(intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.IntakeRecordAlreadyLinked, result.Outcome);
    }

    [Fact]
    public async Task CreateFromVerificationSessionAsync_NonUnitRelatedIntakeRecord_ReturnsNotUnitRelated()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var agentId = Guid.NewGuid();
        var intake = new TigerCS.Domain.Modules.Ticketing.IntakeRecord(Channel.Phone, false, null, null, agentId, DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(intake);
        var session = ConfirmedSession(agentId, 10, 20);
        await f.Sessions.AddAsync(session);

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId, new CreateTicketFromVerificationRequestDto(intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.IntakeRecordNotUnitRelated, result.Outcome);
    }

    [Theory]
    [InlineData(PriorityLevel.Critical)]
    [InlineData(PriorityLevel.High)]
    public async Task CreateProvisionalAsync_CriticalOrHigh_CreatesTicketWithNoUnitReference(PriorityLevel level)
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);

        var result = await f.Service.CreateProvisionalAsync(
            agentId, new CreateProvisionalTicketRequestDto(intake.IntakeRecordId, category.CategoryId, (byte)level, "Flooding reported"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal("PendingCrmVerification", result.Response!.VerificationStatus);
        Assert.Null(result.Response.UnitReferenceId);
        Assert.Null(result.Response.ContactReferenceId);

        var linkedIntake = await f.IntakeRecords.GetByIdAsync(intake.IntakeRecordId);
        Assert.Equal(result.Response.TicketId, linkedIntake!.LinkedTicketId);
        Assert.Equal(CrmVerificationStatus.PendingCrmVerification, linkedIntake.CrmVerificationStatus);
    }

    [Theory]
    [InlineData(PriorityLevel.Medium)]
    [InlineData(PriorityLevel.Low)]
    public async Task CreateProvisionalAsync_MediumOrLow_QueuesIntakeRecordInsteadOfCreatingTicket_NoTicketRowCreated(PriorityLevel level)
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);

        var result = await f.Service.CreateProvisionalAsync(
            agentId, new CreateProvisionalTicketRequestDto(intake.IntakeRecordId, category.CategoryId, (byte)level, "Leaking tap"));

        Assert.Equal(TicketCreationOutcome.QueuedPendingVerification, result.Outcome);
        Assert.Null(result.Response);
        Assert.Equal("PendingCrmVerification", result.QueuedIntakeRecord!.CrmVerificationStatus);
        Assert.Null(result.QueuedIntakeRecord.LinkedTicketId);

        // No partial ticket state of any kind — ISSUE-006's "Medium/Low remains queued" is a genuine no-op on Tickets.
        var reloadedIntake = await f.IntakeRecords.GetByIdAsync(intake.IntakeRecordId);
        Assert.Null(reloadedIntake!.LinkedTicketId);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task CreateFromVerificationSessionAsync_ConcurrentSessionConsumption_ReturnsAlreadyConsumedAndRollsBackTransaction()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session = ConfirmedSession(agentId, 10, 20);
        await f.Sessions.AddAsync(session);

        // Call #1 = the ticket insert (succeeds); call #2 = the
        // snapshot/consume/audit batch — simulates a second, faster
        // concurrent request winning the race on VerificationSessions.Status.
        f.UnitOfWork.ThrowConcurrencyConflictOnCall = 2;

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId,
            new CreateTicketFromVerificationRequestDto(
                intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "AC not cooling"));

        Assert.Equal(TicketCreationOutcome.VerificationSessionAlreadyConsumed, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(1, f.UnitOfWork.TransactionsRolledBack);

        // Not asserted here: that the IntakeRecord's LinkedTicketId is
        // rolled back too. FakeIntakeRecordRepository mutates its tracked
        // object in place with no concept of committed vs. uncommitted
        // state, so it cannot honestly prove that — only a real DbContext
        // (whose rollback discards the whole unit of work, including this
        // object graph, so a fresh read after the failed request sees the
        // database's actual, unchanged row) can. That guarantee rests on
        // the real transaction wrapping both SaveChanges calls, verified
        // above by TransactionsRolledBack == 1 with TransactionsCommitted
        // == 0 — the same "fakes prove the code path, real SQL Server
        // proves persistence" split already established elsewhere in this
        // codebase (see CustomerVerificationUnitOfWork's remarks).
    }

    [Fact]
    public async Task CreateFromVerificationSessionAsync_TicketNumberCollision_ReturnsTicketNumberCollision()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session = ConfirmedSession(agentId, 10, 20);
        await f.Sessions.AddAsync(session);

        f.UnitOfWork.ThrowDuplicateWriteExceptionOnCall = 1;

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId,
            new CreateTicketFromVerificationRequestDto(
                intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "AC not cooling"));

        Assert.Equal(TicketCreationOutcome.TicketNumberCollision, result.Outcome);
        // Session is untouched — a plain retry of the whole request is safe.
        Assert.Equal(VerificationSessionStatus.Confirmed, session.Status);
    }

    [Fact]
    public async Task CreateFromVerificationSessionAsync_CategoryRoutesToInactiveDepartment_ReturnsDepartmentInactive()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Retiring Department", "OLD");
        var category = f.Categories.Seed(department.DepartmentId);
        department.Deactivate();
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session = ConfirmedSession(agentId, 10, 20);
        await f.Sessions.AddAsync(session);

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId,
            new CreateTicketFromVerificationRequestDto(
                intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "x"));

        Assert.Equal(TicketCreationOutcome.DepartmentInactive, result.Outcome);
    }

    [Fact]
    public async Task CreateFromVerificationSessionAsync_HappyPath_CommitsExactlyOneTransaction()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session = ConfirmedSession(agentId, 10, 20);
        await f.Sessions.AddAsync(session);

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId,
            new CreateTicketFromVerificationRequestDto(
                intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, (byte)PriorityLevel.High, "AC not cooling"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(0, f.UnitOfWork.TransactionsRolledBack);
    }

    [Fact]
    public async Task CreateProvisionalAsync_CategoryRoutesToInactiveDepartment_ReturnsDepartmentInactive()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Retiring Department", "OLD");
        var category = f.Categories.Seed(department.DepartmentId);
        department.Deactivate();
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);

        var result = await f.Service.CreateProvisionalAsync(
            agentId, new CreateProvisionalTicketRequestDto(intake.IntakeRecordId, category.CategoryId, (byte)PriorityLevel.Critical, "Flooding"));

        Assert.Equal(TicketCreationOutcome.DepartmentInactive, result.Outcome);
    }

    [Fact]
    public async Task CreateProvisionalAsync_TicketNumberCollision_ReturnsTicketNumberCollision()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);

        f.UnitOfWork.ThrowDuplicateWriteExceptionOnCall = 1;

        var result = await f.Service.CreateProvisionalAsync(
            agentId, new CreateProvisionalTicketRequestDto(intake.IntakeRecordId, category.CategoryId, (byte)PriorityLevel.Critical, "Flooding"));

        Assert.Equal(TicketCreationOutcome.TicketNumberCollision, result.Outcome);
    }

    private static async Task<(TigerCS.Domain.Modules.Ticketing.IntakeRecord Record, Guid AgentId)> SeedNonUnitIntakeAsync(
        FakeIntakeRecordRepository repo)
    {
        var agentId = Guid.NewGuid();
        var record = new TigerCS.Domain.Modules.Ticketing.IntakeRecord(
            Channel.Phone, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, agentId, DateTime.UtcNow);
        await repo.AddAsync(record);
        return (record, agentId);
    }

    // --- CreateFromNonUnitIntakeAsync: business-rule change (non-unit intakes may become tickets) ---

    [Fact]
    public async Task CreateFromNonUnitIntakeAsync_ValidCategory_CreatesUnverifiedTicketAndLinksIntake()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedNonUnitIntakeAsync(f.IntakeRecords);

        var result = await f.Service.CreateFromNonUnitIntakeAsync(
            agentId, new CreateTicketFromNonUnitIntakeRequestDto(intake.IntakeRecordId, category.CategoryId, (byte)PriorityLevel.Medium, "General billing question"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal("Unverified", result.Response!.VerificationStatus);
        Assert.Null(result.Response.UnitReferenceId);
        Assert.Null(result.Response.ContactReferenceId);
        Assert.StartsWith("TG-CS-", result.Response.TicketNumber);

        var linkedIntake = await f.IntakeRecords.GetByIdAsync(intake.IntakeRecordId);
        Assert.Equal(result.Response.TicketId, linkedIntake!.LinkedTicketId);
        Assert.Equal(CrmVerificationStatus.Unverified, linkedIntake.CrmVerificationStatus);

        // No CRM unit/contact snapshot is ever written for a non-unit ticket.
        Assert.Empty(f.Snapshots.Added);

        Assert.Contains(f.Audit.Written, w => w.Action == "Create" && w.EntityType == "Ticket");

        // CreateFromNonUnitIntakeAsync never calls the CRM — TicketCreationAppService
        // has no ICrmGateway/CRM-related dependency at all (see its constructor), so
        // a CRM outage structurally cannot block this path. This fixture (and the
        // successful result above) proves the path completes with no CRM fake wired in.
    }

    [Fact]
    public async Task CreateFromNonUnitIntakeAsync_CategoryNotFound_Rejected()
    {
        var f = CreateService();
        var (intake, agentId) = await SeedNonUnitIntakeAsync(f.IntakeRecords);

        var result = await f.Service.CreateFromNonUnitIntakeAsync(
            agentId, new CreateTicketFromNonUnitIntakeRequestDto(intake.IntakeRecordId, CategoryId: 999, (byte)PriorityLevel.Medium, "General billing question"));

        Assert.Equal(TicketCreationOutcome.CategoryNotFound, result.Outcome);
        Assert.Empty(f.Tickets.All);

        var reloadedIntake = await f.IntakeRecords.GetByIdAsync(intake.IntakeRecordId);
        Assert.Null(reloadedIntake!.LinkedTicketId);
    }

    [Fact]
    public async Task CreateFromNonUnitIntakeAsync_UnitRelatedIntakeRecord_ReturnsUnitRelated()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);

        var result = await f.Service.CreateFromNonUnitIntakeAsync(
            agentId, new CreateTicketFromNonUnitIntakeRequestDto(intake.IntakeRecordId, category.CategoryId, (byte)PriorityLevel.Medium, "x"));

        // A unit-related intake must still go through CRM verification —
        // via CreateFromVerificationSessionAsync or CreateProvisionalAsync —
        // not this path.
        Assert.Equal(TicketCreationOutcome.IntakeRecordUnitRelated, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task CreateFromNonUnitIntakeAsync_IntakeRecordAlreadyLinked_ReturnsAlreadyLinked()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedNonUnitIntakeAsync(f.IntakeRecords);
        intake.LinkToTicket(1, CrmVerificationStatus.Unverified);

        var result = await f.Service.CreateFromNonUnitIntakeAsync(
            agentId, new CreateTicketFromNonUnitIntakeRequestDto(intake.IntakeRecordId, category.CategoryId, (byte)PriorityLevel.Medium, "x"));

        Assert.Equal(TicketCreationOutcome.IntakeRecordAlreadyLinked, result.Outcome);
    }

    [Fact]
    public async Task CreateFromNonUnitIntakeAsync_IntakeRecordNotFound_ReturnsNotFound()
    {
        var f = CreateService();

        var result = await f.Service.CreateFromNonUnitIntakeAsync(
            Guid.NewGuid(), new CreateTicketFromNonUnitIntakeRequestDto(IntakeRecordId: 999, CategoryId: 1, (byte)PriorityLevel.Medium, "x"));

        Assert.Equal(TicketCreationOutcome.IntakeRecordNotFound, result.Outcome);
    }

    [Fact]
    public async Task CreateFromNonUnitIntakeAsync_HappyPath_OpensInitialSlaPeriod()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedNonUnitIntakeAsync(f.IntakeRecords);

        var result = await f.Service.CreateFromNonUnitIntakeAsync(
            agentId, new CreateTicketFromNonUnitIntakeRequestDto(intake.IntakeRecordId, category.CategoryId, (byte)PriorityLevel.Medium, "General billing question"));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal("Running", result.Response!.SlaState);
    }
}
