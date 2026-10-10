namespace TigerCS.Application.Modules.Collections.Abstractions;

/// <summary>
/// One verified link between a CRM project (<c>tblProjects.ID</c>, what GetBuyerByPhone / GetUnitDetails return as <c>projectId</c>) and the PACT tower it is sold as.
/// <c>ProjectCode</c> is the tower's PACT unit-code prefix (<c>TP140</c>), so <c>ProjectCode + "-" + unit number</c> is the PACT unit code. <c>CompanyId</c> is set when the mapping
/// names the PACT company itself; null leaves the company to <c>CollectionsTowers</c>. <c>Source</c> is <c>Manual</c> (verified and entered by an operator) or <c>CrmFeed</c>
/// (derived from the bulk CRM owner feed, which carries both the project id and its code).
/// </summary>
public sealed record CrmProjectTowerMapping(int CrmProjectId, string ProjectCode, int? CompanyId, string Source);

/// <summary>
/// CRM project id -> PACT tower. Display names are never used to build it (two towers can share a name, and a name can be spelled differently in CRM and PACT): a project without a
/// row here is reported as <c>ProjectMappingMissing</c> and its financial data is not looked up.
/// </summary>
public interface ICollectionsCrmProjectMap
{
    /// <summary>Every mapping row of the given CRM projects. A project absent from the result has no verified mapping; several rows for one project are an ambiguous mapping.</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<CrmProjectTowerMapping>>> GetAsync(IReadOnlyCollection<int> crmProjectIds, CancellationToken cancellationToken);
}
