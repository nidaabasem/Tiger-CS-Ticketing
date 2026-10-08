using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.Controllers;

namespace TigerCS.Tests.GenesysIntegration.Contracts;

/// <summary>
/// Source-free drift guard: <c>docs/Genesys/TigerGroupWeb-Forwarding-Implementation.md</c> carries the route table and the C# route list that
/// the (separate) TigerGroupWeb repository must implement. Those must stay equal to what the TigerCS controllers declare - otherwise a new,
/// renamed or re-parameterised Genesys route silently stops working in production because the public proxy never learns about it.
/// </summary>
public sealed partial class TigerGroupWebRouteTableTests
{
    private const string PublicBase = "https://tigergroup.ae";
    private static readonly int[] ProxyOriginatedStatuses = [400, 401, 413, 502, 504];

    private sealed record Row(int Number, string Method, string PublicUrl, string TigerCsUrl, string[] DataActions, string[] Headers, string[] Query, int[] Statuses)
    {
        public string Route => PublicUrl[(PublicBase.Length + 1)..];
    }

    private static string DocPath => Path.Combine(ApiRouteCatalog.RepoRoot(), "docs", "Genesys", "TigerGroupWeb-Forwarding-Implementation.md");

    private static string Doc => File.ReadAllText(DocPath);

    private static IReadOnlyList<Row> Table()
    {
        var text = Doc;
        var begin = text.IndexOf("<!-- ROUTE-TABLE:BEGIN -->", StringComparison.Ordinal);
        var end = text.IndexOf("<!-- ROUTE-TABLE:END -->", StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "The route table markers are missing from the forwarding document.");

        var rows = new List<Row>();
        foreach (var line in text[begin..end].Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("| ", StringComparison.Ordinal)))
        {
            var cells = line.Trim('|').Split('|').Select(c => c.Trim().Trim('`')).ToArray();
            if (!int.TryParse(cells[0], out var number))
            {
                continue; // header row
            }

            Assert.Equal(8, cells.Length);
            rows.Add(new Row(number, cells[1], cells[2], cells[3], List(cells[4]), List(cells[5]), List(cells[6]), List(cells[7]).Select(int.Parse).ToArray()));
        }

        return rows;

        static string[] List(string cell) =>
            cell == "-" ? [] : cell.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void EveryGenesysController_IsCoveredByTheCatalog()
    {
        var declared = typeof(GenesysController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .Where(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Cast<RouteAttribute>().Any(r => r.Template.StartsWith("api/genesys", StringComparison.Ordinal)))
            .ToList();

        Assert.Equal(declared.Select(t => t.Name).Order(), ApiRouteCatalog.GenesysFacingControllers.Select(t => t.Name).Order());
    }

    [Fact]
    public void TheDocumentedRoutes_AreExactlyTheRoutesTheControllersDeclare()
    {
        var documented = Table().Select(r => $"{r.Method} /{r.Route}").Order().ToList();
        var actual = ApiRouteCatalog.GenesysRoutes.Select(r => r.Key).Order().ToList();

        Assert.Equal(actual, documented);
        Assert.Equal(documented.Count, documented.Distinct().Count());
    }

    [Fact]
    public void EveryRow_MatchesItsControllerAction_UrlsHeadersQueryAndStatuses()
    {
        foreach (var row in Table())
        {
            var route = ApiRouteCatalog.GenesysRoutes.Single(r => r.Method == row.Method && r.Route == row.Route);

            Assert.Equal($"{{TCS}}/{route.Route}", row.TigerCsUrl);
            Assert.Equal(route.Headers.Order(StringComparer.OrdinalIgnoreCase), row.Headers.Order(StringComparer.OrdinalIgnoreCase));
            Assert.Equal(route.QueryParameters.Order(StringComparer.OrdinalIgnoreCase), row.Query.Order(StringComparer.OrdinalIgnoreCase));

            Assert.True(route.Statuses.IsSubsetOf(row.Statuses), $"{row.Method} /{row.Route}: statuses the controller declares are missing from the table: {string.Join(", ", route.Statuses.Except(row.Statuses))}");
            var extras = row.Statuses.Except(route.Statuses).Except(ProxyOriginatedStatuses).ToList();
            Assert.True(extras.Count == 0, $"{row.Method} /{row.Route}: statuses the controller does not declare: {string.Join(", ", extras)}");
            Assert.True(row.Statuses.Contains(401) && row.Statuses.Contains(504), "Every route can answer 401 (no Genesys token) and 504 (budget spent).");
            Assert.Equal(row.Method is "POST" or "PATCH", row.Statuses.Contains(413));
        }
    }

    [Fact]
    public void TheDataActionColumn_IsTheDataActionFilesRouting_InBothDirections()
    {
        var fromFiles = new Dictionary<string, SortedSet<string>>();
        foreach (var file in Directory.GetFiles(Path.Combine(ApiRouteCatalog.RepoRoot(), "docs", "Genesys", "data-actions"), "*.json"))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("00-", StringComparison.Ordinal))
            {
                continue;
            }

            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var request = json.RootElement.GetProperty("config").GetProperty("request");
            var url = request.GetProperty("requestUrlTemplate").GetString()!;
            var path = InputPlaceholder().Replace(url[(PublicBase.Length + 1)..].Split('?')[0], "{$1}");
            var key = $"{request.GetProperty("requestType").GetString()} {path}";
            (fromFiles.TryGetValue(key, out var set) ? set : fromFiles[key] = []).Add(name[..2]);
        }

        var fromTable = Table().Where(r => r.DataActions.Length > 0).ToDictionary(r => $"{r.Method} {r.Route}", r => r.DataActions.Order().ToList());

        Assert.Equal(fromFiles.Keys.Order(), fromTable.Keys.Order());
        foreach (var (key, numbers) in fromFiles)
        {
            Assert.Equal(numbers.ToList(), fromTable[key]);
        }
    }

    [Fact]
    public void TheCSharpRouteList_InTheDocument_IsTheSameTable()
    {
        var code = Doc;
        var start = code.IndexOf("public static readonly IReadOnlyList<ForwardRoute> All", StringComparison.Ordinal);
        Assert.True(start > 0, "The ForwardRoutes.All list is missing from the document.");
        var listing = code[start..code.IndexOf("];", start, StringComparison.Ordinal)];

        var coded = ForwardRouteEntry().Matches(listing)
            .Select(m => (Method: m.Groups[1].Value, Route: m.Groups[2].Value, Headers: m.Groups[3].Value, Surface: m.Groups[4].Value, Budget: m.Groups[5].Value))
            .ToList();
        var table = Table();

        Assert.Equal(table.Select(r => $"{r.Method} {r.Route}").Order(), coded.Select(c => $"{c.Method} {c.Route}").Order());
        foreach (var c in coded)
        {
            var row = table.Single(r => r.Method == c.Method && r.Route == c.Route);
            Assert.Equal(row.Headers.Length > 0 ? "Idempotency" : "None", c.Headers);
            Assert.Equal(row.Headers.Length > 0, row.Headers.Contains("Idempotency-Key"));
            // Agent surface = the two staff routes, nothing else; Collections reads get the read budget, everything else the default.
            Assert.Equal(c.Route is "api/genesys/agent-context" or "api/genesys/screen-pop" ? "Agent" : "Customer", c.Surface);
            Assert.Equal(c.Method == "GET" && c.Route.StartsWith("api/genesys/collections/", StringComparison.Ordinal) ? "CollectionsRead" : "Default", c.Budget);
        }
    }

    [Fact]
    public void ThePlaceholders_UsedInRoutes_AreAllGivenAValidationRuleInThePatch()
    {
        var doc = Doc;
        var placeholders = Table().SelectMany(r => Regex.Matches(r.Route, @"\{(\w+)\}").Select(m => m.Groups[1].Value)).Distinct().ToList();

        Assert.NotEmpty(placeholders);
        Assert.All(placeholders, p => Assert.Contains($"\"{p}\"", doc[doc.IndexOf("public static bool IsAcceptable", StringComparison.Ordinal)..]));
    }

    [GeneratedRegex(@"(?:\$esc\.url\()?\$\{input\.(\w+)\}\)?")]
    private static partial Regex InputPlaceholder();

    [GeneratedRegex(@"new\(""(\w+)"", ""([^""]+)"", (None|Idempotency), RouteSurface\.(\w+), Budget\.(\w+)\)")]
    private static partial Regex ForwardRouteEntry();
}
