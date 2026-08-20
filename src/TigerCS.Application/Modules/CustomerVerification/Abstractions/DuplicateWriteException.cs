namespace TigerCS.Application.Modules.CustomerVerification.Abstractions;

/// <summary>
/// Thrown by <see cref="ICustomerVerificationUnitOfWork.SaveChangesAsync"/> when a
/// save fails because it violated a real DB-level uniqueness constraint —
/// two concurrent requests racing to create a <c>VerificationSessions</c> row
/// with the same (AgentEmployeeId, IdempotencyKey) pair
/// (VerificationSessionAppService), or two concurrent lookups racing to
/// cache the same never-before-seen <c>UnitReferences.CrmUnitId</c> /
/// <c>ContactReferences.CrmContactId</c> (CrmUnitLookupAppService).
/// Deliberately narrower than "any failed save": the Infrastructure-layer
/// translation (CustomerVerificationUnitOfWork) only raises this for an actual
/// unique-constraint/duplicate-key violation, never for an unrelated
/// database update failure (a foreign-key violation, a required-field
/// violation, a genuine connectivity failure), which every caller of this
/// exception must be able to rely on before treating it as safely
/// recoverable-by-requery. Translated from the concrete EF Core exception in
/// Infrastructure so the Application layer never depends on EF Core types
/// directly.
/// </summary>
public sealed class DuplicateWriteException(Exception innerException)
    : Exception("A concurrent write violated a uniqueness constraint.", innerException);
