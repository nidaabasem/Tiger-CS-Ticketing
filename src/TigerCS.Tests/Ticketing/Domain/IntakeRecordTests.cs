namespace TigerCS.Tests.Ticketing.Domain;

using TigerCS.Domain.Modules.Ticketing;

public class IntakeRecordTests
{
    private const string Phone = "+971500000001";

    [Fact]
    public void Constructor_BlankPhoneNumber_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new IntakeRecord(Channel.Phone, "", isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public void Constructor_UnitRelatedWithoutRawUnitNumber_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new IntakeRecord(Channel.Phone, Phone, isUnitRelated: true, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public void Constructor_NonUnitRelatedWithRawUnitNumber_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new IntakeRecord(Channel.Phone, Phone, isUnitRelated: false, rawUnitNumberEntered: "1204", priorityHint: null, Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public void Constructor_NonUnitRelated_PreservesPhoneNumberAndStartsUnverifiedAndUnlinked()
    {
        var record = new IntakeRecord(Channel.Phone, Phone, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        Assert.Equal(Phone, record.PhoneNumber);
        Assert.False(record.IsUnitRelated);
        Assert.Equal(CrmVerificationStatus.Unverified, record.CrmVerificationStatus);
        Assert.Null(record.LinkedTicketId);
    }

    [Fact]
    public void LinkToTicket_NonUnitRelated_Succeeds()
    {
        // Business-rule change: a non-unit-related intake may be promoted to
        // a ticket too — only its CrmVerificationStatus differs (Unverified,
        // never Verified/PendingCrmVerification, since it has nothing to
        // verify against the CRM).
        var record = new IntakeRecord(Channel.Phone, Phone, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        record.LinkToTicket(1, CrmVerificationStatus.Unverified);

        Assert.Equal(1, record.LinkedTicketId);
        Assert.Equal(CrmVerificationStatus.Unverified, record.CrmVerificationStatus);
    }

    [Fact]
    public void LinkToTicket_UnitRelatedWithFoundMatch_RecordsVerified()
    {
        // Business-rule change: a customer-lookup match found before ticket
        // creation results in Verified; NotFound/Failed both still promote,
        // just with Unverified instead (see the test above).
        var record = new IntakeRecord(Channel.Phone, Phone, isUnitRelated: true, rawUnitNumberEntered: "1204", priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        record.LinkToTicket(1, CrmVerificationStatus.Verified);

        Assert.Equal(CrmVerificationStatus.Verified, record.CrmVerificationStatus);
    }

    [Fact]
    public void LinkToTicket_AlreadyLinked_Throws()
    {
        var record = new IntakeRecord(Channel.Phone, Phone, isUnitRelated: true, rawUnitNumberEntered: "1204", priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);
        record.LinkToTicket(1, CrmVerificationStatus.Verified);

        Assert.Throws<IntakeRecordAlreadyLinkedException>(() => record.LinkToTicket(2, CrmVerificationStatus.Verified));
    }
}
