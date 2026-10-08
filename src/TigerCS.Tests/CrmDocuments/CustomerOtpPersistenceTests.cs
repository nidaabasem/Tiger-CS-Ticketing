using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Infrastructure.Modules.CustomerVerification.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// The OTP challenge's single-use and attempt-budget guarantees against the REAL
/// EF model on a real relational engine: they rest on concurrency tokens, which
/// no in-memory fake can demonstrate.
/// </summary>
public sealed class CustomerOtpPersistenceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);
    private readonly DashboardSqliteFixture _db = new();

    private Guid Seed()
    {
        using var context = _db.CreateContext();
        var unit = new UnitReference("1101", "1205", "Tiger Sky Tower", null, null, Now);
        context.UnitReferences.Add(unit);
        context.SaveChanges();
        var contact = new ContactReference("9001-1101", unit.UnitReferenceId, "Ahmed", "+971501234567", ContactType.Buyer, null, Now);
        context.ContactReferences.Add(contact);
        context.SaveChanges();

        var id = Guid.NewGuid();
        context.CustomerOtpChallenges.Add(new CustomerOtpChallenge(
            id, _db.CsAgentId, 9001, 12345, unit.UnitReferenceId, contact.ContactReferenceId, "a***@e***.com",
            new byte[16], new byte[32], Now, TimeSpan.FromMinutes(10)));
        context.SaveChanges();
        return id;
    }

    private static Task SaveAsync(TigerCsDbContext context) => new CustomerVerificationUnitOfWork(context).SaveChangesAsync();

    [Fact]
    public async Task ACodeCanBeSpentOnce_TheSecondWriterLosesAndSeesItSpent()
    {
        var id = Seed();
        await using var requestA = _db.CreateContext();
        await using var requestB = _db.CreateContext();
        var a = await requestA.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == id);
        var b = await requestB.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == id);

        // Both requests see a pending challenge and the right code.
        Assert.Equal(OtpVerifyOutcome.Verified, a.Verify(true, Now, 5));
        Assert.Equal(OtpVerifyOutcome.Verified, b.Verify(true, Now, 5));

        await SaveAsync(requestA);
        await Assert.ThrowsAsync<ConcurrentWriteException>(() => SaveAsync(requestB));

        // The loser discards its copy and re-reads: the code is spent.
        new CustomerVerificationUnitOfWork(requestB).DiscardPendingChanges();
        var reread = await requestB.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == id);
        Assert.Equal(OtpVerifyOutcome.AlreadyUsed, reread.Verify(true, Now, 5));
    }

    [Fact]
    public async Task ParallelWrongGuesses_CannotOutRunTheAttemptBudget()
    {
        var id = Seed();
        await using var requestA = _db.CreateContext();
        await using var requestB = _db.CreateContext();
        var a = await requestA.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == id);
        var b = await requestB.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == id);

        a.Verify(false, Now, 5);
        b.Verify(false, Now, 5);
        await SaveAsync(requestA);
        await Assert.ThrowsAsync<ConcurrentWriteException>(() => SaveAsync(requestB)); // not silently lost

        // B retries from the stored state, so its guess is counted ON TOP of A's.
        new CustomerVerificationUnitOfWork(requestB).DiscardPendingChanges();
        var reread = await requestB.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == id);
        reread.Verify(false, Now, 5);
        await SaveAsync(requestB);

        await using var verify = _db.CreateContext();
        Assert.Equal(2, (await verify.CustomerOtpChallenges.AsNoTracking().SingleAsync(c => c.CustomerOtpChallengeId == id)).FailedAttempts);
    }

    [Fact]
    public async Task AResend_AndAGuess_AtOnce_CannotBothWin()
    {
        var id = Seed();
        await using var resendRequest = _db.CreateContext();
        await using var guessRequest = _db.CreateContext();
        var resend = await resendRequest.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == id);
        var guess = await guessRequest.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == id);

        resend.RecordResend(new byte[16], new byte[32], Now.AddMinutes(2), TimeSpan.FromMinutes(10));
        guess.Verify(true, Now.AddMinutes(1), 5); // checked against the OLD code

        await SaveAsync(resendRequest);
        await Assert.ThrowsAsync<ConcurrentWriteException>(() => SaveAsync(guessRequest)); // the old code's success is not honoured
    }

    [Fact]
    public async Task OneSessionPerChallenge_IsEnforcedByTheDatabase()
    {
        var challengeId = Seed();
        await using var context = _db.CreateContext();
        var challenge = await context.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == challengeId);
        VerificationSession NewSession()
        {
            var s = new VerificationSession(
                Guid.NewGuid(), _db.CsAgentId, challenge.UnitReferenceId, challenge.ContactReferenceId, "1205", "P", null, null,
                "Ahmed", "+971501234567", Now, Now.AddMinutes(30), null);
            s.AttachOtpProof(challengeId, 9001, 12345);
            s.Confirm(Now, VerificationMethod.Otp);
            return s;
        }

        context.VerificationSessions.Add(NewSession());
        await context.SaveChangesAsync();
        context.VerificationSessions.Add(NewSession());

        // The filtered unique index is a SQL Server construct; SQLite honours the same filtered-unique definition.
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task ProofAndCrmBinding_RoundTripOnTheSession()
    {
        var challengeId = Seed();
        Guid sessionId;
        await using (var context = _db.CreateContext())
        {
            var challenge = await context.CustomerOtpChallenges.SingleAsync(c => c.CustomerOtpChallengeId == challengeId);
            var s = new VerificationSession(
                Guid.NewGuid(), _db.CsAgentId, challenge.UnitReferenceId, challenge.ContactReferenceId, "1205", "P", null, null,
                "Ahmed", "+971501234567", Now, Now.AddMinutes(30), null);
            s.AttachOtpProof(challengeId, 9001, 12345);
            s.Confirm(Now, VerificationMethod.Otp);
            context.VerificationSessions.Add(s);
            await context.SaveChangesAsync();
            sessionId = s.VerificationSessionId;
        }

        await using var read = _db.CreateContext();
        var stored = await read.VerificationSessions.AsNoTracking().SingleAsync(s => s.VerificationSessionId == sessionId);
        Assert.Equal(challengeId, stored.ProofChallengeId);
        Assert.Equal(9001, stored.CrmBuyerCustomerId);
        Assert.Equal(12345, stored.CrmBuyerLeadId);
    }

    public void Dispose() => _db.Dispose();
}
