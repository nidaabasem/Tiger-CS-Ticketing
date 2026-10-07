using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Conservative, lookup-scoped association. No fuzzy names, unit-id cross-system
/// guesses, or phone-only joins. Missing evidence and ambiguous links stay separate.
/// The original source records, tenant and every company/contract are retained.
/// </summary>
public static class CustomerIdentityLinker
{
    public static CustomerLookupCustomerDto? FindPactMatch(
        CrmBuyerMatchDto buyer, IReadOnlyList<CrmBuyerMatchDto> buyers,
        IReadOnlyList<CustomerLookupSourceResultDto> sources)
    {
        var pact = sources.Where(s => s.Source == "Pact" && s.Status == "Found")
            .SelectMany(s => s.Customers).ToList();
        var matches = pact.Where(p => Matches(buyer, p)).ToList();
        return matches.Count == 1 && buyers.Count(b => Matches(b, matches[0])) == 1 ? matches[0] : null;
    }

    private static bool Matches(CrmBuyerMatchDto buyer, CustomerLookupCustomerDto pact)
    {
        var c = buyer.Customer;
        if (!CustomerPhoneNumber.LooksLikeNumber(c.MobileNumber) || !CustomerPhoneNumber.LooksLikeNumber(pact.PhoneNumber)
            || !CustomerPhoneNumber.AreSameNumber(c.MobileNumber, pact.PhoneNumber)) return false;
        var samePerson = Same(c.Email, pact.Email)
            || Same(c.FullNameEnglish, pact.DisplayName) || Same(c.FullNameArabic, pact.DisplayName);
        return samePerson && buyer.Units.Any(crm => pact.Units.Any(p =>
            Same(crm.UnitNumber, p.UnitNumber) && (Same(crm.ProjectName, p.PropertyName) || Same(crm.ProjectArabicName, p.PropertyName))));
    }

    public static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(Text(left), Text(right), StringComparison.OrdinalIgnoreCase);

    public static string Text(string? value) => string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
