using System.Net;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// Sign-in, sign-out, and the states the brief requires the login screen to
/// distinguish.
/// </summary>
public sealed class LoginTests
{
    [Fact]
    public async Task Login_WithValidCredentials_RedirectsToTheQueue()
    {
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent));

        var client = factory.CreateWebClient();

        var response = await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");

        WebTestHelpers.AssertRedirectTo(response, "/Tickets");
    }

    [Fact]
    public async Task Login_WithValidCredentials_StoresTheTokenOnlyInAnEncryptedHttpOnlyCookie()
    {
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent));

        var client = factory.CreateWebClient();

        var response = await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");

        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : [];

        var session = Assert.Single(setCookies, c => c.StartsWith("TigerCS.Session", StringComparison.Ordinal));

        // The three properties that make this model safe at all.
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", session, StringComparison.OrdinalIgnoreCase);

        // And the point of the whole design: the raw JWT never appears in
        // anything the browser can read.
        Assert.DoesNotContain("header.payload.signature", session, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_WithWrongCredentials_ShowsInvalidCredentialsAndNeverSaysWhichFieldWasWrong()
    {
        using var factory = new TigerCsWebFactory();
        factory.Api.On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.Unauthorized);

        var client = factory.CreateWebClient();

        var response = await WebTestHelpers.SignInAsync(client, "agent", "wrong-password");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "not accepted");

        // Naming the field would confirm whether the username exists.
        await WebTestHelpers.AssertDoesNotContainAsync(response, "password is incorrect");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "no such user");
    }

    [Fact]
    public async Task Login_WithLockedAccount_ShowsADistinctLockedMessage()
    {
        using var factory = new TigerCsWebFactory();
        factory.Api.OnProblem(
            HttpMethod.Post, "/api/auth/login", HttpStatusCode.Locked, "account-locked", "Account locked");

        var client = factory.CreateWebClient();

        var response = await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "Account locked");
        await WebTestHelpers.AssertContainsAsync(response, "administrator");
    }

    [Fact]
    public async Task Login_WithDeactivatedAccount_TellsTheUserToContactAnAdministrator()
    {
        // The API answers a deactivated account with 401, exactly as it answers
        // a wrong password - it does not distinguish them on the wire, and this
        // tier must not invent a distinction it cannot observe. What it can do
        // is name the remedy, so a deactivated user is not left retyping a
        // password that will never work.
        using var factory = new TigerCsWebFactory();
        factory.Api.On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.Unauthorized);

        var client = factory.CreateWebClient();

        var response = await WebTestHelpers.SignInAsync(client, "deactivated", "Test-Password-1!");

        await WebTestHelpers.AssertContainsAsync(response, "deactivated");
        await WebTestHelpers.AssertContainsAsync(response, "administrator");
    }

    [Fact]
    public async Task Login_WithBlankFields_ValidatesWithoutCallingTheApi()
    {
        using var factory = new TigerCsWebFactory();
        var client = factory.CreateWebClient();

        var page = await client.GetAsync("/Account/Login");
        var token = WebTestHelpers.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("Username", ""),
            new KeyValuePair<string, string>("Password", ""),
            new KeyValuePair<string, string>("__RequestVerificationToken", token)
        ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "Enter your username");
        Assert.Equal(0, factory.Api.CountFor(HttpMethod.Post, "/api/auth/login"));
    }

    [Fact]
    public async Task Login_WhenTheApiIsUnreachable_ShowsAServiceUnavailableMessage()
    {
        using var factory = new TigerCsWebFactory();
        factory.Api.On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.BadGateway);

        var client = factory.CreateWebClient();

        var response = await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");

        await WebTestHelpers.AssertContainsAsync(response, "could not be reached");
    }

    [Fact]
    public async Task LoginPage_OffersNoRememberMeAndNoForgotPassword()
    {
        // Both are deliberate omissions, not oversights: no refresh-token
        // contract exists for the first and no reset API exists for the second.
        // This test is what stops either being added back without the API
        // support that would make it honest.
        using var factory = new TigerCsWebFactory();
        var client = factory.CreateWebClient();

        var response = await client.GetAsync("/Account/Login");

        await WebTestHelpers.AssertDoesNotContainAsync(response, "Remember me");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "Forgot password");
    }

    [Fact]
    public async Task LoginPage_CarriesAnAntiforgeryToken()
    {
        using var factory = new TigerCsWebFactory();
        var client = factory.CreateWebClient();

        var response = await client.GetAsync("/Account/Login");

        WebTestHelpers.ExtractAntiforgeryToken(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_PostWithoutAnAntiforgeryToken_IsRejected()
    {
        using var factory = new TigerCsWebFactory();
        var client = factory.CreateWebClient();

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("Username", "agent"),
            new KeyValuePair<string, string>("Password", "Test-Password-1!")
        ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Api.CountFor(HttpMethod.Post, "/api/auth/login"));
    }

    [Theory]
    [InlineData("https://evil.example.com/steal")]
    [InlineData("//evil.example.com/steal")]
    public async Task Login_WithAnOffSiteReturnUrl_RedirectsToTheQueueInstead(string returnUrl)
    {
        // An open redirect on a login page is a phishing primitive: the victim
        // has just typed a password when it fires.
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent));

        var client = factory.CreateWebClient();

        var page = await client.GetAsync("/Account/Login");
        var token = WebTestHelpers.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());

        var response = await client.PostAsync(
            $"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}",
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("Username", "agent"),
                new KeyValuePair<string, string>("Password", "Test-Password-1!"),
                new KeyValuePair<string, string>("__RequestVerificationToken", token)
            ]));

        WebTestHelpers.AssertRedirectTo(response, "/Tickets");
        Assert.DoesNotContain("evil.example.com", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Logout_ClearsTheSessionAndReturnsToLogin()
    {
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(0))
            .On(HttpMethod.Post, "/api/auth/logout", HttpStatusCode.NoContent);

        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");

        var response = await WebTestHelpers.PostFormAsync(client, "/Tickets", "/Account/Logout");

        WebTestHelpers.AssertRedirectTo(response, "/Account/Login");
        Assert.Equal(1, factory.Api.CountFor(HttpMethod.Post, "/api/auth/logout"));
    }

    [Fact]
    public async Task Logout_WhenTheApiCallFails_StillClearsTheLocalSession()
    {
        // Leaving a user in a signed-in shell because the sign-out call failed
        // would be the worst outcome of the three available.
        using var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/tickets", HttpStatusCode.OK, TestData.Queue(0))
            .On(HttpMethod.Post, "/api/auth/logout", HttpStatusCode.BadGateway);

        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");

        var response = await WebTestHelpers.PostFormAsync(client, "/Tickets", "/Account/Logout");

        WebTestHelpers.AssertRedirectTo(response, "/Account/Login");

        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
        Assert.Contains(setCookies, c => c.StartsWith("TigerCS.Session=;", StringComparison.Ordinal));
    }
}
