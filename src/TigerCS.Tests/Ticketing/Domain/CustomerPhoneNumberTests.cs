using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// A database query cannot call <see cref="CustomerPhoneNumber.Normalize"/>,
    /// so the repositories drop the separators in SQL with a chained
    /// <c>REPLACE()</c>. That is a mirror of the canonical rule, not a second
    /// implementation of it — and a mirror is only safe while it still
    /// matches. This asserts that every copy strips exactly
    /// <see cref="CustomerPhoneNumber.SeparatorCharacters"/> and that they are
    /// all character-identical, so one of them cannot quietly drift.
    /// </summary>
    [Fact]
    public void EverySqlSideCopyOfTheRule_StripsExactlyTheCanonicalSeparators_AndIsIdentical()
    {
        var chains = new List<(string File, string Chain)>();
        foreach (var file in new[] { "TicketRepository.cs", "CustomerDirectoryRepository.cs" })
        {
            var path = RepositorySource(file);
            Assert.True(File.Exists(path), $"{path} not found — did the repository move?");

            foreach (Match match in Regex.Matches(File.ReadAllText(path), @"Trim\(\)(?:\.Replace\(""[^""]*"", """"\))+"))
            {
                chains.Add((file, match.Value));
            }
        }

        // Both search predicates and the directory's canonical projection.
        Assert.True(chains.Count >= 3, $"Expected at least three SQL-side copies, found {chains.Count}.");
        Assert.Single(chains.Select(c => c.Chain).Distinct(StringComparer.Ordinal));

        // The characters the chain removes ARE the canonical separator set —
        // no more (which would drop a digit) and no fewer (which would leave
        // "+971…" and "971…" as two customers again).
        var stripped = Regex.Matches(chains[0].Chain, @"\.Replace\(""([^""]*)"", """"\)")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.Equal(
            CustomerPhoneNumber.SeparatorCharacters.Select(c => c.ToString()).OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            stripped.OrderBy(c => c, StringComparer.Ordinal).ToArray());

        // And the mirror agrees with the authority on a real number.
        var sqlResult = "+971 50 972-4162".Trim();
        foreach (var separator in stripped)
        {
            sqlResult = sqlResult.Replace(separator, string.Empty, StringComparison.Ordinal);
        }

        Assert.Equal(CustomerPhoneNumber.Normalize("+971 50 972-4162"), sqlResult);
    }

    private static string RepositorySource(string fileName, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..", ".."));
        return Path.Combine(srcDir, "TigerCS.Infrastructure", "Modules", "Ticketing", "Repositories", fileName);
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
