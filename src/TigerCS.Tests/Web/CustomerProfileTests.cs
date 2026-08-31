using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// Customer Details/Profile page: four tabs (Overview/Contact Info/Units/
/// Previous Tickets), Overview selected by default, each tab's own fields,
/// Previous Tickets reusing the existing Customer History endpoint
/// unchanged, and no CRM/history logic duplicated in the page itself.
/// </summary>
public sealed class CustomerProfileTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string ViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "CustomerProfile.cshtml")));

    private static string ModelSource() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "CustomerProfile.cshtml.cs")));

    private static string TicketsApiClientSource() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Services", "Api", "TicketsApiClient.cs")));

    [Fact]
    public void View_IsTicketAnchored()
    {
        var html = ViewHtml();

        Assert.Contains("@page \"/Tickets/{ticketId:long}/Customer\"", html);
    }

    [Fact]
    public void View_HasAllFourTabs()
    {
        var html = ViewHtml();

        Assert.Contains("id=\"tab-overview\"", html);
        Assert.Contains(">Overview<", html);
        Assert.Contains("id=\"tab-contact\"", html);
        Assert.Contains(">Contact Info<", html);
        Assert.Contains("id=\"tab-units\"", html);
        Assert.Contains(">Units<", html);
        Assert.Contains("id=\"tab-history\"", html);
        Assert.Contains("id=\"panel-overview\"", html);
        Assert.Contains("id=\"panel-contact\"", html);
        Assert.Contains("id=\"panel-units\"", html);
        Assert.Contains("id=\"panel-history\"", html);
    }

    [Fact]
    public void View_OverviewTabIsSelectedByDefault()
    {
        var html = ViewHtml();

        var overviewStart = html.IndexOf("id=\"tab-overview\"", StringComparison.Ordinal);
        var overviewEnd = html.IndexOf("/>", overviewStart, StringComparison.Ordinal);
        Assert.Contains("checked", html[overviewStart..overviewEnd]);

        foreach (var otherTabId in new[] { "id=\"tab-contact\"", "id=\"tab-units\"", "id=\"tab-history\"" })
        {
            var start = html.IndexOf(otherTabId, StringComparison.Ordinal);
            var end = html.IndexOf("/>", start, StringComparison.Ordinal);
            Assert.DoesNotContain("checked", html[start..end]);
        }
    }

    [Fact]
    public void View_OverviewTabShowsTheCompactSummaryFields()
    {
        var html = ViewHtml();
        var panelStart = html.IndexOf("id=\"panel-overview\"", StringComparison.Ordinal);
        var panelEnd = html.IndexOf("id=\"panel-contact\"", StringComparison.Ordinal);
        var panel = html[panelStart..panelEnd];

        Assert.Contains("<dt>Customer Name</dt>", panel);
        Assert.Contains("<dt>CRM Customer ID</dt>", panel);
        Assert.Contains("<dt>Phone</dt>", panel);
        Assert.Contains("<dt>Email</dt>", panel);
        Assert.Contains("<dt>Total Units</dt>", panel);
        Assert.Contains("<dt>Total Previous Tickets</dt>", panel);
    }

    [Fact]
    public void View_ContactInfoTabShowsFullNamesAndCrmId()
    {
        var html = ViewHtml();
        var panelStart = html.IndexOf("id=\"panel-contact\"", StringComparison.Ordinal);
        var panelEnd = html.IndexOf("id=\"panel-units\"", StringComparison.Ordinal);
        var panel = html[panelStart..panelEnd];

        Assert.Contains("<dt>Full Name English</dt>", panel);
        Assert.Contains("<dt>Full Name Arabic</dt>", panel);
        Assert.Contains("<dt>Mobile Number</dt>", panel);
        Assert.Contains("<dt>Email</dt>", panel);
        Assert.Contains("<dt>CRM Customer ID</dt>", panel);
    }

    [Fact]
    public void View_UnitsTabShowsAllEligibleUnitsInATable_NotJustTheCurrentTicketsUnit()
    {
        var html = ViewHtml();
        var panelStart = html.IndexOf("id=\"panel-units\"", StringComparison.Ordinal);
        var panelEnd = html.IndexOf("id=\"panel-history\"", StringComparison.Ordinal);
        var panel = html[panelStart..panelEnd];

        Assert.Contains("<th>Project</th>", panel);
        Assert.Contains("<th>Unit Number</th>", panel);
        Assert.Contains("<th>Lead Status</th>", panel);
        Assert.Contains("<th>Unit Type</th>", panel);
        Assert.Contains("<th>Floor</th>", panel);
        Assert.Contains("@foreach (var unit in profile.Units)", panel);
    }

    [Fact]
    public void View_PreviousTicketsTabTitleIncludesTheCount()
    {
        var html = ViewHtml();

        Assert.Contains("Previous Tickets@(history is not null ? $\" ({history.TotalTickets})\" : \"\")", html);
    }

    [Fact]
    public void View_PreviousTicketsTabShowsTheRequiredColumnsAndAnOpenLink()
    {
        var html = ViewHtml();
        var panelStart = html.IndexOf("id=\"panel-history\"", StringComparison.Ordinal);
        var panel = html[panelStart..];

        Assert.Contains("<th>Ticket</th>", panel);
        Assert.Contains("<th>Category</th>", panel);
        Assert.Contains("<th>Department</th>", panel);
        Assert.Contains("<th>Status</th>", panel);
        Assert.Contains("<th>Priority</th>", panel);
        Assert.Contains("<th>Created</th>", panel);
        Assert.Contains("<th>Closed</th>", panel);
        Assert.Contains("href=\"/Tickets/@row.TicketId\"", panel);
    }

    [Fact]
    public void Model_ReusesTheExistingCustomerHistoryAndCustomerProfileEndpoints_NoDuplicatedLogic()
    {
        var source = ModelSource();

        Assert.Contains("GetCustomerProfileAsync", source);
        Assert.Contains("GetCustomerHistoryAsync", source);
        // Never calls CRM directly — both calls go through TicketsApiClient,
        // which in turn talks to the existing, already-tested backend
        // endpoints only.
        Assert.DoesNotContain("CrmBuyerLookupApiClient", source);
        Assert.DoesNotContain("CrmApiClient", source);
    }

    [Fact]
    public void ApiClient_CustomerProfileCall_IsTicketAnchored_NeverTouchesCrmEndpointsDirectly()
    {
        var source = TicketsApiClientSource();

        Assert.Contains("api/tickets/{ticketId}/customer-profile", source);
        Assert.DoesNotContain("api/crm", source);
    }
}
