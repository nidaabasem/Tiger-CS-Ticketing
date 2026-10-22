// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Tests.Web.Fakes;

namespace TigerCS.Tests.Web;

/// <summary>
/// The public customer review page through the real Web host: reachable with no login, a GET (what an email scanner does) only
/// shows the form and never calls the submit endpoint, and the review is sent only by the explicit, antiforgery-protected POST.
/// </summary>
public sealed class ReviewPageTests : IDisposable
{
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde";

    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;
    private readonly FakeApiHandler _api;
    private ReviewLinkStatus _status = ReviewLinkStatus.Valid;
    private SubmitReviewOutcome _submitOutcome = SubmitReviewOutcome.Submitted;

    public ReviewPageTests()
    {
        _api = new FakeApiHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/lookup", StringComparison.Ordinal))
            {
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new ReviewLinkDto(_status, _status is ReviewLinkStatus.Valid or ReviewLinkStatus.AlreadySubmitted ? "TG-CS-20260822-0042" : null));
            }

            if (path.EndsWith("/submit", StringComparison.Ordinal))
            {
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new SubmitReviewResultDto(_submitOutcome, "TG-CS-20260822-0042"));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = _api)));
        });
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Anonymous() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string AntiforgeryToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"(?<v>[^\"]+)\"").Groups["v"].Value;

    private static FormUrlEncodedContent Form(string antiforgery, params (string Key, string Value)[] fields) =>
        new(fields.Append((Key: "__RequestVerificationToken", Value: antiforgery)).Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    [Fact]
    public async Task OpeningAStarLink_NeedsNoLogin_ShowsTheFormWithTheRatingSelected_AndSavesNothing()
    {
        using var client = Anonymous();

        var response = await client.GetAsync($"/Review?t={Token}&r=4");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, html);
        Assert.Contains("How was your experience?", html, StringComparison.Ordinal);
        Assert.Contains("TG-CS-20260822-0042", html, StringComparison.Ordinal);
        Assert.Matches("<input type=\"radio\" id=\"star-4\" name=\"r\" value=\"4\" checked", html);
        Assert.DoesNotContain("id=\"star-3\" name=\"r\" value=\"3\" checked", html, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", html, StringComparison.Ordinal);
        Assert.Contains("Submit review", html, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());

        // The only call the API saw is the read-only lookup — a GET never submits.
        Assert.All(_api.Requests, r => Assert.EndsWith("/lookup", r.RequestUri, StringComparison.Ordinal));
        Assert.DoesNotContain(_api.Requests, r => r.RequestUri.EndsWith("/submit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ReviewLinkStatus.Invalid, HttpStatusCode.NotFound, "This review link is not valid")]
    [InlineData(ReviewLinkStatus.Expired, HttpStatusCode.NotFound, "This review link has expired")]
    [InlineData(ReviewLinkStatus.AlreadySubmitted, HttpStatusCode.OK, "we already have your review")]
    public async Task InvalidExpiredAndUsedLinks_ShowAMessage_NotTheForm(ReviewLinkStatus status, HttpStatusCode expected, string text)
    {
        _status = status;
        using var client = Anonymous();

        var response = await client.GetAsync($"/Review?t={Token}&r=5");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(expected, response.StatusCode);
        Assert.Contains(text, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Submit review", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASpoofedRating_OnTheLink_IsIgnored()
    {
        using var client = Anonymous();

        var html = await (await client.GetAsync($"/Review?t={Token}&r=99")).Content.ReadAsStringAsync();

        Assert.DoesNotContain(" checked", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmittingTheForm_SendsTheTokenRatingAndComment_AndThanksTheCustomer()
    {
        using var client = Anonymous();
        var form = await (await client.GetAsync($"/Review?t={Token}&r=4")).Content.ReadAsStringAsync();

        var response = await client.PostAsync("/Review", Form(AntiforgeryToken(form), ("t", Token), ("r", "5"), ("Comment", "Quick & polite")));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Thank you for your feedback", html, StringComparison.Ordinal);
        var submit = Assert.Single(_api.Requests, r => r.RequestUri.EndsWith("/submit", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(submit.Body!);
        Assert.Equal(Token, body.RootElement.GetProperty("token").GetString());
        Assert.Equal(5, body.RootElement.GetProperty("rating").GetInt32());
        Assert.Equal("Quick & polite", body.RootElement.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task APostWithoutTheAntiforgeryToken_IsRefused_AndNothingIsSubmitted()
    {
        using var client = Anonymous();

        var response = await client.PostAsync("/Review", new FormUrlEncodedContent(new Dictionary<string, string> { ["t"] = Token, ["r"] = "5" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_api.Requests, r => r.RequestUri.EndsWith("/submit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APostWithoutARating_AsksForOne_AndSubmitsNothing()
    {
        using var client = Anonymous();
        var form = await (await client.GetAsync($"/Review?t={Token}")).Content.ReadAsStringAsync();

        var html = await (await client.PostAsync("/Review", Form(AntiforgeryToken(form), ("t", Token), ("Comment", "hmm")))).Content.ReadAsStringAsync();

        Assert.Contains("Please choose a star rating", html, StringComparison.Ordinal);
        Assert.Contains("Submit review", html, StringComparison.Ordinal);
        Assert.DoesNotContain(_api.Requests, r => r.RequestUri.EndsWith("/submit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADuplicateSubmission_ShowsThatTheReviewWasAlreadyReceived()
    {
        _submitOutcome = SubmitReviewOutcome.AlreadySubmitted;
        using var client = Anonymous();
        var form = await (await client.GetAsync($"/Review?t={Token}&r=2")).Content.ReadAsStringAsync();

        var html = await (await client.PostAsync("/Review", Form(AntiforgeryToken(form), ("t", Token), ("r", "2")))).Content.ReadAsStringAsync();

        Assert.Contains("we already have your review", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheApiBeingDown_ShowsAnUnavailableMessage_NotAnError()
    {
        using var down = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new FakeApiHandler((_, _) => throw new HttpRequestException("down")))));
        });
        using var client = down.CreateClient();

        var response = await client.GetAsync($"/Review?t={Token}&r=5");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("unavailable", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
