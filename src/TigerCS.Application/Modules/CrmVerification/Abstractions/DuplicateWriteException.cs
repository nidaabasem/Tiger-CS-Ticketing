namespace TigerCS.Application.Modules.CrmVerification.Abstractions;

/// <summary>
/// Thrown by <see cref="ICrmVerificationUnitOfWork.SaveChangesAsync"/> when a
/// save fails because it violated a uniqueness constraint — specifically, two
/// concurrent requests racing to create a <c>VerificationSessions</c> row
/// with the same (AgentEmployeeId, IdempotencyKey) pair. Translated from the
/// concrete EF Core exception in Infrastructure so the Application layer
/// never depends on EF Core types directly.
/// </summary>
public sealed class DuplicateWriteException(Exception innerException)
    : Exception("A concurrent write violated a uniqueness constraint.", innerException);
