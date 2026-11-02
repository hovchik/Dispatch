using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Requests;
using Dispatch.Application.Testing;
using Dispatch.Domain;

namespace Dispatch.Application.Probes;

/// <summary>Small request / response helpers the endpoint probes share.</summary>
public static class ProbeHelpers
{
    public static string? Header(ApiResponse response, string name) =>
        response.Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    public static IEnumerable<string> Headers(ApiResponse response, string name) =>
        response.Headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);

    /// <summary>The body as JSON, or null when it isn't JSON.</summary>
    public static JsonNode? TryJson(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var t = body.TrimStart();
        if (t[0] is not ('{' or '['))
            return null;
        try
        {
            return ResponseValues.ParseJson(body);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>First JSONPath that matches a non-null value.</summary>
    public static (string Path, JsonNode Node)? FirstMatch(JsonNode? root, IEnumerable<string> paths)
    {
        if (root is null)
            return null;
        foreach (var path in paths)
        {
            var match = JsonPath.Select(root, path);
            if (match.Count > 0 && match[0] is { } node && !(node is JsonValue v && v.GetValueKind() == JsonValueKind.Null))
                return (path, node);
        }
        return null;
    }

    /// <summary>A copy whose query has <paramref name="name"/> set to <paramref name="value"/> (added when missing).</summary>
    public static ApiRequest WithQuery(ApiRequest request, string name, string value)
    {
        var copy = request.Clone();
        if (copy.QueryParams.Count == 0)
            copy.QueryParams = QueryString.Parse(copy.Url);
        var existing = copy.QueryParams.FirstOrDefault(p => p.IsActive && p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            existing.Value = value;
        else
            copy.QueryParams.Add(new KeyValueItem(name, value));
        copy.Url = QueryString.WithParams(copy.Url, copy.QueryParams);
        return copy;
    }

    /// <summary>The active value of query parameter <paramref name="name"/>, if any.</summary>
    public static string? Query(ApiRequest request, string name) =>
        (request.QueryParams.Count > 0 ? request.QueryParams : QueryString.Parse(request.Url))
        .FirstOrDefault(p => p.IsActive && p.Key.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>A copy pointing at an absolute (or relative) URL the server handed out, e.g. a "next" link.</summary>
    public static ApiRequest WithUrl(ApiRequest request, string url, string? baseUrl)
    {
        var copy = request.Clone();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var absolute) && baseUrl is not null
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) && Uri.TryCreate(b, url, out var resolved))
            absolute = resolved;
        copy.Url = absolute?.ToString() ?? url;
        copy.QueryParams = QueryString.Parse(copy.Url);
        return copy;
    }

    /// <summary>A copy with header <paramref name="name"/> set (replacing any existing one), or removed when value is null.</summary>
    public static ApiRequest WithHeader(ApiRequest request, string name, string? value)
    {
        var copy = request.Clone();
        copy.Headers.RemoveAll(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (value is not null)
            copy.Headers.Add(new KeyValueItem(name, value));
        return copy;
    }

    public static bool HasHeader(ApiRequest request, string name) =>
        request.Headers.Any(h => h.IsActive && h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static readonly HashSet<string> VolatileWords =
        ["time", "date", "datetime", "timestamp", "ts", "created", "updated", "modified", "nonce", "expires", "expiry", "etag", "now", "generated"];

    private static readonly HashSet<string> VolatileIdOwners = ["request", "trace", "span", "correlation", "transaction"];

    /// <summary>Splits a member name into lower-case words: createdAt → created, at; request_id → request, id.</summary>
    public static IReadOnlyList<string> Words(string name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var boundary = !char.IsLetterOrDigit(c) || char.IsUpper(c) && current.Length > 0 && (char.IsLower(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]));
            if (boundary && current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
            if (char.IsLetterOrDigit(c))
                current.Append(char.ToLowerInvariant(c));
        }
        if (current.Length > 0)
            words.Add(current.ToString());
        return words;
    }

    /// <summary>
    /// Whether a JSON member name is likely to change between identical requests: timestamps (createdAt, updated_at,
    /// timestamp) and trace ids (requestId, trace_id). Matched by whole words, so candidateId or updateable aren't.
    /// </summary>
    public static bool IsVolatile(string name)
    {
        var words = Words(name);
        if (words.Count == 0)
            return false;
        if (words.Any(VolatileWords.Contains))
            return true;
        // createdAt / modifiedOn style: a past participle followed by "at" / "on".
        if (words.Count >= 2 && words[^1] is "at" or "on" && words[^2].EndsWith("ed", StringComparison.Ordinal))
            return true;
        return words.Count >= 2 && words[^1] == "id" && VolatileIdOwners.Contains(words[^2]);
    }

    /// <summary>Same scheme, host and port.</summary>
    public static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase) && a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    /// <summary>Whether two responses carry the same body: compares the bytes of binary bodies, the text otherwise.</summary>
    public static bool SameBody(ApiResponse a, ApiResponse b) =>
        a.BodyBytes is { } x && b.BodyBytes is { } y ? x.AsSpan().SequenceEqual(y) : a.Body == b.Body;

    /// <summary>
    /// Whether the request that was actually sent carried credentials: Authorization or Cookie in the raw request (which
    /// includes cookie-jar cookies and script-added headers), else in the saved request.
    /// </summary>
    /// <remarks>
    /// <c>Other</c> covers cookies and API keys: HTTP's special shared-cache rule only applies to Authorization.
    /// </remarks>
    public static (bool Authorization, bool Other) SentCredentials(ApiRequest request, ApiResponse response)
    {
        var raw = response.RawRequest ?? string.Empty;
        bool InRaw(string header) => raw.Split('\n').Any(l => l.TrimStart().StartsWith(header + ":", StringComparison.OrdinalIgnoreCase));
        var authorization = InRaw("Authorization") || HasHeader(request, "Authorization") || request.Auth.Mode is not (AuthMode.None or AuthMode.ApiKey);
        var other = InRaw("Cookie") || HasHeader(request, "Cookie") || request.Auth.Mode == AuthMode.ApiKey;
        return (authorization, other);
    }

    /// <summary>The body with volatile members removed and keys sorted, for "same response?" comparisons.</summary>
    public static string Canonical(string? body)
    {
        var json = TryJson(body);
        return json is null ? (body ?? string.Empty).Trim() : Canonical(json);
    }

    public static string Canonical(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(",", o.Where(p => !IsVolatile(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        null => "null",
        _ => node.ToJsonString()
    };

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static readonly string[] IdPaths = ["$.id", "$.uuid", "$._id", "$.key", "$.data.id", "$.data.uuid", "$.result.id", "$.item.id"];

    /// <summary>
    /// The identity of a created or returned resource: the Location header, else an id-like member of the body.
    /// </summary>
    public static string? ResourceId(ApiResponse response)
    {
        if (Header(response, "Location") is { Length: > 0 } location)
            return location;
        var json = TryJson(response.Body);
        if (json is JsonObject)
        {
            if (FirstMatch(json, IdPaths) is { } id)
                return JsonPath.ToText(id.Node);
            // A single top-level object member that holds an id: { "order": { "id": 7 } }.
            if (json is JsonObject o && o.Count == 1 && o.First().Value is JsonObject inner && inner["id"] is { } innerId)
                return JsonPath.ToText(innerId);
        }
        return null;
    }
}
