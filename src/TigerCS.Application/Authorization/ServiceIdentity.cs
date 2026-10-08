namespace TigerCS.Application.Authorization;

/// <summary>
/// Configuration of the non-human <b>service identities</b> (the Genesys
/// integration account that TigerGroupWeb signs in as). A service identity is
/// still a "CS Agent" employee in Identity, because the agreed model gives the
/// AI / Genesys agent the CS Agent permission set; what distinguishes it from
/// a human is this list. Listed accounts are confined to the integration
/// surface (see <c>ServiceIdentityRestrictionHandler</c>) and are never shown
/// as people (Team Performance).
///
/// <para>
/// The ids in <c>Collections:Authorization:IntegrationEmployeeIds</c> are
/// service identities too, so an account granted the Collections integration
/// grant is automatically confined; <see cref="EmployeeIds"/> lets an
/// environment without Collections name the Genesys account on its own.
/// </para>
/// </summary>
public sealed class ServiceIdentityOptions
{
    public const string SectionName = "Authorization:ServiceIdentity";

    /// <summary>TigerCS employee ids of integration service accounts.</summary>
    public List<Guid> EmployeeIds { get; set; } = [];
}

/// <summary>Answers whether an employee is a configured non-human service identity.</summary>
public interface IServiceIdentityRegistry
{
    bool IsServiceIdentity(Guid employeeId);

    IReadOnlyCollection<Guid> ServiceIdentityIds { get; }
}
