using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// Email OTP verification for CRM buyers, over the REAL service with a scripted
/// CRM buyer lookup, a capturing email adapter and a store that behaves like the
/// database where it matters (tracked vs committed state, concurrency conflicts).
/// </summary>
public class CustomerOtpAppServiceTests
{
    private static readonly Guid Caller = OtpServiceFixture.Caller;

    // ------------------------------------------------------------ lookup

    [Fact]
    public async Task Lookup_ListsTheCustomersUnits_AndMasksTheEmail_AndTouchesNothing()
    {
        var f = new OtpServiceFixture();

        var result = await f.Service.LookupAsync(OtpServiceFixture.Phone);

        Assert.Equal(CustomerOtpStatus.Found, result.Status);
        Assert.Equal(["1101", "1102"], result.Units!.Select(u => u.CrmUnitId).ToArray());
        Assert.Equal([12345, 12346], result.Units!.Select(u => u.LeadId).ToArray());
        Assert.Equal("a***@e***.com", result.MaskedDestination);
        Assert.Empty(f.Email.Sent);
        Assert.Empty(f.Store.Committed);
        Assert.Equal("+971501234567", f.Buyers.LastSearchedPhoneNumber);

        // No name, phone, full email or CRM customer id in what the chatbot gets back.
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("ahmed.ali@example.com", json);
        Assert.DoesNotContain("Ahmed", json);
        Assert.DoesNotContain("9001", json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a phone")]
    [InlineData("12")]
    [InlineData(null)]
    public async Task Lookup_RejectsAnythingThatIsNotAPhoneNumber(string? phone)
    {
        var f = new OtpServiceFixture();

        Assert.Equal(CustomerOtpStatus.InvalidRequest, (await f.Service.LookupAsync(phone)).Status);
        Assert.Equal(0, f.Buyers.CallCount);
    }

    [Theory]
    [InlineData(CrmBuyerLookupOutcome.NotFound, CustomerOtpStatus.CustomerNotFound)]
    [InlineData(CrmBuyerLookupOutcome.AmbiguousCustomerMatch, CustomerOtpStatus.CustomerAmbiguous)]
    [InlineData(CrmBuyerLookupOutcome.Unavailable, CustomerOtpStatus.CrmUnavailable)]
    [InlineData(CrmBuyerLookupOutcome.Unauthorized, CustomerOtpStatus.CrmAuthenticationFailed)]
    [InlineData(CrmBuyerLookupOutcome.InvalidResponse, CustomerOtpStatus.CrmInvalidResponse)]
    public async Task EveryCrmOutcome_IsExplicit_AndNothingIsSent(CrmBuyerLookupOutcome outcome, CustomerOtpStatus expected)
    {
        var f = new OtpServiceFixture(lookup: new CrmBuyerLookupResult(outcome));

        Assert.Equal(expected, (await f.Service.LookupAsync(OtpServiceFixture.Phone)).Status);
        var send = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");
        Assert.Equal(expected, send.Status);
        Assert.Empty(f.Email.Sent);
        Assert.Empty(f.Store.Committed);
    }

    [Fact]
    public async Task WhenOff_EverythingIsRefused()
    {
        var f = new OtpServiceFixture();
        f.Options.Enabled = false;

        Assert.Equal(CustomerOtpStatus.Disabled, (await f.Service.LookupAsync(OtpServiceFixture.Phone)).Status);
        Assert.Equal(CustomerOtpStatus.Disabled, (await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101")).Status);
        Assert.Equal(CustomerOtpStatus.Disabled, (await f.Service.VerifyAsync(Caller, Guid.NewGuid(), "123456")).Status);
        Assert.Equal(0, f.Buyers.CallCount);
    }

    // -------------------------------------------------------------- send

    [Fact]
    public async Task Send_EmailsTheCodeToCrmsAddress_AndBindsTheChallenge()
    {
        var f = new OtpServiceFixture();

        var result = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1102");

        Assert.Equal(CustomerOtpStatus.CodeSent, result.Status);
        var mail = Assert.Single(f.Email.Sent);
        Assert.Equal("ahmed.ali@example.com", mail.ToAddress);          // CRM's address — the request had no destination at all
        Assert.Contains("111111", mail.Body);

        var challenge = f.Store.Single();
        Assert.Equal(Caller, challenge.CallerEmployeeId);               // bound to the integration account
        Assert.Equal(9001, challenge.CrmCustomerId);                    // …the customer
        Assert.Equal(12346, challenge.CrmLeadId);                       // …and the selected unit's lead
        Assert.Equal(result.ChallengeId, challenge.CustomerOtpChallengeId);
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime.AddMinutes(10), challenge.ExpiresAtUtc);

        // The code is stored only as a salted hash, never in the clear, never in the response or the audit trail.
        Assert.DoesNotContain("111111", System.Text.Json.JsonSerializer.Serialize(result));
        Assert.DoesNotContain(f.Audit.Entries, e => (e.AfterValue ?? "").Contains("111111"));
        Assert.NotEqual("111111"u8.ToArray(), challenge.CodeHash);
        Assert.Equal(32, challenge.CodeHash.Length);
        Assert.Equal("a***@e***.com", result.MaskedDestination);
        Assert.Equal("1403", result.Unit!.UnitNumber);
    }

    [Fact]
    public async Task Send_WithSeveralUnitsAndNoChoice_AsksWhichUnit_AndSendsNothing()
    {
        var f = new OtpServiceFixture();

        var result = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, crmUnitId: null);

        Assert.Equal(CustomerOtpStatus.UnitSelectionRequired, result.Status);
        Assert.Equal(2, result.Units!.Count);
        Assert.Empty(f.Email.Sent);
        Assert.Empty(f.Store.Committed);
    }

    [Fact]
    public async Task Send_WithOneUnit_NeedsNoChoice()
    {
        var f = new OtpServiceFixture(twoUnits: false);

        Assert.Equal(CustomerOtpStatus.CodeSent, (await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, null)).Status);
    }

    [Theory]
    [InlineData("9999")]
    [InlineData("12345")] // a LEAD id, not a unit id: never accepted as a unit selector
    [InlineData("1101 OR 1=1")]
    public async Task Send_ForAUnitThatIsNotThisCustomers_IsRefused_AndSendsNothing(string crmUnitId)
    {
        var f = new OtpServiceFixture();

        var result = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, crmUnitId);

        Assert.Equal(CustomerOtpStatus.UnitNotOwned, result.Status);
        Assert.Empty(f.Email.Sent);
        Assert.Empty(f.Store.Committed);
    }

    [Fact]
    public async Task Send_ForUnitsWithoutANumber_OrNoUnitsAtAll_IsRefused()
    {
        var f = new OtpServiceFixture(lookup: CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(
                new CrmCustomerDto(9001, "Ahmed", null, OtpServiceFixture.Phone, "a@example.com"),
                [OtpServiceFixture.Unit(1, 1, null, "P"), OtpServiceFixture.Unit(2, 2, "  ", "P")])
        ]));

        Assert.Equal(CustomerOtpStatus.UnitNotOwned, (await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, null)).Status);
        Assert.Empty((await f.Service.LookupAsync(OtpServiceFixture.Phone)).Units!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("two@@example.com")]
    public async Task Send_WithNoUsableEmailOnRecord_SendsNothing(string? email)
    {
        var f = new OtpServiceFixture(email: email);

        var result = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        Assert.Equal(CustomerOtpStatus.NoEmailOnRecord, result.Status);
        Assert.Empty(f.Email.Sent);
        Assert.Empty(f.Store.Committed);
    }

    [Fact]
    public async Task Send_WithEmailSwitchedOff_OrAFailingTransport_ReportsDeliveryFailed()
    {
        var off = new OtpServiceFixture();
        off.UseEmailPolicy(CustomerNotificationPolicy.Default); // Enabled: false
        Assert.Equal(CustomerOtpStatus.DeliveryFailed, (await off.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101")).Status);
        Assert.Empty(off.Email.Sent);

        var broken = new OtpServiceFixture();
        broken.Email.ThenTransient();
        Assert.Equal(CustomerOtpStatus.DeliveryFailed, (await broken.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101")).Status);
    }

    // ------------------------------------------- the buyer-sourced cache

    [Fact]
    public async Task Send_CachesTheBuyersUnitAndAUnitScopedContact_FromTheRealLookupFields()
    {
        var f = new OtpServiceFixture();

        await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");
        await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1102");

        var unit1 = await f.Units.GetByCrmUnitIdAsync("1101");         // CRM unitId, consistently
        Assert.Equal("1205", unit1!.UnitNumber);
        Assert.Equal("Tiger Sky Tower", unit1.PropertyName);            // projectName
        Assert.Null(unit1.TowerName);                                   // the lookup has no tower…
        Assert.Null(unit1.UnitType);                                    // …and only a numeric unit-type code, never turned into a label

        // One customer on two units → two contact rows, unit-scoped ids, each tied to its own unit.
        var contact1 = await f.Contacts.GetByCrmContactIdAsync("9001-1101");
        var contact2 = await f.Contacts.GetByCrmContactIdAsync("9001-1102");
        Assert.Equal(unit1.UnitReferenceId, contact1!.UnitReferenceId);
        Assert.Equal((await f.Units.GetByCrmUnitIdAsync("1102"))!.UnitReferenceId, contact2!.UnitReferenceId);
        Assert.NotEqual(contact1.UnitReferenceId, contact2.UnitReferenceId);
        Assert.Equal("Ahmed Ali", contact1.DisplayName);
        Assert.Equal(OtpServiceFixture.Phone, contact1.ContactChannel);

        // customerType = 1 is NOT read as ownership.
        Assert.Equal(ContactType.Buyer, contact1.ContactType);
        Assert.NotEqual(ContactType.Owner, contact1.ContactType);
    }

    [Fact]
    public async Task TheCache_NeverDegradesWhatTheAgentDeskAlreadyKnew()
    {
        var f = new OtpServiceFixture();
        var seeded = f.Units.Seed("1101", "1205", "Tiger Sky Tower");
        typeof(UnitReference).GetProperty(nameof(UnitReference.TowerName))!.SetValue(seeded, "Tower 1");
        typeof(UnitReference).GetProperty(nameof(UnitReference.UnitType))!.SetValue(seeded, "Residential");
        var owner = f.Contacts.Seed(seeded.UnitReferenceId, "9001-1101", "Ahmed (CRM unit contacts)", ContactType.Owner);

        await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        var unit = await f.Units.GetByCrmUnitIdAsync("1101");
        Assert.Equal("Tower 1", unit!.TowerName);          // kept
        Assert.Equal("Residential", unit.UnitType);        // kept
        Assert.Equal(ContactType.Owner, (await f.Contacts.GetByCrmContactIdAsync("9001-1101"))!.ContactType); // kept
        Assert.Same(owner, await f.Contacts.GetByCrmContactIdAsync("9001-1101"));
    }

    [Fact]
    public async Task ACachedContactIdBelongingToAnotherUnit_IsRefused_NotSilentlyRebound()
    {
        var f = new OtpServiceFixture();
        var elsewhere = f.Units.Seed("5555", "0001", "Elsewhere");
        f.Contacts.Seed(elsewhere.UnitReferenceId, "9001-1101", "Reused id");

        var result = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        Assert.Equal(CustomerOtpStatus.CrmInvalidResponse, result.Status);
        Assert.Empty(f.Email.Sent);
    }

    // ------------------------------------------------ repeat send / resend

    [Fact]
    public async Task ARetriedSend_ForALiveChallenge_DoesNotEmailAgain()
    {
        var f = new OtpServiceFixture();
        var first = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        f.Clock.Advance(TimeSpan.FromSeconds(10));
        var retry = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        Assert.Equal(CustomerOtpStatus.AlreadySent, retry.Status);
        Assert.Equal(first.ChallengeId, retry.ChallengeId);
        Assert.Single(f.Email.Sent);
        Assert.Single(f.Store.Committed);
    }

    [Fact]
    public async Task Resend_IsRefusedTooSoon_ThenReplacesTheCode()
    {
        var f = new OtpServiceFixture();
        var first = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        var tooSoon = await f.Service.ResendAsync(Caller, first.ChallengeId);
        Assert.Equal(CustomerOtpStatus.ResendTooSoon, tooSoon.Status);
        Assert.Equal(60, tooSoon.RetryAfterSeconds);
        Assert.Single(f.Email.Sent);

        f.Clock.Advance(TimeSpan.FromSeconds(61));
        var resent = await f.Service.ResendAsync(Caller, first.ChallengeId);
        Assert.Equal(CustomerOtpStatus.CodeSent, resent.Status);
        Assert.Equal(2, f.Email.Sent.Count);
        Assert.Equal("222222", f.LastEmailedCode());

        // The first code no longer works; the second does.
        Assert.Equal(CustomerOtpStatus.InvalidCode, (await f.Service.VerifyAsync(Caller, first.ChallengeId, "111111")).Status);
        Assert.Equal(CustomerOtpStatus.Verified, (await f.Service.VerifyAsync(Caller, first.ChallengeId, "222222")).Status);
    }

    [Fact]
    public async Task Resend_IsLimitedPerChallenge()
    {
        var f = new OtpServiceFixture();
        var challenge = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");
        for (var i = 0; i < 2; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(61));
            Assert.Equal(CustomerOtpStatus.CodeSent, (await f.Service.ResendAsync(Caller, challenge.ChallengeId)).Status);
        }

        f.Clock.Advance(TimeSpan.FromSeconds(61));
        var fourth = await f.Service.ResendAsync(Caller, challenge.ChallengeId);

        Assert.Equal(CustomerOtpStatus.ResendLimitReached, fourth.Status);
        Assert.Equal(3, f.Email.Sent.Count); // 1 send + 2 resends, never more
    }

    [Fact]
    public async Task Resend_ResetsTheWrongAttemptCount_ButNotTheSendBudget()
    {
        var f = new OtpServiceFixture();
        var challenge = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");
        for (var i = 0; i < 4; i++)
        {
            await f.Service.VerifyAsync(Caller, challenge.ChallengeId, "000000");
        }

        f.Clock.Advance(TimeSpan.FromSeconds(61));
        await f.Service.ResendAsync(Caller, challenge.ChallengeId);

        var wrong = await f.Service.VerifyAsync(Caller, challenge.ChallengeId, "000000");
        Assert.Equal(CustomerOtpStatus.InvalidCode, wrong.Status);
        Assert.Equal(4, wrong.AttemptsRemaining);
        Assert.Equal(2, f.Store.Single().SendCount);
    }

    [Fact]
    public async Task Resend_OfAnotherCallersChallenge_OrAnUnknownOne_LooksIdentical()
    {
        var f = new OtpServiceFixture();
        var challenge = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");
        f.Clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(CustomerOtpStatus.ChallengeNotFound, (await f.Service.ResendAsync(Guid.NewGuid(), challenge.ChallengeId)).Status);
        Assert.Equal(CustomerOtpStatus.ChallengeNotFound, (await f.Service.ResendAsync(Caller, Guid.NewGuid())).Status);
        Assert.Single(f.Email.Sent);
    }

    [Fact]
    public async Task Resend_RechecksCrm_AndGoesOnlyToTheCustomerTheChallengeWasBoundTo()
    {
        var f = new OtpServiceFixture();
        var challenge = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");
        f.Clock.Advance(TimeSpan.FromSeconds(61));

        // CRM now says that phone belongs to a different customer.
        f.Buyers.Returns(CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(
                new CrmCustomerDto(7777, "Other", null, OtpServiceFixture.Phone, "other@example.com"),
                [OtpServiceFixture.Unit(12345, 1101, "1205", "Tiger Sky Tower")])
        ]));

        var result = await f.Service.ResendAsync(Caller, challenge.ChallengeId);

        Assert.Equal(CustomerOtpStatus.UnitNotOwned, result.Status);
        Assert.Single(f.Email.Sent);
    }

    [Fact]
    public async Task ChallengesPerCustomerPerHour_AreCapped_AcrossCallers()
    {
        var f = new OtpServiceFixture();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(CustomerOtpStatus.CodeSent, (await f.Service.SendAsync(Guid.NewGuid(), OtpServiceFixture.Phone, "1101")).Status);
        }

        var sixth = await f.Service.SendAsync(Guid.NewGuid(), OtpServiceFixture.Phone, "1101");

        Assert.Equal(CustomerOtpStatus.RateLimited, sixth.Status);
        Assert.Equal(5, f.Email.Sent.Count);

        f.Clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(CustomerOtpStatus.CodeSent, (await f.Service.SendAsync(Guid.NewGuid(), OtpServiceFixture.Phone, "1101")).Status);
    }

    // ------------------------------------------------------------ verify

    [Fact]
    public async Task Verify_WithTheRightCode_CreatesTheOtpVerifiedSession_BoundToCustomerUnitAndLead()
    {
        var f = new OtpServiceFixture();
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1102");

        var result = await f.Service.VerifyAsync(Caller, sent.ChallengeId, f.LastEmailedCode());

        Assert.Equal(CustomerOtpStatus.Verified, result.Status);
        var dto = result.Session!;
        Assert.Equal("Confirmed", dto.Status);
        Assert.Equal("Otp", dto.VerificationMethod);
        Assert.Equal(Caller, dto.AgentEmployeeId);
        Assert.Equal("1403", dto.SnapshotUnitNumber);
        Assert.Equal("Ahmed Ali", dto.SnapshotContactDisplayName);

        var session = await f.Sessions.GetByIdAsync(dto.VerificationSessionId);
        Assert.Equal(sent.ChallengeId, session!.ProofChallengeId);   // the proof is recorded on the session
        Assert.Equal(9001, session.CrmBuyerCustomerId);              // customer / unit / lead relationship preserved
        Assert.Equal(12346, session.CrmBuyerLeadId);
        Assert.Equal(f.Store.Single().UnitReferenceId, session.UnitReferenceId);

        var challenge = f.Store.Single();
        Assert.Equal(OtpChallengeStatus.Verified, challenge.Status);
        Assert.Equal(session.VerificationSessionId, challenge.VerificationSessionId);
        Assert.Contains(f.Audit.Entries, e => e.Action == "CustomerOtpVerified");
    }

    [Fact]
    public async Task Verify_WithAWrongCode_CountsDown_AndCreatesNoSession()
    {
        var f = new OtpServiceFixture();
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        var wrong = await f.Service.VerifyAsync(Caller, sent.ChallengeId, "000000");

        Assert.Equal(CustomerOtpStatus.InvalidCode, wrong.Status);
        Assert.Equal(CustomerOtpCodes.OtpInvalid, wrong.Code);
        Assert.Equal(4, wrong.AttemptsRemaining);
        Assert.Null(wrong.Session);
        Assert.Equal(1, f.Store.Single().FailedAttempts);
        Assert.Contains(f.Audit.Entries, e => e.Action == "CustomerOtpWrongCode");
        Assert.Null(await f.Sessions.GetByIdAsync(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12 456")]
    [InlineData("abcdef")]
    [InlineData("111111 ")] // trailing space is trimmed: this one is the right code
    public async Task Verify_TreatsMalformedGuessesAsWrongCodes_AndOnlyATrimmedExactCodeAsRight(string guess)
    {
        var f = new OtpServiceFixture();
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        var result = await f.Service.VerifyAsync(Caller, sent.ChallengeId, guess);

        Assert.Equal(guess == "111111 " ? CustomerOtpStatus.Verified : CustomerOtpStatus.InvalidCode, result.Status);
    }

    [Fact]
    public async Task Verify_LocksAfterTheAttemptBudget_AndEvenTheRightCodeIsThenRefused()
    {
        var f = new OtpServiceFixture();
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(CustomerOtpStatus.InvalidCode, (await f.Service.VerifyAsync(Caller, sent.ChallengeId, "000000")).Status);
        }

        Assert.Equal(CustomerOtpStatus.Locked, (await f.Service.VerifyAsync(Caller, sent.ChallengeId, "000000")).Status);
        var rightButLate = await f.Service.VerifyAsync(Caller, sent.ChallengeId, "111111");

        Assert.Equal(CustomerOtpStatus.Locked, rightButLate.Status);
        Assert.Equal(CustomerOtpCodes.OtpLocked, rightButLate.Code);
        Assert.Equal(OtpChallengeStatus.Locked, f.Store.Single().Status);
        Assert.Null(f.Store.Single().VerificationSessionId);
        Assert.Contains(f.Audit.Entries, e => e.Action == "CustomerOtpLocked");

        // A locked challenge cannot be revived by asking for another code.
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(CustomerOtpStatus.Locked, (await f.Service.ResendAsync(Caller, sent.ChallengeId)).Status);
    }

    [Fact]
    public async Task Verify_AfterTheExpiry_IsRefused_EvenWithTheRightCode()
    {
        var f = new OtpServiceFixture();
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        f.Clock.Advance(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1)));
        var result = await f.Service.VerifyAsync(Caller, sent.ChallengeId, "111111");

        Assert.Equal(CustomerOtpStatus.Expired, result.Status);
        Assert.Equal(CustomerOtpCodes.OtpExpired, result.Code);
        Assert.Null(f.Store.Single().VerificationSessionId);
    }

    [Fact]
    public async Task Verify_AtExactlyTheExpiryInstant_StillWorks()
    {
        var f = new OtpServiceFixture();
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        f.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(CustomerOtpStatus.Verified, (await f.Service.VerifyAsync(Caller, sent.ChallengeId, "111111")).Status);
    }

    [Fact]
    public async Task Verify_IsSingleUse_AReusedCodeCreatesNoSecondSession()
    {
        var f = new OtpServiceFixture();
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");
        var first = await f.Service.VerifyAsync(Caller, sent.ChallengeId, "111111");

        var again = await f.Service.VerifyAsync(Caller, sent.ChallengeId, "111111");

        Assert.Equal(CustomerOtpStatus.Verified, first.Status);
        Assert.Equal(CustomerOtpStatus.AlreadyUsed, again.Status);
        Assert.Equal(CustomerOtpCodes.OtpAlreadyUsed, again.Code);
        Assert.Null(again.Session);
        Assert.Equal(first.Session!.VerificationSessionId, f.Store.Single().VerificationSessionId);
    }

    [Fact]
    public async Task Verify_ByAnotherAccount_OrForAnUnknownChallenge_LooksIdentical_AndSpendsNothing()
    {
        var f = new OtpServiceFixture();
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        var other = await f.Service.VerifyAsync(Guid.NewGuid(), sent.ChallengeId, "111111");
        var unknown = await f.Service.VerifyAsync(Caller, Guid.NewGuid(), "111111");

        Assert.Equal(CustomerOtpStatus.ChallengeNotFound, other.Status);
        Assert.Equal(CustomerOtpStatus.ChallengeNotFound, unknown.Status);
        Assert.Equal(OtpChallengeStatus.Pending, f.Store.Single().Status);
        Assert.Equal(0, f.Store.Single().FailedAttempts); // another account cannot burn the customer's attempts either
    }

    [Theory]
    [InlineData(null, "111111")]
    [InlineData("", "111111")]
    public async Task Verify_RequiresBothFields(string? challenge, string? code)
    {
        var f = new OtpServiceFixture();
        var id = challenge is null ? (Guid?)null : Guid.Empty;

        Assert.Equal(CustomerOtpStatus.InvalidRequest, (await f.Service.VerifyAsync(Caller, id, code)).Status);
        Assert.Equal(CustomerOtpStatus.InvalidRequest, (await f.Service.VerifyAsync(Caller, Guid.NewGuid(), "  ")).Status);
    }

    [Fact]
    public async Task ACodeFromAnotherChallenge_DoesNotVerify()
    {
        var f = new OtpServiceFixture();
        var a = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");
        var codeOfA = f.LastEmailedCode();
        var b = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1102");

        Assert.Equal(CustomerOtpStatus.InvalidCode, (await f.Service.VerifyAsync(Caller, b.ChallengeId, codeOfA)).Status);
        Assert.Equal(CustomerOtpStatus.Verified, (await f.Service.VerifyAsync(Caller, a.ChallengeId, codeOfA)).Status);
    }

    // ------------------------------------------------------- concurrency

    [Fact]
    public async Task ParallelWrongGuesses_EachSpendAnAttempt_NoneIsLost()
    {
        var f = new OtpServiceFixture();
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        // A parallel guess commits first; ours loses the write, re-reads, and is counted on top of it.
        f.UnitOfWork.LoseNextSave(() => f.Store.CommitFromAnotherRequest(sent.ChallengeId!.Value, c => c.Verify(false, f.Clock.GetUtcNow().UtcDateTime, 5)));

        var ours = await f.Service.VerifyAsync(Caller, sent.ChallengeId, "000000");

        Assert.Equal(CustomerOtpStatus.InvalidCode, ours.Status);
        Assert.Equal(2, f.Store.Single().FailedAttempts);   // theirs AND ours
        Assert.Equal(3, ours.AttemptsRemaining);
    }

    [Fact]
    public async Task TheSameCorrectCodeSubmittedTwiceAtOnce_SpendsItOnce_AndCreatesOneSession()
    {
        var f = new OtpServiceFixture();
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");
        var winnerSession = Guid.NewGuid();

        // The other request verifies and commits first; ours is rejected by the concurrency token and re-evaluated.
        f.UnitOfWork.LoseNextSave(() => f.Store.CommitFromAnotherRequest(sent.ChallengeId!.Value, c =>
        {
            c.Verify(true, f.Clock.GetUtcNow().UtcDateTime, 5);
            c.LinkSession(winnerSession);
        }));

        var ours = await f.Service.VerifyAsync(Caller, sent.ChallengeId, "111111");

        Assert.Equal(CustomerOtpStatus.AlreadyUsed, ours.Status);
        Assert.Null(ours.Session);
        Assert.Equal(winnerSession, f.Store.Single().VerificationSessionId);
    }

    // --------------------------------------- no other way to mint an OTP session

    [Fact]
    public async Task TheGenericSessionPath_RefusesOtp_AsAClaim()
    {
        var f = new OtpServiceFixture();
        var unit = f.Units.Seed("U1", "1", "P");
        var contact = f.Contacts.Seed(unit.UnitReferenceId, "C1", "Someone");
        var sessions = new VerificationSessionAppService(f.Sessions, f.Units, f.Contacts, f.UnitOfWork, f.Audit, f.Clock);

        var otp = await sessions.CreateAndConfirmAsync(
            Caller, new CreateVerificationSessionRequestDto(unit.UnitReferenceId, contact.ContactReferenceId, true, "Otp"), null);
        var manual = await sessions.CreateAndConfirmAsync(
            Caller, new CreateVerificationSessionRequestDto(unit.UnitReferenceId, contact.ContactReferenceId, true, "ManualAgentConfirmation"), null);

        Assert.Equal(VerificationSessionOutcome.OtpRequiresChallenge, otp.Outcome);
        Assert.Equal(VerificationSessionOutcome.Success, manual.Outcome); // existing manual-agent verification is untouched
        var manualSession = await f.Sessions.GetByIdAsync(manual.Response!.VerificationSessionId);
        Assert.Null(manualSession!.ProofChallengeId);
    }
}
