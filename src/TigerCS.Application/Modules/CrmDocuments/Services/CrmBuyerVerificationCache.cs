using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.CrmDocuments.Services;

/// <summary>
/// Fills the verification unit/contact cache (<c>UnitReferences</c> /
/// <c>ContactReferences</c>) from the <b>real CRM buyer lookup</b>
/// (<c>GetBuyerByPhone</c>) — the one CRM operation that exists — so a
/// verification session has the rows it refers to.
///
/// <para>
/// Mapping, from existing buyer-lookup fields only:
/// </para>
/// <list type="bullet">
/// <item><b>Unit</b> — <c>CrmUnitId</c> = <c>String(unitId)</c> (the same id the document flow matches on), <c>UnitNumber</c> = <c>unitNumber</c>, <c>PropertyName</c> = <c>projectName</c>. Tower and unit-type label stay null: the buyer lookup has neither (<c>unitType</c> is a bare numeric code).</item>
/// <item><b>Contact</b> — <c>CrmContactId</c> = <c>"{customerId}-{unitId}"</c>: <b>unit-scoped</b>, because <c>CrmContactId</c> is globally unique and a contact row belongs to one unit, so one customer on two units needs two rows. Name from <c>fullNameEnglish</c>/<c>fullNameArabic</c>, channel from <c>mobileNumber</c> (the searched number when CRM returns none).</item>
/// <item><b>Contact type</b> — <see cref="ContactType.Buyer"/>, recorded as CRM labels it. <c>customerType = 1</c> is <i>not</i> turned into <see cref="ContactType.Owner"/>: its ownership meaning is unconfirmed.</item>
/// </list>
///
/// <para>
/// The customer, unit and lead relationship is not lost to the cache's shape:
/// the OTP challenge and the session it produces carry CRM's customer id and
/// lead id (see <see cref="VerificationSession.CrmBuyerLeadId"/>).
/// </para>
///
/// <para>
/// <b>Existing agent-desk rows are never degraded.</b> A unit already cached
/// by the unit/contact lookup keeps its tower and unit type; a contact row
/// that already exists with another type keeps that type.
/// </para>
/// </summary>
public sealed class CrmBuyerVerificationCache(
    IUnitReferenceRepository unitRepository,
    IContactReferenceRepository contactRepository,
    ICustomerVerificationUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public static string ContactIdFor(int customerId, int unitId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{customerId}-{unitId}");

    public async Task<(UnitReference Unit, ContactReference Contact)> EnsureAsync(
        CrmBuyerMatchDto buyer, CrmBuyerUnitDto buyerUnit, string searchedPhone, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var crmUnitId = buyerUnit.UnitId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var unit = await unitRepository.GetByCrmUnitIdAsync(crmUnitId, cancellationToken);
        if (unit is null)
        {
            unit = new UnitReference(crmUnitId, buyerUnit.UnitNumber!.Trim(), Clean(buyerUnit.ProjectName), null, null, now);
            await unitRepository.AddAsync(unit, cancellationToken);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (DuplicateWriteException)
            {
                unit = await unitRepository.GetByCrmUnitIdAsync(crmUnitId, cancellationToken) ?? throw new InvalidOperationException("Unit cache row vanished.");
            }
        }
        else
        {
            unit.RefreshFromCrm(buyerUnit.UnitNumber!.Trim(), Clean(buyerUnit.ProjectName) ?? unit.PropertyName, unit.TowerName, unit.UnitType, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var crmContactId = ContactIdFor(buyer.Customer.CustomerId, buyerUnit.UnitId);
        var name = Clean(buyer.Customer.FullNameEnglish) ?? Clean(buyer.Customer.FullNameArabic) ?? $"Customer {buyer.Customer.CustomerId}";
        var channel = CustomerPhoneNumber.LooksLikeNumber(buyer.Customer.MobileNumber) ? buyer.Customer.MobileNumber!.Trim() : searchedPhone;

        var contact = await contactRepository.GetByCrmContactIdAsync(crmContactId, cancellationToken);
        if (contact is null)
        {
            contact = new ContactReference(crmContactId, unit.UnitReferenceId, name, channel, ContactType.Buyer, null, now);
            await contactRepository.AddAsync(contact, cancellationToken);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (DuplicateWriteException)
            {
                contact = await contactRepository.GetByCrmContactIdAsync(crmContactId, cancellationToken) ?? throw new InvalidOperationException("Contact cache row vanished.");
            }
        }
        else
        {
            contact.RefreshFromCrm(name, channel, contact.ContactType, contact.AuthorizedRepresentativeOfContactReferenceId, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        if (contact.UnitReferenceId != unit.UnitReferenceId)
        {
            // A unit-scoped id can only collide if CRM reuses ids across units. Refuse rather than bind a session to the wrong unit.
            throw new BuyerCacheConflictException(crmContactId);
        }

        return (unit, contact);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The cached contact row for this id belongs to a different unit — CRM data the cache cannot represent safely.</summary>
public sealed class BuyerCacheConflictException(string crmContactId)
    : Exception($"Cached contact '{crmContactId}' belongs to a different unit.");
