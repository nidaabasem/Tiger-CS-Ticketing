using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Dashboard;

/// <summary>
/// The four closure KPIs (Reviewed Tickets, Not Reviewed, Closed by AI,
/// Closed by Agent), end to end from the application service down to real
/// SQL. Each is proven against the persisted facts the system actually
/// records — <c>TicketReviews</c> for feedback and the closing
/// <c>AuditEntries</c> row for who closed — and against the ticket queue's
/// <c>closureCategory</c> drill-down, which must list exactly what the card
/// counted.
/// </summary>
public sealed class DashboardClosureKpiTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;
    private static readonly string[] CrossDepartment = [Roles.CsSupervisor];

    private readonly DashboardSqliteFixture _db = new();

    public void Dispose() => _db.Dispose();

    private Task<DashboardOverviewDto> OverviewAsync(
        Guid caller, string[] roles, DashboardOverviewRequestDto? request = null)
    {
        using var context = _db.CreateContext();
        return _db.CreateService(context).GetOverviewAsync(caller, roles, request ?? new DashboardOverviewRequestDto());
    }

    private async Task<int> QueueCountAsync(
        Guid caller, string[] roles, TicketClosureCategory category, DateOnly? from = null, DateOnly? to = null,
        int? departmentId = null, string? ticketStatus = null)
    {
        using var context = _db.CreateContext();
        var result = await _db.CreateQueryService(context).GetQueueAsync(
            caller, roles,
            new TicketListRequestDto(
                departmentId, null, null, ticketStatus, null, null, null, null, null, 1, 100,
                CreatedFrom: from, CreatedTo: to, ClosureCategory: category.ToString()));
        return result.TotalCount;
    }

    private Ticket ClosedTicket(int departmentId, string? closureAction, DateTime? createdAtUtc = null)
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, departmentId, createdAtUtc: createdAtUtc, status: TicketStatus.Closed);
        if (closureAction is not null)
        {
            _db.AddClosureAudit(context, ticket, closureAction, actor: Guid.NewGuid());
        }

        return ticket;
    }

    // ---------------------------------------------------------------
    // Reviewed Tickets / Not Reviewed
    // ---------------------------------------------------------------

    [Fact]
    public async Task ReviewedTickets_CountsClosedTicketsWithASubmittedReview_AndNotReviewedCountsTheRest()
    {
        var reviewed = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        var alsoReviewed = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAi);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, null);
        using (var write = _db.CreateContext())
        {
            _db.AddReview(write, reviewed);
            _db.AddReview(write, alsoReviewed);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(2, overview.Kpis.ReviewedTickets);
        Assert.Equal(2, overview.Kpis.NotReviewedTickets);
    }

    [Fact]
    public async Task ReviewedTickets_IgnoresInvitationsWithoutASubmission_AndTestModeReviews()
    {
        var invitedOnly = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        var testReview = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        var realReview = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        using (var write = _db.CreateContext())
        {
            _db.AddReview(write, invitedOnly, submit: false);
            _db.AddReview(write, testReview, isTest: true);
            _db.AddReview(write, realReview);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        // An invitation is not feedback and a Test-mode review is not customer
        // feedback — only the one real submission counts.
        Assert.Equal(1, overview.Kpis.ReviewedTickets);
        Assert.Equal(2, overview.Kpis.NotReviewedTickets);
    }

    [Fact]
    public async Task ReviewedAndNotReviewed_CountEachTicketOnce_WhateverItsReviewsAndInvitations()
    {
        var ticket = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        using (var write = _db.CreateContext())
        {
            // A real and a Test-mode review, plus an extra unsubmitted invitation, on one cycle.
            _db.AddReview(write, ticket);
            _db.AddReview(write, ticket, isTest: true);
            _db.AddReview(write, ticket, submit: false);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(1, overview.Kpis.ReviewedTickets);
        Assert.Equal(0, overview.Kpis.NotReviewedTickets);
    }

    [Fact]
    public async Task ReviewedTickets_OnlyCountsActiveAndResolvedTicketsAsNeither()
    {
        using var context = _db.CreateContext();
        var open = _db.AddTicket(context, _db.CustomerServiceId);
        var resolved = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Resolved);
        _db.AddReview(context, open);
        _db.AddReview(context, resolved);

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        // The KPIs are about Closed tickets; a review on a ticket that is not closed does not move them.
        Assert.Equal(0, overview.Kpis.ReviewedTickets);
        Assert.Equal(0, overview.Kpis.NotReviewedTickets);
    }

    [Fact]
    public async Task ReviewedTickets_ARecycledTicketNeedsAReviewForItsCurrentClosure()
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Closed);
        _db.AddReview(context, ticket); // cycle 0

        // Reopen and close again: cycle 1, not yet reviewed.
        ticket.Reopen(ticket.CurrentDepartmentId);
        ticket.AssignTo(_db.LifecycleWorkerId);
        ticket.Resolve(ResolutionOutcome.Resolved, null);
        ticket.Close();
        context.SaveChanges();

        var beforeReview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);
        Assert.Equal(1, ticket.ReopenCount);
        Assert.Equal(0, beforeReview.Kpis.ReviewedTickets);
        Assert.Equal(1, beforeReview.Kpis.NotReviewedTickets);

        _db.AddReview(context, ticket); // cycle 1
        var afterReview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);
        Assert.Equal(1, afterReview.Kpis.ReviewedTickets);
        Assert.Equal(0, afterReview.Kpis.NotReviewedTickets);
    }

    // ---------------------------------------------------------------
    // Closed by AI / Closed by Agent
    // ---------------------------------------------------------------

    [Fact]
    public async Task ClosedByAi_UsesTheRecordedAiClosure_NeverInactivityOrDocumentDelivery()
    {
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAi);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAi);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedForCustomerInactivity);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedForDocumentDelivery);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, null); // closed with no audit trail at all

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(2, overview.Kpis.ClosedByAiTickets);
        Assert.Equal(1, overview.Kpis.ClosedByAgentTickets);
    }

    [Fact]
    public async Task ClosedByAgent_CountsOnlyClosuresRecordedAsAPersonClosingTheTicket()
    {
        using var context = _db.CreateContext();
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAi);
        // An open ticket with a stale "Close" row from an earlier cycle is not a closed ticket.
        var reopened = _db.AddTicket(context, _db.CustomerServiceId);
        _db.AddClosureAudit(context, reopened, TicketClosureAuditActions.ClosedByAgent);

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(3, overview.Kpis.ClosedByAgentTickets);
        Assert.Equal(1, overview.Kpis.ClosedByAiTickets);
    }

    [Fact]
    public async Task ClosedByAiAndAgent_AReopenedTicketIsJudgedByItsLatestClosure_AndCountedOnce()
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Closed);
        // First closed by the AI, reopened, then closed by a person: two closing rows, one ticket.
        _db.AddClosureAudit(context, ticket, TicketClosureAuditActions.ClosedByAi);
        _db.AddClosureAudit(context, ticket, TicketClosureAuditActions.ClosedByAgent, actor: Guid.NewGuid());
        // A duplicate agent row (a retried request) must not double count either.
        _db.AddClosureAudit(context, ticket, TicketClosureAuditActions.ClosedByAgent, actor: Guid.NewGuid());

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(1, overview.Kpis.ClosedByAgentTickets);
        Assert.Equal(0, overview.Kpis.ClosedByAiTickets);
    }

    [Fact]
    public void ClosureAuditActions_AreTheValuesTheLifecycleAlreadyWrites()
    {
        // Pinned against TicketLifecycleAppService, which writes these literals
        // (see its tests): a rename there must be a deliberate decision here.
        Assert.Equal("Close", TicketClosureAuditActions.ClosedByAgent);
        Assert.Equal("AutoCloseByAi", TicketClosureAuditActions.ClosedByAi);
        Assert.Equal("AutoCloseCustomerInactivity", TicketClosureAuditActions.ClosedForCustomerInactivity);
        Assert.Equal("AutoCloseDocumentDelivery", TicketClosureAuditActions.ClosedForDocumentDelivery);
    }

    // ---------------------------------------------------------------
    // Filters
    // ---------------------------------------------------------------

    [Fact]
    public async Task ClosureKpis_FollowTheDateRange_ByTicketCreationDay()
    {
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent, createdAtUtc: Now.AddDays(-2));
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent, createdAtUtc: Now.AddDays(-45));
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAi, createdAtUtc: Now.AddDays(-45));

        var defaultRange = await OverviewAsync(Guid.NewGuid(), CrossDepartment); // last 30 days
        var wide = await OverviewAsync(Guid.NewGuid(), CrossDepartment,
            new DashboardOverviewRequestDto(DateFrom: DateOnly.FromDateTime(Now.AddDays(-60)), DateTo: DateOnly.FromDateTime(Now)));
        var old = await OverviewAsync(Guid.NewGuid(), CrossDepartment,
            new DashboardOverviewRequestDto(DateFrom: DateOnly.FromDateTime(Now.AddDays(-50)), DateTo: DateOnly.FromDateTime(Now.AddDays(-40))));

        Assert.Equal((1, 0), (defaultRange.Kpis.ClosedByAgentTickets, defaultRange.Kpis.ClosedByAiTickets));
        Assert.Equal(2, wide.Kpis.ClosedByAgentTickets);
        Assert.Equal(1, wide.Kpis.ClosedByAiTickets);
        Assert.Equal(1, old.Kpis.ClosedByAgentTickets);
        Assert.Equal(1, old.Kpis.ClosedByAiTickets);
    }

    [Fact]
    public async Task ClosureKpis_RespectTheDepartmentFilter_AndNeverWidenTheCallersScope()
    {
        ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedByAi);
        ClosedTicket(_db.RegistrationId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.RegistrationId, TicketClosureAuditActions.ClosedByAgent);

        var supervisorAll = await OverviewAsync(Guid.NewGuid(), CrossDepartment);
        var supervisorCollections = await OverviewAsync(Guid.NewGuid(), CrossDepartment, new DashboardOverviewRequestDto(DepartmentId: _db.CollectionsId));
        var head = await OverviewAsync(_db.CollectionsHeadId, [Roles.DepartmentHead]);
        var headAskingForRegistration = await OverviewAsync(
            _db.CollectionsHeadId, [Roles.DepartmentHead], new DashboardOverviewRequestDto(DepartmentId: _db.RegistrationId));

        Assert.Equal((3, 1), (supervisorAll.Kpis.ClosedByAgentTickets, supervisorAll.Kpis.ClosedByAiTickets));
        Assert.Equal((1, 1), (supervisorCollections.Kpis.ClosedByAgentTickets, supervisorCollections.Kpis.ClosedByAiTickets));
        Assert.Equal((1, 1), (head.Kpis.ClosedByAgentTickets, head.Kpis.ClosedByAiTickets));
        Assert.Equal((0, 0), (headAskingForRegistration.Kpis.ClosedByAgentTickets, headAskingForRegistration.Kpis.ClosedByAiTickets));
        Assert.Equal(0, headAskingForRegistration.Kpis.NotReviewedTickets);
    }

    // ---------------------------------------------------------------
    // Drill-down parity
    // ---------------------------------------------------------------

    [Fact]
    public async Task TicketQueueClosureCategoryFilter_ListsExactlyWhatEachCardCounted()
    {
        using (var context = _db.CreateContext())
        {
            var reviewedByAgent = ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedByAgent);
            var reviewedByAi = ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedByAi);
            ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedByAi);
            ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedForCustomerInactivity);
            ClosedTicket(_db.CollectionsId, null, createdAtUtc: Now.AddDays(-90)); // outside the default range
            _db.AddTicket(context, _db.CollectionsId, status: TicketStatus.InProgress);   // not closed
            ClosedTicket(_db.RegistrationId, TicketClosureAuditActions.ClosedByAgent); // invisible to the head
            using var write = _db.CreateContext();
            _db.AddReview(write, reviewedByAgent);
            _db.AddReview(write, reviewedByAi);
        }

        var overview = await OverviewAsync(_db.CollectionsHeadId, [Roles.DepartmentHead]);
        var from = overview.Filters.DateFrom;
        var to = overview.Filters.DateTo;
        var head = _db.CollectionsHeadId;
        string[] roles = [Roles.DepartmentHead];

        Assert.Equal((2, 2, 2, 1), (overview.Kpis.ReviewedTickets, overview.Kpis.NotReviewedTickets, overview.Kpis.ClosedByAiTickets, overview.Kpis.ClosedByAgentTickets));
        Assert.Equal(overview.Kpis.ReviewedTickets, await QueueCountAsync(head, roles, TicketClosureCategory.Reviewed, from, to));
        Assert.Equal(overview.Kpis.NotReviewedTickets, await QueueCountAsync(head, roles, TicketClosureCategory.NotReviewed, from, to));
        Assert.Equal(overview.Kpis.ClosedByAiTickets, await QueueCountAsync(head, roles, TicketClosureCategory.ClosedByAi, from, to));
        Assert.Equal(overview.Kpis.ClosedByAgentTickets, await QueueCountAsync(head, roles, TicketClosureCategory.ClosedByAgent, from, to));

        // The filter only ever yields Closed tickets, even if asked for another status.
        Assert.Equal(0, await QueueCountAsync(head, roles, TicketClosureCategory.ClosedByAi, from, to, ticketStatus: nameof(TicketStatus.InProgress)));
    }
}
