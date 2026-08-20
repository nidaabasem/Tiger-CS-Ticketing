using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
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
        FakeAuditEntryWriter Audit);

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

        var service = new TicketCreationAppService(
            intakeRecords, sessions, categories, priorities, departments,
            tickets, snapshots, statusHistory, new FakeTicketingUnitOfWork(), audit, TimeProvider.System);

        return new Fixture(service, intakeRecords, sessions, categories, priorities, departments, tickets, snapshots, statusHistory, audit);
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
}
