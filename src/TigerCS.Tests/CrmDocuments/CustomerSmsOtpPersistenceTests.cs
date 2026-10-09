using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Infrastructure.Audit;
using TigerCS.Infrastructure.Modules.CustomerVerification.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Integrations.Modules.SmsIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// The SMS channel against the REAL EF model on a real relational engine (SQLite), using the real repositories and the real
/// <see cref="CustomerOtpAppService"/>: what is persisted, that rows created before SMS existed still read as email, and that two
/// verifications of the same SMS code racing for real leave exactly one session. (<b>No SMS is sent: the sender is the fake.</b>)
/// </summary>
public sealed class CustomerSmsOtpPersistenceTests : IDisposable
{
    private const string CrmMobile = "+971 50 999 8888";
    private static readonly DateTime Now = new(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc);

    private readonly DashboardSqliteFixture _db = new();
    private readonly FakeCrmBuyerLookupGateway _buyers = new();
    private readonly FakeSmsSender _sms = new(Microsoft.Extensions.Options.Options.Create(new SmsOptions()), NullLogger<FakeSmsSender>.Instance);

    public CustomerSmsOtpPersistenceTests() =>
        _buyers.Returns(CrmBuyerLookupResult.Success([new CrmBuyerMatchDto(
            new CrmCustomerDto(9001, "Ahmed Ali", null, CrmMobile, "ahmed.ali@example.com"),
            [OtpServiceFixture.Unit(12345, 1101, "1205", "Tiger Sky Tower"), OtpServiceFixture.Unit(12346, 1102, "1403", "Tiger Sky Tower")])]));

    public void Dispose() => _db.Dispose();

    private sealed class RaceOnFirstSave(Func<Task> otherRequest) : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await otherRequest();
            }

            return result;
        }
    }

    private CustomerOtpAppService Build(TigerCsDbContext context, params string[] codes)
    {
        var time = new DashboardSqliteFixture.FixedTimeProvider(Now);
        var units = new UnitReferenceRepository(context);
        var contacts = new ContactReferenceRepository(context);
        var uow = new CustomerVerificationUnitOfWork(context);
        var audit = new AuditEntryWriter(context, time);
        var options = new CrmDocumentOptions { Enabled = true, OtpCodePepper = "test-pepper", OtpSmsEnabled = true };
        return new CustomerOtpAppService(
            options, new CrmBuyerLookupAppService(_buyers, NullLogger<CrmBuyerLookupAppService>.Instance),
            new CrmBuyerVerificationCache(units, contacts, uow, time), new CustomerOtpChallengeRepository(context), units, contacts,
            new VerificationSessionAppService(new VerificationSessionRepository(context), units, contacts, uow, audit, time),
            new FixedCodes(codes), new FakeEmailSender(), _sms, CustomerNotificationPolicy.EnabledDefault, uow, audit, time,
            NullLogger<CustomerOtpAppService>.Instance);
    }

    [Fact]
    public async Task AnSmsChallenge_PersistsItsChannelLanguageAndDeliveryState_AndTheBuyerContactIsCachedAsCrmHoldsIt()
    {
        Guid challengeId;
        await using (var context = _db.CreateContext())
        {
            var sent = await Build(context, "246810").SendAsync(_db.CsAgentId, "+971501234567", "1102", "Sms", "ar");
            Assert.Equal(CustomerOtpStatus.CodeSent, sent.Status);
            challengeId = sent.ChallengeId!.Value;
        }

        await using var fresh = _db.CreateContext();
        var challenge = await fresh.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == challengeId);
        Assert.Equal((OtpChannel.Sms, "ar", OtpDeliveryState.Accepted), (challenge.Channel, challenge.Language, challenge.DeliveryState));
        Assert.Equal("+971******888", challenge.MaskedDestination);
        Assert.NotEqual("246810"u8.ToArray(), challenge.CodeHash);

        var contact = await fresh.ContactReferences.SingleAsync(c => c.ContactReferenceId == challenge.ContactReferenceId);
        Assert.Equal(("9001-1102", ContactType.Buyer, CrmMobile), (contact.CrmContactId, contact.ContactType, contact.ContactChannel));
        var unit = await fresh.UnitReferences.SingleAsync(u => u.UnitReferenceId == challenge.UnitReferenceId);
        Assert.Equal("1102", unit.CrmUnitId);
        Assert.Equal(unit.UnitReferenceId, contact.UnitReferenceId);
    }

    [Fact]
    public async Task ARowCreatedBeforeSmsExisted_ReadsAsEmail_WithTheMigrationDefaults()
    {
        await using var context = _db.CreateContext();
        var unit = new UnitReference("1101", "1205", "Tiger Sky Tower", null, null, Now);
        context.UnitReferences.Add(unit);
        await context.SaveChangesAsync();
        var contact = new ContactReference("9001-1101", unit.UnitReferenceId, "Ahmed", CrmMobile, ContactType.Buyer, null, Now);
        context.ContactReferences.Add(contact);
        await context.SaveChangesAsync();
        var id = Guid.NewGuid();

        // The columns a pre-SMS row has: no Channel / Language / DeliveryState at all.
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO CustomerOtpChallenges
              (CustomerOtpChallengeId, CallerEmployeeId, CrmCustomerId, CrmLeadId, UnitReferenceId, ContactReferenceId, MaskedDestination,
               Salt, CodeHash, Status, FailedAttempts, SendCount, CreatedAtUtc, LastSentAtUtc, ExpiresAtUtc)
            VALUES ({0}, {1}, 9001, 12345, {2}, {3}, 'a***@e***.com', X'00', X'00', 1, 0, 1, {4}, {4}, {4})
            """,
            id, _db.CsAgentId, unit.UnitReferenceId, contact.ContactReferenceId, Now);
        context.ChangeTracker.Clear();

        var row = await context.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == id);
        Assert.Equal((OtpChannel.Email, "en", OtpDeliveryState.NotSent), (row.Channel, row.Language, row.DeliveryState));
    }

    [Fact]
    public async Task TheSameSmsCodeVerifiedByTwoRequestsAtOnce_LeavesExactlyOneSession_AndTheLoserSeesItSpent()
    {
        // Request A sends the code. Requests B and C then both verify it; C's save is overtaken by B's complete verification.
        Guid challengeId;
        await using (var sender = _db.CreateContext())
        {
            challengeId = (await Build(sender, "135790").SendAsync(_db.CsAgentId, "+971501234567", "1101", "Sms")).ChallengeId!.Value;
        }

        CustomerOtpResult winner = null!;
        await using var requestB = _db.CreateContext();
        var serviceB = Build(requestB);
        var interceptor = new RaceOnFirstSave(async () => winner = await serviceB.VerifyAsync(_db.CsAgentId, challengeId, "135790"));
        await using var requestC = _db.CreateContext(interceptor);

        var loser = await Build(requestC).VerifyAsync(_db.CsAgentId, challengeId, "135790");

        Assert.Equal(CustomerOtpStatus.Verified, winner.Status);
        Assert.Equal(CustomerOtpStatus.AlreadyUsed, loser.Status);
        Assert.Null(loser.Session);

        await using var check = _db.CreateContext();
        var sessions = await check.VerificationSessions.Where(s => s.ProofChallengeId == challengeId).ToListAsync();
        var only = Assert.Single(sessions);
        Assert.Equal(winner.Session!.VerificationSessionId, only.VerificationSessionId);
        Assert.Equal((9001, 12345), (only.CrmBuyerCustomerId, only.CrmBuyerLeadId));
        var challenge = await check.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == challengeId);
        Assert.Equal((OtpChallengeStatus.Verified, only.VerificationSessionId), (challenge.Status, challenge.VerificationSessionId));
    }
}
