using System.Globalization;
using System.Text.RegularExpressions;

namespace TigerCS.Application.Modules.Collections.Abstractions;

/// <summary>
/// EDSM's payment summary, read through PACT's existing API:
/// <c>GET v1/reports/payment-summary?CompanyId={companyId}&amp;TenantId={tenantId}</c>
/// with the <c>PactApi</c> base URL and <c>X-API-KEY</c>. Its payload is EDSM's
/// <c>PaymentsSummaryOutputModel</c>: five monetary <b>strings</b> —
/// TotalAmount, PaidAmount, DueAmount, OutstandingAmount, LateFines.
///
/// <para>
/// What this port deliberately does NOT claim (docs/Collections/Collections-Integration.md §2):
/// the business definition of each field (e.g. whether OutstandingAmount is
/// overdue principal, or whether DueAmount already includes LateFines), the
/// currency, a source as-of time, or any instalment/due-date breakdown — the
/// model carries none of them. Every value is passed through as EDSM sent it,
/// parsed only when it is unambiguous, and never added to another.
/// </para>
///
/// <para>Outcome-wrapped like <c>IPactCustomerLookupGateway</c>: expected failures never throw.</para>
/// </summary>
public interface IEdsmPaymentSummaryGateway
{
    /// <summary>"Unavailable", "Pact" or "Fixture" — echoed to callers so fixture data is always labelled.</summary>
    string SourceName { get; }

    /// <summary>Mirrors the supplied <c>PactService.PaymentSummaryAsync(int companyId, string tenantId)</c>.</summary>
    Task<EdsmPaymentSummaryResult> GetPaymentSummaryAsync(int companyId, string tenantId, CancellationToken cancellationToken = default);
}

public enum EdsmPaymentSummaryOutcome
{
    Success,

    /// <summary>EDSM/PACT answered 404: no summary for this company/tenant pair.</summary>
    NotFound,

    /// <summary>PACT rejected the X-API-KEY (401/403) — configuration, not data.</summary>
    Unauthorized,

    /// <summary>A 200 whose body is not a recognisable payment summary, or a 400.</summary>
    InvalidResponse,

    /// <summary>Not configured, unreachable, timed out, or a server error.</summary>
    Unavailable
}

/// <summary>
/// One gateway call's result. <paramref name="Envelope"/> records which body
/// shape was recognised (<c>"data"</c> wrapper or <c>"bare"</c> object) so the
/// real response's envelope is visible in diagnostics until EDSM confirms it.
/// </summary>
public sealed record EdsmPaymentSummaryResult(
    EdsmPaymentSummaryOutcome Outcome,
    EdsmPaymentSummary? Summary = null,
    string? Envelope = null,
    string? Message = null)
{
    public static EdsmPaymentSummaryResult Success(EdsmPaymentSummary summary, string envelope) => new(EdsmPaymentSummaryOutcome.Success, summary, envelope);

    public static EdsmPaymentSummaryResult Failure(EdsmPaymentSummaryOutcome outcome, string message) => new(outcome, Message: message);
}

/// <summary>The five fields of EDSM's PaymentsSummaryOutputModel, each kept with its raw value and parse status.</summary>
public sealed record EdsmPaymentSummary(
    EdsmAmount TotalAmount,
    EdsmAmount PaidAmount,
    EdsmAmount DueAmount,
    EdsmAmount OutstandingAmount,
    EdsmAmount LateFines);

public enum EdsmAmountStatus
{
    /// <summary>Parsed unambiguously; <see cref="EdsmAmount.Value"/> is set.</summary>
    Provided,

    /// <summary>The property was absent, or JSON null.</summary>
    Missing,

    /// <summary>The property was an empty/whitespace string (the model's <c>string.Empty</c> default).</summary>
    Empty,

    /// <summary>Present but not in the accepted format, or not a JSON string. Never read as zero.</summary>
    Unreadable
}

/// <summary>
/// One EDSM monetary field. <see cref="Value"/> is null unless
/// <see cref="Status"/> is <see cref="EdsmAmountStatus.Provided"/> — an
/// empty, missing or unreadable value is never turned into zero.
/// <see cref="Raw"/> keeps exactly what EDSM sent, for diagnosis.
/// </summary>
public sealed record EdsmAmount(EdsmAmountStatus Status, decimal? Value, string? Raw)
{
    public static readonly EdsmAmount Missing = new(EdsmAmountStatus.Missing, null, null);
}

/// <summary>
/// The string formats an EDSM amount may be accepted in. EDSM's own
/// formatting has not been confirmed, so the default accepts only the one
/// form with a single reading.
/// </summary>
public enum EdsmAmountFormat
{
    /// <summary>Digits with an optional '.' fraction and optional leading '-': <c>1234.50</c>. Anything else is Unreadable.</summary>
    PlainInvariant = 0,

    /// <summary>
    /// Also accepts ',' thousands groups of exactly three digits: <c>1,234.50</c>.
    /// Enable only once EDSM confirms ',' is a group separator (not a decimal comma).
    /// </summary>
    GroupedInvariant = 1
}

/// <summary>Strict EDSM amount parsing — see <see cref="EdsmAmountFormat"/>.</summary>
public static partial class EdsmAmountParser
{
    public static EdsmAmount Parse(string? raw, EdsmAmountFormat format)
    {
        if (raw is null)
        {
            return EdsmAmount.Missing;
        }

        var text = raw.Trim();
        if (text.Length == 0)
        {
            return new EdsmAmount(EdsmAmountStatus.Empty, null, raw);
        }

        var accepted = PlainPattern().IsMatch(text)
            || (format == EdsmAmountFormat.GroupedInvariant && GroupedPattern().IsMatch(text));
        if (!accepted)
        {
            return new EdsmAmount(EdsmAmountStatus.Unreadable, null, raw);
        }

        return decimal.TryParse(text.Replace(",", string.Empty, StringComparison.Ordinal),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? new EdsmAmount(EdsmAmountStatus.Provided, value, raw)
            : new EdsmAmount(EdsmAmountStatus.Unreadable, null, raw);
    }

    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainPattern();

    [GeneratedRegex(@"^-?[0-9]{1,3}(,[0-9]{3})+(\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupedPattern();
}
