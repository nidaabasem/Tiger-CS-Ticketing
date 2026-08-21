using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Fakes;

/// <summary>
/// Builds the SLA and Escalation services over in-memory fakes, wired exactly
/// as the composition root wires them.
///
/// <para>
/// Shared by this module's own tests and by the Ticketing tests that now
/// depend on it (ticket creation opens an SLA period; resolving finalizes the
/// breach flags), so there is one place that knows how these pieces fit
/// together.
/// </para>
/// </summary>
public sealed class SlaServiceFixture
{
    public FakeSlaPolicyRepository Policies { get; } = new();
    public FakeBusinessCalendarRepository Calendar { get; } = new();
    public FakeTicketSlaInstanceRepository SlaInstances { get; } = new();
    public FakeTicketEscalationRepository Escalations { get; } = new();
    public FakeIdempotencyRecordStore Idempotency { get; } = new();
    public FakeSlaDeadlineScheduler Scheduler { get; } = new();

    public FakeTicketRepository Tickets { get; }
    public FakeTicketResolutionRepository Resolutions { get; }
    public FakeTicketStatusHistoryRepository StatusHistory { get; }
    public FakeUserDepartmentAssignmentRepository DepartmentAssignments { get; }
    public FakeAuditEntryWriter Audit { get; }
    public FakeTicketingUnitOfWork UnitOfWork { get; }
    public TimeProvider Time { get; }

    public SlaDueDateService DueDates { get; }
    public SlaBreachProcessor BreachProcessor { get; }

    public SlaServiceFixture(
        FakeTicketRepository? tickets = null,
        FakeTicketResolutionRepository? resolutions = null,
        FakeTicketStatusHistoryRepository? statusHistory = null,
        FakeUserDepartmentAssignmentRepository? departmentAssignments = null,
        FakeAuditEntryWriter? audit = null,
        FakeTicketingUnitOfWork? unitOfWork = null,
        TimeProvider? timeProvider = null)
    {
        Tickets = tickets ?? new FakeTicketRepository();
        Resolutions = resolutions ?? new FakeTicketResolutionRepository();
        StatusHistory = statusHistory ?? new FakeTicketStatusHistoryRepository();
        DepartmentAssignments = departmentAssignments ?? new FakeUserDepartmentAssignmentRepository();
        Audit = audit ?? new FakeAuditEntryWriter();
        UnitOfWork = unitOfWork ?? new FakeTicketingUnitOfWork();
        Time = timeProvider ?? TimeProvider.System;

        DueDates = new SlaDueDateService(Policies, Calendar, SlaInstances, Scheduler, Audit);
        BreachProcessor = new SlaBreachProcessor(SlaInstances, Escalations, Resolutions, StatusHistory, Idempotency, Audit);
    }

    public SlaBreachDetectionAppService CreateBreachDetection() =>
        new(Tickets, SlaInstances, BreachProcessor, UnitOfWork, Time);

    public TicketEscalationAppService CreateEscalationService() =>
        new(Tickets, Escalations, StatusHistory, DepartmentAssignments, UnitOfWork, Audit, Time);

    public SlaFirstResponseAppService CreateFirstResponseService() =>
        new(Tickets, SlaInstances, StatusHistory, DepartmentAssignments, BreachProcessor, UnitOfWork, Audit, Time);
}
