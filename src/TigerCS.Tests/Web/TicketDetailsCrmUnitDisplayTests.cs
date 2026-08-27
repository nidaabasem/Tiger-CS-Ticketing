using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details "Verification &amp; Unit" panel display fix: for a CRM-Buyer-verified
/// ticket, the page must show the selected Customer/Project/Unit Number snapshot
/// (persisted on Ticket at creation time) instead of the old, meaningless
/// "Unit reference: —" / "Contact reference: —" rows. See TicketDetails.cshtml.
/// </summary>
public sealed class TicketDetailsCrmUnitDisplayTests
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

    [Fact]
    public void View_RendersCrmBuyerCustomerProjectAndUnitNumberLabels()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("<dt>Customer</dt>", html);
        Assert.Contains("<dt>Project</dt>", html);
        Assert.Contains("<dt>Unit Number</dt>", html);
        Assert.Contains("<dt>CRM Unit ID</dt>", html);
        Assert.Contains("<dt>Lead ID</dt>", html);
        Assert.Contains("t.CrmBuyerCustomerName", html);
        Assert.Contains("t.CrmBuyerProjectName", html);
        Assert.Contains("t.CrmBuyerUnitNumber", html);
    }

    [Fact]
    public void View_RendersManualProjectAndUnitNumberFallback()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("t.ManualProjectName", html);
        Assert.Contains("t.ManualUnitNumber", html);
        Assert.Contains("Not Verified / Not Found", html);
    }

    [Fact]
    public void View_DoesNotUnconditionallyRenderLegacyUnitOrContactReferenceRows()
    {
        // The old, meaningless display: two "—" rows shown for every ticket
        // regardless of whether it has a real CRM Buyer or manual match.
        // The legacy Unit/Contact reference rows must now only render inside
        // the legacy fallback branch (hasLegacyReference), not unconditionally.
        var html = TicketDetailsViewHtml();

        Assert.Contains("hasLegacyReference", html);
        var legacyRowIndex = html.IndexOf("<dt>Unit reference</dt>", StringComparison.Ordinal);
        Assert.True(legacyRowIndex > 0, "Legacy 'Unit reference' row should still exist for backward compatibility.");

        // The legacy row must appear after the hasLegacyReference branch check, i.e. it is
        // gated rather than always rendered alongside the Verification row.
        var branchGuardIndex = html.IndexOf("else if (hasLegacyReference)", StringComparison.Ordinal);
        Assert.True(branchGuardIndex > 0 && branchGuardIndex < legacyRowIndex);
    }

    [Fact]
    public void View_DisplayPriorityIsCrmBuyerThenManualThenLegacy()
    {
        var html = TicketDetailsViewHtml();

        var crmCheckIndex = html.IndexOf("hasCrmBuyer = t.CrmBuyerUnitId is not null", StringComparison.Ordinal);
        var manualCheckIndex = html.IndexOf("hasManualUnit = !hasCrmBuyer", StringComparison.Ordinal);
        var legacyCheckIndex = html.IndexOf("hasLegacyReference = !hasCrmBuyer && !hasManualUnit", StringComparison.Ordinal);

        Assert.True(crmCheckIndex > 0);
        Assert.True(manualCheckIndex > crmCheckIndex);
        Assert.True(legacyCheckIndex > manualCheckIndex);
    }

    [Fact]
    public void Model_DoesNotDependOnAnyCrmApiClient_TicketDetailsNeverCallsCrmLive()
    {
        // Ticket Details must render from the persisted ticket-time snapshot alone —
        // never re-query CRM (GetBuyerByPhone or otherwise) when the page loads.
        var source = TicketDetailsModelSource();

        Assert.DoesNotContain("CrmBuyerLookupApiClient", source);
        Assert.DoesNotContain("CrmApiClient", source);
        Assert.DoesNotContain("GetBuyerByPhone", source);
    }
}
