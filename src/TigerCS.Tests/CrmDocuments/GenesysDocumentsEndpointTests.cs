using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
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
/// <c>POST /api/genesys/documents/send-copy</c> through the real host — real
/// authentication, real verification-session endpoints, the real buyer-lookup
/// application service (over its scripted CRM double), the real
/// <see cref="CrmDocumentHttpGateway"/> talking HTTP to a stub Tiger CRM that
/// implements <c>TicketingSystem/GetCustomerDocuments</c> and a storage
/// endpoint, and the in-memory recording email adapter. What is asserted is
/// both what Genesys sees on the wire and what Tiger CRM would see.
/// </summary>
public sealed class GenesysDocumentsEndpointTests
{
    private const string Route = "/api/genesys/documents/send-copy";
    private const string CrmBase = "https://crm.uat.example.test:8014/";
    private const string SecretKey = "uat-like-secret";

    /// <summary>A stub Tiger CRM: records every request it gets, answers as scripted.</summary>
    private sealed class StubCrm
    {
        public List<(HttpMethod Method, string Url, string? Secret, string? Body)> Requests { get; } = [];
        public Func<string, HttpResponseMessage>? ListResponder { get; set; }
        public HttpStatusCode FileStatus { get; set; } = HttpStatusCode.OK;

        public HttpMessageHandler Handler => new StubHttpMessageHandler(async (request, _) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            Requests.Add((request.Method, request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-SECRET-KEY", out var v) ? v.Single() : null, body));

            if (request.RequestUri!.AbsolutePath.EndsWith("/TicketingSystem/GetCustomerDocuments", StringComparison.Ordinal))
            {
                return ListResponder!(body!);
            }

            // Storage reference.
            return FileStatus == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-fake")) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } } }
                : new HttpResponseMessage(FileStatus);
        });
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>CRM's answer for one customer/lead: attachments given as (attachmentId, name, fileUrl).</summary>
    private static string CrmAnswer(int customerId, int leadId, bool selectionRequired, params (string? Id, string Name, string Url)[] attachments) =>
        JsonSerializer.Serialize(new
        {
            customerId, leadId, count = attachments.Length, selectionRequired,
            attachments = attachments.Select(a => a.Id is null
                ? (object)new { fileUrl = a.Url, name = a.Name }
                : new { attachmentId = a.Id, fileUrl = a.Url, name = a.Name }).ToArray()
        });

    private readonly StubCrm _crm = new();
    private readonly TigerCsApiFactory _factory;

    public GenesysDocumentsEndpointTests()
    {
        _factory = new TigerCsApiFactory
        {
            ExtraConfiguration =
            {
                ["CrmDocuments:Enabled"] = "true",
                ["Crm:BaseUrl"] = CrmBase,
                ["Crm:SecretKey"] = SecretKey
            },
            ExtraServices = services =>
            {
                // The real CRM document gateway, over a stub CRM instead of the network.
                services.AddHttpClient<CrmDocumentHttpGateway>(client => client.BaseAddress = new Uri(CrmBase))
                    .ConfigurePrimaryHttpMessageHandler(() => _crm.Handler);
                services.RemoveAll<ICrmDocumentGateway>();
                services.AddScoped<ICrmDocumentGateway>(sp => sp.GetRequiredService<CrmDocumentHttpGateway>());
            }
        };

        // The verified customer in CRM: customer 9001, owner of the Mock unit 1205 (lead 12345) and 1403 (lead 12346).
        _factory.Services.GetRequiredService<FakeCrmBuyerLookupGateway>().Returns(CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(
                new CrmCustomerDto(9001, "Ahmed Ali", null, "+971501234567", "ahmed.ali@example.test"),
                [
                    new CrmBuyerUnitDto(12345, 8, "Sold", 1101, "1205", 3, 2, 12, 79, "Tiger Sky Tower", null, 1, "Buyer"),
                    new CrmBuyerUnitDto(12346, 8, "Sold", 1102, "1403", 3, 2, 14, 79, "Tiger Sky Tower", null, 1, "Buyer")
                ])
        ]));
    }

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

    /// <summary>The existing verification flow on Mock unit 1101 (1205, Tiger Sky Tower), whose owner contact has the phone the CRM buyer is found by.</summary>
    private static async Task<Guid> VerifyAsync(HttpClient client, string method = "Otp")
    {
        var unit = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1101")).Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1101/contacts")).Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();
        var created = await client.PostAsJsonAsync(
            "/api/verification-sessions",
            new CreateVerificationSessionRequestDto(unit!.UnitReferenceId, contacts!.Single().ContactReferenceId, true, method));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<VerificationSessionResponseDto>())!.VerificationSessionId;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, object body, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request);
    }

    private static string NewKey() => "it-" + Guid.NewGuid().ToString("N");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private RecordingEmailSender Mailbox => _factory.Services.GetRequiredService<RecordingEmailSender>();

    // ---------------------------------------------------------------------

    [Fact]
    public async Task WithoutAToken_Is401()
    {
        var response = await PostAsync(_factory.CreateClient(), new { verificationSessionId = Guid.NewGuid(), documentType = "Contract" }, NewKey());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EndToEnd_Contract_IsRequestedFromCrmWithTheRightIds_FetchedWithTheSecret_AndEmailedOnce()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client);
        _crm.ListResponder = _ => Json(CrmAnswer(9001, 12345, false, ("5001", "Sale and Purchase Agreement.pdf", "/Uploads/Contracts/5001.pdf")));
        var before = Mailbox.Recorded.Count;
        var key = NewKey();

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract" }, key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = await JsonAsync(response);
        Assert.Equal("Sent", sent.GetProperty("status").GetString());
        Assert.Equal("5001", sent.GetProperty("recordId").GetString());
        Assert.False(sent.GetProperty("duplicate").GetBoolean());
        Assert.DoesNotContain("/Uploads/", await response.Content.ReadAsStringAsync()); // the storage reference is never exposed

        // What Tiger CRM saw: POST, the secret, PascalCase body with the public type mapped to CRM's TigerContract.
        var list = _crm.Requests[0];
        Assert.Equal(HttpMethod.Post, list.Method);
        Assert.Equal("https://crm.uat.example.test:8014/TicketingSystem/GetCustomerDocuments", list.Url);
        Assert.Equal(SecretKey, list.Secret);
        using (var body = JsonDocument.Parse(list.Body!))
        {
            Assert.Equal(9001, body.RootElement.GetProperty("CustomerID").GetInt32());
            Assert.Equal(12345, body.RootElement.GetProperty("LeadID").GetInt32());
            Assert.Equal("TigerContract", body.RootElement.GetProperty("DocumentType").GetString());
        }

        // The file was fetched server-side from the configured origin, with the credential.
        var file = _crm.Requests[1];
        Assert.Equal(HttpMethod.Get, file.Method);
        Assert.Equal("https://crm.uat.example.test:8014/Uploads/Contracts/5001.pdf", file.Url);
        Assert.Equal(SecretKey, file.Secret);

        var mail = Assert.Single(Mailbox.Recorded.Skip(before));
        Assert.Equal("ahmed.ali@example.test", mail.ToAddress);
        Assert.Equal(["Sale and Purchase Agreement.pdf"], mail.AttachmentFileNames);

        // A Genesys retry with the same key: answered from the record, no new CRM call, no second email.
        var calls = _crm.Requests.Count;
        var replay = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract" }, key);
        Assert.True((await JsonAsync(replay)).GetProperty("duplicate").GetBoolean());
        Assert.Equal(calls, _crm.Requests.Count);
        Assert.Single(Mailbox.Recorded.Skip(before));
    }

    [Theory]
    [InlineData("ReservationForm", "ReservationForm")]
    [InlineData("UnitLayout", "Layout")]
    [InlineData("RegistrationReceipt", "RegistrationReceipt")]
    public async Task OtherPublicTypes_AreMappedToCrmValues(string publicType, string crmType)
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client);
        // Layout has no attachmentId; the others do.
        _crm.ListResponder = _ => Json(CrmAnswer(9001, 12345, false, (publicType == "UnitLayout" ? null : "4001", "Doc.pdf", "files/doc.pdf")));

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = publicType }, NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = await JsonAsync(response);
        Assert.Equal("Sent", sent.GetProperty("status").GetString());
        Assert.Equal(publicType, sent.GetProperty("documentType").GetString());
        if (publicType == "UnitLayout")
        {
            Assert.Equal("LAYOUT-12345", sent.GetProperty("recordId").GetString());
        }

        using var body = JsonDocument.Parse(_crm.Requests[0].Body!);
        Assert.Equal(crmType, body.RootElement.GetProperty("DocumentType").GetString());
    }

    [Fact]
    public async Task SelectionRequired_ReturnsTheDocumentChoices_FetchesNothing_ThenSendsTheChosenOne()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client);
        _crm.ListResponder = _ => Json(CrmAnswer(9001, 12345, true,
            ("5001", "Sale and Purchase Agreement.pdf", "files/5001.pdf"), ("5002", "Addendum 1.pdf", "files/5002.pdf")));
        var before = Mailbox.Recorded.Count;

        var ask = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract" }, NewKey());

        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        var choices = await JsonAsync(ask);
        Assert.Equal("SelectionRequired", choices.GetProperty("status").GetString());
        Assert.Equal("Document", choices.GetProperty("choiceKind").GetString());
        Assert.Equal(
            new[] { "5001", "5002" },
            choices.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("recordId").GetString()!).ToArray());
        Assert.DoesNotContain("files/5001", await ask.Content.ReadAsStringAsync());
        Assert.Single(_crm.Requests); // listed, nothing downloaded
        Assert.Equal(before, Mailbox.Recorded.Count);

        var chosen = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract", recordId = "5002" }, NewKey());
        Assert.Equal("5002", (await JsonAsync(chosen)).GetProperty("recordId").GetString());
        Assert.Equal(["Addendum 1.pdf"], Assert.Single(Mailbox.Recorded.Skip(before)).AttachmentFileNames);
        Assert.EndsWith("files/5002.pdf", _crm.Requests.Last().Url);
    }

    [Fact]
    public async Task ARecordIdCrmDidNotListForThisCustomer_Is403_AndNeverFetched()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client);
        _crm.ListResponder = _ => Json(CrmAnswer(9001, 12345, false, ("5001", "Mine.pdf", "files/5001.pdf")));

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract", recordId = "5999" }, NewKey());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("RECORD_OWNERSHIP_MISMATCH", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Single(_crm.Requests); // listing only
    }

    [Fact]
    public async Task ALeadOfAnotherCustomer_Is403_AndCrmIsNeverCalled()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client);

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract", crmLeadId = 22222 }, NewKey());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("RECORD_OWNERSHIP_MISMATCH", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Empty(_crm.Requests);
    }

    [Fact]
    public async Task ABlankOrNumericStringCrmLeadId_IsAccepted_LikeEveryOtherBlankOptionalField()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client);
        _crm.ListResponder = _ => Json(CrmAnswer(9001, 12346, false, ("5003", "Mine.pdf", "files/5003.pdf")));

        // What a Genesys data action sends: every optional field present, unset ones blank, ids as strings.
        var blank = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract", crmUnitId = "", recordId = "", crmLeadId = "", deliveryChannel = "" }, NewKey());
        Assert.Equal(12345, JsonDocument.Parse(_crm.Requests[0].Body!).RootElement.GetProperty("LeadID").GetInt32());
        Assert.Equal(HttpStatusCode.BadGateway, blank.StatusCode); // the stub answered lead 12346, not the 12345 asked: refused, not trusted

        _crm.Requests.Clear();
        var chosen = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract", crmLeadId = "12346" }, NewKey());
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Equal(12346, JsonDocument.Parse(_crm.Requests[0].Body!).RootElement.GetProperty("LeadID").GetInt32());

        var garbage = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract", crmLeadId = "abc" }, NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    [Fact]
    public async Task ACrmAnswerForAnotherCustomer_IsRefused_AsAnInvalidResponse()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client);
        // CRM (wrongly) answers for customer 9002.
        _crm.ListResponder = _ => Json(CrmAnswer(9002, 12345, false, ("5999", "Theirs.pdf", "files/5999.pdf")));
        var before = Mailbox.Recorded.Count;

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract" }, NewKey());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("CRM_INVALID_RESPONSE", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(before, Mailbox.Recorded.Count);
    }

    [Fact]
    public async Task AFileOnAnotherHost_IsNeverFetched_AndTheSecretIsNotSentThere()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client);
        _crm.ListResponder = _ => Json(CrmAnswer(9001, 12345, false, ("5001", "Mine.pdf", "https://evil.example.net/steal.pdf")));
        var before = Mailbox.Recorded.Count;

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract" }, NewKey());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("CRM_INVALID_RESPONSE", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.DoesNotContain(_crm.Requests, r => r.Url.Contains("evil.example.net", StringComparison.Ordinal));
        Assert.Equal(before, Mailbox.Recorded.Count);
    }

    [Fact]
    public async Task NoDocumentsOnRecord_IsA404_FromCrmNoContentOr404()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client);

        _crm.ListResponder = _ => Json("", HttpStatusCode.NotFound);
        var notFound = await PostAsync(client, new { verificationSessionId = session, documentType = "RegistrationReceipt" }, NewKey());
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("DOCUMENT_NOT_FOUND", (await JsonAsync(notFound)).GetProperty("code").GetString());

        _crm.ListResponder = _ => Json(CrmAnswer(9001, 12345, false));
        var empty = await PostAsync(client, new { verificationSessionId = session, documentType = "RegistrationReceipt" }, NewKey());
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
    }

    [Theory]
    [InlineData(400, HttpStatusCode.BadGateway, "CRM_REQUEST_REJECTED", false)]
    [InlineData(401, HttpStatusCode.BadGateway, "CRM_AUTHENTICATION_FAILED", false)]
    [InlineData(403, HttpStatusCode.BadGateway, "CRM_ACCESS_DENIED", false)]
    [InlineData(500, HttpStatusCode.ServiceUnavailable, "DOCUMENT_SOURCE_UNAVAILABLE", true)]
    [InlineData(503, HttpStatusCode.ServiceUnavailable, "DOCUMENT_SOURCE_UNAVAILABLE", true)]
    public async Task EveryCrmErrorStatus_IsItsOwnAnswer_AndSendsNothing(int crmStatus, HttpStatusCode expected, string code, bool retryable)
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client);
        _crm.ListResponder = _ => new HttpResponseMessage((HttpStatusCode)crmStatus);
        var before = Mailbox.Recorded.Count;

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract" }, NewKey());

        Assert.Equal(expected, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(retryable, problem.GetProperty("retryable").GetBoolean());
        Assert.Equal(before, Mailbox.Recorded.Count);
    }

    [Fact]
    public async Task AWeakVerificationMethod_Is403_AndCrmIsNeverCalled()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client, method: "ManualAgentConfirmation");

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "UnitLayout" }, NewKey());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("VERIFICATION_FAILED", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Empty(_crm.Requests);
    }

    [Fact]
    public async Task SomeoneElsesSession_Is403_VerificationFailed()
    {
        var session = await VerifyAsync(await ClientAsync());

        var response = await PostAsync(await ClientAsync(), new { verificationSessionId = session, documentType = "UnitLayout" }, NewKey());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("VERIFICATION_FAILED", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Empty(_crm.Requests);
    }

    [Fact]
    public async Task OnlyAPhoneNumberOrCustomerId_IsNotAnIdentity()
    {
        var response = await PostAsync(await ClientAsync(), new { documentType = "Contract", customerPhone = "+971501234567", customerId = 9001, leadId = 12345 }, NewKey());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_REQUEST", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Empty(_crm.Requests);
    }

    [Fact]
    public async Task MissingIdempotencyKey_Is400()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, new { verificationSessionId = session, documentType = "UnitLayout" }, key: null)).StatusCode);
    }

    [Fact]
    public async Task WhatsApp_Is501_NamingTheMissingIntegration()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client);

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "UnitLayout", deliveryChannel = "WhatsApp" }, NewKey());

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal("DELIVERY_CHANNEL_NOT_INTEGRATED", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Empty(_crm.Requests);
    }
}

/// <summary>The shipped switches.</summary>
public sealed class DocumentCopyDefaultsTests
{
    [Fact]
    public void TheFeatureIsOffUntilConfigured_AndAcceptsOnlyStrongVerification()
    {
        var options = new TigerCS.Application.Modules.CrmDocuments.CrmDocumentOptions();

        Assert.False(options.Enabled);
        Assert.True(options.IsAccepted(TigerCS.Domain.Modules.CustomerVerification.VerificationMethod.Otp));
        Assert.True(options.IsAccepted(TigerCS.Domain.Modules.CustomerVerification.VerificationMethod.AuthenticatedDigitalUser));
        Assert.False(options.IsAccepted(TigerCS.Domain.Modules.CustomerVerification.VerificationMethod.ManualAgentConfirmation));
        Assert.False(options.IsAccepted(null));
    }
}
