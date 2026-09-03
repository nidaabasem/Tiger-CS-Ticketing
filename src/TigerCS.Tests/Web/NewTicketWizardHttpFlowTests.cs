// TigerCS.Web's Program is referenced via an extern alias so it never collides with
// TigerCS.Api's, which existing WebApplicationFactory<Program> tests use unqualified.
extern alias TigerCsWeb;

using System.Net;
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

    /// <summary>The Api responses the happy-path search needs: intake creation, the department-aware lookup (Crm participates), the CRM Buyer match, and its bounded history.</summary>
    private static FakeApiHandler CrmFoundApi() => new((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;

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
