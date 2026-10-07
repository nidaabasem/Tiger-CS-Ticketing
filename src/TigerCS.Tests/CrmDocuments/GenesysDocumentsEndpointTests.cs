using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Integrations.Modules.EmailIntegration;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// <c>POST /api/genesys/documents/send-copy</c> through the real host: the
/// real authentication, the real verification-session endpoints, the Mock CRM
/// document gateway, and the in-memory recording email adapter — so what is
/// asserted is what a Genesys data action would actually see on the wire.
/// </summary>
public sealed class GenesysDocumentsEndpointTests : IClassFixture<TigerCsApiFactory>
{
    private const string Route = "/api/genesys/documents/send-copy";
    private readonly TigerCsApiFactory _factory;

    public GenesysDocumentsEndpointTests(TigerCsApiFactory factory)
    {
        _factory = new TigerCsApiFactory { ExtraConfiguration = { ["CrmDocuments:Enabled"] = "true" } };
        _ = factory;
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

    /// <summary>The existing verification flow: look the unit and its contacts up, then confirm a session.</summary>
    private static async Task<Guid> VerifyAsync(HttpClient client, string crmUnitId, string crmContactId, string method = "Otp")
    {
        var unit = await (await client.GetAsync($"/api/crm/units/{crmUnitId}")).Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await client.GetAsync($"/api/crm/units/{crmUnitId}/contacts")).Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();
        var contact = contacts!.Single(c => c.CrmContactId == crmContactId);

        var created = await client.PostAsJsonAsync(
            "/api/verification-sessions",
            new CreateVerificationSessionRequestDto(unit!.UnitReferenceId, contact.ContactReferenceId, true, method));
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

    [Fact]
    public async Task WithoutAToken_Is401()
    {
        var response = await PostAsync(_factory.CreateClient(), new { verificationSessionId = Guid.NewGuid(), documentType = "Contract" }, NewKey());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReservationForm_IsSentOnce_AndAReplayIsADuplicate()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client, "CRM-UNIT-1001", "CRM-CONTACT-2001");
        var key = NewKey();
        var before = Mailbox.Recorded.Count;

        var first = await PostAsync(client, new { verificationSessionId = session, documentType = "ReservationForm" }, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var sent = await JsonAsync(first);
        Assert.Equal("Sent", sent.GetProperty("status").GetString());
        Assert.Equal("MOCK-RESERVATION-1", sent.GetProperty("recordId").GetString());
        Assert.Equal("a***@e***.com", sent.GetProperty("maskedDestination").GetString());
        Assert.False(sent.GetProperty("duplicate").GetBoolean());

        var replay = await PostAsync(client, new { verificationSessionId = session, documentType = "ReservationForm" }, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var again = await JsonAsync(replay);
        Assert.True(again.GetProperty("duplicate").GetBoolean());
        Assert.Equal(sent.GetProperty("deliveryRequestId").GetInt64(), again.GetProperty("deliveryRequestId").GetInt64());

        var mail = Assert.Single(Mailbox.Recorded.Skip(before));
        Assert.Equal("ahmed.alfarsi@example.com", mail.ToAddress);
        Assert.Equal(["MOCK-RESERVATION-1.txt"], mail.AttachmentFileNames);
    }

    [Fact]
    public async Task Contract_WithTwoMatches_AsksForASelection_ThenSendsTheChosenOne()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client, "CRM-UNIT-1001", "CRM-CONTACT-2001");
        var before = Mailbox.Recorded.Count;

        var ask = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract" }, NewKey());
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        var choices = await JsonAsync(ask);
        Assert.Equal("SelectionRequired", choices.GetProperty("status").GetString());
        Assert.Equal(
            new[] { "MOCK-CONTRACT-1", "MOCK-CONTRACT-2" },
            choices.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("recordId").GetString()!).ToArray());
        Assert.Equal(before, Mailbox.Recorded.Count); // nothing arbitrary was sent

        var chosen = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract", recordId = "MOCK-CONTRACT-2" }, NewKey());
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Equal("MOCK-CONTRACT-2", (await JsonAsync(chosen)).GetProperty("recordId").GetString());
        Assert.Equal(["MOCK-CONTRACT-2.txt"], Assert.Single(Mailbox.Recorded.Skip(before)).AttachmentFileNames);
    }

    [Fact]
    public async Task UnitLayout_IsSent()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client, "CRM-UNIT-1001", "CRM-CONTACT-2001");

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "UnitLayout" }, NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("MOCK-LAYOUT-1", (await JsonAsync(response)).GetProperty("recordId").GetString());
    }

    [Fact]
    public async Task RegistrationReceipt_IsSentForTheCustomerWhoHasOne()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client, "CRM-UNIT-1002", "CRM-CONTACT-2003");

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "RegistrationReceipt" }, NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("MOCK-RECEIPT-1", (await JsonAsync(response)).GetProperty("recordId").GetString());
    }

    [Fact]
    public async Task MissingDocument_Is404_WithTheCode()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client, "CRM-UNIT-1001", "CRM-CONTACT-2001"); // has no registration receipt

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "RegistrationReceipt" }, NewKey());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal("DOCUMENT_NOT_FOUND", problem.GetProperty("code").GetString());
        Assert.Equal("DocumentUnavailable", problem.GetProperty("outcome").GetString());
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task AnotherCustomersRecord_Is403_OwnershipMismatch_AndNothingIsSent()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client, "CRM-UNIT-1001", "CRM-CONTACT-2001");
        var before = Mailbox.Recorded.Count;

        // MOCK-CONTRACT-3 is the contract of unit 1002's owner.
        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract", recordId = "MOCK-CONTRACT-3" }, NewKey());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("RECORD_OWNERSHIP_MISMATCH", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(before, Mailbox.Recorded.Count);
    }

    [Fact]
    public async Task AnotherUnit_Is403_OwnershipMismatch()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client, "CRM-UNIT-1001", "CRM-CONTACT-2001");

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "Contract", crmUnitId = "CRM-UNIT-1002" }, NewKey());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("RECORD_OWNERSHIP_MISMATCH", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AWeakVerificationMethod_Is403_VerificationFailed()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client, "CRM-UNIT-1001", "CRM-CONTACT-2001", method: "ManualAgentConfirmation");

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "UnitLayout" }, NewKey());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("VERIFICATION_FAILED", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task SomeoneElsesSession_Is403_VerificationFailed()
    {
        var owner = await ClientAsync();
        var session = await VerifyAsync(owner, "CRM-UNIT-1001", "CRM-CONTACT-2001");
        var intruder = await ClientAsync();

        var response = await PostAsync(intruder, new { verificationSessionId = session, documentType = "UnitLayout" }, NewKey());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("VERIFICATION_FAILED", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task OnlyAPhoneNumberOrCustomerId_IsNotAnIdentity()
    {
        var client = await ClientAsync();

        var response = await PostAsync(client, new { documentType = "Contract", customerPhone = "+971500000001", crmCustomerId = "CRM-CUST-5001" }, NewKey());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_REQUEST", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingIdempotencyKey_Is400()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client, "CRM-UNIT-1001", "CRM-CONTACT-2001");

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "UnitLayout" }, key: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheSameKeyForADifferentDocument_Is409()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client, "CRM-UNIT-1001", "CRM-CONTACT-2001");
        var key = NewKey();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, new { verificationSessionId = session, documentType = "UnitLayout" }, key)).StatusCode);

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "ReservationForm" }, key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task WhatsApp_Is501_NamingTheMissingIntegration()
    {
        var client = await ClientAsync();
        var session = await VerifyAsync(client, "CRM-UNIT-1001", "CRM-CONTACT-2001");

        var response = await PostAsync(client, new { verificationSessionId = session, documentType = "UnitLayout", deliveryChannel = "WhatsApp" }, NewKey());

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal("DELIVERY_CHANNEL_NOT_INTEGRATED", problem.GetProperty("code").GetString());
        Assert.Contains("WhatsApp", problem.GetProperty("detail").GetString());
    }
}

/// <summary>The shipped switches: dark by default, and fail-closed against real Tiger CRM.</summary>
public sealed class DocumentCopyDefaultsTests
{
    [Fact]
    public async Task Http_Provider_HasNoDocumentSource_AndSaysSo()
    {
        var gateway = new TigerCS.Integrations.Modules.CrmIntegration.UnimplementedCrmDocumentGateway(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TigerCS.Integrations.Modules.CrmIntegration.UnimplementedCrmDocumentGateway>.Instance);

        await Assert.ThrowsAsync<TigerCS.Application.Modules.CrmDocuments.Abstractions.CrmDocumentSourceUnavailableException>(
            () => gateway.ListAsync(TigerCS.Domain.Modules.CustomerVerification.CrmDocumentType.Contract, "U", "C"));
        await Assert.ThrowsAsync<TigerCS.Application.Modules.CrmDocuments.Abstractions.CrmDocumentSourceUnavailableException>(
            () => gateway.GetContentAsync(TigerCS.Domain.Modules.CustomerVerification.CrmDocumentType.Contract, "R"));
    }

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
