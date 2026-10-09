using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.CustomerVerification.Repositories;

public sealed class CustomerOtpChallengeRepository(TigerCsDbContext dbContext) : ICustomerOtpChallengeRepository
{
    public Task<CustomerOtpChallenge?> GetByIdAsync(Guid challengeId, CancellationToken cancellationToken = default) =>
        dbContext.CustomerOtpChallenges.FirstOrDefaultAsync(c => c.CustomerOtpChallengeId == challengeId, cancellationToken);

    public Task<CustomerOtpChallenge?> FindPendingAsync(
        Guid callerEmployeeId, int crmCustomerId, int crmLeadId, OtpChannel channel, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        dbContext.CustomerOtpChallenges
            .Where(c => c.CallerEmployeeId == callerEmployeeId && c.CrmCustomerId == crmCustomerId && c.CrmLeadId == crmLeadId && c.Channel == channel
                && c.Status == OtpChallengeStatus.Pending && c.ExpiresAtUtc >= nowUtc)
            .OrderByDescending(c => c.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<int> CountStartedSinceAsync(int crmCustomerId, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
        dbContext.CustomerOtpChallenges.CountAsync(c => c.CrmCustomerId == crmCustomerId && c.CreatedAtUtc >= sinceUtc, cancellationToken);

    public async Task AddAsync(CustomerOtpChallenge challenge, CancellationToken cancellationToken = default) =>
        await dbContext.CustomerOtpChallenges.AddAsync(challenge, cancellationToken);
}
