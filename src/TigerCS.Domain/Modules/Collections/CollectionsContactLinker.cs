namespace TigerCS.Domain.Modules.Collections;

/// <summary>How the CRM side of a unit looks: no eligible sale, exactly one customer, several customers, or CRM not loaded at all.</summary>
public enum CrmLinkStatus
{
    /// <summary>The CRM owner data was never loaded (or is disabled): PACT alone is used, nothing is implied about CRM.</summary>
    Unavailable = 0,
    /// <summary>CRM is loaded and holds no eligible (Sold / Contract, not cancelled) sale of this unit.</summary>
    None = 1,
    /// <summary>Exactly one distinct CRM customer holds an eligible sale of the unit.</summary>
    Single = 2,
    /// <summary>More than one distinct CRM customer holds an eligible sale: nobody is chosen, the match needs review.</summary>
    Ambiguous = 3
}

/// <summary>A contact as one source holds it. Phone / Email are already normalised (empty = missing or invalid); Name is trimmed text (empty = missing).</summary>
public sealed record SourceContact(string Name, string Phone, string Email)
{
    public static readonly SourceContact Empty = new("", "", "");
}

/// <summary>The contact to use and where each value came from (<c>Crm</c>, <c>Pact</c> or empty), plus the review reasons the linking raised.</summary>
public sealed record LinkedContact(
    string Name, string Phone, string Email, string NameSource, string PhoneSource, string EmailSource,
    CrmLinkStatus CrmStatus, bool SourceConflict)
{
    public bool CrmCustomerAmbiguous => CrmStatus == CrmLinkStatus.Ambiguous;
    public bool HasContact => Phone.Length > 0 || Email.Length > 0;
}

/// <summary>
/// The single definition of how CRM and PACT contact data are combined (Collections lists, Campaigns and Payment Summary all use it; SQL mirrors it in
/// <c>usp_Collections_GetCampaignUnits</c>). The rules:
/// <list type="number">
/// <item>CRM first: when exactly one eligible CRM customer holds the unit, its name / phone / e-mail win.</item>
/// <item>PACT completes what CRM lacks (a missing name, phone or e-mail); an existing value is never replaced by an empty one.</item>
/// <item>Several CRM customers: nobody is chosen - CRM is not used at all and the unit is flagged; PACT's own contact stays.</item>
/// <item>Conflict - both sources have a phone and they differ, or (when the phones cannot confirm the same person) both have an e-mail and they differ:
/// two people are never merged. CRM's values alone are used, PACT is not mixed in, and the unit is flagged for review.</item>
/// </list>
/// </summary>
public static class CollectionsContactLinker
{
    public static LinkedContact Link(SourceContact pact, SourceContact crm, CrmLinkStatus status)
    {
        if (status != CrmLinkStatus.Single)
            return new(pact.Name, pact.Phone, pact.Email, pact.Name.Length > 0 ? "Pact" : "", pact.Phone.Length > 0 ? "Pact" : "", pact.Email.Length > 0 ? "Pact" : "", status, false);

        var phonesConfirm = crm.Phone.Length > 0 && pact.Phone.Length > 0 && crm.Phone == pact.Phone;
        var conflict = (crm.Phone.Length > 0 && pact.Phone.Length > 0 && crm.Phone != pact.Phone)
            || (crm.Email.Length > 0 && pact.Email.Length > 0 && crm.Email != pact.Email && !phonesConfirm);
        // Under a conflict PACT is not mixed into CRM's customer.
        string Pick(string fromCrm, string fromPact) => fromCrm.Length > 0 || conflict ? fromCrm : fromPact;
        string Source(string value, string fromCrm) => value.Length == 0 ? "" : fromCrm.Length > 0 ? "Crm" : "Pact";
        var name = Pick(crm.Name, pact.Name); var phone = Pick(crm.Phone, pact.Phone); var email = Pick(crm.Email, pact.Email);
        return new(name, phone, email, Source(name, crm.Name), Source(phone, crm.Phone), Source(email, crm.Email), status, conflict);
    }

    /// <summary>The review flags a linking raises (CrmCustomerAmbiguous, ContactSourceConflict) and NoValidContact when the linked contact has neither phone nor e-mail.</summary>
    public static CollectionsCampaignFlags Flags(LinkedContact linked, bool contactRequired)
    {
        var flags = CollectionsCampaignFlags.None;
        if (linked.CrmCustomerAmbiguous) flags |= CollectionsCampaignFlags.CrmCustomerAmbiguous;
        if (linked.SourceConflict) flags |= CollectionsCampaignFlags.ContactSourceConflict;
        if (contactRequired && !linked.HasContact) flags |= CollectionsCampaignFlags.NoValidContact;
        return flags;
    }
}
