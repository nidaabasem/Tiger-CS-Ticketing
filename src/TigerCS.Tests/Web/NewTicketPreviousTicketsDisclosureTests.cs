using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// New Ticket Step 3's "Previous Tickets" preview (UI/UX-only revision):
/// collapsed by default behind a <c>&lt;details&gt;</c> disclosure — the same
/// no-JS-required pattern Ticket Details already uses for its Assign/
/// Transfer/etc. actions — so customer/unit verification stays the page's
/// primary, uncluttered content and the full previous-tickets list is only
/// shown once the agent explicitly opens it.
/// </summary>
public sealed class NewTicketPreviousTicketsDisclosureTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string NewTicketViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

    [Fact]
    public void View_PreviousTicketsPreviewIsACollapsedDisclosure_NotShownInlineByDefault()
    {
        var html = NewTicketViewHtml();

        var summaryIndex = html.IndexOf("Previous Tickets@(Model.PreviousTickets", StringComparison.Ordinal);
        Assert.True(summaryIndex > 0, "Expected a 'Previous Tickets' summary label in the view.");

        var detailsStart = html.LastIndexOf("<details", summaryIndex, StringComparison.Ordinal);
        Assert.True(detailsStart >= 0, "The Previous Tickets preview must be wrapped in a <details> disclosure.");

        var detailsOpenTagEnd = html.IndexOf('>', detailsStart);
        var detailsOpenTag = html[detailsStart..detailsOpenTagEnd];

        // No "open" attribute — collapsed by default, matching the requirement
        // that Previous Tickets never displays inline by default in New Ticket.
        Assert.DoesNotContain(" open", detailsOpenTag);
        Assert.Contains("disclosure", detailsOpenTag);
    }

    [Fact]
    public void View_ShowsThePreviousTicketsCountInTheSummaryLabel()
    {
        var html = NewTicketViewHtml();

        Assert.Contains(
            "Previous Tickets@(Model.PreviousTickets is not null ? $\" ({Model.PreviousTickets.TotalTickets})\" : \"\")",
            html);
    }

    [Fact]
    public void View_CustomerUnitVerificationCardComesBeforeThePreviousTicketsDisclosure()
    {
        // "Keep customer/unit verification information as the primary
        // content" — the verification customer-card must render first.
        var html = NewTicketViewHtml();

        var verificationCardIndex = html.IndexOf("Customer (CRM verified)", StringComparison.Ordinal);
        var disclosureIndex = html.IndexOf("Previous Tickets@(Model.PreviousTickets", StringComparison.Ordinal);

        Assert.True(verificationCardIndex > 0);
        Assert.True(disclosureIndex > verificationCardIndex);
    }
}
