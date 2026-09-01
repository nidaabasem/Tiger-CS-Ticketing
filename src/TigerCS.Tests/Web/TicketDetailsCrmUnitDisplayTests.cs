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
        // Manual entry is exactly that — not externally verified — but it is
        // never conflated with an external-lookup verification (see
        // View_DistinguishesExternalVerificationFromManualEntry below).
        Assert.Contains("Manual entry / Not externally verified", html);
    }

    [Fact]
    public void View_DistinguishesExternalVerificationFromManualEntry()
    {
        // A PACT/Tasleeh-verified ticket (CustomerVerificationSource set at
        // creation) shows "Verified via {source}" with the source's own
        // customer/unit ids — never the manual-entry wording, and never
        // "not verified": the customer WAS verified, against that source.
        var html = TicketDetailsViewHtml();

        Assert.Contains("hasExternalVerification", html);
        Assert.Contains("Verified via", html);
        Assert.Contains("t.CustomerVerificationSource", html);
        Assert.Contains("t.ExternalCustomerId", html);
        Assert.Contains("t.ExternalUnitId", html);

        // The external branch outranks the manual branch, so a ticket
        // carrying both the identity and the snapshot renders as verified.
        var externalBranchIndex = html.IndexOf("if (hasExternalVerification)", StringComparison.Ordinal);
        var manualBranchIndex = html.IndexOf("else if (hasManualUnit)", StringComparison.Ordinal);
        Assert.True(externalBranchIndex > 0 && externalBranchIndex < manualBranchIndex);
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
    public void View_DisplayPriorityIsCrmBuyerThenExternalThenManualThenLegacy()
    {
        var html = TicketDetailsViewHtml();

        var crmCheckIndex = html.IndexOf("hasCrmBuyer = t.CrmBuyerUnitId is not null", StringComparison.Ordinal);
        var externalCheckIndex = html.IndexOf("hasExternalVerification = !hasCrmBuyer", StringComparison.Ordinal);
        var manualCheckIndex = html.IndexOf("hasManualUnit = !hasCrmBuyer && !hasExternalVerification", StringComparison.Ordinal);
        var legacyCheckIndex = html.IndexOf("hasLegacyReference = !hasCrmBuyer && !hasExternalVerification && !hasManualUnit", StringComparison.Ordinal);

        Assert.True(crmCheckIndex > 0);
        Assert.True(externalCheckIndex > crmCheckIndex);
        Assert.True(manualCheckIndex > externalCheckIndex);
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
