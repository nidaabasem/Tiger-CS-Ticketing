using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Abstractions;

/// <summary>One CRM sale relation as the bulk owner feed returns it (<c>GET /TicketingSystem/GetUnitOwners</c>).</summary>
public sealed record CrmUnitOwnerDto(
    int LeadId, int LeadStatus, string? LeadStatusName, int CustomerType, int CustomerId,
    string? FullNameEnglish, string? FullNameArabic, string? MobileNumber, string? Email,
    int UnitId, string? UnitNumber, int ProjectId, string? ProjectCode, string? ProjectName);

public enum CrmUnitOwnersOutcome { Success, Unauthorized, InvalidResponse, Unavailable, NotConfigured }

public sealed record CrmUnitOwnersPage(CrmUnitOwnersOutcome Outcome, IReadOnlyList<CrmUnitOwnerDto> Owners, int? Total = null, string? Message = null);

/// <summary>Bulk, paged read of every unit's CRM sale relations (Sold / Contract leads). Read-only; one request per page, never one per unit.</summary>
public interface ICrmUnitOwnersGateway
{
    Task<CrmUnitOwnersPage> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken);
}

/// <summary>One stored owner row: the unit key it links to, the CRM identifiers and the contact values (phone / e-mail normalised, empty = missing or invalid).</summary>
public sealed record CrmOwnerRow(
    string UnitKey, string ProjectCode, string UnitNumber, int LeadId, int LeadStatus, int CustomerId, int UnitId, int ProjectId,
    string FullName, string Mobile, string Email, string PhoneNorm, string EmailNorm);

public sealed record CrmOwnerState(bool Loaded, DateTime? LastSuccessUtc, DateTime? LastAttemptUtc, string? LastAttemptStatus, string? LastError, int RowCount, int UnlinkedRows);

/// <summary>The CRM side of one unit, aggregated: how many distinct customers hold an eligible sale and, when exactly one, its contact.</summary>
public sealed record CrmUnitLink(CrmLinkStatus Status, int Customers, int CustomerId, int CrmUnitId, int LeadId, SourceContact Contact);

/// <summary>Stores the bulk CRM owner feed and answers bulk link reads (one set-based query for any number of units).</summary>
public interface ICollectionsCrmOwnerStore
{
    /// <summary>Stores a complete new run and publishes it atomically; the previous run is kept until then. Returns the number of rows that could be tied to one company.</summary>
    Task<int> ReplaceAsync(IReadOnlyList<CrmOwnerRow> rows, CancellationToken cancellationToken);
    Task RecordFailureAsync(string message, CancellationToken cancellationToken);
    Task<CrmOwnerState> GetStateAsync(CancellationToken cancellationToken);
    /// <summary>The CRM link of each (company, unit key); a unit CRM does not hold is absent from the result. <c>Unavailable</c> is never returned here: callers use <see cref="GetStateAsync"/>.</summary>
    Task<IReadOnlyDictionary<(int CompanyId, string UnitKey), CrmUnitLink>> GetLinksAsync(IReadOnlyList<(int CompanyId, string UnitKey)> units, CancellationToken cancellationToken);
}
