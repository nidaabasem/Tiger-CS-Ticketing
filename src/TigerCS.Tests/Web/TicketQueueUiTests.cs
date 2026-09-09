using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// Guards two navigation/queue cleanups in TigerCS.Web: the "Pending CRM"
/// primary-nav shortcut is gone (the CRM verification filter and lookup
/// capabilities are not), and the Ticket Queue's status counters render as
/// the shared KPI card row rather than the old inline stat strip.
/// </summary>
public sealed class TicketQueueUiTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string View(params string[] pathUnderPages) =>
        File.ReadAllText(SourceFile(Path.Combine(["TigerCS.Web", "Pages", .. pathUnderPages])));

    // ---- 1: "Pending CRM" is no longer a primary navigation item ----

    [Fact]
    public void PrimaryNav_NoLongerOffersPendingCrm()
    {
        var nav = View("Shared", "_Nav.cshtml");

        Assert.DoesNotContain("Pending CRM", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pendingcrm\"", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("verificationStatus=PendingCrmVerification", nav, StringComparison.Ordinal);

        // The rest of the primary navigation is untouched.
        foreach (var label in new[] { "\"Dashboard\"", "\"Customers\"", "\"Queue\"", "\"My Tickets\"", "\"Closed\"", "\"Administration\"" })
        {
            Assert.Contains(label, nav, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TicketQueue_NoLongerMapsToTheRemovedPendingCrmNavKey_ButKeepsTheVerificationFilter()
    {
        var queue = View("Tickets.cshtml");

        // No view may highlight a nav item that no longer exists.
        Assert.DoesNotContain("\"pendingcrm\"", queue, StringComparison.Ordinal);

        // Navigation cleanup only: the CRM verification filter (and every
        // verification status it offers) still exists on the queue.
        Assert.Contains("name=\"verificationStatus\"", queue, StringComparison.Ordinal);
        Assert.Contains("\"PendingCrmVerification\"", queue, StringComparison.Ordinal);
    }

    // ---- 2: the queue's counters are a KPI card row built from the shared component ----

    [Fact]
    public void TicketQueue_RendersTheFourStatusCountersAsSharedKpiCards()
    {
        var queue = View("Tickets.cshtml");

        Assert.DoesNotContain("stat-strip", queue, StringComparison.Ordinal);
        Assert.Contains("class=\"kpi-grid\"", queue, StringComparison.Ordinal);

        // Four cards, each carrying its existing count and label — the counts
        // themselves are the same page-model properties as before.
        Assert.Equal(4, CountOccurrences(queue, "class=\"kpi-card"));
        foreach (var (count, label) in new[]
        {
            ("Model.OpenCount", "<span>Open</span>"),
            ("Model.InProgressCount", "<span>In Progress</span>"),
            ("Model.PendingCustomerCount", "<span>Pending Customer</span>"),
            ("Model.ClosedCount", "<span>Closed</span>"),
        })
        {
            Assert.Contains(count, queue, StringComparison.Ordinal);
            Assert.Contains(label, queue, StringComparison.Ordinal);
        }

        // Gold is an accent only: exactly one card (Open) carries the emphasis modifier.
        Assert.Equal(1, CountOccurrences(queue, "kpi-card--attention"));

        // Title on the left and the primary action on the right are unchanged.
        Assert.Contains("<h1 class=\"page-title\">Ticket Queue</h1>", queue, StringComparison.Ordinal);
        Assert.Contains("<a class=\"btn btn-gold\" href=\"/NewTicket\">+ New Ticket</a>", queue, StringComparison.Ordinal);
    }

    [Fact]
    public void SiteCss_DefinesAResponsiveKpiGrid_AndNoLongerCarriesTheStatStrip()
    {
        var css = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "wwwroot", "css", "site.css")));

        Assert.DoesNotContain(".stat-strip", css, StringComparison.Ordinal);

        // The shared card row wraps on narrow screens (auto-fit grid) and each
        // card has its own border and surface background.
        Assert.Contains(".kpi-grid {", css, StringComparison.Ordinal);
        Assert.Contains("repeat(auto-fit, minmax(", css, StringComparison.Ordinal);
        Assert.Contains(".kpi-card {", css, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid var(--color-border)", css, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
