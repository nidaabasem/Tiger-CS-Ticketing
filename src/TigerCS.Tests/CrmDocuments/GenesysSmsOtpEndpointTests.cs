using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Integrations.Modules.EmailIntegration;
using TigerCS.Integrations.Modules.SmsIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// <b>SMS verification → server-recorded proof → unit-details (sale) → send-copy</b>, through the real HTTP surface and the real
/// CRM HTTP gateways (buyer lookup, GetUnitDetails, GetCustomerDocuments) over an in-process stub CRM.
///
/// <para>
/// <b>Local verification only.</b> The SMS provider is <see cref="FakeSmsSender"/> (no SMS is sent, Broadnet is not contacted) and
/// CRM is a stub — whose <c>GetUnitDetails</c> answers are test data, not Tiger CRM's. These tests prove TigerCS's behaviour; they say
/// nothing about real SMS delivery or about what the real CRM returns.
/// </para>
/// </summary>
public sealed class GenesysSmsOtpEndpointTests : IDisposable
{
    private const string Verification = "/api/genesys/verification";
    private const string UnitDetails = "/api/genesys/customers/unit-details";
    private const string SendCopy = "/api/genesys/documents/send-copy";
    private const string CrmBase = "https://crm.uat.example.test:8014/";
    private const string SecretKey = "uat-like-secret";
    private const string SearchedPhone = "+971501234567";
    private const string CrmMobile = "+971 50 999 8888";
    private const string BuyerEmail = "ahmed.ali@example.test";

    private static readonly byte[] PdfBytes = [.. "%PDF-1.7\n"u8, 1, 2, 3];

    /// <summary>An in-process Tiger CRM: records requests, answers as scripted.</summary>
    private sealed class StubCrm
    {
        public List<(string Path, string Query, string? Body)> Requests { get; } = [];

        /// <summary>When set, GetUnitDetails answers with this lead id instead of the one it was asked about (a CRM that returns another booking).</summary>
        public int? SaleLeadOverride { get; set; }

        public IEnumerable<NameValueCollectionView> UnitDetailsCalls =>
            Requests.Where(r => r.Path.EndsWith("/TicketingSystem/GetUnitDetails", StringComparison.Ordinal))
                .Select(r => new NameValueCollectionView(HttpUtility.ParseQueryString(r.Query)));

        public IEnumerable<(string Path, string Query, string? Body)> DocumentCalls =>
            Requests.Where(r => r.Path.EndsWith("/TicketingSystem/GetCustomerDocuments", StringComparison.Ordinal));

        public HttpMessageHandler Handler => new StubHttpMessageHandler(async (request, _) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((path, request.RequestUri.Query, body));

            if (path.EndsWith("/TicketingSystem/GetBuyerByPhone", StringComparison.Ordinal))
            {
                return Json(Buyer());
            }

            if (path.EndsWith("/TicketingSystem/GetUnitDetails", StringComparison.Ordinal))
            {
                return Json(UnitDetailsFor(HttpUtility.ParseQueryString(request.RequestUri.Query)));
            }

            if (path.EndsWith("/TicketingSystem/GetCustomerDocuments", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body!);
                return Json(JsonSerializer.Serialize(new
                {
                    customerId = doc.RootElement.GetProperty("CustomerID").GetInt32(),
                    leadId = doc.RootElement.GetProperty("LeadID").GetInt32(),
                    count = 1, selectionRequired = false,
                    attachments = new[] { new { attachmentId = "5001", fileUrl = "/Uploads/Contracts/5001.pdf", name = "Sale and Purchase Agreement.pdf" } }
                }));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(PdfBytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }
            };
        });

        private string UnitDetailsFor(System.Collections.Specialized.NameValueCollection query)
        {
            var asked = int.Parse(query["leadId"]!);
            var wantsSale = string.Equals(query["includeSale"], "true", StringComparison.OrdinalIgnoreCase);
            // Different recorded sale per lead, so a sale read from the wrong lead is visible in the price.
            var (price, fee) = asked == 12345 ? (1850000.50m, 74000m) : (2000000m, 80000m);
            var unit = new Dictionary<string, object?>
            {
                ["towerName"] = "Tower A",
                ["project"] = new
                {
                    status = "Under construction", expectedHandoverDate = "2027-03-31",
                    completionPercentage = 62.5, expectedCompletionDate = "2027-01-31", actualCompletionDate = (string?)null
                }
            };
            if (wantsSale)
            {
                unit["sale"] = new { leadId = SaleLeadOverride ?? asked, soldPrice = price, registrationCost = fee, currency = "AED" };
            }

            return JsonSerializer.Serialize(new { success = true, found = true, message = (string?)null, unit });
        }
    }

    private sealed record NameValueCollectionView(System.Collections.Specialized.NameValueCollection Q)
    {
        public string? this[string key] => Q[key];
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Buyer() => JsonSerializer.Serialize(new
    {
        success = true, found = true, message = "Buyer found successfully.",
        buyers = new[]
        {
            new
            {
                customer = new { customerId = 9001, fullNameEnglish = "Ahmed Ali", fullNameArabic = (string?)null, mobileNumber = CrmMobile, email = BuyerEmail },
                units = new[]
                {
                    new { leadId = 12345, leadStatus = 8, leadStatusName = "Sold", unitId = 1101, unitNumber = "1205", unitStatus = 3, unitType = 2, floorNumber = 12, projectId = 79, projectName = "Tiger Sky Tower", projectArabicName = (string?)null, customerType = 1, customerTypeName = "Buyer" },
                    new { leadId = 12346, leadStatus = 8, leadStatusName = "Sold", unitId = 1102, unitNumber = "1403", unitStatus = 3, unitType = 2, floorNumber = 14, projectId = 79, projectName = "Tiger Sky Tower", projectArabicName = (string?)null, customerType = 1, customerTypeName = "Buyer" }
                }
            }
        }
    });

    private readonly StubCrm _crm = new();
    private readonly TigerCsApiFactory _factory;

    public GenesysSmsOtpEndpointTests()
    {
        _factory = new TigerCsApiFactory
        {
            ExtraConfiguration =
            {
                ["CrmDocuments:Enabled"] = "true",
                ["CrmDocuments:OtpCodePepper"] = "test-only-pepper",
                ["CrmDocuments:OtpSmsEnabled"] = "true",
                ["CrmDocuments:OtpMinResendSeconds"] = "0",
                ["Sms:Provider"] = "Fake",
                ["Crm:BaseUrl"] = CrmBase,
                ["Crm:SecretKey"] = SecretKey
            },
            ExtraServices = services =>
            {
                // The REAL CRM HTTP gateways over the stub CRM instead of the network.
                services.RemoveAll<ICrmBuyerLookupGateway>();
                services.AddHttpClient<ICrmBuyerLookupGateway, CrmBuyerHttpGateway>(c => c.BaseAddress = new Uri(CrmBase))
                    .ConfigurePrimaryHttpMessageHandler(() => _crm.Handler);
                services.AddHttpClient<CrmDocumentHttpGateway>(c => c.BaseAddress = new Uri(CrmBase))
                    .ConfigurePrimaryHttpMessageHandler(() => _crm.Handler);
                services.RemoveAll<TigerCS.Application.Modules.CrmDocuments.Abstractions.ICrmDocumentGateway>();
                services.AddScoped<TigerCS.Application.Modules.CrmDocuments.Abstractions.ICrmDocumentGateway>(
                    sp => sp.GetRequiredService<CrmDocumentHttpGateway>());
                services.AddHttpClient<CrmUnitDetailsHttpGateway>(c => c.BaseAddress = new Uri(CrmBase))
                    .ConfigurePrimaryHttpMessageHandler(() => _crm.Handler);
                services.RemoveAll<ICrmUnitDetailsGateway>();
                services.AddScoped<ICrmUnitDetailsGateway>(sp => sp.GetRequiredService<CrmUnitDetailsHttpGateway>());
            }
        };
    }

    // One host per test: dispose it, or the file watchers every host holds exhaust the OS limit for later tests.
    public void Dispose() => _factory.Dispose();

    private FakeSmsSender Sms => _factory.Services.GetRequiredService<FakeSmsSender>();
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

    private static string NewKey() => "it-" + Guid.NewGuid().ToString("N");

    private string LastSmsCode() => Regex.Match(Sms.Last!.Text, @"\b\d{6}\b").Value;

    private static async Task<Guid> SendSmsAsync(HttpClient client, string unit)
    {
        var sent = await Post(client, Verification + "/otp/send", new { phoneNumber = SearchedPhone, crmUnitId = unit, channel = "Sms" });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        return (await JsonOf(sent)).GetProperty("challengeId").GetGuid();
    }

    /// <summary>SMS send → verify. Returns (challenge, session, the session DTO).</summary>
    private async Task<(Guid Challenge, Guid Session, JsonElement Dto)> VerifyBySmsAsync(HttpClient client, string unit = "1101")
    {
        var challenge = await SendSmsAsync(client, unit);
        var verified = await Post(client, Verification + "/otp/verify", new { challengeId = challenge, code = LastSmsCode() });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var dto = (await JsonOf(verified)).GetProperty("session");
        return (challenge, dto.GetProperty("verificationSessionId").GetGuid(), dto);
    }

    private static Task<HttpResponseMessage> Details(HttpClient client, int unit, Guid? session, string customer = "crm:9001") =>
        Post(client, UnitDetails, new { customerReference = customer, phoneNumber = SearchedPhone, unitId = unit, verificationSessionId = session });

    // =====================================================================
    //  The whole flow
    // =====================================================================

    [Fact]
    public async Task EndToEnd_SmsSend_Verify_UnitDetailsWithTheSale_SendCopy()
    {
        var client = await ClientAsync();
        var mailBefore = Mailbox.Recorded.Count;

        // 1. Lookup advertises SMS and shows a masked mobile only.
        var lookup = await Post(client, Verification + "/buyer-lookup", new { phoneNumber = SearchedPhone });
        var found = await JsonOf(lookup);
        Assert.Equal(["Email", "Sms"], found.GetProperty("availableChannels").EnumerateArray().Select(c => c.GetString()!).ToArray());
        Assert.Equal("+971******888", found.GetProperty("maskedMobile").GetString());
        Assert.DoesNotContain("9998888", await lookup.Content.ReadAsStringAsync());

        // 2. Send by SMS: the code goes to CRM's mobile, not the searched number, and not by email.
        var send = await Post(client, Verification + "/otp/send", new { phoneNumber = SearchedPhone, crmUnitId = "1101", channel = "Sms", language = "ar" });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        var sent = await JsonOf(send);
        Assert.Equal("CodeSent", sent.GetProperty("status").GetString());
        Assert.Equal("Sms", sent.GetProperty("channel").GetString());
        Assert.Equal("+971******888", sent.GetProperty("maskedDestination").GetString());
        var sms = Assert.Single(Sms.Sent);
        Assert.Equal(("971509998888", "ar"), (sms.Destination, sms.Language));
        Assert.Equal(mailBefore, Mailbox.Recorded.Count);
        var code = LastSmsCode();
        Assert.DoesNotContain(code, await send.Content.ReadAsStringAsync());
        var challengeId = sent.GetProperty("challengeId").GetGuid();

        // 3. Verify → the ordinary OTP-verified session, with the cached contact as CRM holds it.
        var verify = await Post(client, Verification + "/otp/verify", new { challengeId, code });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var session = (await JsonOf(verify)).GetProperty("session");
        Assert.Equal("Otp", session.GetProperty("verificationMethod").GetString());
        Assert.Equal("Confirmed", session.GetProperty("status").GetString());
        Assert.Equal(CrmMobile, session.GetProperty("snapshotContactChannel").GetString());
        var sessionId = session.GetProperty("verificationSessionId").GetGuid();

        // 4. Unit details: without the session no sale (and CRM is not even asked for it)…
        var without = await JsonOf(await Details(client, 1101, null));
        Assert.Equal("VerificationRequired", without.GetProperty("financialDetailsStatus").GetString());
        Assert.Equal(JsonValueKind.Null, without.GetProperty("sale").ValueKind);
        Assert.DoesNotContain(_crm.UnitDetailsCalls, c => c["includeSale"] == "true");

        // …with it, the sale of exactly this customer's unit and Lead, and the project completion apart from handover.
        var with = await JsonOf(await Details(client, 1101, sessionId));
        Assert.Equal("Available", with.GetProperty("financialDetailsStatus").GetString());
        var sale = with.GetProperty("sale");
        Assert.Equal(1850000.50m, sale.GetProperty("soldPrice").GetProperty("amount").GetDecimal());
        Assert.Equal("AED", sale.GetProperty("soldPrice").GetProperty("currency").GetString());
        Assert.Equal(74000m, sale.GetProperty("registrationCost").GetProperty("amount").GetDecimal());
        var project = with.GetProperty("project");
        Assert.Equal(62.5m, project.GetProperty("completionPercentage").GetDecimal());
        Assert.Equal("2027-01-31", project.GetProperty("expectedCompletionDate").GetString());
        Assert.Equal("2027-03-31", project.GetProperty("expectedHandoverDate").GetString());
        var saleCall = _crm.UnitDetailsCalls.Last(c => c["includeSale"] == "true");
        Assert.Equal(("9001", "1101", "12345"), (saleCall["customerId"], saleCall["unitId"], saleCall["leadId"]));

        // 5. The same session passes the unchanged send-copy checks.
        var documentMailsBefore = Mailbox.Recorded.Count;
        var copy = await Post(client, SendCopy, new { verificationSessionId = sessionId, documentType = "Contract" }, NewKey());
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        Assert.Equal("Sent", (await JsonOf(copy)).GetProperty("status").GetString());
        var documentMail = Assert.Single(Mailbox.Recorded.Skip(documentMailsBefore));
        Assert.Equal(BuyerEmail, documentMail.ToAddress);
        using var documentRequest = JsonDocument.Parse(_crm.DocumentCalls.Last().Body!);
        Assert.Equal((9001, 12345), (documentRequest.RootElement.GetProperty("CustomerID").GetInt32(), documentRequest.RootElement.GetProperty("LeadID").GetInt32()));
        Assert.Single(Sms.Sent);   // nothing else was texted along the way
    }

    // =====================================================================
    //  Wrong customer / unit / Lead
    // =====================================================================

    [Fact]
    public async Task ASessionVerifiedForOneUnit_OpensNeitherTheOtherUnitsSale_NorItsDocuments()
    {
        var client = await ClientAsync();
        var (_, session, _) = await VerifyBySmsAsync(client, "1101");   // unit 1101 / Lead 12345 only

        var other = await JsonOf(await Details(client, 1102, session));
        Assert.Equal("VerificationFailed", other.GetProperty("financialDetailsStatus").GetString());
        Assert.Equal(JsonValueKind.Null, other.GetProperty("sale").ValueKind);
        Assert.DoesNotContain(_crm.UnitDetailsCalls, c => c["unitId"] == "1102" && c["includeSale"] == "true");

        foreach (var lead in new[] { 12346, 22222 })
        {
            var refused = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract", crmLeadId = lead }, NewKey());
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("RECORD_OWNERSHIP_MISMATCH", (await JsonOf(refused)).GetProperty("code").GetString());
        }

        Assert.Empty(_crm.DocumentCalls);
    }

    [Fact]
    public async Task AnotherCustomersReference_AndAnotherCustomersUnit_AreRefused_EvenWithAValidSession()
    {
        var client = await ClientAsync();
        var (_, session, _) = await VerifyBySmsAsync(client, "1101");

        var customer = await Details(client, 1101, session, customer: "crm:7777");
        var unit = await Details(client, 5555, session);

        Assert.Equal(HttpStatusCode.Forbidden, customer.StatusCode);
        Assert.Equal("CUSTOMER_NOT_VERIFIED", (await JsonOf(customer)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, unit.StatusCode);
        Assert.Equal("UNIT_NOT_ELIGIBLE", (await JsonOf(unit)).GetProperty("code").GetString());
        Assert.DoesNotContain(_crm.UnitDetailsCalls, c => c["includeSale"] == "true" && (c["customerId"] != "9001" || c["unitId"] == "5555"));
    }

    [Fact]
    public async Task ACrmSaleForAnotherLead_IsDiscarded_NotShown()
    {
        var client = await ClientAsync();
        var (_, session, _) = await VerifyBySmsAsync(client, "1101");
        _crm.SaleLeadOverride = 99999;   // CRM answers with an unrelated booking

        var body = await JsonOf(await Details(client, 1101, session));

        Assert.Equal("NotAvailable", body.GetProperty("financialDetailsStatus").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sale").ValueKind);
        Assert.DoesNotContain("2000000", body.ToString());
    }

    // =====================================================================
    //  Asserted sessions never release anything
    // =====================================================================

    [Fact]
    public async Task AnAgentAssertedSession_OnTheVeryUnitCustomerAndAcceptedMethod_ReleasesNeitherTheSaleNorADocument()
    {
        var client = await ClientAsync();
        var (_, _, verified) = await VerifyBySmsAsync(client, "1101");   // only to get the genuine cached unit + contact rows

        var unitRef = verified.GetProperty("unitReferenceId").GetInt32();
        var contactRef = verified.GetProperty("contactReferenceId").GetInt32();

        // The generic endpoint will not mint an Otp session…
        var claim = await Post(client, "/api/verification-sessions", new { unitReferenceId = unitRef, contactReferenceId = contactRef, confirmed = true, verificationMethod = "Otp" });
        Assert.Equal(HttpStatusCode.BadRequest, claim.StatusCode);

        // …but will record an assertion with a method the policy accepts, for exactly the same unit and contact.
        var asserted = await Post(client, "/api/verification-sessions", new { unitReferenceId = unitRef, contactReferenceId = contactRef, confirmed = true, verificationMethod = "AuthenticatedDigitalUser" });
        Assert.Equal(HttpStatusCode.Created, asserted.StatusCode);
        var sessionId = (await JsonOf(asserted)).GetProperty("verificationSessionId").GetGuid();

        var details = await JsonOf(await Details(client, 1101, sessionId));
        Assert.Equal("VerificationFailed", details.GetProperty("financialDetailsStatus").GetString());
        Assert.Equal(JsonValueKind.Null, details.GetProperty("sale").ValueKind);

        var copy = await Post(client, SendCopy, new { verificationSessionId = sessionId, documentType = "Contract" }, NewKey());
        Assert.Equal(HttpStatusCode.Forbidden, copy.StatusCode);
        Assert.Equal("VERIFICATION_FAILED", (await JsonOf(copy)).GetProperty("code").GetString());
        Assert.Empty(_crm.DocumentCalls);
        Assert.DoesNotContain(_crm.UnitDetailsCalls, c => c["includeSale"] == "true");
    }

    [Fact]
    public async Task ASessionBelongsToItsAccount_ForTheSaleToo()
    {
        var owner = await ClientAsync();
        var (_, session, _) = await VerifyBySmsAsync(owner, "1101");

        var other = await JsonOf(await Details(await ClientAsync(), 1101, session));

        Assert.Equal("VerificationFailed", other.GetProperty("financialDetailsStatus").GetString());
        Assert.Equal(JsonValueKind.Null, other.GetProperty("sale").ValueKind);
    }

    // =====================================================================
    //  Replay
    // =====================================================================

    [Fact]
    public async Task ACodeWorksOnce_AReplayIs409_AndLeavesOneSession()
    {
        var client = await ClientAsync();
        var challenge = await SendSmsAsync(client, "1101");
        var code = LastSmsCode();

        var first = await Post(client, Verification + "/otp/verify", new { challengeId = challenge, code });
        var replay = await Post(client, Verification + "/otp/verify", new { challengeId = challenge, code });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("OTP_ALREADY_USED", (await JsonOf(replay)).GetProperty("code").GetString());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.Equal(1, await db.VerificationSessions.CountAsync(s => s.ProofChallengeId == challenge));
    }

    [Fact]
    public async Task AnotherAccount_CannotVerifyTheSmsChallenge()
    {
        var challenge = await SendSmsAsync(await ClientAsync(), "1101");

        var response = await Post(await ClientAsync(), Verification + "/otp/verify", new { challengeId = challenge, code = LastSmsCode() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("OTP_CHALLENGE_NOT_FOUND", (await JsonOf(response)).GetProperty("code").GetString());
    }

    // =====================================================================
    //  Delivery honesty over HTTP
    // =====================================================================

    [Fact]
    public async Task AnUnconfirmedSms_Is504_ARetriedSendDoesNotTextAgain_AndAnExplicitResendDoes()
    {
        var client = await ClientAsync();
        Sms.Then(SmsSendOutcome.Unconfirmed);
        var before = Sms.Sent.Count;

        var first = await Post(client, Verification + "/otp/send", new { phoneNumber = SearchedPhone, crmUnitId = "1101", channel = "Sms" });
        Assert.Equal(HttpStatusCode.GatewayTimeout, first.StatusCode);
        var problem = await JsonOf(first);
        Assert.Equal("OTP_DELIVERY_UNCONFIRMED", problem.GetProperty("code").GetString());
        Assert.NotEqual("CodeSent", problem.GetProperty("outcome").GetString());
        var challenge = problem.GetProperty("challengeId").GetGuid();
        Assert.Equal(before + 1, Sms.Sent.Count);

        // A flow that retries "send" on the 504 must not double-text (the resend interval is 0 in this host).
        var retry = await Post(client, Verification + "/otp/send", new { phoneNumber = SearchedPhone, crmUnitId = "1101", channel = "Sms" });
        Assert.Equal(HttpStatusCode.GatewayTimeout, retry.StatusCode);
        Assert.Equal(challenge, (await JsonOf(retry)).GetProperty("challengeId").GetGuid());
        Assert.Equal(before + 1, Sms.Sent.Count);

        // Only an explicit resend sends another.
        var resend = await Post(client, Verification + "/otp/resend", new { challengeId = challenge });
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        Assert.Equal(before + 2, Sms.Sent.Count);
        var verified = await Post(client, Verification + "/otp/verify", new { challengeId = challenge, code = LastSmsCode() });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
    }

    [Theory]
    [InlineData(SmsSendOutcome.Rejected)]
    [InlineData(SmsSendOutcome.Failed)]
    public async Task ARejectedOrFailedSms_Is502_NeverASuccess(SmsSendOutcome outcome)
    {
        var client = await ClientAsync();
        Sms.Then(outcome);

        var response = await Post(client, Verification + "/otp/send", new { phoneNumber = SearchedPhone, crmUnitId = "1101", channel = "Sms" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("OTP_DELIVERY_FAILED", (await JsonOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task EmailStillWorksExactlyAsBefore_WhenNoChannelIsNamed()
    {
        var client = await ClientAsync();
        var smsBefore = Sms.Sent.Count;
        var mailBefore = Mailbox.Recorded.Count;

        var send = await Post(client, Verification + "/otp/send", new { phoneNumber = SearchedPhone, crmUnitId = "1101" });

        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        var body = await JsonOf(send);
        Assert.Equal("CodeSent", body.GetProperty("status").GetString());
        Assert.Equal("a***@e***.test", body.GetProperty("maskedDestination").GetString());
        Assert.Equal(smsBefore, Sms.Sent.Count);
        var mail = Assert.Single(Mailbox.Recorded.Skip(mailBefore));
        Assert.Equal(BuyerEmail, mail.ToAddress);
    }

    [Fact]
    public async Task WithSmsSwitchedOff_TheChannelIs503_AndNothingIsCreated()
    {
        using var off = new TigerCsApiFactory
        {
            ExtraConfiguration = { ["CrmDocuments:Enabled"] = "true", ["CrmDocuments:OtpCodePepper"] = "test-only-pepper", ["Sms:Provider"] = "Fake" }   // OtpSmsEnabled left at its default (false)
        };
        var (username, password, _) = await off.SeedEmployeeAsync(Roles.CsAgent);
        var client = off.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginResponseDto>())!.AccessToken);

        var response = await Post(client, Verification + "/otp/send", new { phoneNumber = SearchedPhone, crmUnitId = "1101", channel = "Sms" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("OTP_SMS_NOT_CONFIGURED", (await JsonOf(response)).GetProperty("code").GetString());
        Assert.Empty(off.Services.GetRequiredService<FakeSmsSender>().Sent);
    }
}
