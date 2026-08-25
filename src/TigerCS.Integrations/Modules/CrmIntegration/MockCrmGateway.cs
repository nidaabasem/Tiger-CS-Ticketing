using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// Deterministic, in-memory fake implementing <see cref="ICrmGateway"/> —
/// a read-only CRM data-access port only (unit lookup, linked contacts/
/// owners/tenants/representatives). It holds no verification state and
/// makes no verification decision, matching the real contract's own
/// boundary; see <see cref="ICrmGateway"/>'s remarks (MVP-Implementation-Backlog.md S-06).
///
/// <para>
/// <b>NOT PRODUCTION-READY.</b> No real Tiger Group CRM endpoint details
/// were available to build against at this pilot phase — this fixture-backed
/// double exists solely so Tiger CS Ticketing's customer verification
/// business logic can be built and tested end to end. It must be replaced
/// by a real HTTP-backed <see cref="ICrmGateway"/> implementation, swapped
/// in behind the <c>Crm:Provider</c> configuration key (<see cref="IntegrationsServiceCollectionExtensions"/>),
/// before any non-pilot use. Never describe validation against this
/// fixture as production/CRM-integration-tested in any status update or
/// go-live communication (MVP-Implementation-Backlog.md §0).
/// </para>
/// </summary>
public sealed class MockCrmGateway : ICrmGateway, ICrmCustomerLookupGateway
{
    /// <summary>Any input containing this token simulates a CRM outage — 502/504 fallback-path testing (MVP-API-Contracts.md §2.1).</summary>
    public const string OutageTrigger = "OUTAGE";

    private static readonly IReadOnlyDictionary<string, (CrmUnitResult Unit, CrmContactResult[] Contacts)> Fixtures =
        new Dictionary<string, (CrmUnitResult, CrmContactResult[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["CRM-UNIT-1001"] = (
                new CrmUnitResult("CRM-UNIT-1001", "1204", "Tiger Tower A", "Tower A", "Residential"),
                [
                    new CrmContactResult("CRM-CONTACT-2001", "Ahmed Al-Farsi", "ahmed.alfarsi@example.com", ContactType.Owner, null),
                    new CrmContactResult("CRM-CONTACT-2002", "Sara Yousef", "+971500000001", ContactType.Tenant, null)
                ]),
            ["CRM-UNIT-1002"] = (
                new CrmUnitResult("CRM-UNIT-1002", "0507", "Tiger Tower B", "Tower B", "Commercial"),
                [
                    new CrmContactResult("CRM-CONTACT-2003", "Layla Hassan", "layla.hassan@example.com", ContactType.Owner, null),
                    new CrmContactResult(
                        "CRM-CONTACT-2004", "Property Management Co.", "pm@example.com", ContactType.Representative,
                        "CRM-CONTACT-2003")
                ]),

            // Business-rule change: fixtures below back SearchByPhoneAsync's
            // Buyer-lookup-by-phone (multiple customers, multiple units per
            // customer) — additive, never mutating the two units above, so
            // GetUnitAsync/SearchUnitsAsync/GetContactsAsync's existing
            // behavior and tests are untouched.
            ["CRM-UNIT-1101"] = (
                new CrmUnitResult("CRM-UNIT-1101", "1205", "Tiger Sky Tower", "Tower 1", "Residential"),
                [new CrmContactResult("CRM-CONTACT-3001", "Ahmed Ali", PhoneBuyerOneAndTwoUnits, ContactType.Owner, null)]),
            ["CRM-UNIT-1102"] = (
                new CrmUnitResult("CRM-UNIT-1102", "1403", "Tiger Sky Tower", "Tower 1", "Residential"),
                [new CrmContactResult("CRM-CONTACT-3002", "Ahmed Ali", PhoneBuyerOneAndTwoUnits, ContactType.Owner, null)]),
            ["CRM-UNIT-1103"] = (
                new CrmUnitResult("CRM-UNIT-1103", "2004", "Tiger Sky Tower", "Tower 2", "Residential"),
                [new CrmContactResult("CRM-CONTACT-3003", "Ahmad Ali Hassan", PhoneBuyerOneAndTwoUnits, ContactType.Owner, null)]),
            ["CRM-UNIT-1104"] = (
                new CrmUnitResult("CRM-UNIT-1104", "3010", "Tiger Sky Tower", "Tower 3", "Residential"),
                [new CrmContactResult("CRM-CONTACT-3004", "Khalid Nasser", PhoneTenantOnly, ContactType.Tenant, null)]),
            ["CRM-UNIT-1105"] = (
                new CrmUnitResult("CRM-UNIT-1105", "4010", "Tiger Sky Tower", "Tower 4", "Residential"),
                [new CrmContactResult("CRM-CONTACT-3005", "Mona Youssef", PhoneOwnerAndTenant, ContactType.Owner, null)]),
            ["CRM-UNIT-1106"] = (
                new CrmUnitResult("CRM-UNIT-1106", "4011", "Tiger Sky Tower", "Tower 4", "Residential"),
                [new CrmContactResult("CRM-CONTACT-3006", "Mona Youssef", PhoneOwnerAndTenant, ContactType.Tenant, null)]),
            ["CRM-UNIT-1107"] = (
                new CrmUnitResult("CRM-UNIT-1107", "5001", "Tiger Sky Tower", "Tower 5", "Residential"),
                [new CrmContactResult("CRM-CONTACT-3010", "Sami Nasser", PhoneSingleCustomerSingleUnit, ContactType.Owner, null)]),

            // A duplicate relationship row for the same unit/contact — data
            // glitch testing (Buyer test 7: duplicate rows never duplicate units).
            ["CRM-UNIT-1108"] = (
                new CrmUnitResult("CRM-UNIT-1108", "6001", "Tiger Sky Tower", "Tower 6", "Residential"),
                [
                    new CrmContactResult("CRM-CONTACT-3011", "Rania Adel", PhoneDuplicateRelationshipRow, ContactType.Owner, null),
                    new CrmContactResult("CRM-CONTACT-3011", "Rania Adel", PhoneDuplicateRelationshipRow, ContactType.Owner, null)
                ])
        };

    private const string PhoneBuyerOneAndTwoUnits = "+971501234567";
    private const string PhoneTenantOnly = "+971502223333";
    private const string PhoneOwnerAndTenant = "+971503334444";
    private const string PhoneSingleCustomerSingleUnit = "+971509990001";
    private const string PhoneDuplicateRelationshipRow = "+971505556666";

    /// <summary>
    /// Groups a matched contact's own per-unit relationship id
    /// (<see cref="CrmContactResult.CrmContactId"/>) to the Buyer's own,
    /// stable external customer identity — the real distinction
    /// <c>tblCustomer</c>/<c>tblLeadCustomer</c>-style CRM schemas draw
    /// between a customer and their per-unit relationship record, expressed
    /// here without adding a new field to <see cref="CrmContactResult"/>
    /// (that record stays exactly what <c>ICrmGateway</c>'s own contract
    /// already documents — see that interface's remarks) since this mapping
    /// is private to <see cref="SearchByPhoneAsync"/>'s fixture data only.
    /// Two different <see cref="CrmContactResult.CrmContactId"/> values
    /// mapping to the same external customer id here is exactly how "one
    /// Buyer, two units" is represented (CRM-CONTACT-3001/-3002 → Ahmed Ali);
    /// two different contacts left unmapped (falling back to their own
    /// CrmContactId below) that happen to share a phone number is how "two
    /// different customers sharing a phone" is represented
    /// (CRM-CONTACT-3001 vs CRM-CONTACT-3003).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExternalCustomerIdByContactId =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CRM-CONTACT-3001"] = "CRM-CUST-5001",
            ["CRM-CONTACT-3002"] = "CRM-CUST-5001",
            ["CRM-CONTACT-3005"] = "CRM-CUST-5004",
            ["CRM-CONTACT-3006"] = "CRM-CUST-5004"
        };

    private static readonly IReadOnlyDictionary<string, (string? Email, string? CustomerType)> CustomerDirectory =
        new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase)
        {
            ["CRM-CUST-5001"] = ("ahmed.ali@example.com", "Buyer"),
            ["CRM-CONTACT-3003"] = ("ahmad.hassan@example.com", "Buyer"),
            ["CRM-CONTACT-3004"] = (null, "Buyer"),
            ["CRM-CUST-5004"] = ("mona.youssef@example.com", "Buyer")
        };

    public Task<CrmUnitResult?> GetUnitAsync(string crmUnitId, CancellationToken cancellationToken = default)
    {
        ThrowIfSimulatedOutage(crmUnitId);
        return Task.FromResult(Fixtures.TryGetValue(crmUnitId, out var fixture) ? fixture.Unit : null);
    }

    public Task<IReadOnlyList<CrmUnitResult>> SearchUnitsAsync(
        string unitNumber, string? propertyName, CancellationToken cancellationToken = default)
    {
        ThrowIfSimulatedOutage(unitNumber);

        var matches = Fixtures.Values
            .Where(f => f.Unit.UnitNumber.Equals(unitNumber, StringComparison.OrdinalIgnoreCase)
                && (propertyName is null
                    || (f.Unit.PropertyName?.Contains(propertyName, StringComparison.OrdinalIgnoreCase) ?? false)))
            .Select(f => f.Unit)
            .ToList();

        return Task.FromResult<IReadOnlyList<CrmUnitResult>>(matches);
    }

    public Task<IReadOnlyList<CrmContactResult>> GetContactsAsync(string crmUnitId, CancellationToken cancellationToken = default)
    {
        ThrowIfSimulatedOutage(crmUnitId);
        return Task.FromResult<IReadOnlyList<CrmContactResult>>(
            Fixtures.TryGetValue(crmUnitId, out var fixture) ? fixture.Contacts : []);
    }

    /// <summary>
    /// Business-rule change: phone-based Buyer search, for
    /// <see cref="CustomerLookupAppService"/> — a distinct capability from
    /// this gateway's own unit-number lookups above (see
    /// <see cref="ICrmCustomerLookupGateway"/>'s remarks). Never assumes one
    /// phone number resolves to one customer, or one customer owns one unit:
    /// every fixture contact across every unit whose
    /// <see cref="CrmContactResult.ContactChannel"/> equals the searched
    /// phone number is grouped into its Buyer
    /// (<see cref="ExternalCustomerIdByContactId"/>), and only
    /// <see cref="ContactType.Owner"/> relationships contribute a unit — this
    /// integration's real, existing ownership signal (there is no Lead/deal-
    /// status concept anywhere in <c>ICrmGateway</c>'s contract to filter by
    /// instead; see <see cref="ExternalCustomerIdByContactId"/>'s remarks). A
    /// Tenant/Representative-only relationship still surfaces its Buyer
    /// (Found, with zero eligible units) — a customer existing with no
    /// eligible unit is not the same as no customer at all.
    /// </summary>
    public Task<IReadOnlyList<CrmCustomerMatch>> SearchByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        if (phoneNumber.Contains(OutageTrigger, StringComparison.OrdinalIgnoreCase))
        {
            throw new CrmCustomerLookupGatewayUnavailableException(
                $"Simulated CRM outage triggered by '{phoneNumber}' (MockCrmGateway — a test double, never a real CRM failure).");
        }

        var matchingContacts = Fixtures.Values
            .SelectMany(f => f.Contacts.Select(contact => (f.Unit, Contact: contact)))
            .Where(x => string.Equals(x.Contact.ContactChannel, phoneNumber, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingContacts.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<CrmCustomerMatch>>([]);
        }

        var customers = matchingContacts
            .GroupBy(x => ExternalCustomerIdByContactId.GetValueOrDefault(x.Contact.CrmContactId, x.Contact.CrmContactId))
            .Select(group =>
            {
                var externalCustomerId = group.Key;
                (string? Email, string? CustomerType) directory = CustomerDirectory.TryGetValue(externalCustomerId, out var directoryEntry)
                    ? directoryEntry
                    : (null, null);

                var units = group
                    .Where(x => x.Contact.ContactType == ContactType.Owner)
                    .Select(x => new CrmCustomerUnitMatch(x.Unit.CrmUnitId, x.Contact.CrmContactId))
                    .DistinctBy(u => u.CrmUnitId)
                    .ToList();

                return new CrmCustomerMatch(
                    externalCustomerId, group.First().Contact.DisplayName, phoneNumber, directory.Email, directory.CustomerType, units);
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<CrmCustomerMatch>>(customers);
    }

    private static void ThrowIfSimulatedOutage(string input)
    {
        if (input.Contains(OutageTrigger, StringComparison.OrdinalIgnoreCase))
        {
            throw new CrmGatewayUnavailableException(
                $"Simulated CRM outage triggered by '{input}' (MockCrmGateway — a test double, never a real CRM failure).");
        }
    }
}
