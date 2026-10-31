using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// Round Robin against a REAL SQL Server with the application's real migrations: concurrent assignments for one department
/// (including the very first one, when the cursor row does not exist yet) must serialize on the persisted cursor — no duplicate
/// slot, no lost update, no deadlock. Skipped without TIGERCS_TEST_SQL_SERVER.
/// </summary>
public class RoundRobinSqlServerTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    [SqlServerFact]
    public async Task ConcurrentRoundRobinAssignments_ShareOneCursor_NoDuplicateSlotsNoLostUpdates()
    {
        var master = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable)!;
        var database = $"TigerCsRoundRobinTest_{Guid.NewGuid():N}";
        var connectionString = new SqlConnectionStringBuilder(master) { InitialCatalog = database }.ConnectionString;

        await using (var admin = new SqlConnection(master))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{database}]";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            DbContextOptions<TigerCsDbContext> Options() =>
                new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(connectionString).Options;

            int departmentId;
            await using (var setup = new TigerCsDbContext(Options()))
            {
                await setup.Database.MigrateAsync();
                var department = new Department("Collections", "COL");
                setup.Departments.Add(department);
                await setup.SaveChangesAsync();
                departmentId = department.DepartmentId;
            }

            var members = new FakeUserDepartmentAssignmentRepository();
            var roles = new FakeUserRoleReader();
            var employees = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
            foreach (var id in employees)
            {
                members.Assignments.Add(new UserDepartmentAssignment(id, departmentId, true, Now, null));
                roles.Add(id, Roles.DepartmentEmployee);
            }

            const int Workers = 30;
            var picks = new System.Collections.Concurrent.ConcurrentBag<Guid>();
            using var start = new ManualResetEventSlim(false);

            var tasks = Enumerable.Range(1, Workers).Select(n => Task.Run(async () =>
            {
                start.Wait();
                await using var db = new TigerCsDbContext(Options());
                await using var tx = await db.Database.BeginTransactionAsync();

                var service = new TicketAutoAssignmentService(
                    new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(),
                    members, new FakeTicketAssignmentRepository(), new FakeAuditEntryWriter(),
                    new AssignmentRotationRepository(db), roles);
                var ticket = Ticket.CreateUnverified(
                    $"TG-COL-20261031-{n:D4}", departmentId, 5, (byte)PriorityLevel.Medium, "Payment", Now);

                var result = await service.ApplyCategoryRoutingAsync(
                    ticket, new AutoAssignmentResult(AutoAssignmentOutcome.NoRequestType), Now, Guid.NewGuid());
                Assert.Equal(AutoAssignmentOutcome.AssignedRoundRobin, result.Outcome);

                await db.SaveChangesAsync();
                await tx.CommitAsync();
                picks.Add(ticket.CurrentOwnerEmployeeId!.Value);
            })).ToList();

            start.Set();
            await Task.WhenAll(tasks);

            // Perfectly fair: 30 assignments over 3 employees = exactly 10 each. Any duplicated slot or lost cursor update skews this.
            Assert.Equal(Workers, picks.Count);
            Assert.All(employees, e => Assert.Equal(Workers / employees.Count, picks.Count(p => p == e)));

            await using var verify = new TigerCsDbContext(Options());
            var row = Assert.Single(await verify.DepartmentAssignmentRotations.AsNoTracking().ToListAsync());
            Assert.Equal(departmentId, row.DepartmentId);
            Assert.Contains(row.LastAssignedEmployeeId!.Value, employees);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var admin = new SqlConnection(master);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
