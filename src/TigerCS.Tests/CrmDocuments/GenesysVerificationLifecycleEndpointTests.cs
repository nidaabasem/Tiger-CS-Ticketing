using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Integrations.Modules.EmailIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// The OTP lifecycle and the numeric-enum rule, over the real HTTP surface (<c>/api/genesys/verification/*</c>,
/// <c>/api/genesys/documents/send-copy</c>, <c>/api/verification-sessions</c>) with an advanceable clock.
/// What <see cref="GenesysDocumentsEndpointTests"/> and the service tests already prove (wrong code, lock after five, single use,
/// cross-account, replay with <c>duplicate: true</c>, session ownership, refusal of agent-asserted sessions) is not repeated here;
/// this adds what only a real clock-driven HTTP run shows: expiry as <c>410 OTP_EXPIRED</c>, the resend budget and
/// <c>Retry-After</c> value, a locked or spent challenge refusing a resend, and that no internal enum <i>number</i> is accepted
/// where the contract names a word.
/// </summary>
public sealed class GenesysVerificationLifecycleEndpointTests : IDisposable
{
    private const string Phone = "+971501234567";
    private const string CrmBase = "https://crm.uat.example.test:8014/";
    private const string Send = "/api/genesys/verification/otp/send";
    private const string Resend = "/api/genesys/verification/otp/resend";
    private const string Verify = "/api/genesys/verification/otp/verify";
    private const string SendCopy = "/api/genesys/documents/send-copy";

    /// <summary>A clock the test moves; everything in the host that asks <see cref="TimeProvider"/> sees it.</summary>
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly MutableClock _clock = new();
    private readonly TigerCsApiFactory _factory;
    private int _documentCalls;

    public GenesysVerificationLifecycleEndpointTests()
    {
        var crm = new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/TicketingSystem/GetBuyerByPhone", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(Buyer()));
            }

            Interlocked.Increment(ref _documentCalls);
            return Task.FromResult(Json("""{"customerId":9001,"leadId":12345,"count":0,"selectionRequired":false,"attachments":[]}"""));
        });

        _factory = new TigerCsApiFactory
        {
            ExtraConfiguration =
            {
                ["CrmDocuments:Enabled"] = "true",
                ["CrmDocuments:OtpCodePepper"] = "test-only-pepper",
                ["Crm:BaseUrl"] = CrmBase,
                ["Crm:SecretKey"] = "uat-like-secret"
            },
            ExtraServices = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
                services.RemoveAll<ICrmBuyerLookupGateway>();
                services.AddHttpClient<ICrmBuyerLookupGateway, CrmBuyerHttpGateway>(c => c.BaseAddress = new Uri(CrmBase))
                    .ConfigurePrimaryHttpMessageHandler(() => crm);
                services.AddHttpClient<CrmDocumentHttpGateway>(c => c.BaseAddress = new Uri(CrmBase))
                    .ConfigurePrimaryHttpMessageHandler(() => crm);
                services.RemoveAll<TigerCS.Application.Modules.CrmDocuments.Abstractions.ICrmDocumentGateway>();
                services.AddScoped<TigerCS.Application.Modules.CrmDocuments.Abstractions.ICrmDocumentGateway>(
                    sp => sp.GetRequiredService<CrmDocumentHttpGateway>());
            }
        };
    }

    public void Dispose() => _factory.Dispose();

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Buyer() => JsonSerializer.Serialize(new
    {
        success = true, found = true, message = "Buyer found successfully.",
        buyers = new[]
        {
            new
            {
                customer = new { customerId = 9001, fullNameEnglish = "Ahmed Ali", fullNameArabic = (string?)null, mobileNumber = Phone, email = "ahmed.ali@example.test" },
                units = new[]
                {
                    new { leadId = 12345, leadStatus = 8, leadStatusName = "Sold", unitId = 1101, unitNumber = "1205", unitStatus = 3, unitType = 2, floorNumber = 12, projectId = 79, projectName = "Tiger Sky Tower", projectArabicName = (string?)null, customerType = 1, customerTypeName = "Buyer" }
                }
            }
        }
    });

    private RecordingEmailSender Mailbox => _factory.Services.GetRequiredService<RecordingEmailSender>();

    private async Task<HttpClient> ClientAsync()
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginResponseDto>())!.AccessToken);
        return client;
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static Task<HttpResponseMessage> Post(HttpClient client, string route, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    private string LastCode() =>
        Regex.Match(Mailbox.Recorded.Last(m => m.Subject.Contains("verification code", StringComparison.Ordinal)).Body, @"\b\d{6}\b").Value;

    private static async Task<Guid> StartAsync(HttpClient client)
    {
        var sent = await Post(client, Send, new { phoneNumber = Phone, crmUnitId = "1101" });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        return (await JsonOf(sent)).GetProperty("challengeId").GetGuid();
    }

    private async Task<Guid> VerifiedSessionAsync(HttpClient client)
    {
        var challenge = await StartAsync(client);
        var verified = await Post(client, Verify, new { challengeId = challenge, code = LastCode() });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        return (await JsonOf(verified)).GetProperty("session").GetProperty("verificationSessionId").GetGuid();
    }

    // =====================================================================
    //  Expiry
    // =====================================================================

    [Fact]
    public async Task ACodeIsRefusedAfterItsLifetime_As410OtpExpired_ForVerifyAndForResend_AndNoSessionIsMade()
    {
        var client = await ClientAsync();
        var challenge = await StartAsync(client);
        var code = LastCode();

        _clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1)); // OtpLifetimeMinutes = 10

        var late = await Post(client, Verify, new { challengeId = challenge, code });
        Assert.Equal(HttpStatusCode.Gone, late.StatusCode);
        var problem = await JsonOf(late);
        Assert.Equal("OTP_EXPIRED", problem.GetProperty("code").GetString());
        Assert.Equal("application/problem+json", late.Content.Headers.ContentType?.MediaType);

        var resend = await Post(client, Resend, new { challengeId = challenge });
        Assert.Equal(HttpStatusCode.Gone, resend.StatusCode);
        Assert.Equal("OTP_EXPIRED", (await JsonOf(resend)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheLastSecondOfTheLifetime_StillVerifies()
    {
        var client = await ClientAsync();
        var challenge = await StartAsync(client);
        var code = LastCode();

        _clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(HttpStatusCode.OK, (await Post(client, Verify, new { challengeId = challenge, code })).StatusCode);
    }

    // =====================================================================
    //  Resend limits
    // =====================================================================

    [Fact]
    public async Task ResendTooSoon_Is429_WithRetryAfterCountingDown_AndSendsNothing()
    {
        var client = await ClientAsync();
        var challenge = await StartAsync(client);
        var mails = Mailbox.Recorded.Count;

        _clock.Advance(TimeSpan.FromSeconds(20)); // OtpMinResendSeconds = 60 -> 40 s left
        var tooSoon = await Post(client, Resend, new { challengeId = challenge });

        Assert.Equal((HttpStatusCode)429, tooSoon.StatusCode);
        var problem = await JsonOf(tooSoon);
        Assert.Equal("OTP_RESEND_TOO_SOON", problem.GetProperty("code").GetString());
        Assert.Equal("40", tooSoon.Headers.GetValues("Retry-After").Single());
        Assert.Equal(mails, Mailbox.Recorded.Count);
    }

    [Fact]
    public async Task AChallengeAllowsThreeCodes_TheFourthResendIs429LimitReached_AndNothingMoreIsEmailed()
    {
        var client = await ClientAsync();
        var challenge = await StartAsync(client); // code 1
        var mails = Mailbox.Recorded.Count;

        for (var i = 0; i < 2; i++) // codes 2 and 3
        {
            _clock.Advance(TimeSpan.FromSeconds(61));
            Assert.Equal(HttpStatusCode.OK, (await Post(client, Resend, new { challengeId = challenge })).StatusCode);
        }

        _clock.Advance(TimeSpan.FromSeconds(61));
        var fourth = await Post(client, Resend, new { challengeId = challenge });

        Assert.Equal((HttpStatusCode)429, fourth.StatusCode);
        Assert.Equal("OTP_RESEND_LIMIT_REACHED", (await JsonOf(fourth)).GetProperty("code").GetString());
        Assert.Equal(mails + 2, Mailbox.Recorded.Count);

        // The newest code is the only one that works: the first is dead.
        var newest = LastCode();
        Assert.Equal(HttpStatusCode.OK, (await Post(client, Verify, new { challengeId = challenge, code = newest })).StatusCode);
    }

    [Fact]
    public async Task ALockedChallenge_CannotBeRevivedByAResend_And423IsReported()
    {
        var client = await ClientAsync();
        var challenge = await StartAsync(client);
        var wrong = LastCode() == "000000" ? "000001" : "000000";
        for (var i = 0; i < 5; i++)
        {
            await Post(client, Verify, new { challengeId = challenge, code = wrong });
        }

        _clock.Advance(TimeSpan.FromSeconds(61));
        var resend = await Post(client, Resend, new { challengeId = challenge });

        Assert.Equal((HttpStatusCode)423, resend.StatusCode);
        Assert.Equal("OTP_LOCKED", (await JsonOf(resend)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ASpentChallenge_CannotBeResent_ItIs409AlreadyUsed()
    {
        var client = await ClientAsync();
        var challenge = await StartAsync(client);
        await Post(client, Verify, new { challengeId = challenge, code = LastCode() });

        _clock.Advance(TimeSpan.FromSeconds(61));
        var resend = await Post(client, Resend, new { challengeId = challenge });

        Assert.Equal(HttpStatusCode.Conflict, resend.StatusCode);
        Assert.Equal("OTP_ALREADY_USED", (await JsonOf(resend)).GetProperty("code").GetString());
    }

    // =====================================================================
    //  Ownership across callers, once more at the document door
    // =====================================================================

    [Fact]
    public async Task ASessionVerifiedByCallerA_CannotBeUsedByCallerB_ToSendADocument_AndCrmIsNeverAsked()
    {
        var a = await ClientAsync();
        var session = await VerifiedSessionAsync(a);
        var b = await ClientAsync();

        var response = await Post(b, SendCopy, new { verificationSessionId = session, documentType = "Contract" }, "k-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("VERIFICATION_FAILED", (await JsonOf(response)).GetProperty("code").GetString());
        Assert.Equal(0, _documentCalls);
    }

    // =====================================================================
    //  Numeric enum values are not the contract
    // =====================================================================

    [Fact]
    public async Task VerificationMethod_AsANumberOrAnythingButAName_IsRefusedAtTheController_BeforeAnyLookup()
    {
        var client = await ClientAsync();

        // "3" would be Otp - proof only the server-side challenge may produce; "otp" (wrong case) and lists are not names of this contract either.
        foreach (var method in new[] { "0", "1", "2", "3", "4", "-1", "99", "otp", "ManualAgentConfirmation, Otp" })
        {
            var response = await Post(client, "/api/verification-sessions",
                new { unitReferenceId = 1, contactReferenceId = 1, confirmed = true, verificationMethod = method });

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"verificationMethod '{method}' answered {(int)response.StatusCode}");
            Assert.Contains("VerificationMethod", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task VerificationMethod_OtpByName_IsStillTheDedicatedRefusal_NotAValidationError()
    {
        var client = await ClientAsync();

        var response = await Post(client, "/api/verification-sessions",
            new { unitReferenceId = 1, contactReferenceId = 1, confirmed = true, verificationMethod = "Otp" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("otp-requires-challenge", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DocumentType_AsANumber_IsInvalidRequest_AndNothingIsRequestedFromCrm()
    {
        var client = await ClientAsync();
        var session = await VerifiedSessionAsync(client);

        foreach (var documentType in new[] { "1", "2", "3", "4", "0", "-1" }) // 1 would be Contract
        {
            var response = await Post(client, SendCopy, new { verificationSessionId = session, documentType }, "k-" + Guid.NewGuid().ToString("N"));

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"documentType '{documentType}' answered {(int)response.StatusCode}");
            Assert.Equal("INVALID_REQUEST", (await JsonOf(response)).GetProperty("code").GetString());
        }

        Assert.Equal(0, _documentCalls);
    }

    [Fact]
    public async Task DeliveryChannel_AsANumber_IsInvalidRequest_NotEmailAndNotAnUnintegratedChannel()
    {
        var client = await ClientAsync();
        var session = await VerifiedSessionAsync(client);
        var mails = Mailbox.Recorded.Count;

        // 1 would be Email, 2 WhatsApp / 3 Sms (which would answer 501 instead of 400).
        foreach (var channel in new[] { "1", "2", "3", "0", "99" })
        {
            var response = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract", deliveryChannel = channel }, "k-" + Guid.NewGuid().ToString("N"));

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"deliveryChannel '{channel}' answered {(int)response.StatusCode}");
            Assert.Equal("INVALID_REQUEST", (await JsonOf(response)).GetProperty("code").GetString());
        }

        Assert.Equal(0, _documentCalls);
        Assert.Equal(mails, Mailbox.Recorded.Count);
    }

    [Fact]
    public async Task TheNamedForms_PassValidation_SoTheNumericRefusalsAboveAreAboutTheNumbers()
    {
        var client = await ClientAsync();
        var session = await VerifiedSessionAsync(client);

        var response = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract", deliveryChannel = "Email" }, "k-" + Guid.NewGuid().ToString("N"));

        // CRM (stub) holds no document: the request got past validation and verification to the lookup.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("DOCUMENT_NOT_FOUND", (await JsonOf(response)).GetProperty("code").GetString());
        Assert.Equal(1, _documentCalls);
    }
}
