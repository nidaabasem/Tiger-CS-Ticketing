using System.Globalization;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// The call-pickup lookup: an agent has just answered, and Genesys asks who
/// is on the line.
///
/// <para>
/// <b>Pure composition — no second CRM integration.</b> The customer identity
/// comes from <see cref="CustomerSearchAppService"/>, the exact service the
/// New Ticket wizard and the Customer Workspace call, which in turn wraps the
/// existing CRM Buyer Lookup and the PACT/Tasleeh gateways. Genesys never
/// reaches Tiger CRM, PACT or Tasleeh directly, and no gateway, mapping or
/// verification rule is duplicated here — so this endpoint can never disagree
/// with the wizard about who a customer is.
/// </para>
///
/// <para>
/// <b>The ticket context is the existing phone-keyed lookup</b> that
/// <see cref="CustomerHistoryAppService"/>'s unverified path already uses:
/// intake records carry the number the customer called from, and the linked
/// ticket ids come straight off them. Nothing new is queried, and no
/// phone-to-customer identity is invented — these are simply "tickets that
/// arrived from this number".
/// </para>
///
/// <para>
/// <b>Never a gate.</b> Not finding a customer is a normal answer, not an
/// error, and neither it nor a source outage stops the Create Ticket call
/// that follows. That is the same enrichment-never-a-gate rule ticket
/// creation itself applies.
/// </para>
///
/// <para>
/// Read-only and side-effect free: no intake record, no ticket, nothing
/// persisted. A ringing call still creates nothing anywhere.
/// </para>
/// </summary>
public sealed class GenesysCustomerLookupAppService(
    GenesysOptions options,
    CustomerSearchAppService customerSearchAppService,
    IIntakeRecordRepository intakeRecordRepository,
    ITicketRepository ticketRepository)
{
    /// <summary>How many of the caller's recent tickets are returned. Bounded so a frequent caller cannot make a call-pickup lookup slow.</summary>
    private const int TicketLimit = 10;

    /// <summary>Null when the Genesys integration is switched off — the caller is answered 503, exactly as every other Genesys endpoint is.</summary>
    public async Task<GenesysCustomerLookupResultDto?> LookUpAsync(
        string phoneNumber, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return null;
        }

        // Genesys reports the caller as a telephony address ("tel:+971…"),
        // not as a person would type it. Searched in TigerCS' own "+971…"
        // form, or not at all when the address carries no number — a
        // withheld caller id is a normal "nobody found", never a 500, and
        // CRM/PACT are not asked about a number that does not exist.
        var searched = CustomerPhoneNumber.FromTelephonyAddress(phoneNumber);
        if (searched is null)
        {
            return new GenesysCustomerLookupResultDto(
                string.Empty, Found: false, CrmStatus: "NotSearched", [], [], [], 0, EmptyScreenPop);
        }

        var search = await customerSearchAppService.SearchByPhoneAsync(searched, cancellationToken);

        // Genesys-only projection: expired PACT contracts are dropped from the
        // unit lists. The shared search keeps them (Collections mapping and
        // history read them); customers are kept even with no unit left.
        var externalSources = search.ExternalSources.Select(WithoutExpiredPactUnits).ToList();

        var found = search.CrmBuyers.Count > 0
            || externalSources.Any(s => s.Customers.Count > 0);

        // Tickets that arrived from this number. Deliberately NOT scoped to
        // one caller's visible departments: Genesys calls as a service
        // account on behalf of whichever agent picked up, and an agent
        // answering a call needs to know the customer has an open case even
        // when it sits in a department they cannot open themselves. The
        // summary is deliberately thin for that reason — no customer data
        // beyond what this caller's own number already produced.
        var linkedTicketIds = await intakeRecordRepository.ListLinkedTicketIdsByPhoneNumberAsync(searched, cancellationToken);

        var tickets = linkedTicketIds.Count == 0
            ? []
            : (await ticketRepository.SearchCustomerHistoryAsync(
                new CustomerHistoryQuery(
                    VisibleDepartmentIds: null,
                    CrmBuyerCustomerId: null,
                    TicketIds: linkedTicketIds,
                    ExcludeTicketId: null,
                    Limit: TicketLimit,
                    OrderActiveFirst: true),
                cancellationToken)).Tickets;

        var ticketDtos = tickets.Select(ToDto).ToList();

        return new GenesysCustomerLookupResultDto(
            searched,
            found,
            search.CrmStatus,
            search.CrmBuyers,
            externalSources,
            ticketDtos,
            tickets.Count(IsOpen),
            ToScreenPop(search.CrmBuyers, externalSources, ticketDtos));
    }

    /// <summary>
    /// Drops PACT units whose contract expired (the Dubai-date rule already
    /// applied by the lookup, which flags rather than hides them). Contracts
    /// ending today or with no readable end date are not flagged and stay.
    /// Customers are never removed, so matched-customer counts are unchanged.
    /// </summary>
    private static CustomerLookupSourceResultDto WithoutExpiredPactUnits(CustomerLookupSourceResultDto source) =>
        source.Source != nameof(CustomerLookupSource.Pact)
            ? source
            : source with
            {
                Customers = source.Customers
                    .Select(c => c with { Units = c.Units.Where(u => !u.IsContractExpired).ToList() })
                    .ToList()
            };

    private static readonly GenesysScreenPopDto EmptyScreenPop =
        new(string.Empty, string.Empty, string.Empty, string.Empty, 0, [], string.Empty, [], [], string.Empty);

    /// <summary>
    /// The flat screen-pop projection of the full result: CRM first, then
    /// PACT, then Tasleeh — the order the New Ticket wizard presents them in.
    /// Nothing here is looked up again; it only reshapes what the sources
    /// already answered.
    /// </summary>
    private static GenesysScreenPopDto ToScreenPop(
        IReadOnlyList<CrmBuyerMatchDto> crmBuyers,
        IReadOnlyList<CustomerLookupSourceResultDto> externalSources,
        IReadOnlyList<GenesysCustomerTicketDto> tickets)
    {
        var customers = crmBuyers
            .Select(b => new ScreenPopCustomer(
                nameof(CustomerLookupSource.Crm),
                b.Customer.CustomerId.ToString(CultureInfo.InvariantCulture),
                b.Customer.FullNameEnglish ?? b.Customer.FullNameArabic,
                b.Customer.Email,
                b.Units.Select(u => new ScreenPopUnit(
                    nameof(CustomerLookupSource.Crm), UnitLabel(u.ProjectName, u.UnitNumber), u.UnitCode))))
            .Concat(externalSources
                .OrderBy(s => s.Source == nameof(CustomerLookupSource.Pact) ? 0 : 1)
                .SelectMany(s => s.Customers.Select(c => new ScreenPopCustomer(
                    s.Source,
                    c.ExternalCustomerId,
                    c.DisplayName,
                    c.Email,
                    c.Units.Select(u => new ScreenPopUnit(
                        s.Source, UnitLabel(u.PropertyName ?? u.TowerName, u.UnitNumber), u.UnitCode))))))
            .ToList();

        var primary = customers.FirstOrDefault();

        var selectable = DeduplicateUnits(customers.SelectMany(c => c.Units.Select(u => (Customer: c, Unit: u))));
        var units = selectable.Select(u => u.Label).ToList();

        return new GenesysScreenPopDto(
            CustomerName: primary?.Name?.Trim() ?? string.Empty,
            CustomerEmail: primary?.Email?.Trim() ?? string.Empty,
            VerificationSource: primary?.Source ?? string.Empty,
            ExternalCustomerId: primary?.ExternalId ?? string.Empty,
            MatchedCustomerCount: customers.Count,
            Units: units,
            UnitsText: string.Join("; ", units),
            RecentTicketNumbers: tickets.Select(t => t.TicketNumber).ToList(),
            OpenTicketNumbers: tickets.Where(t => t.IsOpen).Select(t => t.TicketNumber).ToList(),
            RecentTicketsText: string.Join("; ", tickets.Select(t => $"{t.TicketNumber} ({t.TicketStatus})")),
            SelectableUnits: selectable);
    }

    /// <summary>
    /// One entry per physical unit. Identity is the resolved unit code (CRM
    /// project code + number, or PACT's own code) — two sources agree on a unit
    /// only when their codes are equal. Never the unit number alone, never
    /// similar tower names and never an identical display label. A unit with no
    /// resolved code is never merged with anything: each such record stays its
    /// own entry with its own source and customer reference, and is reported as
    /// not ready for payment lookup. For a resolved unit, CRM's label wins, and
    /// every customer record it was found under stays attached — units are
    /// merged, people never are.
    /// </summary>
    private static List<GenesysSelectableUnitDto> DeduplicateUnits(IEnumerable<(ScreenPopCustomer Customer, ScreenPopUnit Unit)> units)
    {
        var entries = new List<(string Key, string Label, string Code, List<string> Sources, List<GenesysUnitCustomerRefDto> Customers, bool FromCrm)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (customer, unit) in units.Where(u => u.Unit.Label.Length > 0))
        {
            var holder = new GenesysUnitCustomerRefDto(customer.Source, customer.ExternalId);
            var code = string.IsNullOrWhiteSpace(unit.UnitCode) ? string.Empty : unit.UnitCode.Trim();
            var isCrm = unit.Source == nameof(CustomerLookupSource.Crm);

            // Unresolved: never merged, whatever the label says.
            if (code.Length == 0)
            {
                entries.Add((string.Empty, unit.Label, code, [unit.Source], [holder], isCrm));
                continue;
            }

            var key = "code:" + code;
            if (!index.TryGetValue(key, out var i))
            {
                index[key] = entries.Count;
                entries.Add((key, unit.Label, code, [unit.Source], [holder], isCrm));
                continue;
            }

            var existing = entries[i];
            if (!existing.Sources.Contains(unit.Source))
            {
                existing.Sources.Add(unit.Source);
            }

            if (!existing.Customers.Contains(holder))
            {
                existing.Customers.Add(holder);
            }

            entries[i] = isCrm && !existing.FromCrm
                ? existing with { Label = unit.Label, FromCrm = true }
                : existing;
        }

        return entries
            .Select(e => new GenesysSelectableUnitDto(
                e.Label, e.Code, PaymentLookupReady: e.Code.Length > 0,
                [.. e.Sources.OrderBy(x => x == nameof(CustomerLookupSource.Crm) ? 0 : 1)], e.Customers))
            .ToList();
    }

    private static string UnitLabel(string? project, string? unitNumber)
    {
        var parts = new[] { project, unitNumber }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim());
        return string.Join(" - ", parts);
    }

    /// <summary>A ticket nobody has finished with — what an agent picking up the phone actually needs to spot.</summary>
    private static bool IsOpen(Ticket ticket) =>
        ticket.TicketStatus is not (TicketStatus.Resolved or TicketStatus.Closed);

    private static GenesysCustomerTicketDto ToDto(Ticket ticket) => new(
        ticket.TicketId,
        ticket.TicketNumber,
        ticket.TicketStatus.ToString(),
        IsOpen(ticket),
        ticket.RequestSummary,
        ticket.CurrentDepartmentId,
        ticket.CreatedAtUtc);

    private sealed record ScreenPopCustomer(
        string Source, string ExternalId, string? Name, string? Email, IEnumerable<ScreenPopUnit> Units);

    private sealed record ScreenPopUnit(string Source, string Label, string? UnitCode);
}
