using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// The redesigned New Ticket Step 1's existing-ticket awareness (successor
/// of the old Step 3 "Previous Tickets" disclosure): each matched customer's
/// card carries compact counts and a few recent tickets — ticket number,
/// one-line summary, status, unit, and an Open/View action — never full
/// descriptions and never raw identifiers, with "View all tickets" handing
/// off to the Customer Workspace for anything more.
/// </summary>
public sealed class NewTicketCustomerAwarenessTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string NewTicketViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

    private static string ExistingTicketsBlock()
    {
        var html = NewTicketViewHtml();
        var start = html.IndexOf("existing-tickets", StringComparison.Ordinal);
        Assert.True(start > 0, "Expected the Step 1 existing-tickets awareness block.");
        var end = html.IndexOf("candidate-card__actions", start, StringComparison.Ordinal);
        return html[start..end];
    }

    [Fact]
    public void View_CustomerCard_ShowsCountsAndAnExistingTicketsNotice()
    {
        var html = NewTicketViewHtml();

        Assert.Contains("Previous Ticket", html);
        Assert.Contains("Open Ticket", html);
        Assert.Contains("Existing tickets", html);
    }

    [Fact]
    public void View_ExistingTicketsRows_ShowOneLineSummaries_NeverFullDescriptions()
    {
        var block = ExistingTicketsBlock();

        Assert.Contains("row.TicketNumber", block);
        Assert.Contains("cell-truncate", block);
        Assert.Contains("row.RequestSummary", block);
        Assert.Contains("TicketStatusLabel", block);
    }

    [Fact]
    public void View_ExistingTicketsRows_OfferOpenOrView_AndReopenOnlyPerPolicy()
    {
        var block = ExistingTicketsBlock();

        Assert.Contains("@(isFinished ? \"View\" : \"Open\")", block);
        Assert.Contains("row.IsReopenEligible && viewerCanReopen", block);
        Assert.Contains("?reopen=1", block);
    }

    [Fact]
    public void View_HandsOffToTheCustomerWorkspace_ForTheFullHistory()
    {
        var html = NewTicketViewHtml();

        Assert.Contains("View all tickets", html);
        Assert.Contains("/Customers?phoneNumber=", html);
    }

    [Fact]
    public void View_TheAgentCanAlwaysProceed_UseThisCustomerAndManualEntryBothExist()
    {
        var html = NewTicketViewHtml();

        Assert.Contains("Use this customer", html);
        Assert.Contains("Continue with Manual Entry", html);
        Assert.Contains("Customer not found", html);
    }

    [Fact]
    public void View_ChangingTheCustomer_DropsTheUnitSelection_ButPlainBackKeepsIt()
    {
        // The navigation-link builder carries the full wizard state; a link
        // that overrides the customer never carries the previous customer's
        // unit selection with it (the packed values belong to the customer
        // that produced them), while ordinary Back/Continue links keep it.
        var html = NewTicketViewHtml();

        Assert.Contains("if (keepUnit && customerOverride is null)", html);
        Assert.Contains("customerOverride: candidate.Key", html);
    }

    [Fact]
    public void View_NeverExposesRawIdentifiersAsPrimaryInformation()
    {
        // Technical ids travel only inside packed hidden values — never as
        // labeled, visible fields.
        var html = NewTicketViewHtml();

        Assert.DoesNotContain("Customer ID", html);
        Assert.DoesNotContain("Lead ID", html);
        Assert.DoesNotContain("Project ID", html);
        Assert.DoesNotContain("External ID", html);
        Assert.DoesNotContain(">@Model.CrmBuyerCustomerId<", html);
        Assert.DoesNotContain(">@Model.ExternalCustomerId<", html);
    }
}
