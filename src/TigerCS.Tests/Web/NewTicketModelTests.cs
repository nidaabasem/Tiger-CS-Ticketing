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
/// The wizard's Step 2 is department-aware: the generic
/// <c>CustomerLookupApiClient</c> response decides which sources participate
/// (per <c>DepartmentCustomerLookupSources</c>) and carries the PACT/Tasleeh
/// results; the real CRM Buyer Lookup endpoint still performs the CRM leg
/// itself, phone-number-only, exactly as before — see the "Department-aware
/// customer lookup" section below and <c>NewTicketModel</c>'s own remarks.
/// </para>
/// </summary>
public sealed class NewTicketModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static (NewTicketModel Model, FakeApiHandler Intake, FakeApiHandler CrmBuyerLookup, FakeApiHandler Departments, FakeApiHandler Categories, FakeApiHandler Tickets, FakeApiHandler CustomerHistory, FakeApiHandler CustomerLookup) CreateModel(
        Func<HttpRequestMessage, string?, HttpResponseMessage>? intakeResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? crmBuyerLookupResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? departmentsResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? categoriesResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? ticketsResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? customerHistoryResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? customerLookupResponder = null)
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
        // Department-aware customer lookup default: the no-department shape —
        // all three sources searched, nothing found — so every pre-existing
        // CRM-focused test keeps its old behavior (Crm participates, the real
        // CRM Buyer Lookup runs) without supplying a responder.
        var customerLookupHandler = new FakeApiHandler(customerLookupResponder ?? (
            (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+", [
                CustomerLookupSourceResultDto.NotFound("Crm"),
                CustomerLookupSourceResultDto.NotFound("Pact"),
                CustomerLookupSourceResultDto.NotFound("Tasleeh")]))));

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
        var customerLookupClient = new CustomerLookupApiClient(
            new HttpClient(customerLookupHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<CustomerLookupApiClient>.Instance);

        var model = new NewTicketModel(intakeClient, customerLookupClient, crmBuyerLookupClient, departmentsClient, categoriesClient, ticketsClient, customerHistoryClient);
        return (model, intakeHandler, crmBuyerLookupHandler, departmentsHandler, categoriesHandler, ticketsHandler, customerHistoryHandler, customerLookupHandler);
    }

    private static Func<HttpRequestMessage, string?, HttpResponseMessage> CustomerLookupReturning(params CustomerLookupSourceResultDto[] sources) =>
        (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+", sources));

    private static CustomerLookupCustomerDto PactCustomerWithTwoUnits() => new(
        "7001", "Fatima Noor", "+971509990002", "fatima@example.test", CustomerType: "2",
        [
            new CustomerLookupUnitDto("700", "2304", "Tiger Marina Residences", null, "Residential", null, null),
            new CustomerLookupUnitDto("701", "1105", "Tiger Bay Towers", null, "Commercial", null, null)
        ]);

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

    // ---- A leading '+' is a valid phone number and is preserved exactly as
    // entered — no layer of the New Ticket flow may reject or reformat it;
    // only the PACT gateway (NormalizePactPhone) transforms it, PACT-side. ----

    [Theory]
    [InlineData("+971501234567")]
    [InlineData("971501234567")]
    public void IntakeInput_PhoneNumber_AcceptsPlusPrefixedAndPlainDigits(string phoneNumber)
    {
        var input = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = phoneNumber };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Fact]
    public void IntakeInput_PhoneNumber_CarriesNoFormatRestrictingAnnotations()
    {
        // The phone is a free-form string: no [RegularExpression], no
        // [Phone], no [DataType], no [StringLength] may creep in — any of
        // them could reject the leading '+' or drive the input tag helper
        // to a restrictive HTML type.
        var property = typeof(NewTicketModel.IntakeInput).GetProperty(nameof(NewTicketModel.IntakeInput.PhoneNumber))!;

        Assert.Equal(typeof(string), property.PropertyType);
        var attributeNames = property.GetCustomAttributes(inherit: true).Select(a => a.GetType().Name).ToList();
        Assert.Equal(["RequiredAttribute"], attributeNames);
    }

    [Fact]
    public async Task OnPostIntakeAsync_PlusPrefixedPhone_SentVerbatimToIntakeApi_AndCarriedVerbatimToLookupStep()
    {
        var (model, intake, _, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new IntakeRecordResponseDto(
                42, "Phone", DateTime.UtcNow, "+971501234567", 7, false, null, null, "Unverified", null)));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+971501234567", DepartmentId = 7 };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        // The '+' survives into the API request body exactly as entered…
        using var body = JsonDocument.Parse(Assert.Single(intake.Requests).Body!);
        Assert.Equal("+971501234567", body.RootElement.GetProperty("phoneNumber").GetString());
        // …and into the step-2 carry-forward unchanged (RedirectToPage
        // percent-encodes '+' as %2B in the generated URL, so it binds back
        // intact — verified empirically; the route value here is the raw one).
        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal("+971501234567", values["phoneNumber"]);
    }

    [Fact]
    public async Task OnGetAsync_Lookup_PlainDigitsPhone_StillSearchesNormally()
    {
        var (model, _, crmBuyerLookup, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound());

        await model.OnGetAsync("lookup", 42, "971501234567", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        var sent = Assert.Single(crmBuyerLookup.Requests);
        Assert.Equal("http://localhost/api/crm/buyers?phoneNumber=971501234567", sent.RequestUri);
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
        var (model, _, _, departments, _, _, _, _) = CreateModel(departmentsResponder: DepartmentsReturning(
            new DepartmentDto(7, "Facilities Management"),
            new DepartmentDto(2, "Customer Service")));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Single(departments.Requests);
        Assert.Equal(2, model.Departments.Count);
        Assert.Contains(model.Departments, d => d is { DepartmentId: 7, Name: "Facilities Management" });
    }

    [Fact]
    public async Task OnGetAsync_IntakeStep_WithCarriedPhoneNumber_PrefillsTheIntakeForm_ExactlyAsGiven()
    {
        // Customer Workspace carry-forward: "+ New Ticket" from a selected
        // customer must not make the agent re-type or re-search the same
        // customer — the phone arrives via the query string and lands in the
        // intake form unmodified ('+' preserved), still fully editable.
        var (model, _, _, _, _, _, _, _) = CreateModel();

        await model.OnGetAsync(null, null, "+971501112233", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Equal("intake", model.Step);
        Assert.Equal("+971501112233", model.Intake.PhoneNumber);
    }

    [Fact]
    public async Task OnPostIntakeAsync_Failure_ReloadsDepartmentDirectory_ForRedisplay()
    {
        var (model, _, _, departments, _, _, _, _) = CreateModel(
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
        var (model, intake, _, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
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
        var (model, _, _, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
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
        var (model, _, crmBuyerLookup, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound());

        await model.OnGetAsync("lookup", 42, "+9613040922", null, null, null, null, null, null, null, null, null, CancellationToken.None);

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
        var (model, _, crmBuyerLookup, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound());

        await model.OnGetAsync("lookup", 42, "+9613040922", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        var sent = Assert.Single(crmBuyerLookup.Requests);
        Assert.DoesNotContain("unitNumber", sent.RequestUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("project", sent.RequestUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unit=", sent.RequestUri, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Scenario 1: one buyer, one unit ----

    [Fact]
    public async Task OnGetAsync_Lookup_OneBuyerOneUnit_PopulatesCrmBuyerMatch()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound(
            SingleUnitBuyer(5001, "Sami Nasser", "+971509990001", leadId: 900, unitId: 100, projectId: 10, unitNumber: "5001", projectName: "Tiger Sky Tower")));

        await model.OnGetAsync("lookup", 42, "+971509990001", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.False(model.CrmBuyerLookupUnavailable);
        var match = model.CrmBuyerMatch;
        Assert.NotNull(match);
        Assert.Equal("Sami Nasser", match!.Customer.FullNameEnglish);
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
        var (model, _, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound(buyer));

        await model.OnGetAsync("lookup", 42, "+971501234567", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        var match = model.CrmBuyerMatch;
        Assert.NotNull(match);
        Assert.Equal(2, match!.Units.Count);
        Assert.Contains(match.Units, u => u is { UnitNumber: "1205", LeadStatus: 8 });
        Assert.Contains(match.Units, u => u is { UnitNumber: "1403", LeadStatus: 9 });
        // Nothing on the model itself picks a unit — CrmBuyerUnitId stays
        // unset until the agent explicitly posts OnPostUseCrmBuyerUnit.
        Assert.Null(model.CrmBuyerUnitId);
    }

    // ---- Scenario 3: business rule — a CRM phone number belongs to exactly
    // one customer. TigerCS.Api's CrmBuyerLookupAppService never actually
    // answers 200 OK with more than one distinct customer any more — that
    // case is now a 409 Conflict (see Scenario 3b below) — so the "take the
    // first" here is defense-in-depth only ("never trust a contract further
    // than the wire"), not a real path this Api produces. ----

    [Fact]
    public async Task OnGetAsync_Lookup_ApiUnexpectedlyReturnsMultipleCustomers_WebLayerDefensivelyUsesTheFirstOnly()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound(
            SingleUnitBuyer(5001, "Ahmed Ali", "+971501234567", 901, 101, 10, "1205", "Tiger Sky Tower"),
            SingleUnitBuyer(5002, "Ahmad Ali Hassan", "+971501234567", 903, 103, 10, "2004", "Tiger Sky Tower")));

        await model.OnGetAsync("lookup", 42, "+971501234567", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.NotNull(model.CrmBuyerMatch);
        Assert.Equal(5001, model.CrmBuyerMatch!.Customer.CustomerId);
        Assert.Null(model.CrmBuyerCustomerId);
    }

    // ---- Scenario 3b: CRM data-integrity conflict — TigerCS.Api answers 409
    // (CrmBuyerLookupAppService's AmbiguousCustomerMatch outcome) when CRM
    // itself named more than one distinct customer for this phone number.
    // Never auto-selects a customer/unit; the wizard still isn't blocked. ----

    [Fact]
    public async Task OnGetAsync_Lookup_CrmAmbiguousCustomerMatch_FlagsAmbiguous_NeverAutoSelectsACustomer()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.Conflict));

        await model.OnGetAsync("lookup", 42, "+971501234567", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.True(model.CrmBuyerAmbiguousMatch);
        Assert.Null(model.CrmBuyerMatch);
        Assert.False(model.CrmBuyerLookupUnavailable); // a distinct condition from "CRM unavailable" — different message
        Assert.Null(model.ErrorMessage); // never a blocking error — the wizard must remain usable
    }

    [Fact]
    public async Task FullFlow_CrmAmbiguousCustomerMatch_TicketCreationStillSucceeds_ViaManualProjectUnitNumber()
    {
        // Item 4: even after an ambiguous CRM data-integrity conflict, the
        // agent can still continue the wizard through the existing
        // unverified/manual Project+Unit-Number path — exactly like a plain
        // "not found" — and ticket creation is never blocked.
        var (model, _, _, _, _, tickets, _, _) = CreateModel(
            crmBuyerLookupResponder: (_, _) => new HttpResponseMessage(HttpStatusCode.Conflict),
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")),
            ticketsResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                301, "TG-FM-20260827-0003", 2, 2, null, null, 2, 3, "Open", "Unverified", "None", "Running", "x", DateTime.UtcNow, "AAAA")));

        await model.OnGetAsync("lookup", 42, "+971501234567", 2, null, null, null, null, null, null, null, null, CancellationToken.None);
        Assert.True(model.CrmBuyerAmbiguousMatch);

        var continueResult = model.OnPostContinueWithoutMatch(42, "+971501234567", 2);
        var createRoute = RouteValues(Assert.IsType<RedirectToPageResult>(continueResult));

        await model.OnGetAsync("create", 42, "+971501234567", (int?)createRoute["departmentId"], null, null, null, null, null, null, null, null, CancellationToken.None);

        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = model.Categories.Single().CategoryId, PriorityId = 3, RequestSummary = "x",
            ManualProjectName = "Tiger Tower A", ManualUnitNumber = "1204"
        };
        var createResult = await model.OnPostCreateAsync(42, "+971501234567", 2, null, null, null, null, null, null, null, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(createResult);
        Assert.Equal("/TicketDetails", redirect.PageName);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.Equal("Tiger Tower A", body.RootElement.GetProperty("manualProjectName").GetString());
        Assert.Equal("1204", body.RootElement.GetProperty("manualUnitNumber").GetString());
        Assert.True(body.RootElement.GetProperty("crmBuyerCustomerId").ValueKind == JsonValueKind.Null);
    }

    // ---- Scenario 4: no CRM match ----

    [Fact]
    public async Task OnGetAsync_Lookup_NoCrmMatch_EmptyMatches_NotFlaggedUnavailable()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        await model.OnGetAsync("lookup", 42, "+9613040922", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Null(model.CrmBuyerMatch);
        Assert.False(model.CrmBuyerLookupUnavailable);
    }

    // ---- Scenario 5: CRM unavailable — never blocks the wizard ----

    [Fact]
    public async Task OnGetAsync_Lookup_CrmUnavailable_EmptyMatches_FlaggedUnavailable()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        await model.OnGetAsync("lookup", 42, "+9613040922", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Null(model.CrmBuyerMatch);
        Assert.True(model.CrmBuyerLookupUnavailable);
        Assert.Null(model.ErrorMessage); // never a blocking error — the wizard must remain usable
    }

    [Fact]
    public async Task OnGetAsync_Lookup_CrmNetworkUnreachable_TreatedAsUnavailable_NeverThrows()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: (_, _) =>
            throw new HttpRequestException("Connection refused"));

        await model.OnGetAsync("lookup", 42, "+9613040922", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Null(model.CrmBuyerMatch);
        Assert.True(model.CrmBuyerLookupUnavailable);
    }

    // ---- Selecting a CRM Buyer unit carries every identifier + display snapshot forward ----

    [Fact]
    public void OnPostUseCrmBuyerUnit_CarriesAllFourCrmIdsAndSnapshotText_ToCreateStep()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel();
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
        var (model, _, _, _, _, _, _, _) = CreateModel();
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
        var (model, _, _, _, _, _, _, _) = CreateModel();
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
        var (model, _, _, _, _, _, _, _) = CreateModel();

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
        var (model, _, _, _, categories, _, _, _) = CreateModel(categoriesResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new[]
            {
                new CategoryDto(2, "Corrective Maintenance", 7, "Facilities Management")
            }));

        await model.OnGetAsync("create", 42, "+15551234567", 7, null, null, null, null, null, null, null, null, CancellationToken.None);

        var sent = Assert.Single(categories.Requests);
        Assert.Equal("http://localhost/api/categories?departmentId=7", sent.RequestUri);
        var single = Assert.Single(model.Categories);
        Assert.Equal("Corrective Maintenance", single.Name);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_CategoriesApiFails_SetsCategoriesErrorMessage_NoNumericFallback()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(categoriesResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        await model.OnGetAsync("create", 42, "+15551234567", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.NotNull(model.CategoriesErrorMessage);
        Assert.Empty(model.Categories);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_CarriesCrmBuyerSelectionForward()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));

        await model.OnGetAsync(
            "create", 42, "+971501234567", 2, 5001, 901, 101, 10, "Ahmed Ali", "Tiger Sky Tower", "1205", null, CancellationToken.None);

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
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")),
            customerHistoryResponder: (_, _) => FakeApiHandler.JsonResponse(
                HttpStatusCode.OK, new CustomerHistoryDto("Verified", 5002, null, "Ahmad Ali Hassan", 2, 1, 1,
                [new CustomerHistoryTicketDto(50, "TG-CS-20260810-0001", DateTime.UtcNow.AddDays(-5), "Closed", 3, 2, 2, "Tiger Sky Tower", "2004", "Verified")])));

        await model.OnGetAsync(
            "create", 42, "+971501234567", 2, 5002, 903, 103, 10, "Ahmad Ali Hassan", "Tiger Sky Tower", "2004", null, CancellationToken.None);

        // Two fixed calls per create-step render (never per-row): the
        // customer-wide Previous Tickets preview and Phase E's unit-scoped
        // related-tickets check — both keyed by the selected customer 5002.
        var sent = Assert.Single(customerHistory.Requests, r => !r.RequestUri.Contains("unitNumber"));
        Assert.Contains("/api/customers/crm/5002/ticket-history", sent.RequestUri);
        Assert.All(customerHistory.Requests, r => Assert.Contains("/api/customers/crm/5002/ticket-history", r.RequestUri));
        Assert.NotNull(model.PreviousTickets);
        Assert.Equal(2, model.PreviousTickets!.TotalTickets);
        Assert.Equal(50, Assert.Single(model.PreviousTickets.Tickets).TicketId);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_NoCrmBuyerSelected_NeverCallsCustomerHistoryApi()
    {
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));

        await model.OnGetAsync("create", 42, "+9613040922", 2, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Empty(customerHistory.Requests);
        Assert.Null(model.PreviousTickets);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_LimitsThePreviewToFiveTickets()
    {
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));

        await model.OnGetAsync(
            "create", 42, "+971501234567", 2, 5001, 901, 101, 10, "Ahmed Ali", "Tiger Sky Tower", "1205", null, CancellationToken.None);

        Assert.NotEmpty(customerHistory.Requests);
        Assert.All(customerHistory.Requests, r => Assert.Contains("limit=5", r.RequestUri));
    }

    // ---- Related tickets (Phase E, Step 3): same identity + same unit, advisory only ----

    [Fact]
    public async Task OnGetAsync_CreateStep_CrmSelection_RunsTheRelatedTicketsCheck_ForTheSelectedUnitActiveFirst()
    {
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")),
            customerHistoryResponder: (request, _) => request.RequestUri!.Query.Contains("unitNumber")
                ? FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerHistoryDto("Verified", 5002, null, "Ahmad Ali Hassan", 1, 1, 0,
                    [new CustomerHistoryTicketDto(61, "TG-CS-20260830-0002", DateTime.UtcNow.AddDays(-1), "InProgress", 2, 2, 2, "Tiger Sky Tower", "2004", "Verified", "Water leakage")]))
                : FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerHistoryDto("Verified", 5002, null, "Ahmad Ali Hassan", 0, 0, 0, [])));

        await model.OnGetAsync(
            "create", 42, "+971501234567", 2, 5002, 903, 103, 10, "Ahmad Ali Hassan", "Tiger Sky Tower", "2004", null, CancellationToken.None);

        // One scoped related-tickets query — same identity (5002), the
        // selected unit, active tickets first. Never a call per row.
        var related = Assert.Single(customerHistory.Requests, r => r.RequestUri.Contains("unitNumber"));
        Assert.Contains("/api/customers/crm/5002/ticket-history", related.RequestUri);
        Assert.Contains("unitNumber=2004", related.RequestUri);
        Assert.Contains("orderActiveFirst=true", related.RequestUri);
        Assert.Equal(2, customerHistory.Requests.Count);

        Assert.NotNull(model.RelatedTickets);
        var row = Assert.Single(model.RelatedTickets!.Tickets);
        Assert.Equal("Water leakage", row.RequestSummary);
        Assert.Equal("InProgress", row.TicketStatus);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_ExternalSelection_RunsTheRelatedTicketsCheck_ByThePersistedExternalIdentity()
    {
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));
        var externalSelection = string.Join(':', "Pact", "PACT-CUST-77", "PU-1", "Aisha Rahman", "Marina Heights", "1506");

        await model.OnGetAsync(
            "create", 42, "+971509990002", 2, null, null, null, null, null, null, null, externalSelection, CancellationToken.None);

        // The external identity pair keys the check — never the display name
        // and never a phone fallback; the previous-tickets preview (a
        // CRM-only feature) does not run at all here.
        var sent = Assert.Single(customerHistory.Requests);
        Assert.Contains("/api/customers/external/Pact/PACT-CUST-77/ticket-history", sent.RequestUri);
        Assert.Contains("unitNumber=1506", sent.RequestUri);
        Assert.Contains("orderActiveFirst=true", sent.RequestUri);
        Assert.NotNull(model.RelatedTickets);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_ManualOnlyEntry_RunsNoRelatedCheck_NoIdentityMeansNoAssociation()
    {
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));

        await model.OnGetAsync("create", 42, "+9613040922", 2, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Empty(customerHistory.Requests);
        Assert.Null(model.RelatedTickets);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_RelatedCheckFailure_NeverBlocksTheWizard()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")),
            customerHistoryResponder: (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));

        await model.OnGetAsync(
            "create", 42, "+971501234567", 2, 5002, 903, 103, 10, "Ahmad Ali Hassan", "Tiger Sky Tower", "2004", null, CancellationToken.None);

        Assert.Null(model.RelatedTickets);
        Assert.Null(model.ErrorMessage);
        Assert.Equal("create", model.Step);
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
        var (model, _, _, _, categories, _, _, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = 2, PriorityId = null, RequestSummary = "Summary",
            ManualProjectName = "Tiger Sky Tower", ManualUnitNumber = "1205"
        };

        var result = await model.OnPostCreateAsync(42, "+15551234567", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
        Assert.Single(categories.Requests); // categories reloaded to redisplay the dropdown
    }

    // ---- Project/Unit Number required when no CRM Buyer unit was selected ----

    [Fact]
    public async Task OnPostCreateAsync_NoCrmMatch_ManualProjectMissing_RejectedWithoutCallingTheTicketsApi()
    {
        var (model, _, _, _, categories, _, _, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = 2, PriorityId = 3, RequestSummary = "Summary",
            ManualProjectName = null, ManualUnitNumber = "1205"
        };

        var result = await model.OnPostCreateAsync(42, "+9613040922", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Customer not found in CRM. Project and Unit Number are required.", model.ErrorMessage);
        Assert.Single(categories.Requests);
    }

    [Fact]
    public async Task OnPostCreateAsync_NoCrmMatch_ManualUnitNumberMissing_RejectedWithoutCallingTheTicketsApi()
    {
        var (model, _, _, _, categories, _, _, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = 2, PriorityId = 3, RequestSummary = "Summary",
            ManualProjectName = "Tiger Sky Tower", ManualUnitNumber = null
        };

        var result = await model.OnPostCreateAsync(42, "+9613040922", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Customer not found in CRM. Project and Unit Number are required.", model.ErrorMessage);
        Assert.Single(categories.Requests);
    }

    [Fact]
    public async Task OnPostCreateAsync_NoCrmMatch_BothManualFieldsSupplied_CreatesTicket_NeverRunningAnotherCrmLookup()
    {
        var (model, _, crmBuyerLookup, _, _, tickets, _, _) = CreateModel(ticketsResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                100, "TG-CS-20260827-0001", 7, 7, null, null, 2, 3, "Open", "Unverified", "None", "Running", "Summary", DateTime.UtcNow, "AAAA")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = 2, PriorityId = 3, RequestSummary = "Summary",
            ManualProjectName = "Tiger Sky Tower", ManualUnitNumber = "1205"
        };

        var result = await model.OnPostCreateAsync(42, "+9613040922", null, null, null, null, null, null, null, null, null, CancellationToken.None);

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
        var (model, _, _, _, _, tickets, _, _) = CreateModel(ticketsResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                100, "TG-CS-20260827-0001", 7, 7, null, null, 2, 3, "Open", "Verified", "None", "Running", "Summary", DateTime.UtcNow, "AAAA")));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 3, RequestSummary = "Summary" };

        var result = await model.OnPostCreateAsync(
            42, "+971501234567", null, 5001, 901, 101, 10, "Ahmed Ali", "Tiger Sky Tower", "1205", null, CancellationToken.None);

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
        var (model, _, _, _, categories, _, _, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = null, PriorityId = 3, RequestSummary = "Summary",
            ManualProjectName = "Tiger Sky Tower", ManualUnitNumber = "1205"
        };

        var result = await model.OnPostCreateAsync(42, "+9613040922", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
        Assert.Single(categories.Requests);
    }

    // ---- End-to-end: CRM match found, selected, and carried into ticket creation ----

    [Fact]
    public async Task FullFlow_IntakeLookupSelectCrmBuyerUnitCreate_StillSucceeds()
    {
        var (model, _, _, _, _, tickets, _, _) = CreateModel(
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
            null, null, null, null, null, null, null, null, CancellationToken.None);
        var match = model.CrmBuyerMatch;
        Assert.NotNull(match);
        var unit = Assert.Single(match.Units);
        var packed = string.Join(':', match.Customer.CustomerId, unit.LeadId, unit.UnitId, unit.ProjectId,
            Uri.EscapeDataString(match.Customer.FullNameEnglish!), Uri.EscapeDataString(unit.ProjectName!), Uri.EscapeDataString(unit.UnitNumber!));

        var selectResult = model.OnPostUseCrmBuyerUnit(42, "+971501234567", 2, packed);
        var createRoute = RouteValues(Assert.IsType<RedirectToPageResult>(selectResult));

        await model.OnGetAsync(
            "create", 42, "+971501234567", (int?)createRoute["departmentId"],
            (int?)createRoute["crmBuyerCustomerId"], (int?)createRoute["crmBuyerLeadId"], (int?)createRoute["crmBuyerUnitId"],
            (int?)createRoute["crmBuyerProjectId"], (string?)createRoute["crmBuyerCustomerName"],
            (string?)createRoute["crmBuyerProjectName"], (string?)createRoute["crmBuyerUnitNumber"], null, CancellationToken.None);
        Assert.Single(model.Categories);

        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = model.Categories.Single().CategoryId, PriorityId = 3, RequestSummary = "x" };
        var createResult = await model.OnPostCreateAsync(
            42, "+971501234567", 2,
            (int?)createRoute["crmBuyerCustomerId"], (int?)createRoute["crmBuyerLeadId"], (int?)createRoute["crmBuyerUnitId"],
            (int?)createRoute["crmBuyerProjectId"], (string?)createRoute["crmBuyerCustomerName"],
            (string?)createRoute["crmBuyerProjectName"], (string?)createRoute["crmBuyerUnitNumber"], null, CancellationToken.None);

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
        var (model, _, _, _, _, tickets, _, _) = CreateModel(
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
            null, null, null, null, null, null, null, null, CancellationToken.None);
        Assert.Null(model.CrmBuyerMatch);

        var continueResult = model.OnPostContinueWithoutMatch(42, "+9613040922", 2);
        var createRoute = RouteValues(Assert.IsType<RedirectToPageResult>(continueResult));

        await model.OnGetAsync("create", 42, "+9613040922", (int?)createRoute["departmentId"], null, null, null, null, null, null, null, null, CancellationToken.None);
        Assert.Single(model.Categories);

        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = model.Categories.Single().CategoryId, PriorityId = 3, RequestSummary = "x",
            ManualProjectName = "Tiger Tower A", ManualUnitNumber = "1204"
        };
        var createResult = await model.OnPostCreateAsync(42, "+9613040922", 2, null, null, null, null, null, null, null, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(createResult);
        Assert.Equal("/TicketDetails", redirect.PageName);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.Equal("Tiger Tower A", body.RootElement.GetProperty("manualProjectName").GetString());
        Assert.Equal("1204", body.RootElement.GetProperty("manualUnitNumber").GetString());
    }

    // ---- Department-aware customer lookup: the generic response decides which
    // sources participate (per DepartmentCustomerLookupSources) and carries
    // PACT/Tasleeh results; the real CRM Buyer Lookup still performs the CRM
    // leg itself. ----

    [Fact]
    public async Task OnGetAsync_Lookup_CallsGenericCustomerLookup_ByIntakeRecordId()
    {
        var (model, _, _, _, _, _, _, customerLookup) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound());

        await model.OnGetAsync("lookup", 42, "+9613040922", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        var sent = Assert.Single(customerLookup.Requests);
        Assert.Equal("http://localhost/api/intake-records/42/customer-lookup", sent.RequestUri);
    }

    [Fact]
    public async Task OnGetAsync_Lookup_PactEnabledDepartment_ShowsPactResults_AllUnits_NoneAutoSelected_CrmNotSearched()
    {
        // Department scoped to Pact only — the response carries no Crm entry,
        // so the real CRM Buyer Lookup must not run (its default responder
        // throws if called), and the PACT customer's units all appear with
        // nothing pre-selected.
        var (model, _, crmBuyerLookup, _, _, _, _, _) = CreateModel(customerLookupResponder: CustomerLookupReturning(
            CustomerLookupSourceResultDto.Found("Pact", [PactCustomerWithTwoUnits()])));

        await model.OnGetAsync("lookup", 42, "+971509990002", 7, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.False(model.CrmParticipates);
        Assert.Empty(crmBuyerLookup.Requests);
        var pactSource = Assert.Single(model.ExternalLookupSources);
        Assert.Equal("Pact", pactSource.Source);
        Assert.Equal("Found", pactSource.Status);
        var customer = Assert.Single(pactSource.Customers);
        Assert.Equal("7001", customer.ExternalCustomerId);
        Assert.Equal(2, customer.Units.Count);
        Assert.Contains(customer.Units, u => u is { UnitNumber: "2304", PropertyName: "Tiger Marina Residences" });
        Assert.Contains(customer.Units, u => u is { UnitNumber: "1105", PropertyName: "Tiger Bay Towers" });
        // Nothing auto-selected: the external selection stays unset until the
        // agent explicitly posts OnPostUseExternalUnit.
        Assert.Null(model.ExternalSelection);
    }

    [Fact]
    public async Task OnGetAsync_Lookup_PactNotConfiguredForDepartment_ShowsNoPactResults_CrmStillSearched()
    {
        var (model, _, crmBuyerLookup, _, _, _, _, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(),
            customerLookupResponder: CustomerLookupReturning(CustomerLookupSourceResultDto.NotFound("Crm")));

        await model.OnGetAsync("lookup", 42, "+9613040922", 7, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.True(model.CrmParticipates);
        Assert.Single(crmBuyerLookup.Requests);
        Assert.Empty(model.ExternalLookupSources);
    }

    [Fact]
    public async Task OnGetAsync_Lookup_CrmAndPactBothConfigured_ResultsCoexist()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(
                SingleUnitBuyer(5001, "Sami Nasser", "+971509990002", 900, 100, 10, "5001", "Tiger Sky Tower")),
            customerLookupResponder: CustomerLookupReturning(
                CustomerLookupSourceResultDto.NotFound("Crm"),
                CustomerLookupSourceResultDto.Found("Pact", [PactCustomerWithTwoUnits()])));

        await model.OnGetAsync("lookup", 42, "+971509990002", 7, null, null, null, null, null, null, null, null, CancellationToken.None);

        // Both sources' results are shown together — the real CRM Buyer match
        // AND the PACT customer, neither hiding the other.
        Assert.NotNull(model.CrmBuyerMatch);
        Assert.Equal("Sami Nasser", model.CrmBuyerMatch!.Customer.FullNameEnglish);
        var pactSource = Assert.Single(model.ExternalLookupSources);
        Assert.Equal("Found", pactSource.Status);
        Assert.Equal(2, Assert.Single(pactSource.Customers).Units.Count);
    }

    [Fact]
    public async Task OnGetAsync_Lookup_PactFailed_DoesNotHideCrmResults_NeverBlocks()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(
                SingleUnitBuyer(5001, "Sami Nasser", "+971509990002", 900, 100, 10, "5001", "Tiger Sky Tower")),
            customerLookupResponder: CustomerLookupReturning(
                CustomerLookupSourceResultDto.NotFound("Crm"),
                CustomerLookupSourceResultDto.Failed("Pact")));

        await model.OnGetAsync("lookup", 42, "+971509990002", 7, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.NotNull(model.CrmBuyerMatch);
        var pactSource = Assert.Single(model.ExternalLookupSources);
        Assert.Equal("Failed", pactSource.Status);
        Assert.Null(model.ErrorMessage); // a failed source is a state on its card, never a blocking error
    }

    [Fact]
    public async Task OnGetAsync_Lookup_GenericLookupUnavailable_FailsOpenToCrmOnly_NeverBlocks()
    {
        var (model, _, crmBuyerLookup, _, _, _, _, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(),
            customerLookupResponder: (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));

        await model.OnGetAsync("lookup", 42, "+9613040922", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.True(model.CustomerLookupUnavailable);
        Assert.True(model.CrmParticipates); // fail-open: the real CRM lookup still ran
        Assert.Single(crmBuyerLookup.Requests);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnGetAsync_Lookup_DepartmentWithNoConfiguredSources_ShowsThatState_SearchesNothing()
    {
        var (model, _, crmBuyerLookup, _, _, _, _, _) = CreateModel(customerLookupResponder: CustomerLookupReturning());

        await model.OnGetAsync("lookup", 42, "+9613040922", 7, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.True(model.NoLookupSourcesConfigured);
        Assert.False(model.CrmParticipates);
        Assert.Empty(crmBuyerLookup.Requests);
        Assert.Empty(model.ExternalLookupSources);
    }

    [Fact]
    public void OnPostUseExternalUnit_CarriesTheSelectionToCreateStep()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel();
        var packed = string.Join(':',
            Uri.EscapeDataString("Pact"), Uri.EscapeDataString("7001"), Uri.EscapeDataString("701"),
            Uri.EscapeDataString("Fatima Noor"), Uri.EscapeDataString("Tiger Bay Towers"), Uri.EscapeDataString("1105"));

        var result = model.OnPostUseExternalUnit(42, "+971509990002", 7, packed);

        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal("create", values["step"]);
        Assert.Equal(packed, values["externalSelection"]);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_SecondPactUnitSelected_PrefillsManualFieldsFromThatUnit_NotTheFirst()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Tenancy Inquiry", 7, "Leasing")));
        // The agent picked the SECOND of the PACT customer's two units.
        var packed = string.Join(':',
            Uri.EscapeDataString("Pact"), Uri.EscapeDataString("7001"), Uri.EscapeDataString("701"),
            Uri.EscapeDataString("Fatima Noor"), Uri.EscapeDataString("Tiger Bay Towers"), Uri.EscapeDataString("1105"));

        await model.OnGetAsync("create", 42, "+971509990002", 7, null, null, null, null, null, null, null, packed, CancellationToken.None);

        Assert.Equal("Pact", model.ExternalSource);
        Assert.Equal("7001", model.ExternalCustomerId);
        Assert.Equal("701", model.ExternalUnitId);
        Assert.Equal("Fatima Noor", model.ExternalCustomerName);
        Assert.Equal("Tiger Bay Towers", model.CreateStep.ManualProjectName);
        Assert.Equal("1105", model.CreateStep.ManualUnitNumber);
    }

    [Fact]
    public async Task FullFlow_PactOnlyDepartment_SelectSecondPactUnit_TicketPersistsThatUnitViaManualSnapshot()
    {
        var (model, _, _, _, _, tickets, _, _) = CreateModel(
            customerLookupResponder: CustomerLookupReturning(
                CustomerLookupSourceResultDto.Found("Pact", [PactCustomerWithTwoUnits()])),
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Tenancy Inquiry", 7, "Leasing")),
            ticketsResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                400, "TG-LS-20260901-0001", 7, 7, null, null, 2, 3, "Open", "Unverified", "None", "Running", "x", DateTime.UtcNow, "AAAA")));

        await model.OnGetAsync("lookup", 42, "+971509990002", 7, null, null, null, null, null, null, null, null, CancellationToken.None);
        var pactCustomer = Assert.Single(Assert.Single(model.ExternalLookupSources).Customers);
        var secondUnit = pactCustomer.Units[1];

        var packed = string.Join(':',
            Uri.EscapeDataString("Pact"), Uri.EscapeDataString(pactCustomer.ExternalCustomerId),
            Uri.EscapeDataString(secondUnit.ExternalUnitId), Uri.EscapeDataString(pactCustomer.DisplayName!),
            Uri.EscapeDataString(secondUnit.PropertyName!), Uri.EscapeDataString(secondUnit.UnitNumber!));
        var selectResult = model.OnPostUseExternalUnit(42, "+971509990002", 7, packed);
        var createRoute = RouteValues(Assert.IsType<RedirectToPageResult>(selectResult));

        await model.OnGetAsync(
            "create", 42, "+971509990002", 7, null, null, null, null, null, null, null,
            (string?)createRoute["externalSelection"], CancellationToken.None);
        Assert.Equal("Tiger Bay Towers", model.CreateStep.ManualProjectName);

        model.CreateStep.CategoryId = model.Categories.Single().CategoryId;
        model.CreateStep.PriorityId = 3;
        model.CreateStep.RequestSummary = "AC fault";
        var createResult = await model.OnPostCreateAsync(
            42, "+971509990002", 7, null, null, null, null, null, null, null,
            (string?)createRoute["externalSelection"], CancellationToken.None);

        // The SELECTED (second) PACT unit persisted through the existing
        // manual Project/Unit snapshot — never the first unit, and never any
        // CRM identifier.
        Assert.Equal("/TicketDetails", Assert.IsType<RedirectToPageResult>(createResult).PageName);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.Equal("Tiger Bay Towers", body.RootElement.GetProperty("manualProjectName").GetString());
        Assert.Equal("1105", body.RootElement.GetProperty("manualUnitNumber").GetString());
        // ...and the generic external verification identity travels with it.
        Assert.Equal("Pact", body.RootElement.GetProperty("customerVerificationSource").GetString());
        Assert.Equal("7001", body.RootElement.GetProperty("externalCustomerId").GetString());
        Assert.Equal("701", body.RootElement.GetProperty("externalUnitId").GetString());
        Assert.True(body.RootElement.GetProperty("crmBuyerCustomerId").ValueKind == JsonValueKind.Null);
        Assert.True(body.RootElement.GetProperty("crmBuyerUnitId").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task FullFlow_PactUnavailable_ManualTicketCreationStillSucceeds()
    {
        var (model, _, _, _, _, tickets, _, _) = CreateModel(
            customerLookupResponder: CustomerLookupReturning(CustomerLookupSourceResultDto.Failed("Pact")),
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Tenancy Inquiry", 7, "Leasing")),
            ticketsResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                401, "TG-LS-20260901-0002", 7, 7, null, null, 2, 3, "Open", "Unverified", "None", "Running", "x", DateTime.UtcNow, "AAAA")));

        await model.OnGetAsync("lookup", 42, "+971509990002", 7, null, null, null, null, null, null, null, null, CancellationToken.None);
        Assert.Equal("Failed", Assert.Single(model.ExternalLookupSources).Status);

        var continueResult = model.OnPostContinueWithoutMatch(42, "+971509990002", 7);
        Assert.IsType<RedirectToPageResult>(continueResult);

        await model.OnGetAsync("create", 42, "+971509990002", 7, null, null, null, null, null, null, null, null, CancellationToken.None);
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = 2, PriorityId = 3, RequestSummary = "x",
            ManualProjectName = "Tiger Marina Residences", ManualUnitNumber = "0000"
        };
        var createResult = await model.OnPostCreateAsync(42, "+971509990002", 7, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Equal("/TicketDetails", Assert.IsType<RedirectToPageResult>(createResult).PageName);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.Equal("0000", body.RootElement.GetProperty("manualUnitNumber").GetString());
        // Plain manual entry is never presented as externally verified.
        Assert.True(body.RootElement.GetProperty("customerVerificationSource").ValueKind == JsonValueKind.Null);
        Assert.True(body.RootElement.GetProperty("externalCustomerId").ValueKind == JsonValueKind.Null);
        Assert.True(body.RootElement.GetProperty("externalUnitId").ValueKind == JsonValueKind.Null);
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
        var (model, _, _, _, _, _, _, _) = CreateModel(departmentsResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Empty(model.Departments);
        Assert.NotNull(model.DepartmentsErrorMessage);
        Assert.Contains("not authorized", model.DepartmentsErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnGetAsync_IntakeStep_DepartmentsApi403_SetsPredictableAuthMessage_NotGenericFallback()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(departmentsResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.Forbidden));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, null, null, null, null, CancellationToken.None);

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
        var (model, _, _, _, _, _, _, _) = CreateModel(departmentsResponder: (_, _) =>
            throw new HttpRequestException("Connection refused"));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Empty(model.Departments);
        Assert.NotNull(model.DepartmentsErrorMessage);
    }

    [Fact]
    public async Task OnPostIntakeAsync_ValidationError_SurfacesApiDetail_NotAGenericMessage()
    {
        // A 400 from POST /api/intake-records carries a ProblemDetails body —
        // its "detail" must reach the page verbatim rather than being masked
        // by the page's own generic "Could not record this interaction." text.
        var (model, _, _, departments, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
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
        var (model, _, _, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            throw new HttpRequestException("No connection could be made because the target machine actively refused it."));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567" };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostIntakeAsync_ApiUnauthorized_SetsPredictableAuthMessage_NotGenericFallback()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567" };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
        Assert.Contains("not authorized", model.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
