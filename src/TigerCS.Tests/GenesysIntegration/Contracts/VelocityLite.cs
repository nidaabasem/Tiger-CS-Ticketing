using System.Globalization;
using System.Text;

namespace TigerCS.Tests.GenesysIntegration.Contracts;

/// <summary>
/// A deliberately small Velocity subset - exactly what the Genesys data-action
/// files use, nothing more - so the request/response templates in
/// <c>docs/Genesys/data-actions</c> can be rendered in a test:
/// <c>${a.b}</c>, <c>$a.b</c>, <c>$!{a.b}</c>, <c>$!a.b</c> (silent when null),
/// <c>$esc.jsonString(ref)</c>, <c>$!esc.jsonString(ref)</c>, <c>$esc.url(ref)</c>,
/// and <c>#if(cond)...#else...#end</c> with <c>==</c>/<c>!=</c> or a bare reference.
///
/// <para>
/// Velocity semantics reproduced on purpose: an unresolved reference without
/// <c>!</c> is emitted as its literal source text (this is what turns a missing
/// input into broken JSON in a real data action), with <c>!</c> it renders as
/// empty; an empty string is "true" in <c>#if</c>; only null and <c>false</c> are false.
/// Anything outside the subset throws, so a new construct in a file cannot pass silently.
/// </para>
///
/// <para>
/// <b>Not verified against Genesys Cloud itself</b> - this models Apache Velocity's
/// documented behaviour and the Genesys <c>$esc</c> helper as the files use it.
/// </para>
/// </summary>
public static class VelocityLite
{
    public static string Render(string template, IReadOnlyDictionary<string, object?> context) =>
        new Engine(context).Render(template);

    /// <summary>JSON string escaping as <c>$esc.jsonString</c> must behave: quotes, backslashes and control characters.</summary>
    public static string JsonString(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    private sealed class Engine(IReadOnlyDictionary<string, object?> context)
    {
        public string Render(string t)
        {
            var sb = new StringBuilder();
            var i = 0;
            while (i < t.Length)
            {
                if (t[i] == '#' && string.CompareOrdinal(t, i, "#if(", 0, 4) == 0)
                {
                    i = RenderIf(t, i, sb);
                    continue;
                }

                if (t[i] == '#' && (string.CompareOrdinal(t, i, "#else", 0, 5) == 0 || string.CompareOrdinal(t, i, "#end", 0, 4) == 0 || string.CompareOrdinal(t, i, "#set", 0, 4) == 0 || string.CompareOrdinal(t, i, "#foreach", 0, 8) == 0))
                {
                    throw new NotSupportedException($"Unbalanced or unsupported directive at {i}: {t.Substring(i, Math.Min(12, t.Length - i))}");
                }

                if (t[i] == '$' && TryReference(t, ref i, out var text))
                {
                    sb.Append(text);
                    continue;
                }

                sb.Append(t[i++]);
            }

            return sb.ToString();
        }

        private int RenderIf(string t, int start, StringBuilder sb)
        {
            var condStart = start + 4;
            var condEnd = MatchingParen(t, condStart - 1);
            var condition = t[condStart..condEnd];

            // Find the matching #else / #end at depth 0.
            var i = condEnd + 1;
            var depth = 0;
            int elseAt = -1, endAt = -1;
            while (i < t.Length)
            {
                if (string.CompareOrdinal(t, i, "#if(", 0, 4) == 0)
                {
                    depth++;
                    i += 4;
                }
                else if (string.CompareOrdinal(t, i, "#end", 0, 4) == 0)
                {
                    if (depth == 0)
                    {
                        endAt = i;
                        break;
                    }

                    depth--;
                    i += 4;
                }
                else if (depth == 0 && string.CompareOrdinal(t, i, "#else", 0, 5) == 0)
                {
                    if (string.CompareOrdinal(t, i, "#elseif", 0, 7) == 0)
                    {
                        throw new NotSupportedException("#elseif is outside the supported subset.");
                    }

                    elseAt = i;
                    i += 5;
                }
                else
                {
                    i++;
                }
            }

            if (endAt < 0)
            {
                throw new FormatException("#if without #end: " + t);
            }

            var thenBody = t[(condEnd + 1)..(elseAt >= 0 ? elseAt : endAt)];
            var elseBody = elseAt >= 0 ? t[(elseAt + 5)..endAt] : string.Empty;
            sb.Append(Render(Evaluate(condition) ? thenBody : elseBody));
            return endAt + 4;
        }

        private bool Evaluate(string condition)
        {
            var (left, op, right) = SplitComparison(condition);
            if (op is null)
            {
                var operand = condition.Trim();
                if (!operand.StartsWith('$'))
                {
                    throw new NotSupportedException("Condition not in the supported subset: " + condition);
                }

                var value = Resolve(operand);
                return value is not null and not false;
            }

            var l = Operand(left);
            var r = Operand(right);
            var equal = string.Equals(l, r, StringComparison.Ordinal);
            return op == "==" ? equal : !equal;
        }

        private string? Operand(string raw)
        {
            raw = raw.Trim();
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            {
                return Render(raw[1..^1]);
            }

            if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            {
                return raw[1..^1];
            }

            return raw.StartsWith('$') ? Format(Resolve(raw)) : raw;
        }

        private static (string Left, string? Op, string Right) SplitComparison(string condition)
        {
            var quote = '\0';
            for (var i = 0; i < condition.Length - 1; i++)
            {
                var c = condition[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    continue;
                }

                if (c is '"' or '\'')
                {
                    quote = c;
                    continue;
                }

                if ((c == '=' || c == '!') && condition[i + 1] == '=')
                {
                    return (condition[..i], condition.Substring(i, 2), condition[(i + 2)..]);
                }
            }

            return (condition, null, string.Empty);
        }

        private static int MatchingParen(string t, int openAt)
        {
            var depth = 0;
            var quote = '\0';
            for (var i = openAt; i < t.Length; i++)
            {
                var c = t[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    continue;
                }

                if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '(')
                {
                    depth++;
                }
                else if (c == ')' && --depth == 0)
                {
                    return i;
                }
            }

            throw new FormatException("Unbalanced parenthesis: " + t);
        }

        /// <summary>Parses the reference (or method call) at <paramref name="i"/>; on success advances <paramref name="i"/> past it.</summary>
        private bool TryReference(string t, ref int i, out string text)
        {
            var start = i;
            var j = i + 1;
            var silent = false;
            if (j < t.Length && t[j] == '!')
            {
                silent = true;
                j++;
            }

            string path;
            if (j < t.Length && t[j] == '{')
            {
                var close = t.IndexOf('}', j);
                if (close < 0) { text = string.Empty; return false; }
                path = t[(j + 1)..close];
                j = close + 1;
            }
            else
            {
                var k = j;
                while (k < t.Length && (char.IsLetterOrDigit(t[k]) || t[k] == '_' || (t[k] == '.' && k + 1 < t.Length && (char.IsLetter(t[k + 1]) || t[k + 1] == '_'))))
                {
                    k++;
                }

                path = t[j..k];
                j = k;
            }

            if (path.Length == 0)
            {
                text = string.Empty;
                return false;
            }

            object? value;
            if (j < t.Length && t[j] == '(')
            {
                var close = MatchingParen(t, j);
                var argument = t[(j + 1)..close].Trim();
                j = close + 1;
                var argValue = argument.StartsWith('$') ? Resolve(argument) : argument.Trim('"', '\'');
                var text0 = Format(argValue);
                value = path switch
                {
                    "esc.jsonString" => text0 is null ? null : JsonString(text0),
                    "esc.url" => text0 is null ? null : Uri.EscapeDataString(text0),
                    _ => throw new NotSupportedException("Method outside the supported subset: " + path)
                };
            }
            else
            {
                value = Lookup(path);
            }

            i = j;
            var formatted = Format(value);
            text = formatted ?? (silent ? string.Empty : t[start..j]);
            return true;
        }

        /// <summary>Resolves a whole <c>$ref</c> / <c>${ref}</c> / <c>$!ref</c> token to its value.</summary>
        private object? Resolve(string token)
        {
            var s = token.Trim().TrimStart('$').TrimStart('!');
            if (s.StartsWith('{') && s.EndsWith('}'))
            {
                s = s[1..^1];
            }

            return Lookup(s);
        }

        private object? Lookup(string path)
        {
            var parts = path.Split('.');
            if (!context.TryGetValue(parts[0], out var current))
            {
                return null;
            }

            foreach (var part in parts.Skip(1))
            {
                if (current is IReadOnlyDictionary<string, object?> map && map.TryGetValue(part, out var next))
                {
                    current = next;
                }
                else
                {
                    return null;
                }
            }

            return current;
        }

        private static string? Format(object? value) => value switch
        {
            null => null,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }
}
