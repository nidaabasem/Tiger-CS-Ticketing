// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj — because
// its own top-level-statement Program type would otherwise collide with
// TigerCS.Api's, which existing WebApplicationFactory<Program> tests use unqualified.
extern alias TigerCsWeb;

using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// Covers the New Ticket wizard's PageModel: Intake → real CRM Buyer Lookup
/// (<c>GET /api/crm/buyers?phoneNumber=</c>) → Category/Priority/manual
/// Project+Unit-Number → Ticket, against TigerCS.Api's real DTO contracts
/// with <see cref="FakeApiHandler"/> standing in for the Api itself. No
/// ASP.NET Core host is spun up — like the app-service tests elsewhere in
/// this project, each handler is exercised directly against fakes at its one
/// real dependency boundary (here, HTTP).
///
/// <para>
/// Business-rule change: this wizard's phone search calls the real CRM Buyer
/// Lookup endpoint only — never the generic CRM/PACT/Tasleeh
/// <c>CustomerLookupApiClient</c>, and never a Unit Number/Project search.
/// </para>
/// </summary>
public sealed class NewTicketModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static (NewTicketModel Model, FakeApiHandler Intake, FakeApiHandler CrmBuyerLookup, FakeApiHandler Departments, FakeApiHandler Categories, FakeApiHandler Tickets, FakeApiHandler CustomerHistory) CreateModel(
        Func<HttpRequestMessage, string?, HttpResponseMessage>? intakeResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? crmBuyerLookupResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? departmentsResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? categoriesResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? ticketsResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? customerHistoryResponder = null)
    {
        var intakeHandler = new FakeApiHandler(intakeResponder ?? ((_, _) => throw new InvalidOperationException("Intake API not expected to be called.")));
        var crmBuyerLookupHandler = new FakeApiHandler(crmBuyerLookupResponder ?? ((_, _) => throw new InvalidOperationException("CRM Buyer Lookup API not expected to be called.")));
        var departmentsHandler = new FakeApiHandler(departmentsResponder ?? (
            (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, Array.Empty<DepartmentDto>())));
        var categoriesHandler = new FakeApiHandler(categoriesResponder ?? ((_, _) => throw new InvalidOperationException("Categories API not expected to be called.")));
        var ticketsHandler = new FakeApiHandler(ticketsResponder ?? ((_, _) => throw new InvalidOperationException("Tickets API not expected to be called.")));
        // No-op "empty history" default (unlike the other clients above,
        // which throw by default): the customer-history preview is
        // enrichment, not central to most of this wizard's own tests, so
        // only the tests that actually care about it supply a responder.
        var customerHistoryHandler = new FakeApiHandler(customerHistoryResponder ?? (
            (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerHistoryDto("Verified", null, null, null, 0, 0, 0, []))));

        var intakeClient = new IntakeRecordsApiClient(
            new HttpClient(intakeHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<IntakeRecordsApiClient>.Instance);
        var crmBuyerLookupClient = new CrmBuyerLookupApiClient(
            new HttpClient(crmBuyerLookupHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<CrmBuyerLookupApiClient>.Instance);
        var departmentsClient = new DepartmentsApiClient(
            new HttpClient(departmentsHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<DepartmentsApiClient>.Instance);
        var categoriesClient = new CategoriesApiClient(
            new HttpClient(categoriesHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<CategoriesApiClient>.Instance);
        var ticketsClient = new TicketsApiClient(
            new HttpClient(ticketsHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<TicketsApiClient>.Instance);
        var customerHistoryClient = new CustomerHistoryApiClient(
            new HttpClient(customerHistoryHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<CustomerHistoryApiClient>.Instance);

        var model = new NewTicketModel(intakeClient, crmBuyerLookupClient, departmentsClient, categoriesClient, ticketsClient, customerHistoryClient);
        return (model, intakeHandler, crmBuyerLookupHandler, departmentsHandler, categoriesHandler, ticketsHandler, customerHistoryHandler);
    }

    private static Func<HttpRequestMessage, string?, HttpResponseMessage> CategoriesReturning(params CategoryDto[] categories) =>
        (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, categories);

    private static Func<HttpRequestMessage, string?, HttpResponseMessage> DepartmentsReturning(params DepartmentDto[] departments) =>
        (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, departments);

    private static Func<HttpRequestMessage, string?, HttpResponseMessage> CrmBuyersFound(params CrmBuyerMatchDto[] buyers) =>
        (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, buyers);

    private static CrmBuyerMatchDto SingleUnitBuyer(int customerId, string name, string phone, int leadId, int unitId, int projectId, string unitNumber, string projectName) =>
        new(
            new CrmCustomerDto(customerId, name, null, phone, $"{name.Replace(" ", ".").ToLowerInvariant()}@example.com"),
            [new CrmBuyerUnitDto(leadId, 8, "Sold", unitId, unitNumber, 1, 1, 4, projectId, projectName, null, 1, "Buyer")]);

    // ---- Step 1: PhoneNumber required, DepartmentId optional (unaffected by the CRM Buyer Lookup rewiring) ----

    [Fact]
    public void IntakeInput_PhoneNumber_IsRequired()
    {
        var input = new NewTicketModel.IntakeInput { PhoneNumber = "" };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(NewTicketModel.IntakeInput.PhoneNumber)));
    }

    [Fact]
    public void IntakeInput_DepartmentId_IsOptional()
    {
        var input = new NewTicketModel.IntakeInput { PhoneNumber = "+15551234567", DepartmentId = null };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true);

        Assert.True(isValid);
    }

    [Fact]
    public async Task OnGetAsync_IntakeStep_LoadsDepartmentDirectory_ForTheDropdown()
    {
        var (model, _, _, departments, _, _, _) = CreateModel(departmentsResponder: DepartmentsReturning(
            new DepartmentDto(7, "Facilities Management"),
            new DepartmentDto(2, "Customer Service")));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Single(departments.Requests);
        Assert.Equal(2, model.Departments.Count);
        Assert.Contains(model.Departments, d => d is { DepartmentId: 7, Name: "Facilities Management" });
    }

    [Fact]
    public async Task OnPostIntakeAsync_Failure_ReloadsDepartmentDirectory_ForRedisplay()
    {
        var (model, _, _, departments, _, _, _) = CreateModel(
            intakeResponder: (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway),
            departmentsResponder: DepartmentsReturning(new DepartmentDto(7, "Facilities Management")));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567" };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
        Assert.Single(departments.Requests);
        Assert.Single(model.Departments);
    }

    [Fact]
    public async Task OnPostIntakeAsync_SendsPhoneNumberAndDepartmentIdToIntakeApi()
    {
        var (model, intake, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new IntakeRecordResponseDto(
                42, "Phone", DateTime.UtcNow, "+15551234567", 7, true, null, null, "Unverified", null)));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567", DepartmentId = 7 };

        await model.OnPostIntakeAsync(CancellationToken.None);

        var sent = Assert.Single(intake.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://localhost/api/intake-records", sent.RequestUri);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal("+15551234567", body.RootElement.GetProperty("phoneNumber").GetString());
        Assert.Equal(7, body.RootElement.GetProperty("departmentId").GetInt32());
    }

    [Fact]
    public async Task OnPostIntakeAsync_Success_RedirectsToLookupStep_CarryingIntakeRecordIdPhoneNumberAndDepartment()
    {
        var (model, _, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new IntakeRecordResponseDto(
                42, "Phone", DateTime.UtcNow, "+15551234567", 7, false, null, null, "Unverified", null)));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567", DepartmentId = 7 };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        var values = RouteValues(redirect);
        Assert.Equal("lookup", values["step"]);
        Assert.Equal(42L, values["intakeRecordId"]);
        Assert.Equal("+15551234567", values["phoneNumber"]);
        Assert.Equal(7, values["departmentId"]);
    }

    // ---- The wizard's phone search calls ONLY the real CRM Buyer Lookup endpoint, by phone number ----

    [Fact]
    public async Task OnGetAsync_LookupStep_CallsRealCrmBuyerLookupApi_ByPhoneNumberOnly()
    {
        var (model, _, crmBuyerLookup, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound());

        await model.OnGetAsync("lookup", 42, "+9613040922", null, null, null, null, null, null, null, null, CancellationToken.None);

        var sent = Assert.Single(crmBuyerLookup.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("http://localhost/api/crm/buyers?phoneNumber=%2B9613040922", sent.RequestUri);
    }

    [Fact]
    public async Task OnGetAsync_LookupStep_NeverSendsUnitNumberOrProjectQueryParameters()
    {
        // Business rule: CRM is searched by phone number only, never Unit
        // Number/Project/Tower. A structural guard on the actual outgoing
        // request, not just on the client's method signature.
        var (model, _, crmBuyerLookup, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound());

        await model.OnGetAsync("lookup", 42, "+9613040922", null, null, null, null, null, null, null, null, CancellationToken.None);

        var sent = Assert.Single(crmBuyerLookup.Requests);
        Assert.DoesNotContain("unitNumber", sent.RequestUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("project", sent.RequestUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unit=", sent.RequestUri, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Scenario 1: one buyer, one unit ----

    [Fact]
    public async Task OnGetAsync_Lookup_OneBuyerOneUnit_PopulatesCrmBuyerMatches()
    {
        var (model, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound(
            SingleUnitBuyer(5001, "Sami Nasser", "+971509990001", leadId: 900, unitId: 100, projectId: 10, unitNumber: "5001", projectName: "Tiger Sky Tower")));

        await model.OnGetAsync("lookup", 42, "+971509990001", null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.False(model.CrmBuyerLookupUnavailable);
        var match = Assert.Single(model.CrmBuyerMatches!);
        Assert.Equal("Sami Nasser", match.Customer.FullNameEnglish);
        var unit = Assert.Single(match.Units);
        Assert.Equal("5001", unit.UnitNumber);
        Assert.Equal("Tiger Sky Tower", unit.ProjectName);
        Assert.Equal(8, unit.LeadStatus);
    }

    // ---- Scenario 2: one buyer, multiple units ----

    [Fact]
    public async Task OnGetAsync_Lookup_OneBuyerMultipleUnits_AllUnitsPresent_NoneAutoSelected()
    {
        var buyer = new CrmBuyerMatchDto(
            new CrmCustomerDto(5001, "Ahmed Ali", null, "+971501234567", "ahmed.ali@example.com"),
            [
                new CrmBuyerUnitDto(901, 8, "Sold", 101, "1205", 1, 1, 4, 10, "Tiger Sky Tower", null, 1, "Buyer"),
                new CrmBuyerUnitDto(902, 9, "Contract", 102, "1403", 1, 1, 6, 10, "Tiger Sky Tower", null, 1, "Buyer")
            ]);
        var (model, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound(buyer));

        await model.OnGetAsync("lookup", 42, "+971501234567", null, null, null, null, null, null, null, null, CancellationToken.None);

        var match = Assert.Single(model.CrmBuyerMatches!);
        Assert.Equal(2, match.Units.Count);
        Assert.Contains(match.Units, u => u is { UnitNumber: "1205", LeadStatus: 8 });
        Assert.Contains(match.Units, u => u is { UnitNumber: "1403", LeadStatus: 9 });
        // Nothing on the model itself picks a unit — CrmBuyerUnitId stays
        // unset until the agent explicitly posts OnPostUseCrmBuyerUnit.
        Assert.Null(model.CrmBuyerUnitId);
    }

    // ---- Scenario 3: multiple buyers matched by the same phone number ----

    [Fact]
    public async Task OnGetAsync_Lookup_MultipleBuyersSamePhone_AllBuyersPresent_NoneAutoSelected()
    {
        var (model, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound(
            SingleUnitBuyer(5001, "Ahmed Ali", "+971501234567", 901, 101, 10, "1205", "Tiger Sky Tower"),
            SingleUnitBuyer(5002, "Ahmad Ali Hassan", "+971501234567", 903, 103, 10, "2004", "Tiger Sky Tower")));

        await model.OnGetAsync("lookup", 42, "+971501234567", null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Equal(2, model.CrmBuyerMatches!.Count);
        Assert.Contains(model.CrmBuyerMatches, m => m.Customer.CustomerId == 5001);
        Assert.Contains(model.CrmBuyerMatches, m => m.Customer.CustomerId == 5002);
        Assert.Null(model.CrmBuyerCustomerId);
    }

    // ---- Scenario 4: no CRM match ----

    [Fact]
    public async Task OnGetAsync_Lookup_NoCrmMatch_EmptyMatches_NotFlaggedUnavailable()
    {
        var (model, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        await model.OnGetAsync("lookup", 42, "+9613040922", null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.NotNull(model.CrmBuyerMatches);
        Assert.Empty(model.CrmBuyerMatches!);
        Assert.False(model.CrmBuyerLookupUnavailable);
    }

    // ---- Scenario 5: CRM unavailable — never blocks the wizard ----

    [Fact]
    public async Task OnGetAsync_Lookup_CrmUnavailable_EmptyMatches_FlaggedUnavailable()
    {
        var (model, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        await model.OnGetAsync("lookup", 42, "+9613040922", null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.NotNull(model.CrmBuyerMatches);
        Assert.Empty(model.CrmBuyerMatches!);
        Assert.True(model.CrmBuyerLookupUnavailable);
        Assert.Null(model.ErrorMessage); // never a blocking error — the wizard must remain usable
    }

    [Fact]
    public async Task OnGetAsync_Lookup_CrmNetworkUnreachable_TreatedAsUnavailable_NeverThrows()
    {
        var (model, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: (_, _) =>
            throw new HttpRequestException("Connection refused"));

        await model.OnGetAsync("lookup", 42, "+9613040922", null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.NotNull(model.CrmBuyerMatches);
        Assert.Empty(model.CrmBuyerMatches!);
        Assert.True(model.CrmBuyerLookupUnavailable);
    }

    // ---- Selecting a CRM Buyer unit carries every identifier + display snapshot forward ----

    [Fact]
    public void OnPostUseCrmBuyerUnit_CarriesAllFourCrmIdsAndSnapshotText_ToCreateStep()
    {
        var (model, _, _, _, _, _, _) = CreateModel();
        var packed = string.Join(':', 5001, 901, 101, 10,
            Uri.EscapeDataString("Ahmed Ali"), Uri.EscapeDataString("Tiger Sky Tower"), Uri.EscapeDataString("1205"));

        var result = model.OnPostUseCrmBuyerUnit(42, "+971501234567", 7, packed);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        var values = RouteValues(redirect);
        Assert.Equal("create", values["step"]);
        Assert.Equal(5001, values["crmBuyerCustomerId"]);
        Assert.Equal(901, values["crmBuyerLeadId"]);
        Assert.Equal(101, values["crmBuyerUnitId"]);
        Assert.Equal(10, values["crmBuyerProjectId"]);
        Assert.Equal("Ahmed Ali", values["crmBuyerCustomerName"]);
        Assert.Equal("Tiger Sky Tower", values["crmBuyerProjectName"]);
        Assert.Equal("1205", values["crmBuyerUnitNumber"]);
    }

    [Fact]
    public void OnPostUseCrmBuyerUnit_DifferentUnitSelected_CarriesThatUnitsOwnIdentifiers()
    {
        // A Buyer with multiple units must be able to carry forward whichever
        // specific unit the agent actually selected — never defaulting to
        // the first one.
        var (model, _, _, _, _, _, _) = CreateModel();
        var packed = string.Join(':', 5001, 902, 102, 10,
            Uri.EscapeDataString("Ahmed Ali"), Uri.EscapeDataString("Tiger Sky Tower"), Uri.EscapeDataString("1403"));

        var result = model.OnPostUseCrmBuyerUnit(42, "+971501234567", null, packed);

        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal(102, values["crmBuyerUnitId"]);
        Assert.Equal("1403", values["crmBuyerUnitNumber"]);
    }

    [Fact]
    public void OnPostUseCrmBuyerUnit_NameContainingColon_SurvivesEscapingRoundTrip()
    {
        // Uri.EscapeDataString encodes a literal ':' as %3A, so splitting the
        // packed value on ':' is safe even when display text itself
        // contains one.
        var (model, _, _, _, _, _, _) = CreateModel();
        var packed = string.Join(':', 5001, 901, 101, 10,
            Uri.EscapeDataString("Ahmed: Ali"), Uri.EscapeDataString("Tower: Sky"), Uri.EscapeDataString("12:05"));

        var result = model.OnPostUseCrmBuyerUnit(42, "+971501234567", null, packed);

        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal("Ahmed: Ali", values["crmBuyerCustomerName"]);
        Assert.Equal("Tower: Sky", values["crmBuyerProjectName"]);
        Assert.Equal("12:05", values["crmBuyerUnitNumber"]);
    }

    [Fact]
    public void OnPostContinueWithoutMatch_ProceedsWithNoCrmBuyerSelected()
    {
        var (model, _, _, _, _, _, _) = CreateModel();

        var result = model.OnPostContinueWithoutMatch(42, "+9613040922", 7);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        var values = RouteValues(redirect);
        Assert.Equal("create", values["step"]);
        Assert.Equal(7, values["departmentId"]);
        Assert.False(values.ContainsKey("crmBuyerUnitId"));
    }

    // ---- Category dropdown (unaffected by the CRM Buyer Lookup rewiring) ----

    [Fact]
    public async Task OnGetAsync_CreateStep_WithDepartment_RequestsCategoriesFilteredByThatDepartment()
    {
        var (model, _, _, _, categories, _, _) = CreateModel(categoriesResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new[]
            {
                new CategoryDto(2, "Corrective Maintenance", 7, "Facilities Management")
            }));

        await model.OnGetAsync("create", 42, "+15551234567", 7, null, null, null, null, null, null, null, CancellationToken.None);

        var sent = Assert.Single(categories.Requests);
        Assert.Equal("http://localhost/api/categories?departmentId=7", sent.RequestUri);
        var single = Assert.Single(model.Categories);
        Assert.Equal("Corrective Maintenance", single.Name);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_CategoriesApiFails_SetsCategoriesErrorMessage_NoNumericFallback()
    {
        var (model, _, _, _, _, _, _) = CreateModel(categoriesResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        await model.OnGetAsync("create", 42, "+15551234567", null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.NotNull(model.CategoriesErrorMessage);
        Assert.Empty(model.Categories);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_CarriesCrmBuyerSelectionForward()
    {
        var (model, _, _, _, _, _, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));

        await model.OnGetAsync(
            "create", 42, "+971501234567", 2, 5001, 901, 101, 10, "Ahmed Ali", "Tiger Sky Tower", "1205", CancellationToken.None);

        Assert.Equal(5001, model.CrmBuyerCustomerId);
        Assert.Equal(101, model.CrmBuyerUnitId);
        Assert.Equal("Ahmed Ali", model.CrmBuyerCustomerName);
        Assert.Equal("Tiger Sky Tower", model.CrmBuyerProjectName);
        Assert.Equal("1205", model.CrmBuyerUnitNumber);
    }

    // ---- Customer History preview (Step 3): always the selected customer, never the first search result ----

    [Fact]
    public async Task OnGetAsync_CreateStep_LoadsPreviousTickets_ForTheSelectedCustomer_NotTheFirstSearchResult()
    {
        // Two buyers matched the phone search (5001 first, 5002 second) — the
        // agent selected the second one's unit, so the preview must query
        // history for 5002, never 5001.
        var (model, _, _, _, _, _, customerHistory) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")),
            customerHistoryResponder: (_, _) => FakeApiHandler.JsonResponse(
                HttpStatusCode.OK, new CustomerHistoryDto("Verified", 5002, null, "Ahmad Ali Hassan", 2, 1, 1,
                [new CustomerHistoryTicketDto(50, "TG-CS-20260810-0001", DateTime.UtcNow.AddDays(-5), "Closed", 3, 2, 2, "Tiger Sky Tower", "2004", "Verified")])));

        await model.OnGetAsync(
            "create", 42, "+971501234567", 2, 5002, 903, 103, 10, "Ahmad Ali Hassan", "Tiger Sky Tower", "2004", CancellationToken.None);

        var sent = Assert.Single(customerHistory.Requests);
        Assert.Contains("/api/customers/crm/5002/ticket-history", sent.RequestUri);
        Assert.NotNull(model.PreviousTickets);
        Assert.Equal(2, model.PreviousTickets!.TotalTickets);
        Assert.Equal(50, Assert.Single(model.PreviousTickets.Tickets).TicketId);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_NoCrmBuyerSelected_NeverCallsCustomerHistoryApi()
    {
        var (model, _, _, _, _, _, customerHistory) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));

        await model.OnGetAsync("create", 42, "+9613040922", 2, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Empty(customerHistory.Requests);
        Assert.Null(model.PreviousTickets);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_LimitsThePreviewToFiveTickets()
    {
        var (model, _, _, _, _, _, customerHistory) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));

        await model.OnGetAsync(
            "create", 42, "+971501234567", 2, 5001, 901, 101, 10, "Ahmed Ali", "Tiger Sky Tower", "1205", CancellationToken.None);

        var sent = Assert.Single(customerHistory.Requests);
        Assert.Contains("limit=5", sent.RequestUri);
    }

    // ---- Priority required in Step 3 ----

    [Fact]
    public void CreateStepInput_PriorityId_IsNullableAndRequired()
    {
        var property = typeof(TigerCsWeb::TigerCS.Web.Pages.NewTicketModel.CreateStepInput).GetProperty("PriorityId");
        Assert.NotNull(property);
        Assert.Equal(typeof(byte?), property!.PropertyType);

        var input = new NewTicketModel.CreateStepInput { CategoryId = 1, PriorityId = null, RequestSummary = "x" };
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(NewTicketModel.CreateStepInput.PriorityId)));
    }

    [Fact]
    public async Task OnPostCreateAsync_NoPrioritySelected_RejectedWithoutCallingTheTicketsApi()
    {
        var (model, _, _, _, categories, _, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = 2, PriorityId = null, RequestSummary = "Summary",
            ManualProjectName = "Tiger Sky Tower", ManualUnitNumber = "1205"
        };

        var result = await model.OnPostCreateAsync(42, "+15551234567", null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
        Assert.Single(categories.Requests); // categories reloaded to redisplay the dropdown
    }

    // ---- Project/Unit Number required when no CRM Buyer unit was selected ----

    [Fact]
    public async Task OnPostCreateAsync_NoCrmMatch_ManualProjectMissing_RejectedWithoutCallingTheTicketsApi()
    {
        var (model, _, _, _, categories, _, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = 2, PriorityId = 3, RequestSummary = "Summary",
            ManualProjectName = null, ManualUnitNumber = "1205"
        };

        var result = await model.OnPostCreateAsync(42, "+9613040922", null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Customer not found in CRM. Project and Unit Number are required.", model.ErrorMessage);
        Assert.Single(categories.Requests);
    }

    [Fact]
    public async Task OnPostCreateAsync_NoCrmMatch_ManualUnitNumberMissing_RejectedWithoutCallingTheTicketsApi()
    {
        var (model, _, _, _, categories, _, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = 2, PriorityId = 3, RequestSummary = "Summary",
            ManualProjectName = "Tiger Sky Tower", ManualUnitNumber = null
        };

        var result = await model.OnPostCreateAsync(42, "+9613040922", null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Customer not found in CRM. Project and Unit Number are required.", model.ErrorMessage);
        Assert.Single(categories.Requests);
    }

    [Fact]
    public async Task OnPostCreateAsync_NoCrmMatch_BothManualFieldsSupplied_CreatesTicket_NeverRunningAnotherCrmLookup()
    {
        var (model, _, crmBuyerLookup, _, _, tickets, _) = CreateModel(ticketsResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                100, "TG-CS-20260827-0001", 7, 7, null, null, 2, 3, "Open", "Unverified", "None", "Running", "Summary", DateTime.UtcNow, "AAAA")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = 2, PriorityId = 3, RequestSummary = "Summary",
            ManualProjectName = "Tiger Sky Tower", ManualUnitNumber = "1205"
        };

        var result = await model.OnPostCreateAsync(42, "+9613040922", null, null, null, null, null, null, null, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/TicketDetails", redirect.PageName);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.Equal("Tiger Sky Tower", body.RootElement.GetProperty("manualProjectName").GetString());
        Assert.Equal("1205", body.RootElement.GetProperty("manualUnitNumber").GetString());
        Assert.True(body.RootElement.GetProperty("crmBuyerUnitId").ValueKind == JsonValueKind.Null);
        // The Project/Unit Number values manually entered here must never
        // trigger another CRM search — the lookup handler was never invoked.
        Assert.Empty(crmBuyerLookup.Requests);
    }

    // ---- Selected CRM Buyer identifiers flow into ticket creation ----

    [Fact]
    public async Task OnPostCreateAsync_CrmMatchSelected_SendsAllFourCrmIdsAndSnapshot_NoManualFieldsRequired()
    {
        var (model, _, _, _, _, tickets, _) = CreateModel(ticketsResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                100, "TG-CS-20260827-0001", 7, 7, null, null, 2, 3, "Open", "Verified", "None", "Running", "Summary", DateTime.UtcNow, "AAAA")));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 3, RequestSummary = "Summary" };

        var result = await model.OnPostCreateAsync(
            42, "+971501234567", null, 5001, 901, 101, 10, "Ahmed Ali", "Tiger Sky Tower", "1205", CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.Equal(5001, body.RootElement.GetProperty("crmBuyerCustomerId").GetInt32());
        Assert.Equal(901, body.RootElement.GetProperty("crmBuyerLeadId").GetInt32());
        Assert.Equal(101, body.RootElement.GetProperty("crmBuyerUnitId").GetInt32());
        Assert.Equal(10, body.RootElement.GetProperty("crmBuyerProjectId").GetInt32());
        Assert.Equal("Ahmed Ali", body.RootElement.GetProperty("crmBuyerCustomerName").GetString());
        Assert.Equal("Tiger Sky Tower", body.RootElement.GetProperty("crmBuyerProjectName").GetString());
        Assert.Equal("1205", body.RootElement.GetProperty("crmBuyerUnitNumber").GetString());
        Assert.True(body.RootElement.GetProperty("manualProjectName").ValueKind == JsonValueKind.Null);
        Assert.True(body.RootElement.GetProperty("manualUnitNumber").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task OnPostCreateAsync_NoCategorySelected_RejectedWithoutCallingTheTicketsApi()
    {
        var (model, _, _, _, categories, _, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = null, PriorityId = 3, RequestSummary = "Summary",
            ManualProjectName = "Tiger Sky Tower", ManualUnitNumber = "1205"
        };

        var result = await model.OnPostCreateAsync(42, "+9613040922", null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
        Assert.Single(categories.Requests);
    }

    // ---- End-to-end: CRM match found, selected, and carried into ticket creation ----

    [Fact]
    public async Task FullFlow_IntakeLookupSelectCrmBuyerUnitCreate_StillSucceeds()
    {
        var (model, _, _, _, _, tickets, _) = CreateModel(
            intakeResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.Created, new IntakeRecordResponseDto(
                42, "Phone", DateTime.UtcNow, "+971501234567", 2, false, null, null, "Unverified", null)),
            crmBuyerLookupResponder: CrmBuyersFound(
                SingleUnitBuyer(5001, "Ahmed Ali", "+971501234567", 901, 101, 10, "1205", "Tiger Sky Tower")),
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")),
            ticketsResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                200, "TG-FM-20260827-0001", 2, 2, null, null, 2, 3, "Open", "Verified", "None", "Running", "x", DateTime.UtcNow, "AAAA")));

        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+971501234567", DepartmentId = 2 };
        var intakeResult = await model.OnPostIntakeAsync(CancellationToken.None);
        var lookupRoute = RouteValues(Assert.IsType<RedirectToPageResult>(intakeResult));

        await model.OnGetAsync(
            "lookup", (long)lookupRoute["intakeRecordId"]!, (string?)lookupRoute["phoneNumber"], (int?)lookupRoute["departmentId"],
            null, null, null, null, null, null, null, CancellationToken.None);
        var match = Assert.Single(model.CrmBuyerMatches!);
        var unit = Assert.Single(match.Units);
        var packed = string.Join(':', match.Customer.CustomerId, unit.LeadId, unit.UnitId, unit.ProjectId,
            Uri.EscapeDataString(match.Customer.FullNameEnglish!), Uri.EscapeDataString(unit.ProjectName!), Uri.EscapeDataString(unit.UnitNumber!));

        var selectResult = model.OnPostUseCrmBuyerUnit(42, "+971501234567", 2, packed);
        var createRoute = RouteValues(Assert.IsType<RedirectToPageResult>(selectResult));

        await model.OnGetAsync(
            "create", 42, "+971501234567", (int?)createRoute["departmentId"],
            (int?)createRoute["crmBuyerCustomerId"], (int?)createRoute["crmBuyerLeadId"], (int?)createRoute["crmBuyerUnitId"],
            (int?)createRoute["crmBuyerProjectId"], (string?)createRoute["crmBuyerCustomerName"],
            (string?)createRoute["crmBuyerProjectName"], (string?)createRoute["crmBuyerUnitNumber"], CancellationToken.None);
        Assert.Single(model.Categories);

        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = model.Categories.Single().CategoryId, PriorityId = 3, RequestSummary = "x" };
        var createResult = await model.OnPostCreateAsync(
            42, "+971501234567", 2,
            (int?)createRoute["crmBuyerCustomerId"], (int?)createRoute["crmBuyerLeadId"], (int?)createRoute["crmBuyerUnitId"],
            (int?)createRoute["crmBuyerProjectId"], (string?)createRoute["crmBuyerCustomerName"],
            (string?)createRoute["crmBuyerProjectName"], (string?)createRoute["crmBuyerUnitNumber"], CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(createResult);
        Assert.Equal("/TicketDetails", redirect.PageName);
        var sent = Assert.Single(tickets.Requests);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal(101, body.RootElement.GetProperty("crmBuyerUnitId").GetInt32());
    }

    // ---- End-to-end: no CRM match, manual Project/Unit Number carries the ticket through ----

    [Fact]
    public async Task FullFlow_IntakeLookupNoMatchContinueManualProjectUnit_StillSucceeds()
    {
        var (model, _, _, _, _, tickets, _) = CreateModel(
            intakeResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.Created, new IntakeRecordResponseDto(
                42, "Phone", DateTime.UtcNow, "+9613040922", 2, false, null, null, "Unverified", null)),
            crmBuyerLookupResponder: (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound),
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")),
            ticketsResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                201, "TG-FM-20260827-0002", 2, 2, null, null, 2, 3, "Open", "Unverified", "None", "Running", "x", DateTime.UtcNow, "AAAA")));

        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+9613040922", DepartmentId = 2 };
        var intakeResult = await model.OnPostIntakeAsync(CancellationToken.None);
        var lookupRoute = RouteValues(Assert.IsType<RedirectToPageResult>(intakeResult));

        await model.OnGetAsync(
            "lookup", (long)lookupRoute["intakeRecordId"]!, (string?)lookupRoute["phoneNumber"], (int?)lookupRoute["departmentId"],
            null, null, null, null, null, null, null, CancellationToken.None);
        Assert.Empty(model.CrmBuyerMatches!);

        var continueResult = model.OnPostContinueWithoutMatch(42, "+9613040922", 2);
        var createRoute = RouteValues(Assert.IsType<RedirectToPageResult>(continueResult));

        await model.OnGetAsync("create", 42, "+9613040922", (int?)createRoute["departmentId"], null, null, null, null, null, null, null, CancellationToken.None);
        Assert.Single(model.Categories);

        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = model.Categories.Single().CategoryId, PriorityId = 3, RequestSummary = "x",
            ManualProjectName = "Tiger Tower A", ManualUnitNumber = "1204"
        };
        var createResult = await model.OnPostCreateAsync(42, "+9613040922", 2, null, null, null, null, null, null, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(createResult);
        Assert.Equal("/TicketDetails", redirect.PageName);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.Equal("Tiger Tower A", body.RootElement.GetProperty("manualProjectName").GetString());
        Assert.Equal("1204", body.RootElement.GetProperty("manualUnitNumber").GetString());
    }

    private static IDictionary<string, object?> RouteValues(RedirectToPageResult redirect) =>
        redirect.RouteValues is null
            ? new Dictionary<string, object?>()
            : redirect.RouteValues.ToDictionary(kv => kv.Key, kv => kv.Value);

    // ---- TigerCS.Web -> TigerCS.Api integration failure modes: every one must
    // land as a controlled ErrorMessage/DepartmentsErrorMessage, never an
    // unhandled exception, and — for the outcomes an agent can act on — with
    // wording more specific than a bare generic fallback. ----

    [Fact]
    public async Task OnGetAsync_IntakeStep_DepartmentsApi401_SetsPredictableAuthMessage_NotGenericFallback()
    {
        // An empty-bodied 401 (the default ASP.NET Core auth challenge response,
        // no ProblemDetails "detail"/"title") is exactly what a missing/expired
        // bearer token from TigerCS.Web produces against a protected endpoint.
        var (model, _, _, _, _, _, _) = CreateModel(departmentsResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Empty(model.Departments);
        Assert.NotNull(model.DepartmentsErrorMessage);
        Assert.Contains("not authorized", model.DepartmentsErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnGetAsync_IntakeStep_DepartmentsApi403_SetsPredictableAuthMessage_NotGenericFallback()
    {
        var (model, _, _, _, _, _, _) = CreateModel(departmentsResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.Forbidden));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Empty(model.Departments);
        Assert.NotNull(model.DepartmentsErrorMessage);
        Assert.Contains("not authorized", model.DepartmentsErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnGetAsync_IntakeStep_DepartmentsApiUnreachable_SetsControlledFailureMessage_NoUnhandledException()
    {
        // Simulates TigerCS.Web pointed at an address nothing is listening on
        // (the actual root cause of "Unable to load the department list") —
        // HttpClient surfaces this as HttpRequestException, never a status code.
        var (model, _, _, _, _, _, _) = CreateModel(departmentsResponder: (_, _) =>
            throw new HttpRequestException("Connection refused"));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Empty(model.Departments);
        Assert.NotNull(model.DepartmentsErrorMessage);
    }

    [Fact]
    public async Task OnPostIntakeAsync_ValidationError_SurfacesApiDetail_NotAGenericMessage()
    {
        // A 400 from POST /api/intake-records carries a ProblemDetails body —
        // its "detail" must reach the page verbatim rather than being masked
        // by the page's own generic "Could not record this interaction." text.
        var (model, _, _, departments, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.BadRequest, new { detail = "Customer phone number is invalid." }));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "not-a-phone-number" };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Customer phone number is invalid.", model.ErrorMessage);
        Assert.Single(departments.Requests); // Step 1 redisplays with the department dropdown reloaded
    }

    [Fact]
    public async Task OnPostIntakeAsync_ApiUnreachable_SetsControlledFailureMessage_NoUnhandledException()
    {
        var (model, _, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            throw new HttpRequestException("No connection could be made because the target machine actively refused it."));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567" };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostIntakeAsync_ApiUnauthorized_SetsPredictableAuthMessage_NotGenericFallback()
    {
        var (model, _, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567" };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
        Assert.Contains("not authorized", model.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
