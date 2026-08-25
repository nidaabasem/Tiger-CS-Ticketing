namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>The external customer directories <c>CustomerLookupAppService</c> can search — CRM, PACT, Tasleeh.</summary>
public enum CustomerLookupSource : byte
{
    Crm = 1,
    Pact = 2,
    Tasleeh = 3
}
