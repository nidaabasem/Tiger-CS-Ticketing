using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details' Customer History presentation (Customer -> Previous
/// Ticket History, this increment; later revised to a dedicated "Previous
/// Tickets" tab so it never visually competes with the primary ticket
/// information): renders the customer's other tickets in their own tab,
/// defaults to the Details tab, labels unverified (phone-snapshot) history
/// clearly, links each row to its own Ticket Details page, and never calls
/// CRM live. This is a presentation-only concern — the underlying
/// CustomerHistory API/business logic is unchanged and covered separately
/// (CustomerHistoryAppServiceTests).
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

    private static string LayoutHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "Shared", "_Layout.cshtml")));

    private static string SiteCss() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "wwwroot", "css", "site.css")));

    // ---- Blank-Details regression (route /Tickets/{id} rendered an empty
    // Details tab): the panel must contain every pre-existing Ticket Details
    // section, the page-level panels must not depend on the generic
    // .tab-panel class an older cached stylesheet would hide, and the
    // stylesheet link must be versioned so new markup can never pair with a
    // stale cached site.css again. ----

    [Fact]
    public void View_DetailsPanelContainsAllExistingTicketDetailsSections()
    {
        var html = TicketDetailsViewHtml();

        var detailsStart = html.IndexOf("id=\"panel-details\"", StringComparison.Ordinal);
        var historyStart = html.IndexOf("id=\"panel-history\"", StringComparison.Ordinal);
        Assert.True(detailsStart > 0);
        Assert.True(historyStart > detailsStart);
        var detailsPanel = html[detailsStart..historyStart];

        // Every section that belonged to Ticket Details before the tab
        // conversion must render inside the Details panel.
        Assert.Contains("detail-layout", detailsPanel);
        Assert.Contains("id=\"tab-activity\"", detailsPanel);     // Activity/Audit History tabs + note composer
        Assert.Contains("facts-panel", detailsPanel);
        Assert.Contains("Ticket Information", detailsPanel);      // ticket metadata
        Assert.Contains("Verification &amp; Unit", detailsPanel); // customer/verification + unit information
        Assert.Contains("SLA Information", detailsPanel);
    }

    [Fact]
    public void View_PageLevelPanels_DoNotUseTheGenericTabPanelClass_StaleCssCanNeverBlankThem()
    {
        // The regression's mechanism: an older cached site.css knows the
        // generic .tab-panel class (display:none) but not the new page-level
        // reveal rules, hiding the whole page. Page-level panels therefore
        // carry their own .tab-panel--page class — unknown to any older
        // stylesheet, so the worst-case degradation is visible stacked
        // content, never a blank page.
        var html = TicketDetailsViewHtml();

        Assert.Contains("<div class=\"tab-panel--page\" id=\"panel-details\">", html);
        Assert.Contains("<div class=\"tab-panel--page\" id=\"panel-history\">", html);
        Assert.DoesNotContain("<div class=\"tab-panel\" id=\"panel-details\">", html);
        Assert.DoesNotContain("<div class=\"tab-panel\" id=\"panel-history\">", html);
    }

    [Fact]
    public void Css_PageLevelPanelsAreHiddenByDefault_AndRevealedById()
    {
        var css = SiteCss();

        Assert.Contains(".tab-panel--page { display: none;", css);
        Assert.Contains("#tab-details:checked ~ .tabs-body #panel-details", css);
        Assert.Contains("#tab-history:checked ~ .tabs-body #panel-history", css);
    }

    [Fact]
    public void Layout_VersionsTheStylesheetLink_SoMarkupAndCssAlwaysDeployInLockstep()
    {
        var layout = LayoutHtml();

        var linkStart = layout.IndexOf("~/css/site.css", StringComparison.Ordinal);
        Assert.True(linkStart > 0);
        var lineEnd = layout.IndexOf("/>", linkStart, StringComparison.Ordinal);
        Assert.Contains("asp-append-version=\"true\"", layout[linkStart..lineEnd]);
    }

    [Fact]
    public void View_ViewCustomerProfileLink_GatedOnCrmBuyerCustomerId()
    {
        var html = TicketDetailsViewHtml();

        var guardIndex = html.IndexOf("@if (t.CrmBuyerCustomerId is not null)", StringComparison.Ordinal);
        var linkIndex = html.IndexOf("View Customer Profile", StringComparison.Ordinal);
        Assert.True(guardIndex > 0, "The View Customer Profile link must be guarded by t.CrmBuyerCustomerId is not null.");
        Assert.True(linkIndex > guardIndex, "The View Customer Profile link must render inside its CrmBuyerCustomerId guard.");

        // And inside the Verification & Unit section (before the SLA section).
        var verificationSectionIndex = html.IndexOf("Verification &amp; Unit", StringComparison.Ordinal);
        var slaSectionIndex = html.IndexOf("SLA Information", StringComparison.Ordinal);
        Assert.True(linkIndex > verificationSectionIndex && linkIndex < slaSectionIndex);
    }

    [Fact]
    public void View_RendersAPreviousTicketsSection()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("Previous Tickets", html);
        Assert.Contains("history.TotalTickets", html);
        Assert.Contains("history.OpenTickets", html);
        Assert.Contains("history.ClosedTickets", html);
    }

    [Fact]
    public void View_HasSeparateDetailsAndPreviousTicketsTabs()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("id=\"tab-details\"", html);
        Assert.Contains("for=\"tab-details\"", html);
        Assert.Contains("id=\"tab-history\"", html);
        Assert.Contains("for=\"tab-history\"", html);
        Assert.Contains("id=\"panel-details\"", html);
        Assert.Contains("id=\"panel-history\"", html);
    }

    [Fact]
    public void View_DetailsTabIsSelectedByDefault_NotPreviousTickets()
    {
        var html = TicketDetailsViewHtml();

        var detailsInputStart = html.IndexOf("id=\"tab-details\"", StringComparison.Ordinal);
        var detailsInputEnd = html.IndexOf("/>", detailsInputStart, StringComparison.Ordinal);
        var detailsInputTag = html[detailsInputStart..detailsInputEnd];

        var historyInputStart = html.IndexOf("id=\"tab-history\"", StringComparison.Ordinal);
        var historyInputEnd = html.IndexOf("/>", historyInputStart, StringComparison.Ordinal);
        var historyInputTag = html[historyInputStart..historyInputEnd];

        Assert.Contains("checked", detailsInputTag);
        Assert.DoesNotContain("checked", historyInputTag);
    }

    [Fact]
    public void View_ShowsThePreviousTicketsCountInTheTabLabel()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("Previous Tickets@(Model.CustomerHistory is not null ? $\" ({Model.CustomerHistory.TotalTickets})\" : \"\")", html);
    }

    [Fact]
    public void View_PreviousTicketsTabPanelLivesOutsideTheFactsPanel_DoesNotCompeteWithPrimaryInformation()
    {
        // The whole reason for the tab: Customer History is useful context
        // but not always relevant to the current ticket, so it must not sit
        // inline inside the primary facts-panel any more.
        var html = TicketDetailsViewHtml();

        var factsPanelEnd = html.IndexOf("</aside>", StringComparison.Ordinal);
        var panelHistoryStart = html.IndexOf("id=\"panel-history\"", StringComparison.Ordinal);

        Assert.True(factsPanelEnd > 0, "Expected a facts-panel <aside> in the view.");
        Assert.True(panelHistoryStart > factsPanelEnd, "The Previous Tickets tab panel must be a sibling of, not nested inside, the facts-panel.");
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
