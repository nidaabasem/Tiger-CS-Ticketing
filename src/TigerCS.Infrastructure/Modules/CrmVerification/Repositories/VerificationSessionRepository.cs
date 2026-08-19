using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.CrmVerification.Abstractions;
using TigerCS.Domain.Modules.CrmVerification;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.CrmVerification.Repositories;

public sealed class VerificationSessionRepository(TigerCsDbContext dbContext) : IVerificationSessionRepository
{
    public Task<VerificationSession?> GetByIdAsync(Guid verificationSessionId, CancellationToken cancellationToken = default) =>
        dbContext.VerificationSessions.FirstOrDefaultAsync(s => s.VerificationSessionId == verificationSessionId, cancellationToken);

    public Task<VerificationSession?> GetByIdempotencyKeyAsync(
        Guid agentEmployeeId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        dbContext.VerificationSessions.FirstOrDefaultAsync(
            s => s.AgentEmployeeId == agentEmployeeId && s.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task AddAsync(VerificationSession session, CancellationToken cancellationToken = default) =>
        await dbContext.VerificationSessions.AddAsync(session, cancellationToken);
}
