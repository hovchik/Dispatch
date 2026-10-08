using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dispatch.Application.Testing;

/// <summary>
/// A compact JSONPath implementation covering what API tests use: <c>$</c>, <c>.name</c>, <c>['name']</c>, <c>[0]</c>,
/// <c>[-1]</c>, <c>[*]</c>, <c>.*</c>, <c>..name</c>, <c>[1:3]</c>, <c>[0,2]</c>, filters such as
/// <c>[?(@.age &gt; 18)]</c> / <c>[?(@.id)]</c>, and <c>.length()</c>. A leading <c>$.</c> is optional.
/// </summary>
public static class JsonPath
{
    public static IReadOnlyList<JsonNode?> Select(JsonNode? root, string path)
    {
        path = path.Trim();
        if (path.Length == 0 || path == "$")
            return [root];
        if (!path.StartsWith('$'))
            path = path.StartsWith('[') ? "$" + path : "$." + path;

        var current = new List<JsonNode?> { root };
        var i = 1;
        while (i < path.Length && current.Count > 0)
        {
            if (path[i] == '.')
            {
                if (i + 1 < path.Length && path[i + 1] == '.')
                {
                    i += 2;
                    var name = ReadName(path, ref i);
                    current = current.SelectMany(n => Descendants(n)).ToList();
                    if (name == "*")
                        continue;
                    current = current.SelectMany(n => Child(n, name)).ToList();
                    continue;
                }

                i++;
                var member = ReadName(path, ref i);
                if (member == "length()" || (member == "length" && current.All(n => n is JsonArray or JsonValue)))
                {
                    current = current.Select(n => (JsonNode?)JsonValue.Create(Length(n))).ToList();
                    continue;
                }
                current = member == "*"
                    ? current.SelectMany(Children).ToList()
                    : current.SelectMany(n => Child(n, member)).ToList();
            }
            else if (path[i] == '[')
            {
                var end = FindBracketEnd(path, i);
                var inner = path[(i + 1)..end].Trim();
                i = end + 1;
                current = current.SelectMany(n => Bracket(n, inner)).ToList();
            }
            else
            {
                throw new FormatException($"Unexpected '{path[i]}' at position {i} in JSONPath '{path}'.");
            }
        }
        return current;
    }

    /// <summary>First match rendered as text (strings unquoted), or null when nothing matches.</summary>
    public static string? SelectText(JsonNode? root, string path)
    {
        var matches = Select(root, path);
        return matches.Count == 0 ? null : ToText(matches[0]);
    }

    public static string ToText(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "true",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "false",
        _ => node.ToJsonString()
    };

    private static int Length(JsonNode? node) => node switch
    {
        JsonArray a => a.Count,
        JsonObject o => o.Count,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length,
        _ => 0
    };

    private static string ReadName(string path, ref int i)
    {
        var start = i;
        while (i < path.Length && path[i] != '.' && path[i] != '[')
        {
            // Allow "length()"
            if (path[i] == '(' && i + 1 < path.Length && path[i + 1] == ')')
            {
                i += 2;
                break;
            }
            i++;
        }
        return path[start..i];
    }

    private static int FindBracketEnd(string path, int start)
    {
        var depth = 0;
        char? quote = null;
        for (var j = start; j < path.Length; j++)
        {
            var c = path[j];
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
                continue;
            }
            if (c is '\'' or '"')
                quote = c;
            else if (c == '[')
                depth++;
            else if (c == ']' && --depth == 0)
                return j;
        }
        throw new FormatException($"Unclosed '[' in JSONPath '{path}'.");
    }

    private static IEnumerable<JsonNode?> Children(JsonNode? node) => node switch
    {
        JsonObject o => o.Select(kv => kv.Value),
        JsonArray a => a,
        _ => []
    };

    private static IEnumerable<JsonNode?> Descendants(JsonNode? node)
    {
        yield return node;
        foreach (var child in Children(node))
            foreach (var d in Descendants(child))
                yield return d;
    }

    private static IEnumerable<JsonNode?> Child(JsonNode? node, string name)
    {
        if (node is JsonObject o && o.TryGetPropertyValue(name, out var value))
            yield return value;
    }

    private static IEnumerable<JsonNode?> Bracket(JsonNode? node, string inner)
    {
        if (inner == "*")
            return Children(node);

        if (inner.StartsWith("?(", StringComparison.Ordinal) && inner.EndsWith(')'))
            return Children(node).Where(child => Filter(child, inner[2..^1].Trim()));

        if (inner.Contains(':', StringComparison.Ordinal) && node is JsonArray sliceArray && !IsQuoted(inner))
            return Slice(sliceArray, inner);

        var results = new List<JsonNode?>();
        foreach (var part in SplitTopLevel(inner, ','))
        {
            var token = part.Trim();
            if (IsQuoted(token))
            {
                results.AddRange(Child(node, token[1..^1]));
            }
            else if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (node is JsonArray a)
                {
                    if (index < 0)
                        index += a.Count;
                    if (index >= 0 && index < a.Count)
                        results.Add(a[index]);
                }
            }
            else
            {
                results.AddRange(Child(node, token));
            }
        }
        return results;
    }

    private static IEnumerable<JsonNode?> Slice(JsonArray array, string spec)
    {
        var parts = spec.Split(':');
        int Parse(string s, int fallback) =>
            int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        var start = Parse(parts[0], 0);
        var end = Parse(parts.Length > 1 ? parts[1] : "", array.Count);
        var step = Math.Max(1, Parse(parts.Length > 2 ? parts[2] : "", 1));
        if (start < 0) start += array.Count;
        if (end < 0) end += array.Count;
        start = Math.Clamp(start, 0, array.Count);
        end = Math.Clamp(end, 0, array.Count);
        for (var i = start; i < end; i += step)
            yield return array[i];
    }

    private static bool IsQuoted(string s) =>
        s.Length >= 2 && ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '"' && s[^1] == '"'));

    private static IEnumerable<string> SplitTopLevel(string s, char separator)
    {
        var sb = new StringBuilder();
        char? quote = null;
        foreach (var c in s)
        {
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
                sb.Append(c);
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                sb.Append(c);
            }
            else if (c == separator)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        yield return sb.ToString();
    }

    // ---- Filters: @.a.b op literal, joined with && / || ---------------------------------------

    private static readonly string[] Operators = ["==", "!=", "<=", ">=", "=~", "<", ">"];

    private static bool Filter(JsonNode? item, string expression)
    {
        var orParts = SplitOperator(expression, "||");
        if (orParts.Count > 1)
            return orParts.Any(p => Filter(item, p));
        var andParts = SplitOperator(expression, "&&");
        if (andParts.Count > 1)
            return andParts.All(p => Filter(item, p));

        expression = expression.Trim();
        var negate = expression.StartsWith('!');
        if (negate)
            expression = expression[1..].Trim();

        // Parenthesised group, possibly negated: !(@.a == 1)
        if (expression.StartsWith('(') && expression.EndsWith(')'))
        {
            var inner = Filter(item, expression[1..^1]);
            return negate ? !inner : inner;
        }

        foreach (var op in Operators)
        {
            var at = IndexOutsideQuotes(expression, op);
            if (at <= 0)
                continue;
            var left = Operand(item, expression[..at].Trim());
            var right = Operand(item, expression[(at + op.Length)..].Trim());
            var result = Compare(left, right, op);
            return negate ? !result : result;
        }

        // Existence test: [?(@.email)]
        var exists = expression.StartsWith('@') && Select(item, "$" + expression[1..]).Count > 0;
        return negate ? !exists : exists;
    }

    private static List<string> SplitOperator(string s, string op)
    {
        var parts = new List<string>();
        var depth = 0;
        char? quote = null;
        var last = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                continue;
            }
            if (c is '\'' or '"') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && string.CompareOrdinal(s, i, op, 0, op.Length) == 0)
            {
                parts.Add(s[last..i]);
                last = i + op.Length;
                i += op.Length - 1;
            }
        }
        parts.Add(s[last..]);
        return parts;
    }

    private static int IndexOutsideQuotes(string s, string op)
    {
        char? quote = null;
        for (var i = 0; i < s.Length - op.Length + 1; i++)
        {
            var c = s[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                continue;
            }
            if (c is '\'' or '"') quote = c;
            else if (string.CompareOrdinal(s, i, op, 0, op.Length) == 0)
                return i;
        }
        return -1;
    }

    private static JsonNode? Operand(JsonNode? item, string token)
    {
        if (token.StartsWith('@'))
        {
            var matches = Select(item, "$" + token[1..]);
            return matches.Count > 0 ? matches[0] : null;
        }
        if (IsQuoted(token))
            return JsonValue.Create(token[1..^1]);
        if (token.StartsWith('/') && token.LastIndexOf('/') > 0)
            return JsonValue.Create(token); // regex literal for =~
        try
        {
            return JsonNode.Parse(token);
        }
        catch (JsonException)
        {
            return JsonValue.Create(token);
        }
    }

    /// <summary>User-supplied regex with a timeout, so a pathological pattern cannot hang the filter.</summary>
    private static bool SafeMatch(string input, string pattern, System.Text.RegularExpressions.RegexOptions options)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(input, pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool Compare(JsonNode? left, JsonNode? right, string op)
    {
        if (op == "=~")
        {
            var pattern = ToText(right);
            if (pattern.StartsWith('/'))
            {
                var close = pattern.LastIndexOf('/');
                var flags = pattern[(close + 1)..];
                pattern = pattern[1..close];
                return SafeMatch(ToText(left), pattern,
                    flags.Contains('i') ? System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        : System.Text.RegularExpressions.RegexOptions.None);
            }
            return SafeMatch(ToText(left), pattern, System.Text.RegularExpressions.RegexOptions.None);
        }

        if (TryNumber(left, out var l) && TryNumber(right, out var r))
        {
            return op switch
            {
                "==" => l == r,
                "!=" => l != r,
                "<" => l < r,
                ">" => l > r,
                "<=" => l <= r,
                ">=" => l >= r,
                _ => false
            };
        }

        var ls = left is null ? null : ToText(left);
        var rs = right is null ? null : ToText(right);
        var cmp = string.CompareOrdinal(ls, rs);
        return op switch
        {
            "==" => cmp == 0,
            "!=" => cmp != 0,
            "<" => cmp < 0,
            ">" => cmp > 0,
            "<=" => cmp <= 0,
            ">=" => cmp >= 0,
            _ => false
        };
    }

    internal static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v)
            return false;
        return v.GetValueKind() == JsonValueKind.Number
               && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
