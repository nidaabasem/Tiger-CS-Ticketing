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
    public void NewTicketView_NoMatchAndCrmUnavailableCases_OfferContinueRatherThanDeadEnd()
    {
        // Business-rule change: CRM Buyer Lookup no longer gates ticket
        // creation — a NotFound or Unavailable outcome both still show
        // "Customer not found in CRM." and a Continue path onward, never a
        // dead end.
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.Contains("Customer not found in CRM.", html);
        Assert.Contains("Continue to Ticket", html);
    }

    // ---- The phone input must accept a leading '+': no HTML pattern, numeric
    // type, or length cap may reject "+971501234567" client-side ----

    [Fact]
    public void NewTicketView_PhoneInput_HasNoPatternTypeOrLengthRestriction()
    {
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        var inputLine = html.Split('\n').Single(line => line.Contains("asp-for=\"Intake.PhoneNumber\"") && line.Contains("<input"));

        // A plain text input: the tag helper renders type="text" for an
        // unannotated string, and nothing here may narrow what the browser
        // lets the agent type or submit.
        Assert.DoesNotContain("pattern=", inputLine);
        Assert.DoesNotContain("type=\"number\"", inputLine);
        Assert.DoesNotContain("type=\"tel\"", inputLine);
        Assert.DoesNotContain("maxlength", inputLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inputmode", inputLine, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Department-aware lookup: PACT's raw numeric customer-type code must never render as a label ----

    [Fact]
    public void NewTicketView_NeverRendersTheRawCustomerTypeCode()
    {
        // PACT's customerBuyerType reaches the Web layer as a raw numeric
        // code ("1"/"2") whose code table is not published — presenting it as
        // a customer-type label would mislead, so the New Ticket view must
        // not reference the CustomerType field at all.
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.DoesNotContain("CustomerType", html);
    }

    [Fact]
    public void NewTicketView_PresentsAnExternalSelectionAsVerifiedViaItsSource_NeverAsNotVerified()
    {
        // A matched PACT/Tasleeh customer WAS verified — against that source.
        // Step 3 must say "verified via {source}"; "not verified" wording is
        // reserved for manual entry (which the Ticket Details page labels
        // "Manual entry / Not externally verified").
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.Contains("verified via", html);
        Assert.DoesNotContain("not verified", html, StringComparison.OrdinalIgnoreCase);
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

    // ---- Department directory: the manual numeric DepartmentId textbox and its temporary help text are gone ----

    [Fact]
    public void NewTicketView_DoesNotContainTheManualDepartmentIdInput()
    {
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.DoesNotContain("No department directory endpoint exists yet", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enter the department id", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enter the numeric department id", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<input class=\"form-control\" asp-for=\"Intake.DepartmentId\" />", html);
    }

    [Fact]
    public void NewTicketView_RendersADepartmentDropdown_LabeledByName()
    {
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.Contains("Department (optional)", html);
        Assert.Contains("<select class=\"form-control\" asp-for=\"Intake.DepartmentId\"", html);
        Assert.Contains("department.Name", html);
        Assert.Contains("department.DepartmentId", html);
    }

    [Fact]
    public void NewTicketView_DoesNotContainTheOldUnitIdentificationOrPriorityHintUi()
    {
        // Item 2/3 of the correction: the caller-given unit number is no
        // longer a primary Step 1 selection mechanism, and Priority moved
        // entirely to Step 3 — neither exists in the view any more.
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.DoesNotContain("This interaction concerns a specific unit", html);
        Assert.DoesNotContain("Unit number (as given by the caller)", html);
        Assert.DoesNotContain("Priority hint", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Intake.IsUnitRelated", html);
        Assert.DoesNotContain("Intake.RawUnitNumberEntered", html);
        Assert.DoesNotContain("Intake.PriorityHint", html);
        Assert.DoesNotContain("unit-related intake only", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confirmed verification required", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewTicketView_UnitIsSelectedFromCrmBuyerLookupResults_NeverAutomatically()
    {
        // The actual Ticket Unit is a radio choice inside a matched CRM
        // Buyer's own eligible units — never a manually-typed number, and
        // never pre-checked (no customer or unit is ever auto-selected).
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.Contains("Select Unit", html);
        Assert.Contains("type=\"radio\" name=\"selectedCrmBuyerUnit\"", html);
        Assert.DoesNotContain("checked", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewTicketView_Step3_ShowsCrmBuyerCustomerAndUnitSummary_WithPriorityRequired()
    {
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.Contains("Model.CrmBuyerCustomerName", html);
        Assert.Contains("Model.CrmBuyerUnitNumber", html);
        Assert.Contains("Priority *", html);
        Assert.Contains("<select class=\"form-control\" asp-for=\"CreateStep.PriorityId\"", html);
        Assert.Contains("Select Priority", html);
    }

    [Fact]
    public void NewTicketView_Step3_RequiresManualProjectAndUnitNumber_WhenNoCrmBuyerMatchSelected()
    {
        var html = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

        Assert.Contains("Project *", html);
        Assert.Contains("Unit Number *", html);
        Assert.Contains("asp-for=\"CreateStep.ManualProjectName\"", html);
        Assert.Contains("asp-for=\"CreateStep.ManualUnitNumber\"", html);
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

    // ---- The New Ticket wizard's phone search must call ONLY the real CRM Buyer Lookup client ----

    [Fact]
    public void CrmBuyerLookupApiClient_ExistsInTigerCSWeb_CallingApiCrmBuyers()
    {
        var type = typeof(TicketsApiClient).Assembly.GetType("TigerCS.Web.Services.Api.CrmBuyerLookupApiClient");

        Assert.NotNull(type);
        var method = type!.GetMethod("SearchByPhoneAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
    }

    [Fact]
    public void NewTicketModel_DependsOnBothTheGenericCustomerLookupAndTheRealCrmBuyerLookupClients()
    {
        // Business-rule change (reversing this guard's earlier direction):
        // Step 2 is department-aware, so the wizard now consumes the generic
        // CRM/PACT/Tasleeh CustomerLookupApiClient — the authoritative answer
        // to which sources participate (DepartmentCustomerLookupSources) and
        // the carrier of PACT/Tasleeh results — while the real CRM Buyer
        // Lookup client still performs the CRM leg itself (its generic Crm
        // entry is fixture-backed; see NewTicketModel's remarks).
        var constructor = Assert.Single(typeof(TigerCsWeb::TigerCS.Web.Pages.NewTicketModel).GetConstructors());
        var parameterTypeNames = constructor.GetParameters().Select(p => p.ParameterType.Name).ToList();

        Assert.Contains("CustomerLookupApiClient", parameterTypeNames);
        Assert.Contains("CrmBuyerLookupApiClient", parameterTypeNames);
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
