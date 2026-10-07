// TigerCS.Web's Program is referenced via an extern alias so it never collides with
// TigerCS.Api's, which existing WebApplicationFactory<Program> tests use unqualified.
extern alias TigerCsWeb;

using System.Net;
using System.Text.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Tests.Web.Fakes;

namespace TigerCS.Tests.Web;

/// <summary>
/// Regression coverage for the New Ticket wizard's Step 1 search as REAL
/// HTTP requests through TigerCS.Web's own host — cookie auth conventions,
/// antiforgery, and (critically) the full MVC model-binding + validation
/// pipeline that direct PageModel handler calls skip.
///
/// The bug this pins: OnPostIntakeAsync gated on the page-wide
/// <c>ModelState.IsValid</c>, but the same PageModel co-binds
/// <c>CreateStep</c> (a [BindProperty] whose [Required] CategoryId/
/// PriorityId/RequestSummary the Step 1 Search form never posts), so the
/// pipeline marked ModelState invalid on EVERY intake POST and the page
/// silently re-rendered an empty Step 1 — no redirect, no error, no
/// customer data — even for a perfectly valid phone number. Unit tests
/// that invoke the handler directly can never catch that class of bug,
/// which is why this suite drives the wizard over HTTP.
/// </summary>
public sealed class NewTicketWizardHttpFlowTests
{
    private const string SearchedPhone = "971509724162";
    private const long IntakeId = 42;

    // -----------------------------------------------------------------
    // Host plumbing: TigerCS.Web's real Program, with only two seams —
    // an always-authenticated scheme standing in for the sign-in cookie,
    // and the typed Api HttpClients' primary handler swapped for the same
    // FakeApiHandler the unit tests use (the DI chain itself, including
    // BearerTokenHandler, still runs for real).
    // -----------------------------------------------------------------

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "cs.agent"), new Claim(ClaimTypes.Role, "CsAgent")],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private static WebApplicationFactory<TigerCsWeb::Program> CreateFactory(FakeApiHandler apiHandler) =>
        new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(defaultScheme: "TestAuth")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestAuth", _ => { });
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = apiHandler));
            });
        });

    /// <summary>GET /api/channels as the configured catalogue answers it — the nine approved active channels, in display order.</summary>
    private static HttpResponseMessage ChannelsResponse() =>
        FakeApiHandler.JsonResponse(HttpStatusCode.OK, new ChannelDto[]
        {
            new(1, "Phone", "PHONE", true, true, true, 1),
            new(6, "WhatsApp", "WHATSAPP", true, true, true, 2),
            new(7, "Live Chat", "LIVE_CHAT", true, true, true, 3),
            new(4, "Social Media Direct Message", "SOCIAL_DM", true, true, true, 4),
            new(8, "Website", "WEBSITE", true, false, true, 5),
            new(5, "Walk in / Kiosk", "WALK_IN_KIOSK", false, false, true, 6),
            new(9, "Mobile App (Customer Portal)", "MOBILE_APP", true, false, true, 7),
            new(10, "Instagram", "INSTAGRAM", true, true, true, 8),
            new(11, "Facebook", "FACEBOOK", true, true, true, 9)
        });

    /// <summary>The Api responses the happy-path search needs: the channel catalogue, intake creation, the department-aware lookup (Crm participates), the CRM Buyer match, and its bounded history.</summary>
    private static FakeApiHandler CrmFoundApi() => new((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;

        if (request.Method == HttpMethod.Get && path == "/api/channels")
        {
            return ChannelsResponse();
        }

        if (request.Method == HttpMethod.Post && path == "/api/intake-records")
        {
            return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new IntakeRecordResponseDto(
                IntakeId, "Phone", DateTime.UtcNow, SearchedPhone, null, false, null, null, "Unverified", null));
        }

        if (path == $"/api/intake-records/{IntakeId}/customer-lookup")
        {
            return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(
                IntakeId, SearchedPhone,
                [
                    CustomerLookupSourceResultDto.NotFound("Crm"),
                    CustomerLookupSourceResultDto.NotFound("Pact"),
                    CustomerLookupSourceResultDto.NotFound("Tasleeh"),
                ]));
        }

        if (path == "/api/crm/buyers")
        {
            return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new[]
            {
                new CrmBuyerMatchDto(
                    new CrmCustomerDto(5001, "Aisha Rahman", null, SearchedPhone, "aisha@example.com"),
                    [new CrmBuyerUnitDto(61, 4, "Contract", 601, "TB-1204", 1, 1, 12, 71, "Tiger Bay Towers", null, 1, "Buyer")]),
            });
        }

        if (path == "/api/customers/crm/5001/ticket-history")
        {
            return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerHistoryDto(
                "CrmVerified", 5001, SearchedPhone, "Aisha Rahman", 0, 0, 0, []));
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    [Fact]
    public async Task MatchingCrmAndPact_RenderOneCardWithPaymentsLinkEvenWithoutPreviousTickets()
    {
        var original = CrmFoundApi();
        var api = new FakeApiHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == $"/api/intake-records/{IntakeId}/customer-lookup")
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(IntakeId, SearchedPhone,
                    [CustomerLookupSourceResultDto.Found("Crm", []), CustomerLookupSourceResultDto.Found("Pact",
                        [new CustomerLookupCustomerDto("3001", "Aisha Rahman", "+" + SearchedPhone, "aisha@example.com", "2",
                            [new CustomerLookupUnitDto("999", "TB-1204", "Tiger Bay Towers", null, "Residential", null, null)])])]));
            if (request.RequestUri.AbsolutePath == "/api/customers/lookup/ticket-history")
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerHistoryDto("LinkedVerified", 5001, null, "Aisha", 0, 0, 0, []));
            return original.Respond(request, body);
        });
        using var factory = CreateFactory(api);
        var response = await factory.CreateClient().GetAsync($"/NewTicket?intakeRecordId={IntakeId}&phoneNumber={SearchedPhone}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Single(Regex.Matches(html, "candidate-card candidate-card--stack"));
        Assert.Contains("PACT · Tiger CRM", WebUtility.HtmlDecode(html));
        Assert.Contains("Payments &amp; Fines", html);
        Assert.Contains("customerKey=crm%3A5001", html);

        var property = await factory.CreateClient().GetAsync($"/NewTicket?step=property&customer=crm&intakeRecordId={IntakeId}&phoneNumber={SearchedPhone}");
        Assert.Equal(HttpStatusCode.OK, property.StatusCode);
        var propertyHtml = await property.Content.ReadAsStringAsync();
        Assert.Contains("PACT · Tiger CRM", WebUtility.HtmlDecode(propertyHtml));
        Assert.Single(Regex.Matches(propertyHtml, "data-unit-source=\"Pact\""));
        Assert.DoesNotContain("data-unit-source=\"Crm\"", propertyHtml);
        Assert.Contains("handler=UseExternalUnit", propertyHtml);
    }

    [Fact]
    public async Task Issue_RequestCategoryThenType_RendersDependentChoices_AndCreatesWithBothRealIds()
    {
        var api = new FakeApiHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/departments" => FakeApiHandler.JsonResponse(HttpStatusCode.OK,
                new[] { new DepartmentDto(1, "Customer Service"), new DepartmentDto(2, "Collections") }),
            "/api/categories" => FakeApiHandler.JsonResponse(HttpStatusCode.OK,
                new[] { new CategoryDto(10, "General Inquiry", 1, "Customer Service"), new CategoryDto(20, "Send Receipts", 2, "Collections") }),
            "/api/request-types" => FakeApiHandler.JsonResponse(HttpStatusCode.OK,
                new[] { new RequestTypeOptionDto(100, "General Inquiry", 1, 3, true), new RequestTypeOptionDto(200, "Send Receipts", 2, 3, true) }),
            "/api/tickets" => FakeApiHandler.JsonResponse(HttpStatusCode.Created,
                new TicketResponseDto(300, "TG-COL-20261006-0001", 2, 2, null, null, 20, 3,
                    "Open", "Unverified", "None", "Running", "Please send receipts", DateTime.UtcNow, "AAAA")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/NewTicket?step=issue&intakeRecordId=42&customer=manual&manualProjectName=Tower&manualUnitNumber=101");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Request Category *", html);
        Assert.Contains("Request Type *", html);
        Assert.DoesNotContain("<optgroup", html);
        Assert.DoesNotContain("value=\"request-type:200\"", html);
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(html),
            ["intakeRecordId"] = "42", ["customer"] = "manual",
            ["manualProjectName"] = "Tower", ["manualUnitNumber"] = "101",
            ["CreateStep.UseRequestCategoryPicker"] = "true", ["CreateStep.DepartmentId"] = "2",
            ["CreateStep.RequestTypeChoice"] = "request-type:100", ["CreateStep.PriorityId"] = "3",
            ["CreateStep.RequestSummary"] = "Please send receipts"
        };
        var refreshed = await client.PostAsync("/NewTicket?handler=IssueRefresh", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        html = await refreshed.Content.ReadAsStringAsync();
        Assert.Contains("value=\"request-type:200\"", html);
        Assert.DoesNotContain("value=\"request-type:100\"", html);
        Assert.DoesNotContain("value=\"category:20\"", html);
        Assert.Contains("Please send receipts", html);
        fields["__RequestVerificationToken"] = AntiforgeryToken(html);
        fields["CreateStep.RequestTypeChoice"] = "request-type:200";
        var review = await client.PostAsync("/NewTicket?handler=Review", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        html = await review.Content.ReadAsStringAsync();
        Assert.Contains("Create Ticket", html);
        Assert.Contains("<dt>Request Category</dt>", html);
        Assert.Contains("<dt>Request Type</dt>", html);
        Assert.Contains("Send Receipts", html);
        Assert.Contains("CreateStep.RequestTypeChoice", html);
        fields["__RequestVerificationToken"] = AntiforgeryToken(html);
        fields["CreateStep.CategoryId"] = "999";
        fields["CreateStep.RequestTypeId"] = "999";
        var created = await client.PostAsync("/NewTicket?handler=Create", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        using var body = JsonDocument.Parse(Assert.Single(api.Requests, r => r.Method == HttpMethod.Post && r.RequestUri.EndsWith("/api/tickets", StringComparison.Ordinal)).Body!);
        Assert.Equal(20, body.RootElement.GetProperty("categoryId").GetInt32());
        Assert.Equal(200, body.RootElement.GetProperty("requestTypeId").GetInt32());
        Assert.Equal("Please send receipts", body.RootElement.GetProperty("requestSummary").GetString());
        Assert.Equal("Tower", body.RootElement.GetProperty("manualProjectName").GetString());
        Assert.Equal("101", body.RootElement.GetProperty("manualUnitNumber").GetString());
    }

    /// <summary>Pulls the antiforgery token out of the rendered Search form, exactly as a browser would submit it.</summary>
    private static string AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No __RequestVerificationToken input found in the rendered page.");
        return match.Groups[1].Value;
    }

    private static FormUrlEncodedContent IntakeForm(string token, string channelId, string phoneNumber) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Intake.ChannelId"] = channelId,
            ["Intake.PhoneNumber"] = phoneNumber,
        });

    // -----------------------------------------------------------------
    // The real Step 1 sequence the regression report described.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Step1Search_WithAKnownPhone_RedirectsWithTheIntakeId_AndRendersTheVerifiedCustomer()
    {
        var api = CrmFoundApi();
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // 1. GET /NewTicket — Step 1 renders with the Search form.
        var getResponse = await client.GetAsync("/NewTicket");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getHtml = await getResponse.Content.ReadAsStringAsync();
        Assert.Contains("handler=Intake", getHtml);

        // 2. POST the Search form the way the browser does.
        var postResponse = await client.PostAsync(
            "/NewTicket?handler=Intake", IntakeForm(AntiforgeryToken(getHtml), "Phone", SearchedPhone));

        // 3. Intake creation succeeded and the wizard advanced via PRG — the
        //    regression instead returned 200 with the same empty Step 1.
        Assert.Contains(api.Requests, r => r.Method == HttpMethod.Post && r.RequestUri.Contains("api/intake-records"));
        Assert.Equal(HttpStatusCode.Redirect, postResponse.StatusCode);
        var location = postResponse.Headers.Location!.ToString();
        Assert.Contains("step=customer", location);
        Assert.Contains($"intakeRecordId={IntakeId}", location);
        Assert.Contains($"phoneNumber={SearchedPhone}", location);

        // 4-5. Following the redirect runs customer verification and renders
        //      the lookup result — the verified CRM candidate, not an empty
        //      Step 1.
        var resultResponse = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        var resultHtml = await resultResponse.Content.ReadAsStringAsync();

        Assert.Contains(api.Requests, r => r.RequestUri.Contains($"api/intake-records/{IntakeId}/customer-lookup"));
        Assert.Contains(api.Requests, r => r.RequestUri.Contains("api/crm/buyers?phoneNumber=" + SearchedPhone));

        Assert.Contains("Aisha Rahman", resultHtml);
        Assert.Contains("Verified via", resultHtml);
        Assert.Contains("Use this customer", resultHtml);
    }

    [Fact]
    public async Task Step1Search_WhenAllSourcesReturnNothing_RendersTheNotFoundState_WithTheManualPath()
    {
        var api = new FakeApiHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/channels")
            {
                return ChannelsResponse();
            }

            if (request.Method == HttpMethod.Post && path == "/api/intake-records")
            {
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new IntakeRecordResponseDto(
                    IntakeId, "Phone", DateTime.UtcNow, SearchedPhone, null, false, null, null, "Unverified", null));
            }

            if (path == $"/api/intake-records/{IntakeId}/customer-lookup")
            {
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerLookupResultDto(
                    IntakeId, SearchedPhone,
                    [CustomerLookupSourceResultDto.NotFound("Pact"), CustomerLookupSourceResultDto.NotFound("Tasleeh")]));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var getHtml = await (await client.GetAsync("/NewTicket")).Content.ReadAsStringAsync();
        var postResponse = await client.PostAsync(
            "/NewTicket?handler=Intake", IntakeForm(AntiforgeryToken(getHtml), "Phone", SearchedPhone));
        Assert.Equal(HttpStatusCode.Redirect, postResponse.StatusCode);

        var resultHtml = await (await client.GetAsync(postResponse.Headers.Location!.ToString())).Content.ReadAsStringAsync();

        Assert.Contains("Customer not found", resultHtml);
        Assert.Contains("Continue with Manual Entry", resultHtml);
    }

    [Fact]
    public async Task Step1Search_WithAnEmptyPhone_ShowsAVisibleValidationError_NeverASilentEmptyStep1()
    {
        var api = CrmFoundApi();
        using var factory = CreateFactory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var getHtml = await (await client.GetAsync("/NewTicket")).Content.ReadAsStringAsync();
        var postResponse = await client.PostAsync(
            "/NewTicket?handler=Intake", IntakeForm(AntiforgeryToken(getHtml), "Phone", ""));

        // Invalid input redisplays Step 1 — but with a visible error, and
        // without ever creating an intake.
        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
        var html = await postResponse.Content.ReadAsStringAsync();
        Assert.Contains("Enter a phone number to search.", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Post && r.RequestUri.Contains("api/intake-records"));
    }
}
