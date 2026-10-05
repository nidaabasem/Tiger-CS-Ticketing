using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Tests.Collections.Services;

public class CollectionsMoneyTests
{
    [Theory]
    [InlineData("44000", "44000.00")]
    [InlineData("10.5000", "10.50")]
    [InlineData("0", "0.00")]
    [InlineData("3333.333", "3333.333")] // more precision than two places: passed through, never rounded
    public void AmountsCarryTwoDecimalPlaces_WithoutRounding(string input, string expected) =>
        Assert.Equal(expected, CollectionsMoney.Two(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture))
            .ToString(System.Globalization.CultureInfo.InvariantCulture));
}
