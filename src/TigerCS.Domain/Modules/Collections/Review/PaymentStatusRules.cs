namespace TigerCS.Domain.Modules.Collections.Review;

public enum ReviewPaymentStatus
{
    Unknown = 0,
    Unpaid = 1,
    PartiallyPaid = 2,
    Paid = 3
}

/// <summary>
/// Payment status comes only from verified source fields. The deployed PACT report reports <c>Paid</c> or
/// <c>Installment</c> and nothing else, and <c>Installment</c> cannot separate unpaid from partially paid, so with
/// the deployed procedures every open balance is <see cref="ReviewPaymentStatus.Unknown"/>. The reviewed V2 report
/// also returns the instalment's PlanAmount: a remainder equal to the plan is Unpaid, a smaller positive remainder is
/// PartiallyPaid. A due date never decides payment status.
/// </summary>
public static class PaymentStatusRules
{
    public static ReviewPaymentStatus Derive(string? sourceStatus, decimal remaining, decimal? planAmount,
        IReadOnlyDictionary<string, string>? verifiedMap = null)
    {
        var status = sourceStatus?.Trim();
        if (string.IsNullOrEmpty(status)) return ReviewPaymentStatus.Unknown;
        if (status.Equals("Paid", StringComparison.OrdinalIgnoreCase))
            return remaining <= 0 ? ReviewPaymentStatus.Paid : ReviewPaymentStatus.Unknown; // contradictory
        if (remaining <= 0) return ReviewPaymentStatus.Unknown;

        if (status.Equals("Installment", StringComparison.OrdinalIgnoreCase))
        {
            if (planAmount is not { } plan || plan <= 0 || remaining > plan) return ReviewPaymentStatus.Unknown;
            return remaining == plan ? ReviewPaymentStatus.Unpaid : ReviewPaymentStatus.PartiallyPaid;
        }

        var mapped = verifiedMap?.FirstOrDefault(p => p.Key.Trim().Equals(status, StringComparison.OrdinalIgnoreCase)).Value;
        if (mapped is not null)
            return mapped.Trim().ToLowerInvariant() switch
            {
                "unpaid" => ReviewPaymentStatus.Unpaid,
                "partiallypaid" => ReviewPaymentStatus.PartiallyPaid,
                _ => ReviewPaymentStatus.Unknown
            };
        return ReviewPaymentStatus.Unknown;
    }

    /// <summary>One unit combines several instalments: all must be known, otherwise Unknown; mixed unpaid/partial is PartiallyPaid.</summary>
    public static ReviewPaymentStatus Combine(IReadOnlyCollection<ReviewPaymentStatus> statuses)
    {
        if (statuses.Count == 0 || statuses.Any(s => s is ReviewPaymentStatus.Unknown or ReviewPaymentStatus.Paid)) return ReviewPaymentStatus.Unknown;
        return statuses.All(s => s == ReviewPaymentStatus.Unpaid) ? ReviewPaymentStatus.Unpaid : ReviewPaymentStatus.PartiallyPaid;
    }
}
