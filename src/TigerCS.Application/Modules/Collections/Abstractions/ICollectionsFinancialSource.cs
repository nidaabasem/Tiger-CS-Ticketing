using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Abstractions;

/// <summary>
/// The authoritative financial source — the only place a balance comes from.
///
/// <para>
/// <b>No production implementation exists yet.</b> Tiger CRM publishes one
/// endpoint today (<c>GET /TicketingSystem/GetBuyerByPhone</c>, buyer and
/// units only), no Oracle integration exists in TigerCS, and neither system
/// has published an account/instalment/payment contract. The
/// "Unavailable" provider therefore fails closed on every call, so every
/// Collections endpoint answers 503 rather than a number; the fixture
/// provider is refused outside Development/Testing. The contract this port
/// needs is written down in docs/Collections/Collections-Integration.md §2.
/// </para>
/// </summary>
public interface ICollectionsFinancialSource
{
    /// <summary>Which system answered — returned to clients so a figure is never anonymous.</summary>
    string SourceName { get; }

    /// <summary>
    /// The customer's accounts with their schedule, charges and payments.
    /// Null when the source does not know the customer.
    /// </summary>
    /// <exception cref="CollectionsFinancialSourceUnavailableException">The source could not answer.</exception>
    Task<IReadOnlyList<FinancialAccountSnapshot>?> GetCustomerAccountsAsync(string crmCustomerId, CancellationToken cancellationToken = default);

    /// <summary>One page of accounts that still have principal outstanding — the population reminders are chosen from.</summary>
    /// <exception cref="CollectionsFinancialSourceUnavailableException">The source could not answer.</exception>
    Task<FinancialAccountPage> ListAccountsWithOutstandingPrincipalAsync(int page, int pageSize, CancellationToken cancellationToken = default);
}

public sealed record FinancialAccountPage(IReadOnlyList<FinancialAccountSnapshot> Accounts, bool HasMore);

/// <summary>The financial source could not be reached, or no source is configured. Maps to 503; a balance is never guessed.</summary>
public sealed class CollectionsFinancialSourceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
