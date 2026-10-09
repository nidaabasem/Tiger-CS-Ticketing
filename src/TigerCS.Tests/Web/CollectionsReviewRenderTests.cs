extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

public sealed class CollectionsReviewRenderTests
{
    private static readonly DateTime Read = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);

    private static WebApplicationFactory<TigerCsWeb::Program> Factory(Api api) =>
        new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = api));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(options => { options.DefaultAuthenticateScheme = "Test"; options.DefaultChallengeScheme = "Test"; });
            });
        });

    private static ReviewRecordDto Record(string name, string status, string pay, string[]? reasons = null, string? previous = null) => new(
        $"KEY{name}", "CurrentMonth", 4, name, "+971500003001", "c@example.test", "TP140", "TP140-101", pay, 500m, "AED", new DateOnly(2026, 10, 20),
        status, (reasons ?? []).Select(r => new ReviewReasonDto(r, "NeedsReview", $"Plain words for {r}")).ToList(),
        "PACT receivables (companies 4 and 32)", Read, previous, previous is null ? null : Read);

    private static ReviewRunDto Run(bool reconciled = true, string state = "Completed") => new(7, state, state == "Running" ? "Validating records" : "Completed",
        state == "Running" ? 45 : 100, new DateOnly(2026, 10, 14), null, new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31), "PACT receivables (companies 4 and 32)",
        "", reconciled, Read, Read, Read, Read, 1234, 56, null, true, false);

    [Fact]
    public async Task ReviewScreenShowsCountsReasonsSourceAndPreservesFilters()
    {
        var api = new Api();
        using var factory = Factory(api); using var client = factory.CreateClient();
        var response = await client.GetAsync("/Collections/Review?companyId=4&paymentStatus=Unpaid&minRemaining=250&reminderType=CurrentMonth&month=2026-10&customer=3001");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("PACT receivables (companies 4 and 32)", html);                 // the source
        Assert.Contains("2026-10-14 08:00 UTC", html);                                    // and its last refresh time
        Assert.Contains("Plain words for NoValidContact", html);                          // reasons in plain language
        Assert.Contains("Already Sent", html);
        Assert.Contains("Uploaded to Genesys", html);                                     // previous dispatch status, not "contacted"
        Assert.DoesNotContain("Contacted", html);
        Assert.Contains("value=\"250\"", html);                                           // editable minimum shown
        Assert.Contains("have an unknown payment status", html.Replace("\r", "").Replace("\n", " "));
        // Exactly the Ready row is selectable.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "class=\"review-select\""));
        // The Api was asked with the chosen filters, server-side.
        Assert.Contains("companyId=4", api.LastRecordsQuery);
        Assert.Contains("paymentStatus=Unpaid", api.LastRecordsQuery);
        Assert.Contains("minRemaining=250", api.LastRecordsQuery);
        Assert.Contains("year=2026", api.LastRecordsQuery);
        Assert.Contains("month=10", api.LastRecordsQuery);
        Assert.Contains("pageSize=25", api.LastRecordsQuery);
    }

    [Fact]
    public async Task AnUnreconciledSourceIsExplainedAtTheTop()
    {
        using var factory = Factory(new Api { Run = Run(reconciled: false) }); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Review")).Content.ReadAsStringAsync();
        Assert.Contains("has not been reconciled against PACT", html);
        Assert.Contains("Source reconciled:</strong> no", html);
    }

    [Fact]
    public async Task ARunningRefreshShowsItsProgress_AndBrowsingNeverStartsOne()
    {
        var api = new Api { Run = Run(state: "Running") };
        using var factory = Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Review")).Content.ReadAsStringAsync();
        Assert.Contains("data-run-active=\"true\"", html);
        Assert.Contains("value=\"45\"", html);
        Assert.Contains("Validating records", html);
        Assert.Equal(0, api.Refreshes);
    }

    [Fact]
    public async Task ADispatchPageDistinguishesUploadedFromContacted_AndOffersReconciliationOnlyForUnconfirmedBatches()
    {
        var api = new Api();
        using var factory = Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Review?dispatch={Api.DispatchId}")).Content.ReadAsStringAsync();
        Assert.Contains("uploaded to Genesys", html);
        Assert.Contains("does not mean the customer was called", html);
        Assert.Contains("Do not send it again", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "class=\"review-reconcile\""));   // only the unconfirmed batch
        Assert.Contains("372d81d7-6d2d-4b9e-8f09-cf2262aecfdf", html);
    }

    private sealed class Api : HttpMessageHandler
    {
        public static readonly Guid DispatchId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        public ReviewRunDto Run { get; set; } = CollectionsReviewRenderTests.Run();
        public string LastRecordsQuery { get; private set; } = "";
        public int Refreshes { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/runs/current")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Run) });
            if (path.EndsWith("/refresh")) { Refreshes++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = JsonContent.Create(Run) }); }
            if (path.EndsWith("/records"))
            {
                LastRecordsQuery = request.RequestUri.Query;
                var items = new[]
                {
                    Record("Ready Customer", "Ready", "Unpaid"),
                    Record("Review Customer", "NeedsReview", "Unpaid", ["NoValidContact"]),
                    Record("Sent Customer", "AlreadySent", "Unpaid", previous: "UploadedToGenesys")
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new ReviewPageDto(Run, new ReviewCountsDto(3, 1, 1, 0, 1, 1), 1, 25, 3, items, 250m)) });
            }
            if (path.Contains("/dispatches/"))
            {
                var batches = new[]
                {
                    new DispatchBatchDto(1, "CurrentMonth", "79e5ae74-ea6e-4941-b76d-45ddf487d8d1", 1, 1000, "Uploaded", 1, 200, 1000, null, Read, Read, null, null),
                    new DispatchBatchDto(2, "LegalNotice", "372d81d7-6d2d-4b9e-8f09-cf2262aecfdf", 2, 20, "UnknownOutcome", 1, null, 0, "The request timed out.", Read, Read, null, null)
                };
                var dispatch = new DispatchDto(DispatchId, "CompletedWithErrors", "Some batches have an unconfirmed outcome.", Guid.NewGuid(), Read, Read, Read, 1020, 1000, 0, 0, 20,
                    new Dictionary<string, decimal> { ["AED"] = 510000m }, batches, DispatchService.UploadDisclaimer);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(dispatch) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Name, "Manager"),
                new Claim(ClaimTypes.Role, Roles.CsManager)
            ], "Test")), "Test")));
    }
}
