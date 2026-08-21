using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class IntakeRecordRepository(TigerCsDbContext dbContext) : IIntakeRecordRepository
{
    public Task<IntakeRecord?> GetByIdAsync(long intakeRecordId, CancellationToken cancellationToken = default) =>
        dbContext.IntakeRecords.FirstOrDefaultAsync(i => i.IntakeRecordId == intakeRecordId, cancellationToken);

    public Task<IntakeRecord?> GetByLinkedTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.IntakeRecords.FirstOrDefaultAsync(i => i.LinkedTicketId == ticketId, cancellationToken);

    public async Task AddAsync(IntakeRecord intakeRecord, CancellationToken cancellationToken = default) =>
        await dbContext.IntakeRecords.AddAsync(intakeRecord, cancellationToken);
}
