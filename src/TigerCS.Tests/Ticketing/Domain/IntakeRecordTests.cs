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
    public void LinkToTicket_NonUnitRelated_Succeeds()
    {
        // Business-rule change: a non-unit-related intake may be promoted to
        // a ticket too — only its CrmVerificationStatus differs (Unverified,
        // never Verified/PendingCrmVerification, since it has nothing to
        // verify against the CRM).
        var record = new IntakeRecord(Channel.Phone, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        record.LinkToTicket(1, CrmVerificationStatus.Unverified);

        Assert.Equal(1, record.LinkedTicketId);
        Assert.Equal(CrmVerificationStatus.Unverified, record.CrmVerificationStatus);
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

    [Fact]
    public void MarkPendingCrmVerification_NonUnitRelated_Throws()
    {
        // Unchanged: ISSUE-006's CRM-outage queueing only ever applies to a
        // unit-related request awaiting CRM reconciliation — a non-unit
        // request has nothing to reconcile and is never queued this way.
        var record = new IntakeRecord(Channel.Phone, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        Assert.Throws<IntakeRecordNotUnitRelatedException>(() => record.MarkPendingCrmVerification());
    }
}
