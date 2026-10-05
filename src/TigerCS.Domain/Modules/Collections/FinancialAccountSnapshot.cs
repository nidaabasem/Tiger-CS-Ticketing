namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// One finance account (a sale contract / payment plan) exactly as the
/// authoritative financial source reported it. TigerCS never stores this and
/// never edits it: it is read from the source for every request and every
/// reminder decision, so a figure TigerCS shows is always the source's figure.
///
/// <para>
/// <b>The source owns allocation.</b> Each instalment and charge carries the
/// amount the source itself still considers outstanding — after the source
/// has applied payments, partial payments, credits, waivers and reversals by
/// its own rules. TigerCS does not re-allocate payments against instalments
/// and does not subtract the payment history from anything: the history is
/// display only. That is what keeps an unverified payment proof (or a pending
/// bank transfer the source has not posted) from ever reducing a balance.
/// </para>
/// </summary>
/// <param name="AccountId">The source's stable finance account / contract identifier.</param>
/// <param name="CrmCustomerId">The Tiger CRM customer the account belongs to.</param>
/// <param name="UnitId">The Tiger CRM unit the account is for, when the source links one. A unit may have several accounts; they are never merged.</param>
/// <param name="TowerName">Display only.</param>
/// <param name="UnitNumber">Display only.</param>
/// <param name="Currency">ISO 4217 code. Every amount on the account is in this currency; amounts in different currencies are never summed.</param>
/// <param name="AsOfUtc">When the source computed these figures.</param>
/// <param name="ReportedOutstandingPrincipal">The source's own total of unpaid principal, when it reports one. Used to cross-check the instalment schedule; never replaced by a TigerCS figure.</param>
/// <param name="Instalments">The principal schedule.</param>
/// <param name="Charges">Penalties and fees. <c>null</c> = the source did not report charges (unknown — never "none"); an empty list = it reported none.</param>
/// <param name="Payments">Payment history. Display only — see the type remarks.</param>
/// <param name="AppliedCreditAmount">A credit the source has applied against the amount due now but not allocated to a specific instalment or charge. Subtracted once, from amount due now only. <c>null</c> = the source did not report one (unknown — never zero).</param>
/// <param name="CustomerPhone">The approved reminder contact number the source holds, when it holds one.</param>
/// <param name="CustomerEmail">The approved reminder contact email the source holds, when it holds one.</param>
/// <param name="CustomerName">Display only.</param>
public sealed record FinancialAccountSnapshot(
    string AccountId,
    long CrmCustomerId,
    long? UnitId,
    string? TowerName,
    string? UnitNumber,
    string Currency,
    DateTime AsOfUtc,
    decimal? ReportedOutstandingPrincipal,
    IReadOnlyList<FinancialInstalment> Instalments,
    IReadOnlyList<FinancialCharge>? Charges,
    IReadOnlyList<FinancialPayment> Payments,
    decimal? AppliedCreditAmount = null,
    string? CustomerPhone = null,
    string? CustomerEmail = null,
    string? CustomerName = null);

/// <summary>One scheduled principal instalment.</summary>
/// <param name="InstalmentId">The source's identifier.</param>
/// <param name="DueDate">The contractual due date (a calendar date in the business time zone).</param>
/// <param name="ScheduledAmount">The scheduled principal.</param>
/// <param name="RemainingAmount">What the source says is still unpaid on it, after its own allocation of posted payments. Between 0 and <paramref name="ScheduledAmount"/>.</param>
public sealed record FinancialInstalment(
    string InstalmentId,
    DateOnly DueDate,
    decimal ScheduledAmount,
    decimal RemainingAmount);

public enum FinancialChargeType
{
    Penalty = 1,
    Fee = 2
}

/// <summary>A penalty or fee.</summary>
/// <param name="ChargeId">The source's identifier.</param>
/// <param name="Type">Penalty or fee — reported separately.</param>
/// <param name="Amount">The charged amount.</param>
/// <param name="Outstanding">What the source says is still unpaid, after waivers and payments.</param>
/// <param name="DueDate">When it is payable from. Null means payable now.</param>
/// <param name="IsPayable">False when the source has the charge on hold (disputed, pending waiver approval). A held charge is never in amount due now.</param>
public sealed record FinancialCharge(
    string ChargeId,
    FinancialChargeType Type,
    decimal Amount,
    decimal Outstanding,
    DateOnly? DueDate,
    bool IsPayable);

/// <summary>A payment as the source records it.</summary>
/// <param name="PaymentId">The source's identifier.</param>
/// <param name="PaymentDate">The payment's value date.</param>
/// <param name="Amount">The amount.</param>
/// <param name="Method">"BankTransfer", "Cheque", … — display only.</param>
/// <param name="Status">Whether the source has posted it. Only posted payments are ever shown as payment history.</param>
/// <param name="ReceiptNumber">The source's receipt reference, when one was issued.</param>
/// <param name="ReceiptAvailable">True when the source reports that a receipt document exists. Downloading it still needs a verified document API.</param>
/// <param name="Allocations">How the source allocated the payment, including to other accounts when one receipt covers several units.</param>
public sealed record FinancialPayment(
    string PaymentId,
    DateOnly PaymentDate,
    decimal Amount,
    string? Method,
    FinancialPaymentStatus Status,
    string? ReceiptNumber,
    bool ReceiptAvailable,
    IReadOnlyList<FinancialPaymentAllocation> Allocations);

/// <param name="InstalmentId">The instalment (or charge) the source applied the money to.</param>
/// <param name="Amount">The amount applied.</param>
/// <param name="AccountId">The account, when the allocation is to a different account than the payment's own.</param>
public sealed record FinancialPaymentAllocation(string InstalmentId, decimal Amount, string? AccountId = null);

public enum FinancialPaymentStatus
{
    /// <summary>Posted by the source. Already reflected in the outstanding amounts the source reports.</summary>
    Posted = 1,

    /// <summary>Proof submitted, or money received but not yet verified. Never shown as a payment, never reduces anything.</summary>
    PendingVerification = 2,

    /// <summary>Reversed (a bounced cheque, a recalled transfer).</summary>
    Reversed = 3,

    /// <summary>Rejected by verification.</summary>
    Rejected = 4
}
