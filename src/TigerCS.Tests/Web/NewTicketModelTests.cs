// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj — because
// its own top-level-statement Program type would otherwise collide with
// TigerCS.Api's, which existing WebApplicationFactory<Program> tests use unqualified.
extern alias TigerCsWeb;

using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// Covers the New Ticket wizard's PageModel: Intake → Customer Lookup → Ticket,
/// against TigerCS.Api's real DTO contracts with <see cref="FakeApiHandler"/>
/// standing in for the Api itself. No ASP.NET Core host is spun up — like the
/// app-service tests elsewhere in this project, each handler is exercised
/// directly against fakes at its one real dependency boundary (here, HTTP).
/// </summary>
public sealed class NewTicketModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static (NewTicketModel Model, FakeApiHandler Intake, FakeApiHandler Lookup, FakeApiHandler Tickets) CreateModel(
        Func<HttpRequestMessage, string?, HttpResponseMessage>? intakeResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? lookupResponder = null,
        Func<HttpRequestMessage, string?, HttpResponseMessage>? ticketsResponder = null)
    {
        var intakeHandler = new FakeApiHandler(intakeResponder ?? ((_, _) => throw new InvalidOperationException("Intake API not expected to be called.")));
        var lookupHandler = new FakeApiHandler(lookupResponder ?? ((_, _) => throw new InvalidOperationException("Customer lookup API not expected to be called.")));
        var ticketsHandler = new FakeApiHandler(ticketsResponder ?? ((_, _) => throw new InvalidOperationException("Tickets API not expected to be called.")));

        var intakeClient = new IntakeRecordsApiClient(new HttpClient(intakeHandler) { BaseAddress = new Uri("http://localhost/") });
        var lookupClient = new CustomerLookupApiClient(new HttpClient(lookupHandler) { BaseAddress = new Uri("http://localhost/") });
        var ticketsClient = new TicketsApiClient(new HttpClient(ticketsHandler) { BaseAddress = new Uri("http://localhost/") });

        var model = new NewTicketModel(intakeClient, lookupClient, ticketsClient);
        return (model, intakeHandler, lookupHandler, ticketsHandler);
    }

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

    // ---- 3: Intake API receives PhoneNumber and DepartmentId ----

    [Fact]
    public async Task OnPostIntakeAsync_SendsPhoneNumberAndDepartmentIdToIntakeApi()
    {
        var (model, intake, _, _) = CreateModel(intakeResponder: (_, _) =>
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

    // ---- 4: Customer lookup is called after successful Intake creation ----

    [Fact]
    public async Task OnPostIntakeAsync_Success_RedirectsToLookupStep_CarryingIntakeRecordIdAndPhoneNumber()
    {
        var (model, _, _, _) = CreateModel(intakeResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new IntakeRecordResponseDto(
                42, "Phone", DateTime.UtcNow, "+15551234567", null, false, null, null, "Unverified", null)));
        model.Intake = new NewTicketModel.IntakeInput { ChannelId = "Phone", PhoneNumber = "+15551234567" };

        var result = await model.OnPostIntakeAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        var values = RouteValues(redirect);
        Assert.Equal("lookup", values["step"]);
        Assert.Equal(42L, values["intakeRecordId"]);
        Assert.Equal("+15551234567", values["phoneNumber"]);
    }

    [Fact]
    public async Task OnGetAsync_LookupStep_CallsCustomerLookupApi_ForTheIntakeRecord()
    {
        var (model, _, lookup, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567", [])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, CancellationToken.None);

        var sent = Assert.Single(lookup.Requests);
        Assert.Equal("http://localhost/api/intake-records/42/customer-lookup", sent.RequestUri);
    }

    // ---- 5, 6, 7: Found / NotFound / Failed all populate LookupResult and never block continuing ----

    [Fact]
    public async Task OnGetAsync_Lookup_Found_PopulatesLookupResultWithSourceDetails()
    {
        var (model, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567",
            [
                new CustomerLookupSourceResultDto("Crm", "Found", "Jane Doe", "+15551234567", "12B", 5, 9)
            ])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, CancellationToken.None);

        Assert.Null(model.ErrorMessage);
        var source = Assert.Single(model.LookupResult!.Sources);
        Assert.Equal("Found", source.Status);
        Assert.Equal("Jane Doe", source.DisplayName);
        Assert.Equal(5, source.UnitReferenceId);
        Assert.Equal(9, source.ContactReferenceId);
    }

    [Fact]
    public async Task OnGetAsync_Lookup_NotFound_StillPopulatesLookupResult_NoErrorMessage()
    {
        var (model, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567",
            [
                new CustomerLookupSourceResultDto("Crm", "NotFound", null, null, null, null, null)
            ])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, CancellationToken.None);

        Assert.Null(model.ErrorMessage);
        Assert.Equal("NotFound", Assert.Single(model.LookupResult!.Sources).Status);
    }

    [Fact]
    public async Task OnGetAsync_Lookup_Failed_StillPopulatesLookupResult_NoErrorMessage()
    {
        var (model, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567",
            [
                new CustomerLookupSourceResultDto("Pact", "Failed", null, null, null, null, null)
            ])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, CancellationToken.None);

        // A source being unavailable is not an error state for the page — Continue must remain reachable.
        Assert.Null(model.ErrorMessage);
        Assert.Equal("Failed", Assert.Single(model.LookupResult!.Sources).Status);
    }

    // ---- 8: Partial results (Found + Failed + NotFound) all render independently ----

    [Fact]
    public async Task OnGetAsync_Lookup_PartialResultsAcrossSources_AllPresentIndependently()
    {
        var (model, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567",
            [
                new CustomerLookupSourceResultDto("Crm", "Found", "Jane Doe", "+15551234567", "12B", 5, 9),
                new CustomerLookupSourceResultDto("Pact", "Failed", null, null, null, null, null),
                new CustomerLookupSourceResultDto("Tasleeh", "NotFound", null, null, null, null, null)
            ])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, CancellationToken.None);

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
        var (model, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567",
            [
                new CustomerLookupSourceResultDto("Crm", "NotFound", null, null, null, null, null),
                new CustomerLookupSourceResultDto("Pact", "NotFound", null, null, null, null, null),
                new CustomerLookupSourceResultDto("Tasleeh", "NotFound", null, null, null, null, null)
            ])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, CancellationToken.None);

        Assert.Equal(["Crm", "Pact", "Tasleeh"], model.LookupResult!.Sources.Select(s => s.Source));
    }

    [Fact]
    public async Task OnGetAsync_Lookup_DepartmentScoped_OnlyTheSourcesTheApiActuallySearchedAreShown()
    {
        // The Web page never invents source-selection logic of its own: it renders exactly the
        // Sources list the Api returns, so a Department-scoped search (Api returns just Pact)
        // must never grow a fabricated "Crm → NotFound"/"Tasleeh → NotFound" entry client-side.
        var (model, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567",
            [
                new CustomerLookupSourceResultDto("Pact", "Found", "Jane Doe", "+15551234567", null, null, null)
            ])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, CancellationToken.None);

        Assert.Equal(["Pact"], model.LookupResult!.Sources.Select(s => s.Source));
    }

    [Fact]
    public async Task OnGetAsync_Lookup_ConfigurationMissing_EmptySourcesList_NoErrorMessage()
    {
        var (model, _, _, _) = CreateModel(lookupResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(42, "+15551234567", [])));

        await model.OnGetAsync("lookup", 42, "+15551234567", null, null, CancellationToken.None);

        Assert.Null(model.ErrorMessage);
        Assert.Empty(model.LookupResult!.Sources);
    }

    // ---- 11: ticket creation uses only POST /api/tickets ----

    [Fact]
    public async Task OnPostCreateAsync_CallsOnlyPostApiTickets_WithNoVerificationSessionField()
    {
        var (model, _, _, tickets) = CreateModel(ticketsResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                100, "TG-CS-20260825-0001", 7, 7, 5, 9, 2, 3, "Open", "Unverified", "None", "Running", "Summary", DateTime.UtcNow, "AAAA")));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 3, RequestSummary = "Summary" };

        await model.OnPostCreateAsync(42, "+15551234567", 5, 9, CancellationToken.None);

        var sent = Assert.Single(tickets.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://localhost/api/tickets", sent.RequestUri);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.False(body.RootElement.TryGetProperty("verificationSessionId", out _));
    }

    // ---- 12: optional UnitReferenceId/ContactReferenceId can be null ----

    [Fact]
    public async Task OnPostCreateAsync_WithNullOptionalReferences_StillCreatesTicket()
    {
        var (model, _, _, tickets) = CreateModel(ticketsResponder: (_, _) =>
            FakeApiHandler.JsonResponse(HttpStatusCode.Created, new TicketResponseDto(
                101, "TG-CS-20260825-0002", 7, 7, null, null, 2, 3, "Open", "Unverified", "None", "Running", "Summary", DateTime.UtcNow, "AAAA")));
        model.CreateStep = new NewTicketModel.CreateStepInput { CategoryId = 2, PriorityId = 3, RequestSummary = "Summary" };

        var result = await model.OnPostCreateAsync(42, "+15551234567", null, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/TicketDetails", redirect.PageName);
        using var body = JsonDocument.Parse(Assert.Single(tickets.Requests).Body!);
        Assert.True(body.RootElement.GetProperty("unitReferenceId").ValueKind == JsonValueKind.Null);
        Assert.True(body.RootElement.GetProperty("contactReferenceId").ValueKind == JsonValueKind.Null);
    }

    // ---- 13 & 14: multiple/none of the lookup results are selectable ----

    [Fact]
    public void OnPostUseMatch_CarriesSelectedReference_ToCreateStep()
    {
        var (model, _, _, _) = CreateModel();

        var result = model.OnPostUseMatch(42, "+15551234567", 5, 9);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        var values = RouteValues(redirect);
        Assert.Equal("create", values["step"]);
        Assert.Equal(5, values["unitReferenceId"]);
        Assert.Equal(9, values["contactReferenceId"]);
    }

    [Fact]
    public void OnPostContinueWithoutMatch_ProceedsWithNoReferenceSelected()
    {
        var (model, _, _, _) = CreateModel();

        var result = model.OnPostContinueWithoutMatch(42, "+15551234567");

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        var values = RouteValues(redirect);
        Assert.Equal("create", values["step"]);
        Assert.False(values.ContainsKey("unitReferenceId"));
    }

    private static IDictionary<string, object?> RouteValues(RedirectToPageResult redirect) =>
        redirect.RouteValues is null
            ? new Dictionary<string, object?>()
            : redirect.RouteValues.ToDictionary(kv => kv.Key, kv => kv.Value);
}
