using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Domain;

public class IntakeRecordTests
{
    [Fact]
    public void Constructor_UnitRelatedWithoutRawUnitNumber_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new IntakeRecord(Channel.Phone, isUnitRelated: true, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public void Constructor_NonUnitRelatedWithRawUnitNumber_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new IntakeRecord(Channel.Phone, isUnitRelated: false, rawUnitNumberEntered: "1204", priorityHint: null, Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public void Constructor_NonUnitRelated_StartsUnverifiedAndUnlinked()
    {
        var record = new IntakeRecord(Channel.Phone, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        Assert.False(record.IsUnitRelated);
        Assert.Equal(CrmVerificationStatus.Unverified, record.CrmVerificationStatus);
        Assert.Null(record.LinkedTicketId);
    }

    [Fact]
    public void LinkToTicket_NonUnitRelated_Throws()
    {
        var record = new IntakeRecord(Channel.Phone, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        Assert.Throws<IntakeRecordNotUnitRelatedException>(() => record.LinkToTicket(1, CrmVerificationStatus.Verified));
    }

    [Fact]
    public void LinkToTicket_AlreadyLinked_Throws()
    {
        var record = new IntakeRecord(Channel.Phone, isUnitRelated: true, rawUnitNumberEntered: "1204", priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);
        record.LinkToTicket(1, CrmVerificationStatus.Verified);

        Assert.Throws<IntakeRecordAlreadyLinkedException>(() => record.LinkToTicket(2, CrmVerificationStatus.Verified));
    }

    [Fact]
    public void MarkPendingCrmVerification_UnitRelatedAndUnlinked_SetsStatus()
    {
        var record = new IntakeRecord(Channel.Phone, isUnitRelated: true, rawUnitNumberEntered: "1204", priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        record.MarkPendingCrmVerification();

        Assert.Equal(CrmVerificationStatus.PendingCrmVerification, record.CrmVerificationStatus);
    }
}
