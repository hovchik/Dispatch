using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dispatch.Application.Running;

/// <summary>Reads data-driven test rows from CSV (header row + RFC 4180 quoting) or JSON (array of objects).</summary>
public static class DataFile
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Load(string path)
    {
        var text = File.ReadAllText(path);
        return Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith('[')
            ? ParseJson(text)
            : ParseCsv(text);
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseJson(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Data file is not valid JSON: {ex.Message}");
        }
        if (root is not JsonArray array)
            throw new FormatException("A JSON data file must be an array of objects.");

        return array.Select((item, i) => item is JsonObject obj
                ? (IReadOnlyDictionary<string, string>)obj.ToDictionary(kv => kv.Key, kv => kv.Value switch
                {
                    null => "",
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    _ => kv.Value.ToJsonString()
                })
                : throw new FormatException($"Row {i + 1} of the data file is not an object."))
            .ToList();
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseCsv(string csv)
    {
        var records = ParseCsvRecords(csv).Where(r => r.Count > 1 || (r.Count == 1 && r[0].Length > 0)).ToList();
        if (records.Count == 0)
            return [];
        var header = records[0].Select(h => h.Trim()).ToList();
        return records.Skip(1).Select(row =>
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < header.Count; i++)
                map[header[i]] = i < row.Count ? row[i] : "";
            return (IReadOnlyDictionary<string, string>)map;
        }).ToList();
    }

    private static IEnumerable<List<string>> ParseCsvRecords(string csv)
    {
        var delimiter = DetectDelimiter(csv);
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                    i++;
                record.Add(field.ToString());
                field.Clear();
                yield return record;
                record = [];
            }
            else
            {
                field.Append(c);
            }
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }

    /// <summary>Comma by default; semicolon or tab when the header uses them (common in European Excel exports).</summary>
    private static char DetectDelimiter(string csv)
    {
        var firstLine = csv.Split('\n', 2)[0];
        var counts = new[] { ',', ';', '\t' }.Select(d => (d, firstLine.Count(c => c == d))).OrderByDescending(x => x.Item2).First();
        return counts.Item2 > 0 ? counts.d : ',';
    }
}
