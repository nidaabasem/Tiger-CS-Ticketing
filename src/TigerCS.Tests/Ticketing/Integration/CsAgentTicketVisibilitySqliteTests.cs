using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Persistence;
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

    // =====================================================================
    // Awaiting Human Agent: waiting work an agent may claim
    // =====================================================================

    private TicketAgentHandoff AddWaitingWork(TigerCsDbContext context, int departmentId, Guid? claimedBy = null)
    {
        var ticket = _db.AddTicket(context, departmentId);
        var interaction = TicketInteraction.CreateLocal(ticket.TicketId, WellKnownChannels.Phone, "+971500000000", DashboardSqliteFixture.Now, isOriginatingInteraction: true);
        context.TicketInteractions.Add(interaction);
        context.SaveChanges();
        var handoff = new TicketAgentHandoff(
            ticket.TicketId, interaction.TicketInteractionId, departmentId, WellKnownChannels.Phone,
            DashboardSqliteFixture.Now.AddMinutes(-10), DashboardSqliteFixture.Now.AddMinutes(-10));
        if (claimedBy is { } claimer)
        {
            handoff.ClaimBy(claimer, DashboardSqliteFixture.Now.AddMinutes(-5));
        }

        context.TicketAgentHandoffs.Add(handoff);
        context.SaveChanges();
        return handoff;
    }

    [Fact]
    public async Task TheWaitingWorkList_AndTheDashboardTile_ShowAnAgentUnclaimedWorkOfTheirOwnDepartmentsOnly()
    {
        long inMine, inOther, claimedByColleague, assignedToMe;
        using (var context = _db.CreateContext())
        {
            inMine = AddWaitingWork(context, _db.CustomerServiceId).TicketAgentHandoffId;
            inOther = AddWaitingWork(context, _db.CollectionsId).TicketAgentHandoffId;
            claimedByColleague = AddWaitingWork(context, _db.CustomerServiceId, claimedBy: _agentB).TicketAgentHandoffId;
            var mine = AddWaitingWork(context, _db.CollectionsId, claimedBy: _agentA);   // claimed by me, in a department I am not in
            context.Tickets.Find(mine.TicketId)!.AssignTo(_agentA);
            context.SaveChanges();
            assignedToMe = mine.TicketAgentHandoffId;
        }

        using var read = _db.CreateContext();
        var repository = new TicketAgentHandoffRepository(read);
        var claimable = new[] { _db.CustomerServiceId };

        var list = await repository.SearchAsync(new AgentHandoffQuery(
            [], AgentScopeEmployeeId: _agentA, ClaimableDepartmentIds: claimable));
        var tile = await repository.GetAwaitingHumanAgentSnapshotAsync([], CancellationToken.None, _agentA, claimable);
        var supervisorList = await repository.SearchAsync(new AgentHandoffQuery(null));

        // Mine (claimable + waiting) and the one I already own; not another department's, not a colleague's claimed work.
        Assert.Equal(
            new[] { inMine, assignedToMe }.Order(),
            list.Items.Select(i => i.Handoff.TicketAgentHandoffId).Order());
        Assert.DoesNotContain(list.Items, i => i.Handoff.TicketAgentHandoffId == inOther || i.Handoff.TicketAgentHandoffId == claimedByColleague);
        Assert.Equal(1, tile.Count);                       // waiting & claimable only
        Assert.Equal(4, supervisorList.TotalCount);        // a supervisor's view is unchanged
    }

    // =====================================================================
    // Migration backfill
    // =====================================================================

    [Fact]
    public async Task TheMigrationBackfill_CopiesEachLegacySingleHandler_OnceAndOnlyThose()
    {
        long withHandler, withoutHandler;
        using (var context = _db.CreateContext())
        {
            var first = _db.AddTicket(context, _db.CustomerServiceId);
            var second = _db.AddTicket(context, _db.CustomerServiceId);
            var handled = TicketInteraction.CreateFromGenesys(
                first.TicketId, WellKnownChannels.Phone, "+971500000000", "conv-bf-1", null, null, null, null, null, null, "Inbound", DashboardSqliteFixture.Now,
                isOriginatingInteraction: true);
            handled.RecordHandlingAgentIfAbsent("genesys-agent-a", _agentA);
            var unhandled = TicketInteraction.CreateFromGenesys(
                second.TicketId, WellKnownChannels.Phone, "+971500000000", "conv-bf-2", null, null, null, null, null, null, "Inbound", DashboardSqliteFixture.Now,
                isOriginatingInteraction: true);
            context.TicketInteractions.AddRange(handled, unhandled);
            context.SaveChanges();
            withHandler = first.TicketId;
            withoutHandler = second.TicketId;
        }

        // The very SQL the migration runs, taken from its operations.
        var migration = new TigerCS.Infrastructure.Persistence.Migrations.AddTicketInteractionHandlers();
        var backfill = migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Single().Sql;

        using (var context = _db.CreateContext())
        {
            context.Database.ExecuteSqlRaw(backfill);
            context.Database.ExecuteSqlRaw(backfill.Replace("INSERT INTO", "INSERT OR IGNORE INTO")); // sanity: a second run must not duplicate
        }

        using var read = _db.CreateContext();
        var row = Assert.Single(read.TicketInteractionHandlers.ToList());
        Assert.Equal(withHandler, row.TicketId);
        Assert.Equal(_agentA, row.HandledByUserId);
        Assert.Equal("genesys-agent-a", row.GenesysAgentUserId);
        Assert.Equal(InteractionHandlerSource.Legacy, row.Source);
        Assert.Empty(read.TicketInteractionHandlers.Where(h => h.TicketId == withoutHandler));
        // ...and the agent then sees exactly that ticket.
        Assert.Equal([withHandler], await QueueAsync(_agentA, Agent));
    }

    // =====================================================================
    // Ticket creator: read-only access, "Created by Me", creator backfill
    // =====================================================================

    private Ticket AddCreatedTicket(TigerCsDbContext context, Guid creator, int departmentId, Guid? owner = null)
    {
        var ticket = _db.AddTicket(context, departmentId, owner: owner);
        ticket.RecordCreator(creator);
        context.SaveChanges();
        return ticket;
    }

    [Fact]
    public async Task ACreatorKeepsTheTicketAfterItMovesDepartment_AsReadOnly_InTheQueueDashboardAndDetail()
    {
        long id;
        using (var context = _db.CreateContext())
        {
            id = AddCreatedTicket(context, _agentA, _db.CustomerServiceId, owner: _agentA).TicketId;
            AddCreatedTicket(context, _agentB, _db.CustomerServiceId, owner: _agentB);  // someone else's
        }

        using (var context = _db.CreateContext())
        {
            context.Tickets.Find(id)!.TransferToDepartment(_db.CollectionsId);
            context.SaveChanges();
        }

        Assert.Equal([id], await QueueAsync(_agentA, Agent));
        Assert.Empty(await QueueAsync(Guid.NewGuid(), Agent));

        using var read = _db.CreateContext();
        var detail = await _db.CreateQueryService(read).GetDetailAsync(_agentA, Agent, id);
        Assert.Equal(TicketQueryOutcome.Success, detail.Outcome);
        Assert.True(detail.Response!.IsReadOnlyAccess);
        Assert.Equal(_db.CollectionsId, detail.Response.CurrentDepartmentId);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await _db.CreateQueryService(read).GetDetailAsync(_agentB, Agent, id)).Outcome);

        var overview = await _db.CreateService(read).GetOverviewAsync(_agentA, Agent, new DashboardOverviewRequestDto());
        Assert.Equal(1, overview.Kpis.OpenTickets);
        Assert.Equal(0, overview.Kpis.MyTickets);   // the owner was cleared by the transfer
    }

    [Fact]
    public async Task CreatedByMe_NarrowsToTheCallersCreations_AndNeverToSomeoneElses()
    {
        long mineMoved, mineAssigned, assignedButNotMine;
        using (var context = _db.CreateContext())
        {
            mineMoved = AddCreatedTicket(context, _agentA, _db.CollectionsId).TicketId;
            mineAssigned = AddCreatedTicket(context, _agentA, _db.CustomerServiceId, owner: _agentA).TicketId;
            assignedButNotMine = AddCreatedTicket(context, _agentB, _db.CustomerServiceId, owner: _agentA).TicketId;
        }

        Assert.Equal([mineMoved, mineAssigned], await QueueAsync(_agentA, Agent, Everything() with { CreatedByMe = true }));
        Assert.Equal([mineMoved, mineAssigned, assignedButNotMine], await QueueAsync(_agentA, Agent));
        Assert.Equal([assignedButNotMine], await QueueAsync(_agentB, Agent, Everything() with { CreatedByMe = true }));
    }

    [Fact]
    public async Task ACreatorsOwnTicketInAnotherDepartment_IsNotClaimableWork_AndIsNeverListedAsPendingWork()
    {
        long handoffId, ticketId;
        using (var context = _db.CreateContext())
        {
            var work = AddWaitingWork(context, _db.CollectionsId);
            ticketId = work.TicketId;
            context.Tickets.Find(work.TicketId)!.RecordCreator(_agentA);
            context.SaveChanges();
            handoffId = work.TicketAgentHandoffId;
        }

        using var read = _db.CreateContext();
        var list = await new TicketAgentHandoffRepository(read).SearchAsync(new AgentHandoffQuery(
            [], AgentScopeEmployeeId: _agentA, ClaimableDepartmentIds: [_db.CustomerServiceId]));

        Assert.DoesNotContain(list.Items, i => i.Handoff.TicketAgentHandoffId == handoffId);
        // ...while the ticket itself is still theirs to read.
        Assert.Contains(ticketId, await QueueAsync(_agentA, Agent));
    }

    [Fact]
    public async Task TheCreatorBackfill_FillsFromTheCreateAudit_AndOnlyThere()
    {
        long audited, unaudited, alreadySet, doubleAudited;
        var original = Guid.NewGuid();
        using (var context = _db.CreateContext())
        {
            audited = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
            unaudited = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
            alreadySet = AddCreatedTicket(context, original, _db.CustomerServiceId).TicketId;
            doubleAudited = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
            void Audit(long ticket, Guid? actor, string action = "Create") => context.AuditEntries.Add(
                new TigerCS.Domain.Audit.AuditEntry(actor, action, "Ticket", ticket.ToString(), null, null, Guid.NewGuid(), DashboardSqliteFixture.Now));
            Audit(audited, _agentA);
            Audit(alreadySet, _agentB);                 // must not overwrite a creator that is already recorded
            Audit(doubleAudited, _agentA);
            Audit(doubleAudited, _agentB);              // two Create rows: the first wins, and the statement does not fail
            Audit(unaudited, _agentB, action: "Update"); // not a creation
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            context.Database.ExecuteSqlRaw(TigerCS.Infrastructure.Persistence.Migrations.AddTicketCreatedBy.BackfillSql);
        }

        using var read = _db.CreateContext();
        Guid? CreatorOf(long id) => read.Tickets.Single(t => t.TicketId == id).CreatedByEmployeeId;
        Assert.Equal(_agentA, CreatorOf(audited));
        Assert.Null(CreatorOf(unaudited));
        Assert.Equal(original, CreatorOf(alreadySet));
        Assert.Equal(_agentA, CreatorOf(doubleAudited));
    }
}
