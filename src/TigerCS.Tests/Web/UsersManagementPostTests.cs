// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// Users Management writes through the real Web pipeline (antiforgery, page
/// handlers, redirects, TempData) against a recording fake Api: Unlock User
/// (fetch JSON and no-JS redirect), the Identity password rejection shown as a
/// readable list on Reset Password and Add User, and the confirm-mismatch rule.
/// </summary>
public sealed class UsersManagementPostTests : IDisposable
{
    // Antiforgery tokens are bound to the signed-in identity, so the viewer must be stable across requests.
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly Guid UserId = new("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime Now = new(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly string[] IdentityErrors =
    [
        "Passwords must have at least one digit ('0'-'9').",
        "Passwords must have at least one uppercase ('A'-'Z').",
        "Passwords must have at least one non alphanumeric character."
    ];

    private readonly FakeUsersApi _api = new();
    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;

    public UsersManagementPostTests()
    {
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = _api));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AuthHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(o =>
                {
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                });
            });
        });
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Client()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", Roles.SystemAdministrator);
        return client;
    }

    private static async Task<string> TokenAsync(HttpClient client, string pageUrl)
    {
        var html = await (await client.GetAsync(pageUrl)).Content.ReadAsStringAsync();
        return WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }

    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
        new(new[] { ("__RequestVerificationToken", token) }.Concat(fields).ToDictionary(f => f.Item1, f => f.Item2));

    // ---------------------------------------------------------------- unlock

    [Fact]
    public async Task Unlock_ByFetch_CallsTheApiOnce_AndAnswersJsonWithTheSuccessMessage()
    {
        var client = Client();
        var token = await TokenAsync(client, "/Admin/Users");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Users?handler=Unlock&id={UserId}&pageNo=1")
        {
            Content = Form(token)
        };
        request.Headers.Add("X-Requested-With", "fetch");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("Alma Hassan has been unlocked.", json.GetProperty("message").GetString());
        Assert.Equal([$"api/admin/users/{UserId}/unlock"], _api.Unlocks);
    }

    [Fact]
    public async Task Unlock_WithoutJavaScript_RedirectsBackToTheListWithTheStatusMessage()
    {
        var client = Client();
        var token = await TokenAsync(client, "/Admin/Users");

        var response = await client.PostAsync(
            $"/Admin/Users?handler=Unlock&id={UserId}&search=alma&includeInactive=True&pageNo=2", Form(token));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Admin/Users?search=alma&includeInactive=true&page=2", response.Headers.Location!.OriginalString);
        var list = await (await client.GetAsync("/Admin/Users")).Content.ReadAsStringAsync();
        Assert.Contains("Alma Hassan has been unlocked.", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unlock_WhenTheApiRefuses_ShowsTheApisReasonAsAnError()
    {
        _api.UnlockStatus = HttpStatusCode.Conflict;
        var client = Client();
        var token = await TokenAsync(client, "/Admin/Users");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Users?handler=Unlock&id={UserId}&pageNo=1") { Content = Form(token) };
        request.Headers.Add("X-Requested-With", "fetch");

        var json = await (await client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Contains("This account is not locked.", json.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unlock_WithoutAnAntiforgeryToken_IsRejected()
    {
        var client = Client();
        var response = await client.PostAsync($"/Admin/Users?handler=Unlock&id={UserId}&pageNo=1", new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_api.Unlocks);
    }

    // ------------------------------------------------- password validation

    [Fact]
    public async Task ResetPassword_WhenIdentityRejectsIt_ListsEachReasonOnItsOwnLine_AndNeverEchoesThePassword()
    {
        var client = Client();
        var page = $"/Admin/Users/{UserId}";
        var token = await TokenAsync(client, page);

        var response = await client.PostAsync($"{page}?handler=ResetPassword", Form(token,
            ("ResetPassword.NewPassword", "abcdefghij"), ("ResetPassword.ConfirmPassword", "abcdefghij")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var html = await (await client.GetAsync(page)).Content.ReadAsStringAsync();

        var list = Regex.Match(html, "<ul class=\"alert__list\">(.*?)</ul>", RegexOptions.Singleline);
        Assert.True(list.Success, "Expected the errors as a list.");
        var items = Regex.Matches(list.Groups[1].Value, "<li>(.*?)</li>", RegexOptions.Singleline).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        Assert.Equal(IdentityErrors, items);
        Assert.DoesNotContain("abcdefghij", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetPassword_WithMismatchedConfirmation_ShowsTheMismatchMessage_WithoutCallingTheApi()
    {
        var client = Client();
        var page = $"/Admin/Users/{UserId}";
        var token = await TokenAsync(client, page);

        await client.PostAsync($"{page}?handler=ResetPassword", Form(token,
            ("ResetPassword.NewPassword", "Valid-Pass-1!"), ("ResetPassword.ConfirmPassword", "Different-1!")));
        var html = await (await client.GetAsync(page)).Content.ReadAsStringAsync();

        Assert.Contains("The new password and its confirmation do not match.", html, StringComparison.Ordinal);
        Assert.Equal(0, _api.PasswordPosts);
    }

    [Fact]
    public async Task AddUser_WhenIdentityRejectsTheInitialPassword_ReRendersTheFormWithTheReasonsAsAList_AndTheRulesStillShown()
    {
        var client = Client();
        var token = await TokenAsync(client, "/Admin/Users/New");

        var response = await client.PostAsync("/Admin/Users/New?handler=Create", Form(token,
            ("Create.UserName", "new.user"), ("Create.DisplayName", "New User"),
            ("Create.InitialPassword", "abcdefghij"), ("Create.ConfirmPassword", "abcdefghij")));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = Regex.Matches(Regex.Match(html, "<ul class=\"alert__list\">(.*?)</ul>", RegexOptions.Singleline).Groups[1].Value,
            "<li>(.*?)</li>", RegexOptions.Singleline).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        Assert.Equal(IdentityErrors, items);
        Assert.Contains("id=\"create-password-rules\" data-password-rules", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddUser_WithBlankFields_ListsEachWebSideValidationMessage()
    {
        var client = Client();
        var token = await TokenAsync(client, "/Admin/Users/New");

        var html = await (await client.PostAsync("/Admin/Users/New?handler=Create", Form(token,
            ("Create.UserName", ""), ("Create.DisplayName", "x"), ("Create.InitialPassword", "p"), ("Create.ConfirmPassword", "q")))).Content.ReadAsStringAsync();

        Assert.Contains("<li>User name is required.</li>", html, StringComparison.Ordinal);
        Assert.Contains("<li>The two passwords do not match.</li>", html, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- fake api

    private sealed class AuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers.TryGetValue("X-Test-Role", out var h) ? h.ToString() : Roles.CsAgent;
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Administrator"),
                new Claim(ClaimTypes.Role, role)
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class FakeUsersApi : HttpMessageHandler
    {
        public List<string> Unlocks { get; } = [];
        public int PasswordPosts { get; private set; }
        public HttpStatusCode UnlockStatus { get; set; } = HttpStatusCode.OK;

        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        private static AdminUserDto User(bool locked) => new(
            UserId, "alma.hassan", "alma@example.com", "Alma Hassan", false, true, null, Now.AddYears(-2), locked,
            [Roles.SystemAdministrator], [new DepartmentMembershipDto(1, "Collections", true)], true);

        private static HttpResponseMessage Json<T>(T body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(body, options: Web) };

        private static HttpResponseMessage Problem(HttpStatusCode status, string? detail, Dictionary<string, string[]>? errors = null)
        {
            var body = new Dictionary<string, object?> { ["status"] = (int)status, ["detail"] = detail };
            if (errors is not null) body["errors"] = errors;
            return new(status) { Content = JsonContent.Create(body, options: Web, mediaType: new("application/problem+json")) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var post = request.Method == HttpMethod.Post;
            HttpResponseMessage response = path switch
            {
                "api/admin/users" when post => Problem(HttpStatusCode.UnprocessableEntity, null, new() { ["NewPassword"] = IdentityErrors }),
                "api/admin/users" => Json(new AdminUserListDto([User(true)], 1, 25, 1)),
                "api/admin/users/password-policy" => Json(new PasswordPolicyDto(8, true, true, true, true, 4)),
                "api/roles" => Json(Roles.All.Select(r => new RoleDto(r, r)).ToList()),
                "api/departments" => Json(new List<DepartmentDto> { new(1, "Collections") }),
                _ when path == $"api/admin/users/{UserId}" => Json(User(true)),
                _ when path == $"api/admin/users/{UserId}/unlock" && post => Unlock(path),
                _ when path == $"api/admin/users/{UserId}/password" && post => PasswordRejected(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            return Task.FromResult(response);
        }

        private HttpResponseMessage Unlock(string path)
        {
            if (UnlockStatus != HttpStatusCode.OK)
            {
                return Problem(UnlockStatus, "This account is not locked.");
            }

            Unlocks.Add(path);
            return Json(User(false));
        }

        private HttpResponseMessage PasswordRejected()
        {
            PasswordPosts++;
            return Problem(HttpStatusCode.UnprocessableEntity, null, new() { ["NewPassword"] = IdentityErrors });
        }
    }
}
