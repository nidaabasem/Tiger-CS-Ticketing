using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Ticketing.Seed;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Infrastructure.Persistence.Migrations;

namespace TigerCS.Tests.GenesysIntegration.Persistence;

/// <summary>
/// The schema half of "several tickets under one Genesys conversation": the unique identity of a Genesys ticket is now
/// (conversation id, service request id), and the migration that gets existing databases there is purely additive and reversible.
/// Enforcement is proved on a real relational engine (SQLite); the NULL-service-request rule — SQL Server treats NULLs as equal in a unique
/// index, which is what keeps "one conversation, one ticket" for integrations that send no service request id — cannot be shown on SQLite
/// (it treats NULLs as distinct) and is covered by the application-level idempotency tests plus the model assertions below.
/// </summary>
public class GenesysServiceRequestPersistenceTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);

    private static TigerCsDbContext SqlServerModelContext() => new(
        new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer("Server=(model-only);Database=(never-opened);").Options);

    [Fact]
    public void Model_UniqueIdentityOfAGenesysTicket_IsTheConversationAndServiceRequestPair_WithAPlainConversationIndexBesideIt()
    {
        using var context = SqlServerModelContext();
        var interaction = context.Model.FindEntityType(typeof(TicketInteraction))!;

        Assert.DoesNotContain(interaction.GetIndexes(), i => i.Name == "UX_TicketInteractions_GenesysConversationId");

        var unique = Assert.Single(interaction.GetIndexes(), i => i.Name == "UX_TicketInteractions_GenesysConversationId_ServiceRequestId");
        Assert.True(unique.IsUnique);
        Assert.Equal("[GenesysConversationId] IS NOT NULL", unique.GetFilter());
        Assert.Equal(
            [nameof(TicketInteraction.GenesysConversationId), nameof(TicketInteraction.GenesysServiceRequestId)],
            unique.Properties.Select(p => p.Name));

        var lookup = Assert.Single(interaction.GetIndexes(), i => i.Name == "IX_TicketInteractions_GenesysConversationId");
        Assert.False(lookup.IsUnique);
        Assert.Equal("[GenesysConversationId] IS NOT NULL", lookup.GetFilter());

        var serviceRequest = interaction.FindProperty(nameof(TicketInteraction.GenesysServiceRequestId))!;
        Assert.True(serviceRequest.IsNullable);
        Assert.Equal(TicketInteraction.GenesysServiceRequestIdMaxLength, serviceRequest.GetMaxLength());
        Assert.False(interaction.FindProperty(nameof(TicketInteraction.RequiresHumanFollowUp))!.IsNullable);

        // Still at most one ORIGINATING interaction per ticket.
        Assert.Single(interaction.GetIndexes(), i => i.Name == "UX_TicketInteractions_OneOriginatingPerTicket");
    }

    [Fact]
    public void Migration_IsAdditive_WidensTheUniqueIndex_AndIsReversible()
    {
        var migration = new AddGenesysServiceRequestToTicketInteractions();
        var up = migration.UpOperations;

        var columns = up.OfType<AddColumnOperation>().ToList();
        Assert.Equal(2, columns.Count);
        var serviceRequest = Assert.Single(columns, c => c.Name == "GenesysServiceRequestId");
        Assert.True(serviceRequest.IsNullable);               // every existing row keeps a NULL service request id
        Assert.Equal(64, serviceRequest.MaxLength);
        var flag = Assert.Single(columns, c => c.Name == "RequiresHumanFollowUp");
        Assert.False(flag.IsNullable);
        Assert.Equal(false, flag.DefaultValue);               // every existing row is "not human follow-up"
        Assert.All(columns, c => Assert.Equal("TicketInteractions", c.Table));

        Assert.Equal("UX_TicketInteractions_GenesysConversationId", Assert.Single(up.OfType<DropIndexOperation>()).Name);
        var creates = up.OfType<CreateIndexOperation>().ToList();
        var unique = Assert.Single(creates, i => i.IsUnique);
        Assert.Equal("UX_TicketInteractions_GenesysConversationId_ServiceRequestId", unique.Name);
        Assert.Equal(["GenesysConversationId", "GenesysServiceRequestId"], unique.Columns);
        Assert.Equal("[GenesysConversationId] IS NOT NULL", unique.Filter);

        // No data is touched or dropped.
        Assert.Empty(up.OfType<DropTableOperation>());
        Assert.Empty(up.OfType<DropColumnOperation>());
        Assert.Empty(up.OfType<SqlOperation>());
        Assert.Empty(up.OfType<UpdateDataOperation>());
        Assert.Empty(up.OfType<DeleteDataOperation>());

        var down = migration.DownOperations;
        Assert.Equal(2, down.OfType<DropColumnOperation>().Count());
        Assert.Contains(down.OfType<CreateIndexOperation>(), i => i.Name == "UX_TicketInteractions_GenesysConversationId" && i.IsUnique);
    }

    // ---- A real relational engine ----

    private sealed class SqliteTigerCsDbContext(DbContextOptions<TigerCsDbContext> options) : TigerCsDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Ticket>().Property(t => t.RowVersion).HasDefaultValueSql("X'0000000000000000'");
        }
    }

    private sealed class SqliteDatabase : IDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public SqliteDatabase()
        {
            _connection.Open();
            using var context = CreateContext();
            context.Database.EnsureCreated();
            ChannelReferenceData.SeedAsync(context).GetAwaiter().GetResult();
            context.Departments.Add(new Department("Customer Service", "CS"));
            context.SaveChanges();
        }

        public TigerCsDbContext CreateContext() => new SqliteTigerCsDbContext(
            new DbContextOptionsBuilder<TigerCsDbContext>()
                .UseSqlite(_connection)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
                .Options);

        public long AddTicket(string number)
        {
            using var context = CreateContext();
            var department = context.Departments.Single();
            var ticket = Ticket.CreateUnclassified(number, department.DepartmentId, "Genesys inquiry", Now, null, null);
            context.Tickets.Add(ticket);
            context.SaveChanges();
            return ticket.TicketId;
        }

        public void Dispose() => _connection.Dispose();
    }

    private static TicketInteraction Interaction(long ticketId, string conversationId, string? serviceRequestId, bool humanFollowUp = false) =>
        TicketInteraction.CreateFromGenesys(
            ticketId, WellKnownChannels.Phone, "+971500000001", conversationId, null, null, null, null, null, null, null, Now,
            isOriginatingInteraction: true, genesysServiceRequestId: serviceRequestId, requiresHumanFollowUp: humanFollowUp);

    [Fact]
    public void Database_AllowsTwoServiceRequestsInOneConversation_ButRefusesTheSamePairTwice()
    {
        using var db = new SqliteDatabase();
        var documents = db.AddTicket("TG-CS-20261022-0001");
        var leasing = db.AddTicket("TG-CS-20261022-0002");
        var duplicate = db.AddTicket("TG-CS-20261022-0003");

        using (var context = db.CreateContext())
        {
            context.TicketInteractions.Add(Interaction(documents, "conv-1", "documents"));
            context.TicketInteractions.Add(Interaction(leasing, "conv-1", "leasing", humanFollowUp: true));
            context.SaveChanges();
        }

        using (var context = db.CreateContext())
        {
            // A concurrent redelivery of the documents request that slipped past the application's read: the index is the backstop.
            context.TicketInteractions.Add(Interaction(duplicate, "conv-1", "documents"));
            Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        }

        using (var context = db.CreateContext())
        {
            var rows = context.TicketInteractions.Where(i => i.GenesysConversationId == "conv-1").OrderBy(i => i.TicketInteractionId).ToList();
            Assert.Equal(["documents", "leasing"], rows.Select(r => r.GenesysServiceRequestId));
            Assert.Equal([false, true], rows.Select(r => r.RequiresHumanFollowUp));
        }
    }

    [Fact]
    public void Database_AllowsTheSameServiceRequestIdInDifferentConversations()
    {
        using var db = new SqliteDatabase();
        var first = db.AddTicket("TG-CS-20261022-0001");
        var second = db.AddTicket("TG-CS-20261022-0002");

        using var context = db.CreateContext();
        context.TicketInteractions.Add(Interaction(first, "conv-a", "documents"));
        context.TicketInteractions.Add(Interaction(second, "conv-b", "documents"));
        context.SaveChanges();

        Assert.Equal(2, context.TicketInteractions.Count(i => i.GenesysServiceRequestId == "documents"));
    }

    [Fact]
    public void HistoricalRows_WithNoServiceRequestId_RemainValid_AndTheFlagDefaultsToFalse()
    {
        using var db = new SqliteDatabase();
        var ticket = db.AddTicket("TG-CS-20261022-0001");

        using (var context = db.CreateContext())
        {
            context.TicketInteractions.Add(Interaction(ticket, "conv-historical", null));
            context.SaveChanges();
        }

        using (var context = db.CreateContext())
        {
            var row = context.TicketInteractions.Single();
            Assert.Null(row.GenesysServiceRequestId);
            Assert.False(row.RequiresHumanFollowUp);
        }
    }
}
