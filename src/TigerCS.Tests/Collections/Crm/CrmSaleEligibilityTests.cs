using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Crm;

/// <summary>The one Sold / Contract / not-cancelled rule, shared by the CRM owner feed and the phone lookup. The status NAME decides; numbers are a fallback for rows without a name.</summary>
public sealed class CrmSaleEligibilityTests
{
    private static readonly int[] Fallback = [8, 9];   // CRM: Sold = 8, Contract = 9, Approved = 4, Cancelled = 6

    [Theory]
    [InlineData("Sold", 8, true)]
    [InlineData("Sold", 999, true)]          // the name decides, whatever the number
    [InlineData("Contract", 9, true)]
    [InlineData(" contract ", 1, true)]
    [InlineData("Approved", 4, false)]       // Approved (4) is not a sale
    [InlineData("Approved", 8, false)]
    [InlineData("Cancelled", 8, false)]      // a cancelled lead is refused by name even if its number is on the fallback list
    [InlineData("Canceled", 9, false)]
    [InlineData("Hot", 8, false)]
    [InlineData("Reserved", 4, false)]
    [InlineData(null, 8, true)]
    [InlineData(null, 9, true)]
    [InlineData("", 9, true)]
    [InlineData(null, 4, false)]             // no name: Approved is not eligible
    [InlineData(null, 6, false)]             // no name: Cancelled is not eligible
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

public sealed class CrmEligibleStatusDefaultsTests
{
    [Fact]
    public void TheDefaultFallbackList_IsSoldAndContract_NeverApprovedOrCancelled()
    {
        Assert.Equal([8, 9], new CollectionsCrmOwnersOptions().EligibleLeadStatuses);
        Assert.DoesNotContain(4, new CollectionsCrmOwnersOptions().EligibleLeadStatuses);    // Approved
        Assert.DoesNotContain(6, new CollectionsCrmOwnersOptions().EligibleLeadStatuses);    // Cancelled
    }

    [Fact]
    public void TheShippedConfiguration_UsesTheSameList()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = System.IO.Path.Combine(dir.FullName, "src", "TigerCS.Api", "appsettings.json");
            if (!File.Exists(path)) continue;
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var statuses = json.RootElement.GetProperty("CollectionsSource").GetProperty("CrmOwners").GetProperty("EligibleLeadStatuses").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal([8, 9], statuses);
            return;
        }
        throw new FileNotFoundException("src/TigerCS.Api/appsettings.json");
    }
}
