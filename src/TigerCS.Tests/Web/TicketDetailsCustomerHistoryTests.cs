using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details' Customer History section (Customer -> Previous Ticket
/// History, this increment): renders the customer's other tickets, labels
/// unverified (phone-snapshot) history clearly, links each row to its own
/// Ticket Details page, and never calls CRM live.
/// </summary>
public sealed class TicketDetailsCustomerHistoryTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string TicketDetailsViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml")));

    private static string TicketDetailsModelSource() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml.cs")));

    private static string TicketsApiClientSource() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Services", "Api", "TicketsApiClient.cs")));

    [Fact]
    public void View_RendersACustomerHistorySection()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("Customer History", html);
        Assert.Contains("history.TotalTickets", html);
        Assert.Contains("history.OpenTickets", html);
        Assert.Contains("history.ClosedTickets", html);
    }

    [Fact]
    public void View_LabelsUnverifiedHistoryClearly()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("Unverified Customer History", html);
        Assert.Contains("history.VerificationType == \"Unverified\"", html);
    }

    [Fact]
    public void View_HistoryRowsLinkToTheirOwnTicketDetailsPage()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("/Tickets/@row.TicketId", html);
    }

    [Fact]
    public void Model_LoadsCustomerHistory_ViaTicketsApiClient()
    {
        var source = TicketDetailsModelSource();

        Assert.Contains("GetCustomerHistoryAsync", source);
        Assert.Contains("CustomerHistory", source);
    }

    [Fact]
    public void Model_DoesNotDependOnAnyCrmApiClient_CustomerHistoryNeverCallsCrmLive()
    {
        var source = TicketDetailsModelSource();

        Assert.DoesNotContain("CrmBuyerLookupApiClient", source);
        Assert.DoesNotContain("CrmApiClient", source);
        Assert.DoesNotContain("GetBuyerByPhone", source);
    }

    [Fact]
    public void ApiClient_CustomerHistoryCall_NeverTouchesCrmEndpoints()
    {
        var source = TicketsApiClientSource();

        Assert.Contains("api/tickets/{ticketId}/customer-history", source);
        Assert.DoesNotContain("api/crm", source);
    }
}
