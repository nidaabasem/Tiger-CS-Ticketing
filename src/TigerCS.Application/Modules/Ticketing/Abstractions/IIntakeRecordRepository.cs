using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface IIntakeRecordRepository
{
    Task<IntakeRecord?> GetByIdAsync(long intakeRecordId, CancellationToken cancellationToken = default);

    /// <summary>Finds the IntakeRecord that was promoted into the given ticket — used at reconciliation time to recover the raw, as-spoken unit context (MVP-Data-Dictionary.md §2.9's RawUnitNumberEntered) a provisional ticket itself does not store.</summary>
    Task<IntakeRecord?> GetByLinkedTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every ticket id linked from an IntakeRecord recorded against this
    /// phone number — the Customer History fallback key
    /// (<c>CustomerHistoryAppService</c>) for a customer with no
    /// <c>Ticket.CrmBuyerCustomerId</c>, and the Genesys call-pickup lookup's
    /// recent tickets. Matched on <see cref="CustomerPhoneNumber.Normalize"/>'s
    /// canonical form, so the same number written differently ("+971 50…",
    /// "971…") is the same caller; the stored values are never rewritten. A
    /// number with no digits matches nothing.
    /// </summary>
    Task<IReadOnlyList<long>> ListLinkedTicketIdsByPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken = default);

    Task AddAsync(IntakeRecord intakeRecord, CancellationToken cancellationToken = default);
}
