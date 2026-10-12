using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Domain;

/// <summary>
/// Newest tower first: the tower NUMBER as an integer, descending (TP140, TP132, TP131 ... TP119) - never the text and never the name - then a fixed order for the units of a tower.
/// </summary>
public sealed class CollectionsTowerOrderTests
{
    private sealed record U(string Code, int Company = 4, string Tenant = "T", long UnitId = 1)
    {
        public string? Tower => CollectionsTowerNumber.Parse(Code);
    }

    private static List<U> Sorted(IEnumerable<U> units)
    {
        var list = units.ToList();
        list.Sort((a, b) => CollectionsTowerOrder.Compare(a.Tower, a.Code, a.Company, a.Tenant, a.UnitId, b.Tower, b.Code, b.Company, b.Tenant, b.UnitId));
        return list;
    }

    [Fact]
    public void Towers_AreOrderedByTheirNumberDescending_TP140First_TP119Last()
    {
        var shuffled = new[] { "TP119-1", "TP131-1", "TP140-1", "TP132-1", "TP127-1", "TP122-1", "TP128-1" }.Select(c => new U(c));
        Assert.Equal(["TP140-1", "TP132-1", "TP131-1", "TP128-1", "TP127-1", "TP122-1", "TP119-1"], Sorted(shuffled).Select(u => u.Code));
    }

    [Fact]
    public void TheTowerNumber_IsAnInteger_NotText_SoNinetyNineIsAfterOneHundredForty()
    {
        // As text "99" > "140" and "119" < "13"; as numbers 140 > 119 > 99 > 13.
        Assert.Equal(["TP140-1", "TP119-1", "TP99-1", "TP13-1"], Sorted(new[] { "TP99-1", "TP13-1", "TP140-1", "TP119-1" }.Select(c => new U(c))).Select(u => u.Code));
        Assert.Equal(140, CollectionsTowerOrder.Number("140"));
        Assert.Equal(140, CollectionsTowerOrder.Number("TP140"));
        Assert.Equal(7, CollectionsTowerOrder.Number(" 007 "));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("1A")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12.5")]
    [InlineData("-3")]
    public void ATowerNumberThatIsNotAPlainInteger_SortsAfterEveryNumericTower(string? number)
    {
        Assert.Null(CollectionsTowerOrder.Number(number));
        Assert.True(CollectionsTowerOrder.Compare("1", "TP1-1", 4, "T", 1, number, "X-1", 4, "T", 1) < 0);
        Assert.True(CollectionsTowerOrder.Compare(number, "X-1", 4, "T", 1, "1", "TP1-1", 4, "T", 1) > 0);
    }

    [Fact]
    public void UnitsOfOneTower_AreOrderedByUnitNumber_NumericBeforeLettered_ThenTheCode()
    {
        var units = new[] { "TP115-1921", "TP115-302", "TP115-C-402", "TP115-1001", "TP115-A12", "TP115-302B" }.Select(c => new U(c));
        // numeric unit numbers ascending (302, 1001, 1921), then the lettered ones by text.
        Assert.Equal(["TP115-302", "TP115-1001", "TP115-1921", "TP115-302B", "TP115-A12", "TP115-C-402"], Sorted(units).Select(u => u.Code));
    }

    [Fact]
    public void TheOrder_IsTotal_SameUnitCodeInTwoCompanies_AndSeveralCustomers_NeverTie()
    {
        var units = new[]
        {
            new U("TP115-100", 32, "B", 9), new U("TP115-100", 4, "Z", 1), new U("TP115-100", 4, "A", 2), new U("TP115-100", 4, "A", 1), new U("TP115-100", 32, "A", 1)
        };
        var order = Sorted(units);
        Assert.Equal([(4, "A", 1L), (4, "A", 2L), (4, "Z", 1L), (32, "A", 1L), (32, "B", 9L)], order.Select(u => (u.Company, u.Tenant, u.UnitId)));
        // Any starting order gives the same result: pages cut from it can never repeat or skip a unit.
        var rng = new Random(7);
        for (var i = 0; i < 20; i++) Assert.Equal(order, Sorted(units.OrderBy(_ => rng.Next())));
    }

    [Fact]
    public void Pages_CutFromTheOrderedWholeSet_AreDisjointAndComplete()
    {
        var all = Enumerable.Range(119, 22).SelectMany(t => Enumerable.Range(1, 7).Select(n => new U($"TP{t}-{n * 101}", t % 2 == 0 ? 4 : 32, "T" + n, t * 10000 + n))).ToList();
        var ordered = Sorted(all.OrderBy(_ => Guid.NewGuid()));
        var pages = Enumerable.Range(0, (ordered.Count + 24) / 25).Select(p => ordered.Skip(p * 25).Take(25).ToList()).ToList();
        Assert.Equal(all.Count, pages.Sum(p => p.Count));
        Assert.Equal(all.Count, pages.SelectMany(p => p).Distinct().Count());                                        // nothing repeated
        Assert.Equal(["TP140-101"], pages[0].Take(1).Select(u => u.Code));                                           // the newest tower leads page 1
        Assert.Equal("119", pages[^1][^1].Tower);                                                                  // the oldest ends the last page
    }

    [Fact]
    public void TheTowerPicker_IsOrderedByNumberDescending_ThenCompanyThenId()
    {
        var towers = new (string Number, int Company, long Id)[] { ("119", 4, 5), ("140", 32, 9), ("99", 4, 1), ("140", 4, 8), ("127", 32, 7), ("A", 4, 2) };
        var sorted = towers.ToList();
        sorted.Sort((a, b) => CollectionsTowerOrder.CompareTowers(a.Number, a.Company, a.Id, b.Number, b.Company, b.Id));
        Assert.Equal([("140", 4L), ("140", 32L), ("127", 32L), ("119", 4L), ("99", 4L), ("A", 4L)], sorted.Select(t => (t.Number, t.Company == 4 ? 4L : 32L)));
    }

    [Theory]
    [InlineData("Blue Wave", "115", true)]
    [InlineData("Tower 119", "119", true)]          // a real name stored as "Tower N" is kept: only a bare repeat of the number is a placeholder
    [InlineData("119", "119", false)]
    [InlineData(" 119 ", "119", false)]
    [InlineData("TP119", "119", false)]
    [InlineData("tp-119", "119", false)]
    [InlineData("", "119", false)]
    [InlineData(null, "119", false)]
    [InlineData("   ", "119", false)]
    public void ATowerName_IsRealUnlessItIsBlankOrJustTheNumber(string? name, string number, bool usable) =>
        Assert.Equal(usable, CollectionsTowerName.IsUsable(name, number));

    [Fact]
    public void TheDisplay_ShowsTheRealName_ElseTheNumber_NeverAMadeUpName()
    {
        Assert.Equal("Blue Wave", CollectionsTowerName.Display("Blue Wave", "115"));
        Assert.Equal("119", CollectionsTowerName.Display("119", "119"));
        Assert.Equal("119", CollectionsTowerName.Display(null, "119"));
        Assert.Equal("—", CollectionsTowerName.Display(null, null));
    }
}

public sealed class InternationalPhoneTests
{
    [Theory]
    [InlineData("+966508650505", "+966508650505")]        // the reported case: a Saudi number kept as it is
    [InlineData("+966 50 865 0505", "+966508650505")]
    [InlineData("00966508650505", "+966508650505")]
    [InlineData("966508650505", "+966508650505")]         // country code written without the "+"
    [InlineData("+44 7911 123456", "+447911123456")]
    [InlineData("+971501234567", "+971501234567")]
    [InlineData("971501234567", "+971501234567")]
    [InlineData("0501234567", "+971501234567")]           // a bare UAE national number is still repaired
    [InlineData("501234567", "+971501234567")]
    [InlineData("+971 (50) 123-4567", "+971501234567")]
    [InlineData("٠٥٠١٢٣٤٥٦٧", "+971501234567")]          // Arabic-Indic digits
    [InlineData("+٩٦٦٥٠٨٦٥٠٥٠٥", "+966508650505")]
    public void ANumber_KeepsItsOwnCountryCode_AndOnlyUaeNationalFormsAreRepaired(string raw, string expected)
    {
        Assert.Equal(expected, CollectionsContactNormalizer.NormalizePhone(raw));
        Assert.Equal(expected, PhoneNormalizer.Normalize(raw).E164);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("+0501234567")]
    [InlineData("+971123")]                                // a UAE number that is not one
    [InlineData("0501234567 / 0509999999")]                // two different numbers in one field are never guessed between
    public void ANumberThatCannotBeConfirmed_IsEmpty(string raw) => Assert.Equal("", CollectionsContactNormalizer.NormalizePhone(raw));

    [Fact]
    public void ASaudiNumber_IsNeverTurnedIntoAUaeNumber()
    {
        foreach (var raw in new[] { "+966508650505", "966508650505", "00966508650505" })
            Assert.StartsWith("+966", CollectionsContactNormalizer.NormalizePhone(raw), StringComparison.Ordinal);
    }
}
