extern alias TigerCsWeb;

using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Web;

/// <summary>
/// The unified customer card's combined ticket history: both identities'
/// rows, no ticket id twice, active first then newest, bounded, and counts
/// that never double-count a duplicate.
/// </summary>
public sealed class CustomerHistoryMergeTests
{
    private static CustomerHistoryTicketDto Row(long id, string status, int daysAgo) =>
        new(id, $"TG-{id}", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo), status, 2, 2, 2, "P", "1506", "Verified");

    [Fact]
    public void Combine_DropsDuplicateTicketIds_OrdersActiveFirstThenNewest_AndBounds()
    {
        var crm = new CustomerHistoryDto("Verified", 5001, null, "Sami", 3, 1, 2,
            [Row(1, "Open", 5), Row(2, "Resolved", 1), Row(3, "Closed", 9)], CustomerKey: "crm:5001");
        var external = new CustomerHistoryDto("ExternalVerified", null, null, null, 3, 2, 1,
            [Row(1, "Open", 5), Row(4, "InProgress", 2), Row(5, "Closed", 3)], "Pact", "7001", "ext:Pact:7001");

        var merged = CustomerHistoryMerge.Combine(crm, external, limit: 3);

        Assert.Equal([4L, 1L, 2L], merged.Tickets.Select(t => t.TicketId).ToArray());
        Assert.Equal(5, merged.TotalTickets);
        Assert.Equal(2, merged.OpenTickets);
        Assert.Equal(3, merged.ClosedTickets);
        Assert.Equal(5001, merged.CrmBuyerCustomerId);
        Assert.Equal("Pact", merged.ExternalSource);
        Assert.Equal("7001", merged.ExternalCustomerId);
        Assert.Equal("crm:5001", merged.CustomerKey);
        Assert.Equal("Sami", merged.CustomerDisplayName);
    }

    [Fact]
    public void Combine_DisjointHistories_SumTheCounts()
    {
        var crm = new CustomerHistoryDto("Verified", 5001, null, null, 2, 1, 1, [Row(1, "Open", 1), Row(2, "Closed", 2)]);
        var external = new CustomerHistoryDto("ExternalVerified", null, null, "From PACT", 1, 0, 1, [Row(3, "Resolved", 3)], "Pact", "7001");

        var merged = CustomerHistoryMerge.Combine(crm, external, limit: 10);

        Assert.Equal(3, merged.Tickets.Count);
        Assert.Equal(3, merged.TotalTickets);
        Assert.Equal(1, merged.OpenTickets);
        Assert.Equal(2, merged.ClosedTickets);
        Assert.Equal("From PACT", merged.CustomerDisplayName);
    }
}
