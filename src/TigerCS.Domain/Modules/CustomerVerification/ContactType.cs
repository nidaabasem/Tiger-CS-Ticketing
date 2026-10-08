namespace TigerCS.Domain.Modules.CustomerVerification;

/// <summary>MVP-Data-Dictionary.md §2.7 (ContactReferences.ContactType).</summary>
public enum ContactType
{
    Owner = 1,
    Tenant = 2,
    Representative = 3,

    /// <summary>
    /// A CRM <b>Buyer</b> (<c>GetBuyerByPhone</c>'s <c>customerType = 1</c>) —
    /// recorded exactly as CRM labels it. Deliberately not <see cref="Owner"/>:
    /// what "Buyer" means for ownership has not been confirmed by CRM, so
    /// TigerCS does not infer it. Created only by the buyer-lookup cache path
    /// (<c>CrmBuyerVerificationCache</c>); the agent-desk flows never produce it.
    /// </summary>
    Buyer = 4
}
