using System.Text.Json;

namespace TigerCS.Tests.GenesysIntegration.Contracts;

/// <summary>Pins the behaviour of the two simulators the data-action contract tests rely on, so a wrong simulator cannot make a broken file pass.</summary>
public sealed class TemplateSimulatorTests
{
    private static Dictionary<string, object?> Ctx(params (string Key, object? Value)[] input) => new()
    {
        ["input"] = input.ToDictionary(i => i.Key, i => i.Value)
    };

    [Fact]
    public void AnUnresolvedReference_IsEmittedLiterally_UnlessItIsSilent()
    {
        Assert.Equal("[${input.x}]", VelocityLite.Render("[${input.x}]", Ctx()));
        Assert.Equal("[]", VelocityLite.Render("[$!{input.x}]", Ctx()));
        Assert.Equal("[]", VelocityLite.Render("[$!esc.jsonString($input.x)]", Ctx()));
        Assert.Equal("[$esc.jsonString(${input.x})]", VelocityLite.Render("[$esc.jsonString(${input.x})]", Ctx()));
    }

    [Fact]
    public void JsonString_EscapesQuotesBackslashesAndControlCharacters_AndLeavesArabicAlone()
    {
        var rendered = VelocityLite.Render("\"$esc.jsonString(${input.v})\"", Ctx(("v", "a\"b\\c\nd\u0001 محمد")));

        Assert.Equal("a\"b\\c\nd\u0001 محمد", JsonSerializer.Deserialize<string>(rendered));
    }

    [Fact]
    public void If_UsesNullAndFalseAsFalse_AndAnEmptyStringAsTrue()
    {
        Assert.Equal("yes", VelocityLite.Render("#if($input.x)yes#else no#end", Ctx(("x", 0))));
        Assert.Equal("yes", VelocityLite.Render("#if($input.x)yes#else no#end", Ctx(("x", ""))));
        Assert.Equal(" no", VelocityLite.Render("#if($input.x)yes#else no#end", Ctx(("x", null))));
        Assert.Equal(" no", VelocityLite.Render("#if($input.x)yes#else no#end", Ctx(("x", false))));
    }

    [Fact]
    public void If_ComparesQuotedInterpolatedStrings()
    {
        const string template = "#if(\"$!{input.d}\" == \"\")null#else\"$esc.jsonString(${input.d})\"#end";
        Assert.Equal("null", VelocityLite.Render(template, Ctx()));
        Assert.Equal("null", VelocityLite.Render(template, Ctx(("d", ""))));
        Assert.Equal("\"2026-10-08\"", VelocityLite.Render(template, Ctx(("d", "2026-10-08"))));
    }

    [Fact]
    public void EscUrl_EncodesSeparators_SoAValueCannotLeaveItsPathSegment()
    {
        Assert.Equal("ext%3APact%3A3001%2F..%3Fx", VelocityLite.Render("$esc.url(${input.k})", Ctx(("k", "ext:Pact:3001/..?x"))));
    }

    [Fact]
    public void JsonPath_SupportsPropertiesWildcardsFiltersAndLength()
    {
        using var doc = JsonDocument.Parse("""
            {"a":{"b":"x","n":null},"items":[{"k":"p","v":1},{"k":"q","v":null}],"cos":[{"s":"Available","id":4},{"s":"Down","id":5}]}
            """);

        Assert.Equal("\"x\"", MiniJsonPath.Evaluate(doc.RootElement, "$.a.b"));
        Assert.Null(MiniJsonPath.Evaluate(doc.RootElement, "$.a.n"));
        Assert.Null(MiniJsonPath.Evaluate(doc.RootElement, "$.a.missing"));
        Assert.Equal("[1,null]", MiniJsonPath.Evaluate(doc.RootElement, "$.items[*].v"));
        Assert.Equal("2", MiniJsonPath.Evaluate(doc.RootElement, "$.items.length()"));
        Assert.Equal("[4]", MiniJsonPath.Evaluate(doc.RootElement, "$.cos[?(@.s == 'Available')].id"));
        Assert.Equal("[5]", MiniJsonPath.Evaluate(doc.RootElement, "$.cos[?(@.s != 'Available')].id"));
        Assert.Equal("[]", MiniJsonPath.Evaluate(doc.RootElement, "$.cos[?(@.s == 'None')].id"));
    }
}
