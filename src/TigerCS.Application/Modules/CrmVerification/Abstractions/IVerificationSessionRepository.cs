using TigerCS.Domain.Modules.CrmVerification;

namespace TigerCS.Application.Modules.CrmVerification.Abstractions;

/// <summary>Application-layer port over VerificationSession persistence; implemented in Infrastructure with EF Core.</summary>
public interface IVerificationSessionRepository
{
    Task<VerificationSession?> GetByIdAsync(Guid verificationSessionId, CancellationToken cancellationToken = default);

    /// <summary>Looks up a prior session by the same agent + client-supplied idempotency key (a double-submit replay).</summary>
    Task<VerificationSession?> GetByIdempotencyKeyAsync(
        Guid agentEmployeeId, string idempotencyKey, CancellationToken cancellationToken = default);

    Task AddAsync(VerificationSession session, CancellationToken cancellationToken = default);
}
