extern alias TigerCsWeb;

using System.Reflection;
using System.Runtime.CompilerServices;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// Guards the cleanup half of the Intake → Customer Lookup → Ticket rewrite: the old
/// VerificationSession-gated flow's dead-end messages, endpoints, and client methods must
/// actually be gone from TigerCS.Web, and the new duplicate-submit guard must actually be present.
/// </summary>
public sealed class WebFrontEndCleanupTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    // ---- 15: the old non-unit/CRM-verification dead-end messages no longer exist ----

    [Fact]
    public void NewTicketView_DoesNotContainOldDeadEndMessages()
    {
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.DoesNotContain("cannot be promoted", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("only be created after CRM verification", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VerificationSessionId", html);
        Assert.DoesNotContain("non-unit-related IntakeRecord", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewTicketView_NoMatchAndConfigMissingCases_OfferContinueRatherThanDeadEnd()
    {
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.Contains("No customer information found. You can continue creating the ticket.", html);
        Assert.Contains("Customer lookup is not configured for the selected department. You can continue creating the ticket.", html);
        Assert.Contains("Continue to Ticket", html);
    }

    // ---- Category directory: the manual numeric CategoryId textbox and its temporary help text are gone ----

    [Fact]
    public void NewTicketView_DoesNotContainTheManualCategoryIdInput()
    {
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.DoesNotContain("Category ID", html);
        Assert.DoesNotContain("No category directory endpoint exists yet", html);
        Assert.DoesNotContain("numeric category id", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("asp-for=\"CreateStep.CategoryId\" />", html);
    }

    [Fact]
    public void NewTicketView_RendersACategoryDropdown_LabeledByName()
    {
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.Contains("Category *", html);
        Assert.Contains("<select class=\"form-control\" asp-for=\"CreateStep.CategoryId\"", html);
        Assert.Contains("category.Name", html);
    }

    [Fact]
    public void NewTicketView_HandlesEmptyAndFailedCategoryLoadStates()
    {
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.Contains("No active categories are configured for this department.", html);
        Assert.Contains("Model.CategoriesErrorMessage", html);
    }

    [Fact]
    public void CreateStepInput_CategoryId_IsNullableWithNoNumericRangeFallback()
    {
        var property = typeof(TigerCsWeb::TigerCS.Web.Pages.NewTicketModel.CreateStepInput).GetProperty("CategoryId");

        Assert.NotNull(property);
        Assert.Equal(typeof(int?), property!.PropertyType);
    }

    // ---- 16: deleted endpoints/clients are gone, and the only ticket-creation client method is CreateAsync → POST api/tickets ----

    [Fact]
    public void VerificationSessionsApiClient_NoLongerExistsInTigerCSWeb()
    {
        var type = typeof(TicketsApiClient).Assembly.GetType("TigerCS.Web.Services.Api.VerificationSessionsApiClient");

        Assert.Null(type);
    }

    [Fact]
    public void CrmApiClient_NoLongerExistsInTigerCSWeb()
    {
        var type = typeof(TicketsApiClient).Assembly.GetType("TigerCS.Web.Services.Api.CrmApiClient");

        Assert.Null(type);
    }

    [Fact]
    public void TicketsApiClient_NoLongerExposesReconcileAsync()
    {
        var method = typeof(TicketsApiClient).GetMethod("ReconcileAsync", BindingFlags.Public | BindingFlags.Instance);

        Assert.Null(method);
    }

    [Fact]
    public void TicketsApiClient_CreateAsync_IsTheOnlyTicketCreationMethod()
    {
        var creationMethods = typeof(TicketsApiClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.Contains("Create", StringComparison.Ordinal));

        var method = Assert.Single(creationMethods);
        Assert.Equal(nameof(TicketsApiClient.CreateAsync), method.Name);
    }

    [Fact]
    public void ProgramCs_NoLongerRegistersDeletedApiClients()
    {
        var programCs = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Program.cs")));

        Assert.DoesNotContain("VerificationSessionsApiClient", programCs);
        Assert.DoesNotContain("CrmApiClient", programCs);
    }

    // ---- 17: duplicate ticket submission is prevented ----

    [Fact]
    public void SiteJs_DisablesSubmitButtonsOnFormSubmit_ToPreventDuplicateSubmission()
    {
        var siteJs = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "wwwroot", "js", "site.js")));

        Assert.Contains("addEventListener(\"submit\"", siteJs);
        Assert.Contains("btn.disabled = true", siteJs);
    }
}
