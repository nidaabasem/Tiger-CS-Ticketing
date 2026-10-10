using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Crm;

/// <summary>The one Sold / Contract / not-cancelled rule, shared by the CRM owner feed and the phone lookup. The status NAME decides; numbers are a fallback for rows without a name.</summary>
public sealed class CrmSaleEligibilityTests
{
    private static readonly int[] Fallback = [4, 8];

    [Theory]
    [InlineData("Sold", 8, true)]
    [InlineData("Sold", 999, true)]          // the name decides, whatever the number
    [InlineData("Contract", 4, true)]
    [InlineData(" contract ", 1, true)]
    [InlineData("Cancelled", 8, false)]      // a cancelled lead is refused by name even if its number is on the fallback list
    [InlineData("Canceled", 4, false)]
    [InlineData("Hot", 8, false)]
    [InlineData("Reserved", 4, false)]
    [InlineData(null, 8, true)]
    [InlineData("", 4, true)]
    [InlineData(null, 9, false)]
    [InlineData(null, 2, false)]
    public void TheStatusNameDecides_TheNumberOnlyWhenThereIsNoName(string? name, int number, bool expected) =>
        Assert.Equal(expected, CrmSaleEligibility.IsEligible(number, name, Fallback));

    [Fact]
    public void TheOwnerFeed_UsesTheSameRule()
    {
        var refresh = new CollectionsCrmOwnersRefreshService(new CollectionsCrmOwnersOptions { Enabled = true }, new FakeCrmUnitOwnersGateway(), new FakeCrmOwnerStore(), NullLogger<CollectionsCrmOwnersRefreshService>.Instance);
        Assert.NotNull(refresh.ToRow(FakeCrmUnitOwnersGateway.Owner(1, "TP140", "101", leadStatus: 99, statusName: "Sold")));          // name Sold, unknown number: eligible
        Assert.Null(refresh.ToRow(FakeCrmUnitOwnersGateway.Owner(2, "TP140", "102", leadStatus: 8, statusName: "Hot")));              // number on the list, wrong name: not a sale
        Assert.Null(refresh.ToRow(FakeCrmUnitOwnersGateway.Owner(3, "TP140", "103", leadStatus: 8, statusName: "Cancelled")));
        Assert.NotNull(refresh.ToRow(FakeCrmUnitOwnersGateway.Owner(4, "TP140", "104", leadStatus: 8, statusName: null)));            // no name: fallback list
    }
}
