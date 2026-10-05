namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// One customer financial account (a sale contract / payment plan for a unit)
/// exactly as the authoritative financial source reported it. TigerCS never
/// stores this and never edits it: it is read from the source for every
/// request and every reminder decision, so a figure TigerCS shows is always
/// the source's figure.
///
/// <para>
/// <b>The source owns allocation.</b> Each instalment and charge carries the
/// amount the source itself still considers outstanding — after the source
/// has applied payments, partial payments, waivers and reversals by its own
/// rules. TigerCS does not re-allocate payments against instalments and does
/// not subtract the payment history from anything: the history is display
/// only. That is what keeps an unverified payment proof (or a pending bank
/// transfer the source has not posted) from ever reducing a balance here.
/// </para>
/// </summary>
/// <param name="AccountId">The source's identifier for the account. Opaque to TigerCS.</param>
/// <param name="CrmCustomerId">The Tiger CRM customer the account belongs to.</param>
/// <param name="CrmUnitId">The Tiger CRM unit the account is for, when the source links one.</param>
/// <param name="UnitNumber">Display only.</param>
/// <param name="ProjectName">Display only.</param>
/// <param name="Currency">ISO 4217 code. Every amount on the account is in this currency; amounts in different currencies are never summed.</param>
/// <param name="AsOfUtc">When the source computed these figures.</param>
/// <param name="ReportedOutstandingPrincipal">The source's own total of unpaid principal, when it reports one. Used to cross-check the instalment schedule; never replaced by a TigerCS figure.</param>
/// <param name="Instalments">The principal schedule.</param>
/// <param name="Charges">Fines and fees.</param>
/// <param name="Payments">Payment history, posted and otherwise. Display only — see the type remarks.</param>
/// <param name="CustomerPhone">The contact number the source holds for reminders, when it holds one.</param>
/// <param name="CustomerEmail">The contact email the source holds for reminders, when it holds one.</param>
/// <param name="CustomerName">Display only.</param>
public sealed record FinancialAccountSnapshot(
    string AccountId,
    string CrmCustomerId,
    string? CrmUnitId,
    string? UnitNumber,
    string? ProjectName,
    string Currency,
    DateTime AsOfUtc,
    decimal? ReportedOutstandingPrincipal,
    IReadOnlyList<FinancialInstalment> Instalments,
    IReadOnlyList<FinancialCharge> Charges,
    IReadOnlyList<FinancialPayment> Payments,
    string? CustomerPhone = null,
    string? CustomerEmail = null,
    string? CustomerName = null);

/// <summary>One scheduled principal instalment.</summary>
/// <param name="InstalmentId">The source's identifier.</param>
/// <param name="Sequence">Its position in the plan, for display order.</param>
/// <param name="DueDate">The contractual due date (a calendar date in the business time zone).</param>
/// <param name="PrincipalAmount">The scheduled principal.</param>
/// <param name="PrincipalOutstanding">What the source says is still unpaid on it, after its own allocation of posted payments. Between 0 and <paramref name="PrincipalAmount"/>.</param>
public sealed record FinancialInstalment(
    string InstalmentId,
    int Sequence,
    DateOnly DueDate,
    decimal PrincipalAmount,
    decimal PrincipalOutstanding);

/// <summary>A fine or fee.</summary>
/// <param name="ChargeId">The source's identifier.</param>
/// <param name="Kind">"Fine", "Fee" or the source's own label. Display only.</param>
/// <param name="Description">Display only.</param>
/// <param name="Amount">The charged amount.</param>
/// <param name="Outstanding">What the source says is still unpaid, after waivers and payments.</param>
/// <param name="DueDate">When it is payable from. Null means payable now.</param>
/// <param name="IsPayable">False when the source has the charge on hold (disputed, pending waiver approval). A held charge is never in "amount due now".</param>
public sealed record FinancialCharge(
    string ChargeId,
    string Kind,
    string? Description,
    decimal Amount,
    decimal Outstanding,
    DateOnly? DueDate,
    bool IsPayable);

/// <summary>A payment as the source records it.</summary>
/// <param name="PaymentId">The source's identifier.</param>
/// <param name="ReceivedOn">When the money was received or the proof submitted.</param>
/// <param name="PostedOn">When the source posted it to the account. Null while unposted.</param>
/// <param name="Amount">The amount.</param>
/// <param name="Method">"BankTransfer", "Cheque", … — display only.</param>
/// <param name="Reference">The source's or bank's reference — display only.</param>
/// <param name="Status">Whether the source has posted it.</param>
/// <param name="ReceiptAvailable">True only when the source exposes a verified receipt document for it.</param>
public sealed record FinancialPayment(
    string PaymentId,
    DateOnly ReceivedOn,
    DateOnly? PostedOn,
    decimal Amount,
    string? Method,
    string? Reference,
    FinancialPaymentStatus Status,
    bool ReceiptAvailable = false);

public enum FinancialPaymentStatus
{
    /// <summary>Posted by the source. Already reflected in the outstanding amounts the source reports.</summary>
    Posted = 1,

    /// <summary>Proof submitted, or money received but not yet verified. Never reduces anything.</summary>
    PendingVerification = 2,

    /// <summary>Reversed (a bounced cheque, a recalled transfer). Never reduces anything.</summary>
    Reversed = 3,

    /// <summary>Rejected by verification. Never reduces anything.</summary>
    Rejected = 4
}
