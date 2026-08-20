namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>
/// Thrown by <see cref="ITicketingUnitOfWork.SaveChangesAsync"/> when persisting
/// a <c>VerificationSession</c> consumption loses an optimistic-concurrency
/// race — two concurrent ticket-creation requests both read the same
/// <c>Confirmed</c> session before either committed, both passed the
/// in-memory <c>Consume()</c> check, and only one write can win
/// (MVP-ERD.md §2.24's single-use invariant, enforced here at the database
/// level via a concurrency token on <c>VerificationSessions.Status</c> —
/// see <c>TigerCsDbContext</c>'s supplemental configuration). The losing
/// caller's session is, by the time this is thrown, genuinely already
/// consumed by the winner — translated by
/// <c>TicketCreationAppService</c> to the same
/// <c>VerificationSessionAlreadyConsumed</c> outcome a sequential replay
/// would get, not a raw 500.
/// </summary>
public sealed class VerificationSessionConcurrentlyConsumedException(Exception innerException)
    : Exception("A concurrent request already consumed this verification session.", innerException);
