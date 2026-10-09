using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.CustomerVerification.Otp;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.CustomerVerification.Repositories;

/// <summary>"Someone changed it first" (optimistic concurrency on <c>Version</c>) is an expected outcome, answered with false.</summary>
public sealed class OtpChallengeRepository(TigerCsDbContext dbContext) : IOtpChallengeRepository
{
    public Task<OtpChallenge?> GetAsync(Guid otpChallengeId, CancellationToken cancellationToken = default) =>
        dbContext.OtpChallenges.FirstOrDefaultAsync(c => c.OtpChallengeId == otpChallengeId, cancellationToken);

    public async Task AddAsync(OtpChallenge challenge, CancellationToken cancellationToken = default) =>
        await dbContext.OtpChallenges.AddAsync(challenge, cancellationToken);

    public Task<int> CountForCustomerSinceAsync(int crmCustomerId, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
        dbContext.OtpChallenges.CountAsync(c => c.CrmCustomerId == crmCustomerId && c.CreatedAtUtc >= sinceUtc, cancellationToken);

    public async Task<bool> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
