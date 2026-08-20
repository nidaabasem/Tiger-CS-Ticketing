using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface IIntakeRecordRepository
{
    Task<IntakeRecord?> GetByIdAsync(long intakeRecordId, CancellationToken cancellationToken = default);

    Task AddAsync(IntakeRecord intakeRecord, CancellationToken cancellationToken = default);
}
