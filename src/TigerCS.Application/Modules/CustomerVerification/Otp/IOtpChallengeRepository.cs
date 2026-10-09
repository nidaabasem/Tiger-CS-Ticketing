using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CustomerVerification.Otp;

public interface IOtpChallengeRepository
{
    Task<OtpChallenge?> GetAsync(Guid otpChallengeId, CancellationToken cancellationToken = default);

    Task AddAsync(OtpChallenge challenge, CancellationToken cancellationToken = default);

    Task<int> CountForCustomerSinceAsync(int crmCustomerId, DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>True when saved; false when another request changed the challenge first (optimistic concurrency).</summary>
    Task<bool> SaveChangesAsync(CancellationToken cancellationToken = default);
}
