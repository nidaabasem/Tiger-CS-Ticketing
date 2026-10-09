using System.Text.Json;

namespace TigerCS.Tests.GenesysIntegration.Contracts;

/// <summary>
/// The JSONPath subset the Genesys data-action <c>translationMap</c> files use:
/// <c>$.a.b</c>, <c>[*]</c>, <c>[?(@.key == 'v')]</c> / <c>!=</c> and <c>.length()</c>.
/// Returns the raw JSON text a data action substitutes for <c>${name}</c>
/// (strings keep their quotes), or <c>null</c> when the path yields nothing or a
/// JSON null - which is when <c>translationMapDefaults</c> applies.
/// A path through <c>[*]</c> or a filter yields a JSON array (empty array when nothing matches).
/// </summary>
public static class MiniJsonPath
{
    public static string? Evaluate(JsonElement root, string path)
    {
        if (!path.StartsWith('$'))
        {
            throw new FormatException("JSONPath must start with $: " + path);
        }

        var nodes = new List<JsonElement> { root };
        var multi = false;
        long? length = null;
        var i = 1;
        while (i < path.Length)
        {
            if (length is not null)
            {
                throw new NotSupportedException("Nothing may follow .length(): " + path);
            }

            if (string.CompareOrdinal(path, i, ".length()", 0, 9) == 0)
            {
                length = nodes.Count == 1 && nodes[0].ValueKind == JsonValueKind.Array ? nodes[0].GetArrayLength() : 0;
                i += 9;
            }
            else if (path[i] == '.')
            {
                var end = i + 1;
                while (end < path.Length && (char.IsLetterOrDigit(path[end]) || path[end] == '_'))
                {
                    end++;
                }

                var name = path[(i + 1)..end];
                nodes = nodes
                    .Where(n => n.ValueKind == JsonValueKind.Object && n.TryGetProperty(name, out _))
                    .Select(n => n.GetProperty(name))
                    .ToList();
                i = end;
            }
            else if (string.CompareOrdinal(path, i, "[*]", 0, 3) == 0)
            {
                multi = true;
                nodes = nodes.Where(n => n.ValueKind == JsonValueKind.Array).SelectMany(n => n.EnumerateArray()).ToList();
                i += 3;
            }
            else if (string.CompareOrdinal(path, i, "[?(@.", 0, 5) == 0)
            {
                var close = path.IndexOf(")]", i, StringComparison.Ordinal);
                var expression = path[(i + 5)..close]; // key == 'value'
                var op = expression.Contains("!=", StringComparison.Ordinal) ? "!=" : "==";
                var parts = expression.Split(op, 2, StringSplitOptions.TrimEntries);
                var key = parts[0];
                var expected = parts[1].Trim('\'');
                multi = true;
                nodes = nodes
                    .Where(n => n.ValueKind == JsonValueKind.Array)
                    .SelectMany(n => n.EnumerateArray())
                    .Where(item =>
                    {
                        var actual = item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
                            ? p.GetString()
                            : null;
                        return op == "==" ? actual == expected : actual != expected;
                    })
                    .ToList();
                i = close + 2;
            }
            else
            {
                throw new NotSupportedException($"JSONPath construct outside the supported subset at {i}: {path}");
            }
        }

        if (length is { } count)
        {
            return count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (multi)
        {
            return "[" + string.Join(",", nodes.Select(n => n.GetRawText())) + "]";
        }

        return nodes.Count == 1 && nodes[0].ValueKind != JsonValueKind.Null ? nodes[0].GetRawText() : null;
    }
}
