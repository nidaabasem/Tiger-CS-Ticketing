namespace TigerCS.Application.Modules.Collections.Dto;

/// <summary>
/// One Leasing (PACT company 7) contract/unit of a tenant, independent of every other: its own company, tenant, unit and contract. Amounts are null while no company-7
/// receivables source is configured - that is "unavailable", never a zero balance.
/// </summary>
public sealed record CollectionsLeasingContractDto(
    int CompanyId, string PactTenantId, string TenantName, string Mobile, string Email,
    string UnitId, string? UnitNumber, string? ContractNumber, string? ProjectName, string? UnitType, DateOnly? ContractEndDate,
    string FinancialStatus, string? FinancialDetail, decimal? Due, decimal? Overdue, decimal? Total);

/// <summary>The Leasing fallback of the Payment Summary: found by the NORMALISED mobile in PACT under company 7 when the customer has no eligible CRM unit.</summary>
public sealed record CollectionsLeasingSummaryDto(
    string MobileNormalized, string LookupStatus, IReadOnlyList<CollectionsLeasingContractDto> Contracts,
    int ExcludedEndedContracts, int ExcludedOtherCompanies, IReadOnlyList<string> Notes);
