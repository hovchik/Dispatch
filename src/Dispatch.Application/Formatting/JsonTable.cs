using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Testing;

namespace Dispatch.Application.Formatting;

/// <summary>A JSON array of objects laid out as rows and columns.</summary>
public sealed record JsonTable(string Source, IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows, int TotalRows)
{
    public const int MaxRows = 5000;
    private const int MaxDepth = 3;

    /// <summary>
    /// Finds the array to tabulate: the one at <paramref name="path"/> (JSONPath), else the root array, else the largest
    /// array property of the root object (e.g. <c>{"data":[...]}</c>). Nested objects become <c>a.b</c> columns; nested
    /// arrays are shown as JSON. Returns null when the body has no array of objects or values.
    /// </summary>
    public static JsonTable? Build(string body, string? path = null)
    {
        JsonNode? root;
        try
        {
            var t = body.TrimStart();
            if (!(t.StartsWith('{') || t.StartsWith('[')))
                return null;
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        var (array, source) = Locate(root, path);
        if (array is null || array.Count == 0)
            return null;

        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var flattened = new List<Dictionary<string, string>>();
        foreach (var item in array.Take(MaxRows))
        {
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item is JsonObject obj)
                Flatten(obj, "", row, 0);
            else
                row["value"] = Text(item);
            foreach (var key in row.Keys)
                if (seen.Add(key))
                    columns.Add(key);
            flattened.Add(row);
        }

        var rows = flattened.Select(r => columns.Select(c => r.GetValueOrDefault(c) ?? "").ToArray()).ToList();
        return new JsonTable(source, columns, rows, array.Count);
    }

    private static (JsonArray? Array, string Source) Locate(JsonNode? root, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && root is not null)
        {
            var matches = JsonPath.Select(root, path);
            if (matches.Count == 1 && matches[0] is JsonArray selected)
                return (selected, path.Trim());
            return (new JsonArray(matches.Select(m => m?.DeepClone()).ToArray()), path.Trim());
        }
        if (root is JsonArray rootArray)
            return (rootArray, "$");
        if (root is JsonObject obj)
        {
            var best = obj.Where(p => p.Value is JsonArray { Count: > 0 }).OrderByDescending(p => ((JsonArray)p.Value!).Count).FirstOrDefault();
            if (best.Value is JsonArray array)
                return (array, "$." + best.Key);
        }
        return (null, "");
    }

    private static void Flatten(JsonObject obj, string prefix, Dictionary<string, string> row, int depth)
    {
        foreach (var (key, value) in obj)
        {
            var name = prefix + key;
            if (value is JsonObject child && depth < MaxDepth && child.Count > 0)
                Flatten(child, name + ".", row, depth + 1);
            else
                row[name] = Text(value);
        }
    }

    private static string Text(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString()
    };

    /// <summary>RFC 4180 CSV of the table.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns.Select(Escape)));
        foreach (var row in Rows)
            sb.AppendLine(string.Join(",", row.Select(Escape)));
        return sb.ToString();

        static string Escape(string value) =>
            value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public string Summary => TotalRows > Rows.Count
        ? $"{Source}: showing {Rows.Count.ToString("N0", CultureInfo.InvariantCulture)} of {TotalRows.ToString("N0", CultureInfo.InvariantCulture)} rows · {Columns.Count} columns"
        : $"{Source}: {Rows.Count} row(s) · {Columns.Count} column(s)";
}
