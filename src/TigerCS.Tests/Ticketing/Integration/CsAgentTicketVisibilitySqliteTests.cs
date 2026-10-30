using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Tests.Ticketing.Dashboard;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The CS Agent visibility predicate (<c>TicketQueryFilters.VisibleTo</c>) as real SQL over the real schema: a ticket is visible to an
/// agent when it is assigned to them right now or when they have a <c>TicketInteractionHandlers</c> row on it — and in no other case,
/// whatever the department, queue or filters. Every read the agent has (queue, dashboard KPIs and breakdowns, customer history, customer
/// directory, pending work) runs through it, so each is proven here rather than only against an in-memory fake.
/// </summary>
public sealed class CsAgentTicketVisibilitySqliteTests : IDisposable
{
    private static readonly string[] Agent = [Roles.CsAgent];
    private static readonly string[] Supervisor = [Roles.CsSupervisor];

    // Employees here are bare ids (the fixture seeds no identity users), so foreign keys to AspNetUsers are not enforced.
    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);
    private readonly Guid _agentA = Guid.NewGuid();
    private readonly Guid _agentB = Guid.NewGuid();

    public void Dispose() => _db.Dispose();

    private static TicketListRequestDto Everything(string? search = null) =>
        new(null, null, null, null, null, null, search, null, null, 1, 100);

    private async Task<long[]> QueueAsync(Guid caller, string[] roles, TicketListRequestDto? request = null)
    {
        using var context = _db.CreateContext();
        var result = await _db.CreateQueryService(context).GetQueueAsync(caller, roles, request ?? Everything());
        Assert.Equal(result.Items.Count, result.TotalCount); // the count and the page agree: no row is counted twice or hidden after counting
        return [.. result.Items.Select(i => i.TicketId).Order()];
    }

    // =====================================================================
    // Ticket queue and search
    // =====================================================================

    [Fact]
    public async Task TheQueue_ShowsAssignedAndHandledTickets_AndNoOthers_InTheSameDepartment()
    {
        long assignedToA, handledByA, bothAssignedAndHandled, assignedToB, unassigned;
        using (var context = _db.CreateContext())
        {
            assignedToA = _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA).TicketId;
            handledByA = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
            _db.AddHandler(context, context.Tickets.Find(handledByA)!, _agentA);
            bothAssignedAndHandled = _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA).TicketId;
            _db.AddHandler(context, context.Tickets.Find(bothAssignedAndHandled)!, _agentA);
            _db.AddHandler(context, context.Tickets.Find(bothAssignedAndHandled)!, _agentA, additionalInteraction: true); // a second interaction, same agent
            assignedToB = _db.AddTicket(context, _db.CustomerServiceId, owner: _agentB).TicketId;
            unassigned = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
        }

        var seenByA = await QueueAsync(_agentA, Agent);

        Assert.Equal([assignedToA, handledByA, bothAssignedAndHandled], seenByA);
        Assert.DoesNotContain(assignedToB, seenByA);
        Assert.DoesNotContain(unassigned, seenByA);
    }

    [Fact]
    public async Task ATicketHandledByTwoAgents_IsInBothQueues_AndNotInAThirds()
    {
        long transferred;
        using (var context = _db.CreateContext())
        {
            var ticket = _db.AddTicket(context, _db.CustomerServiceId);
            transferred = ticket.TicketId;
            _db.AddHandler(context, ticket, _agentA);
            _db.AddHandler(context, ticket, _agentB, additionalInteraction: true);
        }

        Assert.Equal([transferred], await QueueAsync(_agentA, Agent));
        Assert.Equal([transferred], await QueueAsync(_agentB, Agent));
        Assert.Empty(await QueueAsync(Guid.NewGuid(), Agent));
    }

    [Fact]
    public async Task Reassigning_RemovesTheAssignmentGrant_ButNotTheHandlingGrant()
    {
        long assignedOnly, assignedAndHandled;
        using (var context = _db.CreateContext())
        {
            var first = _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA);
            var second = _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA);
            _db.AddHandler(context, second, _agentA);
            assignedOnly = first.TicketId;
            assignedAndHandled = second.TicketId;
        }

        Assert.Equal([assignedOnly, assignedAndHandled], await QueueAsync(_agentA, Agent));

        using (var context = _db.CreateContext())
        {
            foreach (var id in new[] { assignedOnly, assignedAndHandled })
            {
                context.Tickets.Find(id)!.AssignTo(_agentB);
            }

            context.SaveChanges();
        }

        Assert.Equal([assignedAndHandled], await QueueAsync(_agentA, Agent));       // lost one, kept the one it handled
        Assert.Equal([assignedOnly, assignedAndHandled], await QueueAsync(_agentB, Agent));
    }

    [Fact]
    public async Task ADepartmentTransfer_ClearsTheOwner_SoAnAssigneeWhoNeverHandledItNoLongerSeesIt()
    {
        long id;
        using (var context = _db.CreateContext())
        {
            id = _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA).TicketId;
        }

        using (var context = _db.CreateContext())
        {
            context.Tickets.Find(id)!.TransferToDepartment(_db.CollectionsId);
            context.SaveChanges();
        }

        Assert.Empty(await QueueAsync(_agentA, Agent));
    }

    [Fact]
    public async Task AgentMembershipOfTheTicketsDepartment_GrantsNothing()
    {
        using (var context = _db.CreateContext())
        {
            context.UserDepartmentAssignments.Add(new UserDepartmentAssignment(_agentA, _db.CustomerServiceId, true, DateTime.UtcNow, null));
            context.SaveChanges();
            _db.AddTicket(context, _db.CustomerServiceId);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _agentB);
        }

        Assert.Empty(await QueueAsync(_agentA, Agent));
    }

    [Fact]
    public async Task SearchFiltersAndPaging_OnlyNarrowTheVisibleSet_NeverWidenIt()
    {
        long mine, theirs;
        using (var context = _db.CreateContext())
        {
            mine = _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA, customerName: "Fatima Mine").TicketId;
            theirs = _db.AddTicket(context, _db.CustomerServiceId, owner: _agentB, customerName: "Fatima Theirs").TicketId;
        }

        Assert.Equal([mine], await QueueAsync(_agentA, Agent, Everything("Fatima")));
        Assert.Empty(await QueueAsync(_agentA, Agent, Everything("Theirs")));
        Assert.Empty(await QueueAsync(_agentA, Agent, Everything() with { OwnerEmployeeId = _agentB })); // asking for someone else's tickets finds none
        Assert.Empty(await QueueAsync(_agentA, Agent, Everything() with { DepartmentId = _db.CollectionsId }));
        Assert.Equal([mine], await QueueAsync(_agentA, Agent, Everything() with { OwnerEmployeeId = _agentA, DepartmentId = _db.CustomerServiceId }));
        Assert.Contains(theirs, await QueueAsync(Guid.NewGuid(), Supervisor));
    }

    [Fact]
    public async Task APageOfTheAgentsTickets_CountsTheWholeVisibleSet()
    {
        using (var context = _db.CreateContext())
        {
            for (var i = 0; i < 5; i++)
            {
                _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA);
            }

            _db.AddTicket(context, _db.CustomerServiceId, owner: _agentB);
        }

        using var read = _db.CreateContext();
        var page = await _db.CreateQueryService(read).GetQueueAsync(_agentA, Agent, Everything() with { Page = 1, PageSize = 2 });

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(5, page.TotalCount);
    }

    [Fact]
    public async Task TheDepartmentSetAloneIsFailClosed_AQueryWithNoAgentScopeAndNoDepartmentsReturnsNothing()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA);
        }

        using var read = _db.CreateContext();
        var result = await new TicketRepository(read).SearchAsync(
            new TicketQuery([], null, null, null, null, null, null, null, TicketSortBy.CreatedAtUtc, true, 1, 50));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SupervisorsAndManagers_StillSeeEverything()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId);
            _db.AddTicket(context, _db.CollectionsId, owner: _agentB);
        }

        Assert.Equal(2, (await QueueAsync(Guid.NewGuid(), Supervisor)).Length);
        Assert.Equal(2, (await QueueAsync(Guid.NewGuid(), [Roles.CsManager])).Length);
        Assert.Equal(2, (await QueueAsync(Guid.NewGuid(), [Roles.SystemAdministrator])).Length);
        Assert.Equal(2, (await QueueAsync(_agentA, [Roles.CsAgent, Roles.CsSupervisor])).Length);
    }

    // =====================================================================
    // Dashboard
    // =====================================================================

    [Fact]
    public async Task DashboardKpisAndBreakdowns_CoverOnlyTheAgentsVisibleTickets()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA, priorityId: (byte)TigerCS.Domain.Modules.SlaAndEscalation.PriorityLevel.High);
            var handled = _db.AddTicket(context, _db.CollectionsId);
            _db.AddHandler(context, handled, _agentA);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _agentB);   // not visible
            _db.AddTicket(context, _db.CollectionsId);                       // unassigned, not visible
            _db.AddTicket(context, _db.RegistrationId, owner: _agentB, status: TicketStatus.InProgress); // not visible
        }

        using var read = _db.CreateContext();
        var service = _db.CreateService(read);
        var agentView = await service.GetOverviewAsync(_agentA, Agent, new DashboardOverviewRequestDto());
        var supervisorView = await service.GetOverviewAsync(Guid.NewGuid(), Supervisor, new DashboardOverviewRequestDto());

        Assert.Equal(2, agentView.Kpis.OpenTickets);
        Assert.Equal(1, agentView.Kpis.MyTickets);
        Assert.Equal(1, agentView.Kpis.InDepartmentQueue); // the unassigned ticket the agent handled
        Assert.Equal(2, agentView.VolumeTotal);
        Assert.Equal(2, agentView.VolumeByDepartment.Sum(d => d.Count));
        Assert.Equal(2, agentView.StatusBreakdown.Sum(d => d.Count));
        Assert.Equal(2, agentView.PriorityBreakdown.Sum(d => d.Count));
        Assert.Equal(2, agentView.BacklogAgeing.Sum(d => d.Count));
        Assert.Equal(2, agentView.RecentTickets.Count);
        Assert.Equal(5, supervisorView.Kpis.OpenTickets);
    }

    [Fact]
    public async Task DashboardSummary_CountsAndAttentionList_AreScopedToo()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA);
            _db.AddHandler(context, _db.AddTicket(context, _db.CollectionsId), _agentA);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _agentB);
            _db.AddTicket(context, _db.CollectionsId);
        }

        using var read = _db.CreateContext();
        var summary = await _db.CreateService(read).GetSummaryAsync(_agentA, Agent);

        Assert.Equal(2, summary.OpenTickets);
        Assert.Equal(1, summary.MyTickets);
        Assert.Equal(1, summary.Unassigned);
        // "Requiring attention" is breached / high / unassigned work: of the agent's two, only the unassigned one they handled qualifies —
        // and the other unassigned Collections ticket (nobody they know handled it) must not appear.
        Assert.Equal(_db.CollectionsId, Assert.Single(summary.AttentionTickets).CurrentDepartmentId);
    }

    [Fact]
    public async Task ADashboardDepartmentFilter_NarrowsAnAgentsView_NeverWidensIt()
    {
        long inCs, inCollections;
        using (var context = _db.CreateContext())
        {
            inCs = _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA).TicketId;
            inCollections = _db.AddTicket(context, _db.CollectionsId, owner: _agentA).TicketId;
            _db.AddTicket(context, _db.CollectionsId, owner: _agentB);
        }

        using var read = _db.CreateContext();
        var service = _db.CreateService(read);

        var collectionsOnly = await service.GetOverviewAsync(
            _agentA, Agent, new DashboardOverviewRequestDto(DepartmentId: _db.CollectionsId));
        var someoneElsesOwner = await service.GetOverviewAsync(
            _agentA, Agent, new DashboardOverviewRequestDto(OwnerEmployeeId: _agentB));

        Assert.Equal(1, collectionsOnly.Kpis.OpenTickets);   // their Collections ticket only — not B's
        Assert.Equal(inCollections, Assert.Single(collectionsOnly.RecentTickets).TicketId);
        Assert.Equal(0, someoneElsesOwner.Kpis.OpenTickets);
        Assert.NotEqual(inCs, inCollections);
    }

    // =====================================================================
    // Customer history and directory
    // =====================================================================

    [Fact]
    public async Task CustomerHistory_ListsOnlyTheCustomersTicketsTheAgentCanSee()
    {
        long mine;
        using (var context = _db.CreateContext())
        {
            mine = _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA, customerName: "Same Customer").TicketId;
            // A different ticket carrying the very same CRM identity, assigned to someone else.
            var other = Ticket.CreateVerifiedFromCrmBuyer(
                "TG-TST-20260910-HIST", _db.CustomerServiceId,
                crmBuyerCustomerId: context.Tickets.Find(mine)!.CrmBuyerCustomerId!.Value, crmBuyerLeadId: 1, crmBuyerUnitId: 1, crmBuyerProjectId: 1,
                crmBuyerCustomerName: "Same Customer", crmBuyerProjectName: "Tiger Tower", crmBuyerUnitNumber: "U-1",
                categoryId: _db.CsCategoryId, priorityId: 3, requestSummary: "someone else's", createdAtUtc: DashboardSqliteFixture.Now.AddHours(-2));
            other.AssignTo(_agentB);
            context.Tickets.Add(other);
            context.SaveChanges();
        }

        using var read = _db.CreateContext();
        var crmId = read.Tickets.Single(t => t.TicketId == mine).CrmBuyerCustomerId!.Value;
        var history = new CustomerHistoryAppService(
            new TicketRepository(read), new FakeIntakeRecordRepository(), new FakeTicketResolutionRepository(),
            new FakeTicketStatusHistoryRepository(), _db.CreateQueryService(read), ReopenPolicy.Default, TimeProvider.System);

        var asAgent = await history.GetByCrmCustomerIdAsync(_agentA, Agent, crmId);
        var asSupervisor = await history.GetByCrmCustomerIdAsync(Guid.NewGuid(), Supervisor, crmId);

        Assert.Equal(mine, Assert.Single(asAgent.Tickets).TicketId);
        Assert.Equal(1, asAgent.TotalTickets);
        Assert.Equal(2, asSupervisor.TotalTickets);
    }

    [Fact]
    public async Task TheCustomerDirectory_ListsOnlyCustomersWithAVisibleTicket()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA, customerName: "Visible Customer");
            _db.AddTicket(context, _db.CustomerServiceId, owner: _agentB, customerName: "Hidden Customer");
            _db.AddHandler(context, _db.AddTicket(context, _db.CollectionsId, customerName: "Handled Customer"), _agentA);
        }

        using var read = _db.CreateContext();
        var directory = new CustomerDirectoryAppService(
            new CustomerDirectoryRepository(read), _db.CreateQueryService(read));

        var asAgent = await directory.ListAsync(_agentA, Agent, new CustomerDirectoryListRequestDto(PageSize: 50));
        var asSupervisor = await directory.ListAsync(Guid.NewGuid(), Supervisor, new CustomerDirectoryListRequestDto(PageSize: 50));

        Assert.Equal(["Handled Customer", "Visible Customer"], asAgent.Items.Select(i => i.DisplayName ?? "").Order().ToArray());
        Assert.Equal(3, asSupervisor.Items.Count);

        var hidden = asSupervisor.Items.Single(i => i.DisplayName == "Hidden Customer");
        var profile = await directory.GetProfileAsync(_agentA, Agent, hidden.CustomerKey);
        Assert.Equal(CustomerDirectoryProfileOutcome.NotFound, profile.Outcome); // reached by direct key, still not theirs
    }
}
