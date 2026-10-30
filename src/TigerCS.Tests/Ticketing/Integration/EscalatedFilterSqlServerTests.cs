using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>A test that needs a REAL SQL Server. Without <see cref="Variable"/> it is reported SKIPPED, never "passed" without having run any SQL.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    /// <summary>A connection string to a server whose login may CREATE and DROP databases (it is never used for the tests' own data — each run makes a throw-away database).</summary>
    public const string Variable = "TIGERCS_TEST_SQL_SERVER";

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Not executed: {Variable} is not set (real SQL Server required).";
        }
    }
}

/// <summary>
/// A throw-away database on a real SQL Server with the application's REAL migrations applied (so the
/// AddTicketTransferTracking migration is exercised too), seeded with tickets covering every kind of ownership change.
/// </summary>
public sealed class EscalationSqlServerFixture : IDisposable
{
    private readonly string? _masterConnection = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
    private readonly string _databaseName = $"TigerCsEscalationTest_{Guid.NewGuid():N}";
    private static readonly DateTime Now = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    public int DepartmentA { get; private set; }
    public int DepartmentB { get; private set; }
    public Guid Agent { get; } = Guid.NewGuid();
    public Guid HeadA { get; } = Guid.NewGuid();
    public Guid HeadB { get; } = Guid.NewGuid();
    public Guid Receiver { get; } = Guid.NewGuid();

    /// <summary>Answered the call but belongs to NO department any more — the "left the department" case.</summary>
    public Guid FormerHandler { get; } = Guid.NewGuid();

    public long UnableToResolveA { get; private set; }
    public long NeedsEscalationB { get; private set; }
    public long WrongDepartmentA { get; private set; }
    public long SystemRoutedA { get; private set; }
    public long HandlingOnlyA { get; private set; }
    public long LegacyUnspecifiedA { get; private set; }
    public long NoEventsA { get; private set; }

    public string ConnectionString => new SqlConnectionStringBuilder(_masterConnection!) { InitialCatalog = _databaseName }.ConnectionString;

    public EscalationSqlServerFixture()
    {
        if (string.IsNullOrWhiteSpace(_masterConnection))
        {
            return; // every test is skipped
        }

        using (var master = new SqlConnection(_masterConnection))
        {
            master.Open();
            using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_databaseName}]";
            create.ExecuteNonQuery();
        }

        using var context = CreateContext();
        context.Database.Migrate();
        Seed(context);
    }

    public TigerCsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options);

    public TicketQueryAppService CreateQueryService(TigerCsDbContext context) => new(
        new TicketRepository(context), new UserDepartmentAssignmentRepository(context),
        new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, TimeProvider.System,
        interactionRepository: new TicketInteractionRepository(context),
        workflowEventRepository: new TicketWorkflowEventRepository(context),
        employeeRepository: new EmployeeRepository(context));

    private void Seed(TigerCsDbContext context)
    {
        var a = new Department("Customer Service", "CSX");
        var b = new Department("Collections", "COLX");
        context.Departments.AddRange(a, b);
        foreach (var (id, name) in new[] { (Agent, "Amal Agent"), (HeadA, "Hana Head A"), (HeadB, "Hadi Head B"), (Receiver, "Rana Receiver"), (FormerHandler, "Layla Left") })
        {
            context.Users.Add(new ApplicationUser
            {
                Id = id, UserName = $"u-{id:N}", NormalizedUserName = $"U-{id:N}".ToUpperInvariant(),
                Email = $"{id:N}@example.test", SecurityStamp = Guid.NewGuid().ToString()
            });
            context.Employees.Add(new Employee(id, name, isGeynessStaff: false, Now.AddDays(-60)));
        }

        context.SaveChanges();
        DepartmentA = a.DepartmentId;
        DepartmentB = b.DepartmentId;
        context.UserDepartmentAssignments.AddRange(
            new UserDepartmentAssignment(Agent, DepartmentA, true, Now.AddDays(-50), null),
            new UserDepartmentAssignment(HeadA, DepartmentA, true, Now.AddDays(-50), null),
            new UserDepartmentAssignment(HeadB, DepartmentB, true, Now.AddDays(-50), null),
            new UserDepartmentAssignment(Receiver, DepartmentB, true, Now.AddDays(-50), null));
        context.SaveChanges();

        long NewTicket(int departmentId, int n)
        {
            var ticket = Ticket.CreateUnclassified($"TG-ESC-20261030-{n:D4}", departmentId, $"Escalation test {n}", Now.AddHours(-n));
            context.Tickets.Add(ticket);
            context.SaveChanges();
            return ticket.TicketId;
        }

        void Add(TicketWorkflowEvent e) { context.TicketWorkflowEvents.Add(e); context.SaveChanges(); }

        UnableToResolveA = NewTicket(DepartmentA, 1);
        Add(TicketWorkflowEvent.ForTransfer(UnableToResolveA, Now, Agent, TicketTransferReason.UnableToResolve, DepartmentA, DepartmentB, Receiver, "Could not fix", Guid.NewGuid()));

        NeedsEscalationB = NewTicket(DepartmentB, 2);
        Add(TicketWorkflowEvent.ForTransfer(NeedsEscalationB, Now, HeadB, TicketTransferReason.NeedsEscalation, DepartmentB, DepartmentB, Receiver, null, Guid.NewGuid()));

        WrongDepartmentA = NewTicket(DepartmentA, 3);
        Add(TicketWorkflowEvent.ForTransfer(WrongDepartmentA, Now, HeadA, TicketTransferReason.WrongDepartment, DepartmentA, DepartmentB, null, null, Guid.NewGuid()));

        SystemRoutedA = NewTicket(DepartmentA, 4);
        Add(TicketWorkflowEvent.ForTransfer(SystemRoutedA, Now, null, TicketTransferReason.SystemRouting, DepartmentB, DepartmentA, null, "Request type routing", Guid.NewGuid()));

        // Handling history only: a Genesys call transfer, a reopen and a handoff accept, each with ownership but no reason.
        HandlingOnlyA = NewTicket(DepartmentA, 5);
        Add(new TicketWorkflowEvent(HandlingOnlyA, WorkflowEventType.GenesysCallTransferred, Now, FormerHandler, null, "call moved", Guid.NewGuid())
            .WithOwnership(null, null, Agent));
        Add(new TicketWorkflowEvent(HandlingOnlyA, WorkflowEventType.Reopened, Now, Agent, null, "reopened", Guid.NewGuid())
            .WithOwnership(DepartmentB, DepartmentA, Agent));
        Add(new TicketWorkflowEvent(HandlingOnlyA, WorkflowEventType.HandoffStarted, Now, Agent, null, "accepted", Guid.NewGuid())
            .WithOwnership(DepartmentA, DepartmentA, Agent));

        // A TicketTransferred row with NO reason (what an older client or pre-tracking data looks like): never inferred as an escalation.
        LegacyUnspecifiedA = NewTicket(DepartmentA, 6);
        Add(new TicketWorkflowEvent(LegacyUnspecifiedA, WorkflowEventType.TicketTransferred, Now, HeadA, null, "Please take this", Guid.NewGuid()));

        NoEventsA = NewTicket(DepartmentA, 7);

        // The first handler on the originating Genesys interaction — someone who is in no department any more.
        var interaction = TicketInteraction.CreateFromGenesys(
            UnableToResolveA, WellKnownChannels.Phone, "+971500000001", "conv-esc-1", null, "q", "CS Queue",
            "ga-left-1", "Layla", Now, "Inbound", Now, isOriginatingInteraction: true);
        interaction.RecordHandlingAgentIfAbsent("ga-left-1", FormerHandler);
        context.TicketInteractions.Add(interaction);
        context.SaveChanges();
    }

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(_masterConnection))
        {
            return;
        }

        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(_masterConnection);
        master.Open();
        using var drop = master.CreateCommand();
        drop.CommandText = $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]";
        drop.ExecuteNonQuery();
    }
}

/// <summary>
/// The supervisor "Escalated" queue filter and the new ticket-detail facts, executed as SQL on a real SQL Server through the
/// real repositories and application services: who may use the filter, that department visibility still bounds it, and that
/// only Unable-to-Resolve / Needs-Escalation transfers count — nothing is inferred for rows without a reason.
/// </summary>
public class EscalatedFilterSqlServerTests(EscalationSqlServerFixture db) : IClassFixture<EscalationSqlServerFixture>
{
    private static TicketListRequestDto Request(bool? escalated) => new(
        null, null, null, null, null, null, null, null, null, 1, 50, Escalated: escalated);

    private async Task<IReadOnlyList<long>> QueueAsync(string[] roles, Guid caller, bool? escalated)
    {
        await using var context = db.CreateContext();
        var result = await db.CreateQueryService(context).GetQueueAsync(caller, roles, Request(escalated));
        return result.Items.Select(i => i.TicketId).OrderBy(i => i).ToList();
    }

    [SqlServerFact]
    public async Task TheRealMigrations_ApplyAndCreateTheTransferTrackingColumns()
    {
        await using var context = db.CreateContext();

        var columns = await context.Database.SqlQueryRaw<string>(
            "SELECT COLUMN_NAME AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TicketWorkflowEvents'").ToListAsync();
        Assert.Contains("TransferReasonCode", columns);
        Assert.Contains("FromDepartmentId", columns);
        Assert.Contains("ToDepartmentId", columns);
        Assert.Contains("ToEmployeeId", columns);

        var migrations = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(migrations, m => m.EndsWith("_AddTicketTransferTracking", StringComparison.Ordinal));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    [SqlServerFact]
    public async Task ASupervisor_SeesOnlyUnableToResolveAndNeedsEscalation_AcrossDepartments()
    {
        var escalated = await QueueAsync([Roles.CsSupervisor], Guid.NewGuid(), escalated: true);

        Assert.Equal(new[] { db.UnableToResolveA, db.NeedsEscalationB }.OrderBy(i => i), escalated);
    }

    [SqlServerFact]
    public async Task TheFilter_NarrowsTheQueue_ItDoesNotReplaceIt()
    {
        var all = await QueueAsync([Roles.CsSupervisor], Guid.NewGuid(), escalated: null);
        var escalated = await QueueAsync([Roles.CsSupervisor], Guid.NewGuid(), escalated: true);

        Assert.Equal(7, all.Count);
        Assert.True(escalated.Count < all.Count);
        Assert.All(escalated, id => Assert.Contains(id, all));
    }

    [SqlServerFact]
    public async Task NoReasonRecorded_WrongDepartment_SystemRouting_AndHandlingHistory_AreNeverEscalations()
    {
        var escalated = await QueueAsync([Roles.CsManager], Guid.NewGuid(), escalated: true);

        Assert.DoesNotContain(db.WrongDepartmentA, escalated);
        Assert.DoesNotContain(db.SystemRoutedA, escalated);
        Assert.DoesNotContain(db.HandlingOnlyA, escalated);     // Genesys call transfer + reopen + handoff accept
        Assert.DoesNotContain(db.LegacyUnspecifiedA, escalated); // a transfer without a reason is not inferred
        Assert.DoesNotContain(db.NoEventsA, escalated);
    }

    [SqlServerFact]
    public async Task ADepartmentHead_ReviewsOnlyTheirOwnDepartmentsEscalations()
    {
        var headA = await QueueAsync([Roles.DepartmentHead], db.HeadA, escalated: true);
        var headB = await QueueAsync([Roles.DepartmentHead], db.HeadB, escalated: true);

        // Department visibility still bounds the review: A sees A's, B sees B's, neither sees the other's.
        Assert.Equal([db.UnableToResolveA], headA);
        Assert.Equal([db.NeedsEscalationB], headB);
    }

    [SqlServerFact]
    public async Task ACsAgent_AndOtherNonReviewers_NeverGetAnEscalationList_NorTheUnfilteredQueueInItsPlace()
    {
        foreach (var role in new[] { Roles.CsAgent, Roles.DepartmentEmployee, Roles.ReportingUser })
        {
            Assert.False(TicketQueryAppService.CanReviewEscalations([role]));
            Assert.Empty(await QueueAsync([role], db.Agent, escalated: true));
        }
    }

    [SqlServerFact]
    public async Task TheEscalatedFlag_NeverChangesWhatACsAgentSees()
    {
        // Whatever the CS Agent visibility rule is (cross-department today, assigned-or-handled once the visibility change lands),
        // this feature must not move it: an absent and an explicitly false flag give the same list, and a refused flag gives none.
        // Deliberately no count here, so the test holds on either side of that rule.
        var baseline = await QueueAsync([Roles.CsAgent], db.Agent, escalated: null);
        var explicitFalse = await QueueAsync([Roles.CsAgent], db.Agent, escalated: false);
        var refused = await QueueAsync([Roles.CsAgent], db.Agent, escalated: true);

        Assert.Equal(baseline, explicitFalse);
        Assert.Empty(refused);
    }

    [SqlServerFact]
    public async Task TheDetail_CountsOnlyPeoplesTransfers_AndNamesAFormerDepartmentMember()
    {
        await using var context = db.CreateContext();
        var query = db.CreateQueryService(context);

        var escalated = (await query.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], db.UnableToResolveA)).Response!;
        Assert.Equal(1, escalated.TransferCount);
        Assert.Equal(1, escalated.EscalationCount);
        // The first handler is in no department, yet is named — from the employee directory itself.
        Assert.Equal(db.FormerHandler, escalated.FirstHandledByEmployeeId);
        Assert.Equal("Layla Left", escalated.FirstHandledByName);

        var plain = (await query.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], db.WrongDepartmentA)).Response!;
        Assert.Equal(1, plain.TransferCount);
        Assert.Equal(0, plain.EscalationCount);

        var system = (await query.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], db.SystemRoutedA)).Response!;
        Assert.Equal(0, system.TransferCount);

        var handlingOnly = (await query.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], db.HandlingOnlyA)).Response!;
        Assert.Equal(0, handlingOnly.TransferCount);
        Assert.Equal(0, handlingOnly.EscalationCount);

        var legacy = (await query.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], db.LegacyUnspecifiedA)).Response!;
        Assert.Equal(1, legacy.TransferCount);       // it is a recorded hand-over...
        Assert.Equal(0, legacy.EscalationCount);     // ...but no reason is ever inferred
    }
}
