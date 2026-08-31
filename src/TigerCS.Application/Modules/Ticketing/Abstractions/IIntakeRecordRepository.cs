using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface IIntakeRecordRepository
{
    Task<IntakeRecord?> GetByIdAsync(long intakeRecordId, CancellationToken cancellationToken = default);

    /// <summary>Finds the IntakeRecord that was promoted into the given ticket — used at reconciliation time to recover the raw, as-spoken unit context (MVP-Data-Dictionary.md §2.9's RawUnitNumberEntered) a provisional ticket itself does not store.</summary>
    Task<IntakeRecord?> GetByLinkedTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every ticket id linked from an IntakeRecord recorded against this
    /// exact phone number — the Customer History fallback key
    /// (<c>CustomerHistoryAppService</c>) for a customer with no
    /// <c>Ticket.CrmBuyerCustomerId</c>. Exact, persisted-value match only —
    /// no phone normalization is applied beyond whatever was already stored
    /// at intake time (see <see cref="IntakeRecord"/>'s own remarks; this
    /// codebase has no existing phone-normalization convention to reuse).
    /// </summary>
    Task<IReadOnlyList<long>> ListLinkedTicketIdsByPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken = default);

    Task AddAsync(IntakeRecord intakeRecord, CancellationToken cancellationToken = default);
}
