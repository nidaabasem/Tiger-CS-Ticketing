using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Integrations.Modules.CollectionsIntegration;

/// <summary>
/// The "CollectionsSource" configuration section. <see cref="Provider"/>:
/// "Unavailable" — the default, and the only value for UAT/Production until
/// the EDSM adapter exists — or "Fixture", deterministic sample data for
/// Development and the automated tests, refused anywhere else by
/// <see cref="CollectionsSourceSafety"/>.
/// </summary>
public sealed class CollectionsSourceOptions
{
    public const string SectionName = "CollectionsSource";

    public string Provider { get; set; } = "Unavailable";
}

public static class CollectionsSourceSafety
{
    public static readonly IReadOnlyCollection<string> FixtureAllowedEnvironments = ["Development", "Testing"];

    /// <summary>True when fixture balances would be served outside Development/Testing.</summary>
    public static bool IsUnsafe(string? provider, string environmentName) =>
        string.Equals(provider, "Fixture", StringComparison.OrdinalIgnoreCase)
        && !FixtureAllowedEnvironments.Contains(environmentName);
}

/// <summary>
/// What every real environment resolves today. <b>EDSM is the authoritative
/// financial source</b>, but its client and payment function are not in this
/// repository, so no adapter can be written without inventing its contract.
/// Every call fails closed with the port's own outage exception: the
/// Collections API answers <c>503 FinanceUnavailable</c> and the Payment tab
/// says the balance is unavailable — never a zero, never fixture data.
/// Replace with the EDSM adapter (docs/Collections/Collections-Integration.md §2).
/// </summary>
public sealed class UnavailableCollectionsFinancialSource(ILogger<UnavailableCollectionsFinancialSource> logger) : ICollectionsFinancialSource
{
    public const string Message =
        "Payment information is temporarily unavailable: the EDSM financial source is not yet connected to TigerCS.";

    public string SourceName => "Unavailable";

    public Task<IReadOnlyList<FinancialAccountSnapshot>?> GetCustomerAccountsAsync(long crmCustomerId, CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<FinancialAccountSnapshot>?>(Fail("customer accounts"));

    public Task<FinancialAccountPage> ListAccountsWithOutstandingPrincipalAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromException<FinancialAccountPage>(Fail("accounts with outstanding principal"));

    private CollectionsFinancialSourceUnavailableException Fail(string operation)
    {
        logger.LogWarning("Collections financial source requested ({Operation}) but the EDSM adapter is not connected; failing closed.", operation);
        return new CollectionsFinancialSourceUnavailableException(Message);
    }
}

/// <summary>
/// Deterministic sample accounts for Development and the automated tests —
/// <b>not real data, not EDSM data, and refused outside those environments</b>.
/// Dates are relative to the business date so every bucket is populated.
///
/// <list type="bullet">
///   <item><description><c>9001</c> — two units, three accounts: <c>ACC-9001-1204</c> in arrears with a partial payment, a payable penalty, a fee on hold, an applied credit and an unverified payment proof; <c>ACC-9001-0805</c> fully settled; <c>ACC-9001-1204-P</c> a second contract on unit 1204 (parking), never merged with the first.</description></item>
///   <item><description><c>9002</c> — one account whose schedule disagrees with the source's total (Inconsistent).</description></item>
///   <item><description>Any other id — unknown to the source.</description></item>
/// </list>
/// </summary>
public sealed class FixtureCollectionsFinancialSource(CollectionsClock clock) : ICollectionsFinancialSource
{
    public string SourceName => "Fixture (Development/Testing only — not real data)";

    public Task<IReadOnlyList<FinancialAccountSnapshot>?> GetCustomerAccountsAsync(long crmCustomerId, CancellationToken cancellationToken = default)
    {
        var accounts = All(clock.BusinessDate, clock.UtcNow).Where(a => a.CrmCustomerId == crmCustomerId).ToList();
        return Task.FromResult<IReadOnlyList<FinancialAccountSnapshot>?>(accounts.Count == 0 ? null : accounts);
    }

    public Task<FinancialAccountPage> ListAccountsWithOutstandingPrincipalAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var open = All(clock.BusinessDate, clock.UtcNow).Where(a => a.Instalments.Any(i => i.RemainingAmount > 0m)).ToList();
        var slice = open.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new FinancialAccountPage(slice, page * pageSize < open.Count));
    }

    public static IReadOnlyList<FinancialAccountSnapshot> All(DateOnly businessDate, DateTime asOfUtc)
    {
        var month = new DateOnly(businessDate.Year, businessDate.Month, 1);
        DateOnly Due(int monthOffset) => month.AddMonths(monthOffset).AddDays(9); // the 10th

        // ACC-9001-1204: 10,000/month on the 10th, from three months ago.
        var arrears = new List<FinancialInstalment>
        {
            new("INS-1204-01", Due(-3), 10_000m, 0m),
            new("INS-1204-02", Due(-2), 10_000m, 4_000m),   // partially paid, overdue more than a month
            new("INS-1204-03", Due(-1), 10_000m, 10_000m),
            new("INS-1204-04", Due(0), 10_000m, 10_000m),   // this month
            new("INS-1204-05", Due(1), 10_000m, 10_000m),
            new("INS-1204-06", Due(2), 10_000m, 10_000m),
        };

        var parking = new List<FinancialInstalment>
        {
            new("INS-1204P-01", Due(-1), 1_500m, 1_500m),
            new("INS-1204P-02", Due(1), 1_500m, 1_500m),
        };

        var settled = new List<FinancialInstalment>
        {
            new("INS-0805-01", Due(-2), 25_000m, 0m),
            new("INS-0805-02", Due(-1), 25_000m, 0m),
        };

        var mismatch = new List<FinancialInstalment>
        {
            new("INS-0310-01", Due(-1), 8_000m, 8_000m),
            new("INS-0310-02", Due(0), 8_000m, 8_000m),
        };

        return
        [
            new FinancialAccountSnapshot(
                "ACC-9001-1204", 9001, 9200, "Tiger Tower A", "1204", "AED", asOfUtc,
                ReportedOutstandingPrincipal: arrears.Sum(i => i.RemainingAmount),
                arrears,
                [
                    new FinancialCharge("PEN-1204-01", FinancialChargeType.Penalty, 500m, 500m, Due(-1), IsPayable: true),
                    new FinancialCharge("FEE-1204-01", FinancialChargeType.Fee, 250m, 250m, null, IsPayable: false),
                    new FinancialCharge("FEE-1204-02", FinancialChargeType.Fee, 300m, 300m, null, IsPayable: true),
                ],
                [
                    new FinancialPayment("PAY-1204-01", Due(-3), 10_000m, "BankTransfer", FinancialPaymentStatus.Posted, "RCT-20001", true,
                        [new FinancialPaymentAllocation("INS-1204-01", 10_000m)]),
                    // One receipt covering two units: part of it is allocated to the parking contract.
                    new FinancialPayment("PAY-1204-02", Due(-2).AddDays(3), 7_500m, "Cheque", FinancialPaymentStatus.Posted, "RCT-20002", true,
                        [new FinancialPaymentAllocation("INS-1204-02", 6_000m), new FinancialPaymentAllocation("INS-1204P-00", 1_500m, "ACC-9001-1204-P")]),
                    new FinancialPayment("PAY-1204-03", businessDate.AddDays(-1), 6_000m, "BankTransfer", FinancialPaymentStatus.PendingVerification, null, false, []),
                ],
                AppliedCreditAmount: 200m,
                CustomerPhone: "+971500000900", CustomerEmail: "buyer@example.test", CustomerName: "Test Buyer"),

            new FinancialAccountSnapshot(
                "ACC-9001-1204-P", 9001, 9200, "Tiger Tower A", "1204 (parking)", "AED", asOfUtc,
                ReportedOutstandingPrincipal: parking.Sum(i => i.RemainingAmount),
                parking, [], [],
                CustomerPhone: "+971500000900", CustomerEmail: "buyer@example.test", CustomerName: "Test Buyer"),

            new FinancialAccountSnapshot(
                "ACC-9001-0805", 9001, 9201, "Tiger Marina Residences", "0805", "AED", asOfUtc,
                ReportedOutstandingPrincipal: 0m,
                settled, [],
                [
                    new FinancialPayment("PAY-0805-01", Due(-2), 25_000m, "BankTransfer", FinancialPaymentStatus.Posted, "RCT-10001", true, [new FinancialPaymentAllocation("INS-0805-01", 25_000m)]),
                    new FinancialPayment("PAY-0805-02", Due(-1), 25_000m, "BankTransfer", FinancialPaymentStatus.Posted, "RCT-10002", true, [new FinancialPaymentAllocation("INS-0805-02", 25_000m)]),
                ],
                CustomerPhone: "+971500000900", CustomerEmail: "buyer@example.test", CustomerName: "Test Buyer"),

            new FinancialAccountSnapshot(
                "ACC-9002-0310", 9002, 9300, "Tiger Tower B", "0310", "AED", asOfUtc,
                ReportedOutstandingPrincipal: 12_000m, // disagrees with the 16,000 schedule
                mismatch, [], [],
                CustomerPhone: "+971500000901", CustomerName: "Mismatch Buyer"),
        ];
    }
}
