using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

public sealed record ReviewBuildContext(
    DateOnly AsOfDate, DateTime SourceReadAtUtc, string SourceLabel, string Currency, bool SourceReconciled,
    bool LegalNoticeReleased, bool LegalCaseApproved, decimal FloatTolerance,
    IReadOnlyDictionary<string, string> StatusMap);

public sealed record ReviewBuildResult(IReadOnlyList<CollectionsReviewRecord> Records, int FloatNormalizedRows, int SettledRows);

/// <summary>
/// Turns one financial-source read into validated review records: one per unit and reminder type that the approved
/// campaign policy selects. Pure and deterministic. Nothing is guessed or merged: balances of different units stay
/// separate, a reason is attached to every unresolved doubt, and a record is Ready only when none remains.
/// </summary>
public static class ReviewRecordBuilder
{
    public static ReviewBuildResult Build(IEnumerable<PactReceivableInstalment> source, ReviewBuildContext context)
    {
        var floatNormalized = 0;
        var settled = 0;
        var rows = new List<(PactReceivableInstalment Row, DateOnly Due, decimal Amount, MoneyResult Money)>();
        foreach (var row in source)
        {
            var money = MoneyNormalizer.Normalize(row.Amount, row.AmountIsFloatingPoint, context.FloatTolerance);
            if (money.Precision == AmountPrecision.FloatingPointNoise) floatNormalized++;
            // Settled or noise-only remainders are not receivables. An unresolved precision issue is kept and flagged.
            if (money.IsResolved && money.Value <= 0) { settled++; continue; }
            if (money.Raw <= 0) { settled++; continue; }
            rows.Add((row, DateOnly.FromDateTime(row.DueDate), money.Value ?? money.Raw, money));
        }

        // The deployed procedures can repeat one instalment under several units of a tenant.
        var ambiguousTenants = rows.GroupBy(r => (r.Row.CompanyId, Tenant: r.Row.TenantId.Trim()))
            .Where(g => g.GroupBy(r => r.Due).Any(d => d.Select(r => (r.Row.UnitId, Code: r.Row.UnitCode.Trim())).Distinct().Count() > 1)
                || g.GroupBy(r => r.Row.UnitId).Any(u => u.Select(r => r.Row.UnitCode.Trim()).Distinct().Count() > 1)
                || g.GroupBy(r => r.Row.UnitCode.Trim()).Any(u => u.Select(r => r.Row.UnitId).Distinct().Count() > 1))
            .Select(g => g.Key).ToHashSet();

        var records = new List<CollectionsReviewRecord>();
        foreach (var unit in rows.GroupBy(r => (r.Row.CompanyId, Tenant: r.Row.TenantId.Trim(), r.Row.UnitId, Code: r.Row.UnitCode.Trim())))
        {
            var unitRows = unit.ToList();
            var first = unitRows[0].Row;
            var phone = PhoneNormalizer.Normalize(first.Mobile);
            var email = NormalizeEmail(first.Email);
            var contactConflict = unitRows.Select(r => (r.Row.FullName.Trim(), Phone: PhoneNormalizer.Normalize(r.Row.Mobile).E164,
                Email: NormalizeEmail(r.Row.Email), r.Row.ProjectCode)).Distinct().Count() > 1;

            foreach (var stage in Enum.GetValues<CollectionsCampaignStage>())
            {
                var instalments = unitRows.Select(r => new CampaignInstalment(r.Due, r.Amount)).ToList();
                var amount = CollectionsCampaignPolicy.Evaluate(instalments, stage, context.AsOfDate);
                if (amount.Reason is "NoQualifyingBalance" or "BelowThreshold") continue;

                var qualifying = CollectionsCampaignPolicy.Qualifying(instalments, stage, context.AsOfDate);
                var stageRows = unitRows.Where(r => qualifying.Any(q => q.DueDate == r.Due && q.RemainingAmount == r.Amount)).ToList();
                var reasons = new List<string>();

                if (amount.Reason == "AmbiguousInstalments")
                {
                    var sameDate = stageRows.GroupBy(r => r.Due).Where(g => g.Count() > 1).ToList();
                    var exact = sameDate.Where(g => g.Select(r => (r.Row.VoucherNumber, r.Row.ChequeNumber, r.Row.Amount)).Distinct().Count() == 1).ToList();
                    if (exact.Count > 0) reasons.Add(ReviewReasons.DuplicateSourceRecord);
                    if (sameDate.Count > exact.Count) reasons.Add(ReviewReasons.AmbiguousInstalments);
                }
                var unresolved = stageRows.Any(r => !r.Money.IsResolved);
                if (unresolved) reasons.Add(ReviewReasons.AmountPrecisionNeedsReview);

                var statuses = stageRows.Select(r => PaymentStatusRules.Derive(r.Row.SourceStatus, r.Amount, r.Row.PlanAmount, context.StatusMap)).ToList();
                if (statuses.Contains(ReviewPaymentStatus.Paid) || stageRows.Any(r => string.Equals(r.Row.SourceStatus?.Trim(), "Paid", StringComparison.OrdinalIgnoreCase)))
                    reasons.Add(ReviewReasons.ContradictoryPaymentStatus);
                var payment = PaymentStatusRules.Combine(statuses);
                if (payment == ReviewPaymentStatus.Unknown && !reasons.Contains(ReviewReasons.ContradictoryPaymentStatus))
                    reasons.Add(ReviewReasons.PaymentStatusUnknown);

                if (unit.Key.UnitId is not > 0 || string.IsNullOrWhiteSpace(unit.Key.Code) || unit.Key.Code == "0")
                    reasons.Add(ReviewReasons.MissingUnitIdentity);
                if (string.IsNullOrWhiteSpace(unit.Key.Tenant) || string.IsNullOrWhiteSpace(first.FullName) || unit.Key.CompanyId is not (4 or 32))
                    reasons.Add(ReviewReasons.MissingCustomerIdentity);
                if (contactConflict) reasons.Add(ReviewReasons.ConflictingContactDetails);
                if (ambiguousTenants.Contains((unit.Key.CompanyId, unit.Key.Tenant))) reasons.Add(ReviewReasons.UnitAllocationNeedsReview);
                if (!context.Currency.Equals("AED", StringComparison.Ordinal)) reasons.Add(ReviewReasons.CurrencyNeedsReview);
                if (!context.SourceReconciled) reasons.Add(ReviewReasons.SourceReconciliationRequired);
                if (!phone.IsValid) reasons.Add(ReviewReasons.NoValidContact);

                if (!CollectionsCampaignPolicy.ScheduledDates(stage, context.AsOfDate).Contains(context.AsOfDate))
                    reasons.Add(ReviewReasons.OutsideSchedule);
                if (stage == CollectionsCampaignStage.LegalNotice && !context.LegalNoticeReleased) reasons.Add(ReviewReasons.LegalNoticeReleaseRequired);
                if (stage == CollectionsCampaignStage.LegalReferral && !context.LegalCaseApproved) reasons.Add(ReviewReasons.LegalCaseNotApproved);

                var cycle = $"{context.AsOfDate.ToString("yyyy-MM", CultureInfo.InvariantCulture)}:{stage}";
                var recordKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{unit.Key.CompanyId}:{unit.Key.Tenant}:{unit.Key.UnitId}:{unit.Key.Code}:{cycle}")));
                var distinctReasons = reasons.Distinct().ToList();
                records.Add(new CollectionsReviewRecord
                {
                    RecordKey = recordKey,
                    CycleKey = cycle,
                    ReminderType = CampaignReminderTypes.FromStage(stage),
                    CompanyId = unit.Key.CompanyId,
                    TenantId = unit.Key.Tenant,
                    CustomerName = first.FullName.Trim(),
                    Phone = phone.E164,
                    Email = email,
                    UnitId = unit.Key.UnitId,
                    UnitCode = unit.Key.Code,
                    ProjectCode = first.ProjectCode?.Trim() ?? "",
                    PaymentStatus = payment,
                    SourceStatus = string.Join(",", stageRows.Select(r => r.Row.SourceStatus?.Trim() ?? "").Distinct()),
                    RemainingAmount = unresolved || amount.Amount is null ? null : amount.Amount,
                    RawRemainingAmount = stageRows.Sum(r => r.Money.Raw),
                    Currency = context.Currency,
                    DueDate = amount.EarliestDueDate,
                    InstalmentCount = stageRows.Count,
                    Reasons = Serialize(distinctReasons),
                    ValidationStatus = ReviewReasons.StatusFor(distinctReasons, alreadySent: false),
                    SourceReadAtUtc = context.SourceReadAtUtc,
                    Source = context.SourceLabel
                });
            }
        }

        MarkSharedPhones(records);
        return new ReviewBuildResult(records, floatNormalized, settled);
    }

    /// <summary>
    /// The same phone on several records that could be sent today means several calls to one person; flagged, not merged.
    /// Records outside today's schedule are not candidates, so a unit's Follow Up (day 28) does not "share" with its Current Month (day 14).
    /// </summary>
    private static void MarkSharedPhones(List<CollectionsReviewRecord> records)
    {
        var candidates = records.Where(r => r.Phone.Length > 0 && !Parse(r.Reasons).Contains(ReviewReasons.OutsideSchedule));
        foreach (var group in candidates.GroupBy(r => r.Phone).Where(g => g.Count() > 1))
            foreach (var record in group)
                record.Reasons = Serialize(Parse(record.Reasons).Append(ReviewReasons.SharedPhoneMultipleUnits).Distinct());
    }

    public static string Serialize(IEnumerable<string> reasons)
    {
        var list = reasons.ToList();
        return list.Count == 0 ? "" : ";" + string.Join(";", list) + ";";
    }

    public static IReadOnlyList<string> Parse(string? reasons) =>
        string.IsNullOrEmpty(reasons) ? [] : reasons.Split(';', StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizeEmail(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        return trimmed.Length > 0 && MailAddress.TryCreate(trimmed, out var address) && address.Address == trimmed ? address.Address : "";
    }
}
