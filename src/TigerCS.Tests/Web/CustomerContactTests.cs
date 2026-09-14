// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Web;

/// <summary>
/// How a customer's contact points are shown: one row per real way of
/// reaching them, never one row per spelling. The reported bug was a profile
/// that read
/// <code>
/// Mobile Number      971509724162
/// Also called from   +971509724162
/// </code>
/// — the same number twice, because the aliases were compared as raw strings.
/// </summary>
public sealed class CustomerContactTests
{
    private const string Primary = "971509724162";

    /// <summary>Every written form of the primary number is the primary number — none of them is an alias of it.</summary>
    [Theory]
    [InlineData("971509724162")]
    [InlineData("+971509724162")]
    [InlineData("+971 50 972 4162")]
    [InlineData("971-50-972-4162")]
    [InlineData("(971) 50 972 4162")]
    [InlineData("  +971509724162  ")]
    public void OtherPhones_NeverListsTheSameNumberWrittenDifferently(string capturedAlias) =>
        Assert.Empty(CustomerContact.OtherPhones(Primary, [capturedAlias]));

    /// <summary>All four spellings at once still collapse to nothing — the exact case from the bug report.</summary>
    [Fact]
    public void OtherPhones_TheFourReportedSpellings_AreOneContactNumber()
    {
        var others = CustomerContact.OtherPhones(
            Primary,
            ["971509724162", "+971509724162", "+971 50 972 4162", "971-50-972-4162"]);

        Assert.Empty(others);
    }

    /// <summary>A genuinely different number is still an alias — deduplication must not hide a second phone.</summary>
    [Fact]
    public void OtherPhones_AGenuinelyDifferentNumber_IsStillShown()
    {
        var others = CustomerContact.OtherPhones(
            Primary,
            ["+971509724162", "+971501112222", "971 50 972 4162", "971559998877"]);

        // Shown exactly as captured, in the order captured, once each.
        Assert.Equal(["+971501112222", "971559998877"], others.ToArray());
    }

    /// <summary>Two different numbers that were each captured several ways collapse to two rows, not six.</summary>
    [Fact]
    public void OtherPhones_DeduplicatesTheAliasesAgainstEachOtherToo()
    {
        var others = CustomerContact.OtherPhones(
            Primary,
            ["+971501112222", "971-50-111-2222", "971501112222", "+971559998877", "971 55 999 8877"]);

        Assert.Equal(["+971501112222", "+971559998877"], others.ToArray());
    }

    [Fact]
    public void OtherPhones_WithNoPrimary_StillDeduplicatesTheCandidates()
    {
        Assert.Equal(
            ["+971509724162"],
            CustomerContact.OtherPhones(null, ["+971509724162", "971509724162", "971 50 972 4162"]).ToArray());
        Assert.Empty(CustomerContact.OtherPhones(Primary, null));
        Assert.Empty(CustomerContact.OtherPhones(null, []));
    }

    /// <summary>A value with no digits cannot be compared canonically, so it falls back to its own text rather than colliding with every other digit-less value.</summary>
    [Fact]
    public void OtherPhones_ADigitlessValue_IsDeduplicatedOnItsOwnText()
    {
        var others = CustomerContact.OtherPhones(Primary, ["+", "n/a", "n/a", "  ", "+971509724162"]);

        Assert.Equal(["+", "n/a"], others.ToArray());
    }

    // ---- emails ----

    [Fact]
    public void OtherEmails_DeduplicatesAgainstThePrimaryCaseInsensitively()
    {
        var others = CustomerContact.OtherEmails(
            "fatima@example.com",
            ["Fatima@Example.COM", "fatima@example.com", "f.noor@example.com"]);

        Assert.Equal(["f.noor@example.com"], others.ToArray());
    }

    [Fact]
    public void OtherEmails_DeduplicatesTheCandidatesAgainstEachOther()
    {
        var others = CustomerContact.OtherEmails(
            null,
            ["a@example.com", "A@EXAMPLE.COM", "b@example.com", "  ", null!]);

        Assert.Equal(["a@example.com", "b@example.com"], others.ToArray());
        Assert.Empty(CustomerContact.OtherEmails("a@example.com", null));
    }
}
