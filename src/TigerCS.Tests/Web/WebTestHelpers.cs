using System.Net;
using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

/// <summary>
/// The small amount of HTML handling these tests need.
///
/// <para>
/// Deliberately regex-based rather than a parser dependency: the assertions
/// here are "does this control exist", "is this text present", and "what is the
/// anti-forgery token", none of which need a DOM. Adding an HTML parser to the
/// solution for that would be a dependency the build has to carry forever.
/// </para>
/// </summary>
public static partial class WebTestHelpers
{
    [GeneratedRegex(
        """<input name="__RequestVerificationToken" type="hidden" value="([^"]+)" />""",
        RegexOptions.IgnoreCase)]
    private static partial Regex AntiforgeryTokenRegex();

    [GeneratedRegex(@"<input type=""hidden"" name=""SubmitToken"" value=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex SubmitTokenRegex();

    /// <summary>
    /// Signs in through the real login page and returns a client carrying the
    /// resulting authentication cookie.
    ///
    /// <para>
    /// Goes through the actual form, anti-forgery token included, rather than
    /// synthesising a cookie: a test that forges its own principal would pass
    /// even if sign-in were broken, and would not prove the token ends up where
    /// this application says it does.
    /// </para>
    /// </summary>
    public static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client, string username, string password)
    {
        var loginPage = await client.GetAsync("/Account/Login");
        loginPage.EnsureSuccessStatusCode();

        var token = ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());

        return await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("Username", username),
            new KeyValuePair<string, string>("Password", password),
            new KeyValuePair<string, string>("__RequestVerificationToken", token)
        ]));
    }

    /// <summary>Posts a form to a page handler, fetching a fresh anti-forgery token from <paramref name="formPageUrl"/> first.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string formPageUrl,
        string postUrl,
        params KeyValuePair<string, string>[] fields)
    {
        var page = await client.GetAsync(formPageUrl);
        page.EnsureSuccessStatusCode();

        var html = await page.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(html);

        var body = fields.Append(
            new KeyValuePair<string, string>("__RequestVerificationToken", token)).ToList();

        // A browser posts every hidden field in the form. The wizard's
        // duplicate-submission token is one of them, so a test that omitted it
        // would be exercising the duplicate-submission guard rather than the
        // path it means to test.
        var submitToken = SubmitTokenRegex().Match(html);
        if (submitToken.Success && !body.Any(f => f.Key == "SubmitToken"))
        {
            body.Add(new KeyValuePair<string, string>("SubmitToken", submitToken.Groups[1].Value));
        }

        return await client.PostAsync(postUrl, new FormUrlEncodedContent(body));
    }

    /// <summary>The anti-forgery token from a rendered page. Fails the test loudly when absent.</summary>
    public static string ExtractAntiforgeryToken(string html)
    {
        var match = AntiforgeryTokenRegex().Match(html);

        Assert.True(
            match.Success,
            "No anti-forgery token was rendered. Every form in this application must carry one.");

        return match.Groups[1].Value;
    }

    /// <summary>Asserts the response is a redirect to a location starting with the given path.</summary>
    public static void AssertRedirectTo(HttpResponseMessage response, string expectedPathPrefix)
    {
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther,
            $"Expected a redirect, got {(int)response.StatusCode}.");

        var location = response.Headers.Location?.ToString() ?? string.Empty;

        // The framework may emit an absolute or a relative Location; only the
        // path is being asserted either way.
        var path = Uri.TryCreate(location, UriKind.Absolute, out var absolute)
            ? absolute.PathAndQuery
            : location;

        Assert.True(
            path.StartsWith(expectedPathPrefix, StringComparison.OrdinalIgnoreCase),
            $"Expected a redirect to '{expectedPathPrefix}…', got '{location}'.");
    }

    /// <summary>
    /// Reads the body and asserts it contains the given text.
    ///
    /// <para>
    /// The body is HTML-decoded first, so an assertion can be written in the
    /// words a user reads rather than in Razor's encoded output - "don't"
    /// rather than "don&amp;#x27;t". Encoding is exactly what these tests want
    /// Razor to keep doing, so the decode happens here, not in the view.
    /// </para>
    /// </summary>
    public static async Task AssertContainsAsync(HttpResponseMessage response, string expected)
    {
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.True(
            html.Contains(expected, StringComparison.OrdinalIgnoreCase),
            $"Expected the page to contain '{expected}'.");
    }

    /// <summary>Reads the body and asserts it does NOT contain the given text.</summary>
    public static async Task AssertDoesNotContainAsync(HttpResponseMessage response, string unexpected)
    {
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.False(
            html.Contains(unexpected, StringComparison.OrdinalIgnoreCase),
            $"Expected the page NOT to contain '{unexpected}'.");
    }
}
