namespace TigerCS.Tests.Ticketing.Domain;

using TigerCS.Domain.Modules.Ticketing;

public class IntakeRecordTests
{
    private const string Phone = "+971500000001";

    [Fact]
    public void Constructor_BlankPhoneNumber_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new IntakeRecord(Channel.Phone, "", null, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow));
    }

    // ---- RawUnitNumberEntered is optional historical/raw information only,
    // independent of IsUnitRelated in either direction — the coupling the
    // constructor used to enforce is obsolete under the lookup-first
    // workflow, where IsUnitRelated is routinely still false at construction
    // and only later upgraded once a real Unit is selected (LinkToTicket). ----

    [Fact]
    public void Constructor_NotUnitRelatedWithNoRawUnitNumber_Succeeds()
    {
        // Exactly what the current New Ticket wizard sends for every intake:
        // no unit classification and no raw number known yet.
        var record = new IntakeRecord(Channel.Phone, Phone, null, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        Assert.False(record.IsUnitRelated);
        Assert.Null(record.RawUnitNumberEntered);
    }

    [Fact]
    public void Constructor_UnitRelatedWithNoRawUnitNumber_Succeeds()
    {
        // The old constructor invariant required a raw number here — no
        // longer: a caller may classify an interaction unit-related without
        // ever having a raw caller-given number to go with it.
        var record = new IntakeRecord(Channel.Phone, Phone, null, isUnitRelated: true, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        Assert.True(record.IsUnitRelated);
        Assert.Null(record.RawUnitNumberEntered);
    }

    [Fact]
    public void Constructor_NotUnitRelatedWithRawUnitNumber_Succeeds()
    {
        // The old constructor invariant forbade this combination — no
        // longer: RawUnitNumberEntered is a historical note, not evidence
        // that forces classification either way.
        var record = new IntakeRecord(Channel.Phone, Phone, null, isUnitRelated: false, rawUnitNumberEntered: "1204", priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        Assert.False(record.IsUnitRelated);
        Assert.Equal("1204", record.RawUnitNumberEntered);
    }

    [Fact]
    public void Constructor_NonUnitRelated_PreservesPhoneNumberAndStartsUnverifiedAndUnlinked()
    {
        var record = new IntakeRecord(Channel.Phone, Phone, null, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

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
        var record = new IntakeRecord(Channel.Phone, Phone, null, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        record.LinkToTicket(1, CrmVerificationStatus.Unverified, hasSelectedUnit: false);

        Assert.Equal(1, record.LinkedTicketId);
        Assert.Equal(CrmVerificationStatus.Unverified, record.CrmVerificationStatus);
    }

    [Fact]
    public void LinkToTicket_UnitRelatedWithFoundMatch_RecordsVerified()
    {
        // Business-rule change: a customer-lookup match found before ticket
        // creation results in Verified; NotFound/Failed both still promote,
        // just with Unverified instead (see the test above).
        var record = new IntakeRecord(Channel.Phone, Phone, null, isUnitRelated: true, rawUnitNumberEntered: "1204", priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        record.LinkToTicket(1, CrmVerificationStatus.Verified, hasSelectedUnit: true);

        Assert.Equal(CrmVerificationStatus.Verified, record.CrmVerificationStatus);
    }

    [Fact]
    public void LinkToTicket_AlreadyLinked_Throws()
    {
        var record = new IntakeRecord(Channel.Phone, Phone, null, isUnitRelated: true, rawUnitNumberEntered: "1204", priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);
        record.LinkToTicket(1, CrmVerificationStatus.Verified, hasSelectedUnit: true);

        Assert.Throws<IntakeRecordAlreadyLinkedException>(() => record.LinkToTicket(2, CrmVerificationStatus.Verified, hasSelectedUnit: true));
    }

    // ---- IsUnitRelated must never end up false while the linked Ticket has a real Unit reference,
    // and must never be inferred from CrmVerificationStatus/customer verification alone ----

    [Fact]
    public void LinkToTicket_CreatedNotUnitRelated_UnitSelected_UpgradesToUnitRelated_RawUnitNumberStaysNull()
    {
        // The current New Ticket wizard never classifies "unit-related" up
        // front — every intake it creates has IsUnitRelated=false and no raw
        // unit number, deferring identification to customer lookup entirely.
        // hasSelectedUnit=true means the promoted Ticket carries a resolved,
        // already-validated Unit/Contact reference — stronger evidence than
        // the raw unit number ever was — so the record is reclassified, and
        // RawUnitNumberEntered is never backfilled to justify it: the
        // authoritative Unit lives on the Ticket, not this raw string.
        var record = new IntakeRecord(Channel.Phone, Phone, null, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        record.LinkToTicket(1, CrmVerificationStatus.Verified, hasSelectedUnit: true);

        Assert.True(record.IsUnitRelated);
        Assert.Null(record.RawUnitNumberEntered);
    }

    [Fact]
    public void LinkToTicket_NoUnitSelected_UnverifiedOutcome_StaysNotUnitRelated()
    {
        // No resolved Unit reference was ever attached to the Ticket — the
        // record must stay exactly what it was, not be classified unit-related
        // just because a Ticket happened to link.
        var record = new IntakeRecord(Channel.Phone, Phone, null, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        record.LinkToTicket(1, CrmVerificationStatus.Unverified, hasSelectedUnit: false);

        Assert.False(record.IsUnitRelated);
    }

    [Fact]
    public void LinkToTicket_CustomerVerifiedButNoUnitSelected_DoesNotUpgrade()
    {
        // The failure mode this correction rules out: inferring
        // unit-related status merely from customer verification. A Verified
        // CrmVerificationStatus with hasSelectedUnit=false (a customer
        // identity confirmed with no specific Unit chosen) must not flip
        // IsUnitRelated — only an actual selected Unit does that. Proves the
        // decoupling from CrmVerificationStatus: the outcome here would have
        // upgraded the record under the old "resultingStatus == Verified"
        // rule this correction replaced.
        var record = new IntakeRecord(Channel.Phone, Phone, null, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        record.LinkToTicket(1, CrmVerificationStatus.Verified, hasSelectedUnit: false);

        Assert.False(record.IsUnitRelated);
        Assert.Equal(CrmVerificationStatus.Verified, record.CrmVerificationStatus);
    }

    [Fact]
    public void LinkToTicket_HasSelectedUnitTrue_UpgradesRegardlessOfResultingStatus_SourceAgnostic()
    {
        // The classification signal is hasSelectedUnit alone — not which
        // CRM-named verification status accompanies it, and not which
        // lookup source (CRM/PACT/Tasleeh) produced the reference. A real
        // Unit reference is a real Unit reference, whatever status label
        // rides along with it.
        var record = new IntakeRecord(Channel.Phone, Phone, null, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        record.LinkToTicket(1, CrmVerificationStatus.Unverified, hasSelectedUnit: true);

        Assert.True(record.IsUnitRelated);
    }

    [Fact]
    public void LinkToTicket_AlreadyUnitRelated_NoUnitSelectedOnThisTicket_NeverDowngraded()
    {
        // Upgrade-only: a record already unit-related at creation (e.g. a
        // future caller outside the current wizard) must never be pulled
        // back to false by this Ticket's own (unrelated) outcome.
        var record = new IntakeRecord(Channel.Phone, Phone, null, isUnitRelated: true, rawUnitNumberEntered: "1204", priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);

        record.LinkToTicket(1, CrmVerificationStatus.Unverified, hasSelectedUnit: false);

        Assert.True(record.IsUnitRelated);
    }
}
