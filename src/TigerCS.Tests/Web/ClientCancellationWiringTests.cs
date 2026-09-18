using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// The two halves of "an agent clicked Customers twice" are wired where they
/// have to be: the client-disconnect guard is registered in BOTH hosts (and,
/// in TigerCS.Api, inside the exception handler so an abandoned request is
/// recognised before that handler treats it as a defect), and the browser is
/// stopped from starting the second request at all.
///
/// <para>
/// Source-level assertions because the thing under test is composition —
/// the order of a middleware pipeline, and a script the test host has no
/// engine to run. The middleware's own behaviour is proven directly in
/// <see cref="ClientDisconnectMiddlewareTests"/>.
/// </para>
/// </summary>
public sealed class ClientCancellationWiringTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    // ---- the server treats an abandoned request as a cancellation, not a fault ----

    [Fact]
    public void ApiProgram_RegistersTheClientDisconnectGuard_InsideTheExceptionHandler()
    {
        var program = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Api", "Program.cs")));

        var exceptionHandler = program.IndexOf("app.UseExceptionHandler();", StringComparison.Ordinal);
        var clientDisconnect = program.IndexOf("app.UseTigerCsClientDisconnectHandling();", StringComparison.Ordinal);

        Assert.True(exceptionHandler >= 0, "TigerCS.Api must keep its ProblemDetails exception handler.");
        Assert.True(clientDisconnect >= 0, "TigerCS.Api must register the client-disconnect guard.");
        // Registered later means nested INSIDE: the guard sees the cancelled
        // Customers query first, so UseExceptionHandler never logs it as an
        // unhandled exception or answers it with a 500.
        Assert.True(
            clientDisconnect > exceptionHandler,
            "The client-disconnect guard must be registered after UseExceptionHandler so it runs inside it.");
    }

    [Fact]
    public void WebProgram_RegistersTheClientDisconnectGuard_AroundThePagePipeline()
    {
        var program = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Program.cs")));

        var clientDisconnect = program.IndexOf("app.UseTigerCsClientDisconnectHandling();", StringComparison.Ordinal);
        var routing = program.IndexOf("app.UseRouting();", StringComparison.Ordinal);

        Assert.True(clientDisconnect >= 0, "TigerCS.Web must register the client-disconnect guard.");
        Assert.True(
            clientDisconnect < routing,
            "The guard must wrap routing so a cancelled page handler is caught before it leaves the pipeline.");
    }

    // ---- the browser does not start the second request in the first place ----

    [Fact]
    public void SiteJs_DropsASecondSubmitWhileTheFirstIsStillInFlight()
    {
        var siteJs = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "wwwroot", "js", "site.js")));

        Assert.Contains("addEventListener(\"submit\"", siteJs);
        // The re-entrancy guard, not the disabled button, is what actually
        // stops the duplicate: a second Enter or a filter change mid-navigation
        // never reaches the browser's submit algorithm.
        Assert.Contains("data-submitting", siteJs);
        Assert.Contains("event.preventDefault()", siteJs);
        Assert.Contains("btn.disabled = true", siteJs);
    }

    [Fact]
    public void SiteJs_AutoSubmittingFilters_DoNotFireOverAnInFlightSubmit()
    {
        var siteJs = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "wwwroot", "js", "site.js")));

        Assert.Contains("if (el.form && !el.form.hasAttribute(\"data-submitting\"))", siteJs);
    }

    /// <summary>
    /// Back/forward restores the page as it was left — mid-submit — so the
    /// guard has to be released, or the agent returns to a Customers filter
    /// bar whose Apply button no longer works.
    /// </summary>
    [Fact]
    public void SiteJs_ReleasesTheGuard_WhenThePageIsRestoredFromTheBackForwardCache()
    {
        var siteJs = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "wwwroot", "js", "site.js")));

        Assert.Contains("\"pageshow\"", siteJs);
        Assert.Contains("form.removeAttribute(\"data-submitting\")", siteJs);
        Assert.Contains("btn.disabled = false", siteJs);
    }

    /// <summary>
    /// The Customers search/filter bar is a real GET form with a real submit
    /// button, so the guard above applies to it — that is the form an agent
    /// double-submits.
    /// </summary>
    [Fact]
    public void CustomersView_FilterBar_IsAPlainFormCoveredByTheDuplicateSubmitGuard()
    {
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "Customers.cshtml")));

        Assert.Contains("id=\"customerFilters\"", html);
        Assert.Contains("method=\"get\"", html);
        Assert.Contains("type=\"submit\"", html);
    }
}
