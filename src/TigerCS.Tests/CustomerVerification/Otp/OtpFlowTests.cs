using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Otp;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.GenesysIntegration;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Integrations.Modules.SmsIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.CustomerVerification.Otp;

public sealed class FakeOtpChallengeRepository : IOtpChallengeRepository
{
    public Dictionary<Guid, OtpChallenge> Items { get; } = [];
    public bool FailNextSave { get; set; }

    public Task<OtpChallenge?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(id));

    public Task AddAsync(OtpChallenge challenge, CancellationToken cancellationToken = default)
    {
        Items[challenge.OtpChallengeId] = challenge;
        return Task.CompletedTask;
    }

    public Task<int> CountForCustomerSinceAsync(int crmCustomerId, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.Values.Count(c => c.CrmCustomerId == crmCustomerId && c.CreatedAtUtc >= sinceUtc));

    public Task<bool> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (FailNextSave)
        {
            FailNextSave = false;
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// The OTP flow with CRM and the SMS provider controlled. <b>Local/fake verification only:</b> the sender is
/// <see cref="FakeSmsSender"/>; nothing here sends a real SMS or calls Broadnet.
/// </summary>
public sealed class OtpFlowTests
{
    private const string Phone = "tel:+971500000900";
    private static readonly Guid Caller = Guid.NewGuid();

    private sealed class Clock(Func<DateTime> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now(), TimeSpan.Zero);
    }

    private sealed class Harness
    {
        public FakeCrmBuyerLookupGateway Crm { get; } = new();
        public FakeSmsSender Sms { get; } = new(Options.Create(new SmsOptions()), NullLogger<FakeSmsSender>.Instance);
        public FakeOtpChallengeRepository Challenges { get; } = new();
        public FakeVerificationSessionRepository Sessions { get; } = new();
        public FakeUnitReferenceRepository Units { get; } = new();
        public FakeContactReferenceRepository Contacts { get; } = new();
        public FakeAuditEntryWriter Audit { get; } = new();
        public OtpOptions Otp { get; } = new() { Enabled = true, Pepper = "unit-test-pepper-not-a-secret" };
        public GenesysOptions Genesys { get; } = new() { Enabled = true };
        public DateTime Now { get; set; } = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        public FakeCrmUnitDetailsGateway Details { get; } = new();

        public Harness(string? mobile = "+971500000900")
        {
            Crm.Returns(CrmBuyerLookupResult.Success([new CrmBuyerMatchDto(
                new CrmCustomerDto(9001, "Test Buyer", null, mobile, "private@example.test"),
                [Unit(9200, "1204", 79, "Tiger Tower", 9100), Unit(9201, "0507", 80, "Tiger Heights", 9101)])]));
        }

        private static CrmBuyerUnitDto Unit(int unitId, string number, int projectId, string project, int lead) =>
            new(lead, 4, "Contract", unitId, number, 3, 2, 12, projectId, project, null, 1, "Buyer");

        private GenesysVerifiedBuyerResolver Resolver => new(
            new CrmBuyerLookupAppService(Crm, NullLogger<CrmBuyerLookupAppService>.Instance), NullLogger<GenesysVerifiedBuyerResolver>.Instance);

        public OtpAppService Service => new(
            Otp, Genesys, Resolver, Sms, Challenges, Sessions, Units, Contacts, new FakeCustomerVerificationUnitOfWork(), Audit,
            new Clock(() => Now), NullLogger<OtpAppService>.Instance);

        public GenesysCustomerUnitDetailsAppService UnitDetails => new(
            Genesys, Resolver, Details, new CrmDocumentOptions(), Sessions, Units, new Clock(() => Now),
            NullLogger<GenesysCustomerUnitDetailsAppService>.Instance);

        public Task<OtpResult> Send(int unit = 9200, string? language = null, Guid? caller = null, string customer = "crm:9001") =>
            Service.SendAsync(caller ?? Caller, new OtpSendRequestDto(customer, Phone, unit, null, language));

        public Task<OtpResult> Verify(Guid challenge, string? code, Guid? caller = null) =>
            Service.VerifyAsync(caller ?? Caller, new OtpVerifyRequestDto(challenge, code));

        public Task<OtpResult> Resend(Guid challenge, Guid? caller = null) =>
            Service.ResendAsync(caller ?? Caller, new OtpResendRequestDto(challenge));
    }

    // ---------------------------------------------------------------- send

    [Fact]
    public async Task Send_GoesToTheMobileCrmHolds_NotWhatTheCallerTyped_AndNeverReturnsTheCode()
    {
        var h = new Harness(mobile: "+971 50 111 2222");

        var result = await h.Send();

        Assert.Equal(OtpStatus.Sent, result.Status);
        Assert.Equal("+971******222", result.MaskedDestination);
        Assert.Equal("971501112222", h.Sms.Last!.Destination);   // CRM's number, not the searched 971500000900
        var code = h.Sms.LastCode!;
        Assert.Equal(6, code.Length);
        Assert.DoesNotContain(code, System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Equal(h.Now.AddMinutes(5), result.ExpiresAtUtc);
        Assert.Equal(h.Now.AddSeconds(60), result.ResendAvailableAtUtc);
        Assert.Equal(2, result.SendsRemaining);
    }

    [Fact]
    public async Task OnlyAKeyedHashOfTheCodeIsStored_AndItIsNotTheCode()
    {
        var h = new Harness();
        var sent = await h.Send();

        var stored = h.Challenges.Items[sent.OtpChallengeId!.Value];

        Assert.Matches("^[0-9a-f]{64}$", stored.CodeHash);
        Assert.DoesNotContain(h.Sms.LastCode!, stored.CodeHash);
        // The pepper is a key: with another pepper the stored hash no longer verifies the right code.
        var code = h.Sms.LastCode;
        h.Otp.Pepper = "a-different-pepper";
        Assert.Equal(OtpStatus.InvalidCode, (await h.Verify(sent.OtpChallengeId.Value, code)).Status);
    }

    [Fact]
    public async Task Audit_NeverContainsTheCodeOrThePhoneNumber()
    {
        var h = new Harness();
        var sent = await h.Send();
        await h.Verify(sent.OtpChallengeId!.Value, h.Sms.LastCode);

        var audit = string.Join("|", h.Audit.Entries.Select(e => e.AfterValue));
        Assert.NotEmpty(h.Audit.Entries);
        Assert.DoesNotContain(h.Sms.LastCode!, audit);
        Assert.DoesNotContain("971500000900", audit);
    }

    [Theory]
    [InlineData("en", "Your Tiger verification code is")]
    [InlineData("ar", "رمز التحقق الخاص بك من تايجر هو")]
    public async Task MessageLanguage_FollowsTheRequest(string language, string expectedStart)
    {
        var h = new Harness();

        await h.Send(language: language);

        Assert.StartsWith(expectedStart, h.Sms.Last!.Text);
        Assert.Equal(language, h.Sms.Last.Language);
        Assert.Contains("5", h.Sms.Last.Text);
    }

    [Fact]
    public async Task SwitchedOff_OrNotConfigured_CreatesNoChallengeAndSendsNothing()
    {
        var off = new Harness { Otp = { Enabled = false } };
        Assert.Equal(OtpStatus.Disabled, (await off.Send()).Status);

        var noPepper = new Harness { Otp = { Pepper = "" } };
        Assert.Equal(OtpStatus.SmsNotConfigured, (await noPepper.Send()).Status);

        var genesysOff = new Harness { Genesys = { Enabled = false } };
        Assert.Equal(OtpStatus.Disabled, (await genesysOff.Send()).Status);

        Assert.Empty(off.Challenges.Items);
        Assert.Empty(noPepper.Challenges.Items);
        Assert.Empty(genesysOff.Challenges.Items);
        Assert.Empty(off.Sms.Sent);
    }

    [Fact]
    public async Task UnconfiguredProvider_IsReported_BeforeAnyChallengeExists()
    {
        var h = new Harness();
        var disabled = new OtpAppService(
            h.Otp, h.Genesys,
            new GenesysVerifiedBuyerResolver(new CrmBuyerLookupAppService(h.Crm, NullLogger<CrmBuyerLookupAppService>.Instance), NullLogger<GenesysVerifiedBuyerResolver>.Instance),
            new DisabledSmsSender(), h.Challenges, h.Sessions, h.Units, h.Contacts, new FakeCustomerVerificationUnitOfWork(), h.Audit,
            new Clock(() => h.Now), NullLogger<OtpAppService>.Instance);

        var result = await disabled.SendAsync(Caller, new OtpSendRequestDto("crm:9001", Phone, 9200));

        Assert.Equal(OtpStatus.SmsNotConfigured, result.Status);
        Assert.Empty(h.Challenges.Items);
    }

    [Fact]
    public async Task OtherChannels_AreNotIntegrated()
    {
        var h = new Harness();

        var result = await h.Service.SendAsync(Caller, new OtpSendRequestDto("crm:9001", Phone, 9200, "WhatsApp"));

        Assert.Equal(OtpStatus.ChannelNotIntegrated, result.Status);
        Assert.Empty(h.Sms.Sent);
    }

    [Fact]
    public async Task OwnershipIsTheUnitDetailsRule_AnotherCustomersUnitOrReferenceSendsNothing()
    {
        var h = new Harness();

        Assert.Equal(OtpStatus.UnitNotEligible, (await h.Send(unit: 5555)).Status);
        Assert.Equal(OtpStatus.CustomerNotVerified, (await h.Send(customer: "crm:7777")).Status);
        Assert.Equal(OtpStatus.InvalidRequest, (await h.Send(customer: "ext:Pact:55")).Status);
        Assert.Equal(OtpStatus.InvalidRequest, (await h.Service.SendAsync(Caller, new OtpSendRequestDto("crm:9001", Phone, null))).Status);
        Assert.Equal(OtpStatus.InvalidRequest, (await h.Send(language: "fr")).Status);
        Assert.Empty(h.Sms.Sent);
        Assert.Empty(h.Challenges.Items);
    }

    [Theory]
    [InlineData(CrmBuyerLookupOutcome.Unavailable, OtpStatus.CrmUnavailable)]
    [InlineData(CrmBuyerLookupOutcome.AmbiguousCustomerMatch, OtpStatus.CustomerAmbiguous)]
    [InlineData(CrmBuyerLookupOutcome.NotFound, OtpStatus.CustomerNotVerified)]
    public async Task CrmProblems_AreReported_AndNothingIsSent(CrmBuyerLookupOutcome crm, OtpStatus expected)
    {
        var h = new Harness();
        h.Crm.Returns(new CrmBuyerLookupResult(crm));

        Assert.Equal(expected, (await h.Send()).Status);
        Assert.Empty(h.Sms.Sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("0501112222")]      // national form: refused unless a country code is configured
    public async Task NoUsableMobileInCrm_SendsNothing(string? mobile)
    {
        var h = new Harness(mobile);

        Assert.Equal(OtpStatus.DestinationUnavailable, (await h.Send()).Status);
        Assert.Empty(h.Sms.Sent);
    }

    [Fact]
    public async Task NationalNumber_IsCompletedOnlyWithAConfiguredCountryCode()
    {
        var h = new Harness("0501112222") { Otp = { DefaultCountryCode = "971" } };

        Assert.Equal(OtpStatus.Sent, (await h.Send()).Status);
        Assert.Equal("971501112222", h.Sms.Last!.Destination);
    }

    [Fact]
    public async Task TooManyChallengesForOneCustomer_AreRateLimited()
    {
        var h = new Harness { Otp = { MaxChallengesPerCustomerPerHour = 2 } };

        Assert.Equal(OtpStatus.Sent, (await h.Send()).Status);
        Assert.Equal(OtpStatus.Sent, (await h.Send(unit: 9201)).Status);
        Assert.Equal(OtpStatus.RateLimited, (await h.Send()).Status);
        Assert.Equal(2, h.Sms.Sent.Count);

        h.Now = h.Now.AddHours(1).AddSeconds(1);
        Assert.Equal(OtpStatus.Sent, (await h.Send()).Status);
    }

    // ------------------------------------------------- delivery honesty

    [Theory]
    [InlineData(SmsSendOutcome.Rejected, OtpStatus.DeliveryRejected, OtpDeliveryState.Rejected)]
    [InlineData(SmsSendOutcome.Failed, OtpStatus.DeliveryFailed, OtpDeliveryState.Failed)]
    public async Task RejectionAndFailure_AreNeverReportedAsSent(SmsSendOutcome outcome, OtpStatus expected, OtpDeliveryState state)
    {
        var h = new Harness();
        h.Sms.Then(outcome);

        var result = await h.Send();

        Assert.Equal(expected, result.Status);
        var stored = h.Challenges.Items[result.OtpChallengeId!.Value];
        Assert.Equal(state, stored.DeliveryState);
        Assert.Equal(OtpChallengeStatus.DeliveryFailed, stored.Status);
        // A code that never reached the customer cannot be verified.
        Assert.Equal(OtpStatus.ChallengeNotActive, (await h.Verify(result.OtpChallengeId.Value, h.Sms.LastCode)).Status);
    }

    [Fact]
    public async Task Timeout_IsUnconfirmed_NothingIsResentAutomatically_AndTheCodeStaysValid()
    {
        var h = new Harness();
        h.Sms.Then(SmsSendOutcome.Unconfirmed);

        var result = await h.Send();

        Assert.Equal(OtpStatus.DeliveryUnconfirmed, result.Status);
        Assert.NotEqual(OtpStatus.Sent, result.Status);
        Assert.Single(h.Sms.Sent);                                   // no automatic second send
        var stored = h.Challenges.Items[result.OtpChallengeId!.Value];
        Assert.Equal(OtpDeliveryState.Unconfirmed, stored.DeliveryState);
        Assert.Equal(OtpChallengeStatus.Pending, stored.Status);
        // The SMS may have arrived: the customer can still use it.
        Assert.Equal(OtpStatus.Verified, (await h.Verify(result.OtpChallengeId.Value, h.Sms.LastCode)).Status);
        Assert.Single(h.Sms.Sent);
    }

    [Fact]
    public async Task ASenderThatFaults_IsTreatedAsUnconfirmed_NotSuccess()
    {
        var h = new Harness();
        h.Sms.Throws = new InvalidOperationException("boom");

        var result = await h.Send();

        Assert.Equal(OtpStatus.DeliveryUnconfirmed, result.Status);
        Assert.Single(h.Challenges.Items);
    }

    [Fact]
    public async Task UnconfirmedSend_ThenAnExplicitResend_SendsExactlyOneMoreSms()
    {
        var h = new Harness();
        h.Sms.Then(SmsSendOutcome.Unconfirmed);
        var first = await h.Send();
        var firstCode = h.Sms.LastCode;

        h.Now = h.Now.AddSeconds(61);
        var second = await h.Resend(first.OtpChallengeId!.Value);

        Assert.Equal(OtpStatus.Sent, second.Status);
        Assert.Equal(2, h.Sms.Sent.Count);
        Assert.NotEqual(firstCode, h.Sms.LastCode);
    }

    // ---------------------------------------------------------------- resend

    [Fact]
    public async Task Resend_IsHeldBackByTheCooldown_ThenReplacesTheCode()
    {
        var h = new Harness();
        var sent = await h.Send();
        var oldCode = h.Sms.LastCode!;

        var tooSoon = await h.Resend(sent.OtpChallengeId!.Value);
        Assert.Equal(OtpStatus.ResendTooSoon, tooSoon.Status);
        Assert.Equal(h.Now.AddSeconds(60), tooSoon.ResendAvailableAtUtc);
        Assert.Single(h.Sms.Sent);

        h.Now = h.Now.AddSeconds(60);
        var resent = await h.Resend(sent.OtpChallengeId.Value);
        Assert.Equal(OtpStatus.Sent, resent.Status);
        var newCode = h.Sms.LastCode!;

        if (newCode != oldCode)
        {
            Assert.Equal(OtpStatus.InvalidCode, (await h.Verify(sent.OtpChallengeId.Value, oldCode)).Status);
        }

        Assert.Equal(OtpStatus.Verified, (await h.Verify(sent.OtpChallengeId.Value, newCode)).Status);
    }

    [Fact]
    public async Task Resend_StopsAtTheConfiguredLimit()
    {
        var h = new Harness { Otp = { MaxSendsPerChallenge = 2, ResendCooldownSeconds = 0 } };
        var sent = await h.Send();

        Assert.Equal(OtpStatus.Sent, (await h.Resend(sent.OtpChallengeId!.Value)).Status);
        Assert.Equal(OtpStatus.ResendLimitReached, (await h.Resend(sent.OtpChallengeId.Value)).Status);
        Assert.Equal(2, h.Sms.Sent.Count);
    }

    [Fact]
    public async Task Resend_ByAnotherAccountOrForAnUnknownChallenge_IsNotFound()
    {
        var h = new Harness();
        var sent = await h.Send();
        h.Now = h.Now.AddMinutes(2);

        Assert.Equal(OtpStatus.ChallengeNotFound, (await h.Resend(sent.OtpChallengeId!.Value, caller: Guid.NewGuid())).Status);
        Assert.Equal(OtpStatus.ChallengeNotFound, (await h.Resend(Guid.NewGuid())).Status);
        Assert.Single(h.Sms.Sent);
    }

    [Fact]
    public async Task Resend_AfterAWrongCodeLock_OrVerification_IsRefused()
    {
        var h = new Harness { Otp = { MaxVerifyAttempts = 1, ResendCooldownSeconds = 0 } };
        var sent = await h.Send();
        await h.Verify(sent.OtpChallengeId!.Value, "not-the-code");

        Assert.Equal(OtpStatus.ChallengeNotActive, (await h.Resend(sent.OtpChallengeId.Value)).Status);
    }

    [Fact]
    public async Task Resend_ResetsTheAttemptCounter_ForTheNewCode()
    {
        var h = new Harness { Otp = { ResendCooldownSeconds = 0 } };
        var sent = await h.Send();
        await h.Verify(sent.OtpChallengeId!.Value, "wrong1");
        await h.Verify(sent.OtpChallengeId.Value, "wrong2");

        await h.Resend(sent.OtpChallengeId.Value);

        Assert.Equal(0, h.Challenges.Items[sent.OtpChallengeId.Value].FailedAttempts);
    }

    // ---------------------------------------------------------------- verify

    [Fact]
    public async Task CorrectCode_RecordsTheOrdinaryConfirmedVerificationSession()
    {
        var h = new Harness();
        var sent = await h.Send();

        var verified = await h.Verify(sent.OtpChallengeId!.Value, h.Sms.LastCode);

        Assert.Equal(OtpStatus.Verified, verified.Status);
        Assert.Equal(9200, verified.UnitId);
        Assert.Equal("crm:9001", verified.CustomerReference);
        var session = await h.Sessions.GetByIdAsync(verified.VerificationSessionId!.Value);
        Assert.NotNull(session);
        Assert.True(session!.IsOwnedBy(Caller));
        Assert.True(session.Confirmed);
        Assert.Equal(VerificationMethod.Otp, session.VerificationMethod);
        Assert.Equal(VerificationSessionStatus.Confirmed, session.Status);
        Assert.Equal(h.Now.Add(VerificationSessionAppService.SessionLifetime), session.ExpiresAtUtc);
        Assert.Equal("9200", (await h.Units.GetByIdAsync(session.UnitReferenceId))!.CrmUnitId);
        Assert.Equal(OtpChallengeStatus.Verified, h.Challenges.Items[sent.OtpChallengeId.Value].Status);
    }

    [Fact]
    public async Task ACodeWorksOnce()
    {
        var h = new Harness();
        var sent = await h.Send();
        var code = h.Sms.LastCode;

        Assert.Equal(OtpStatus.Verified, (await h.Verify(sent.OtpChallengeId!.Value, code)).Status);
        Assert.Equal(OtpStatus.ChallengeNotActive, (await h.Verify(sent.OtpChallengeId.Value, code)).Status);
    }

    [Fact]
    public async Task WrongCodes_CountDown_ThenLock_EvenForTheRightCodeAfterwards()
    {
        var h = new Harness { Otp = { MaxVerifyAttempts = 3 } };
        var sent = await h.Send();
        var id = sent.OtpChallengeId!.Value;
        var right = h.Sms.LastCode!;
        var wrong = right == "111111" ? "222222" : "111111";

        var first = await h.Verify(id, wrong);
        Assert.Equal((OtpStatus.InvalidCode, 2), (first.Status, first.AttemptsRemaining));
        Assert.Equal(1, (await h.Verify(id, wrong)).AttemptsRemaining);
        Assert.Equal(OtpStatus.Locked, (await h.Verify(id, wrong)).Status);
        Assert.Equal(OtpStatus.Locked, (await h.Verify(id, right)).Status);
    }

    [Fact]
    public async Task AnExpiredCode_IsRefused_AndSoIsAnOldChallenge()
    {
        var h = new Harness();
        var sent = await h.Send();
        var code = h.Sms.LastCode;

        h.Now = h.Now.AddMinutes(5);
        Assert.Equal(OtpStatus.Expired, (await h.Verify(sent.OtpChallengeId!.Value, code)).Status);
    }

    [Fact]
    public async Task Verify_ByAnotherAccount_OrWithAMalformedRequest_GetsNothing()
    {
        var h = new Harness();
        var sent = await h.Send();

        Assert.Equal(OtpStatus.ChallengeNotFound, (await h.Verify(sent.OtpChallengeId!.Value, h.Sms.LastCode, caller: Guid.NewGuid())).Status);
        Assert.Equal(OtpStatus.ChallengeNotFound, (await h.Verify(Guid.NewGuid(), "123456")).Status);
        Assert.Equal(OtpStatus.InvalidRequest, (await h.Verify(sent.OtpChallengeId.Value, "")).Status);
        Assert.Equal(OtpStatus.InvalidRequest, (await h.Verify(Guid.Empty, "123456")).Status);
        Assert.Equal(OtpChallengeStatus.Pending, h.Challenges.Items[sent.OtpChallengeId.Value].Status);
    }

    [Fact]
    public async Task TwoSimultaneousVerifications_OnlyOneWins()
    {
        var h = new Harness();
        var sent = await h.Send();
        h.Challenges.FailNextSave = true;     // the other request saved first

        var result = await h.Verify(sent.OtpChallengeId!.Value, h.Sms.LastCode);

        Assert.Equal(OtpStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task OneCustomerWithTwoUnits_GetsADistinctSessionForEach()
    {
        var h = new Harness();
        var a = await h.Send(unit: 9200);
        var codeA = h.Sms.LastCode;
        var b = await h.Send(unit: 9201);
        var codeB = h.Sms.LastCode;

        var va = await h.Verify(a.OtpChallengeId!.Value, codeA);
        var vb = await h.Verify(b.OtpChallengeId!.Value, codeB);

        Assert.Equal(OtpStatus.Verified, va.Status);
        Assert.Equal(OtpStatus.Verified, vb.Status);
        Assert.NotEqual(va.VerificationSessionId, vb.VerificationSessionId);
        Assert.Equal((9200, 9201), (va.UnitId, vb.UnitId));
    }

    // ------------------------- the session it records is the existing proof

    [Fact]
    public async Task TheSessionFromAnOtp_UnlocksTheSale_OnlyForItsOwnUnit_ThroughTheExistingUnitDetailsCheck()
    {
        var h = new Harness();
        h.Details.Returns(9200, CrmUnitDetailsResult.Found(new CrmUnitDetails(
            null, null, null, null, null, null, null, null, null, new CrmSaleDetails(9100, 1850000m, 74000m, "AED"))));
        h.Details.Returns(9201, CrmUnitDetailsResult.Found(new CrmUnitDetails(
            null, null, null, null, null, null, null, null, null, new CrmSaleDetails(9101, 2000000m, 80000m, "AED"))));
        var sent = await h.Send(unit: 9200);
        var verified = await h.Verify(sent.OtpChallengeId!.Value, h.Sms.LastCode);
        var session = verified.VerificationSessionId;

        var own = (await h.UnitDetails.GetAsync("crm:9001", Phone, 9200, Caller, session)).Response!;
        Assert.Equal("Available", own.FinancialDetailsStatus);
        Assert.Equal(1850000m, own.Sale!.SoldPrice!.Amount);

        // Same customer, other unit: this OTP proved unit 9200 only.
        var other = (await h.UnitDetails.GetAsync("crm:9001", Phone, 9201, Caller, session)).Response!;
        Assert.Equal("VerificationFailed", other.FinancialDetailsStatus);
        Assert.Null(other.Sale);

        // Another service account cannot borrow the session.
        var borrowed = (await h.UnitDetails.GetAsync("crm:9001", Phone, 9200, Guid.NewGuid(), session)).Response!;
        Assert.Equal("VerificationFailed", borrowed.FinancialDetailsStatus);

        // After the session lifetime the proof is gone.
        h.Now = h.Now.AddMinutes(31);
        var late = (await h.UnitDetails.GetAsync("crm:9001", Phone, 9200, Caller, session)).Response!;
        Assert.Equal("VerificationFailed", late.FinancialDetailsStatus);
    }

    [Fact]
    public void Mask_KeepsOnlyThePrefixAndLastThreeDigits()
    {
        Assert.Equal("+971******900", OtpAppService.Mask("971500000900"));
        Assert.Equal("***", OtpAppService.Mask("12345"));
    }
}
