namespace TigerCS.Integrations.Modules.PactIntegration;

/// <summary>
/// The wire shape of PACT's <c>GET v1/contracts/{mobile}</c> JSON body, as
/// established from the existing, manually verified PACT integration this
/// gateway was rebuilt from (the EDSM customer/contract responses),
/// deserialized with <c>PropertyNameCaseInsensitive</c> so these PascalCase
/// record properties bind to PACT's JSON regardless of its casing. Every
/// member is nullable on purpose — PACT fields that are absent stay null
/// rather than failing the whole body, and only
/// <see cref="PactCustomerHttpGateway"/> decides what a usable response is.
/// Never referenced outside <see cref="PactCustomerHttpGateway"/> —
/// everything past the gateway sees only the mapped
/// <c>TigerCS.Application.Modules.CustomerVerification.PactIntegration</c>
/// contracts.
/// </summary>
internal sealed record PactContractsHttpResponse(
    string? TenantId,
    string? TenantName,
    string? Mobile,
    string? Email,
    string? CustomerType,
    List<PactContractHttpDto>? Contracts);

internal sealed record PactContractHttpDto(
    string? TenantId,
    string? ContractNumber,
    string? UnitCode,
    string? UnitNumber,
    string? ProjectName,
    string? UnitType);

/// <summary>The wire shape of <c>GET v1/contracts/{mobile}/customer-type</c>'s JSON body.</summary>
internal sealed record PactCustomerTypeHttpResponse(string? TenantId, string? CustomerType);
