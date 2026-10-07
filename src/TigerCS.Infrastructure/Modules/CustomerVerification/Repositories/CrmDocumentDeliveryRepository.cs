using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.CustomerVerification.Repositories;

public sealed class CrmDocumentDeliveryRepository(TigerCsDbContext dbContext) : ICrmDocumentDeliveryRepository
{
    public Task<CrmDocumentDeliveryRequest?> GetByKeyAsync(
        Guid callerEmployeeId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        dbContext.CrmDocumentDeliveryRequests.FirstOrDefaultAsync(
            r => r.CallerEmployeeId == callerEmployeeId && r.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<CrmDocumentDeliveryRequest?> FindRecentSentAsync(
        Guid verificationSessionId, CrmDocumentType type, string crmRecordId, DocumentDeliveryChannel channel,
        DateTime sinceUtc, CancellationToken cancellationToken = default) =>
        dbContext.CrmDocumentDeliveryRequests
            .Where(r => r.VerificationSessionId == verificationSessionId
                && r.DocumentType == type
                && r.CrmRecordId == crmRecordId
                && r.Channel == channel
                && r.Status == DocumentDeliveryStatus.Sent
                && r.SentAtUtc != null && r.SentAtUtc >= sinceUtc)
            .OrderByDescending(r => r.SentAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(CrmDocumentDeliveryRequest request, CancellationToken cancellationToken = default) =>
        await dbContext.CrmDocumentDeliveryRequests.AddAsync(request, cancellationToken);
}
