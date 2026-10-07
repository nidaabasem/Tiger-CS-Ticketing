namespace TigerCS.Application.Modules.Collections;

/// <summary>
/// <c>CollectionsSource:NextPayment</c>. <b>Everything defaults to off.</b> A next
/// payment is computed for a company only when (1) <see cref="Enabled"/> and (2) the
/// EDSM owners' written confirmation of that company's due-installments semantics is
/// recorded in <see cref="Companies"/> and complete
/// (<see cref="NextPaymentSemanticsAttestation.MissingItems"/> is empty). Otherwise the
/// answer is an explicit <c>Unavailable</c> with the reasons — never a guess.
/// <c>CollectionsSource:DueInstallmentsEnabled</c> is a separate, unrelated switch and is
/// not read or changed by this feature.
/// </summary>
public sealed class CollectionsNextPaymentOptions
{
    /// <summary>Master switch. Off: every response says <c>FeatureDisabled</c> and EDSM is not called for this.</summary>
    public bool Enabled { get; set; }

    /// <summary>How far ahead of the business date to look for the next instalment, in days. Clamped 31–1830. When nothing is found through it the answer is <c>NoneWithinHorizon</c>, never "no payment".</summary>
    public int SearchHorizonDays { get; set; } = 730;

    /// <summary>Size of each due-installments date window (company-wide, unpaged), in days. Clamped 7–366. Windows overlap by one day so an inclusive/exclusive range boundary can never hide a row.</summary>
    public int WindowDays { get; set; } = 92;

    /// <summary>Per EDSM company id: the confirmed semantics. Absent = not confirmed = unavailable.</summary>
    public Dictionary<int, NextPaymentSemanticsAttestation> Companies { get; set; } = [];

    public int EffectiveHorizonDays => Math.Clamp(SearchHorizonDays, 31, 1830);

    public int EffectiveWindowDays => Math.Clamp(WindowDays, 7, 366);
}

/// <summary>
/// What the EDSM owners have confirmed — from the stored procedures
/// (<c>p4DuePayments</c>, <c>p32DuePayments</c>, <c>p25GetCheques</c>, <c>p7GetCheques</c>)
/// or from deployed behaviour — about the <c>v1/due-installments</c> rows of one company.
/// Each item is a fact somebody verified and can cite in <see cref="EvidenceReference"/>;
/// none is assumed by TigerCS (docs/Collections/EDSM-Instalment-Semantics.md).
/// </summary>
public sealed class NextPaymentSemanticsAttestation
{
    public const string RemainingUnpaid = "RemainingUnpaid";

    /// <summary>The raw <c>status</c> values (compared trimmed, case-insensitive) that mean "this instalment is still unpaid". Rows with any other status are never next-payment candidates.</summary>
    public List<string> UnpaidStatusValues { get; set; } = [];

    /// <summary>What <c>amount</c> is. Only <see cref="RemainingUnpaid"/> (the amount still unpaid, after partial payments) is accepted; "OriginalScheduled" or anything else cannot be netted for partial payments, so it is refused.</summary>
    public string? AmountRepresents { get; set; }

    /// <summary>True when <c>chequeDueDate</c> is the date the instalment is payable, as opposed to e.g. a cheque's value date or a created date.</summary>
    public bool ChequeDueDateIsInstalmentDueDate { get; set; }

    /// <summary>True when the route lists <b>every</b> unpaid instalment of the tenant for the range, including ones never partially paid. If it can omit any, the earliest unpaid instalment may be invisible.</summary>
    public bool ListsEveryUnpaidInstalment { get; set; }

    /// <summary>True when the amounts are in the configured currency (<c>CollectionsSource:Currency</c>) — EDSM returns none.</summary>
    public bool CurrencyConfirmed { get; set; }

    public string? ConfirmedBy { get; set; }

    public DateOnly? ConfirmedOn { get; set; }

    /// <summary>Where the confirmation can be checked (a stored-procedure definition, ticket id, UAT record).</summary>
    public string? EvidenceReference { get; set; }

    /// <summary>What is still missing; empty only when every semantic is confirmed and attributable.</summary>
    public IReadOnlyList<string> MissingItems()
    {
        var missing = new List<string>();
        if (UnpaidStatusValues.Count(s => !string.IsNullOrWhiteSpace(s)) == 0)
        {
            missing.Add("UnpaidStatusValues");
        }

        if (!string.Equals(AmountRepresents?.Trim(), RemainingUnpaid, StringComparison.OrdinalIgnoreCase))
        {
            missing.Add("AmountRepresents=RemainingUnpaid");
        }

        if (!ChequeDueDateIsInstalmentDueDate)
        {
            missing.Add("ChequeDueDateIsInstalmentDueDate");
        }

        if (!ListsEveryUnpaidInstalment)
        {
            missing.Add("ListsEveryUnpaidInstalment");
        }

        if (!CurrencyConfirmed)
        {
            missing.Add("CurrencyConfirmed");
        }

        if (string.IsNullOrWhiteSpace(ConfirmedBy) || ConfirmedOn is null || string.IsNullOrWhiteSpace(EvidenceReference))
        {
            missing.Add("ConfirmedBy/ConfirmedOn/EvidenceReference");
        }

        return missing;
    }
}
