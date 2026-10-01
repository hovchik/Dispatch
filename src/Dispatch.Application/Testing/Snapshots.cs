using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Diff;
using Dispatch.Domain;

namespace Dispatch.Application.Testing;

/// <summary>How <see cref="ValueSource.Snapshot"/> assertions treat the stored snapshot.</summary>
public enum SnapshotMode
{
    /// <summary>Compare only; a missing snapshot fails (CI).</summary>
    Verify,

    /// <summary>Record snapshots that don't exist yet, compare the rest (interactive default).</summary>
    RecordMissing,

    /// <summary>Overwrite every snapshot with the current response.</summary>
    Update
}

/// <summary>
/// Snapshot testing: the first response is stored on the assertion and later responses must match it. JSON is compared
/// structurally (key order doesn't matter) with ignore paths such as <c>$.createdAt, $..id</c> for values that change.
/// </summary>
public static class Snapshots
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The snapshot text for a response: pretty JSON when the body is JSON, else the body as-is.</summary>
    public static string Capture(ApiResponse response)
    {
        var body = response.Body;
        return TryJson(body, out var node) ? node!.ToJsonString(Pretty) : body.Replace("\r\n", "\n");
    }

    public static IReadOnlyList<string> ParseIgnorePaths(string? text) =>
        (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Differences between the stored snapshot and a response (empty when they match).</summary>
    public static IReadOnlyList<string> Compare(string snapshot, ApiResponse response, string? ignorePaths)
    {
        var ignore = ParseIgnorePaths(ignorePaths);
        if (TryJson(snapshot, out _) && TryJson(response.Body, out _))
            return ResponseDiff.Json(snapshot, response.Body, ignore).Select(c => c.ToString()).ToList();

        var actual = response.Body.Replace("\r\n", "\n");
        var expected = snapshot.Replace("\r\n", "\n");
        if (actual == expected)
            return [];
        return ResponseDiff.Lines(expected, actual)
            .Where(l => l.Kind != DiffKind.Same)
            .Select(l => (l.Kind == DiffKind.Added ? "+ " : "- ") + l.Text)
            .ToList();
    }

    private static bool TryJson(string text, out JsonNode? node)
    {
        node = null;
        var t = text.TrimStart();
        if (!(t.StartsWith('{') || t.StartsWith('[')))
            return false;
        try
        {
            node = JsonNode.Parse(text);
            return node is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
