using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dispatch.Application.Diff;

public enum DiffKind
{
    Same,
    Added,
    Removed
}

public sealed record DiffLine(DiffKind Kind, string Text, int? LeftLine, int? RightLine);

public enum JsonChangeKind
{
    Added,
    Removed,
    Changed,
    TypeChanged
}

public sealed record JsonChange(JsonChangeKind Kind, string Path, string? Left, string? Right)
{
    public override string ToString() => Kind switch
    {
        JsonChangeKind.Added => $"+ {Path}: {Right}",
        JsonChangeKind.Removed => $"- {Path}: {Left}",
        JsonChangeKind.TypeChanged => $"~ {Path}: type {Left} → {Right}",
        _ => $"~ {Path}: {Left} → {Right}"
    };
}

/// <summary>
/// Compares two responses: a line diff (Myers) of the formatted bodies, and for JSON a structural diff with ignore rules
/// (e.g. <c>$.id</c>, <c>$..timestamp</c>, <c>$.items[*].updatedAt</c>) for values that differ on every run.
/// </summary>
public static class ResponseDiff
{
    private const int MaxLines = 20_000;

    public static IReadOnlyList<DiffLine> Lines(string left, string right)
    {
        var a = left.Replace("\r\n", "\n").Split('\n');
        var b = right.Replace("\r\n", "\n").Split('\n');
        if (a.Length > MaxLines || b.Length > MaxLines)
        {
            a = a.Take(MaxLines).ToArray();
            b = b.Take(MaxLines).ToArray();
        }
        return Myers(a, b);
    }

    /// <summary>Beyond this many edits the diff degrades to "replace everything" to bound time and memory.</summary>
    private const int MaxEdits = 4000;

    /// <summary>
    /// Myers O(ND) diff producing a full, ordered script of same/removed/added lines. Each step only snapshots the
    /// diagonals it can reach (k in -d-1..d+1), so memory is O(D²) rather than O(D·(N+M)).
    /// </summary>
    private static List<DiffLine> Myers(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length, max = n + m;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();

        for (var d = 0; d <= Math.Min(max, MaxEdits); d++)
        {
            var snapshot = new int[2 * d + 3];
            Array.Copy(v, max - d, snapshot, 0, Math.Min(snapshot.Length, v.Length - (max - d)));
            trace.Add(snapshot);

            for (var k = -d; k <= d; k += 2)
            {
                var index = k + max + 1;
                int x = k == -d || (k != d && v[index - 1] < v[index + 1]) ? v[index + 1] : v[index - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                v[index] = x;
                if (x >= n && y >= m)
                    return Backtrack(trace, a, b);
            }
        }

        // Too different: show it as a full replacement.
        return a.Select((line, i) => new DiffLine(DiffKind.Removed, line, i + 1, null))
            .Concat(b.Select((line, i) => new DiffLine(DiffKind.Added, line, null, i + 1)))
            .ToList();
    }

    private static List<DiffLine> Backtrack(List<int[]> trace, string[] a, string[] b)
    {
        var result = new List<DiffLine>();
        int x = a.Length, y = b.Length;
        for (var d = trace.Count - 1; d >= 0 && (x > 0 || y > 0); d--)
        {
            // trace[d] holds the state before step d, for diagonals -d-1..d+1 at offsets 0..2d+2.
            var v = trace[d];
            int V(int diagonal) => v[diagonal + d + 1];
            var k = x - y;
            var prevK = k == -d || (k != d && V(k - 1) < V(k + 1)) ? k + 1 : k - 1;
            var prevX = d == 0 ? 0 : V(prevK);
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY)
            {
                x--;
                y--;
                result.Add(new DiffLine(DiffKind.Same, a[x], x + 1, y + 1));
            }
            if (d > 0)
            {
                if (x == prevX)
                {
                    y--;
                    result.Add(new DiffLine(DiffKind.Added, b[y], null, y + 1));
                }
                else
                {
                    x--;
                    result.Add(new DiffLine(DiffKind.Removed, a[x], x + 1, null));
                }
            }
        }
        result.Reverse();
        return result;
    }

    /// <summary>Structural JSON comparison. Object key order doesn't matter; array items are compared by index.</summary>
    public static IReadOnlyList<JsonChange> Json(string left, string right, IEnumerable<string>? ignorePaths = null)
    {
        JsonNode? l, r;
        try
        {
            l = JsonNode.Parse(left);
            r = JsonNode.Parse(right);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Both bodies must be JSON for a structural diff: {ex.Message}");
        }

        var ignore = (ignorePaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(IgnorePattern).ToList();
        var changes = new List<JsonChange>();
        Compare(l, r, "$", ignore, changes);
        return changes;
    }

    /// <summary>JSONPath-like ignore rule → regex over concrete paths: <c>[*]</c> any index, <c>..</c> any depth, <c>*</c> any key.</summary>
    private static Regex IgnorePattern(string path)
    {
        var p = path.Trim();
        if (!p.StartsWith('$'))
            p = "$." + p;
        var pattern = Regex.Escape(p)
            .Replace(@"\.\.", "(\\..+)?\\.")
            .Replace(@"\[\*]", @"\[\d+]")
            .Replace(@"\.\*", @"\.[^.\[]+");
        return new Regex("^" + pattern + @"($|\.|\[)", RegexOptions.CultureInvariant);
    }

    private static void Compare(JsonNode? left, JsonNode? right, string path, List<Regex> ignore, List<JsonChange> changes)
    {
        if (ignore.Any(r => r.IsMatch(path)))
            return;

        var lt = TypeOf(left);
        var rt = TypeOf(right);
        if (lt != rt)
        {
            changes.Add(new JsonChange(JsonChangeKind.TypeChanged, path, lt, rt));
            return;
        }

        switch (left)
        {
            case JsonObject lo:
                var ro = (JsonObject)right!;
                foreach (var (key, value) in lo)
                {
                    var childPath = ChildPath(path, key);
                    if (ro.TryGetPropertyValue(key, out var other))
                        Compare(value, other, childPath, ignore, changes);
                    else if (!ignore.Any(r => r.IsMatch(childPath)))
                        changes.Add(new JsonChange(JsonChangeKind.Removed, childPath, Show(value), null));
                }
                foreach (var (key, value) in ro)
                {
                    var childPath = ChildPath(path, key);
                    if (!lo.ContainsKey(key) && !ignore.Any(r => r.IsMatch(childPath)))
                        changes.Add(new JsonChange(JsonChangeKind.Added, childPath, null, Show(value)));
                }
                break;
            case JsonArray la:
                var ra = (JsonArray)right!;
                for (var i = 0; i < Math.Max(la.Count, ra.Count); i++)
                {
                    var childPath = $"{path}[{i}]";
                    if (i >= ra.Count)
                        changes.Add(new JsonChange(JsonChangeKind.Removed, childPath, Show(la[i]), null));
                    else if (i >= la.Count)
                        changes.Add(new JsonChange(JsonChangeKind.Added, childPath, null, Show(ra[i])));
                    else
                        Compare(la[i], ra[i], childPath, ignore, changes);
                }
                break;
            default:
                if (!JsonNode.DeepEquals(left, right))
                    changes.Add(new JsonChange(JsonChangeKind.Changed, path, Show(left), Show(right)));
                break;
        }
    }

    private static string ChildPath(string path, string key) =>
        Regex.IsMatch(key, @"^[A-Za-z_$][\w$]*$") ? $"{path}.{key}" : $"{path}['{key.Replace("'", "\\'")}']";

    private static string TypeOf(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "null"
        },
        _ => "unknown"
    };

    private static string Show(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length > 120 ? text[..117] + "..." : text;
    }
}
