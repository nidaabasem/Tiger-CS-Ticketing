using System.Text.Json;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Integrations.Modules.SmsIntegration;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// SMS as the second channel of the existing OTP flow, over the REAL <c>CustomerOtpAppService</c> with a scripted CRM buyer lookup and the
/// <see cref="FakeSmsSender"/>. <b>Local verification only: no SMS is sent and Broadnet is not contacted.</b> The email behaviour these
/// tests sit next to is covered, unchanged, by <see cref="CustomerOtpAppServiceTests"/>.
/// </summary>
public class CustomerSmsOtpAppServiceTests
{
    private static readonly Guid Caller = OtpServiceFixture.Caller;
    private const string CrmMobile = "+971 50 999 8888";          // as CRM writes it
    private const string CrmMobileDigits = "971509998888";

    private static OtpServiceFixture Sms(string? mobile = CrmMobile, bool twoUnits = true)
    {
        var f = new OtpServiceFixture(mobile: mobile, twoUnits: twoUnits);
        f.EnableSms();
        return f;
    }

    private static Task<CustomerOtpResult> SendSms(OtpServiceFixture f, string unit = "1102", string? language = null) =>
        f.Service.SendAsync(Caller, OtpServiceFixture.Phone, unit, "Sms", language);

    // ------------------------------------------------------------ lookup

    [Fact]
    public async Task Lookup_AdvertisesSms_OnlyWhenEnabled_AndAMobileIsOnRecord_WithoutLeakingIt()
    {
        var on = Sms();
        var off = new OtpServiceFixture(mobile: CrmMobile);

        var enabled = await on.Service.LookupAsync(OtpServiceFixture.Phone);
        var disabled = await off.Service.LookupAsync(OtpServiceFixture.Phone);

        Assert.Equal(["Email", "Sms"], enabled.AvailableChannels);
        Assert.Equal("+971******888", enabled.MaskedMobile);
        Assert.Equal(["Email"], disabled.AvailableChannels);
        Assert.Null(disabled.MaskedMobile);
        var json = JsonSerializer.Serialize(enabled);
        Assert.DoesNotContain("9998888", json);
        Assert.DoesNotContain("999 8888", json);
        Assert.Empty(on.Sms.Sent);
    }

    // -------------------------------------------------------------- send

    [Fact]
    public async Task Send_GoesToTheMobileCrmHolds_NotTheSearchedNumber_AndSendsNoEmail()
    {
        var f = Sms();

        var result = await SendSms(f);

        Assert.Equal(CustomerOtpStatus.CodeSent, result.Status);
        Assert.Equal("Sms", result.Channel);
        var sms = Assert.Single(f.Sms.Sent);
        Assert.Equal(CrmMobileDigits, sms.Destination);            // CRM's number — the request searched by +971501234567 and carried no destination
        Assert.NotEqual("971501234567", sms.Destination);
        Assert.Equal("111111", f.LastSmsCode());
        Assert.Empty(f.Email.Sent);
        Assert.Equal("+971******888", result.MaskedDestination);
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime.AddMinutes(10), result.ExpiresAtUtc);
        Assert.Contains("10 minutes", sms.Text);

        var challenge = f.Store.Single();
        Assert.Equal((OtpChannel.Sms, "en", OtpDeliveryState.Accepted), (challenge.Channel, challenge.Language, challenge.DeliveryState));
        Assert.Equal((Caller, 9001, 12346), (challenge.CallerEmployeeId, challenge.CrmCustomerId, challenge.CrmLeadId));
    }

    [Fact]
    public async Task TheCodeIsOnlyEverInTheSms_NeverInTheResult_TheAudit_OrTheStoredHash()
    {
        var f = Sms();

        var result = await SendSms(f);
        var code = f.LastSmsCode();

        Assert.DoesNotContain(code, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(f.Audit.Entries, e => (e.AfterValue ?? "").Contains(code) || (e.AfterValue ?? "").Contains("9998888") || (e.AfterValue ?? "").Contains(CrmMobileDigits));
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(code), f.Store.Single().CodeHash);
        Assert.Contains(f.Audit.Entries, e => e.Action == "CustomerOtpSent" && (e.AfterValue ?? "").Contains("Channel=Sms"));
    }

    [Theory]
    [InlineData("en", "Your Tiger verification code is")]
    [InlineData("ar", "رمز التحقق الخاص بك من تايجر هو")]
    [InlineData("AR", "رمز التحقق الخاص بك من تايجر هو")]
    public async Task TheSmsLanguageFollowsTheRequest(string language, string start)
    {
        var f = Sms();

        await SendSms(f, language: language);

        Assert.StartsWith(start, f.Sms.Last!.Text);
        Assert.Equal(language.ToLowerInvariant(), f.Sms.Last.Language);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("english")]
    public async Task AnUnknownLanguage_IsRefused_AndNothingIsSent(string language)
    {
        var f = Sms();

        Assert.Equal(CustomerOtpStatus.InvalidRequest, (await SendSms(f, language: language)).Status);
        Assert.Empty(f.Sms.Sent);
        Assert.Empty(f.Store.Committed);
    }

    [Theory]
    [InlineData("WhatsApp")]
    [InlineData("Voice")]
    [InlineData("1")]
    public async Task AnUnknownChannel_IsRefused_NeitherEmailNorSmsIsUsed(string channel)
    {
        var f = Sms();

        var result = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1102", channel);

        Assert.Equal(CustomerOtpStatus.InvalidRequest, result.Status);
        Assert.Empty(f.Sms.Sent);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task WithNoChannel_TheDefaultIsStillEmail_AndNoSmsIsSent()
    {
        var f = Sms();

        var result = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1102");

        Assert.Equal(CustomerOtpStatus.CodeSent, result.Status);
        Assert.Single(f.Email.Sent);
        Assert.Empty(f.Sms.Sent);
        Assert.Equal(OtpChannel.Email, f.Store.Single().Channel);
    }

    // ----------------------------------------------- off / not configured

    [Fact]
    public async Task SmsSwitchedOff_IsRefusedBeforeCrmIsAsked_AndNothingExists()
    {
        var f = new OtpServiceFixture(mobile: CrmMobile);   // CrmDocuments:OtpSmsEnabled is false by default

        var result = await SendSms(f);

        Assert.Equal(CustomerOtpStatus.SmsNotConfigured, result.Status);
        Assert.Equal(CustomerOtpCodes.SmsNotConfigured, result.Code);
        Assert.Equal(0, f.Buyers.CallCount);
        Assert.Empty(f.Store.Committed);
        Assert.Empty(f.Sms.Sent);
    }

    [Fact]
    public async Task EnabledButProviderNotConfigured_CreatesNoChallenge()
    {
        var f = Sms();
        f.UseSmsSender(new DisabledSmsSender());

        var result = await SendSms(f);

        Assert.Equal(CustomerOtpStatus.SmsNotConfigured, result.Status);
        Assert.Empty(f.Store.Committed);
        Assert.Equal(0, f.Buyers.CallCount);
    }

    // ----------------------------------------------------- the CRM mobile

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("12345")]
    [InlineData("0509998888")]      // national form and no country code configured: refused, not guessed
    public async Task NoUsableMobileInCrm_SendsNothing_NeverFallingBackToTheSearchedNumber(string? mobile)
    {
        var f = Sms(mobile);

        var result = await SendSms(f);

        Assert.Equal(CustomerOtpStatus.NoMobileOnRecord, result.Status);
        Assert.Equal(CustomerOtpCodes.NoMobileOnRecord, result.Code);
        Assert.Empty(f.Sms.Sent);
        Assert.Empty(f.Store.Committed);
    }

    [Fact]
    public async Task ANationalNumber_IsCompletedOnlyWithAConfiguredCountryCode()
    {
        var f = Sms("0509998888");
        f.Options.OtpSmsDefaultCountryCode = "971";

        Assert.Equal(CustomerOtpStatus.CodeSent, (await SendSms(f)).Status);
        Assert.Equal(CrmMobileDigits, f.Sms.Last!.Destination);
    }

    [Fact]
    public async Task A00PrefixedMobile_IsReadAsInternational()
    {
        var f = Sms("00971509998888");

        await SendSms(f);

        Assert.Equal(CrmMobileDigits, f.Sms.Last!.Destination);
    }

    // ------------------------------------- wrong customer / unit / lead

    [Theory]
    [InlineData("9999")]
    [InlineData("12346")]   // a LEAD id, never accepted as a unit selector
    [InlineData("1102 OR 1=1")]
    public async Task AUnitThatIsNotThisCustomers_IsRefused_AndNoSmsIsSent(string unit)
    {
        var f = Sms();

        var result = await SendSms(f, unit);

        Assert.Equal(CustomerOtpStatus.UnitNotOwned, result.Status);
        Assert.Empty(f.Sms.Sent);
        Assert.Empty(f.Store.Committed);
    }

    [Theory]
    [InlineData(CrmBuyerLookupOutcome.NotFound, CustomerOtpStatus.CustomerNotFound)]
    [InlineData(CrmBuyerLookupOutcome.AmbiguousCustomerMatch, CustomerOtpStatus.CustomerAmbiguous)]
    [InlineData(CrmBuyerLookupOutcome.Unavailable, CustomerOtpStatus.CrmUnavailable)]
    public async Task CrmProblems_AreExplicit_AndNoSmsIsSent(CrmBuyerLookupOutcome outcome, CustomerOtpStatus expected)
    {
        var f = new OtpServiceFixture(lookup: new CrmBuyerLookupResult(outcome));
        f.EnableSms();

        Assert.Equal(expected, (await SendSms(f)).Status);
        Assert.Empty(f.Sms.Sent);
    }

    [Fact]
    public async Task SeveralUnitsAndNoChoice_AsksWhichUnit_BeforeAnySms()
    {
        var f = Sms();

        var result = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, null, "Sms");

        Assert.Equal(CustomerOtpStatus.UnitSelectionRequired, result.Status);
        Assert.Empty(f.Sms.Sent);
    }

    // ------------------------------------------------- delivery honesty

    [Theory]
    [InlineData(SmsSendOutcome.Rejected, OtpDeliveryState.Rejected)]
    [InlineData(SmsSendOutcome.Failed, OtpDeliveryState.Failed)]
    public async Task RejectionAndFailure_AreErrors_NeverReportedAsSent(SmsSendOutcome outcome, OtpDeliveryState state)
    {
        var f = Sms();
        f.Sms.Then(outcome);

        var result = await SendSms(f);

        Assert.Equal(CustomerOtpStatus.DeliveryFailed, result.Status);
        Assert.Equal(CustomerOtpCodes.DeliveryFailed, result.Code);
        Assert.NotNull(result.ChallengeId);
        Assert.Equal(state, f.Store.Single().DeliveryState);
    }

    [Fact]
    public async Task ATimeout_IsUnconfirmed_TheChallengeStaysValid_AndOneSmsWasAttempted()
    {
        var f = Sms();
        f.Sms.Then(SmsSendOutcome.Unconfirmed);

        var result = await SendSms(f);

        Assert.Equal(CustomerOtpStatus.DeliveryUnconfirmed, result.Status);
        Assert.Equal(CustomerOtpCodes.DeliveryUnconfirmed, result.Code);
        Assert.NotNull(result.ChallengeId);
        Assert.Single(f.Sms.Sent);
        var challenge = f.Store.Single();
        Assert.Equal((OtpChallengeStatus.Pending, OtpDeliveryState.Unconfirmed), (challenge.Status, challenge.DeliveryState));
        // The SMS may have arrived: the customer can still use the code.
        Assert.Equal(CustomerOtpStatus.Verified, (await f.Service.VerifyAsync(Caller, result.ChallengeId, f.LastSmsCode())).Status);
    }

    [Fact]
    public async Task ASenderThatFaults_IsUnconfirmed_NotSuccess()
    {
        var f = Sms();
        f.Sms.Throws = new InvalidOperationException("boom");

        Assert.Equal(CustomerOtpStatus.DeliveryUnconfirmed, (await SendSms(f)).Status);
        Assert.Equal(OtpDeliveryState.Unconfirmed, f.Store.Single().DeliveryState);
    }

    [Fact]
    public async Task NothingIsResentAutomatically_ARetriedSend_AfterAnUnconfirmedOne_SendsNoSecondSms()
    {
        var f = Sms();
        f.Sms.Then(SmsSendOutcome.Unconfirmed);
        var first = await SendSms(f);

        // Even after the resend interval has passed, a retrying flow calling "send" again must not double-send.
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        var again = await SendSms(f);

        Assert.Equal(CustomerOtpStatus.DeliveryUnconfirmed, again.Status);
        Assert.Equal(first.ChallengeId, again.ChallengeId);
        Assert.Single(f.Sms.Sent);
        Assert.Equal(1, f.Store.Single().SendCount);
    }

    [Fact]
    public async Task OnlyAnExplicitResend_AfterAnUnconfirmedSend_IssuesANewCode()
    {
        var f = Sms();
        f.Sms.Then(SmsSendOutcome.Unconfirmed);
        var first = await SendSms(f);
        var firstCode = f.LastSmsCode();

        f.Clock.Advance(TimeSpan.FromSeconds(61));
        var resent = await f.Service.ResendAsync(Caller, first.ChallengeId);

        Assert.Equal(CustomerOtpStatus.CodeSent, resent.Status);
        Assert.Equal(2, f.Sms.Sent.Count);
        Assert.NotEqual(firstCode, f.LastSmsCode());
        Assert.Equal(OtpDeliveryState.Accepted, f.Store.Single().DeliveryState);
        Assert.Equal(CustomerOtpStatus.InvalidCode, (await f.Service.VerifyAsync(Caller, first.ChallengeId, firstCode)).Status);   // the old code no longer works
        Assert.Equal(CustomerOtpStatus.Verified, (await f.Service.VerifyAsync(Caller, first.ChallengeId, f.LastSmsCode())).Status);
    }

    [Fact]
    public async Task ARetriedSend_AfterAnAcceptedOne_StillSendsNothingMore_InsideTheInterval()
    {
        var f = Sms();
        var first = await SendSms(f);

        var again = await SendSms(f);

        Assert.Equal(CustomerOtpStatus.AlreadySent, again.Status);
        Assert.Equal(first.ChallengeId, again.ChallengeId);
        Assert.Single(f.Sms.Sent);
    }

    // ------------------------------------------------------------ resend

    [Fact]
    public async Task Resend_UsesTheChallengesOwnChannelAndLanguage_ToCrmsMobile()
    {
        var f = Sms();
        var sent = await SendSms(f, language: "ar");
        f.Clock.Advance(TimeSpan.FromSeconds(61));

        var resent = await f.Service.ResendAsync(Caller, sent.ChallengeId);

        Assert.Equal(CustomerOtpStatus.CodeSent, resent.Status);
        Assert.Equal("Sms", resent.Channel);
        Assert.Equal(2, f.Sms.Sent.Count);
        Assert.All(f.Sms.Sent, m => Assert.Equal((CrmMobileDigits, "ar"), (m.Destination, m.Language)));
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task Resend_IsRefused_IfCrmsMobileChangedSinceTheChallengeWasCreated()
    {
        var f = Sms();
        var sent = await SendSms(f);
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        f.Buyers.Returns(CrmBuyerLookupResult.Success([new CrmBuyerMatchDto(
            new CrmCustomerDto(9001, "Ahmed Ali", null, "+971 55 111 2222", "ahmed.ali@example.com"),
            [OtpServiceFixture.Unit(12345, 1101, "1205", "Tiger Sky Tower"), OtpServiceFixture.Unit(12346, 1102, "1403", "Tiger Sky Tower")])]));

        var resent = await f.Service.ResendAsync(Caller, sent.ChallengeId);

        Assert.NotEqual(CustomerOtpStatus.CodeSent, resent.Status);
        Assert.Single(f.Sms.Sent);                       // a code is never redirected to a new number mid-challenge
        Assert.DoesNotContain(f.Sms.Sent, m => m.Destination.EndsWith("1112222", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resend_AfterSmsIsSwitchedOff_SendsNothing_AndKeepsTheLiveCode()
    {
        var f = Sms();
        var sent = await SendSms(f);
        var code = f.LastSmsCode();
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        f.Options.OtpSmsEnabled = false;

        var resent = await f.Service.ResendAsync(Caller, sent.ChallengeId);

        Assert.Equal(CustomerOtpStatus.SmsNotConfigured, resent.Status);
        Assert.Single(f.Sms.Sent);
        Assert.Equal(1, f.Store.Single().SendCount);
        f.Options.OtpSmsEnabled = true;
        Assert.Equal(CustomerOtpStatus.Verified, (await f.Service.VerifyAsync(Caller, sent.ChallengeId, code)).Status);
    }

    [Fact]
    public async Task Resend_ByAnotherAccount_IsNotFound_AndSendsNothing()
    {
        var f = Sms();
        var sent = await SendSms(f);
        f.Clock.Advance(TimeSpan.FromSeconds(61));

        var resent = await f.Service.ResendAsync(Guid.NewGuid(), sent.ChallengeId);

        Assert.Equal(CustomerOtpStatus.ChallengeNotFound, resent.Status);
        Assert.Single(f.Sms.Sent);
    }

    // ---------------------------------- channels are independent, limits shared

    [Fact]
    public async Task AnEmailChallengeAndAnSmsChallenge_ForTheSameUnit_AreSeparate_AndEachVerifiesOnItsOwn()
    {
        var f = Sms();
        var email = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1102");
        var emailCode = f.LastEmailedCode();

        var sms = await SendSms(f);          // an SMS was asked for: it is not swallowed by the live email challenge
        var smsCode = f.LastSmsCode();

        Assert.Equal(CustomerOtpStatus.CodeSent, sms.Status);
        Assert.NotEqual(email.ChallengeId, sms.ChallengeId);
        Assert.Single(f.Email.Sent);
        Assert.Single(f.Sms.Sent);
        Assert.Equal(CustomerOtpStatus.Verified, (await f.Service.VerifyAsync(Caller, sms.ChallengeId, smsCode)).Status);
        Assert.Equal(CustomerOtpStatus.Verified, (await f.Service.VerifyAsync(Caller, email.ChallengeId, emailCode)).Status);
    }

    [Fact]
    public async Task TheHourlyChallengeCap_CountsBothChannelsTogether()
    {
        var f = Sms();
        f.Options.OtpMaxChallengesPerCustomerPerHour = 2;

        Assert.Equal(CustomerOtpStatus.CodeSent, (await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101")).Status);   // email
        Assert.Equal(CustomerOtpStatus.CodeSent, (await SendSms(f, "1101")).Status);                                              // sms
        var third = await SendSms(f, "1102");

        Assert.Equal(CustomerOtpStatus.RateLimited, third.Status);
        Assert.Single(f.Sms.Sent);
    }

    // ------------------------------------------------------------ verify

    [Fact]
    public async Task ACorrectSmsCode_RecordsTheServerSideProof_BoundToAccountCustomerUnitAndLead()
    {
        var f = Sms();
        var sent = await SendSms(f, "1102");

        var verified = await f.Service.VerifyAsync(Caller, sent.ChallengeId, f.LastSmsCode());

        Assert.Equal(CustomerOtpStatus.Verified, verified.Status);
        var session = (await f.Sessions.GetByIdAsync(verified.Session!.VerificationSessionId))!;
        Assert.True(session.IsOwnedBy(Caller));
        Assert.Equal(VerificationMethod.Otp, session.VerificationMethod);
        Assert.Equal(VerificationSessionStatus.Confirmed, session.Status);
        Assert.Equal(sent.ChallengeId, session.ProofChallengeId);   // the recorded challenge
        Assert.Equal(9001, session.CrmBuyerCustomerId);             // the CRM customer
        Assert.Equal(12346, session.CrmBuyerLeadId);                // the unit's Lead
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime.AddMinutes(30), session.ExpiresAtUtc);
        Assert.Equal(verified.Session.VerificationSessionId, f.Store.Single().VerificationSessionId);
        Assert.Equal(OtpChallengeStatus.Verified, f.Store.Single().Status);
        Assert.Equal("1102", (await f.Units.GetByIdAsync(session.UnitReferenceId))!.CrmUnitId);
    }

    [Fact]
    public async Task TheCachedContact_IsUnitScoped_IsABuyerNotAnOwner_AndKeepsTheActualCrmMobile()
    {
        var f = Sms();
        var a = await SendSms(f, "1101");
        var codeA = f.LastSmsCode();
        var b = await SendSms(f, "1102");
        var codeB = f.LastSmsCode();

        var sessionA = (await f.Service.VerifyAsync(Caller, a.ChallengeId, codeA)).Session!;
        var sessionB = (await f.Service.VerifyAsync(Caller, b.ChallengeId, codeB)).Session!;

        var contactA = (await f.Contacts.GetByCrmContactIdAsync("9001-1101"))!;
        var contactB = (await f.Contacts.GetByCrmContactIdAsync("9001-1102"))!;
        Assert.NotEqual(contactA.ContactReferenceId, contactB.ContactReferenceId);            // one customer, two units, two contact rows
        Assert.Equal(sessionA.ContactReferenceId, contactA.ContactReferenceId);
        Assert.Equal(sessionB.ContactReferenceId, contactB.ContactReferenceId);
        Assert.All(new[] { contactA, contactB }, c =>
        {
            Assert.Equal(ContactType.Buyer, c.ContactType);                                    // CRM's Buyer, never assumed to be Owner
            Assert.NotEqual(ContactType.Owner, c.ContactType);
            Assert.Equal(CrmMobile, c.ContactChannel);                                         // the actual CRM phone, as CRM holds it
            Assert.NotEqual(OtpServiceFixture.Phone, c.ContactChannel);                        // not the number the caller searched by
        });
        Assert.Equal(CrmMobile, sessionB.SnapshotContactChannel);
        Assert.NotEqual(contactA.UnitReferenceId, contactB.UnitReferenceId);
    }

    [Fact]
    public async Task WrongCodes_CountDown_ThenLock_EvenForTheRightCodeAfterwards()
    {
        var f = Sms();
        var sent = await SendSms(f);
        var right = f.LastSmsCode();

        var first = await f.Service.VerifyAsync(Caller, sent.ChallengeId, "000000");
        Assert.Equal((CustomerOtpStatus.InvalidCode, 4), (first.Status, first.AttemptsRemaining));
        for (var i = 0; i < 3; i++)
        {
            await f.Service.VerifyAsync(Caller, sent.ChallengeId, "000000");
        }

        Assert.Equal(CustomerOtpStatus.Locked, (await f.Service.VerifyAsync(Caller, sent.ChallengeId, "000000")).Status);
        Assert.Equal(CustomerOtpStatus.Locked, (await f.Service.VerifyAsync(Caller, sent.ChallengeId, right)).Status);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "ConfirmVerificationSession");
    }

    [Fact]
    public async Task ACodeWorksOnce_AReplayIsRefused_AndMakesNoSecondSession()
    {
        var f = Sms();
        var sent = await SendSms(f);
        var code = f.LastSmsCode();

        Assert.Equal(CustomerOtpStatus.Verified, (await f.Service.VerifyAsync(Caller, sent.ChallengeId, code)).Status);
        var replay = await f.Service.VerifyAsync(Caller, sent.ChallengeId, code);

        Assert.Equal(CustomerOtpStatus.AlreadyUsed, replay.Status);
        Assert.Null(replay.Session);
        Assert.Single(f.Audit.Entries, e => e.Action == "ConfirmVerificationSession");
    }

    [Fact]
    public async Task TheSameCorrectCodeSubmittedTwiceAtOnce_SpendsItOnce_AndCreatesOneSession()
    {
        var f = Sms();
        var sent = await SendSms(f);
        var winnerSession = Guid.NewGuid();

        // The other request verifies and commits first; ours is rejected by the concurrency token and re-evaluated.
        f.UnitOfWork.LoseNextSave(() => f.Store.CommitFromAnotherRequest(sent.ChallengeId!.Value, c =>
        {
            c.Verify(true, f.Clock.GetUtcNow().UtcDateTime, 5);
            c.LinkSession(winnerSession);
        }));

        var ours = await f.Service.VerifyAsync(Caller, sent.ChallengeId, f.LastSmsCode());

        Assert.Equal(CustomerOtpStatus.AlreadyUsed, ours.Status);
        Assert.Null(ours.Session);
        Assert.Equal(winnerSession, f.Store.Single().VerificationSessionId);
    }

    [Fact]
    public async Task AnExpiredSmsCode_IsRefused()
    {
        var f = Sms();
        var sent = await SendSms(f);
        var code = f.LastSmsCode();

        f.Clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(CustomerOtpStatus.Expired, (await f.Service.VerifyAsync(Caller, sent.ChallengeId, code)).Status);
    }

    [Fact]
    public async Task AnotherAccount_CannotVerify_OrBurnAttemptsOn_AnSmsChallenge()
    {
        var f = Sms();
        var sent = await SendSms(f);

        var theirs = await f.Service.VerifyAsync(Guid.NewGuid(), sent.ChallengeId, f.LastSmsCode());

        Assert.Equal(CustomerOtpStatus.ChallengeNotFound, theirs.Status);
        Assert.Equal(0, f.Store.Single().FailedAttempts);
    }
}
