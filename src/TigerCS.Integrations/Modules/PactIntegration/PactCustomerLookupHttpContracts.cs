namespace TigerCS.Integrations.Modules.PactIntegration;

/// <summary>
/// The real wire shape of PACT's <c>GET v1/contracts/{mobile}</c> JSON body:
/// a single <c>data</c> array of flat customer/tenant-contract-unit rows —
/// NOT a customer object with a nested contracts array. One row per
/// contract; the same tenant appears once per contract, so
/// <see cref="PactCustomerHttpGateway"/> groups rows by <c>tenantID</c> (the
/// primary external PACT customer/tenant identifier) into one
/// <c>PactCustomerMatchDto</c> carrying ALL of that tenant's contracts/units.
/// Deserialized with <c>PropertyNameCaseInsensitive</c> so these PascalCase
/// record properties bind to PACT's camelCase-with-ID-suffix JSON
/// (<c>tenantID</c>, <c>contractID</c>, …) without per-member attributes.
///
/// <para>
/// Every member is nullable on purpose — an absent PACT field stays null
/// rather than failing the whole body. PACT's financial fields
/// (<c>contractNetAmount</c>, <c>contractDiscountAmount</c>,
/// <c>contractServicesNetAmount</c>, <c>contractServicesDiscountAmount</c>,
/// <c>grossArea</c>, <c>netArea</c>) are deliberately NOT modeled: the
/// Application contract carries no financial data, and nothing financial is
/// persisted into Ticketing just because PACT returns it — the deserializer
/// simply ignores those properties.
/// </para>
///
/// <para>
/// Never referenced outside <see cref="PactCustomerHttpGateway"/> —
/// everything past the gateway sees only the mapped
/// <c>TigerCS.Application.Modules.CustomerVerification.PactIntegration</c>
/// contracts.
/// </para>
/// </summary>
internal sealed record PactContractsHttpResponse(List<PactContractRowHttpDto>? Data);

internal sealed record PactContractRowHttpDto(
    long? TenantID,
    int? CompanyID,
    string? ProjectCode,
    string? ProjectName,
    long? UnitID,
    string? UnitCode,
    string? UnitType,
    string? UnitNumber,
    string? UnitStatus,
    long? ContractID,
    DateTime? ContractStartDate,
    DateTime? ContractEndDate,
    string? CustomerMobile,
    string? CustomerName,
    string? CustomerEmail,
    int? CustomerBuyerType);

/// <summary>
/// The wire shape of <c>GET v1/contracts/{mobile}/customer-type</c>'s JSON
/// body — fallback only: the contracts response's own <c>customerBuyerType</c>
/// is authoritative, and this endpoint is called solely when that field came
/// back null/absent (see <see cref="PactCustomerHttpGateway"/>'s remarks).
/// </summary>
internal sealed record PactCustomerTypeHttpResponse(string? TenantId, string? CustomerType);
