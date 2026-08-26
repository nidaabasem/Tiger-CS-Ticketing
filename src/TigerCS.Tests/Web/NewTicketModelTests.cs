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
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// Covers the New Ticket wizard's PageModel: Intake → Customer Lookup →
/// Category/Priority → Ticket, against TigerCS.Api's real DTO contracts with
/// <see cref="FakeApiHandler"/> standing in for the Api itself. No ASP.NET
/// Core host is spun up — like the app-service tests elsewhere in this
/// project, each handler is exercised directly against fakes at its one
/// real dependency boundary (here, HTTP).
/// </summary>
public sealed class NewTicketModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static (NewTicketModel Model, FakeApiHandler Intake, FakeApiHandler Lookup, FakeApiHandler Departments, FakeApiHandler Categories, FakeApiHandler Tickets) CreateModel(
        Func<HttpRequestMessage, string?, HttpResponseMessage>? intakeResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? lookupResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? departmentsResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? categoriesResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? ticketsResponder = null)
    {
        var intakeHandler = new FakeApiHandler(intakeResponder ?? ((_, _) => throw new InvalidOperationException("Intake API not expected to be called.")));
        var lookupHandler = new FakeApiHandler(lookupResponder ?? ((_, _) => throw new InvalidOperationException("Customer lookup API not expected to be called.")));
        var departmentsHandler = new FakeApiHandler(departmentsResponder ?? (
            (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, Array.Empty<DepartmentDto>())));
        var categoriesHandler = new FakeApiHandler(categoriesResponder ?? ((_, _) => throw new InvalidOperationException("Categories API not expected to be called.")));
        var ticketsHandler = new FakeApiHandler(ticketsResponder ?? ((_, _) => throw new InvalidOperationException("Tickets API not expected to be called.")));

        var intakeClient = new IntakeRecordsApiClient(
            new HttpClient(intakeHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<IntakeRecordsApiClient>.Instance);
        var lookupClient = new CustomerLookupApiClient(
            new HttpClient(lookupHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<CustomerLookupApiClient>.Instance);
        var departmentsClient = new DepartmentsApiClient(
            new HttpClient(departmentsHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<DepartmentsApiClient>.Instance);
        var categoriesClient = new CategoriesApiClient(
            new HttpClient(categoriesHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<CategoriesApiClient>.Instance);
        var ticketsClient = new TicketsApiClient(
            new HttpClient(ticketsHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<TicketsApiClient>.Instance);

        var model = new NewTicketModel(intakeClient, lookupClient, departmentsClient, categoriesClient, ticketsClient);
        return (model, intakeHandler, lookupHandler, departmentsHandler, categoriesHandler, ticketsHandler);
    }

    private static Func<HttpRequestMessage, string?, HttpResponseMessage> CategoriesReturning(params CategoryDto[] categories) =>
        (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, categories);

    private static Func<HttpRequestMessage, string?, HttpResponseMessage> DepartmentsReturning(params DepartmentDto[] departments) =>
        (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, departments);

    // ---- 1 & 2: PhoneNumber required, DepartmentId optional ----

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

    // ---- Department is a real, existing DepartmentId — never manually typed ----

    [Fact]
    public void IntakeInput_HasNoIsUnitRelatedOrRawUnitNumberOrPriorityHintFields()
    {
        // Step 1 no longer collects a unit-related flag, a raw caller-given
        // unit number, or an Intake priority hint — the real Unit comes from
        // customer-lookup selection (Step 2) and the real Priority is chosen
        // once, on Step 3.
        var type = typeof(NewTicketModel.IntakeInput);

        Assert.Null(type.GetProperty("IsUnitRelated"));
        Assert.Null(type.GetProperty("RawUnitNumberEntered"));
        Assert.Null(type.GetProperty("PriorityHint"));
    }

    [Fact]
    public async Task OnGetAsync_IntakeStep_LoadsDepartmentDirectory_ForTheDropdown()
    {
        var (model, _, _, departments, _, _) = CreateModel(departmentsResponder: DepartmentsReturning(
            new DepartmentDto(7, "Facilities Management"),
            new DepartmentDto(2, "Customer Service")));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Single(departments.Requests);
        Assert.Equal(2, model.Departments.Count);
        Assert.Contains(model.Departments, d => d is { DepartmentId: 7, Name: "Facilities Management" });
    }

    [Fact]
    public async Task OnPostIntakeAsync_Failure_ReloadsDepartmentDirectory_ForRedisplay()
    {
        var (model, _, _, departments, _, _) = CreateModel(
            intakeResponder: (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway),
            departmentsResponder: DepartmentsReturning(new DepartmentDto(7, "Facilities Management")));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567" };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
        Assert.Single(departments.Requests);
        Assert.Single(model.Departments);
    }

    // ---- 3: Intake API receives PhoneNumber and DepartmentId, and always records not-unit-related with no raw unit number/priority hint ----

    [Fact]
    public async Task OnPostIntakeAsync_SendsPhoneNumberAndDepartmentIdToIntakeApi()
    {
        var (model, intake, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
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
    public async Task OnPostIntakeAsync_NeverSendsUnitRelatedFlagOrRawUnitNumberOrPriorityHint()
    {
        // The wizard itself never collects these — every intake it creates
        // is not-unit-related, with no raw unit number and no priority hint.
        var (model, intake, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new IntakeRecordResponseDto(
                42, "Phone", DateTime.UtcNow, "+15551234567", null, false, null, null, "Unverified", null)));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567" };

        await model.OnPostIntakeAsync(CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(intake.Requests).Body!);
        Assert.False(body.RootElement.GetProperty("isUnitRelated").GetBoolean());
        Assert.True(body.RootElement.GetProperty("rawUnitNumberEntered").ValueKind == JsonValueKind.Null);
        Assert.True(body.RootElement.GetProperty("priorityHint").ValueKind == JsonValueKind.Null);
    }

    // ---- 4: Customer lookup is called after successful Intake creation ----

    [Fact]
    public async Task OnPostIntakeAsync_Success_RedirectsToLookupStep_CarryingIntakeRecordIdPhoneNumberAndDepartment()
    {
        var (model, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
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

    [Fact]
    public async Task OnGetAsync_LookupStep_CallsCustomerLookupApi_ForTheIntakeRecord()
    {
        var (model, _, lookup, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567", [])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        var sent = Assert.Single(lookup.Requests);
        Assert.Equal("http://localhost/api/intake-records/42/customer-lookup", sent.RequestUri);
    }

    // ---- 5, 6, 7: Found / NotFound / Failed all populate LookupResult and never block continuing ----

    [Fact]
    public async Task OnGetAsync_Lookup_Found_PopulatesLookupResultWithSourceDetails()
    {
        var (model, _, _, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567",
            [
                new CustomerLookupSourceResultDto("Crm", "Found",
                [
                    new CustomerLookupCustomerDto("CRM-CUST-1", "Jane Doe", "+15551234567", null, "Buyer",
                    [
                        new CustomerLookupUnitDto("CRM-UNIT-1", "12B", "Tiger Tower", null, null, 5, 9)
                    ])
                ])
            ])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        Assert.Null(model.ErrorMessage);
        var source = Assert.Single(model.LookupResult!.Sources);
        Assert.Equal("Found", source.Status);
        var customer = Assert.Single(source.Customers);
        Assert.Equal("Jane Doe", customer.DisplayName);
        var unit = Assert.Single(customer.Units);
        Assert.Equal(5, unit.UnitReferenceId);
        Assert.Equal(9, unit.ContactReferenceId);
    }

    [Fact]
    public async Task OnGetAsync_Lookup_NotFound_StillPopulatesLookupResult_NoErrorMessage()
    {
        var (model, _, _, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567",
            [
                CustomerLookupSourceResultDto.NotFound("Crm")
            ])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        Assert.Null(model.ErrorMessage);
        Assert.Equal("NotFound", Assert.Single(model.LookupResult!.Sources).Status);
    }

    [Fact]
    public async Task OnGetAsync_Lookup_Failed_StillPopulatesLookupResult_NoErrorMessage()
    {
        var (model, _, _, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567",
            [
                CustomerLookupSourceResultDto.Failed("Pact")
            ])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        // A source being unavailable is not an error state for the page — Continue must remain reachable.
        Assert.Null(model.ErrorMessage);
        Assert.Equal("Failed", Assert.Single(model.LookupResult!.Sources).Status);
    }

    // ---- 8: Partial results (Found + Failed + NotFound) all render independently ----

    [Fact]
    public async Task OnGetAsync_Lookup_PartialResultsAcrossSources_AllPresentIndependently()
    {
        var (model, _, _, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567",
            [
                new CustomerLookupSourceResultDto("Crm", "Found",
                [
                    new CustomerLookupCustomerDto("CRM-CUST-1", "Jane Doe", "+15551234567", null, "Buyer",
                    [
                        new CustomerLookupUnitDto("CRM-UNIT-1", "12B", "Tiger Tower", null, null, 5, 9)
                    ])
                ]),
                CustomerLookupSourceResultDto.Failed("Pact"),
                CustomerLookupSourceResultDto.NotFound("Tasleeh")
            ])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        Assert.Null(model.ErrorMessage);
        Assert.Equal(3, model.LookupResult!.Sources.Count);
        Assert.Equal("Found", model.LookupResult.Sources.Single(s => s.Source == "Crm").Status);
        Assert.Equal("Failed", model.LookupResult.Sources.Single(s => s.Source == "Pact").Status);
        Assert.Equal("NotFound", model.LookupResult.Sources.Single(s => s.Source == "Tasleeh").Status);
    }

    // ---- 9 & 10: no-Department returns all searched sources; a Department scopes to only what was searched ----

    [Fact]
    public async Task OnGetAsync_Lookup_NoDepartment_AllThreeSourcesFromApiAreShown()
    {
        var (model, _, _, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567",
            [
                CustomerLookupSourceResultDto.NotFound("Crm"),
                CustomerLookupSourceResultDto.NotFound("Pact"),
                CustomerLookupSourceResultDto.NotFound("Tasleeh")
            ])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        Assert.Equal(["Crm", "Pact", "Tasleeh"], model.LookupResult!.Sources.Select(s => s.Source));
    }

    [Fact]
    public async Task OnGetAsync_Lookup_DepartmentScoped_OnlyTheSourcesTheApiActuallySearchedAreShown()
    {
        // The Web page never invents source-selection logic of its own: it renders exactly the
        // Sources list the Api returns, so a Department-scoped search (Api returns just Pact)
        // must never grow a fabricated "Crm → NotFound"/"Tasleeh → NotFound" entry client-side.
        var (model, _, _, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567",
            [
                new CustomerLookupSourceResultDto("Pact", "Found",
                [
                    new CustomerLookupCustomerDto("PACT-CUST-1", "Jane Doe", "+15551234567", null, null, [])
                ])
            ])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        Assert.Equal(["Pact"], model.LookupResult!.Sources.Select(s => s.Source));
    }

    // ---- Multiple customer/unit matches: the DTO shape carries them, selection is never automatic ----

    [Fact]
    public async Task OnGetAsync_Lookup_MultipleCustomers_AllPresentInLookupResult()
    {
        var (model, _, _, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+971501234567",
            [
                new CustomerLookupSourceResultDto("Crm", "Found",
                [
                    new CustomerLookupCustomerDto("CRM-CUST-1", "Ahmed Ali", "+971501234567", null, "Buyer",
                    [
                        new CustomerLookupUnitDto("CRM-UNIT-1", "1205", "Tiger Sky Tower", null, null, 5, 9),
                        new CustomerLookupUnitDto("CRM-UNIT-2", "1403", "Tiger Sky Tower", null, null, 6, 10)
                    ]),
                    new CustomerLookupCustomerDto("CRM-CUST-2", "Ahmad Ali Hassan", "+971501234567", null, "Buyer",
                    [
                        new CustomerLookupUnitDto("CRM-UNIT-3", "2004", "Tiger Sky Tower", null, null, 7, 11)
                    ])
                ])
            ])));

        await model.OnGetAsync("lookup", 42, "+971501234567", null, null, null, null, null, CancellationToken.None);

        var source = Assert.Single(model.LookupResult!.Sources);
        Assert.Equal(2, source.Customers.Count);
        Assert.Equal(2, source.Customers.Single(c => c.ExternalCustomerId == "CRM-CUST-1").Units.Count);
        Assert.Single(source.Customers.Single(c => c.ExternalCustomerId == "CRM-CUST-2").Units);
    }

    [Fact]
    public async Task OnGetAsync_Lookup_CustomerWithNoUnits_StillPresentInLookupResult()
    {
        var (model, _, _, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+971502223333",
            [
                new CustomerLookupSourceResultDto("Crm", "Found",
                [
                    new CustomerLookupCustomerDto("CRM-CUST-3", "Khalid Nasser", "+971502223333", null, "Buyer", [])
                ])
            ])));

        await model.OnGetAsync("lookup", 42, "+971502223333", null, null, null, null, null, CancellationToken.None);

        var customer = Assert.Single(Assert.Single(model.LookupResult!.Sources).Customers);
        Assert.Empty(customer.Units);
    }

    [Fact]
    public async Task OnGetAsync_Lookup_ConfigurationMissing_EmptySourcesList_NoErrorMessage()
    {
        var (model, _, _, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567", [])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        Assert.Null(model.ErrorMessage);
        Assert.Empty(model.LookupResult!.Sources);
    }

    // ---- Category dropdown: loaded from the Categories API, scoped by Department ----

    [Fact]
    public async Task OnGetAsync_CreateStep_WithDepartment_RequestsCategoriesFilteredByThatDepartment()
    {
        var (model, _, _, _, categories, _) = CreateModel(categoriesResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new[]
            {
                new CategoryDto(2, "Corrective Maintenance", 7, "Facilities Management")
            }));

        await model.OnGetAsync("create", 42, "+15551234567", 7, null, null, null, null, CancellationToken.None);

        var sent = Assert.Single(categories.Requests);
        Assert.Equal("http://localhost/api/categories?departmentId=7", sent.RequestUri);
        var single = Assert.Single(model.Categories);
        Assert.Equal("Corrective Maintenance", single.Name);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_NoDepartment_RequestsAllCategoriesWithNoFilter()
    {
        var (model, _, _, _, categories, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(1, "General Inquiry", 1, "Customer Service"),
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));

        await model.OnGetAsync("create", 42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        var sent = Assert.Single(categories.Requests);
        Assert.Equal("http://localhost/api/categories", sent.RequestUri);
        Assert.Equal(2, model.Categories.Count);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_CategoriesApiFails_SetsCategoriesErrorMessage_NoNumericFallback()
    {
        var (model, _, _, _, _, _) = CreateModel(categoriesResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        await model.OnGetAsync("create", 42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        Assert.NotNull(model.CategoriesErrorMessage);
        Assert.Empty(model.Categories);
    }

    [Fact]
    public async Task OnGetAsync_CreateStep_DepartmentWithNoActiveCategories_EmptyListNoErrorMessage()
    {
        var (model, _, _, _, _, _) = CreateModel(categoriesResponder: CategoriesReturning());

        await model.OnGetAsync("create", 42, "+15551234567", 3, null, null, null, null, CancellationToken.None);

        Assert.Null(model.CategoriesErrorMessage);
        Assert.Empty(model.Categories);
    }

    // ---- Selected Customer/Unit display labels persist to Step 3 ----

    [Fact]
    public async Task OnGetAsync_CreateStep_CarriesSelectedCustomerAndUnitLabelForward()
    {
        var (model, _, _, _, _, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));

        await model.OnGetAsync(
            "create", 42, "+15551234567", 2, 5, 9, "Ahmed Ali", "Tiger Sky Tower — Unit 1205", CancellationToken.None);

        Assert.Equal("Ahmed Ali", model.CustomerDisplayName);
        Assert.Equal("Tiger Sky Tower — Unit 1205", model.UnitLabel);
    }

    // ---- 19: Priority is required in Step 3, never requested in Step 1 ----

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
        var (model, _, _, _, categories, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = null, RequestSummary = "Summary" };

        var result = await model.OnPostCreateAsync(42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
        Assert.Single(categories.Requests); // categories reloaded to redisplay the dropdown
    }

    // ---- 11: ticket creation uses only POST /api/tickets, and posts the real selected CategoryId/PriorityId ----

    [Fact]
    public async Task OnPostCreateAsync_CallsOnlyPostApiTickets_WithNoVerificationSessionField()
    {
        var (model, _, _, _, _, tickets) = CreateModel(ticketsResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                100, "TG-CS-20260825-0001", 7, 7, 5, 9, 2, 3, "Open", "Unverified", "None", "Running", "Summary", DateTime.UtcNow, "AAAA")));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 3, RequestSummary = "Summary" };

        await model.OnPostCreateAsync(42, "+15551234567", null, 5, 9, "Ahmed Ali", "Tiger Sky Tower — Unit 1205", CancellationToken.None);

        var sent = Assert.Single(tickets.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://localhost/api/tickets", sent.RequestUri);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.False(body.RootElement.TryGetProperty("verificationSessionId", out _));
        Assert.Equal(3, body.RootElement.GetProperty("priorityId").GetByte());
    }

    [Fact]
    public async Task OnPostCreateAsync_PostsTheRealSelectedCategoryId_NotAManuallyTypedOne()
    {
        var (model, _, _, _, _, tickets) = CreateModel(ticketsResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                100, "TG-FM-20260825-0001", 2, 2, null, null, 2, 3, "Open", "Unverified", "None", "Running", "Summary", DateTime.UtcNow, "AAAA")));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 3, RequestSummary = "AC unit not cooling" };

        await model.OnPostCreateAsync(42, "+15551234567", 2, null, null, null, null, CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.Equal(2, body.RootElement.GetProperty("categoryId").GetInt32());
    }

    [Fact]
    public async Task OnPostCreateAsync_NoCategorySelected_RejectedWithoutCallingTheTicketsApi()
    {
        var (model, _, _, _, categories, _) = CreateModel(categoriesResponder: CategoriesReturning(
            new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = null, PriorityId = 3, RequestSummary = "Summary" };

        var result = await model.OnPostCreateAsync(42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
        Assert.Single(categories.Requests); // categories reloaded to redisplay the dropdown
    }

    // ---- 21: optional UnitReferenceId/ContactReferenceId can be null ----

    // ---- 8: CustomerDisplayName/UnitLabel are display-only — never sent to any API as identity data ----

    [Fact]
    public async Task OnPostCreateAsync_NeverSendsCustomerDisplayNameOrUnitLabelToTicketsApi()
    {
        // The server must never trust browser-supplied labels as authoritative
        // identity data — actual linking uses only the validated
        // UnitReferenceId/ContactReferenceId the Api itself resolves.
        // CustomerDisplayName/UnitLabel exist purely for Step 3's summary text.
        var (model, _, _, _, _, tickets) = CreateModel(ticketsResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                100, "TG-CS-20260825-0001", 7, 7, 5, 9, 2, 3, "Open", "Unverified", "None", "Running", "Summary", DateTime.UtcNow, "AAAA")));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 3, RequestSummary = "Summary" };

        await model.OnPostCreateAsync(
            42, "+15551234567", null, 5, 9, "Ahmed Ali", "Tiger Sky Tower — Unit 1205", CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        var properties = body.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(properties, p => p.Contains("customerDisplayName", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Contains("unitLabel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Contains("customerName", StringComparison.OrdinalIgnoreCase));
        // Only the validated reference ids identify the unit/contact.
        Assert.Equal(5, body.RootElement.GetProperty("unitReferenceId").GetInt32());
        Assert.Equal(9, body.RootElement.GetProperty("contactReferenceId").GetInt32());
    }

    [Fact]
    public void CreateTicketRequestDto_HasNoDisplayLabelFields()
    {
        // A structural guard, not just a snapshot of one request body: the
        // wire contract itself carries only validated reference ids, so a
        // future field addition can't silently start trusting a label.
        var properties = typeof(CreateTicketRequestDto).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain(properties, p => p.Contains("DisplayName", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Contains("Label", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OnPostCreateAsync_WithNullOptionalReferences_StillCreatesTicket()
    {
        var (model, _, _, _, _, tickets) = CreateModel(ticketsResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                101, "TG-CS-20260825-0002", 7, 7, null, null, 2, 3, "Open", "Unverified", "None", "Running", "Summary", DateTime.UtcNow, "AAAA")));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 3, RequestSummary = "Summary" };

        var result = await model.OnPostCreateAsync(42, "+15551234567", null, null, null, null, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/TicketDetails", redirect.PageName);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.True(body.RootElement.GetProperty("unitReferenceId").ValueKind == JsonValueKind.Null);
        Assert.True(body.RootElement.GetProperty("contactReferenceId").ValueKind == JsonValueKind.Null);
    }

    // ---- 12, 13 & 14: multiple/none of the lookup results are selectable, Department + display labels carry through ----

    [Fact]
    public void OnPostUseMatch_CarriesSelectedReferenceAndDepartment_ToCreateStep()
    {
        var (model, _, _, _, _, _) = CreateModel();

        var result = model.OnPostUseMatch(42, "+15551234567", 7, "5:9", "Ahmed Ali");

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        var values = RouteValues(redirect);
        Assert.Equal("create", values["step"]);
        Assert.Equal(7, values["departmentId"]);
        Assert.Equal(5, values["unitReferenceId"]);
        Assert.Equal(9, values["contactReferenceId"]);
        Assert.Equal("Ahmed Ali", values["customerDisplayName"]);
    }

    [Fact]
    public void OnPostUseMatch_DifferentUnitSelected_CarriesThatUnitsOwnReferences()
    {
        // A customer with multiple units must be able to carry forward
        // whichever specific unit's reference pair the agent actually
        // selected — never defaulting to the first one.
        var (model, _, _, _, _, _) = CreateModel();

        var result = model.OnPostUseMatch(42, "+971501234567", null, "6:10", "Ahmed Ali");

        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal(6, values["unitReferenceId"]);
        Assert.Equal(10, values["contactReferenceId"]);
    }

    [Fact]
    public void OnPostUseMatch_CarriesTheSelectedUnitsDisplayLabel_UrlDecoded()
    {
        var (model, _, _, _, _, _) = CreateModel();
        var encodedLabel = Uri.EscapeDataString("Tiger Sky Tower — Unit 1205 (Sold)");

        var result = model.OnPostUseMatch(42, "+971501234567", null, $"5:9:{encodedLabel}", "Ahmed Ali");

        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal("Tiger Sky Tower — Unit 1205 (Sold)", values["unitLabel"]);
    }

    [Fact]
    public void OnPostContinueWithoutMatch_ProceedsWithNoReferenceSelected_CarriesDepartment()
    {
        var (model, _, _, _, _, _) = CreateModel();

        var result = model.OnPostContinueWithoutMatch(42, "+15551234567", 7);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        var values = RouteValues(redirect);
        Assert.Equal("create", values["step"]);
        Assert.Equal(7, values["departmentId"]);
        Assert.False(values.ContainsKey("unitReferenceId"));
    }

    [Fact]
    public void OnPostContinueWithoutMatch_CustomerWithNoUnits_CarriesCustomerNameForward_NoUnitReference()
    {
        var (model, _, _, _, _, _) = CreateModel();

        var result = model.OnPostContinueWithoutMatch(42, "+971502223333", null, "Khalid Nasser");

        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal("create", values["step"]);
        Assert.Equal("Khalid Nasser", values["customerDisplayName"]);
        Assert.False(values.ContainsKey("unitReferenceId"));
    }

    // ---- 15: the existing valid creation flow still succeeds end-to-end through the PageModel ----

    [Fact]
    public async Task FullFlow_IntakeLookupCategorySelectionCreate_StillSucceeds()
    {
        var (model, _, _, _, _, tickets) = CreateModel(
            intakeResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.Created, new IntakeRecordResponseDto(
                42, "Phone", DateTime.UtcNow, "+15551234567", 2, false, null, null, "Unverified", null)),
            lookupResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567", [])),
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")),
            ticketsResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                200, "TG-FM-20260825-0001", 2, 2, null, null, 2, 3, "Open", "Unverified", "None", "Running", "x", DateTime.UtcNow, "AAAA")));

        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567", DepartmentId = 2 };
        var intakeResult = await model.OnPostIntakeAsync(CancellationToken.None);
        var lookupRoute = RouteValues(Assert.IsType<RedirectToPageResult>(intakeResult));

        await model.OnGetAsync("lookup", (long)lookupRoute["intakeRecordId"]!, (string?)lookupRoute["phoneNumber"], (int?)lookupRoute["departmentId"], null, null, null, null, CancellationToken.None);
        var continueResult = model.OnPostContinueWithoutMatch(42, "+15551234567", 2);
        var createRoute = RouteValues(Assert.IsType<RedirectToPageResult>(continueResult));

        await model.OnGetAsync("create", 42, "+15551234567", (int?)createRoute["departmentId"], null, null, null, null, CancellationToken.None);
        Assert.Single(model.Categories);

        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = model.Categories.Single().CategoryId, PriorityId = 3, RequestSummary = "x" };
        var createResult = await model.OnPostCreateAsync(42, "+15551234567", 2, null, null, null, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(createResult);
        Assert.Equal("/TicketDetails", redirect.PageName);
        Assert.Single(tickets.Requests);
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
        var (model, _, _, _, _, _) = CreateModel(departmentsResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Empty(model.Departments);
        Assert.NotNull(model.DepartmentsErrorMessage);
        Assert.Contains("not authorized", model.DepartmentsErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnGetAsync_IntakeStep_DepartmentsApi403_SetsPredictableAuthMessage_NotGenericFallback()
    {
        var (model, _, _, _, _, _) = CreateModel(departmentsResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.Forbidden));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, CancellationToken.None);

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
        var (model, _, _, _, _, _) = CreateModel(departmentsResponder: (_, _) =>
            throw new HttpRequestException("Connection refused"));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Empty(model.Departments);
        Assert.NotNull(model.DepartmentsErrorMessage);
    }

    [Fact]
    public async Task OnPostIntakeAsync_ValidationError_SurfacesApiDetail_NotAGenericMessage()
    {
        // A 400 from POST /api/intake-records carries a ProblemDetails body —
        // its "detail" must reach the page verbatim rather than being masked
        // by the page's own generic "Could not record this interaction." text.
        var (model, _, _, departments, _, _) = CreateModel(intakeResponder: (_, _) =>
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
        var (model, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            throw new HttpRequestException("No connection could be made because the target machine actively refused it."));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567" };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostIntakeAsync_ApiUnauthorized_SetsPredictableAuthMessage_NotGenericFallback()
    {
        var (model, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567" };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorMessage);
        Assert.Contains("not authorized", model.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
