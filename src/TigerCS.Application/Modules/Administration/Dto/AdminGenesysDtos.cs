namespace TigerCS.Application.Modules.Administration.Dto;

/// <summary>One configured Genesys Queue → Department mapping.</summary>
/// <param name="GenesysQueueMappingId">The mapping.</param>
/// <param name="QueueId">Genesys' own queue identifier — the routing key inbound inquiries carry.</param>
/// <param name="QueueName">Display name for administrators, where known. Never used for matching.</param>
/// <param name="DepartmentId">The TigerCS department inquiries from this queue belong to.</param>
/// <param name="DepartmentName">That department's name.</param>
/// <param name="IsActive">Whether the mapping is currently used to resolve a department.</param>
public sealed record AdminGenesysQueueMappingDto(
    int GenesysQueueMappingId,
    string QueueId,
    string? QueueName,
    int DepartmentId,
    string DepartmentName,
    bool IsActive);

/// <summary>Create or edit a Genesys queue mapping. The queue id is the identity: it is set once and never edited.</summary>
/// <param name="QueueId">Required on create, ignored on edit. Genesys' queue identifier, unique (case-insensitive).</param>
/// <param name="QueueName">Optional display name.</param>
/// <param name="DepartmentId">Required. Must be an active department.</param>
/// <param name="IsActive">Whether the mapping resolves a department. Defaults to true.</param>
public sealed record SaveGenesysQueueMappingRequestDto(
    string QueueId,
    string? QueueName,
    int DepartmentId,
    bool IsActive = true);

/// <summary>A department's Genesys settings — chiefly the category its unclassified Genesys tickets are created under.</summary>
/// <param name="DepartmentId">The department.</param>
/// <param name="DepartmentName">Its name.</param>
/// <param name="DefaultCategoryId">The category Genesys-created tickets of this department start under.</param>
/// <param name="DefaultCategoryName">That category's name.</param>
/// <param name="IsActive">Whether the department currently accepts Genesys inquiries. Inactive means ingestion reports a configuration gap rather than guessing a category.</param>
public sealed record AdminGenesysDepartmentSettingsDto(
    int DepartmentId,
    string DepartmentName,
    int DefaultCategoryId,
    string? DefaultCategoryName,
    bool IsActive);

/// <summary>Configure a department for Genesys inquiries.</summary>
/// <param name="DefaultCategoryId">Required. Must be an ACTIVE category of this same department — Genesys tickets are never routed to another department's category.</param>
/// <param name="IsActive">Whether the department accepts Genesys inquiries. Defaults to true.</param>
public sealed record SaveGenesysDepartmentSettingsRequestDto(int DefaultCategoryId, bool IsActive = true);
