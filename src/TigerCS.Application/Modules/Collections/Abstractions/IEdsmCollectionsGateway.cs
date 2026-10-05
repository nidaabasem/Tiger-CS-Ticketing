using System.Globalization;
using System.Text.RegularExpressions;

namespace TigerCS.Application.Modules.Collections.Abstractions;

/// <summary>
/// EDSM (external-data-source-manager), reached on PACT's base URL with the
/// <c>PactApi</c> X-API-KEY. Contract: docs/Collections/EDSM_Collections_Contract.md
/// (static analysis of the EDSM source). Only <b>read-only</b> routes are exposed:
/// <list type="bullet">
/// <item><c>GET v1/reports/payment-summary?TenantId=&amp;CompanyId=</c></item>
/// <item><c>GET v1/reports/payment-transactions?Mobile=&amp;TenantId=&amp;CompanyId=&amp;TransactionTypeId=1|2|3</c>.
/// Type 4 ("All") is deliberately not offered: for rented companies it calls
/// <c>GetCustomerContractsAsync</c>, which writes to EDSM's databases (contract §8.3).</item>
/// <item><c>GET v1/due-installments?CompanyId=&amp;FromDate=&amp;ToDate=</c> (company-wide; filtered here by tenant)</item>
/// </list>
/// Outcome-wrapped: expected failures never throw.
/// </summary>
public interface IEdsmCollectionsGateway
{
    /// <summary>"Unavailable", "Pact" or "Fixture" — echoed to callers so fixture data is always labelled.</summary>
    string SourceName { get; }

    Task<EdsmResult<EdsmPaymentSummary>> GetPaymentSummaryAsync(int companyId, string tenantId, CancellationToken cancellationToken = default);

    Task<EdsmResult<EdsmPaymentTransactions>> GetPaymentTransactionsAsync(
        int companyId, string tenantId, string mobile, EdsmTransactionType type, CancellationToken cancellationToken = default);

    Task<EdsmResult<IReadOnlyList<EdsmDueInstallment>>> GetDueInstallmentsAsync(
        int companyId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default);
}

/// <summary>EDSM's <c>TransactionTypeEnum</c>, without 4 ("All") — see <see cref="IEdsmCollectionsGateway"/>.</summary>
public enum EdsmTransactionType
{
    Paid = 1,
    Due = 2,
    Outstanding = 3
}

public enum EdsmOutcome
{
    Success,

    /// <summary>Envelope with <c>status</c> 400 and a <c>title</c>, e.g. "Company not supported".</summary>
    BusinessRuleRejected,

    /// <summary>ASP.NET ProblemDetails 400 ("One or more validation errors occurred.") — a malformed request.</summary>
    ValidationRejected,

    /// <summary>401 "API Key was not provided." or 403 "Unauthorized client." (plain text) — configuration.</summary>
    Unauthorized,

    /// <summary>A 200 that is not the documented envelope or payload.</summary>
    InvalidResponse,

    /// <summary>500 (no envelope), an unexpected status, timeout, unreachable, or not configured.</summary>
    Unavailable,

    /// <summary>TigerCS did not call: EDSM does not support this company on this route.</summary>
    NotSupported
}

/// <summary>One EDSM call's result. <paramref name="Title"/> is the envelope title (or plain-text body) when EDSM gave one.</summary>
public sealed record EdsmResult<T>(EdsmOutcome Outcome, T? Value = default, string? Title = null, string? Message = null)
    where T : class
{
    public static EdsmResult<T> Ok(T value) => new(EdsmOutcome.Success, value);

    public static EdsmResult<T> Fail(EdsmOutcome outcome, string message, string? title = null) => new(outcome, null, title, message);
}

/// <summary>Owned = buyers (sale contracts); Rented = tenants (leases). The payment-summary formulas differ (contract §3.4, §3.5).</summary>
public enum EdsmBusinessModel
{
    Owned,
    Rented
}

/// <summary>One EDSM company (contract §2.1, <c>CompanyEnum</c>) and which routes support it (§2.2).</summary>
public sealed record EdsmCompany(int CompanyId, string Name, EdsmBusinessModel Model, bool SupportsDueInstallments);

/// <summary>The authoritative company list — EDSM's <c>CompanyEnum</c> (contract §2.1).</summary>
public static class EdsmCompanies
{
    public static readonly IReadOnlyList<EdsmCompany> All =
    [
        new(4, "Tiger Group Dubai", EdsmBusinessModel.Owned, SupportsDueInstallments: true),
        new(32, "Tiger Group Sharjah", EdsmBusinessModel.Owned, SupportsDueInstallments: true),
        new(25, "Hirmas Dubai", EdsmBusinessModel.Rented, SupportsDueInstallments: true),
        new(7, "Alsabeel Sharjah", EdsmBusinessModel.Rented, SupportsDueInstallments: true),
        new(20, "Alsabeel Sharjah Trio 3", EdsmBusinessModel.Rented, SupportsDueInstallments: false),
    ];

    public static EdsmCompany? Find(int companyId) => All.FirstOrDefault(c => c.CompanyId == companyId);
}

/// <summary>The five pre-formatted strings of <c>PaymentsSummaryOutputModel</c>, each with its parse status.</summary>
public sealed record EdsmPaymentSummary(
    EdsmAmount TotalAmount,
    EdsmAmount PaidAmount,
    EdsmAmount DueAmount,
    EdsmAmount OutstandingAmount,
    EdsmAmount LateFines);

/// <summary>
/// <c>PaymentTransactionsOutputModel</c>. Totals are deliberately not carried:
/// for rented companies the Paid total mixes refunds and the Due total has a
/// fee-allocation defect (contract §5.5), so nothing is derived from them.
/// </summary>
public sealed record EdsmPaymentTransactions(IReadOnlyList<EdsmTransaction> Items);

/// <summary>One transaction row. <see cref="Date"/> is <c>dd-MMM-yyyy</c> in EDSM's culture; <c>""</c> for opening-balance/contract rows.</summary>
public sealed record EdsmTransaction(
    decimal? Amount,
    EdsmAmount FormattedAmount,
    string? DateRaw,
    DateOnly? Date,
    string? ChequeNumber,
    int? TransactionTypeId,
    int? PaymentTypeId);

/// <summary>One due-installments row (contract §4.3). <see cref="Status"/> is EDSM's raw value — its meaning is UNVERIFIED.</summary>
public sealed record EdsmDueInstallment(
    int CompanyId,
    long TenantId,
    int? UnitId,
    string? VoucherNumber,
    string? ChequeNumber,
    DateOnly? ChequeDueDate,
    decimal? Amount,
    string? Status);

public enum EdsmAmountStatus
{
    /// <summary>Parsed unambiguously; <see cref="EdsmAmount.Value"/> is set.</summary>
    Provided,

    /// <summary>The property was absent, or JSON null.</summary>
    Missing,

    /// <summary>The property was an empty/whitespace string.</summary>
    Empty,

    /// <summary>Present but not in EDSM's <c>#,##0.00</c> format for the configured culture, or not a JSON string.</summary>
    Unreadable,

    /// <summary>No EDSM number culture is configured, so no formatted string is read (contract §7: host culture UNVERIFIED).</summary>
    FormatNotConfigured
}

/// <summary>
/// One EDSM monetary value. <see cref="Value"/> is null unless
/// <see cref="Status"/> is <see cref="EdsmAmountStatus.Provided"/> — never a
/// substituted zero. <see cref="Raw"/> is exactly what EDSM sent.
/// </summary>
public sealed record EdsmAmount(EdsmAmountStatus Status, decimal? Value, string? Raw)
{
    public static readonly EdsmAmount Missing = new(EdsmAmountStatus.Missing, null, null);
}

/// <summary>
/// EDSM formats every amount with <c>ToString("#,##0.00")</c> in its host's
/// <c>CurrentCulture</c> (contract §7): group separators, exactly two decimals,
/// a leading negative sign, no currency. The host culture is UNVERIFIED, so it
/// is configured explicitly (<c>CollectionsSource:EdsmNumberCulture</c>); with
/// none configured nothing is read. Only strings in exactly that shape for
/// that culture are accepted.
/// </summary>
public static class EdsmAmountParser
{
    public const string AedSuffix = " AED";

    /// <summary>Null when <paramref name="cultureName"/> is blank, unknown, or not 3-digit grouped (where <c>#,##0.00</c> would be ambiguous).</summary>
    public static CultureInfo? ResolveCulture(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return null;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName.Trim());
            return culture.NumberFormat.NumberGroupSizes is [3] ? culture : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <param name="raw">The formatted string.</param>
    /// <param name="culture">The verified EDSM host culture, or null when not configured.</param>
    /// <param name="allowAedSuffix">Only for rented Due/Fees <c>formattedAmount</c> rows in payment-transactions (contract §5.4).</param>
    public static EdsmAmount Parse(string? raw, CultureInfo? culture, bool allowAedSuffix = false)
    {
        if (raw is null)
        {
            return EdsmAmount.Missing;
        }

        if (raw.Trim().Length == 0)
        {
            return new EdsmAmount(EdsmAmountStatus.Empty, null, raw);
        }

        if (culture is null)
        {
            return new EdsmAmount(EdsmAmountStatus.FormatNotConfigured, null, raw);
        }

        var text = raw;
        if (allowAedSuffix && text.EndsWith(AedSuffix, StringComparison.Ordinal))
        {
            text = text[..^AedSuffix.Length];
        }

        var nf = culture.NumberFormat;
        var pattern = "^" + Regex.Escape(nf.NegativeSign) + "?[0-9]{1,3}(" + Regex.Escape(nf.NumberGroupSeparator) + "[0-9]{3})*"
            + Regex.Escape(nf.NumberDecimalSeparator) + "[0-9]{2}$";
        if (!Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant))
        {
            return new EdsmAmount(EdsmAmountStatus.Unreadable, null, raw);
        }

        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, nf, out var value)
            ? new EdsmAmount(EdsmAmountStatus.Provided, value, raw)
            : new EdsmAmount(EdsmAmountStatus.Unreadable, null, raw);
    }

    /// <summary>EDSM's raw doubles carry binary noise (1234.5600000000001); rounded half-away to 2 dp as the contract recommends.</summary>
    public static decimal RoundRaw(double value) => Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);
}
