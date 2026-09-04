using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.WorkflowConfiguration;

/// <summary>
/// Builds an in-memory <see cref="TigerCsDbContext"/> carrying the same
/// prerequisites a real deployment has before the workflow seed runs (the
/// fixed priorities and the already-seeded Customer Service department), then
/// runs <see cref="WorkflowReferenceData.SeedAsync"/> — the exact seed a
/// deployment gets, never a test-local restatement of it.
/// </summary>
internal static class WorkflowConfigurationTestDb
{
    public static TigerCsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TigerCsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public static async Task<TigerCsDbContext> CreateSeededContextAsync()
    {
        var db = CreateContext();

        foreach (var level in Enum.GetValues<PriorityLevel>())
        {
            db.Priorities.Add(new Priority((byte)level, level.ToString(), (byte)level));
        }

        db.Departments.Add(new Department("Customer Service", WorkflowReferenceData.CustomerServiceCode));
        await db.SaveChangesAsync();

        await WorkflowReferenceData.SeedAsync(db);
        return db;
    }
}
