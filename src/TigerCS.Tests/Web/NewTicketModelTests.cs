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
/// The redesigned four-step New Ticket wizard's PageModel: Customer →
/// Property → Issue → Review → Create, against TigerCS.Api's real DTO
/// contracts with <see cref="FakeApiHandler"/> standing in for the Api
/// itself. Every pre-redesign invariant is preserved and re-asserted here in
/// the new flow's terms: the phone number travels verbatim (no pattern, no
/// normalization), lookups are enrichment that never gate creation, packed
/// unit selections keep ids and display text together, a PACT/Tasleeh
/// selection persists as external identity + manual snapshot, plain manual
/// entry carries no external identity, and POST /api/tickets receives
/// exactly what it always received.
/// </summary>
public sealed class NewTicketModelTests
{
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
        // which throw by default): the customer-history awareness is
        // enrichment, not central to most of this wizard's own tests, so
        // only the tests that actually care about it supply a responder.
        var customerHistoryHandler = new FakeApiHandler(customerHistoryResponder ?? (
            (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerHistoryDto("Verified", null, null, null, 0, 0, 0, []))));
        // Department-aware customer lookup default: this flow's intakes carry
        // no Department, so the no-department shape — all three sources
        // searched, nothing found — is the natural default.
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

    /// <summary>OnGetAsync with the wizard's carried state as named optionals — the query-string shape, minus repetition.</summary>
    private static Task<IActionResult> GetAsync(
        NewTicketModel model, string? step = null, long? intakeRecordId = null, string? phoneNumber = null,
        string? customer = null,
        int? crmBuyerCustomerId = null, int? crmBuyerLeadId = null, int? crmBuyerUnitId = null, int? crmBuyerProjectId = null,
        string? crmBuyerCustomerName = null, string? crmBuyerProjectName = null, string? crmBuyerUnitNumber = null,
        string? externalSelection = null, string? manualProjectName = null, string? manualUnitNumber = null,
        int? departmentId = null, long? createdTicketId = null, string? createdTicketNumber = null) =>
        model.OnGetAsync(
            step, intakeRecordId, phoneNumber, customer,
            crmBuyerCustomerId, crmBuyerLeadId, crmBuyerUnitId, crmBuyerProjectId,
            crmBuyerCustomerName, crmBuyerProjectName, crmBuyerUnitNumber,
            externalSelection, manualProjectName, manualUnitNumber,
            departmentId, createdTicketId, createdTicketNumber, CancellationToken.None);

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

    private static Func<HttpRequestMessage, string?, HttpResponseMessage> TicketCreated(long ticketId, string ticketNumber) =>
        (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
            ticketId, ticketNumber, 7, 7, null, null, 2, 3, "Open", "Unverified", "None", "Running", "x", DateTime.UtcNow, "AAAA"));

    private static string PackedExternal(
        string source = "Pact", string customerId = "7001", string unitId = "701",
        string name = "Fatima Noor", string project = "Tiger Bay Towers", string unit = "1105") =>
        string.Join(':',
            Uri.EscapeDataString(source), Uri.EscapeDataString(customerId), Uri.EscapeDataString(unitId),
            Uri.EscapeDataString(name), Uri.EscapeDataString(project), Uri.EscapeDataString(unit));

    private static IDictionary<string, object?> RouteValues(RedirectToPageResult redirect) =>
        redirect.RouteValues is null
            ? new Dictionary<string, object?>()
            : redirect.RouteValues.ToDictionary(kv => kv.Key, kv => kv.Value);

    // ---- Step 1: the phone number is free-form and travels verbatim ----

    [Fact]
    public void IntakeInput_PhoneNumber_IsRequired()
    {
        var input = new NewTicketModel.IntakeInput { PhoneNumber = "" };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(NewTicketModel.IntakeInput.PhoneNumber)));
    }

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
    public async Task OnPostIntakeAsync_PlusPrefixedPhone_SentVerbatimToIntakeApi_AndCarriedVerbatimToCustomerResults()
    {
        var (model, intake, _, _, _, _, _, _) = CreateModel(intakeResponder: (_, body) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created,
                new IntakeRecordResponseDto(42, "Phone", DateTime.UtcNow, "+971501234567", null, false, null, null, "Unverified", null)));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+971501234567" };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        using var sentBody = JsonDocument.Parse(Assert.Single(intake.Requests).Body!);
        Assert.Equal("+971501234567", sentBody.RootElement.GetProperty("phoneNumber").GetString());

        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal(NewTicketModel.StepCustomer, values["step"]);
        Assert.Equal("+971501234567", values["phoneNumber"]);
        Assert.Equal(42L, values["intakeRecordId"]);
    }

    [Fact]
    public async Task OnPostIntakeAsync_SendsNoDepartment_DepartmentMovedToTheIssueStep()
    {
        // The redesigned flow selects Department on the Issue step, so the
        // intake record carries none — which, per the existing
        // department-aware rule, means every configured source is searched.
        var (model, intake, _, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created,
                new IntakeRecordResponseDto(42, "Phone", DateTime.UtcNow, "+15551234567", null, false, null, null, "Unverified", null)));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567" };

        await model.OnPostIntakeAsync(CancellationToken.None);

        using var sentBody = JsonDocument.Parse(Assert.Single(intake.Requests).Body!);
        Assert.Equal(JsonValueKind.Null, sentBody.RootElement.GetProperty("departmentId").ValueKind);
        Assert.Equal("Phone", sentBody.RootElement.GetProperty("channelId").GetString());
    }

    [Fact]
    public async Task OnGetAsync_CustomerStep_WithCarriedPhoneNumber_PrefillsTheSearchField_ExactlyAsGiven()
    {
        // Customer Workspace carry-forward: "+ New Ticket" from a selected
        // customer must not make the agent re-type the same number — the
        // phone arrives via the query string and lands in the search field
        // unmodified ('+' preserved), still fully editable, and search still
        // re-verifies through the same lookups.
        var (model, _, _, _, _, _, _, _) = CreateModel();

        await GetAsync(model, phoneNumber: "+971501112233");

        Assert.Equal(NewTicketModel.StepCustomer, model.Step);
        Assert.Equal("+971501112233", model.Intake.PhoneNumber);
    }

    [Fact]
    public async Task OnPostIntakeAsync_Failure_SurfacesApiDetail_NotAGenericMessage()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.BadRequest, new { detail = "Customer phone number is invalid." }));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "not-a-phone-number" };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Customer phone number is invalid.", model.ErrorMessage);
        Assert.Equal(NewTicketModel.StepCustomer, model.Step);
    }

    [Fact]
    public async Task OnPostIntakeAsync_EmptyPhone_RedisplaysStep1WithAVisibleError_AndNeverCallsTheApi()
    {
        // The empty-search guard validates only what the Search form posts —
        // NEVER the page-wide ModelState, which the co-bound CreateStep's
        // [Required] members make invalid on every Step 1 POST in the real
        // pipeline (the silent-empty-Step-1 regression; the full-pipeline
        // proof lives in NewTicketWizardHttpFlowTests).
        var (model, handler, _, _, _, _, _, _) = CreateModel();
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "  " };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(NewTicketModel.StepCustomer, model.Step);
        Assert.Equal("Enter a phone number to search.", model.ErrorMessage);
        Assert.Empty(handler.Requests);
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

    // ---- Step 1 results: lookups run by intake id + phone only, and build
    // customer cards without exposing raw identifiers ----

    [Fact]
    public async Task OnGetAsync_CustomerResults_CallsGenericCustomerLookup_ByIntakeRecordId()
    {
        var (model, _, _, _, _, _, _, customerLookup) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound());

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+9613040922");

        var sent = Assert.Single(customerLookup.Requests);
        Assert.Equal("http://localhost/api/intake-records/42/customer-lookup", sent.RequestUri);
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_CallsRealCrmBuyerLookupApi_ByPhoneNumberOnly()
    {
        var (model, _, crmBuyerLookup, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound());

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+971501234567");

        var sent = Assert.Single(crmBuyerLookup.Requests);
        Assert.StartsWith("http://localhost/api/crm/buyers?phoneNumber=", sent.RequestUri);
        Assert.Contains(Uri.EscapeDataString("+971501234567"), sent.RequestUri);
        Assert.DoesNotContain("unitNumber", sent.RequestUri);
        Assert.DoesNotContain("projectName", sent.RequestUri);
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_CrmMatch_BecomesACandidateCard_WithHistoryKeyedByTheCrmCustomerId()
    {
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(
                SingleUnitBuyer(5001, "Sami Nasser", "+971509990001", 900, 100, 10, "1506", "Nobles Tower")),
            customerHistoryResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK,
                new CustomerHistoryDto("Verified", 5001, null, "Sami Nasser", 4, 1, 3,
                    [new CustomerHistoryTicketDto(50, "TG-CS-20260810-0001", DateTime.UtcNow.AddDays(-5), "InProgress", 2, 2, 2, "Nobles Tower", "1506", "Verified", "AC issue")])));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+971509990001");

        var candidate = Assert.Single(model.Candidates);
        Assert.Equal("crm", candidate.Key);
        Assert.Equal("Sami Nasser", candidate.DisplayName);
        Assert.Equal(1, candidate.UnitsCount);

        // The awareness call is keyed by the CRM customer id — never a name
        // or phone match — bounded, active-first.
        var sent = Assert.Single(customerHistory.Requests);
        Assert.Contains("/api/customers/crm/5001/ticket-history", sent.RequestUri);
        Assert.Contains($"limit={NewTicketModel.CandidateHistoryLimit}", sent.RequestUri);
        Assert.Contains("orderActiveFirst=true", sent.RequestUri);

        var history = model.CandidateHistories[candidate.Key];
        Assert.Equal(4, history.TotalTickets);
        Assert.Equal(1, history.OpenTickets);
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_PactMatch_BecomesACandidateCard_WithHistoryKeyedByThePersistedExternalIdentity()
    {
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(),
            customerLookupResponder: CustomerLookupReturning(
                CustomerLookupSourceResultDto.NotFound("Crm"),
                CustomerLookupSourceResultDto.Found("Pact", [PactCustomerWithTwoUnits()])));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+971509990002");

        var candidate = Assert.Single(model.Candidates);
        Assert.Equal("Pact", candidate.Source);
        Assert.Equal("Fatima Noor", candidate.DisplayName);
        Assert.Equal(2, candidate.UnitsCount);

        var sent = Assert.Single(customerHistory.Requests);
        Assert.Contains("/api/customers/external/Pact/7001/ticket-history", sent.RequestUri);
        Assert.DoesNotContain("Fatima", sent.RequestUri);
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_TasleehMatch_BecomesACandidateCard_ThroughTheSameExternalIdentity()
    {
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(),
            customerLookupResponder: CustomerLookupReturning(
                CustomerLookupSourceResultDto.NotFound("Crm"),
                CustomerLookupSourceResultDto.Found("Tasleeh",
                    [new CustomerLookupCustomerDto("TAS-9", "Aisha Rahman", "+971509990003", null, null, [])])));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+971509990003");

        var candidate = Assert.Single(model.Candidates);
        Assert.Equal("Tasleeh", candidate.Source);
        Assert.Equal(0, candidate.UnitsCount);
        Assert.Contains("/api/customers/external/Tasleeh/TAS-9/ticket-history", Assert.Single(customerHistory.Requests).RequestUri);
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_CrmAndPactBothMatch_BothCandidatesCoexist()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(
                SingleUnitBuyer(5001, "Sami Nasser", "+971509990002", 900, 100, 10, "5001", "Tiger Sky Tower")),
            customerLookupResponder: CustomerLookupReturning(
                CustomerLookupSourceResultDto.NotFound("Crm"),
                CustomerLookupSourceResultDto.Found("Pact", [PactCustomerWithTwoUnits()])));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+971509990002");

        Assert.Equal(2, model.Candidates.Count);
        Assert.Contains(model.Candidates, c => c.Key == "crm");
        Assert.Contains(model.Candidates, c => c.Source == "Pact");
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_NoMatchAnywhere_NoCandidates_NoHistoryCalls_NotAnError()
    {
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel(crmBuyerLookupResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+9613040922");

        Assert.Empty(model.Candidates);
        Assert.Empty(customerHistory.Requests);
        Assert.Null(model.ErrorMessage);
        Assert.False(model.CrmBuyerLookupUnavailable);
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_CrmUnavailable_FlaggedDistinctlyFromNotFound()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+9613040922");

        Assert.True(model.CrmBuyerLookupUnavailable);
        Assert.Null(model.CrmBuyerMatch);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_CrmNetworkUnreachable_TreatedAsUnavailable_NeverThrows()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: (_, _) =>
            throw new HttpRequestException("Connection refused"));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+9613040922");

        Assert.True(model.CrmBuyerLookupUnavailable);
        Assert.Null(model.CrmBuyerMatch);
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_CrmAmbiguousCustomerMatch_FlagsAmbiguous_NeverAutoSelectsACustomer()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Conflict, new { title = "Multiple CRM customer records" }));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+9613040922");

        Assert.True(model.CrmBuyerAmbiguousMatch);
        Assert.Null(model.CrmBuyerMatch);
        Assert.Empty(model.Candidates);
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_ApiUnexpectedlyReturnsMultipleCustomers_WebLayerDefensivelyUsesTheFirstOnly()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound(
            SingleUnitBuyer(5001, "First Buyer", "+971501234567", 900, 100, 10, "1205", "Tiger Sky Tower"),
            SingleUnitBuyer(5002, "Second Buyer", "+971501234567", 901, 101, 10, "2004", "Tiger Sky Tower")));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+971501234567");

        Assert.NotNull(model.CrmBuyerMatch);
        Assert.Equal("First Buyer", model.CrmBuyerMatch!.Customer.FullNameEnglish);
        Assert.Single(model.Candidates, c => c.Key == "crm");
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_PactFailed_DoesNotHideCrmResults_NeverBlocks()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(
                SingleUnitBuyer(5001, "Sami Nasser", "+971509990002", 900, 100, 10, "5001", "Tiger Sky Tower")),
            customerLookupResponder: CustomerLookupReturning(
                CustomerLookupSourceResultDto.NotFound("Crm"),
                CustomerLookupSourceResultDto.Failed("Pact")));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+971509990002");

        Assert.NotNull(model.CrmBuyerMatch);
        Assert.Equal("Failed", Assert.Single(model.ExternalLookupSources).Status);
        Assert.Null(model.ErrorMessage); // a failed source is a state, never a blocking error
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_GenericLookupUnavailable_FailsOpenToCrmOnly_NeverBlocks()
    {
        var (model, _, crmBuyerLookup, _, _, _, _, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(),
            customerLookupResponder: (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+9613040922");

        Assert.True(model.CustomerLookupUnavailable);
        Assert.True(model.CrmParticipates); // fail-open: the real CRM lookup still ran
        Assert.Single(crmBuyerLookup.Requests);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_LookupResponseWithoutCrm_CrmNotSearched()
    {
        // The lookup response stays the authoritative participation signal —
        // a response without a Crm entry means the real CRM Buyer Lookup
        // must not run (its default responder throws if called).
        var (model, _, crmBuyerLookup, _, _, _, _, _) = CreateModel(customerLookupResponder: CustomerLookupReturning(
            CustomerLookupSourceResultDto.Found("Pact", [PactCustomerWithTwoUnits()])));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+971509990002");

        Assert.False(model.CrmParticipates);
        Assert.Empty(crmBuyerLookup.Requests);
        Assert.Single(model.Candidates, c => c.Source == "Pact");
    }

    [Fact]
    public async Task OnGetAsync_CustomerResults_NoConfiguredSources_ShowsThatState_SearchesNothing()
    {
        var (model, _, crmBuyerLookup, _, _, _, _, _) = CreateModel(customerLookupResponder: CustomerLookupReturning());

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+9613040922");

        Assert.True(model.NoLookupSourcesConfigured);
        Assert.False(model.CrmParticipates);
        Assert.Empty(crmBuyerLookup.Requests);
        Assert.Empty(model.Candidates);
    }

    // ---- Step 2: unit selection — packed values keep ids and display text
    // together, nothing is ever auto-selected ----

    [Fact]
    public void OnPostUseCrmBuyerUnit_CarriesAllFourCrmIdsAndSnapshotText_ToThePropertyStep()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel();
        var packed = string.Join(':',
            5001, 900, 100, 10,
            Uri.EscapeDataString("Sami Nasser"), Uri.EscapeDataString("Tiger Sky Tower"), Uri.EscapeDataString("1205"));

        var result = model.OnPostUseCrmBuyerUnit(42, "+971501234567", packed);

        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal(NewTicketModel.StepProperty, values["step"]);
        Assert.Equal("crm", values["customer"]);
        Assert.Equal(5001, values["crmBuyerCustomerId"]);
        Assert.Equal(900, values["crmBuyerLeadId"]);
        Assert.Equal(100, values["crmBuyerUnitId"]);
        Assert.Equal(10, values["crmBuyerProjectId"]);
        Assert.Equal("Sami Nasser", values["crmBuyerCustomerName"]);
        Assert.Equal("Tiger Sky Tower", values["crmBuyerProjectName"]);
        Assert.Equal("1205", values["crmBuyerUnitNumber"]);
    }

    [Fact]
    public void OnPostUseCrmBuyerUnit_NameContainingColon_SurvivesEscapingRoundTrip()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel();
        var packed = string.Join(':',
            5001, 900, 100, 10,
            Uri.EscapeDataString("Nasser: Holdings"), Uri.EscapeDataString("Tower: North"), Uri.EscapeDataString("12:05"));

        var result = model.OnPostUseCrmBuyerUnit(42, "+971501234567", packed);

        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal("Nasser: Holdings", values["crmBuyerCustomerName"]);
        Assert.Equal("Tower: North", values["crmBuyerProjectName"]);
        Assert.Equal("12:05", values["crmBuyerUnitNumber"]);
    }

    [Fact]
    public void OnPostUseExternalUnit_CarriesTheSelectionToThePropertyStep()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel();
        var packed = PackedExternal();

        var result = model.OnPostUseExternalUnit(42, "+971509990002", "ext:Pact:7001", packed);

        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal(NewTicketModel.StepProperty, values["step"]);
        Assert.Equal("ext:Pact:7001", values["customer"]);
        Assert.Equal(packed, values["externalSelection"]);
    }

    [Fact]
    public async Task OnPostUseManualUnitAsync_BothFieldsSupplied_CarriesThemToThePropertyStep()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel();
        model.CreateStep = new NewTicketModel.CreateStepInput { ManualProjectName = "Tiger Tower A", ManualUnitNumber = "1204" };

        var result = await model.OnPostUseManualUnitAsync(42, "+9613040922", CancellationToken.None);

        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal(NewTicketModel.StepProperty, values["step"]);
        Assert.Equal("manual", values["customer"]);
        Assert.Equal("Tiger Tower A", values["manualProjectName"]);
        Assert.Equal("1204", values["manualUnitNumber"]);
    }

    [Fact]
    public async Task OnPostUseManualUnitAsync_MissingUnitNumber_RedisplaysWithError_NothingCarried()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel();
        model.CreateStep = new NewTicketModel.CreateStepInput { ManualProjectName = "Tiger Tower A" };

        var result = await model.OnPostUseManualUnitAsync(42, "+9613040922", CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(NewTicketModel.StepProperty, model.Step);
        Assert.NotNull(model.ErrorMessage);
        Assert.False(model.HasUnitSelection);
    }

    [Fact]
    public async Task OnGetAsync_PropertyStep_CrmCustomer_ListsTheirUnits_NoneAutoSelected()
    {
        var buyer = new CrmBuyerMatchDto(
            new CrmCustomerDto(5001, "Sami Nasser", null, "+971501234567", null),
            [
                new CrmBuyerUnitDto(900, 8, "Sold", 100, "1205", 1, 1, 4, 10, "Tiger Sky Tower", null, 1, "Buyer"),
                new CrmBuyerUnitDto(901, 4, "Contract", 101, "2004", 1, 1, 8, 10, "Tiger Sky Tower", null, 1, "Buyer")
            ]);
        var (model, _, _, _, _, _, _, _) = CreateModel(crmBuyerLookupResponder: CrmBuyersFound(buyer));

        await GetAsync(model, step: NewTicketModel.StepProperty, intakeRecordId: 42, phoneNumber: "+971501234567", customer: "crm");

        Assert.NotNull(model.CrmBuyerMatch);
        Assert.Equal(2, model.CrmBuyerMatch!.Units.Count);
        Assert.False(model.HasUnitSelection);
        Assert.Null(model.ExternalSelection);
    }

    [Fact]
    public async Task OnGetAsync_PropertyStep_WithCrmUnitSelected_RunsTheRelatedTicketsCheck_ForTheSelectedUnitActiveFirst()
    {
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(
                SingleUnitBuyer(5002, "Ahmad Ali Hassan", "+971501234567", 903, 103, 10, "2004", "Tiger Sky Tower")),
            customerHistoryResponder: (request, _) => request.RequestUri!.Query.Contains("unitNumber")
                ? FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerHistoryDto("Verified", 5002, null, "Ahmad Ali Hassan", 1, 1, 0,
                    [new CustomerHistoryTicketDto(61, "TG-CS-20260830-0002", DateTime.UtcNow.AddDays(-1), "InProgress", 2, 2, 2, "Tiger Sky Tower", "2004", "Verified", "Water leakage")]))
                : FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerHistoryDto("Verified", 5002, null, "Ahmad Ali Hassan", 0, 0, 0, [])));

        await GetAsync(model,
            step: NewTicketModel.StepProperty, intakeRecordId: 42, phoneNumber: "+971501234567", customer: "crm",
            crmBuyerCustomerId: 5002, crmBuyerLeadId: 903, crmBuyerUnitId: 103, crmBuyerProjectId: 10,
            crmBuyerCustomerName: "Ahmad Ali Hassan", crmBuyerProjectName: "Tiger Sky Tower", crmBuyerUnitNumber: "2004");

        // One scoped related-tickets query — same identity (5002), the
        // selected unit, active tickets first. Never a call per row.
        var related = Assert.Single(customerHistory.Requests, r => r.RequestUri.Contains("unitNumber"));
        Assert.Contains("/api/customers/crm/5002/ticket-history", related.RequestUri);
        Assert.Contains("unitNumber=2004", related.RequestUri);
        Assert.Contains("orderActiveFirst=true", related.RequestUri);

        Assert.True(model.HasUnitSelection);
        Assert.NotNull(model.RelatedTickets);
        var row = Assert.Single(model.RelatedTickets!.Tickets);
        Assert.Equal("Water leakage", row.RequestSummary);
    }

    [Fact]
    public async Task OnGetAsync_PropertyStep_WithExternalUnitSelected_RunsTheRelatedTicketsCheck_ByThePersistedExternalIdentity()
    {
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(),
            customerLookupResponder: CustomerLookupReturning(
                CustomerLookupSourceResultDto.NotFound("Crm"),
                CustomerLookupSourceResultDto.Found("Pact", [PactCustomerWithTwoUnits()])));

        await GetAsync(model,
            step: NewTicketModel.StepProperty, intakeRecordId: 42, phoneNumber: "+971509990002",
            customer: "ext:Pact:7001", externalSelection: PackedExternal());

        var related = Assert.Single(customerHistory.Requests, r => r.RequestUri.Contains("unitNumber"));
        Assert.Contains("/api/customers/external/Pact/7001/ticket-history", related.RequestUri);
        Assert.Contains("unitNumber=1105", related.RequestUri);
        Assert.True(model.HasUnitSelection);
    }

    [Fact]
    public async Task OnGetAsync_PropertyStep_ManualSelection_RunsNoRelatedCheck_NoIdentityMeansNoAssociation()
    {
        var (model, _, _, _, _, _, customerHistory, _) = CreateModel();

        await GetAsync(model,
            step: NewTicketModel.StepProperty, intakeRecordId: 42, phoneNumber: "+9613040922",
            customer: "manual", manualProjectName: "Tiger Tower A", manualUnitNumber: "1204");

        Assert.Empty(customerHistory.Requests);
        Assert.Null(model.RelatedTickets);
        Assert.True(model.HasUnitSelection);
    }

    [Fact]
    public async Task OnGetAsync_PropertyStep_RelatedCheckFailure_NeverBlocksTheWizard()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(
            crmBuyerLookupResponder: CrmBuyersFound(
                SingleUnitBuyer(5002, "Ahmad Ali Hassan", "+971501234567", 903, 103, 10, "2004", "Tiger Sky Tower")),
            customerHistoryResponder: (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));

        await GetAsync(model,
            step: NewTicketModel.StepProperty, intakeRecordId: 42, phoneNumber: "+971501234567", customer: "crm",
            crmBuyerCustomerId: 5002, crmBuyerLeadId: 903, crmBuyerUnitId: 103, crmBuyerProjectId: 10,
            crmBuyerCustomerName: "Ahmad Ali Hassan", crmBuyerProjectName: "Tiger Sky Tower", crmBuyerUnitNumber: "2004");

        Assert.Null(model.RelatedTickets);
        Assert.Null(model.ErrorMessage);
        Assert.True(model.HasUnitSelection);
    }

    // ---- Back / state preservation ----

    [Fact]
    public async Task OnGetAsync_PropertyStep_ReturningWithACarriedSelection_KeepsIt_WithoutReRunningTheBuyerSearch()
    {
        // Going Back to Step 2 with a manual selection carried: nothing about
        // the selection is re-derived, and no verification re-runs for the
        // manual path (the CRM/external paths re-read their unit lists to
        // render them — reads, not re-verification of the selection).
        var (model, _, crmBuyerLookup, _, _, _, _, customerLookup) = CreateModel();

        await GetAsync(model,
            step: NewTicketModel.StepProperty, intakeRecordId: 42, phoneNumber: "+9613040922",
            customer: "manual", manualProjectName: "Tiger Tower A", manualUnitNumber: "1204");

        Assert.Empty(crmBuyerLookup.Requests);
        Assert.Empty(customerLookup.Requests);
        Assert.Equal("Tiger Tower A", model.SummaryProjectName);
        Assert.Equal("1204", model.SummaryUnitNumber);
    }

    [Fact]
    public async Task OnGetAsync_IssueStep_CarriesTheSelectionIntoTheSummary()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 2, "Facilities Management")));

        await GetAsync(model,
            step: NewTicketModel.StepIssue, intakeRecordId: 42, phoneNumber: "+971501234567", customer: "crm",
            crmBuyerCustomerId: 5001, crmBuyerLeadId: 900, crmBuyerUnitId: 100, crmBuyerProjectId: 10,
            crmBuyerCustomerName: "Sami Nasser", crmBuyerProjectName: "Tiger Sky Tower", crmBuyerUnitNumber: "1205");

        Assert.Equal("Sami Nasser", model.SummaryCustomerName);
        Assert.Equal("Tiger Sky Tower", model.SummaryProjectName);
        Assert.Equal("1205", model.SummaryUnitNumber);
        Assert.Equal("Crm", model.SummarySourceKey);
    }

    // ---- Step 3: Issue — Department narrows Request Types; entered values survive ----

    [Fact]
    public async Task OnGetAsync_IssueStep_LoadsDepartmentsAndCategories()
    {
        var (model, _, _, departments, categories, _, _, _) = CreateModel(
            departmentsResponder: DepartmentsReturning(new DepartmentDto(7, "Facilities Management")),
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 7, "Facilities Management")));

        await GetAsync(model,
            step: NewTicketModel.StepIssue, intakeRecordId: 42, phoneNumber: "+9613040922",
            customer: "manual", manualProjectName: "Tiger Tower A", manualUnitNumber: "1204");

        Assert.Single(departments.Requests);
        var categoriesRequest = Assert.Single(categories.Requests);
        Assert.DoesNotContain("departmentId", categoriesRequest.RequestUri);
        Assert.Single(model.Departments);
        Assert.Single(model.Categories);
    }

    [Fact]
    public async Task OnGetAsync_IssueStep_WithDepartment_RequestsCategoriesFilteredByThatDepartment()
    {
        var (model, _, _, _, categories, _, _, _) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 7, "Facilities Management")));

        await GetAsync(model,
            step: NewTicketModel.StepIssue, intakeRecordId: 42, phoneNumber: "+9613040922",
            customer: "manual", manualProjectName: "Tiger Tower A", manualUnitNumber: "1204", departmentId: 7);

        Assert.Contains("departmentId=7", Assert.Single(categories.Requests).RequestUri);
    }

    [Fact]
    public async Task OnGetAsync_IssueStep_CategoriesApiFails_SetsCategoriesErrorMessage_NoNumericFallback()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(categoriesResponder: (_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        await GetAsync(model,
            step: NewTicketModel.StepIssue, intakeRecordId: 42, phoneNumber: "+9613040922",
            customer: "manual", manualProjectName: "Tiger Tower A", manualUnitNumber: "1204");

        Assert.Empty(model.Categories);
        Assert.NotNull(model.CategoriesErrorMessage);
    }

    [Fact]
    public async Task OnGetAsync_IssueStep_DepartmentsApiUnauthorized_SetsPredictableAuthMessage()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(
            departmentsResponder: (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            categoriesResponder: CategoriesReturning());

        await GetAsync(model,
            step: NewTicketModel.StepIssue, intakeRecordId: 42, phoneNumber: "+9613040922",
            customer: "manual", manualProjectName: "Tiger Tower A", manualUnitNumber: "1204");

        Assert.Empty(model.Departments);
        Assert.NotNull(model.DepartmentsErrorMessage);
        Assert.Contains("not authorized", model.DepartmentsErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnPostIssueRefreshAsync_ReloadsCategoriesForTheChosenDepartment_KeepingEnteredValues()
    {
        var (model, _, _, _, categories, _, _, _) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 7, "Facilities Management")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            DepartmentId = 7, PriorityId = 2, RequestSummary = "AC not cooling",
            ManualProjectName = "Tiger Tower A", ManualUnitNumber = "1204"
        };

        var result = await model.OnPostIssueRefreshAsync(
            42, "+9613040922", "manual", null, null, null, null, null, null, null, null,
            "Tiger Tower A", "1204", CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(NewTicketModel.StepIssue, model.Step);
        Assert.Contains("departmentId=7", Assert.Single(categories.Requests).RequestUri);
        // Back-and-forth never loses what the agent already entered.
        Assert.Equal((byte)2, model.CreateStep.PriorityId);
        Assert.Equal("AC not cooling", model.CreateStep.RequestSummary);
    }

    [Fact]
    public async Task OnPostIssueRefreshAsync_RequestTypeOutsideTheChosenDepartment_IsCleared_NeverSilentlySubmitted()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(9, "Tenancy Inquiry", 5, "Leasing")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            DepartmentId = 5, CategoryId = 2, PriorityId = 2, RequestSummary = "x"
        };

        await model.OnPostIssueRefreshAsync(
            42, "+9613040922", "manual", null, null, null, null, null, null, null, null,
            "Tiger Tower A", "1204", CancellationToken.None);

        Assert.Null(model.CreateStep.CategoryId);
    }

    // ---- Step 4: Review — validation happens before review, creation from review ----

    [Fact]
    public async Task OnPostReviewAsync_ValidIssue_RendersTheReviewStep_ResolvingDisplayNames()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 7, "Facilities Management")));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = 2, PriorityId = 2, RequestSummary = "AC not cooling"
        };

        var result = await model.OnPostReviewAsync(
            42, "+971501234567", "crm", 5001, 900, 100, 10, "Sami Nasser", "Tiger Sky Tower", "1205",
            null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(NewTicketModel.StepReview, model.Step);
        Assert.Equal("Corrective Maintenance", model.SelectedCategory!.Name);
        Assert.Equal("Facilities Management", model.SummaryDepartmentName);
    }

    [Fact]
    public async Task OnPostReviewAsync_NoRequestType_RedisplaysIssue_WithoutCallingTheTicketsApi()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(categoriesResponder: CategoriesReturning());
        model.CreateStep = new NewTicketModel.CreateStepInput { PriorityId = 2, RequestSummary = "x" };

        var result = await model.OnPostReviewAsync(
            42, "+971501234567", "crm", 5001, 900, 100, 10, "Sami Nasser", "Tiger Sky Tower", "1205",
            null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(NewTicketModel.StepIssue, model.Step);
        Assert.NotNull(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostReviewAsync_NoPriority_RedisplaysIssue()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(categoriesResponder: CategoriesReturning());
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, RequestSummary = "x" };

        var result = await model.OnPostReviewAsync(
            42, "+971501234567", "crm", 5001, 900, 100, 10, "Sami Nasser", "Tiger Sky Tower", "1205",
            null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(NewTicketModel.StepIssue, model.Step);
        Assert.Contains("priority", model.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnPostReviewAsync_NoCrmMatch_ManualPairMissing_RedisplaysIssue()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(categoriesResponder: CategoriesReturning());
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 2, RequestSummary = "x" };

        var result = await model.OnPostReviewAsync(
            42, "+9613040922", "manual", null, null, null, null, null, null, null,
            null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(NewTicketModel.StepIssue, model.Step);
        Assert.Contains("Project and Unit Number are required", model.ErrorMessage);
    }

    [Fact]
    public void CreateStepInput_PriorityId_IsNullableAndRequired()
    {
        var property = typeof(NewTicketModel.CreateStepInput).GetProperty("PriorityId");
        Assert.NotNull(property);
        Assert.Equal(typeof(byte?), property!.PropertyType);

        var input = new NewTicketModel.CreateStepInput { CategoryId = 1, PriorityId = null, RequestSummary = "x" };
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("PriorityId"));
    }

    // ---- Create: exactly what POST /api/tickets always received ----

    [Fact]
    public async Task OnPostCreateAsync_CrmMatchSelected_SendsAllFourCrmIdsAndSnapshot_NoManualFields()
    {
        var (model, _, _, _, _, tickets, _, _) = CreateModel(
            ticketsResponder: TicketCreated(300, "TG-FM-20260903-0001"));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 2, RequestSummary = "AC not cooling" };

        var result = await model.OnPostCreateAsync(
            42, "+971501234567", "crm", 5001, 900, 100, 10, "Sami Nasser", "Tiger Sky Tower", "1205",
            null, null, null, CancellationToken.None);

        var values = RouteValues(Assert.IsType<RedirectToPageResult>(result));
        Assert.Equal(NewTicketModel.StepDone, values["step"]);
        Assert.Equal(300L, values["createdTicketId"]);
        Assert.Equal("TG-FM-20260903-0001", values["createdTicketNumber"]);

        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.Equal(5001, body.RootElement.GetProperty("crmBuyerCustomerId").GetInt32());
        Assert.Equal(900, body.RootElement.GetProperty("crmBuyerLeadId").GetInt32());
        Assert.Equal(100, body.RootElement.GetProperty("crmBuyerUnitId").GetInt32());
        Assert.Equal(10, body.RootElement.GetProperty("crmBuyerProjectId").GetInt32());
        Assert.Equal("Sami Nasser", body.RootElement.GetProperty("crmBuyerCustomerName").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("manualProjectName").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("manualUnitNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("customerVerificationSource").ValueKind);
    }

    [Fact]
    public async Task OnPostCreateAsync_ExternalSelection_SendsExternalIdentityPlusManualSnapshotFromTheSelectedUnit()
    {
        var (model, _, _, _, _, tickets, _, _) = CreateModel(
            ticketsResponder: TicketCreated(400, "TG-LS-20260903-0001"));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 3, RequestSummary = "AC fault" };

        var result = await model.OnPostCreateAsync(
            42, "+971509990002", "ext:Pact:7001", null, null, null, null, null, null, null,
            PackedExternal(), null, null, CancellationToken.None);

        Assert.Equal(NewTicketModel.StepDone, RouteValues(Assert.IsType<RedirectToPageResult>(result))["step"]);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        // The SELECTED PACT unit persists through the existing manual
        // Project/Unit snapshot AND the generic external identity.
        Assert.Equal("Tiger Bay Towers", body.RootElement.GetProperty("manualProjectName").GetString());
        Assert.Equal("1105", body.RootElement.GetProperty("manualUnitNumber").GetString());
        Assert.Equal("Pact", body.RootElement.GetProperty("customerVerificationSource").GetString());
        Assert.Equal("7001", body.RootElement.GetProperty("externalCustomerId").GetString());
        Assert.Equal("701", body.RootElement.GetProperty("externalUnitId").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("crmBuyerCustomerId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("crmBuyerUnitId").ValueKind);
    }

    [Fact]
    public async Task OnPostCreateAsync_ManualEntry_SendsManualFields_NeverAnExternalIdentity_NeverRunningAnotherCrmLookup()
    {
        var (model, _, _, _, _, tickets, _, _) = CreateModel(
            ticketsResponder: TicketCreated(401, "TG-LS-20260903-0002"));
        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = 2, PriorityId = 3, RequestSummary = "x",
            ManualProjectName = "Tiger Marina Residences", ManualUnitNumber = "0000"
        };

        var result = await model.OnPostCreateAsync(
            42, "+971509990002", "manual", null, null, null, null, null, null, null,
            null, "Tiger Marina Residences", "0000", CancellationToken.None);

        Assert.Equal(NewTicketModel.StepDone, RouteValues(Assert.IsType<RedirectToPageResult>(result))["step"]);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.Equal("0000", body.RootElement.GetProperty("manualUnitNumber").GetString());
        // Plain manual entry is never presented as externally verified.
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("customerVerificationSource").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("externalCustomerId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("externalUnitId").ValueKind);
    }

    [Fact]
    public async Task OnPostCreateAsync_NoPrioritySelected_RejectedWithoutCallingTheTicketsApi()
    {
        var (model, _, _, _, _, tickets, _, _) = CreateModel(categoriesResponder: CategoriesReturning());
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, RequestSummary = "x" };

        var result = await model.OnPostCreateAsync(
            42, "+971501234567", "crm", 5001, 900, 100, 10, "Sami Nasser", "Tiger Sky Tower", "1205",
            null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(NewTicketModel.StepIssue, model.Step);
        Assert.Empty(tickets.Requests);
    }

    [Fact]
    public async Task OnPostCreateAsync_ManualPairMissing_RejectedWithoutCallingTheTicketsApi()
    {
        var (model, _, _, _, _, tickets, _, _) = CreateModel(categoriesResponder: CategoriesReturning());
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 2, RequestSummary = "x" };

        var result = await model.OnPostCreateAsync(
            42, "+9613040922", "manual", null, null, null, null, null, null, null,
            null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Empty(tickets.Requests);
        Assert.Contains("Project and Unit Number are required", model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostCreateAsync_ApiFailure_RedisplaysTheReview_WithTheApiDetail()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel(
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 7, "Facilities Management")),
            ticketsResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.UnprocessableEntity, new { detail = "Category is inactive." }));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 2, RequestSummary = "x" };

        var result = await model.OnPostCreateAsync(
            42, "+971501234567", "crm", 5001, 900, 100, 10, "Sami Nasser", "Tiger Sky Tower", "1205",
            null, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(NewTicketModel.StepReview, model.Step);
        Assert.Equal("Category is inactive.", model.ErrorMessage);
    }

    // ---- Done ----

    [Fact]
    public async Task OnGetAsync_DoneStep_ShowsTheCreatedTicket()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel();

        await GetAsync(model, step: NewTicketModel.StepDone, createdTicketId: 300, createdTicketNumber: "TG-FM-20260903-0001");

        Assert.Equal(NewTicketModel.StepDone, model.Step);
        Assert.Equal(300L, model.CreatedTicketId);
        Assert.Equal("TG-FM-20260903-0001", model.CreatedTicketNumber);
    }

    [Fact]
    public async Task OnGetAsync_DoneStep_WithoutACreatedTicket_RedirectsToAFreshWizard()
    {
        var (model, _, _, _, _, _, _, _) = CreateModel();

        var result = await GetAsync(model, step: NewTicketModel.StepDone);

        Assert.IsType<RedirectToPageResult>(result);
    }

    // ---- Full flows through the four steps ----

    [Fact]
    public async Task FullFlow_SearchSelectCrmCustomerAndUnitIssueReviewCreate_Succeeds()
    {
        var buyer = SingleUnitBuyer(5001, "Sami Nasser", "+971501234567", 900, 100, 10, "1205", "Tiger Sky Tower");
        var (model, _, _, _, _, tickets, _, _) = CreateModel(
            intakeResponder: (_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.Created,
                new IntakeRecordResponseDto(42, "Phone", DateTime.UtcNow, "+971501234567", null, false, null, null, "Unverified", null)),
            crmBuyerLookupResponder: CrmBuyersFound(buyer),
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Corrective Maintenance", 7, "Facilities Management")),
            ticketsResponder: TicketCreated(300, "TG-FM-20260903-0001"));

        // Step 1 — search, results, pick the customer.
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+971501234567" };
        var intakeRedirect = RouteValues(Assert.IsType<RedirectToPageResult>(await model.OnPostIntakeAsync(CancellationToken.None)));
        await GetAsync(model, step: (string?)intakeRedirect["step"], intakeRecordId: 42, phoneNumber: "+971501234567");
        Assert.Equal("crm", Assert.Single(model.Candidates).Key);

        // Step 2 — select the unit.
        var unit = buyer.Units[0];
        var packed = string.Join(':',
            buyer.Customer.CustomerId, unit.LeadId, unit.UnitId, unit.ProjectId,
            Uri.EscapeDataString(buyer.Customer.FullNameEnglish!),
            Uri.EscapeDataString(unit.ProjectName!), Uri.EscapeDataString(unit.UnitNumber!));
        var selectRedirect = RouteValues(Assert.IsType<RedirectToPageResult>(
            model.OnPostUseCrmBuyerUnit(42, "+971501234567", packed)));
        await GetAsync(model,
            step: (string?)selectRedirect["step"], intakeRecordId: 42, phoneNumber: "+971501234567",
            customer: (string?)selectRedirect["customer"],
            crmBuyerCustomerId: (int?)selectRedirect["crmBuyerCustomerId"],
            crmBuyerLeadId: (int?)selectRedirect["crmBuyerLeadId"],
            crmBuyerUnitId: (int?)selectRedirect["crmBuyerUnitId"],
            crmBuyerProjectId: (int?)selectRedirect["crmBuyerProjectId"],
            crmBuyerCustomerName: (string?)selectRedirect["crmBuyerCustomerName"],
            crmBuyerProjectName: (string?)selectRedirect["crmBuyerProjectName"],
            crmBuyerUnitNumber: (string?)selectRedirect["crmBuyerUnitNumber"]);
        Assert.True(model.HasUnitSelection);

        // Step 3 → Review → Create.
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 2, RequestSummary = "AC not cooling" };
        var review = await model.OnPostReviewAsync(
            42, "+971501234567", "crm", 5001, 900, 100, 10, "Sami Nasser", "Tiger Sky Tower", "1205",
            null, null, null, CancellationToken.None);
        Assert.IsType<PageResult>(review);
        Assert.Equal(NewTicketModel.StepReview, model.Step);

        var create = await model.OnPostCreateAsync(
            42, "+971501234567", "crm", 5001, 900, 100, 10, "Sami Nasser", "Tiger Sky Tower", "1205",
            null, null, null, CancellationToken.None);
        Assert.Equal(NewTicketModel.StepDone, RouteValues(Assert.IsType<RedirectToPageResult>(create))["step"]);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.Equal("AC not cooling", body.RootElement.GetProperty("requestSummary").GetString());
        Assert.Equal(5001, body.RootElement.GetProperty("crmBuyerCustomerId").GetInt32());
    }

    [Fact]
    public async Task FullFlow_CustomerNotFound_ManualEntry_Succeeds()
    {
        var (model, _, _, _, _, tickets, _, _) = CreateModel(
            crmBuyerLookupResponder: (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound),
            categoriesResponder: CategoriesReturning(new CategoryDto(2, "Tenancy Inquiry", 5, "Leasing")),
            ticketsResponder: TicketCreated(401, "TG-LS-20260903-0002"));

        await GetAsync(model, step: NewTicketModel.StepCustomer, intakeRecordId: 42, phoneNumber: "+9613040922");
        Assert.Empty(model.Candidates);

        model.CreateStep = new NewTicketModel.CreateStepInput { ManualProjectName = "Tiger Tower A", ManualUnitNumber = "1204" };
        var manualRedirect = RouteValues(Assert.IsType<RedirectToPageResult>(
            await model.OnPostUseManualUnitAsync(42, "+9613040922", CancellationToken.None)));
        Assert.Equal("manual", manualRedirect["customer"]);

        model.CreateStep = new NewTicketModel.CreateStepInput
        {
            CategoryId = 2, PriorityId = 3, RequestSummary = "x",
            ManualProjectName = "Tiger Tower A", ManualUnitNumber = "1204"
        };
        var create = await model.OnPostCreateAsync(
            42, "+9613040922", "manual", null, null, null, null, null, null, null,
            null, "Tiger Tower A", "1204", CancellationToken.None);

        Assert.Equal(NewTicketModel.StepDone, RouteValues(Assert.IsType<RedirectToPageResult>(create))["step"]);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.Equal("Tiger Tower A", body.RootElement.GetProperty("manualProjectName").GetString());
        Assert.Equal("1204", body.RootElement.GetProperty("manualUnitNumber").GetString());
    }
}
