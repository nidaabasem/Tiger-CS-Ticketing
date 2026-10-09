using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.CustomerVerification.Otp;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Integrations.Modules.SmsIntegration;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.GenesysIntegration.Integration;

/// <summary>
/// <c>api/genesys/verification/otp/{send,resend,verify}</c> through the real host, CRM answered by the host's double
/// (customer 9001 owns unit 9200 through lead 9100) and the SMS provider by <see cref="FakeSmsSender"/>.
/// <b>Local verification only: no SMS is sent and Broadnet is not contacted.</b>
/// </summary>
public sealed class GenesysOtpEndpointTests : IDisposable
{
    private const string Send = "/api/genesys/verification/otp/send";
    private const string Resend = "/api/genesys/verification/otp/resend";
    private const string Verify = "/api/genesys/verification/otp/verify";
    private const string UnitDetails = "/api/genesys/customers/unit-details";
    private const string Phone = "tel:+971500000900";

    private readonly TigerCsApiFactory _enabled = new()
    {
        ExtraConfiguration = new()
        {
            ["Otp:Enabled"] = "true",
            ["Otp:Pepper"] = "endpoint-test-pepper-not-a-secret",
            ["Otp:ResendCooldownSeconds"] = "0",
            ["Sms:Provider"] = "Fake"
        }
    };

    private readonly TigerCsApiFactory _shipped = new();   // shipped defaults: Otp:Enabled=false, Sms:Provider=Disabled

    public void Dispose()
    {
        _enabled.Dispose();
        _shipped.Dispose();
    }

    private static async Task<HttpClient> SignInAsync(TigerCsApiFactory factory, string role = Roles.CsAgent)
    {
        var (username, password, _) = await factory.SeedEmployeeAsync(role);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private FakeSmsSender Sms => _enabled.Services.GetRequiredService<FakeSmsSender>();

    private static object SendBody(int unitId = 9200, string customer = "crm:9001") =>
        new { customerReference = customer, phoneNumber = Phone, unitId };

    [Fact]
    public async Task SendVerifyThenUnitDetails_TheCodeUnlocksTheSale_EndToEnd()
    {
        var client = await SignInAsync(_enabled);

        var sent = await client.PostAsJsonAsync(Send, SendBody());
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("no-store", sent.Headers.CacheControl?.ToString());
        var sentJson = await Json(sent);
        Assert.Equal("Sent", sentJson.GetProperty("status").GetString());
        Assert.Equal("Sms", sentJson.GetProperty("channel").GetString());
        Assert.Equal("+971******900", sentJson.GetProperty("maskedDestination").GetString());
        var code = Sms.LastCode!;
        Assert.DoesNotContain(code, sentJson.GetRawText());
        var challenge = sentJson.GetProperty("otpChallengeId").GetGuid();

        var wrong = await client.PostAsJsonAsync(Verify, new { otpChallengeId = challenge, code = code == "000000" ? "111111" : "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var wrongJson = await Json(wrong);
        Assert.Equal("OTP_INVALID_CODE", wrongJson.GetProperty("code").GetString());
        Assert.Equal(4, wrongJson.GetProperty("attemptsRemaining").GetInt32());

        var verified = await client.PostAsJsonAsync(Verify, new { otpChallengeId = challenge, code });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var verifiedJson = await Json(verified);
        Assert.Equal("Verified", verifiedJson.GetProperty("status").GetString());
        Assert.Equal(9200, verifiedJson.GetProperty("unitId").GetInt32());
        var session = verifiedJson.GetProperty("verificationSessionId").GetGuid();

        // The proof is the ordinary VerificationSession: unit-details releases the sale with it...
        var details = await Json(await client.PostAsJsonAsync(UnitDetails,
            new GenesysCustomerUnitDetailsRequest("crm:9001", Phone, 9200, session)));
        Assert.Equal("Available", details.GetProperty("financialDetailsStatus").GetString());
        Assert.Equal(1850000m, details.GetProperty("sale").GetProperty("soldPrice").GetProperty("amount").GetDecimal());

        // ...and withholds it again without it.
        var without = await Json(await client.PostAsJsonAsync(UnitDetails, new GenesysCustomerUnitDetailsRequest("crm:9001", Phone, 9200)));
        Assert.Equal("VerificationRequired", without.GetProperty("financialDetailsStatus").GetString());
    }

    [Fact]
    public async Task AnUnconfirmedSend_Is504_WithTheChallenge_AndNothingIsResent()
    {
        var client = await SignInAsync(_enabled);
        var before = Sms.Sent.Count;
        Sms.Then(SmsSendOutcome.Unconfirmed);

        var response = await client.PostAsJsonAsync(Send, SendBody());

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("OTP_DELIVERY_UNCONFIRMED", body.GetProperty("code").GetString());
        Assert.NotEqual("Sent", body.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.String, body.GetProperty("otpChallengeId").ValueKind);
        Assert.Equal(before + 1, Sms.Sent.Count);
    }

    [Theory]
    [InlineData(SmsSendOutcome.Rejected, HttpStatusCode.BadGateway, "OTP_DELIVERY_REJECTED")]
    [InlineData(SmsSendOutcome.Failed, HttpStatusCode.BadGateway, "OTP_DELIVERY_FAILED")]
    public async Task RejectedOrFailedDelivery_IsNeverASuccess(SmsSendOutcome outcome, HttpStatusCode status, string code)
    {
        var client = await SignInAsync(_enabled);
        Sms.Then(outcome);

        var response = await client.PostAsJsonAsync(Send, SendBody());

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnotherCustomersUnit_OrAWrongCustomer_Gets403_AndNoSms()
    {
        var client = await SignInAsync(_enabled);
        var before = Sms.Sent.Count;

        var unit = await client.PostAsJsonAsync(Send, SendBody(unitId: 5555));
        var customer = await client.PostAsJsonAsync(Send, SendBody(customer: "crm:1234"));

        Assert.Equal(HttpStatusCode.Forbidden, unit.StatusCode);
        Assert.Equal(GenesysController.ErrorCodes.UnitNotEligible, (await Json(unit)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, customer.StatusCode);
        Assert.Equal(GenesysController.ErrorCodes.CustomerNotVerified, (await Json(customer)).GetProperty("code").GetString());
        Assert.Equal(before, Sms.Sent.Count);
    }

    [Fact]
    public async Task AnotherServiceAccount_CannotVerifyOrResendSomeoneElsesChallenge()
    {
        var owner = await SignInAsync(_enabled);
        var intruder = await SignInAsync(_enabled);
        var challenge = (await Json(await owner.PostAsJsonAsync(Send, SendBody()))).GetProperty("otpChallengeId").GetGuid();
        var code = Sms.LastCode;

        var verify = await intruder.PostAsJsonAsync(Verify, new { otpChallengeId = challenge, code });
        var resend = await intruder.PostAsJsonAsync(Resend, new { otpChallengeId = challenge });

        Assert.Equal(HttpStatusCode.NotFound, verify.StatusCode);
        Assert.Equal("OTP_CHALLENGE_NOT_FOUND", (await Json(verify)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, resend.StatusCode);
    }

    [Fact]
    public async Task Resend_ReplacesTheCode()
    {
        var client = await SignInAsync(_enabled);
        var challenge = (await Json(await client.PostAsJsonAsync(Send, SendBody()))).GetProperty("otpChallengeId").GetGuid();
        var sentBefore = Sms.Sent.Count;

        var resent = await client.PostAsJsonAsync(Resend, new { otpChallengeId = challenge });

        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        Assert.Equal(sentBefore + 1, Sms.Sent.Count);
        var ok = await client.PostAsJsonAsync(Verify, new { otpChallengeId = challenge, code = Sms.LastCode });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task WrongCodesLockTheChallenge_With429()
    {
        var client = await SignInAsync(_enabled);
        var challenge = (await Json(await client.PostAsJsonAsync(Send, SendBody()))).GetProperty("otpChallengeId").GetGuid();
        var wrong = Sms.LastCode == "000000" ? "111111" : "000000";

        HttpResponseMessage last = null!;
        for (var i = 0; i < 5; i++)
        {
            last = await client.PostAsJsonAsync(Verify, new { otpChallengeId = challenge, code = wrong });
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.Equal("OTP_LOCKED", (await Json(last)).GetProperty("code").GetString());
        var right = await client.PostAsJsonAsync(Verify, new { otpChallengeId = challenge, code = Sms.LastCode });
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
    }

    [Fact]
    public async Task ShippedDefaults_AreSwitchedOff_503_AndNothingIsCreated()
    {
        var client = await SignInAsync(_shipped);

        foreach (var (route, body) in new (string, object)[]
        {
            (Send, SendBody()),
            (Resend, new { otpChallengeId = Guid.NewGuid() }),
            (Verify, new { otpChallengeId = Guid.NewGuid(), code = "123456" })
        })
        {
            var response = await client.PostAsJsonAsync(route, body);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("OTP_DISABLED", (await Json(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task EnabledButNoProvider_IsSmsNotConfigured_503()
    {
        using var factory = new TigerCsApiFactory
        {
            ExtraConfiguration = new() { ["Otp:Enabled"] = "true", ["Otp:Pepper"] = "endpoint-test-pepper-not-a-secret" }   // Sms:Provider stays Disabled
        };
        var client = await SignInAsync(factory);

        var response = await client.PostAsJsonAsync(Send, SendBody());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("OTP_SMS_NOT_CONFIGURED", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task OtherChannels_Are501()
    {
        var client = await SignInAsync(_enabled);

        var response = await client.PostAsJsonAsync(Send, new { customerReference = "crm:9001", phoneNumber = Phone, unitId = 9200, channel = "WhatsApp" });

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Theory]
    [InlineData(Send)]
    [InlineData(Resend)]
    [InlineData(Verify)]
    public async Task Unauthenticated_Is401(string route)
    {
        var response = await _enabled.CreateClient().PostAsJsonAsync(route, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARoleWithoutCustomerVerificationAccess_Is403()
    {
        var client = await SignInAsync(_enabled, Roles.ReportingUser);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Send, SendBody())).StatusCode);
    }
}
