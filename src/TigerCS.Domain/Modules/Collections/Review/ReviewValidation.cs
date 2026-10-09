namespace TigerCS.Domain.Modules.Collections.Review;

public enum ReviewValidationStatus
{
    Ready = 1,
    NeedsReview = 2,
    Excluded = 3,
    AlreadySent = 4
}

public enum ReasonKind { NeedsReview = 1, Exclusion = 2, Warning = 3 }

public sealed record ReviewReasonInfo(string Code, ReasonKind Kind, string Explanation);

/// <summary>
/// The single catalogue of validation reasons: stable codes (filterable) with the plain-language explanation shown to
/// reviewers. A needs-review reason is a data problem a person could resolve; an exclusion is a policy decision that
/// keeps the record out of a send; a warning is shown but does not block.
/// </summary>
public static class ReviewReasons
{
    public const string SourceReconciliationRequired = "SourceReconciliationRequired";
    public const string AmountPrecisionNeedsReview = "AmountPrecisionNeedsReview";
    public const string NoValidContact = "NoValidContact";
    public const string PaymentStatusUnknown = "PaymentStatusUnknown";
    public const string StaleSource = "StaleSource";
    public const string MissingUnitIdentity = "MissingUnitIdentity";
    public const string MissingCustomerIdentity = "MissingCustomerIdentity";
    public const string UnitAllocationNeedsReview = "UnitAllocationNeedsReview";
    public const string AmbiguousInstalments = "AmbiguousInstalments";
    public const string DuplicateSourceRecord = "DuplicateSourceRecord";
    public const string ConflictingContactDetails = "ConflictingContactDetails";
    public const string ContradictoryPaymentStatus = "ContradictoryPaymentStatus";
    public const string CurrencyNeedsReview = "CurrencyNeedsReview";
    public const string OutsideSchedule = "OutsideSchedule";
    public const string LegalNoticeReleaseRequired = "LegalNoticeReleaseRequired";
    public const string LegalCaseNotApproved = "LegalCaseNotApproved";
    public const string SharedPhoneMultipleUnits = "SharedPhoneMultipleUnits";

    public static readonly IReadOnlyList<ReviewReasonInfo> All =
    [
        new(SourceReconciliationRequired, ReasonKind.NeedsReview, "The financial source has not been reconciled with PACT accounts for this procedure yet, so its balances cannot be trusted for a send."),
        new(AmountPrecisionNeedsReview, ReasonKind.NeedsReview, "The source amount has more than two decimal places that are not simple rounding noise. The real balance must be confirmed in PACT; it was not rounded."),
        new(NoValidContact, ReasonKind.NeedsReview, "There is no valid phone number that the voice campaign can call (missing, wrong length, not a UAE/international number, or several numbers in one field)."),
        new(PaymentStatusUnknown, ReasonKind.NeedsReview, "The source does not confirm whether this balance is unpaid or partially paid. It stays visible but cannot be sent."),
        new(StaleSource, ReasonKind.NeedsReview, "The financial data was read too long ago. Refresh the data before sending."),
        new(MissingUnitIdentity, ReasonKind.NeedsReview, "The record has no valid unit identity, so the balance cannot be tied to one apartment."),
        new(MissingCustomerIdentity, ReasonKind.NeedsReview, "The customer, company or name is missing on the source record."),
        new(UnitAllocationNeedsReview, ReasonKind.NeedsReview, "The same instalment appears under several units of this customer; the balance may be allocated to the wrong unit."),
        new(AmbiguousInstalments, ReasonKind.NeedsReview, "Several instalments share one due date, so the amount may be double counted."),
        new(DuplicateSourceRecord, ReasonKind.NeedsReview, "The source returned the identical instalment (same voucher, date and amount) more than once. It was not merged automatically."),
        new(ConflictingContactDetails, ReasonKind.NeedsReview, "The source rows for this unit carry different names, phones or emails."),
        new(ContradictoryPaymentStatus, ReasonKind.NeedsReview, "The source says Paid but still shows an amount remaining."),
        new(CurrencyNeedsReview, ReasonKind.NeedsReview, "The currency is not AED; amounts cannot be added or sent until reviewed."),
        new(OutsideSchedule, ReasonKind.Exclusion, "Today is not a scheduled send day for this reminder type."),
        new(LegalNoticeReleaseRequired, ReasonKind.Exclusion, "Sending legal notices to customers has not been released (needs an approved legal workflow)."),
        new(LegalCaseNotApproved, ReasonKind.Exclusion, "Legal Case records are internal Legal referrals; calling customers for them needs an explicit business decision."),
        new(SharedPhoneMultipleUnits, ReasonKind.Warning, "The same phone number is on several units or reminders; sending all would call this number repeatedly.")
    ];

    private static readonly Dictionary<string, ReviewReasonInfo> ByCode = All.ToDictionary(r => r.Code, StringComparer.Ordinal);

    public static string Explain(string code) => ByCode.TryGetValue(code, out var info) ? info.Explanation : code;

    public static ReasonKind? KindOf(string code) => ByCode.TryGetValue(code, out var info) ? info.Kind : null;

    public static ReviewValidationStatus StatusFor(IEnumerable<string> reasonCodes, bool alreadySent)
    {
        if (alreadySent) return ReviewValidationStatus.AlreadySent;
        var kinds = reasonCodes.Select(KindOf).ToList();
        if (kinds.Contains(ReasonKind.NeedsReview)) return ReviewValidationStatus.NeedsReview;
        if (kinds.Contains(ReasonKind.Exclusion)) return ReviewValidationStatus.Excluded;
        return ReviewValidationStatus.Ready;
    }
}
