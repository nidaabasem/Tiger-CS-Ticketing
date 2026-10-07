using Microsoft.AspNetCore.Identity;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.Reporting.Repositories;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Reporting;

/// <summary>
/// The Team Performance report end to end from the application service
/// down to real SQL (SQLite in-memory over the real EF Core schema, the
/// same fixture the Dashboard uses): who is listed and how they are typed,
/// each of the four metrics, the zero-activity inclusion, the one-row-per-
/// employee rule, breach attribution to the holder at breach time, and
/// that every records list matches the count it was opened from.
/// </summary>
public sealed class TeamPerformanceReportTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;

    /// <summary>The default period the service resolves at <see cref="Now"/>: the last 30 UTC days, today included.</summary>
    private static readonly DateTime PeriodStart = Now.Date.AddDays(-29);

    private readonly DashboardSqliteFixture _db = new();

    private readonly Guid _aminaId = Guid.NewGuid();   // CS Agent, Customer Service
    private readonly Guid _bilalId = Guid.NewGuid();   // CS Agent, Call Center (primary) + Customer Service
    private readonly Guid _danaId = Guid.NewGuid();    // CS Agent, Customer Service, no activity
    private readonly Guid _hadiId = Guid.NewGuid();    // CS Supervisor only — not listed
    private readonly Guid _retiredId = Guid.NewGuid(); // Deactivated CS Agent — not listed
    private int _callCenterId;

    public TeamPerformanceReportTests()
    {
        using var context = _db.CreateContext();

        var callCenter = new Department("Call Center", WorkflowReferenceData.CallCenterCode);
        context.Departments.Add(callCenter);
        context.SaveChanges();
        _callCenterId = callCenter.DepartmentId;

        AddEmployee(context, _aminaId, "Amina Agent", [Roles.CsAgent], [(_db.CustomerServiceId, true)]);
        AddEmployee(context, _bilalId, "Bilal Caller", [Roles.CsAgent], [(_callCenterId, true), (_db.CustomerServiceId, false)]);
        AddEmployee(context, _danaId, "Dana Quiet", [Roles.CsAgent], [(_db.CustomerServiceId, true)]);
        AddEmployee(context, _hadiId, "Hadi Supervisor", [Roles.CsSupervisor], [(_db.CustomerServiceId, true)]);
        AddEmployee(context, _retiredId, "Rami Retired", [Roles.CsAgent], [(_db.CustomerServiceId, true)], deactivated: true);
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------

    private TeamPerformanceAppService CreateService(TigerCsDbContext context) =>
        new(new TeamPerformanceQueryRepository(context), new DashboardSqliteFixture.FixedTimeProvider(Now));

    private async Task<TeamPerformanceReportDto> ReportAsync(TeamPerformanceRequestDto? request = null)
    {
        using var context = _db.CreateContext();
        return await CreateService(context).GetReportAsync(request ?? new TeamPerformanceRequestDto());
    }

    private async Task<TeamPerformanceRecordsResult> RecordsAsync(Guid employeeId, string metric)
    {
        using var context = _db.CreateContext();
        return await CreateService(context).GetRecordsAsync(new TeamPerformanceRecordsRequestDto(employeeId, metric));
    }

    private static TeamPerformanceRowDto Row(TeamPerformanceReportDto report, Guid employeeId) =>
        report.Rows.Single(r => r.EmployeeId == employeeId);

    /// <summary>An Identity user + Employee with real AspNetUserRoles rows and department memberships — the eligibility rule reads exactly these tables.</summary>
    private static void AddEmployee(
        TigerCsDbContext context, Guid employeeId, string displayName, string[] roles, (int DepartmentId, bool IsPrimary)[] departments, bool deactivated = false)
    {
        context.Users.Add(new ApplicationUser
        {
            Id = employeeId,
            UserName = $"user-{employeeId:N}",
            NormalizedUserName = $"USER-{employeeId:N}",
            Email = $"user-{employeeId:N}@example.test",
            SecurityStamp = Guid.NewGuid().ToString()
        });

        var employee = new Employee(employeeId, displayName, isGeynessStaff: false, Now.AddDays(-60));
        if (deactivated)
        {
            employee.Deactivate(Now.AddDays(-1));
        }

        context.Employees.Add(employee);
        context.SaveChanges();

        foreach (var roleName in roles)
        {
            var role = context.Roles.SingleOrDefault(r => r.Name == roleName);
            if (role is null)
            {
                role = new ApplicationRole(roleName, roleName) { NormalizedName = roleName.ToUpperInvariant() };
                context.Roles.Add(role);
                context.SaveChanges();
            }

            context.UserRoles.Add(new IdentityUserRole<Guid> { UserId = employeeId, RoleId = role.Id });
        }

        foreach (var (departmentId, isPrimary) in departments)
        {
            context.UserDepartmentAssignments.Add(new UserDepartmentAssignment(employeeId, departmentId, isPrimary, Now.AddDays(-50), null));
        }

        context.SaveChanges();
    }

    /// <summary>Appends an assignment the way the real services do: the prior current row is superseded (the filtered unique index allows one current row per ticket), never rewritten.</summary>
    private static void Assign(TigerCsDbContext context, Ticket ticket, Guid employeeId, DateTime atUtc)
    {
        foreach (var current in context.TicketAssignments.Where(a => a.TicketId == ticket.TicketId && a.IsCurrent))
        {
            current.MarkSuperseded();
        }

        context.TicketAssignments.Add(new TicketAssignment(ticket.TicketId, employeeId, ticket.CurrentDepartmentId, atUtc, null));
        context.SaveChanges();
    }

    private static void RecordBreach(TigerCsDbContext context, Ticket ticket, DateTime atUtc)
    {
        context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
            ticket.TicketId, TicketStatusDimension.SlaState, (byte)SlaState.Running, (byte)SlaState.Breached,
            actorEmployeeId: null, actorIsSystem: true, note: "Resolution SLA breached.", Guid.NewGuid(), atUtc));
        context.SaveChanges();
    }

    private static TicketAgentHandoff AddHandoff(TigerCsDbContext context, Ticket ticket, Guid assignedTo, DateTime assignedAtUtc, DateTime? completedAtUtc)
    {
        var interaction = TicketInteraction.CreateLocal(ticket.TicketId, WellKnownChannels.Phone, "+971500000001", assignedAtUtc);
        context.TicketInteractions.Add(interaction);
        context.SaveChanges();

        var handoff = new TicketAgentHandoff(
            ticket.TicketId, interaction.TicketInteractionId, ticket.CurrentDepartmentId, WellKnownChannels.Phone,
            requestedAtUtc: assignedAtUtc, createdAtUtc: assignedAtUtc,
            assignedEmployeeId: assignedTo, assignedAtUtc: assignedAtUtc);
        if (completedAtUtc is { } completed)
        {
            handoff.Complete(assignedTo, completed, "Done.");
        }

        context.TicketAgentHandoffs.Add(handoff);
        context.SaveChanges();
        return handoff;
    }

    // ---------------------------------------------------------------
    // Who is listed
    // ---------------------------------------------------------------

    [Fact]
    public async Task Rows_ListEveryActiveCsAgentExactlyOnce_SortedByName_TypedByCallCenterMembership()
    {
        var report = await ReportAsync();

        // Amina, Bilal, Dana — not the supervisor, not the deactivated agent,
        // not the fixture's role-less employees. Dana has no activity at all
        // and is still a row.
        Assert.Equal(["Amina Agent", "Bilal Caller", "Dana Quiet"], report.Rows.Select(r => r.DisplayName).ToArray());
        Assert.Equal(3, report.Totals.Employees);

        var amina = Row(report, _aminaId);
        Assert.Equal(TeamPerformanceAgentTypes.CsAgent, amina.AgentType);
        Assert.Equal(["Customer Service"], amina.Departments.Select(d => d.Name).ToArray());

        // Two departments, ONE row — primary first.
        var bilal = Row(report, _bilalId);
        Assert.Equal(TeamPerformanceAgentTypes.CallCenterAgent, bilal.AgentType);
        Assert.Equal(["Call Center", "Customer Service"], bilal.Departments.Select(d => d.Name).ToArray());
        Assert.True(bilal.Departments[0].IsPrimary);
        Assert.Single(report.Rows, r => r.EmployeeId == _bilalId);

        var dana = Row(report, _danaId);
        Assert.Equal((0, 0, 0, 0), (dana.CurrentlyAssigned, dana.TicketsWorked, dana.CompletedFollowUps, dana.SlaBreaches));

        // The pickers offer the whole team and every department any of them belongs to.
        Assert.Equal(3, report.FilterOptions.Employees.Count);
        Assert.Equal(["Call Center", "Customer Service"], report.FilterOptions.Departments.Select(d => d.Label).ToArray());
        Assert.Equal(TeamPerformanceAgentTypes.All, report.FilterOptions.AgentTypes);
    }

    [Fact]
    public void CallCenterDepartmentCode_IsTheSameCodeTheReferenceDataSeeds()
    {
        // The agent type is derived from this code; if the seed ever changes
        // the report must change with it, not silently stop recognising
        // Call Center agents.
        Assert.Equal(WorkflowReferenceData.CallCenterCode, TeamPerformanceAppService.CallCenterDepartmentCode);
        Assert.Equal([Roles.CsAgent], TeamPerformanceAppService.EligibleRoles);
    }

    // ---------------------------------------------------------------
    // Metrics
    // ---------------------------------------------------------------

    [Fact]
    public async Task CurrentlyAssigned_CountsOpenTicketsHeldNow_NotClosedOnes_RegardlessOfPeriod()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId, status: TicketStatus.InProgress);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId, status: TicketStatus.Resolved);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId, status: TicketStatus.Closed);
            // Old, but still held — the period does not apply to current state.
            _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId, createdAtUtc: Now.AddDays(-200));
            _db.AddTicket(context, _db.CustomerServiceId, owner: _bilalId);
            _db.AddTicket(context, _db.CustomerServiceId);
        }

        var report = await ReportAsync();

        Assert.Equal(4, Row(report, _aminaId).CurrentlyAssigned);
        Assert.Equal(1, Row(report, _bilalId).CurrentlyAssigned);
        Assert.Equal(5, report.Totals.CurrentlyAssigned);
    }

    [Fact]
    public async Task TicketsWorked_CountsDistinctTicketsAcrossEveryActivitySource_WithinThePeriodOnly()
    {
        using (var context = _db.CreateContext())
        {
            var t1 = _db.AddTicket(context, _db.CustomerServiceId);
            var t2 = _db.AddTicket(context, _db.CustomerServiceId);
            var t3 = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Resolved);
            var t4 = _db.AddTicket(context, _db.CustomerServiceId);
            var t5 = _db.AddTicket(context, _db.CustomerServiceId);
            var t6 = _db.AddTicket(context, _db.CustomerServiceId);
            var t7 = _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId);

            // t1: assigned AND noted by Amina — one ticket, not two.
            Assign(context, t1, _aminaId, Now.AddDays(-3));
            context.TicketNotes.Add(new TicketNote(t1.TicketId, "Called the customer.", _aminaId, Now.AddDays(-2)));
            // t2: a status change by Amina.
            context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
                t2.TicketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Open, (byte)TicketStatus.InProgress,
                _aminaId, false, null, Guid.NewGuid(), Now.AddDays(-4)));
            // t3: a resolution by Amina.
            context.TicketResolutions.Add(new TicketResolution(t3.TicketId, ResolutionOutcome.Resolved, "Fixed.", null, null, _aminaId, Now.AddDays(-5)));
            // t4: a workflow event by Amina.
            context.TicketWorkflowEvents.Add(new TicketWorkflowEvent(t4.TicketId, WorkflowEventType.PrerequisitesCompleted, Now.AddDays(-6), _aminaId, null, null, Guid.NewGuid()));
            context.SaveChanges();
            // t5: a follow-up assigned to Amina (not completed).
            AddHandoff(context, t5, _aminaId, Now.AddDays(-7), null);
            // t6: an assignment to Amina BEFORE the period — not worked in it.
            Assign(context, t6, _aminaId, PeriodStart.AddDays(-1));
            // t7: Amina holds it now but nothing in history says she touched
            // it in the period — current ownership is not work performed.
            // Bilal: one note.
            context.TicketNotes.Add(new TicketNote(t2.TicketId, "Checked.", _bilalId, Now.AddDays(-1)));
            context.SaveChanges();
        }

        var report = await ReportAsync();

        Assert.Equal(5, Row(report, _aminaId).TicketsWorked);
        Assert.Equal(1, Row(report, _bilalId).TicketsWorked);
        Assert.Equal(0, Row(report, _danaId).TicketsWorked);
        // t2 was worked by both — the team total counts the effort, so 6.
        Assert.Equal(6, report.Totals.TicketsWorked);
    }

    [Fact]
    public async Task CompletedFollowUps_CountsHandoffsCompletedInThePeriod_PerWorkItem()
    {
        using (var context = _db.CreateContext())
        {
            var t1 = _db.AddTicket(context, _db.CustomerServiceId);
            var t2 = _db.AddTicket(context, _db.CustomerServiceId);
            AddHandoff(context, t1, _aminaId, Now.AddDays(-3), Now.AddDays(-2));
            AddHandoff(context, t1, _aminaId, Now.AddDays(-2), Now.AddDays(-1)); // same ticket, second work item
            AddHandoff(context, t2, _aminaId, Now.AddDays(-3), null);           // still open
            AddHandoff(context, t2, _aminaId, PeriodStart.AddDays(-5), PeriodStart.AddDays(-4)); // before the period
            AddHandoff(context, t2, _bilalId, Now.AddDays(-1), Now.AddHours(-1));
        }

        var report = await ReportAsync();

        Assert.Equal(2, Row(report, _aminaId).CompletedFollowUps);
        Assert.Equal(1, Row(report, _bilalId).CompletedFollowUps);
        Assert.Equal(3, report.Totals.CompletedFollowUps);
    }

    [Fact]
    public async Task SlaBreaches_AreAttributedToWhoeverHeldTheTicketWhenItBreached_NotTheCurrentOwner()
    {
        using (var context = _db.CreateContext())
        {
            // Amina held it when it breached; Bilal holds it now.
            var reassigned = _db.AddTicket(context, _db.CustomerServiceId, owner: _bilalId, slaBreached: true);
            Assign(context, reassigned, _aminaId, Now.AddDays(-10));
            RecordBreach(context, reassigned, Now.AddDays(-5));
            Assign(context, reassigned, _bilalId, Now.AddDays(-2));

            // Two breach rows (first response, then resolution) on one ticket
            // held by Amina throughout — one ticket, counted once.
            var twice = _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId, slaBreached: true);
            Assign(context, twice, _aminaId, Now.AddDays(-8));
            RecordBreach(context, twice, Now.AddDays(-6));
            RecordBreach(context, twice, Now.AddDays(-3));

            // Breached before the period — not counted.
            var old = _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId, slaBreached: true, createdAtUtc: Now.AddDays(-60));
            Assign(context, old, _aminaId, Now.AddDays(-59));
            RecordBreach(context, old, PeriodStart.AddDays(-1));

            // Breached while nobody held it, assigned to Dana afterwards — attributed to nobody.
            var queued = _db.AddTicket(context, _db.CustomerServiceId, owner: _danaId, slaBreached: true);
            RecordBreach(context, queued, Now.AddDays(-4));
            Assign(context, queued, _danaId, Now.AddDays(-1));
        }

        var report = await ReportAsync();

        Assert.Equal(2, Row(report, _aminaId).SlaBreaches);
        Assert.Equal(0, Row(report, _bilalId).SlaBreaches);
        Assert.Equal(0, Row(report, _danaId).SlaBreaches);
        Assert.Equal(2, report.Totals.SlaBreaches);
    }

    // ---------------------------------------------------------------
    // Filters
    // ---------------------------------------------------------------

    [Fact]
    public async Task Filters_NarrowTheRows_WhileThePickersStillOfferTheWholeTeam()
    {
        var byType = await ReportAsync(new TeamPerformanceRequestDto(AgentType: "call center agent"));
        Assert.Equal([_bilalId], byType.Rows.Select(r => r.EmployeeId).ToArray());
        Assert.Equal(TeamPerformanceAgentTypes.CallCenterAgent, byType.Filters.AgentType);
        Assert.Equal(3, byType.FilterOptions.Employees.Count);

        // Membership of the department, primary or not: Bilal is in Customer Service too.
        var byDepartment = await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: _db.CustomerServiceId));
        Assert.Equal([_aminaId, _bilalId, _danaId], byDepartment.Rows.Select(r => r.EmployeeId).ToArray());
        var byCallCenter = await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: _callCenterId));
        Assert.Equal([_bilalId], byCallCenter.Rows.Select(r => r.EmployeeId).ToArray());

        var byEmployee = await ReportAsync(new TeamPerformanceRequestDto(EmployeeId: _danaId));
        Assert.Equal([_danaId], byEmployee.Rows.Select(r => r.EmployeeId).ToArray());

        // An employee who is not on the report yields no rows, never an error.
        var supervisor = await ReportAsync(new TeamPerformanceRequestDto(EmployeeId: _hadiId));
        Assert.Empty(supervisor.Rows);
        Assert.Equal(0, supervisor.Totals.Employees);

        // An unknown agent type is ignored rather than refused.
        var unknownType = await ReportAsync(new TeamPerformanceRequestDto(AgentType: "Wizard"));
        Assert.Equal(3, unknownType.Rows.Count);
        Assert.Null(unknownType.Filters.AgentType);
    }

    [Fact]
    public async Task Period_DefaultsToTheLastThirtyDays_AndAnExplicitRangeBoundsTheActivity()
    {
        using (var context = _db.CreateContext())
        {
            var t1 = _db.AddTicket(context, _db.CustomerServiceId);
            var t2 = _db.AddTicket(context, _db.CustomerServiceId);
            Assign(context, t1, _aminaId, Now.AddDays(-20));
            Assign(context, t2, _aminaId, Now.AddDays(-2));
        }

        var defaulted = await ReportAsync();
        Assert.Equal(DateOnly.FromDateTime(PeriodStart), defaulted.Filters.DateFrom);
        Assert.Equal(DateOnly.FromDateTime(Now), defaulted.Filters.DateTo);
        Assert.Equal(2, Row(defaulted, _aminaId).TicketsWorked);

        var lastWeek = await ReportAsync(new TeamPerformanceRequestDto(
            DateFrom: DateOnly.FromDateTime(Now.AddDays(-7)), DateTo: DateOnly.FromDateTime(Now)));
        Assert.Equal(1, Row(lastWeek, _aminaId).TicketsWorked);
    }

    // ---------------------------------------------------------------
    // Records behind a count
    // ---------------------------------------------------------------

    [Fact]
    public async Task Records_MatchTheCountTheyWereOpenedFrom_AndCarryTheMetricSpecificInstant()
    {
        long breachedTicketId;
        using (var context = _db.CreateContext())
        {
            var held = _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId);
            var breached = _db.AddTicket(context, _db.CustomerServiceId, owner: _bilalId, slaBreached: true);
            breachedTicketId = breached.TicketId;
            Assign(context, breached, _aminaId, Now.AddDays(-10));
            RecordBreach(context, breached, Now.AddDays(-5));
            Assign(context, breached, _bilalId, Now.AddDays(-2));
            AddHandoff(context, held, _aminaId, Now.AddDays(-3), Now.AddDays(-1));
        }

        var report = await ReportAsync();
        var amina = Row(report, _aminaId);

        foreach (var metric in Enum.GetValues<TeamPerformanceMetric>())
        {
            var result = await RecordsAsync(_aminaId, metric.ToString());
            Assert.Equal(TeamPerformanceRecordsOutcome.Success, result.Outcome);
            var expected = metric switch
            {
                TeamPerformanceMetric.CurrentlyAssigned => amina.CurrentlyAssigned,
                TeamPerformanceMetric.TicketsWorked => amina.TicketsWorked,
                TeamPerformanceMetric.CompletedFollowUps => amina.CompletedFollowUps,
                _ => amina.SlaBreaches
            };
            Assert.Equal(expected, result.Records!.Records.Count);
            Assert.Equal("Amina Agent", result.Records.DisplayName);
            Assert.Equal(metric.ToString(), result.Records.Metric);
            Assert.All(result.Records.Records, r => Assert.False(string.IsNullOrWhiteSpace(r.TicketNumber)));
        }

        var breaches = (await RecordsAsync(_aminaId, "slaBreaches")).Records!;
        var breach = Assert.Single(breaches.Records);
        Assert.Equal(breachedTicketId, breach.TicketId);
        Assert.Equal(Now.AddDays(-5), breach.BreachedAtUtc);
        Assert.Equal("Customer Service", breach.DepartmentName);
        Assert.Null(breach.CompletedAtUtc);

        var followUps = (await RecordsAsync(_aminaId, nameof(TeamPerformanceMetric.CompletedFollowUps))).Records!;
        var followUp = Assert.Single(followUps.Records);
        Assert.Equal(Now.AddDays(-1), followUp.CompletedAtUtc);
        Assert.Null(followUp.BreachedAtUtc);

        // Bilal holds the breached ticket now, so it is in his Currently
        // Assigned list — and not in his SLA Breaches, which are Amina's.
        Assert.Contains((await RecordsAsync(_bilalId, nameof(TeamPerformanceMetric.CurrentlyAssigned))).Records!.Records, r => r.TicketId == breachedTicketId);
        Assert.Empty((await RecordsAsync(_bilalId, nameof(TeamPerformanceMetric.SlaBreaches))).Records!.Records);
    }

    [Fact]
    public async Task Records_RefuseAnUnknownMetric_AndAnEmployeeWhoIsNotOnTheReport()
    {
        Assert.Equal(TeamPerformanceRecordsOutcome.UnknownMetric, (await RecordsAsync(_aminaId, "Velocity")).Outcome);
        Assert.Equal(TeamPerformanceRecordsOutcome.EmployeeNotEligible, (await RecordsAsync(_hadiId, "TicketsWorked")).Outcome);
        Assert.Equal(TeamPerformanceRecordsOutcome.EmployeeNotEligible, (await RecordsAsync(_retiredId, "TicketsWorked")).Outcome);
    }
}
