namespace TigerCS.Domain.Modules.CustomerVerification;

/// <summary>
/// CRM cache row for a unit's contacts (owner/tenant/representative) —
/// never master data (ADR-0006, MVP-Data-Dictionary.md §2.7).
/// </summary>
public class ContactReference
{
    public int ContactReferenceId { get; private set; }
    public string CrmContactId { get; private set; } = string.Empty;
    public int UnitReferenceId { get; private set; }
    public string? DisplayName { get; private set; }
    public string? ContactChannel { get; private set; }
    public ContactType ContactType { get; private set; }

    /// <summary>Self-ref — supports ISSUE-007's CRM-recorded-authorization requirement.</summary>
    public int? AuthorizedRepresentativeOfContactReferenceId { get; private set; }

    public DateTime LastSyncedAtUtc { get; private set; }

    private ContactReference() { }

    public ContactReference(
        string crmContactId,
        int unitReferenceId,
        string? displayName,
        string? contactChannel,
        ContactType contactType,
        int? authorizedRepresentativeOfContactReferenceId,
        DateTime syncedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(crmContactId))
        {
            throw new ArgumentException("CrmContactId is required.", nameof(crmContactId));
        }

        CrmContactId = crmContactId;
        UnitReferenceId = unitReferenceId;
        DisplayName = displayName;
        ContactChannel = contactChannel;
        ContactType = contactType;
        AuthorizedRepresentativeOfContactReferenceId = authorizedRepresentativeOfContactReferenceId;
        LastSyncedAtUtc = syncedAtUtc;
    }

    /// <summary>Refreshes the display cache from a fresh CRM read — never becomes the authoritative record (ADR-0006).</summary>
    public void RefreshFromCrm(
        string? displayName,
        string? contactChannel,
        ContactType contactType,
        int? authorizedRepresentativeOfContactReferenceId,
        DateTime syncedAtUtc)
    {
        DisplayName = displayName;
        ContactChannel = contactChannel;
        ContactType = contactType;
        AuthorizedRepresentativeOfContactReferenceId = authorizedRepresentativeOfContactReferenceId;
        LastSyncedAtUtc = syncedAtUtc;
    }
}
