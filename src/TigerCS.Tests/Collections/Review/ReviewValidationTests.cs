using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

public sealed class ReviewValidationTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);   // Current Month and Legal Notice are scheduled today

    private static ReviewBuildContext Context(bool reconciled = true, bool legalNotice = true, bool legalCase = false, DateOnly? asOf = null) =>
        new(asOf ?? Today, ReviewHarness.Now, "PACT test", "AED", reconciled, legalNotice, legalCase,
            TigerCS.Domain.Modules.Collections.Review.MoneyNormalizer.DefaultFloatTolerance, new Dictionary<string, string>());

    private static CollectionsReviewRecord Single(IEnumerable<PactReceivableInstalment> rows, CampaignReminderType type = CampaignReminderType.CurrentMonth,
        ReviewBuildContext? context = null) =>
        Assert.Single(ReviewRecordBuilder.Build(rows, context ?? Context()).Records, r => r.ReminderType == type);

    private static string[] Reasons(CollectionsReviewRecord r) => ReviewRecordBuilder.Parse(r.Reasons).ToArray();

    [Fact]
    public void FullyVerifiedUnitIsReady()
    {
        var record = Single([ReviewHarness.Row(1)]);
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
        Assert.Equal(ReviewPaymentStatus.Unpaid, record.PaymentStatus);
        Assert.Equal(500m, record.RemainingAmount);
        Assert.Equal("+971500000001", record.Phone);
        Assert.Empty(Reasons(record));
    }

    [Fact]
    public void SourceReconciliationRequired_HasARealCause_AndIsNotSilentlyCleared()
    {
        // Cause: the reconciliation sign-off does not cover the configured procedure.
        var record = Single([ReviewHarness.Row(1)], context: Context(reconciled: false));
        Assert.Equal(ReviewValidationStatus.NeedsReview, record.ValidationStatus);
        Assert.Contains(ReviewReasons.SourceReconciliationRequired, Reasons(record));
    }

    [Fact]
    public void ASignOffForAnotherProcedureDoesNotCount()
    {
        var h = new ReviewHarness();
        h.Campaign.FinancialSourceValidated = true;
        h.Campaign.ValidatedProcedureSuffix = "";     // reconciled the originals...
        h.Sql.ProcedureSuffix = "V2";                 // ...but the deployment now reads V2
        using var scope = h.NewScope();
        Assert.False(scope.Refresh.SourceReconciled);
        h.Campaign.ValidatedProcedureSuffix = "V2";
        Assert.True(scope.Refresh.SourceReconciled);
    }

    [Fact]
    public void AmountPrecision_DecimalSubFilsAmountIsFlagged_AndCarriesNoQuotedAmount()
    {
        var record = Single([ReviewHarness.Row(1, amount: 500.005m, plan: 500.005m)]);
        Assert.Contains(ReviewReasons.AmountPrecisionNeedsReview, Reasons(record));
        Assert.Null(record.RemainingAmount);                 // the unresolved value is never quoted as a clean two-decimal figure
        Assert.Equal(500.005m, record.RawRemainingAmount);   // and the raw source value stays visible for review
        Assert.NotEqual(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void AmountPrecision_FloatingPointResidueFromTheOriginalProcedureIsNormalizedAndStaysReady()
    {
        // Cause of most flags: the deployed procedures compute in float. 750.0000000000001 is the same money as 750.00.
        var row = ReviewHarness.Row(1, amount: 750.00000000000m) with { Amount = 750.0000000000001m, PlanAmount = 750m, AmountIsFloatingPoint = true };
        var record = Single([row]);
        Assert.DoesNotContain(ReviewReasons.AmountPrecisionNeedsReview, Reasons(record));
        Assert.Equal(750.00m, record.RemainingAmount);
    }

    [Fact]
    public void AmountPrecision_AFloatRemainderOfZeroIsASettledInstalment_NotAReceivable()
    {
        var settled = ReviewHarness.Row(1) with { Amount = 0.000000000001m, AmountIsFloatingPoint = true };
        var result = ReviewRecordBuilder.Build([settled], Context());
        Assert.Empty(result.Records);
        Assert.Equal(1, result.SettledRows);
    }

    [Fact]
    public void AmountPrecision_FloatNoiseDoesNotExcuseAGenuineSubFilsAmount()
    {
        var row = ReviewHarness.Row(1) with { Amount = 500.004m, PlanAmount = 500.004m, AmountIsFloatingPoint = true };
        Assert.Contains(ReviewReasons.AmountPrecisionNeedsReview, Reasons(Single([row])));
    }

    [Theory]
    [InlineData("")]
    [InlineData("n/a")]
    [InlineData("12345")]
    [InlineData("0500003001 / 0559999999")]
    public void NoValidContact_IsRaisedForEveryUnusablePhone(string mobile)
    {
        var record = Single([ReviewHarness.Row(1, mobile: mobile)]);
        Assert.Contains(ReviewReasons.NoValidContact, Reasons(record));
        Assert.Equal("", record.Phone);
    }

    [Theory]
    [InlineData("0500003001")]
    [InlineData("500003001")]
    [InlineData("+971 50 000 3001")]
    [InlineData("00971-50-000-3001")]
    public void NoValidContact_IsNotRaisedForRepairableUaeFormats(string mobile)
    {
        var record = Single([ReviewHarness.Row(1, mobile: mobile)]);
        Assert.DoesNotContain(ReviewReasons.NoValidContact, Reasons(record));
        Assert.Equal("+971500003001", record.Phone);
    }

    [Fact]
    public void AVoiceCampaignNeedsAPhone_AnEmailAloneIsNotEnough()
    {
        var record = Single([ReviewHarness.Row(1, mobile: "")]);   // has a valid email
        Assert.NotEqual("", record.Email);
        Assert.Contains(ReviewReasons.NoValidContact, Reasons(record));
        Assert.NotEqual(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void UnknownPaymentStatusIsVisibleButNotReady()
    {
        // The deployed report has no plan amount, so Installment cannot be classed unpaid or partially paid.
        var row = ReviewHarness.Row(1) with { PlanAmount = null };
        var record = Single([row]);
        Assert.Equal(ReviewPaymentStatus.Unknown, record.PaymentStatus);
        Assert.Contains(ReviewReasons.PaymentStatusUnknown, Reasons(record));
        Assert.Equal(ReviewValidationStatus.NeedsReview, record.ValidationStatus);
    }

    [Fact]
    public void PartiallyPaidIsRecognisedFromPlanAmount()
    {
        var record = Single([ReviewHarness.Row(1, amount: 200m, plan: 500m)]);
        Assert.Equal(ReviewPaymentStatus.PartiallyPaid, record.PaymentStatus);
        Assert.Equal(200m, record.RemainingAmount);
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void PaidStatusWithARemainingBalanceIsContradictory()
    {
        var record = Single([ReviewHarness.Row(1, status: "Paid")]);
        Assert.Contains(ReviewReasons.ContradictoryPaymentStatus, Reasons(record));
        Assert.NotEqual(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void IdenticalDuplicateSourceRowsAreFlaggedAsDuplicates_NotMerged()
    {
        var row = ReviewHarness.Row(1);
        var record = Single([row, row]);
        Assert.Contains(ReviewReasons.DuplicateSourceRecord, Reasons(record));
        Assert.Null(record.RemainingAmount);        // not summed to 1,000 and not collapsed to 500
        Assert.NotEqual(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void DifferentInstalmentsOnOneDateAreAmbiguous_NotDuplicates()
    {
        var a = ReviewHarness.Row(1);
        var b = a with { VoucherNumber = "INV-OTHER", Amount = 300m, PlanAmount = 300m };
        var reasons = Reasons(Single([a, b]));
        Assert.Contains(ReviewReasons.AmbiguousInstalments, reasons);
        Assert.DoesNotContain(ReviewReasons.DuplicateSourceRecord, reasons);
    }

    [Fact]
    public void ACustomersUnitsStaySeparateRecords_BalancesAreNeverMerged()
    {
        var first = ReviewHarness.Row(1, amount: 300m, day: 16);
        var second = ReviewHarness.Row(1, amount: 450m, day: 18) with { UnitId = 2001, UnitCode = "TP140-2001" };
        var records = ReviewRecordBuilder.Build([first, second], Context()).Records.Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal(new decimal?[] { 300m, 450m }, records.OrderBy(r => r.DueDate).Select(r => r.RemainingAmount));
        Assert.All(records, r => Assert.Equal(ReviewValidationStatus.Ready, r.ValidationStatus));
    }

    [Fact]
    public void ThePhoneSharedByTwoUnitsIsWarnedAboutNotMerged()
    {
        var a = ReviewHarness.Row(1, mobile: "0500003001");
        var b = ReviewHarness.Row(2, mobile: "0500003001");
        var records = ReviewRecordBuilder.Build([a, b], Context()).Records.Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).ToList();
        Assert.Equal(2, records.Count);
        Assert.All(records, r =>
        {
            Assert.Contains(ReviewReasons.SharedPhoneMultipleUnits, Reasons(r));
            Assert.Equal(ReviewValidationStatus.Ready, r.ValidationStatus);   // a warning, not a block: the business decision is the acknowledgement
        });
    }

    [Fact]
    public void InstalmentRepeatedUnderTwoUnitsOfOneCustomerNeedsAllocationReview()
    {
        var a = ReviewHarness.Row(1);
        var b = a with { UnitId = 2001, UnitCode = "TP140-2001" };
        Assert.All(ReviewRecordBuilder.Build([a, b], Context()).Records.Where(r => r.ReminderType == CampaignReminderType.CurrentMonth),
            r => Assert.Contains(ReviewReasons.UnitAllocationNeedsReview, Reasons(r)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void MissingUnitIdentityNeedsReview(int unitId)
    {
        var row = ReviewHarness.Row(1) with { UnitId = unitId };
        Assert.Contains(ReviewReasons.MissingUnitIdentity, Reasons(Single([row])));
    }

    [Fact]
    public void MissingCustomerIdentityNeedsReview()
    {
        Assert.Contains(ReviewReasons.MissingCustomerIdentity, Reasons(Single([ReviewHarness.Row(1, name: " ")])));
        Assert.Contains(ReviewReasons.MissingCustomerIdentity, Reasons(Single([ReviewHarness.Row(1) with { TenantId = " " }])));
    }

    [Fact]
    public void ConflictingContactDetailsOnOneUnitNeedReview()
    {
        var a = ReviewHarness.Row(1, day: 16);
        var b = ReviewHarness.Row(1, day: 18, mobile: "0559999999");
        Assert.Contains(ReviewReasons.ConflictingContactDetails, Reasons(Single([a, b])));
    }

    [Fact]
    public void NonAedCurrencyNeedsReview()
    {
        var record = Single([ReviewHarness.Row(1)], context: Context() with { Currency = "USD" });
        Assert.Contains(ReviewReasons.CurrencyNeedsReview, Reasons(record));
    }

    [Fact]
    public void ReminderTypeIsClassifiedByTheApprovedPolicy_NotByAgeAlone()
    {
        // A balance 4 months overdue and above AED 20,000 is a Legal Case candidate by the approved policy...
        var old = ReviewHarness.Row(1, amount: 25000m, day: 1) with { DueDate = new DateTime(2026, 6, 1) };
        var types = ReviewRecordBuilder.Build([old], Context()).Records.Select(r => r.ReminderType).ToHashSet();
        Assert.Contains(CampaignReminderType.LegalCase, types);
        Assert.Contains(CampaignReminderType.Overdue, types);

        // ...but the same age with AED 5,000 is not: age alone never creates a Legal Case.
        var smaller = old with { Amount = 5000m, PlanAmount = 5000m };
        types = ReviewRecordBuilder.Build([smaller], Context()).Records.Select(r => r.ReminderType).ToHashSet();
        Assert.DoesNotContain(CampaignReminderType.LegalCase, types);
        Assert.DoesNotContain(CampaignReminderType.LegalNotice, types);
        Assert.Contains(CampaignReminderType.Overdue, types);
    }

    [Fact]
    public void LegalNoticeNeedsPreviousMonthBalanceAboveThresholdAndRelease()
    {
        var september = ReviewHarness.Row(1, amount: 1500m, day: 10) with { DueDate = new DateTime(2026, 9, 10) };
        Assert.DoesNotContain(ReviewRecordBuilder.Build([september], Context()).Records, r => r.ReminderType == CampaignReminderType.LegalNotice); // strictly more than 1,500
        var above = september with { Amount = 1500.01m, PlanAmount = 1500.01m };
        var notice = Single([above], CampaignReminderType.LegalNotice);
        Assert.Equal(ReviewValidationStatus.Ready, notice.ValidationStatus);
        var unreleased = Single([above], CampaignReminderType.LegalNotice, Context(legalNotice: false));
        Assert.Equal(ReviewValidationStatus.Excluded, unreleased.ValidationStatus);
        Assert.Contains(ReviewReasons.LegalNoticeReleaseRequired, Reasons(unreleased));
    }

    [Fact]
    public void LegalCaseIsExcludedUntilTheBusinessApprovesCustomerContact()
    {
        var old = ReviewHarness.Row(1, amount: 25000m) with { DueDate = new DateTime(2026, 6, 1) };
        var day30 = new DateOnly(2026, 10, 30);
        var blocked = Single([old], CampaignReminderType.LegalCase, Context(asOf: day30));
        Assert.Equal(ReviewValidationStatus.Excluded, blocked.ValidationStatus);
        Assert.Contains(ReviewReasons.LegalCaseNotApproved, Reasons(blocked));
        var allowed = Single([old], CampaignReminderType.LegalCase, Context(legalCase: true, asOf: day30));
        Assert.Equal(ReviewValidationStatus.Ready, allowed.ValidationStatus);
    }

    [Fact]
    public void OutsideTheScheduledDayIsExcluded()
    {
        var record = Single([ReviewHarness.Row(1, day: 20)], context: Context(asOf: new DateOnly(2026, 10, 15)));
        Assert.Equal(ReviewValidationStatus.Excluded, record.ValidationStatus);
        Assert.Contains(ReviewReasons.OutsideSchedule, Reasons(record));
    }

    [Fact]
    public void RecordKeyIsStableForTheSameUnitAndCycleAndDiffersByStage()
    {
        var first = ReviewRecordBuilder.Build([ReviewHarness.Row(1)], Context()).Records.ToList();
        var again = ReviewRecordBuilder.Build([ReviewHarness.Row(1, amount: 900m)], Context()).Records.ToList();
        Assert.Equal(first.Select(r => r.RecordKey).Order(), again.Select(r => r.RecordKey).Order());
        Assert.Equal(first.Count, first.Select(r => r.RecordKey).Distinct().Count());
    }
}
