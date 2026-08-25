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
                ])
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
    /// Business-rule change: phone-based customer search, for
    /// <see cref="CustomerLookupAppService"/> — a distinct capability from
    /// this gateway's own unit-number lookups above (see
    /// <see cref="ICrmCustomerLookupGateway"/>'s remarks). Matches a fixture
    /// contact whose <see cref="CrmContactResult.ContactChannel"/> equals the
    /// searched phone number.
    /// </summary>
    public Task<CrmCustomerMatch?> SearchByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        if (phoneNumber.Contains(OutageTrigger, StringComparison.OrdinalIgnoreCase))
        {
            throw new CrmCustomerLookupGatewayUnavailableException(
                $"Simulated CRM outage triggered by '{phoneNumber}' (MockCrmGateway — a test double, never a real CRM failure).");
        }

        foreach (var (unit, contacts) in Fixtures.Values)
        {
            var contact = contacts.FirstOrDefault(c =>
                string.Equals(c.ContactChannel, phoneNumber, StringComparison.OrdinalIgnoreCase));
            if (contact is not null)
            {
                return Task.FromResult<CrmCustomerMatch?>(
                    new CrmCustomerMatch(unit.CrmUnitId, contact.CrmContactId, contact.DisplayName, contact.ContactChannel));
            }
        }

        return Task.FromResult<CrmCustomerMatch?>(null);
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
