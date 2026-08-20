using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface IIntakeRecordRepository
{
    Task<IntakeRecord?> GetByIdAsync(long intakeRecordId, CancellationToken cancellationToken = default);

    /// <summary>Finds the IntakeRecord that was promoted into the given ticket — used at reconciliation time to recover the raw, as-spoken unit context (MVP-Data-Dictionary.md §2.9's RawUnitNumberEntered) a provisional ticket itself does not store.</summary>
    Task<IntakeRecord?> GetByLinkedTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(IntakeRecord intakeRecord, CancellationToken cancellationToken = default);
}
