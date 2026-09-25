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

    public async Task<IReadOnlyList<long>> ListLinkedTicketIdsByPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        // A number with no digits is not an identity — without this guard a
        // blank number would match every intake that recorded none.
        var canonical = CustomerPhoneNumber.Normalize(phoneNumber);
        if (canonical.Length == 0)
        {
            return [];
        }

        // The SQL-side mirror of CustomerPhoneNumber.Normalize (kept identical
        // to TicketRepository's and CustomerDirectoryRepository's by
        // CustomerPhoneNumberTests), so "+971 50 123 4567" typed by an agent
        // and "+971501234567" reported by Genesys are the same caller.
        return await dbContext.IntakeRecords
            .Where(i => i.LinkedTicketId != null
                && i.PhoneNumber.Trim().Replace("+", "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("/", "") == canonical)
            .Select(i => i.LinkedTicketId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(IntakeRecord intakeRecord, CancellationToken cancellationToken = default) =>
        await dbContext.IntakeRecords.AddAsync(intakeRecord, cancellationToken);
}
