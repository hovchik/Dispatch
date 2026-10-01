using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dispatch.Application.Impact;

public enum ShapeChangeKind
{
    /// <summary>A field that existed before is gone.</summary>
    Removed,

    /// <summary>A field disappeared and another with the same type (and usually value) appeared: probably renamed or moved.</summary>
    Renamed,

    /// <summary>The field is still there but holds a different JSON type.</summary>
    TypeChanged,

    /// <summary>A new field (never breaks anything; listed for context).</summary>
    Added
}

/// <summary>One structural difference between two JSON bodies. Paths use <c>[*]</c> for array items.</summary>
public sealed record ShapeChange(ShapeChangeKind Kind, string Path, string? OldType = null, string? NewType = null, string? NewPath = null)
{
    internal IReadOnlyList<string> Segments { get; init; } = [];

    public override string ToString() => Kind switch
    {
        ShapeChangeKind.Removed => $"removed {Path} ({OldType})",
        ShapeChangeKind.Renamed => $"renamed {Path} → {NewPath}",
        ShapeChangeKind.TypeChanged => $"{Path} changed from {OldType} to {NewType}",
        _ => $"added {Path} ({NewType})"
    };
}

/// <summary>
/// The shape of a JSON document: every path (array items collapsed to <c>[*]</c>) with the JSON types seen there, plus a
/// sample value for leaves. Used to describe how a response changed structurally, ignoring values.
/// </summary>
public static class JsonShape
{
    /// <summary>Wildcard segment for "any array item".</summary>
    internal const string Any = "*";

    /// <summary>Recursive-descent segment (<c>..</c>) in a reference path.</summary>
    internal const string Deep = "**";

    private sealed class Info
    {
        public HashSet<string> Types { get; } = new(StringComparer.Ordinal);
        public string? Sample { get; set; }
    }

    /// <summary>Structural changes from <paramref name="before"/> to <paramref name="after"/> (empty when either isn't JSON).</summary>
    public static IReadOnlyList<ShapeChange> Diff(string before, string after)
    {
        if (Parse(before) is not { } oldRoot || Parse(after) is not { } newRoot)
            return [];
        var oldShape = Flatten(oldRoot);
        var newShape = Flatten(newRoot);

        var removedAll = oldShape.Keys.Where(k => !newShape.ContainsKey(k)).ToList();
        var addedAll = newShape.Keys.Where(k => !oldShape.ContainsKey(k)).ToList();
        // Only the top-most path of a removed / added subtree is reported.
        var removed = removedAll.Where(p => !removedAll.Any(o => IsStrictPrefix(o, p))).ToList();
        var added = addedAll.Where(p => !addedAll.Any(o => IsStrictPrefix(o, p))).ToList();

        var changes = new List<ShapeChange>();
        // A field may move into a new subtree ($.ref → $.order.ref), so rename targets are any added path.
        foreach (var (rename, to) in PairRenames(removed, addedAll, oldShape, newShape))
        {
            removed.Remove(rename);
            added.Remove(to);
            changes.Add(Change(ShapeChangeKind.Renamed, rename, Type(oldShape[rename]), Type(newShape[to]), to));
        }
        changes.AddRange(removed.Select(p => Change(ShapeChangeKind.Removed, p, Type(oldShape[p]))));

        foreach (var (path, info) in oldShape)
        {
            if (!newShape.TryGetValue(path, out var now))
                continue;
            // Becoming nullable (or no longer null) is reported as a type change only when a real type changes.
            var oldTypes = info.Types.Where(t => t != "null").ToHashSet();
            var newTypes = now.Types.Where(t => t != "null").ToHashSet();
            if (oldTypes.Count > 0 && newTypes.Count > 0 && !oldTypes.SetEquals(newTypes))
                changes.Add(Change(ShapeChangeKind.TypeChanged, path, Type(info), Type(now)));
        }
        changes.AddRange(added.Select(p => Change(ShapeChangeKind.Added, p, newType: Type(newShape[p]))));
        return changes;
    }

    private static ShapeChange Change(ShapeChangeKind kind, string path, string? oldType = null, string? newType = null, string? newPath = null) =>
        new(kind, path, oldType, newType, newPath) { Segments = Split(path) };

    /// <summary>
    /// Pairs removed with added paths that look like the same field: same type and same sample value (a move anywhere), or
    /// the only same-typed candidate under the same parent (a rename).
    /// </summary>
    private static List<(string From, string To)> PairRenames(List<string> removed, List<string> added,
        Dictionary<string, Info> oldShape, Dictionary<string, Info> newShape)
    {
        var pairs = new List<(string, string)>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var from in removed)
        {
            var o = oldShape[from];
            var candidates = added.Where(a => !used.Contains(a) && Type(newShape[a]) == Type(o)
                                              && SimilarChildren(from, oldShape, a, newShape)).ToList();
            var byValue = o.Sample is { Length: > 0 } sample && Distinctive(sample)
                ? candidates.Where(a => newShape[a].Sample == sample).ToList()
                : [];
            var sameParent = candidates.Where(a => Parent(a) == Parent(from)).ToList();
            var to = byValue.Count == 1 ? byValue[0]
                : sameParent.Count == 1 && removed.Count(r => Parent(r) == Parent(from) && Type(oldShape[r]) == Type(o)) == 1 ? sameParent[0]
                : null;
            if (to is null)
                continue;
            used.Add(to);
            pairs.Add((from, to));
        }
        return pairs;
    }

    /// <summary>
    /// For objects and arrays, the renamed container must keep most of its structure (at least half of the relative child
    /// paths in common); leaves always qualify.
    /// </summary>
    private static bool SimilarChildren(string from, Dictionary<string, Info> oldShape, string to, Dictionary<string, Info> newShape)
    {
        HashSet<string> Children(string root, Dictionary<string, Info> shape)
        {
            var depth = Split(root).Count;
            return shape.Keys.Where(k => IsStrictPrefix(root, k)).Select(k => Join(Split(k).Skip(depth).ToList())).ToHashSet(StringComparer.Ordinal);
        }
        var a = Children(from, oldShape);
        var b = Children(to, newShape);
        if (a.Count == 0 && b.Count == 0)
            return true;
        var common = a.Intersect(b).Count();
        return common * 2 >= Math.Max(a.Count, b.Count) && common > 0;
    }

    /// <summary>A value specific enough to identify a moved field (not true/false/0/1/"").</summary>
    private static bool Distinctive(string sample) => sample.Length >= 3 && sample is not ("true" or "false" or "null");

    private static string Parent(string path)
    {
        var segments = Split(path);
        return Join(segments.Take(segments.Count - 1).ToList());
    }

    private static string Type(Info info) => string.Join("|", info.Types.Order(StringComparer.Ordinal));

    private static Dictionary<string, Info> Flatten(JsonNode root)
    {
        var shape = new Dictionary<string, Info>(StringComparer.Ordinal);
        void Walk(JsonNode? node, List<string> path)
        {
            var key = Join(path);
            if (!shape.TryGetValue(key, out var info))
                shape[key] = info = new Info();
            switch (node)
            {
                case JsonObject obj:
                    info.Types.Add("object");
                    foreach (var (name, value) in obj)
                        Walk(value, [.. path, name]);
                    break;
                case JsonArray array:
                    info.Types.Add("array");
                    foreach (var item in array)
                        Walk(item, [.. path, Any]);
                    break;
                case JsonValue value:
                    var kind = value.GetValueKind();
                    info.Types.Add(kind switch
                    {
                        JsonValueKind.String => "string",
                        JsonValueKind.Number => "number",
                        JsonValueKind.True or JsonValueKind.False => "boolean",
                        _ => "null"
                    });
                    info.Sample ??= value.ToJsonString().Trim('"');
                    break;
                default:
                    info.Types.Add("null");
                    break;
            }
        }
        Walk(root, []);
        return shape;
    }

    private static JsonNode? Parse(string text)
    {
        var t = text.TrimStart();
        if (!(t.StartsWith('{') || t.StartsWith('[')))
            return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsStrictPrefix(string prefix, string path)
    {
        var a = Split(prefix);
        var b = Split(path);
        return a.Count < b.Count && a.SequenceEqual(b.Take(a.Count));
    }

    // ---- Paths -------------------------------------------------------------------------------

    /// <summary>Path text for segments: <c>$.user.id</c>, <c>$.items[*].sku</c>, <c>$['odd key']</c>.</summary>
    internal static string Join(IReadOnlyList<string> segments)
    {
        var sb = new StringBuilder("$");
        foreach (var s in segments)
        {
            if (s == Any)
                sb.Append("[*]");
            else if (s == Deep)
                sb.Append("..");
            else if (s.Length > 0 && (char.IsLetter(s[0]) || s[0] is '_' or '$') && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '$'))
                sb.Append(sb[^1] == '.' ? "" : ".").Append(s);
            else
                sb.Append("['").Append(s.Replace("'", "\\'")).Append("']");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses a JSONPath as written in assertions and extraction rules into segments: property names, <c>*</c> for any
    /// array index / slice / filter / wildcard, and <c>**</c> for recursive descent (<c>..</c>).
    /// </summary>
    internal static IReadOnlyList<string> Split(string path)
    {
        var segments = new List<string>();
        var p = path.Trim();
        var i = p.StartsWith('$') ? 1 : 0;
        while (i < p.Length)
        {
            if (p[i] == '.')
            {
                if (i + 1 < p.Length && p[i + 1] == '.')
                {
                    segments.Add(Deep);
                    i += 2;
                    continue;
                }
                i++;
                continue;
            }
            if (p[i] == '[')
            {
                var end = FindClose(p, i);
                var inner = p[(i + 1)..end].Trim();
                if (inner.Length >= 2 && inner[0] is '\'' or '"' && inner[^1] == inner[0])
                    segments.Add(inner[1..^1].Replace("\\'", "'"));
                else
                    segments.Add(Any); // index, slice, filter or *
                i = end + 1;
                continue;
            }
            var start = i;
            while (i < p.Length && p[i] is not ('.' or '['))
                i++;
            var name = p[start..i].Trim();
            if (name.Length > 0)
                segments.Add(name == "*" ? Any : name);
        }
        return segments;
    }

    private static int FindClose(string p, int open)
    {
        var depth = 0;
        char? quote = null;
        for (var i = open; i < p.Length; i++)
        {
            var c = p[i];
            if (quote is not null)
            {
                if (c == quote && p[i - 1] != '\\')
                    quote = null;
                continue;
            }
            if (c is '\'' or '"')
                quote = c;
            else if (c == '[')
                depth++;
            else if (c == ']' && --depth == 0)
                return i;
        }
        return p.Length - 1;
    }

    /// <summary>How a reference path relates to a changed path.</summary>
    internal enum Overlap
    {
        None,

        /// <summary>The reference reads the changed field or something inside it.</summary>
        Inside,

        /// <summary>The reference reads a parent object / array of the changed field (its value changes shape).</summary>
        Ancestor
    }

    /// <summary>Whether reading <paramref name="reference"/> touches <paramref name="changed"/>. Wildcards on either side match any item.</summary>
    internal static Overlap Relate(IReadOnlyList<string> reference, IReadOnlyList<string> changed)
    {
        if (MatchesPrefix(reference, 0, changed, 0))
            return Overlap.Inside;
        return IsAncestor(reference, 0, changed, 0) ? Overlap.Ancestor : Overlap.None;
    }

    /// <summary>
    /// True when the reference pattern matches the whole changed path, possibly continuing below it (the reference reads
    /// the changed field itself or a descendant).
    /// </summary>
    private static bool MatchesPrefix(IReadOnlyList<string> r, int ri, IReadOnlyList<string> c, int ci)
    {
        if (ci == c.Count)
            return true;
        if (ri == r.Count)
            return false;
        if (r[ri] == Deep)
        {
            // ".." matches zero or more segments.
            for (var skip = ci; skip <= c.Count; skip++)
                if (MatchesPrefix(r, ri + 1, c, skip))
                    return true;
            return false;
        }
        return Same(r[ri], c[ci]) && MatchesPrefix(r, ri + 1, c, ci + 1);
    }

    /// <summary>True when the whole reference matches a strict prefix of the changed path.</summary>
    private static bool IsAncestor(IReadOnlyList<string> r, int ri, IReadOnlyList<string> c, int ci)
    {
        if (ri == r.Count)
            return ci < c.Count;
        if (ci == c.Count)
            return false;
        if (r[ri] == Deep)
        {
            for (var skip = ci; skip < c.Count; skip++)
                if (IsAncestor(r, ri + 1, c, skip))
                    return true;
            return false;
        }
        return Same(r[ri], c[ci]) && IsAncestor(r, ri + 1, c, ci + 1);
    }

    /// <summary>A reference segment matches the same name, and <c>*</c> in a reference matches any property or item.</summary>
    private static bool Same(string reference, string changed) => reference == changed || reference == Any;
}
