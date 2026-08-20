using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// Domain-level tests for the VerificationSession state machine
/// (MVP-ERD.md §2.24) — single-use, expiry, and ownership invariants, kept
/// real even though this phase's application layer collapses selection and
/// confirmation into one call (MVP-Implementation-Backlog.md S-07).
/// </summary>
public class VerificationSessionTests
{
    private static VerificationSession CreateSession(DateTime now, TimeSpan? lifetime = null) =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), unitReferenceId: 1, contactReferenceId: 1,
            "1204", "Tiger Tower A", "Tower A", "Residential", "Ahmed Al-Farsi", "ahmed@example.com",
            now, now.Add(lifetime ?? TimeSpan.FromMinutes(30)), idempotencyKey: null);

    [Fact]
    public void Confirm_WhileInProgress_TransitionsToConfirmed()
    {
        var now = DateTime.UtcNow;
        var session = CreateSession(now);

        session.Confirm(now, VerificationMethod.ManualAgentConfirmation);

        Assert.Equal(VerificationSessionStatus.Confirmed, session.Status);
        Assert.True(session.Confirmed);
        Assert.Equal(now, session.ConfirmedAtUtc);
        Assert.Equal(VerificationMethod.ManualAgentConfirmation, session.VerificationMethod);
    }

    /// <summary>
    /// Channel-neutral proof: Confirm() accepts any VerificationMethod, not
    /// just this pilot's ManualAgentConfirmation — a future digital channel
    /// needs no domain change (see VerificationSession's remarks).
    /// </summary>
    [Theory]
    [InlineData(VerificationMethod.ManualAgentConfirmation)]
    [InlineData(VerificationMethod.AuthenticatedDigitalUser)]
    [InlineData(VerificationMethod.Otp)]
    [InlineData(VerificationMethod.FaceToFaceDocumentCheck)]
    [InlineData(VerificationMethod.Other)]
    public void Confirm_AnyVerificationMethod_IsRecordedUnchanged(VerificationMethod method)
    {
        var now = DateTime.UtcNow;
        var session = CreateSession(now);

        session.Confirm(now, method);

        Assert.Equal(method, session.VerificationMethod);
    }

    [Fact]
    public void Confirm_AlreadyConfirmed_ThrowsNotInProgress()
    {
        var now = DateTime.UtcNow;
        var session = CreateSession(now);
        session.Confirm(now, VerificationMethod.ManualAgentConfirmation);

        Assert.Throws<VerificationSessionNotInProgressException>(() => session.Confirm(now, VerificationMethod.ManualAgentConfirmation));
    }

    [Fact]
    public void Confirm_AfterExpiry_ThrowsExpired()
    {
        var now = DateTime.UtcNow;
        var session = CreateSession(now, TimeSpan.FromMinutes(30));

        Assert.Throws<VerificationSessionExpiredException>(() => session.Confirm(now.AddMinutes(31), VerificationMethod.ManualAgentConfirmation));
    }

    [Fact]
    public void EffectiveStatus_PastExpiry_ReflectsExpiredEvenWithoutSweep()
    {
        var now = DateTime.UtcNow;
        var session = CreateSession(now, TimeSpan.FromMinutes(30));

        Assert.Equal(VerificationSessionStatus.Expired, session.EffectiveStatus(now.AddMinutes(31)));
        // The underlying persisted Status is untouched until an explicit sweep/mutation.
        Assert.Equal(VerificationSessionStatus.InProgress, session.Status);
    }

    [Fact]
    public void MarkExpired_PastExpiry_MutatesStatus()
    {
        var now = DateTime.UtcNow;
        var session = CreateSession(now, TimeSpan.FromMinutes(30));

        session.MarkExpired(now.AddMinutes(31));

        Assert.Equal(VerificationSessionStatus.Expired, session.Status);
    }

    [Fact]
    public void MarkExpired_BeforeExpiry_DoesNothing()
    {
        var now = DateTime.UtcNow;
        var session = CreateSession(now, TimeSpan.FromMinutes(30));

        session.MarkExpired(now.AddMinutes(5));

        Assert.Equal(VerificationSessionStatus.InProgress, session.Status);
    }

    [Fact]
    public void Consume_WhileConfirmed_TransitionsToConsumed()
    {
        var now = DateTime.UtcNow;
        var session = CreateSession(now);
        session.Confirm(now, VerificationMethod.ManualAgentConfirmation);

        session.Consume(ticketId: 42, now);

        Assert.Equal(VerificationSessionStatus.Consumed, session.Status);
        Assert.Equal(42, session.ConsumedByTicketId);
        Assert.Equal(now, session.ConsumedAtUtc);
    }

    [Fact]
    public void Consume_NotYetConfirmed_ThrowsNotConfirmed()
    {
        var now = DateTime.UtcNow;
        var session = CreateSession(now);

        Assert.Throws<VerificationSessionNotConfirmedException>(() => session.Consume(1, now));
    }

    [Fact]
    public void Consume_Twice_ThrowsAlreadyConsumed_SingleUseEnforced()
    {
        var now = DateTime.UtcNow;
        var session = CreateSession(now);
        session.Confirm(now, VerificationMethod.ManualAgentConfirmation);
        session.Consume(1, now);

        Assert.Throws<VerificationSessionAlreadyConsumedException>(() => session.Consume(2, now));
    }

    [Fact]
    public void Consume_AfterExpiry_ThrowsExpired()
    {
        var now = DateTime.UtcNow;
        var session = CreateSession(now, TimeSpan.FromMinutes(30));
        session.Confirm(now, VerificationMethod.ManualAgentConfirmation);

        Assert.Throws<VerificationSessionExpiredException>(() => session.Consume(1, now.AddMinutes(31)));
    }

    [Fact]
    public void IsOwnedBy_DifferentEmployee_ReturnsFalse()
    {
        var now = DateTime.UtcNow;
        var owner = Guid.NewGuid();
        var session = new VerificationSession(
            Guid.NewGuid(), owner, 1, 1, "1204", null, null, null, null, null,
            now, now.AddMinutes(30), null);

        Assert.True(session.IsOwnedBy(owner));
        Assert.False(session.IsOwnedBy(Guid.NewGuid()));
    }

    [Fact]
    public void Constructor_ExpiresAtUtcNotAfterCreatedAtUtc_Throws()
    {
        var now = DateTime.UtcNow;

        Assert.Throws<ArgumentException>(() => new VerificationSession(
            Guid.NewGuid(), Guid.NewGuid(), 1, 1, null, null, null, null, null, null,
            now, now, null));
    }
}
