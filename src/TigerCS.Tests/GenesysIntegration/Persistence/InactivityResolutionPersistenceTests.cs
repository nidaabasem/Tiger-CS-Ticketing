using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.GenesysIntegration.Persistence;

/// <summary>
/// The inactivity-closure marker on the REAL EF model and a relational engine —
/// the mapping the <c>AddResolutionClosedForCustomerInactivity</c> migration
/// describes. Fakes cannot show that the column round-trips, defaults to false
/// for ordinary resolutions, or survives archiving on reopen.
/// </summary>
public sealed class InactivityResolutionPersistenceTests : IDisposable
{
    private readonly DashboardSqliteFixture _db = new();

    [Fact]
    public async Task Flag_RoundTrips_DefaultsFalse_AndSurvivesArchive()
    {
        long inactivityTicket, manualTicket;
        await using (var context = _db.CreateContext())
        {
            var a = _db.AddTicket(context, _db.CustomerServiceId);
            var b = _db.AddTicket(context, _db.CustomerServiceId);
            context.SaveChanges();
            inactivityTicket = a.TicketId;
            manualTicket = b.TicketId;

            context.TicketResolutions.Add(TicketResolution.ForCustomerInactivity(
                a.TicketId, "Automatically closed — customer did not respond for more than 5 minutes.", _db.CsAgentId, DashboardSqliteFixture.Now));
            context.TicketResolutions.Add(new TicketResolution(
                b.TicketId, ResolutionOutcome.Cancelled, "Customer withdrew.", reasonCode: null, duplicateOfTicketId: null,
                _db.CsAgentId, DashboardSqliteFixture.Now));
            await context.SaveChangesAsync();
        }

        await using (var read = _db.CreateContext())
        {
            var repository = new TicketResolutionRepository(read);
            var current = await repository.ListCurrentByTicketIdsAsync([inactivityTicket, manualTicket]);
            Assert.True(current[inactivityTicket].ClosedForCustomerInactivity);
            Assert.Equal(ResolutionOutcome.Cancelled, current[inactivityTicket].ResolutionOutcome);
            Assert.False(current[manualTicket].ClosedForCustomerInactivity);
        }

        // Reopen archives the row; the marker stays on the archived history.
        await using (var archive = _db.CreateContext())
        {
            var resolution = await new TicketResolutionRepository(archive).GetCurrentAsync(inactivityTicket);
            resolution!.Archive();
            await archive.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        var stored = await verify.TicketResolutions.AsNoTracking().SingleAsync(r => r.TicketId == inactivityTicket);
        Assert.False(stored.IsCurrent);
        Assert.True(stored.ClosedForCustomerInactivity);
        Assert.Null(await new TicketResolutionRepository(verify).GetCurrentAsync(inactivityTicket));
    }

    public void Dispose() => _db.Dispose();
}
