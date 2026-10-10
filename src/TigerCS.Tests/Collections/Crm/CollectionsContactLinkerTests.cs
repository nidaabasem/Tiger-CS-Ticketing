using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Crm;

/// <summary>The single rule for combining CRM and PACT contact data (CRM first, PACT completes, never two people merged).</summary>
public sealed class CollectionsContactLinkerTests
{
    private static SourceContact C(string name = "", string phone = "", string email = "") => new(name, phone, email);

    [Fact]
    public void WithoutAnEligibleCrmCustomer_PactAloneIsUsed()
    {
        var linked = CollectionsContactLinker.Link(C("Pact Name", "+971500000001", "p@x.test"), SourceContact.Empty, CrmLinkStatus.None);
        Assert.Equal(("Pact Name", "+971500000001", "p@x.test", "Pact", "Pact", "Pact"), (linked.Name, linked.Phone, linked.Email, linked.NameSource, linked.PhoneSource, linked.EmailSource));
        Assert.False(linked.SourceConflict); Assert.True(linked.HasContact);
    }

    [Fact]
    public void CrmWins_AndPactCompletesWhatCrmLacks_NeverReplacingAValueWithAnEmptyOne()
    {
        // PACT has no mobile at all; CRM has name + mobile but no e-mail: the e-mail is completed from PACT.
        var linked = CollectionsContactLinker.Link(C("Pact Name", "", "p@x.test"), C("Crm Name", "+971501111111", ""), CrmLinkStatus.Single);
        Assert.Equal(("Crm Name", "+971501111111", "p@x.test"), (linked.Name, linked.Phone, linked.Email));
        Assert.Equal(("Crm", "Crm", "Pact"), (linked.NameSource, linked.PhoneSource, linked.EmailSource));
        Assert.False(linked.SourceConflict);
        // CRM empty everywhere: nothing is blanked out, PACT fills all three.
        var empty = CollectionsContactLinker.Link(C("P", "+971502222222", "p@x.test"), C(), CrmLinkStatus.Single);
        Assert.Equal(("P", "+971502222222", "p@x.test"), (empty.Name, empty.Phone, empty.Email));
        // PACT empty: CRM alone.
        var crmOnly = CollectionsContactLinker.Link(C(), C("Crm", "+971503333333", "c@x.test"), CrmLinkStatus.Single);
        Assert.Equal(("Crm", "+971503333333", "c@x.test"), (crmOnly.Name, crmOnly.Phone, crmOnly.Email));
    }

    [Fact]
    public void DifferentPhones_AreTwoPeople_NotMerged_AndFlagged()
    {
        var linked = CollectionsContactLinker.Link(C("Pact Person", "+971500000001", "pact@x.test"), C("Crm Person", "+971509999999", ""), CrmLinkStatus.Single);
        Assert.True(linked.SourceConflict);
        Assert.Equal(("Crm Person", "+971509999999", ""), (linked.Name, linked.Phone, linked.Email));   // CRM alone: PACT's e-mail is NOT borrowed for the CRM customer
        Assert.True(CollectionsContactLinker.Flags(linked, true).HasFlag(CollectionsCampaignFlags.ContactSourceConflict));
    }

    [Fact]
    public void DifferentEmails_ConflictOnlyWhenThePhonesCannotConfirmTheSamePerson()
    {
        Assert.False(CollectionsContactLinker.Link(C("A", "+971500000001", "old@x.test"), C("A", "+971500000001", "new@x.test"), CrmLinkStatus.Single).SourceConflict);   // same phone: same person
        Assert.True(CollectionsContactLinker.Link(C("A", "", "old@x.test"), C("A", "", "new@x.test"), CrmLinkStatus.Single).SourceConflict);                         // nothing confirms it
        Assert.True(CollectionsContactLinker.Link(C("A", "", "old@x.test"), C("A", "+971500000001", "new@x.test"), CrmLinkStatus.Single).SourceConflict);
    }

    [Fact]
    public void SeveralCrmCustomers_NobodyIsChosen_CrmIsNotUsed_AndTheUnitNeedsReview()
    {
        var linked = CollectionsContactLinker.Link(C("Pact", "+971500000001", ""), C("Crm One", "+971501111111", "one@x.test"), CrmLinkStatus.Ambiguous);
        Assert.Equal(("Pact", "+971500000001", ""), (linked.Name, linked.Phone, linked.Email));
        Assert.True(linked.CrmCustomerAmbiguous);
        Assert.True(CollectionsContactLinker.Flags(linked, true).HasFlag(CollectionsCampaignFlags.CrmCustomerAmbiguous));
    }

    [Theory]
    [InlineData("", "", true, true)]
    [InlineData("+971500000001", "", true, false)]
    [InlineData("", "a@x.test", true, false)]
    [InlineData("", "", false, false)]     // the internal legal referral needs no contact
    public void NoContactMethod_FlagsNoValidContact_OnlyWhenOneIsRequired(string phone, string email, bool required, bool expected) =>
        Assert.Equal(expected, CollectionsContactLinker.Flags(CollectionsContactLinker.Link(C("N", phone, email), SourceContact.Empty, CrmLinkStatus.None), required).HasFlag(CollectionsCampaignFlags.NoValidContact));

    [Theory]
    [InlineData("TP140-101", "TP140-101")]
    [InlineData(" tp140-101 ", "TP140-101")]
    [InlineData("140-101", "TP140-101")]
    [InlineData("TP136-C-402", "TP136-C-402")]
    [InlineData("", null)]
    [InlineData("TP", null)]
    public void TheUnitKey_IsTowerPlusUnit_NormalisedTheSameWayEverywhere(string code, string? key) => Assert.Equal(key, CollectionsUnitKey.Normalize(code));

    [Fact]
    public void ACrmUnit_GetsTheSameKeyAsItsPactUnitCode_AndTowersNeverMix()
    {
        Assert.Equal(CollectionsUnitKey.Normalize("TP140-101"), CollectionsUnitKey.FromCrm("TP140", "101"));
        Assert.Equal(CollectionsUnitKey.Normalize("TP140-101"), CollectionsUnitKey.FromCrm("140", " 101 "));
        Assert.Equal("TP136-C-402", CollectionsUnitKey.FromCrm("TP136", "C-402"));
        Assert.NotEqual(CollectionsUnitKey.FromCrm("TP140", "101"), CollectionsUnitKey.FromCrm("TP141", "101"));   // the same apartment number in another tower is another unit
        Assert.Null(CollectionsUnitKey.FromCrm("", "101")); Assert.Null(CollectionsUnitKey.FromCrm("TP140", " "));
    }

    [Theory]
    [InlineData("TP140-513*", false)]
    [InlineData("TP140-0", true)]
    [InlineData("0", false)]
    [InlineData(" ", false)]
    [InlineData("TP140-513", true)]
    public void CancelledAndInvalidUnitCodes_AreNotListable(string code, bool listable) => Assert.Equal(listable, CollectionsUnitKey.IsListable(code));
}
