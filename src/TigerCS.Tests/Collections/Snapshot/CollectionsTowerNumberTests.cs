using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Snapshot;

public sealed class CollectionsTowerNumberTests
{
    [Theory]
    [InlineData("TP124-1001", "124")]
    [InlineData("124-1001", "124")]
    [InlineData("TP136-C-402", "136")]
    [InlineData("136-C-402", "136")]
    [InlineData("tp119-3005", "119")]
    [InlineData("  TP140-101  ", "140")]
    [InlineData("TP 127-12", "127")]
    [InlineData("TP118-2308-A", "118")]
    [InlineData("TP124", "124")]
    [InlineData("124", "124")]
    public void TowerIsTheComponentBeforeTheFirstHyphen_AfterAnOptionalLeadingTP(string unit, string expected) =>
        Assert.Equal(expected, CollectionsTowerNumber.Parse(unit));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TP")]
    [InlineData("TP-101")]
    [InlineData("-101")]
    public void BlankOrEmptyComponentHasNoTower(string? unit) => Assert.Null(CollectionsTowerNumber.Parse(unit));

    [Fact]
    public void OnlyOneLeadingTPIsRemoved() => Assert.Equal("TP124", CollectionsTowerNumber.Parse("TPTP124-1"));

    [Theory]
    [InlineData("124", "124", true)]
    [InlineData("124", " 124 ", true)]
    [InlineData("124", "125", false)]
    [InlineData("124", null, false)]
    [InlineData(null, "124", false)]
    [InlineData("A", "a", true)]
    public void MatchingIsTrimmedText(string? fromUnit, string? fromTable, bool expected) =>
        Assert.Equal(expected, CollectionsTowerNumber.Matches(fromUnit, fromTable));
}
