using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Repositories;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.SlaAndEscalation.Integration;

/// <summary>
/// The pause tables and the safety sweep as real SQL (SQLite in-memory over the
/// real EF mapping): the open-pause filter on the sweep, the one-open-pause
/// index, and the snapshot columns on the period.
/// </summary>
public sealed class SlaPauseSqliteTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;

    private readonly DashboardSqliteFixture _db = new();

    public void Dispose() => _db.Dispose();

    private Ticket AddPendingTicket(Infrastructure.Persistence.TigerCsDbContext context, DateTime dueAtUtc)
    {
        var ticket = _db.AddTicket(
            context, _db.CustomerServiceId, status: TicketStatus.PendingCustomer, resolutionDueAtUtc: dueAtUtc);
        return ticket;
    }

    private static TicketSlaPausePeriod OpenPause(Ticket ticket, TicketSlaInstance instance) =>
        new(ticket.TicketId, instance.TicketSlaInstanceId, SlaPauseReason.PendingCustomer, Now.AddHours(-2), instance.ResolutionDueAtUtc);

    [Fact]
    public async Task TheSweep_SkipsAResolutionClockWithAnOpenPause_ButStillExaminesFirstResponse()
    {
        using var context = _db.CreateContext();
        var paused = AddPendingTicket(context, Now.AddHours(-1));
        var running = AddPendingTicket(context, Now.AddHours(-1));
        var pausedInstance = await context.TicketSlaInstances.SingleAsync(i => i.TicketId == paused.TicketId);
        context.TicketSlaPausePeriods.Add(OpenPause(paused, pausedInstance));
        await context.SaveChangesAsync();

        var repository = new TicketSlaInstanceRepository(context);
        var resolution = await repository.ListTicketIdsWithDeadlineDueAsync(SlaDeadlineType.Resolution, Now, 50);
        var firstResponse = await repository.ListTicketIdsWithDeadlineDueAsync(SlaDeadlineType.FirstResponse, Now, 50);

        Assert.Equal([running.TicketId], resolution);
        Assert.Contains(paused.TicketId, firstResponse);
        Assert.Contains(running.TicketId, firstResponse);
    }

    [Fact]
    public async Task OnceThePauseIsClosed_TheTicketIsASweepCandidateAgain()
    {
        using var context = _db.CreateContext();
        var ticket = AddPendingTicket(context, Now.AddHours(-1));
        var instance = await context.TicketSlaInstances.SingleAsync(i => i.TicketId == ticket.TicketId);
        var pause = OpenPause(ticket, instance);
        context.TicketSlaPausePeriods.Add(pause);
        await context.SaveChangesAsync();

        pause.Close(Now.AddHours(-1), instance.ResolutionDueAtUtc, endedByResolution: false);
        await context.SaveChangesAsync();

        var repository = new TicketSlaInstanceRepository(context);
        Assert.Contains(ticket.TicketId, await repository.ListTicketIdsWithDeadlineDueAsync(SlaDeadlineType.Resolution, Now, 50));
    }

    [Fact]
    public async Task TheDatabase_RefusesASecondOpenPauseForTheSameTicket()
    {
        using var context = _db.CreateContext();
        var ticket = AddPendingTicket(context, Now.AddHours(3));
        var instance = await context.TicketSlaInstances.SingleAsync(i => i.TicketId == ticket.TicketId);
        context.TicketSlaPausePeriods.Add(OpenPause(ticket, instance));
        await context.SaveChangesAsync();

        context.TicketSlaPausePeriods.Add(OpenPause(ticket, instance));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task PausesRoundTripThroughTheRepository_OpenOneAndHistory()
    {
        using var context = _db.CreateContext();
        var ticket = AddPendingTicket(context, Now.AddHours(3));
        var instance = await context.TicketSlaInstances.SingleAsync(i => i.TicketId == ticket.TicketId);
        var repository = new TicketSlaPausePeriodRepository(context);

        var first = OpenPause(ticket, instance);
        await repository.AddAsync(first);
        await context.SaveChangesAsync();
        Assert.Equal(first.TicketSlaPausePeriodId, (await repository.GetOpenAsync(ticket.TicketId))!.TicketSlaPausePeriodId);

        first.Close(Now.AddHours(-1), Now.AddHours(4), endedByResolution: false);
        var second = new TicketSlaPausePeriod(ticket.TicketId, instance.TicketSlaInstanceId, SlaPauseReason.PendingCustomer, Now, Now.AddHours(4));
        await repository.AddAsync(second);
        await context.SaveChangesAsync();

        var history = await repository.ListByInstanceIdAsync(instance.TicketSlaInstanceId);
        Assert.Equal(2, history.Count);
        Assert.False(history[0].IsOpen);
        Assert.Equal(Now.AddHours(4), history[0].ResolutionDueAfterAtUtc);
        Assert.Same(history[1], await repository.GetOpenAsync(ticket.TicketId));
    }

    [Fact]
    public async Task ThePolicySnapshot_RoundTripsOnThePeriod()
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.InProgress);
        var instance = TicketSlaInstance.OpenInitialPeriod(
            ticket.TicketId, (byte)PriorityLevel.Medium, Now, Now.AddHours(4), Now.AddDays(1),
            new AppliedSlaPolicy(
                SlaClockBasis.TwentyFourSeven, RequestTypeSlaPolicyId: null, PausesOnPendingCustomerOverride: false,
                RequestTypeSlaNote: "Request-type SLA not applied: range interpretation undecided.",
                AppliedFirstResponseTargetMinutes: 240, AppliedResolutionTargetMinutes: 1440));
        context.TicketSlaInstances.Add(instance);
        await context.SaveChangesAsync();

        using var fresh = _db.CreateContext();
        var loaded = await new TicketSlaInstanceRepository(fresh).GetCurrentAsync(ticket.TicketId);

        Assert.NotNull(loaded);
        Assert.Equal(SlaClockBasis.TwentyFourSeven, loaded.ResolutionClockBasis);
        Assert.False(loaded.PausesOnPendingCustomerOverride);
        Assert.Equal("Request-type SLA not applied: range interpretation undecided.", loaded.RequestTypeSlaNote);
        Assert.Equal(240, loaded.AppliedFirstResponseTargetMinutes);
        Assert.Equal(1440, loaded.AppliedResolutionTargetMinutes);
    }
}
