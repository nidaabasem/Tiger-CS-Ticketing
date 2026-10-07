using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.GenesysIntegration.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.GenesysIntegration.Persistence;

/// <summary>
/// The reply/timeout race against the REAL EF model and a real relational
/// engine: the timer column is a concurrency token, so a customer reply that
/// commits while the timeout job is closing makes the job's write fail instead
/// of silently overwriting the reply. The fakes used elsewhere cannot prove
/// this — it is a property of the mapping.
/// </summary>
public sealed class InteractionTimerConcurrencyTests : IDisposable
{
    private static readonly Guid Reporter = Guid.NewGuid();
    private readonly DashboardSqliteFixture _db = new();

    private (long TicketId, long InteractionId) Seed(DateTime waitingSince)
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId);
        context.SaveChanges();

        var interaction = TicketInteraction.CreateFromGenesys(
            ticket.TicketId, channelId: 3, "+971500000001", $"conv-{Guid.NewGuid():N}", null, null, null, null, null, null, null,
            waitingSince, isOriginatingInteraction: true);
        interaction.BeginAwaitingCustomerReply(waitingSince, _db.CsAgentId);
        context.TicketInteractions.Add(interaction);
        context.SaveChanges();
        return (ticket.TicketId, interaction.TicketInteractionId);
    }

    [Fact]
    public async Task ReplyThatClearsTheTimerFirst_MakesTheClosersWriteFail()
    {
        var (_, interactionId) = Seed(DashboardSqliteFixture.Now.AddMinutes(-10));

        // The job loads the interaction and decides it is due…
        await using var jobContext = _db.CreateContext();
        var jobInteraction = await new GenesysConversationRepository(jobContext).GetByIdAsync(interactionId);
        Assert.True(jobInteraction!.IsInactivityTimeoutDue(DashboardSqliteFixture.Now, TimeSpan.FromMinutes(5)));

        // …the customer's reply commits meanwhile (a separate request, separate context)…
        await using (var replyContext = _db.CreateContext())
        {
            var replied = await new GenesysConversationRepository(replyContext).GetByIdAsync(interactionId);
            Assert.True(replied!.CancelAwaitingCustomerReply());
            await replyContext.SaveChangesAsync();
        }

        // …so the job's own write (record the closure) is rejected by the database layer
        // and the real unit of work reports it as the lost race the closer handles.
        jobInteraction.RecordInactivityClosure(DashboardSqliteFixture.Now);
        await Assert.ThrowsAsync<TicketConcurrentlyModifiedException>(
            () => new TicketingUnitOfWork(jobContext).SaveChangesAsync());

        await using var verify = _db.CreateContext();
        var stored = await verify.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketInteractionId == interactionId);
        Assert.Null(stored.InactivityClosedAtUtc);
        Assert.Null(stored.AwaitingCustomerReplySinceUtc);
    }

    [Fact]
    public async Task DueQuery_ReturnsOnlyTimersOlderThanTheCutoff_OldestFirst()
    {
        var now = DashboardSqliteFixture.Now;
        var (_, older) = Seed(now.AddMinutes(-30));
        var (_, old) = Seed(now.AddMinutes(-20));
        Seed(now.AddMinutes(-2)); // still inside the 5-minute window

        await using var context = _db.CreateContext();
        var due = await new GenesysConversationRepository(context)
            .ListAwaitingReplyOlderThanAsync(now.AddMinutes(-5), batchSize: 10);

        Assert.Equal([older, old], due);
    }

    [Fact]
    public async Task ClearedTimer_IsNoLongerACandidate()
    {
        var now = DashboardSqliteFixture.Now;
        var (_, interactionId) = Seed(now.AddMinutes(-30));

        await using (var context = _db.CreateContext())
        {
            var interaction = await new GenesysConversationRepository(context).GetByIdAsync(interactionId);
            interaction!.CancelAwaitingCustomerReply();
            await context.SaveChangesAsync();
        }

        await using var query = _db.CreateContext();
        Assert.Empty(await new GenesysConversationRepository(query).ListAwaitingReplyOlderThanAsync(now.AddMinutes(-5), 10));
    }

    public void Dispose() => _db.Dispose();
}
