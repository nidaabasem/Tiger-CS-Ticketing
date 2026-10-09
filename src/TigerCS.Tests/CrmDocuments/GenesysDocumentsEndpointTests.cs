using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Integrations.Modules.EmailIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// The whole chatbot document flow through the real host, over the public
/// Genesys routes: <b>CRM buyer lookup → unit selection → email OTP →
/// verified session → document selection → download → exactly one email.</b>
///
/// <para>
/// Real here: authentication, the OTP service, the session service, the
/// buyer-lookup HTTP gateway (<c>GetBuyerByPhone</c>, parsed by the real
/// parser), the document HTTP gateway (<c>GetCustomerDocuments</c> + file
/// download), the delivery record and idempotency, and the email adapter (the
/// in-memory recording one, so the OTP code and the attachment can be read
/// back). Stubbed: Tiger CRM itself — one in-process handler that implements
/// the three CRM operations from <c>docs/Genesys/CRM-Required-Contracts.md</c>
/// and records every request it receives.
/// </para>
/// </summary>
public sealed class GenesysDocumentsEndpointTests : IDisposable
{
    private const string SendCopy = "/api/genesys/documents/send-copy";
    private const string CrmBase = "https://crm.uat.example.test:8014/";
    private const string SecretKey = "uat-like-secret";
    private const string Phone = "+971501234567";
    private const string BuyerEmail = "ahmed.ali@example.test";

    private static readonly byte[] PdfBytes = [.. "%PDF-1.7\n"u8, 1, 2, 3];
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    /// <summary>An in-process Tiger CRM: records requests, answers as scripted.</summary>
    private sealed class StubCrm
    {
        public List<(HttpMethod Method, string Url, string? Secret, string? Body)> Requests { get; } = [];

        public string BuyerJson { get; set; } = Buyer();
        public HttpStatusCode BuyerStatus { get; set; } = HttpStatusCode.OK;
        public Func<string, HttpResponseMessage>? DocumentsResponder { get; set; }
        public Func<string, HttpResponseMessage> FileResponder { get; set; } = _ => FileOk(PdfBytes);

        public IEnumerable<(HttpMethod Method, string Url, string? Secret, string? Body)> DocumentCalls =>
            Requests.Where(r => r.Url.Contains("GetCustomerDocuments", StringComparison.Ordinal));

        public HttpMessageHandler Handler => new StubHttpMessageHandler(async (request, _) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            Requests.Add((request.Method, request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-SECRET-KEY", out var v) ? v.Single() : null, body));

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/TicketingSystem/GetBuyerByPhone", StringComparison.Ordinal))
            {
                return Json(BuyerStatus, BuyerJson);
            }

            if (path.EndsWith("/TicketingSystem/GetCustomerDocuments", StringComparison.Ordinal))
            {
                return DocumentsResponder!(body!);
            }

            return FileResponder(path);
        });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage FileOk(byte[] bytes, string contentType = "application/pdf") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue(contentType) } } };

    /// <summary>CRM's GetBuyerByPhone answer: customer 9001 with two units (leads 12345 / 12346).</summary>
    private static string Buyer(string? email = BuyerEmail, bool twoUnits = true) => JsonSerializer.Serialize(new
    {
        success = true, found = true, message = "Buyer found successfully.",
        buyers = new[]
        {
            new
            {
                customer = new { customerId = 9001, fullNameEnglish = "Ahmed Ali", fullNameArabic = (string?)null, mobileNumber = Phone, email },
                units = new[]
                {
                    new { leadId = 12345, leadStatus = 8, leadStatusName = "Sold", unitId = 1101, unitNumber = "1205", unitStatus = 3, unitType = 2, floorNumber = 12, projectId = 79, projectName = "Tiger Sky Tower", projectArabicName = (string?)null, customerType = 1, customerTypeName = "Buyer" },
                    new { leadId = 12346, leadStatus = 8, leadStatusName = "Sold", unitId = 1102, unitNumber = "1403", unitStatus = 3, unitType = 2, floorNumber = 14, projectId = 79, projectName = "Tiger Sky Tower", projectArabicName = (string?)null, customerType = 1, customerTypeName = "Buyer" }
                }.Take(twoUnits ? 2 : 1).ToArray()
            }
        }
    });

    /// <summary>CRM's GetCustomerDocuments answer for the customer/lead asked about, with the given attachments.</summary>
    private static Func<string, HttpResponseMessage> Documents(bool selectionRequired, params (string? Id, string Name, string Url)[] attachments) =>
        body =>
        {
            using var doc = JsonDocument.Parse(body);
            var customerId = doc.RootElement.GetProperty("CustomerID").GetInt32();
            var leadId = doc.RootElement.GetProperty("LeadID").GetInt32();
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                customerId, leadId, count = attachments.Length, selectionRequired,
                attachments = attachments.Select(a => a.Id is null
                    ? (object)new { fileUrl = a.Url, name = a.Name }
                    : new { attachmentId = a.Id, fileUrl = a.Url, name = a.Name }).ToArray()
            }));
        };

    private readonly StubCrm _crm = new();
    private readonly TigerCsApiFactory _factory;

    public GenesysDocumentsEndpointTests()
    {
        _factory = new TigerCsApiFactory
        {
            ExtraConfiguration =
            {
                ["CrmDocuments:Enabled"] = "true",
                ["CrmDocuments:OtpCodePepper"] = "test-only-pepper",
                ["Crm:BaseUrl"] = CrmBase,
                ["Crm:SecretKey"] = SecretKey
            },
            ExtraServices = services =>
            {
                // The REAL CRM HTTP gateways (buyer lookup, documents) over the stub CRM instead of the network.
                services.RemoveAll<ICrmBuyerLookupGateway>();
                services.AddHttpClient<ICrmBuyerLookupGateway, CrmBuyerHttpGateway>(c => c.BaseAddress = new Uri(CrmBase))
                    .ConfigurePrimaryHttpMessageHandler(() => _crm.Handler);
                services.AddHttpClient<CrmDocumentHttpGateway>(c => c.BaseAddress = new Uri(CrmBase))
                    .ConfigurePrimaryHttpMessageHandler(() => _crm.Handler);
                services.RemoveAll<TigerCS.Application.Modules.CrmDocuments.Abstractions.ICrmDocumentGateway>();
                services.AddScoped<TigerCS.Application.Modules.CrmDocuments.Abstractions.ICrmDocumentGateway>(
                    sp => sp.GetRequiredService<CrmDocumentHttpGateway>());
            }
        };
    }

    // One host per test: dispose it, or the file watchers every host holds exhaust the OS limit for later tests.
    public void Dispose() => _factory.Dispose();

    private RecordingEmailSender Mailbox => _factory.Services.GetRequiredService<RecordingEmailSender>();

    private async Task<HttpClient> ClientAsync()
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static Task<HttpResponseMessage> Post(HttpClient client, string route, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }

    private static string NewKey() => "it-" + Guid.NewGuid().ToString("N");

    private int MailCount => Mailbox.Recorded.Count;

    /// <summary>The 6-digit code in the most recent verification email.</summary>
    private string LastCode() =>
        Regex.Match(Mailbox.Recorded.Last(m => m.Subject.Contains("verification code", StringComparison.Ordinal)).Body, @"\b\d{6}\b").Value;

    private static async Task<Guid> StartChallengeAsync(HttpClient client, string unit = "1101")
    {
        var sent = await Post(client, "/api/genesys/verification/otp/send", new { phoneNumber = Phone, crmUnitId = unit });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        return (await JsonAsync(sent)).GetProperty("challengeId").GetGuid();
    }

    /// <summary>Lookup → unit → email OTP → verify. Returns the verified session id.</summary>
    private async Task<Guid> VerifyCustomerAsync(HttpClient client, string unit = "1101")
    {
        var challenge = await StartChallengeAsync(client, unit);
        var verified = await Post(client, "/api/genesys/verification/otp/verify", new { challengeId = challenge, code = LastCode() });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        return (await JsonAsync(verified)).GetProperty("session").GetProperty("verificationSessionId").GetGuid();
    }

    // =====================================================================
    //  The whole flow
    // =====================================================================

    [Fact]
    public async Task EndToEnd_Lookup_UnitSelection_Otp_Session_DocumentSelection_Download_ExactlyOneEmail()
    {
        var client = await ClientAsync();
        _crm.DocumentsResponder = Documents(true,
            ("5001", "Sale and Purchase Agreement.pdf", "/Uploads/Contracts/5001.pdf"), ("5002", "Addendum 1.pdf", "/Uploads/Contracts/5002.pdf"));
        var before = MailCount;

        // 1. Real CRM lookup (GetBuyerByPhone) — two units, a masked email, nothing sensitive.
        var lookup = await Post(client, "/api/genesys/verification/buyer-lookup", new { phoneNumber = Phone });
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        var found = await JsonAsync(lookup);
        Assert.Equal("Found", found.GetProperty("status").GetString());
        Assert.Equal(["1101", "1102"], found.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("crmUnitId").GetString()!).ToArray());
        Assert.Equal("a***@e***.test", found.GetProperty("maskedDestination").GetString());
        var lookupJson = await lookup.Content.ReadAsStringAsync();
        Assert.DoesNotContain(BuyerEmail, lookupJson);
        Assert.DoesNotContain("Ahmed", lookupJson);
        Assert.Equal(MailCount, before);

        // 2. Several units and no choice → asked which; nothing emailed.
        var ask = await Post(client, "/api/genesys/verification/otp/send", new { phoneNumber = Phone });
        Assert.Equal("UnitSelectionRequired", (await JsonAsync(ask)).GetProperty("status").GetString());
        Assert.Equal(MailCount, before);

        // 3. Unit chosen → a code goes to the address CRM returned (the request had no destination).
        var send = await Post(client, "/api/genesys/verification/otp/send", new { phoneNumber = Phone, crmUnitId = "1101" });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        var sent = await JsonAsync(send);
        Assert.Equal("CodeSent", sent.GetProperty("status").GetString());
        var codeMail = Assert.Single(Mailbox.Recorded.Skip(before));
        Assert.Equal(BuyerEmail, codeMail.ToAddress);
        var challengeId = sent.GetProperty("challengeId").GetGuid();
        Assert.DoesNotContain(LastCode(), await send.Content.ReadAsStringAsync()); // the code is only ever in the email

        // 4. The code → the OTP-verified session.
        var verify = await Post(client, "/api/genesys/verification/otp/verify", new { challengeId, code = LastCode() });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var verified = await JsonAsync(verify);
        Assert.Equal("Verified", verified.GetProperty("status").GetString());
        var session = verified.GetProperty("session");
        Assert.Equal("Otp", session.GetProperty("verificationMethod").GetString());
        Assert.Equal("Confirmed", session.GetProperty("status").GetString());
        Assert.Equal("1205", session.GetProperty("snapshotUnitNumber").GetString());
        var sessionId = session.GetProperty("verificationSessionId").GetGuid();

        // 5. Document selection: CRM says selectionRequired → choices, nothing fetched or sent.
        var documentMailsBefore = MailCount;
        var choices = await Post(client, SendCopy, new { verificationSessionId = sessionId, documentType = "Contract" }, NewKey());
        Assert.Equal(HttpStatusCode.OK, choices.StatusCode);
        var offered = await JsonAsync(choices);
        Assert.Equal("SelectionRequired", offered.GetProperty("status").GetString());
        Assert.Equal("Document", offered.GetProperty("choiceKind").GetString());
        Assert.Equal(documentMailsBefore, MailCount);
        Assert.DoesNotContain(_crm.Requests, r => r.Url.Contains("/Uploads/", StringComparison.Ordinal));

        // 6. The choice → download (with the CRM secret, on the CRM origin) → one email with that file.
        var key = NewKey();
        var chosen = await Post(client, SendCopy, new { verificationSessionId = sessionId, documentType = "Contract", recordId = "5002" }, key);
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        var final = await JsonAsync(chosen);
        Assert.Equal("Sent", final.GetProperty("status").GetString());
        Assert.False(final.GetProperty("duplicate").GetBoolean());

        var documentCall = _crm.DocumentCalls.Last();
        Assert.Equal(SecretKey, documentCall.Secret);
        using (var body = JsonDocument.Parse(documentCall.Body!))
        {
            Assert.Equal(9001, body.RootElement.GetProperty("CustomerID").GetInt32());
            Assert.Equal(12345, body.RootElement.GetProperty("LeadID").GetInt32());
            Assert.Equal("TigerContract", body.RootElement.GetProperty("DocumentType").GetString());
        }

        var download = _crm.Requests.Last(r => r.Url.Contains("/Uploads/", StringComparison.Ordinal));
        Assert.Equal("https://crm.uat.example.test:8014/Uploads/Contracts/5002.pdf", download.Url);
        Assert.Equal(SecretKey, download.Secret);

        var documentMail = Assert.Single(Mailbox.Recorded.Skip(documentMailsBefore));
        Assert.Equal(BuyerEmail, documentMail.ToAddress);
        Assert.Equal(["Addendum 1.pdf"], documentMail.AttachmentFileNames);

        // 7. Genesys retries — same key, and a fresh key — and nothing more is sent.
        var replay = await Post(client, SendCopy, new { verificationSessionId = sessionId, documentType = "Contract", recordId = "5002" }, key);
        Assert.True((await JsonAsync(replay)).GetProperty("duplicate").GetBoolean());
        var newKey = await Post(client, SendCopy, new { verificationSessionId = sessionId, documentType = "Contract", recordId = "5002" }, NewKey());
        Assert.True((await JsonAsync(newKey)).GetProperty("duplicate").GetBoolean());
        Assert.Single(Mailbox.Recorded.Skip(documentMailsBefore));
    }

    [Fact]
    public async Task ALayoutPlan_WithNoExtensionInItsName_IsMailedWithItsTrueExtension()
    {
        var client = await ClientAsync();
        var session = await VerifyCustomerAsync(client);
        _crm.DocumentsResponder = Documents(false, (null, "Layout Plan", "/Uploads/UnitPlans/plan-1205"));
        _crm.FileResponder = _ => FileOk(PngBytes, "application/octet-stream");
        var before = MailCount;

        var response = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "UnitLayout" }, NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("LAYOUT-12345", (await JsonAsync(response)).GetProperty("recordId").GetString());
        using var body = JsonDocument.Parse(_crm.DocumentCalls.Last().Body!);
        Assert.Equal("Layout", body.RootElement.GetProperty("DocumentType").GetString());
        Assert.Equal(["Layout Plan.png"], Assert.Single(Mailbox.Recorded.Skip(before)).AttachmentFileNames); // not Layout Plan.pdf
    }

    [Theory]
    [InlineData("ReservationForm", "ReservationForm", "4001")]
    [InlineData("RegistrationReceipt", "RegistrationReceipt", "6001")]
    public async Task OtherDocumentTypes_FlowThroughTheSameVerifiedSession(string publicType, string crmType, string attachmentId)
    {
        var client = await ClientAsync();
        var session = await VerifyCustomerAsync(client);
        _crm.DocumentsResponder = Documents(false, (attachmentId, "Doc.pdf", "/Uploads/doc.pdf"));

        var response = await Post(client, SendCopy, new { verificationSessionId = session, documentType = publicType }, NewKey());

        Assert.Equal("Sent", (await JsonAsync(response)).GetProperty("status").GetString());
        using var body = JsonDocument.Parse(_crm.DocumentCalls.Last().Body!);
        Assert.Equal(crmType, body.RootElement.GetProperty("DocumentType").GetString());
    }

    // =====================================================================
    //  OTP: invalid, expired-by-lockout, reused, wrong account
    // =====================================================================

    [Fact]
    public async Task AWrongCode_IsRefused_WithTheAttemptsLeft_AndTheRightCodeStillWorks()
    {
        var client = await ClientAsync();
        var challenge = await StartChallengeAsync(client);

        var wrong = await Post(client, "/api/genesys/verification/otp/verify", new { challengeId = challenge, code = "000000" });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var problem = await JsonAsync(wrong);
        Assert.Equal("OTP_INVALID", problem.GetProperty("code").GetString());
        Assert.Equal(4, problem.GetProperty("attemptsRemaining").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/api/genesys/verification/otp/verify", new { challengeId = challenge, code = LastCode() })).StatusCode);
    }

    [Fact]
    public async Task FiveWrongCodes_Lock_AndTheRightCodeIsThenRefused()
    {
        var client = await ClientAsync();
        var challenge = await StartChallengeAsync(client);
        var wrongGuess = LastCode() == "000000" ? "000001" : "000000";

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "/api/genesys/verification/otp/verify", new { challengeId = challenge, code = wrongGuess })).StatusCode);
        }

        var fifth = await Post(client, "/api/genesys/verification/otp/verify", new { challengeId = challenge, code = wrongGuess });
        Assert.Equal((HttpStatusCode)423, fifth.StatusCode);

        var late = await Post(client, "/api/genesys/verification/otp/verify", new { challengeId = challenge, code = LastCode() });
        Assert.Equal((HttpStatusCode)423, late.StatusCode);
        Assert.Equal("OTP_LOCKED", (await JsonAsync(late)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ACodeWorksOnce_AReusedCodeIs409_AndMakesNoSecondSession()
    {
        var client = await ClientAsync();
        var challenge = await StartChallengeAsync(client);
        var code = LastCode();

        var first = await Post(client, "/api/genesys/verification/otp/verify", new { challengeId = challenge, code });
        var again = await Post(client, "/api/genesys/verification/otp/verify", new { challengeId = challenge, code });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("OTP_ALREADY_USED", (await JsonAsync(again)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnotherAccount_CannotVerify_OrBurnAttemptsOn_ThisChallenge()
    {
        var owner = await ClientAsync();
        var challenge = await StartChallengeAsync(owner);
        var intruder = await ClientAsync();

        var attempt = await Post(intruder, "/api/genesys/verification/otp/verify", new { challengeId = challenge, code = LastCode() });
        var resend = await Post(intruder, "/api/genesys/verification/otp/resend", new { challengeId = challenge });

        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
        Assert.Equal("OTP_CHALLENGE_NOT_FOUND", (await JsonAsync(attempt)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, resend.StatusCode);
        // …and the owner's challenge is untouched.
        Assert.Equal(HttpStatusCode.OK, (await Post(owner, "/api/genesys/verification/otp/verify", new { challengeId = challenge, code = LastCode() })).StatusCode);
    }

    [Fact]
    public async Task ARepeatedSend_DoesNotEmailAgain_AndResendIsRateLimited()
    {
        var client = await ClientAsync();
        var challenge = await StartChallengeAsync(client);
        var mails = MailCount;

        var again = await Post(client, "/api/genesys/verification/otp/send", new { phoneNumber = Phone, crmUnitId = "1101" });
        Assert.Equal("AlreadySent", (await JsonAsync(again)).GetProperty("status").GetString());

        var resend = await Post(client, "/api/genesys/verification/otp/resend", new { challengeId = challenge });
        Assert.Equal((HttpStatusCode)429, resend.StatusCode);
        Assert.Equal("OTP_RESEND_TOO_SOON", (await JsonAsync(resend)).GetProperty("code").GetString());
        Assert.True(resend.Headers.Contains("Retry-After"));
        Assert.Equal(mails, MailCount);
    }

    // =====================================================================
    //  CRM lookup outcomes
    // =====================================================================

    [Fact]
    public async Task CustomerNotFound_Ambiguous_NoEmail_AndCrmDown_AreEachExplicit_AndSendNothing()
    {
        var client = await ClientAsync();
        var before = MailCount;

        _crm.BuyerJson = """{ "success": true, "found": false, "message": "none", "buyers": [] }""";
        var notFound = await Post(client, "/api/genesys/verification/otp/send", new { phoneNumber = Phone });
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("CUSTOMER_NOT_FOUND", (await JsonAsync(notFound)).GetProperty("code").GetString());

        // Two distinct CRM customers behind one number: a data-integrity conflict, never guessed through.
        _crm.BuyerJson = JsonSerializer.Serialize(new
        {
            success = true, found = true, message = "ok",
            buyers = new[] { 9001, 9002 }.Select(id => new
            {
                customer = new { customerId = id, fullNameEnglish = "X", fullNameArabic = (string?)null, mobileNumber = Phone, email = "x@example.test" },
                units = new[] { new { leadId = id, leadStatus = 8, leadStatusName = "Sold", unitId = id, unitNumber = "1", unitStatus = 3, unitType = 2, floorNumber = 1, projectId = 1, projectName = "P", projectArabicName = (string?)null, customerType = 1, customerTypeName = "Buyer" } }
            })
        });
        var ambiguous = await Post(client, "/api/genesys/verification/otp/send", new { phoneNumber = Phone });
        Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);
        Assert.Equal("CUSTOMER_AMBIGUOUS", (await JsonAsync(ambiguous)).GetProperty("code").GetString());

        _crm.BuyerJson = Buyer(email: null);
        var noEmail = await Post(client, "/api/genesys/verification/otp/send", new { phoneNumber = Phone, crmUnitId = "1101" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noEmail.StatusCode);
        Assert.Equal("NO_EMAIL_ON_RECORD", (await JsonAsync(noEmail)).GetProperty("code").GetString());

        _crm.BuyerJson = "{}";
        _crm.BuyerStatus = HttpStatusCode.ServiceUnavailable;
        var down = await Post(client, "/api/genesys/verification/otp/send", new { phoneNumber = Phone, crmUnitId = "1101" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal("CRM_UNAVAILABLE", (await JsonAsync(down)).GetProperty("code").GetString());

        _crm.BuyerStatus = HttpStatusCode.Unauthorized;
        var unauthorised = await Post(client, "/api/genesys/verification/otp/send", new { phoneNumber = Phone, crmUnitId = "1101" });
        Assert.Equal(HttpStatusCode.BadGateway, unauthorised.StatusCode);
        Assert.Equal("CRM_AUTHENTICATION_FAILED", (await JsonAsync(unauthorised)).GetProperty("code").GetString());

        Assert.Equal(before, MailCount);
    }

    [Fact]
    public async Task AUnitThatIsNotTheCustomers_Is403_AndNoCodeIsSent()
    {
        var client = await ClientAsync();
        var before = MailCount;

        var response = await Post(client, "/api/genesys/verification/otp/send", new { phoneNumber = Phone, crmUnitId = "7777" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("UNIT_NOT_OWNED", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(before, MailCount);
    }

    // =====================================================================
    //  Cannot be bypassed
    // =====================================================================

    [Fact]
    public async Task TheGenericSessionEndpoint_RefusesOtp_AndAnAgentAssertedSessionNeverGetsADocument()
    {
        var client = await ClientAsync();
        var unit = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1101")).Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1101/contacts")).Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();
        var contactId = contacts!.Single().ContactReferenceId;

        // "Otp" cannot be claimed.
        var claim = await Post(client, "/api/verification-sessions", new { unitReferenceId = unit!.UnitReferenceId, contactReferenceId = contactId, confirmed = true, verificationMethod = "Otp" });
        Assert.Equal(HttpStatusCode.BadRequest, claim.StatusCode);
        Assert.Contains("otp-requires-challenge", await claim.Content.ReadAsStringAsync());

        // The existing authorised manual-agent verification still works…
        var manual = await Post(client, "/api/verification-sessions", new { unitReferenceId = unit.UnitReferenceId, contactReferenceId = contactId, confirmed = true, verificationMethod = "ManualAgentConfirmation" });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);

        // …and so does AuthenticatedDigitalUser, but neither can fetch a document.
        var digital = await Post(client, "/api/verification-sessions", new { unitReferenceId = unit.UnitReferenceId, contactReferenceId = contactId, confirmed = true, verificationMethod = "AuthenticatedDigitalUser" });
        Assert.Equal(HttpStatusCode.Created, digital.StatusCode);
        _crm.DocumentsResponder = Documents(false, ("5001", "C.pdf", "/Uploads/c.pdf"));
        foreach (var asserted in new[] { manual, digital })
        {
            var id = (await asserted.Content.ReadFromJsonAsync<VerificationSessionResponseDto>())!.VerificationSessionId;
            var refused = await Post(client, SendCopy, new { verificationSessionId = id, documentType = "Contract" }, NewKey());
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("VERIFICATION_FAILED", (await JsonAsync(refused)).GetProperty("code").GetString());
        }

        Assert.Empty(_crm.DocumentCalls);
    }

    [Fact]
    public async Task AnotherCustomersLead_AndTheCustomersOtherUnit_AreRefused_WithoutCallingCrmForDocuments()
    {
        var client = await ClientAsync();
        var session = await VerifyCustomerAsync(client, "1101"); // verified for unit 1101 / lead 12345 only
        _crm.DocumentsResponder = Documents(false, ("5001", "C.pdf", "/Uploads/c.pdf"));

        foreach (var lead in new[] { 22222, 12346 })
        {
            var response = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract", crmLeadId = lead }, NewKey());
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("RECORD_OWNERSHIP_MISMATCH", (await JsonAsync(response)).GetProperty("code").GetString());
        }

        Assert.Empty(_crm.DocumentCalls);
    }

    [Fact]
    public async Task ASessionBelongsToItsAccount_AndOnlyToIt()
    {
        var owner = await ClientAsync();
        var session = await VerifyCustomerAsync(owner);

        var response = await Post(await ClientAsync(), SendCopy, new { verificationSessionId = session, documentType = "Contract" }, NewKey());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("VERIFICATION_FAILED", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Empty(_crm.DocumentCalls);
    }

    [Fact]
    public async Task EveryNewEndpoint_RequiresAToken()
    {
        var anonymous = _factory.CreateClient();
        foreach (var route in new[] { "buyer-lookup", "otp/send", "otp/resend", "otp/verify" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, "/api/genesys/verification/" + route, new { })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, SendCopy, new { }, NewKey())).StatusCode);
    }

    // =====================================================================
    //  Download and CRM failures
    // =====================================================================

    [Theory]
    [InlineData(404, HttpStatusCode.NotFound, "DOCUMENT_NOT_FOUND")]
    [InlineData(401, HttpStatusCode.BadGateway, "CRM_AUTHENTICATION_FAILED")]
    [InlineData(403, HttpStatusCode.BadGateway, "CRM_ACCESS_DENIED")]
    [InlineData(500, HttpStatusCode.ServiceUnavailable, "DOCUMENT_SOURCE_UNAVAILABLE")]
    [InlineData(503, HttpStatusCode.ServiceUnavailable, "DOCUMENT_SOURCE_UNAVAILABLE")]
    public async Task ADownloadFailure_SendsNothing_AndARetryWithTheSameKeyDeliversOnceWhenCrmRecovers(int fileStatus, HttpStatusCode expected, string code)
    {
        var client = await ClientAsync();
        var session = await VerifyCustomerAsync(client);
        _crm.DocumentsResponder = Documents(false, ("5001", "Contract.pdf", "/Uploads/c.pdf"));
        _crm.FileResponder = _ => new HttpResponseMessage((HttpStatusCode)fileStatus);
        var key = NewKey();
        var before = MailCount;

        var failed = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract" }, key);

        Assert.Equal(expected, failed.StatusCode);
        Assert.Equal(code, (await JsonAsync(failed)).GetProperty("code").GetString());
        Assert.Equal(before, MailCount);

        _crm.FileResponder = _ => FileOk(PdfBytes);
        var retried = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract" }, key);
        Assert.Equal("Sent", (await JsonAsync(retried)).GetProperty("status").GetString());
        Assert.Single(Mailbox.Recorded.Skip(before));
    }

    [Fact]
    public async Task AnHtmlPage_AnUnknownFileType_AndAFileOnAnotherHost_AreNeverMailed()
    {
        var client = await ClientAsync();
        var session = await VerifyCustomerAsync(client);
        var before = MailCount;

        _crm.DocumentsResponder = Documents(false, ("5001", "Contract.pdf", "/Uploads/c.pdf"));
        _crm.FileResponder = _ => FileOk(Encoding.UTF8.GetBytes("<html>Login</html>"), "text/html");
        var html = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract" }, NewKey());
        Assert.Equal(HttpStatusCode.BadGateway, html.StatusCode);
        Assert.Equal("CRM_INVALID_RESPONSE", (await JsonAsync(html)).GetProperty("code").GetString());

        _crm.FileResponder = _ => FileOk([0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0], "application/pdf"); // an executable claiming to be a PDF
        var exe = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract" }, NewKey());
        Assert.Equal(HttpStatusCode.BadGateway, exe.StatusCode);

        _crm.DocumentsResponder = Documents(false, ("5001", "Contract.pdf", "https://evil.example.net/steal.pdf"));
        var external = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract" }, NewKey());
        Assert.Equal(HttpStatusCode.BadGateway, external.StatusCode);
        Assert.DoesNotContain(_crm.Requests, r => r.Url.Contains("evil.example.net", StringComparison.Ordinal));

        Assert.Equal(before, MailCount);
    }

    [Fact]
    public async Task ACrmDocumentAnswerForAnotherCustomer_IsRefused()
    {
        var client = await ClientAsync();
        var session = await VerifyCustomerAsync(client);
        _crm.DocumentsResponder = _ => Json(HttpStatusCode.OK, """{ "customerId": 9002, "leadId": 12345, "count": 1, "selectionRequired": false, "attachments": [ { "attachmentId": 1, "fileUrl": "/u/1.pdf", "name": "x.pdf" } ] }""");
        var before = MailCount;

        var response = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract" }, NewKey());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("CRM_INVALID_RESPONSE", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(before, MailCount);
    }

    [Theory]
    [InlineData(400, HttpStatusCode.BadGateway, "CRM_REQUEST_REJECTED")]
    [InlineData(401, HttpStatusCode.BadGateway, "CRM_AUTHENTICATION_FAILED")]
    [InlineData(403, HttpStatusCode.BadGateway, "CRM_ACCESS_DENIED")]
    [InlineData(404, HttpStatusCode.NotFound, "DOCUMENT_NOT_FOUND")]
    [InlineData(500, HttpStatusCode.ServiceUnavailable, "DOCUMENT_SOURCE_UNAVAILABLE")]
    [InlineData(503, HttpStatusCode.ServiceUnavailable, "DOCUMENT_SOURCE_UNAVAILABLE")]
    public async Task EveryCrmListingStatus_IsItsOwnAnswer(int crmStatus, HttpStatusCode expected, string code)
    {
        var client = await ClientAsync();
        var session = await VerifyCustomerAsync(client);
        _crm.DocumentsResponder = _ => new HttpResponseMessage((HttpStatusCode)crmStatus);
        var before = MailCount;

        var response = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract" }, NewKey());

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(before, MailCount);
    }

    [Fact]
    public async Task WhatsApp_Is501_NamingTheMissingIntegration()
    {
        var client = await ClientAsync();
        var session = await VerifyCustomerAsync(client);

        var response = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "UnitLayout", deliveryChannel = "WhatsApp" }, NewKey());

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal("DELIVERY_CHANNEL_NOT_INTEGRATED", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Empty(_crm.DocumentCalls);
    }

    [Fact]
    public async Task ABlankCrmLeadId_FromAGenesysDataAction_IsTreatedAsNotSupplied()
    {
        var client = await ClientAsync();
        var session = await VerifyCustomerAsync(client);
        _crm.DocumentsResponder = Documents(false, ("5001", "C.pdf", "/Uploads/c.pdf"));

        var blank = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract", crmUnitId = "", recordId = "", crmLeadId = "", deliveryChannel = "" }, NewKey());
        Assert.Equal(HttpStatusCode.OK, blank.StatusCode);

        var garbage = await Post(client, SendCopy, new { verificationSessionId = session, documentType = "Contract", crmLeadId = "abc" }, NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
    }
}

/// <summary>The shipped switches.</summary>
public sealed class DocumentCopyDefaultsTests
{
    [Fact]
    public void TheFeatureIsOffUntilConfigured_NotAllowedInProduction_AndAcceptsOnlyOtp()
    {
        var options = new TigerCS.Application.Modules.CrmDocuments.CrmDocumentOptions();

        Assert.False(options.Enabled);
        Assert.False(options.AllowInProduction);
        Assert.True(options.IsAccepted(TigerCS.Domain.Modules.CustomerVerification.VerificationMethod.Otp));
        Assert.True(options.IsAccepted(TigerCS.Domain.Modules.CustomerVerification.VerificationMethod.AuthenticatedDigitalUser));
        Assert.False(options.IsAccepted(TigerCS.Domain.Modules.CustomerVerification.VerificationMethod.ManualAgentConfirmation));
        Assert.False(options.IsAccepted(null));
        Assert.Equal((10, 5, 3, 60, 5), (options.OtpLifetimeMinutes, options.OtpMaxAttempts, options.OtpMaxSendsPerChallenge, options.OtpMinResendSeconds, options.OtpMaxChallengesPerCustomerPerHour));
    }
}
