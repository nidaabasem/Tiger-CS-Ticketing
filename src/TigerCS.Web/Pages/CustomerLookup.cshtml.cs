using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Models;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Web.Pages;

/// <summary>One unit/contract belonging to a search candidate — display snapshot only, no raw external ids.</summary>
public sealed record CandidateUnit(
    string? UnitNumber, string? ProjectName, string? UnitType, int? FloorNumber,
    bool IsContractExpired = false, DateOnly? ContractEndDate = null);

/// <summary>
/// One customer a verification source matched for the searched phone number.
/// <see cref="Key"/> is the round-trippable selection token carried in the
/// query string; identity for follow-up calls is either
/// <see cref="CrmCustomerId"/> (CRM Buyer) or <see cref="Source"/> +
/// <see cref="ExternalCustomerId"/> (PACT/Tasleeh) — never the display name.
/// </summary>
public sealed record CustomerCandidate(
    string Key,
    string Source,
    string? DisplayName,
    string? PhoneNumber,
    string? Email,
    string? CustomerType,
    int? CrmCustomerId,
    string? ExternalCustomerId,
    IReadOnlyList<CandidateUnit> Units,
    CustomerLookupCustomerDto? LinkedPact = null)
{
    /// <summary>How many of the candidate's units are expired PACT contracts — shown as a label, so the count never passes as current units.</summary>
    public int ExpiredUnitsCount => Units.Count(u => u.IsContractExpired);

    /// <summary>True when every unit the source reports is an expired contract: the customer is still discoverable here for historical tickets, payments and fines, but has nothing a new ticket could be raised against.</summary>
    public bool AllContractsExpired => Units.Count > 0 && ExpiredUnitsCount == Units.Count;
}

/// <summary>
/// Customer Lookup (`/Customers/Lookup`): search a customer by phone across
/// every integrated verification source, pick the right match when more than
/// one source answered, then work from their summary — tickets across ALL
/// units first (with an optional unit filter), units/contracts, and contact
/// info. All state rides the query string (phoneNumber / customer / unit) so
/// every view is bookmarkable and refresh-safe, the same discipline as the
/// New Ticket wizard. History identity is always the selected candidate's
/// stable id (CRM customer id, or the persisted PACT/Tasleeh external id) —
/// never a name or phone match.
/// </summary>
public sealed class CustomerLookupModel(
    CustomerHistoryApiClient customersApiClient,
    TicketNameResolver nameResolver) : PageModel
{
    public string? PhoneNumber { get; private set; }
    public string? SelectedKey { get; private set; }
    public string? UnitFilter { get; private set; }

    public bool Searched => !string.IsNullOrWhiteSpace(PhoneNumber);
    public CustomerSearchResultDto? SearchResult { get; private set; }
    public ApiOutcome SearchOutcome { get; private set; }

    public IReadOnlyList<CustomerCandidate> Candidates { get; private set; } = [];
    public CustomerCandidate? Selected { get; private set; }

    public CustomerHistoryDto? History { get; private set; }
    public bool HistoryUnavailable { get; private set; }
    public IReadOnlyList<CustomerHistoryTicketDto> FilteredTickets { get; private set; } = [];
    public IReadOnlyList<string> UnitOptions { get; private set; } = [];

    public CurrentUser? Viewer { get; private set; }
    public bool ViewerCanReopen => TicketActions.CanReopen(Viewer?.Roles);
    public TicketNameResolver NameResolver => nameResolver;

    public async Task OnGetAsync(
        string? phoneNumber, string? customer, string? unit, CancellationToken cancellationToken)
    {
        Viewer = CurrentUser.FromPrincipal(User);
        PhoneNumber = phoneNumber?.Trim();
        SelectedKey = customer;
        UnitFilter = string.IsNullOrWhiteSpace(unit) ? null : unit;

        if (!Searched)
        {
            return;
        }

        await nameResolver.PrimeOwnDepartmentsAsync(cancellationToken);

        var result = await customersApiClient.SearchCustomersAsync(PhoneNumber!, cancellationToken);
        SearchOutcome = result.Outcome;
        if (!result.IsSuccess || result.Value is null)
        {
            return;
        }

        SearchResult = result.Value;
        Candidates = BuildCandidates(SearchResult);

        // An explicit selection wins; a single unambiguous match self-selects
        // so the common case is one step. Multiple matches render the picker.
        Selected = Candidates.FirstOrDefault(c => c.Key == SelectedKey)
            ?? (Candidates.Count == 1 ? Candidates[0] : null);
        if (Selected is null)
        {
            return;
        }

        var historyResult = Selected.CrmCustomerId is int crmCustomerId
            ? Selected.LinkedPact is not null
                ? await customersApiClient.GetByLinkedLookupAsync(PhoneNumber!, crmCustomerId, 50, cancellationToken)
                : await customersApiClient.GetByCrmCustomerIdAsync(crmCustomerId, limit: 50, cancellationToken)
            : await customersApiClient.GetByExternalIdentityAsync(
                Selected.Source, Selected.ExternalCustomerId!, limit: 50, cancellationToken);

        if (historyResult.IsSuccess && historyResult.Value is not null)
        {
            History = historyResult.Value;
        }
        else
        {
            HistoryUnavailable = true;
        }

        var historyTickets = History?.Tickets ?? [];

        // The unit filter's options span everything known about the customer
        // — units on their history AND units the source reports — with the
        // default always "All Units": the agent never has to pick a unit to
        // see history.
        UnitOptions = historyTickets.Select(t => t.UnitNumber)
            .Concat(Selected.Units.Select(u => u.UnitNumber))
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FilteredTickets = UnitFilter is null
            ? historyTickets
            : historyTickets.Where(t => string.Equals(t.UnitNumber, UnitFilter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static IReadOnlyList<CustomerCandidate> BuildCandidates(CustomerSearchResultDto result)
    {
        var candidates = new List<CustomerCandidate>();

        var linkedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var buyer in result.CrmBuyers)
        {
            var pact = CustomerIdentityLinker.FindPactMatch(buyer, result.CrmBuyers, result.ExternalSources);
            if (pact is not null) linkedIds.Add(pact.ExternalCustomerId);
            candidates.Add(new CustomerCandidate(
                $"crm:{buyer.Customer.CustomerId}",
                "Crm",
                buyer.Customer.FullNameEnglish ?? buyer.Customer.FullNameArabic,
                buyer.Customer.MobileNumber,
                buyer.Customer.Email ?? pact?.Email,
                buyer.Units.Select(u => u.CustomerTypeName).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
                buyer.Customer.CustomerId,
                ExternalCustomerId: null,
                buyer.Units
                    .Select(u => new CandidateUnit(u.UnitNumber, u.ProjectName, UnitType: null, u.FloorNumber))
                    .Concat((pact?.Units ?? []).Where(p => !buyer.Units.Any(c =>
                        CustomerIdentityLinker.Same(c.UnitNumber, p.UnitNumber) && (CustomerIdentityLinker.Same(c.ProjectName, p.PropertyName)
                            || CustomerIdentityLinker.Same(c.ProjectArabicName, p.PropertyName))))
                        .Select(u => new CandidateUnit(u.UnitNumber, u.PropertyName, u.UnitType, null, u.IsContractExpired, u.ContractEndDate)))
                    .Distinct().ToList(), pact));
        }

        foreach (var source in result.ExternalSources.Where(s => s.Status == "Found"))
        {
            foreach (var external in source.Customers)
            {
                if (source.Source == "Pact" && linkedIds.Contains(external.ExternalCustomerId)) continue;
                candidates.Add(new CustomerCandidate(
                    $"ext:{source.Source}:{Uri.EscapeDataString(external.ExternalCustomerId)}",
                    source.Source,
                    external.DisplayName,
                    external.PhoneNumber,
                    external.Email,
                    external.CustomerType,
                    CrmCustomerId: null,
                    external.ExternalCustomerId,
                    external.Units
                        .Select(u => new CandidateUnit(u.UnitNumber, u.PropertyName, u.UnitType, FloorNumber: null, u.IsContractExpired, u.ContractEndDate))
                        .ToList()));
            }
        }

        return candidates;
    }

    /// <summary>Human wording for one source's search outcome on the quiet status line.</summary>
    public static string SourceStatusLabel(string status) => status switch
    {
        "Found" => "match found",
        "NotFound" => "no match",
        "AmbiguousMatch" => "conflicting records — verify manually",
        _ => "unavailable"
    };
}
