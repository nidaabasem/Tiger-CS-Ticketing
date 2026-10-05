using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Abstractions;

/// <summary>
/// The authoritative financial source — the only place a balance comes from.
///
/// <para>
/// <b>The source is EDSM</b>, which already has a payment function. Neither
/// the EDSM client nor that function's contract (name, request parameters,
/// response fields, identifier keys) is in this repository, so no EDSM
/// adapter exists yet and none is guessed at: the "Unavailable" provider
/// fails closed on every call and every financial route answers 503. The
/// fixture provider is refused outside Development/Testing.
/// docs/Collections/Collections-Integration.md §2 lists exactly what is needed
/// to write the EDSM adapter behind this port.
/// </para>
///
/// <para>
/// An adapter must return the source's own figures: remaining principal per
/// instalment after the source's allocation, penalties and fees with their
/// payable state, applied credits, and posted payments with allocations.
/// A field the source does not provide must surface as invalid/unavailable —
/// never as zero.
/// </para>
/// </summary>
public interface ICollectionsFinancialSource
{
    /// <summary>Which system answered — returned in responses so a figure is never anonymous.</summary>
    string SourceName { get; }

    /// <summary>The customer's finance accounts. Null when the source does not know the customer.</summary>
    /// <exception cref="CollectionsFinancialSourceUnavailableException">The source could not answer.</exception>
    Task<IReadOnlyList<FinancialAccountSnapshot>?> GetCustomerAccountsAsync(long crmCustomerId, CancellationToken cancellationToken = default);

    /// <summary>One page of accounts with principal outstanding — the population reminder candidates are chosen from.</summary>
    /// <exception cref="CollectionsFinancialSourceUnavailableException">The source could not answer.</exception>
    Task<FinancialAccountPage> ListAccountsWithOutstandingPrincipalAsync(int page, int pageSize, CancellationToken cancellationToken = default);
}

public sealed record FinancialAccountPage(IReadOnlyList<FinancialAccountSnapshot> Accounts, bool HasMore);

/// <summary>The financial source could not be reached, or none is configured. Maps to 503 FinanceUnavailable; a balance is never guessed.</summary>
public sealed class CollectionsFinancialSourceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
