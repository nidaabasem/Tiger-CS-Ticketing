using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Domain;

/// <summary>
/// The one canonical phone rule, and the identity built on it: a number is
/// the same customer's however it was written, the phone fallback key is
/// the canonical value, and the stronger identities are never merged by it.
/// </summary>
public sealed class CustomerPhoneNumberTests
{
    [Theory]
    [InlineData("+971501234567")]
    [InlineData("971501234567")]
    [InlineData("+971 50 123 4567")]
    [InlineData("971-50-123-4567")]
    [InlineData("+971-50-123-4567")]
    [InlineData("  +971 (50) 123-4567  ")]
    [InlineData("(+971) 50 123 4567")]
    public void Normalize_EveryWrittenFormOfOneNumber_IsTheSameDigits(string phoneNumber) =>
        Assert.Equal("971501234567", CustomerPhoneNumber.Normalize(phoneNumber));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    [InlineData("()- ")]
    public void Normalize_ANumberWithNoDigits_IsEmpty(string? phoneNumber) =>
        Assert.Equal(string.Empty, CustomerPhoneNumber.Normalize(phoneNumber));

    [Fact]
    public void Normalize_KeepsDigitOrder_AndDoesNotConflateDifferentNumbers()
    {
        Assert.Equal("971509999000", CustomerPhoneNumber.Normalize("+971 50 999 9000"));
        Assert.NotEqual(CustomerPhoneNumber.Normalize("+971501234567"), CustomerPhoneNumber.Normalize("+971501234576"));
    }

    [Fact]
    public void AreSameNumber_ComparesTheCanonicalForm_AndNeverMatchesANumberWithoutDigits()
    {
        Assert.True(CustomerPhoneNumber.AreSameNumber("+971501234567", "971501234567"));
        Assert.True(CustomerPhoneNumber.AreSameNumber("+971 50 123 4567", "971-50-123-4567"));
        Assert.False(CustomerPhoneNumber.AreSameNumber("+971501234567", "+971509999000"));
        Assert.False(CustomerPhoneNumber.AreSameNumber("+", "+"));
        Assert.False(CustomerPhoneNumber.AreSameNumber(null, null));
    }

    /// <summary>PACT's request form is deliberately narrower — only the '+' goes, so a number reaches PACT as PACT holds it.</summary>
    [Fact]
    public void WithoutPlus_IsThePactRequestForm_NotTheCanonicalForm()
    {
        Assert.Equal("971501234567", CustomerPhoneNumber.WithoutPlus("  +971501234567  "));
        Assert.Equal("971 50 123 4567", CustomerPhoneNumber.WithoutPlus("+971 50 123 4567"));
        Assert.Equal(string.Empty, CustomerPhoneNumber.WithoutPlus("+"));
    }

    // ---- the identity built on it ----

    [Theory]
    [InlineData("+971501234567")]
    [InlineData("971501234567")]
    [InlineData("+971 50 123 4567")]
    [InlineData("971-50-123-4567")]
    public void PhoneFallbackKey_IsTheCanonicalNumber_HoweverItWasCaptured(string phoneNumber)
    {
        Assert.Equal("phone:971501234567", CustomerIdentity.Phone(phoneNumber).Key);
        Assert.Equal("phone:971501234567", CustomerIdentity.FromTicketFacts(null, null, null, phoneNumber)!.Key);
    }

    [Theory]
    [InlineData("phone:971501234567")]
    [InlineData("phone:%2B971501234567")]
    [InlineData("phone:+971501234567")]
    [InlineData("phone:971%2050%20123%204567")]
    [InlineData("phone:971-50-123-4567")]
    public void ParsingAnyWrittenFormOfAPhoneKey_YieldsTheOneCanonicalIdentity(string key)
    {
        Assert.True(CustomerIdentity.TryParse(key, out var identity));
        Assert.Equal(CustomerIdentity.Phone("+971501234567"), identity);
        Assert.Equal("phone:971501234567", identity.Key);
    }

    [Fact]
    public void AKeyWhosePhoneHasNoDigits_IsNotAnIdentity()
    {
        Assert.False(CustomerIdentity.TryParse("phone:", out _));
        Assert.False(CustomerIdentity.TryParse("phone:%2B", out _));
        Assert.Null(CustomerIdentity.FromTicketFacts(null, null, null, "+"));
        Assert.Null(CustomerIdentity.FromTicketFacts(null, null, null, " "));
    }

    /// <summary>
    /// Precedence is unchanged: CRM first, then the external verified
    /// identity, and only then the canonical phone. Canonicalizing the
    /// phone never promotes it over either.
    /// </summary>
    [Fact]
    public void IdentityPrecedence_IsUnchanged_CrmThenExternalThenCanonicalPhone()
    {
        Assert.Equal(CustomerIdentity.Crm(7), CustomerIdentity.FromTicketFacts(7, "Pact", "PACT-1", "+971501234567"));
        Assert.Equal(CustomerIdentity.External("Pact", "PACT-1"), CustomerIdentity.FromTicketFacts(null, "Pact", "PACT-1", "+971501234567"));
        Assert.Equal(CustomerIdentity.Phone("971501234567"), CustomerIdentity.FromTicketFacts(null, "Pact", null, "+971501234567"));

        // Two CRM customers, or two external customers, sharing one phone
        // stay two identities — the phone never merges them.
        Assert.NotEqual(
            CustomerIdentity.FromTicketFacts(7, null, null, "+971501234567"),
            CustomerIdentity.FromTicketFacts(8, null, null, "971501234567"));
        Assert.NotEqual(
            CustomerIdentity.FromTicketFacts(null, "Pact", "PACT-1", "+971501234567"),
            CustomerIdentity.FromTicketFacts(null, "Pact", "PACT-2", "971501234567"));
        Assert.NotEqual(
            CustomerIdentity.FromTicketFacts(null, "Pact", "PACT-1", "+971501234567"),
            CustomerIdentity.FromTicketFacts(null, "Tasleeh", "PACT-1", "971501234567"));
    }
}
