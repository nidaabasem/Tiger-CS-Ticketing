using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.SlaAndEscalation.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// Resolving a ticket is the Resolution SLA's achievement event
/// (SLA-Architecture.md §2 — closure deliberately is not), so it is where a
/// late resolution is finalized as a breach.
///
/// <para>
/// This matters beyond tidiness: once a ticket reaches Closed, nothing may
/// touch its SLA state again (closed-ticket immutability), and breach
/// detection skips it. Resolution is the last honest moment to record what
/// actually happened, so it has to happen here rather than being left to a
/// job that will never run.
/// </para>
/// </summary>
public class SlaResolutionBreachTests
{
    private const int TicketDepartmentId = 2;
    private static readonly DateTime CreatedAt = new(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);

    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed record Harness(SlaServiceFixture Sla, TicketLifecycleAppService Service, Ticket Ticket, Guid OwnerId);

    private static async Task<Harness> CreateAsync(DateTime nowUtc, DateTime firstResponseDue, DateTime resolutionDue)
    {
        var sla = new SlaServiceFixture(timeProvider: new FakeTimeProvider(nowUtc));

        var ticket = Ticket.CreateVerified(
            "TG-CS-20260823-0001", TicketDepartmentId, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, (byte)PriorityLevel.High, "AC not cooling", CreatedAt);
        await sla.Tickets.AddAsync(ticket);

        var ownerId = Guid.NewGuid();
        ticket.AssignTo(ownerId);
        ticket.ChangeStatus(TicketStatus.InProgress);

        sla.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(ownerId, TicketDepartmentId, isPrimary: true, CreatedAt, assignedByEmployeeId: null));

        await sla.SlaInstances.AddAsync(TicketSlaInstance.OpenInitialPeriod(
            ticket.TicketId, (byte)PriorityLevel.High, CreatedAt, firstResponseDue, resolutionDue));

        var service = new TicketLifecycleAppService(
            sla.Tickets, sla.Resolutions, sla.StatusHistory, sla.DepartmentAssignments,
            sla.UnitOfWork, sla.Audit, sla.BreachProcessor, new FakeTimeProvider(nowUtc), ReopenPolicy.Default,
            new Ticketing.Fakes.FakeTicketPendingRecordRepository(),
            new Ticketing.Fakes.FakeRequestTypeRepository(),
            new Ticketing.Fakes.FakeWorkflowTemplateRepository());

        return new Harness(sla, service, ticket, ownerId);
    }

    private static ResolveTicketRequestDto ResolveRequest() =>
        new("Resolved", "Replaced the compressor.", null, null, RowVersion: []);

    [Fact]
    public async Task ResolvingWithinTheDeadline_RecordsNoBreach()
    {
        var h = await CreateAsync(
            nowUtc: CreatedAt.AddHours(2),
            firstResponseDue: CreatedAt.AddHours(3),
            resolutionDue: CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(30));

        var result = await h.Service.ResolveAsync(
            h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, ResolveRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);

        var instance = (await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!;
        Assert.False(instance.ResolutionBreached);
        Assert.False(instance.FirstResponseBreached);
        Assert.Empty(h.Sla.Escalations.All);
    }

    /// <summary>Resolving on time with nothing breached reports the SLA as met.</summary>
    [Fact]
    public async Task ResolvingWithinTheDeadline_ReportsTheSlaAsMet()
    {
        var h = await CreateAsync(
            nowUtc: CreatedAt.AddHours(2),
            firstResponseDue: CreatedAt.AddHours(3),
            resolutionDue: CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(30));

        await h.Service.ResolveAsync(h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, ResolveRequest());

        Assert.Equal(SlaState.Met, h.Ticket.SlaState);
    }

    [Fact]
    public async Task ResolvingAfterTheDeadline_FinalizesTheResolutionBreach()
    {
        var h = await CreateAsync(
            nowUtc: CreatedAt.AddHours(8),
            firstResponseDue: CreatedAt.AddHours(1),
            resolutionDue: CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(30));

        await h.Service.ResolveAsync(h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, ResolveRequest());

        var instance = (await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!;
        Assert.True(instance.ResolutionBreached);
        Assert.Equal(SlaState.Breached, h.Ticket.SlaState);
    }

    /// <summary>A late resolution raises the automatic Level 2 escalation exactly as a scheduled job would.</summary>
    [Fact]
    public async Task ResolvingAfterTheDeadline_RaisesTheAutomaticLevel2Escalation()
    {
        var h = await CreateAsync(
            nowUtc: CreatedAt.AddHours(8),
            firstResponseDue: CreatedAt.AddHours(1),
            resolutionDue: CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(30));

        await h.Service.ResolveAsync(h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, ResolveRequest());

        var escalation = Assert.Single(h.Sla.Escalations.All);
        Assert.Equal(EscalationTriggerType.AutoBreach, escalation.TriggerType);
        Assert.Equal(EscalationLevel.Level2, h.Ticket.EscalationLevel);

        // Escalating did not un-resolve the ticket — the dimensions stay
        // independent even when the system is what moved one of them.
        Assert.Equal(TicketStatus.Resolved, h.Ticket.TicketStatus);
    }

    /// <summary>
    /// A ticket resolved without a First Human Response ever recorded has
    /// missed that target too. This is the last moment it can be recorded:
    /// after closure, breach detection skips the ticket entirely.
    /// </summary>
    [Fact]
    public async Task ResolvingWithNoFirstResponseEverRecorded_BreachesThatClockToo()
    {
        var h = await CreateAsync(
            nowUtc: CreatedAt.AddHours(8),
            firstResponseDue: CreatedAt.AddHours(1),
            resolutionDue: CreatedAt.AddHours(6));

        await h.Service.ResolveAsync(h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, ResolveRequest());

        var instance = (await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!;
        Assert.True(instance.FirstResponseBreached);
        Assert.True(instance.ResolutionBreached);

        // Still exactly one automatic escalation, even though both clocks
        // breached in the same transaction.
        Assert.Single(h.Sla.Escalations.All);
    }

    /// <summary>A First Response breach already recorded is not softened by an on-time resolution.</summary>
    [Fact]
    public async Task AnEarlierFirstResponseBreach_SurvivesAnOnTimeResolution()
    {
        var h = await CreateAsync(
            nowUtc: CreatedAt.AddHours(2),
            firstResponseDue: CreatedAt.AddMinutes(15),
            resolutionDue: CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddHours(1));

        await h.Service.ResolveAsync(h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, ResolveRequest());

        var instance = (await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!;
        Assert.True(instance.FirstResponseBreached);
        Assert.False(instance.ResolutionBreached);

        // Breached wins over Met — the summary is not allowed to hide a
        // recorded breach behind a punctual resolution.
        Assert.Equal(SlaState.Breached, h.Ticket.SlaState);
    }

    /// <summary>Resolution's breach finalization shares the background jobs' idempotency key, so a job that already flagged it records nothing twice.</summary>
    [Fact]
    public async Task ADeadlineAlreadyFlaggedByAJob_IsNotRecordedAgainAtResolution()
    {
        var h = await CreateAsync(
            nowUtc: CreatedAt.AddHours(8),
            firstResponseDue: CreatedAt.AddHours(1),
            resolutionDue: CreatedAt.AddHours(6));

        await h.Sla.CreateBreachDetection().CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.Resolution);
        var breachAuditsAfterJob = h.Sla.Audit.Entries.Count(e => e.Action == "DetectSlaBreach");

        await h.Service.ResolveAsync(h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, ResolveRequest());

        // The First Response clock breaches at resolution (it never was
        // satisfied), so exactly one more breach audit — not a repeat of the
        // Resolution one the job already recorded.
        Assert.Equal(breachAuditsAfterJob + 1, h.Sla.Audit.Entries.Count(e => e.Action == "DetectSlaBreach"));
        Assert.Single(h.Sla.Escalations.All);
    }

    /// <summary>
    /// The race this method's breach finalization introduced: a scheduled
    /// deadline job can claim the same idempotency key between this request's
    /// read and its commit, so the unique index rejects the write. The
    /// resolution must answer as a conflict and roll back whole, never leak a
    /// 500 or half-apply.
    /// </summary>
    [Fact]
    public async Task LosingTheBreachKeyRaceToAConcurrentJob_IsReportedAsAConflict()
    {
        var h = await CreateAsync(
            nowUtc: CreatedAt.AddHours(8),
            firstResponseDue: CreatedAt.AddHours(1),
            resolutionDue: CreatedAt.AddHours(6));

        h.Sla.UnitOfWork.ThrowDuplicateWriteExceptionOnCall = 1;

        var result = await h.Service.ResolveAsync(
            h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, ResolveRequest());

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Equal(0, h.Sla.UnitOfWork.TransactionsCommitted);
    }

    /// <summary>Closure is not the achievement event (SLA-Architecture.md §2) — the breach recorded at resolution stands unchanged through it.</summary>
    [Fact]
    public async Task ClosingAfterALateResolution_LeavesTheRecordedBreachIntact()
    {
        var h = await CreateAsync(
            nowUtc: CreatedAt.AddHours(8),
            firstResponseDue: CreatedAt.AddHours(1),
            resolutionDue: CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(30));

        await h.Service.ResolveAsync(h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, ResolveRequest());
        var closeResult = await h.Service.CloseAsync(
            Guid.NewGuid(), [Roles.CsManager], h.Ticket.TicketId, new CloseTicketRequestDto(RowVersion: []));

        Assert.Equal(TicketMutationOutcome.Success, closeResult.Outcome);
        Assert.Equal(TicketStatus.Closed, h.Ticket.TicketStatus);
        Assert.True((await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!.ResolutionBreached);
        Assert.Equal(SlaState.Breached, h.Ticket.SlaState);
    }
}
