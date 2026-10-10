namespace TigerCS.Application.Modules.Collections.Dto;

/// <summary>
/// One unit of a customer, linked end to end: the customer (CRM first, PACT completing), the unit (CRM project + apartment, or PACT company + unit code), and the PACT
/// financial result for exactly that unit. Nothing is mixed between candidates: every amount comes from the PACT account of this candidate's company + tenant + unit.
/// <list type="bullet">
/// <item><c>Source</c>: <c>Crm</c> (found through an eligible CRM Sold / Contract sale), <c>Pact</c> (found in PACT by phone, companies 4 / 32) or <c>Leasing</c> (PACT company 7).</item>
/// <item><c>LinkStatus</c>: <c>Linked</c>, <c>NeedsReview</c> (see <c>ReviewReasons</c>) or <c>MatchFailed</c> (the unit could not be tied to one PACT unit).</item>
/// <item><c>FinancialStatus</c>: <c>Available</c> (amounts), <c>NoDues</c> (PACT confirms nothing is due: a confirmed zero), <c>NoFinancialData</c> (nothing usable: NOT zero),
/// <c>MatchFailed</c> (failed or ambiguous unit / company / project match) or <c>SourceError</c> (a source could not be read). Due / Overdue / Total are null unless Available or NoDues.</item>
/// <item><c>FinancialReason</c>: machine-readable cause of anything but Available / NoDues, e.g. <c>ProjectMappingMissing</c>, <c>CompanyMappingAmbiguous</c>, <c>PactHoldsNoRecord</c>,
/// <c>CompanySnapshotNotLoaded</c>, <c>LeasingReceivablesSourceMissing</c>, <c>PactUnavailable</c>.</item>
/// </list>
/// </summary>
public sealed record LinkedUnitCandidateDto(
    string SelectionId, string Source, int? CompanyId, string? TowerNumber, string? TowerName, string? ProjectName, string? UnitNumber, string? UnitKey,
    string LinkStatus, IReadOnlyList<string> ReviewReasons,
    CollectionsUnitPartyDto Customer, int? CrmCustomerId, int? CrmUnitId, int? CrmProjectId, string? PactTenantId, int? PactUnitId, string? ContractNumber, DateOnly? ContractEndDate,
    string FinancialStatus, string? FinancialReason, string? FinancialDetail, decimal? Due, decimal? Overdue, decimal? Total,
    IReadOnlyList<CollectionsUnitInstalmentDto> Instalments, DateOnly AsOf, string PactFreshness);

/// <summary>
/// The answer of the customer / unit linking: every candidate unit with its financial result, and whether the caller has to choose. <c>CrmStatus</c>: <c>Found</c>,
/// <c>NoEligibleUnits</c> (the customer exists but holds no Sold / Contract, not cancelled sale), <c>NotFound</c>, <c>Ambiguous</c> (several CRM customers: nobody chosen),
/// <c>Unavailable</c> or <c>NotSearched</c>. <c>PactStatus</c>: <c>Found</c>, <c>NotFound</c>, <c>Unavailable</c> or <c>NotSearched</c> (PACT is only searched by phone when CRM
/// holds no eligible customer). <c>SelectionRequired</c> is true whenever there is more than one candidate and none was selected; the first is never taken.
/// </summary>
public sealed record CustomerUnitLinkResultDto(
    string PhoneNormalized, string CrmStatus, string PactStatus, IReadOnlyList<LinkedUnitCandidateDto> Candidates, bool SelectionRequired,
    string? SelectedId, IReadOnlyList<string> Notes);
