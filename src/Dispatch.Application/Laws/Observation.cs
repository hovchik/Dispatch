using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Capture;
using Dispatch.Application.Running;
using Dispatch.Domain;

namespace Dispatch.Application.Laws;

/// <summary>One observed request/response pair, from history, captured traffic, a HAR file or a collection run.</summary>
public sealed record Observation(
    DateTimeOffset At,
    string Method,
    string Url,
    string RequestBody,
    int Status,
    string ResponseBody)
{
    /// <summary><c>GET /users/{id}</c>: method plus the path with id-like segments replaced.</summary>
    public string Endpoint => $"{Method.ToUpperInvariant()} {Template ?? Observations.PathTemplate(Url)}";

    /// <summary>A path template learned from the traffic (see <see cref="Observations.LearnTemplates"/>), overriding the heuristic one.</summary>
    public string? Template { get; init; }

    /// <summary>The URL path (no scheme, host or query), for matching ids across calls.</summary>
    public string Path => Observations.PathOf(Url);

    public bool Succeeded => Status is >= 200 and < 300;
}

/// <summary>Builds observations from the places Dispatch already keeps traffic.</summary>
public static partial class Observations
{
    public static Observation? FromHistory(HistoryEntry entry)
    {
        if (entry.Kind != RequestKind.Http || entry.Response is not { Error: null } response)
            return null;
        // Older history has no effective URL; the template still groups by endpoint (stateful laws then can't match ids).
        var url = response.EffectiveUrl.Length > 0 ? response.EffectiveUrl : entry.Url;
        return new Observation(entry.Timestamp, entry.Method.ToString().ToUpperInvariant(), url, response.RequestBody, response.StatusCode,
            response.Body);
    }

    public static Observation? FromCapture(CapturedExchange exchange) =>
        exchange.Error is not null
            ? null
            : new Observation(exchange.Timestamp, exchange.Method.ToUpperInvariant(), exchange.Url, exchange.RequestBody, exchange.StatusCode,
                exchange.ResponseBody);

    public static Observation? FromRun(RequestRunResult result, DateTimeOffset at)
    {
        var response = result.Response;
        if (result.Request.Kind != RequestKind.Http || !response.HasResponse)
            return null;
        return new Observation(at, result.Request.Method.ToString().ToUpperInvariant(), response.EffectiveUrl ?? result.Request.Url,
            ResponseSnapshot.BodyOf(response.RawRequest), response.StatusCode, response.Body);
    }

    /// <summary>Entries of a HAR file (browser devtools or the capture proxy's export), in time order.</summary>
    public static IReadOnlyList<Observation> FromHar(JsonNode root)
    {
        var list = new List<Observation>();
        foreach (var entry in root["log"]?["entries"]?.AsArray() ?? [])
        {
            var request = entry?["request"];
            var response = entry?["response"];
            if (request is null || response is null || response["status"]?.GetValue<int>() is not int status || status <= 0)
                continue;
            var at = DateTimeOffset.TryParse(entry!["startedDateTime"]?.ToString(), out var started) ? started : DateTimeOffset.MinValue;
            list.Add(new Observation(at, request["method"]?.ToString() ?? "GET", request["url"]?.ToString() ?? "",
                request["postData"]?["text"]?.ToString() ?? "", status, response["content"]?["text"]?.ToString() ?? ""));
        }
        return list.OrderBy(o => o.At).ToList();
    }

    /// <summary>The path of a URL, also for templates like <c>{{base}}/users/{{id}}</c>.</summary>
    public static string PathOf(string url)
    {
        var u = url.Trim();
        var query = u.IndexOfAny(['?', '#']);
        if (query >= 0)
            u = u[..query];
        if (Uri.TryCreate(u, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
            return absolute.AbsolutePath;
        // {{base}}/users → /users
        if (u.StartsWith("{{", StringComparison.Ordinal) && u.IndexOf("}}", StringComparison.Ordinal) is var end and > 0)
            return u[(end + 2)..] is { Length: > 0 } rest ? rest : "/";
        // host:port/users → /users
        var slash = u.IndexOf('/');
        return slash >= 0 ? u[slash..] : "/";
    }

    /// <summary>Replaces id-like path segments (numbers, UUIDs, long hex or mixed tokens, {{variables}}) with <c>{id}</c>.</summary>
    public static string PathTemplate(string url)
    {
        var segments = PathOf(url).Split('/');
        for (var i = 0; i < segments.Length; i++)
            if (IsIdSegment(segments[i]))
                segments[i] = "{id}";
        var path = string.Join('/', segments).TrimEnd('/');
        return path.Length == 0 ? "/" : path;
    }

    /// <summary>Numbers, UUIDs, hex, {{variables}} and tokens with digits (u100, ord_42) — but not API versions like v2.</summary>
    internal static bool IsIdSegment(string s) =>
        s.Length > 0 && (s.StartsWith("{{", StringComparison.Ordinal) || s.StartsWith('{') && s.EndsWith('}') || NumberRegex().IsMatch(s)
                         || UuidRegex().IsMatch(s) || HexRegex().IsMatch(s)
                         || s.Length >= 3 && s.Any(char.IsDigit) && !VersionRegex().IsMatch(s));

    /// <summary>
    /// Learns path templates from the traffic itself: where paths that are otherwise identical differ in one position
    /// across at least <paramref name="minDistinct"/> values (<c>/users/alice</c>, <c>/users/bob</c>, …), that position
    /// is a parameter. Returns the observations with <see cref="Observation.Template"/> set.
    /// </summary>
    public static IReadOnlyList<Observation> LearnTemplates(IReadOnlyList<Observation> observations, int minDistinct = 3)
    {
        var templates = observations.Select(o => PathTemplate(o.Url).Split('/')).ToList();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var group in Enumerable.Range(0, templates.Count).GroupBy(i => templates[i].Length))
            {
                var width = group.Key;
                for (var position = 1; position < width; position++)
                {
                    var p = position;
                    foreach (var siblings in group.GroupBy(i => string.Join('/', templates[i].Select((s, k) => k == p ? "" : s))))
                    {
                        var values = siblings.Select(i => templates[i][p]).Where(v => v != "{id}").Distinct(StringComparer.Ordinal).Count();
                        if (values < minDistinct)
                            continue;
                        foreach (var i in siblings)
                            if (templates[i][p] != "{id}")
                            {
                                templates[i][p] = "{id}";
                                changed = true;
                            }
                    }
                }
            }
        }
        return observations.Select((o, i) => o with { Template = string.Join('/', templates[i]) is { Length: > 0 } t ? t : "/" }).ToList();
    }

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}$")]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"^v\d+(\.\d+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{12,}$")]
    private static partial Regex HexRegex();
}
