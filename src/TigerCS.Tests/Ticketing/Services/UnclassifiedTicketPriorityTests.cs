using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// An Unclassified ticket carries no priority, and every reader of priority
/// has to say what it does with that.
///
/// <para>
/// The specific hazard these pin down: both SQL Server and LINQ-to-objects
/// sort NULL <i>first</i> ascending, and ascending is "most urgent first"
/// here (1 = Critical). Left alone, a ticket nobody has read would outrank
/// every Critical ticket in the queue and in the attention list, and a
/// <c>_ =&gt;</c> arm in a display switch would label it "Medium". Each of
/// those would be the system asserting an urgency judgement no human made.
/// </para>
/// </summary>
public class UnclassifiedTicketPriorityTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        TicketQueryAppService Queries,
        DashboardAppService Dashboard,
        FakeTicketRepository Tickets,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeTicketResolutionRepository Resolutions);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var resolutions = new FakeTicketResolutionRepository();
        tickets.Resolutions = resolutions;
        var queries = new TicketQueryAppService(
            tickets, departmentAssignments, resolutions, ReopenPolicy.Default, TimeProvider.System);
        return new Fixture(
            queries, new DashboardAppService(tickets, queries, TimeProvider.System, new FakeDashboardQueryRepository(), departmentAssignments),
            tickets, departmentAssignments, resolutions);
    }

    private static async Task<Ticket> SeedClassifiedAsync(
        Fixture f, string number, byte priorityId, DateTime? createdAtUtc = null)
    {
        var ticket = Ticket.CreateUnverified(
            number, departmentId: 1, categoryId: 5, priorityId, "Classified seed", createdAtUtc ?? Now);
        await f.Tickets.AddAsync(ticket);
        return ticket;
    }

    private static async Task<Ticket> SeedUnclassifiedAsync(Fixture f, string number, DateTime? createdAtUtc = null)
    {
        var ticket = Ticket.CreateUnclassified(number, departmentId: 1, "Genesys inquiry", createdAtUtc ?? Now);
        await f.Tickets.AddAsync(ticket);
        return ticket;
    }

    private static TicketListRequestDto ByPriority(string sortDir) =>
        new(null, null, null, null, null, null, null, SortBy: "priority", SortDir: sortDir, Page: 1, PageSize: 50);

    // ---- Queue sorting ----

    [Fact]
    public async Task Queue_SortedByPriorityAscending_PutsTheUnclassifiedTicketLast_NotAheadOfCritical()
    {
        var f = CreateService();
        var unclassified = await SeedUnclassifiedAsync(f, "TG-CS-20260910-0001");
        var critical = await SeedClassifiedAsync(f, "TG-CS-20260910-0002", (byte)PriorityLevel.Critical);
        var low = await SeedClassifiedAsync(f, "TG-CS-20260910-0003", (byte)PriorityLevel.Low);

        var page = await f.Queries.GetQueueAsync(Guid.NewGuid(), [Roles.CsManager], ByPriority("asc"));

        Assert.Equal(
            [critical.TicketId, low.TicketId, unclassified.TicketId],
            page.Items.Select(t => t.TicketId).ToArray());
    }

    [Fact]
    public async Task Queue_SortedByPriorityDescending_AlsoPutsTheUnclassifiedTicketLast()
    {
        var f = CreateService();
        var unclassified = await SeedUnclassifiedAsync(f, "TG-CS-20260910-0001");
        var critical = await SeedClassifiedAsync(f, "TG-CS-20260910-0002", (byte)PriorityLevel.Critical);
        var low = await SeedClassifiedAsync(f, "TG-CS-20260910-0003", (byte)PriorityLevel.Low);

        var page = await f.Queries.GetQueueAsync(Guid.NewGuid(), [Roles.CsManager], ByPriority("desc"));

        // "Least urgent first" reverses the judged tiers but never promotes
        // the unjudged one — an unclassified ticket is not the least urgent
        // either, it is simply not ranked.
        Assert.Equal(
            [low.TicketId, critical.TicketId, unclassified.TicketId],
            page.Items.Select(t => t.TicketId).ToArray());
    }

    [Fact]
    public async Task Queue_FilteredByAPriority_NeverMatchesTheUnclassifiedTicket()
    {
        var f = CreateService();
        await SeedUnclassifiedAsync(f, "TG-CS-20260910-0001");
        var medium = await SeedClassifiedAsync(f, "TG-CS-20260910-0002", (byte)PriorityLevel.Medium);

        var page = await f.Queries.GetQueueAsync(
            Guid.NewGuid(), [Roles.CsManager],
            new TicketListRequestDto(null, null, (byte)PriorityLevel.Medium, null, null, null, null, null, null, 1, 50));

        Assert.Equal(medium.TicketId, Assert.Single(page.Items).TicketId);
    }

    [Fact]
    public async Task Queue_ProjectsTheUnclassifiedTicketsPriorityAsNull_RatherThanSubstitutingATier()
    {
        var f = CreateService();
        var unclassified = await SeedUnclassifiedAsync(f, "TG-CS-20260910-0001");

        var page = await f.Queries.GetQueueAsync(
            Guid.NewGuid(), [Roles.CsManager],
            new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 50));

        var row = Assert.Single(page.Items);
        Assert.Equal(unclassified.TicketId, row.TicketId);
        Assert.Null(row.PriorityId);
        Assert.Null(row.CategoryId);
    }

    // ---- Dashboard ----

    [Fact]
    public async Task Dashboard_DoesNotCountAnUnclassifiedTicketAsCriticalOrHigh()
    {
        var f = CreateService();
        await SeedUnclassifiedAsync(f, "TG-CS-20260910-0001");
        await SeedClassifiedAsync(f, "TG-CS-20260910-0002", (byte)PriorityLevel.High);
        await SeedClassifiedAsync(f, "TG-CS-20260910-0003", (byte)PriorityLevel.Low);

        var summary = await f.Dashboard.GetSummaryAsync(Guid.NewGuid(), [Roles.CsManager]);

        // Three open tickets, but only the High one has been judged urgent.
        Assert.Equal(3, summary.OpenTickets);
        Assert.Equal(1, summary.CriticalOrHigh);
    }

    [Fact]
    public async Task Dashboard_AttentionList_RanksAnUnclassifiedTicketBelowACriticalOne()
    {
        var f = CreateService();
        // Both appear — every unassigned ticket does — but the ranking must
        // not let the unjudged one lead.
        var unclassified = await SeedUnclassifiedAsync(f, "TG-CS-20260910-0001", Now.AddHours(-2));
        var critical = await SeedClassifiedAsync(f, "TG-CS-20260910-0002", (byte)PriorityLevel.Critical, Now);

        var summary = await f.Dashboard.GetSummaryAsync(Guid.NewGuid(), [Roles.CsManager]);

        Assert.Equal(critical.TicketId, summary.AttentionTickets[0].TicketId);
        Assert.Contains(summary.AttentionTickets, t => t.TicketId == unclassified.TicketId);
    }
}
