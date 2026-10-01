using Dispatch.Domain;

namespace Dispatch.Application.Security;

/// <summary>A modified copy of a request, plus how to judge the response it gets back.</summary>
public sealed record Probe(string Location, string Field, string Payload, ApiRequest Request, ProbeKind Kind);

public enum ProbeKind
{
    ErrorSignature,
    Reflection,
    Boundary,
    AuthBypass
}

/// <summary>
/// Builds the modified requests an active scan sends. Probes mutate one parameter, header, or body field at a time with
/// a benign marker or an error-inducing token, so the analyzer can tell whether the endpoint validates and handles
/// input safely. Payloads are diagnostic (they reveal mishandling); they are not exploitation strings.
/// </summary>
public static class Probes
{
    /// <summary>A harmless unique marker; if it comes back unencoded in an HTML response the endpoint reflects input.</summary>
    public const string ReflectionMarker = "dxsr9k7q<zZ>";

    /// <summary>Tokens that a naive back end tends to turn into a database or parser error, revealing missing validation.</summary>
    private static readonly (string Payload, string Note)[] ErrorProbes =
    [
        ("'", "unbalanced single quote"),
        ("\"", "unbalanced double quote"),
        ("';", "quote and statement terminator"),
        ("\\", "trailing backslash"),
        ("{{7*7}}", "template-expression marker")
    ];

    /// <summary>Out-of-range and malformed values that exercise boundary and type handling.</summary>
    private static readonly string[] BoundaryProbes =
    [
        "-1", "0", "2147483648", "9999999999999999999", "1e309", "NaN", "null", "true",
        new string('A', 8192), "../../../../etc/hostname", "%00", "\u0000"
    ];

    public static IEnumerable<Probe> ForRequest(ApiRequest request, ScanOptions options)
    {
        var targets = ParameterTargets(request).ToList();

        // Auth and injection probes are the highest-signal, so they are kept even when the budget is tight; the larger
        // boundary set fills whatever budget remains.
        var priority = new List<Probe>();
        var boundary = new List<Probe>();

        if (options.CheckAuth && HasAuth(request))
            priority.Add(new Probe("request", "authorization", "(removed)", WithoutAuth(request), ProbeKind.AuthBypass));

        if (options.CheckInjection)
            foreach (var (location, field, apply) in targets)
            {
                foreach (var (payload, _) in ErrorProbes)
                    priority.Add(new Probe(location, field, payload, apply(payload), ProbeKind.ErrorSignature));
                priority.Add(new Probe(location, field, ReflectionMarker, apply(ReflectionMarker), ProbeKind.Reflection));
            }

        if (options.CheckBoundaries)
            foreach (var (location, field, apply) in targets)
                foreach (var payload in BoundaryProbes)
                    boundary.Add(new Probe(location, field, payload, apply(payload), ProbeKind.Boundary));

        return priority.Take(options.MaxProbesPerRequest)
            .Concat(boundary.Take(Math.Max(0, options.MaxProbesPerRequest - priority.Count)));
    }

    private static IEnumerable<(string Location, string Field, Func<string, ApiRequest> Apply)> ParameterTargets(ApiRequest request)
    {
        foreach (var param in request.QueryParams.Where(p => p.IsActive))
            yield return ("query", param.Key, payload => MutateQuery(request, param.Key, payload));

        // Path segments that look like values ({{id}} placeholders or numeric/uuid segments). The ordinal is the position
        // among value segments only, matching MutatePathSegment.
        var segments = PathSegments(request.Url);
        var valueOrdinal = 0;
        for (var i = 0; i < segments.Count; i++)
            if (LooksLikeValue(segments[i]))
            {
                var ordinal = valueOrdinal++;
                yield return ("path", $"segment {i + 1}", payload => MutatePathSegment(request, ordinal, payload));
            }

        if (request.Kind is RequestKind.Http && request.Body.Mode is BodyMode.FormUrlEncoded or BodyMode.Multipart)
            foreach (var field in request.Body.FormFields.Where(f => f.IsActive && !f.IsFile))
                yield return ("body", field.Key, payload => MutateFormField(request, field.Key, payload));

        if (request.Kind is RequestKind.Http && request.Body.Mode == BodyMode.Json && request.Body.Content.Trim().Length > 0)
            foreach (var field in JsonStringFields(request.Body.Content))
                yield return ("body", field, payload => MutateJsonField(request, field, payload));
    }

    // ---- Mutations (each returns a fresh request) --------------------------------------------------

    private static ApiRequest MutateQuery(ApiRequest request, string key, string payload)
    {
        var copy = request.Clone();
        var param = copy.QueryParams.FirstOrDefault(p => p.Key == key);
        if (param is not null)
            param.Value = payload;
        copy.Url = Requests.QueryString.WithParams(copy.Url, copy.QueryParams);
        return copy;
    }

    private static ApiRequest MutatePathSegment(ApiRequest request, int ordinal, string payload)
    {
        var copy = request.Clone();
        var (prefix, path, suffix) = SplitUrl(copy.Url);
        var segments = path.Split('/');
        var valueIndices = new List<int>();
        for (var i = 0; i < segments.Length; i++)
            if (LooksLikeValue(segments[i]))
                valueIndices.Add(i);
        if (ordinal < valueIndices.Count)
            segments[valueIndices[ordinal]] = Uri.EscapeDataString(payload);
        copy.Url = prefix + string.Join('/', segments) + suffix;
        return copy;
    }

    private static ApiRequest MutateFormField(ApiRequest request, string key, string payload)
    {
        var copy = request.Clone();
        var field = copy.Body.FormFields.FirstOrDefault(f => f.Key == key);
        if (field is not null)
            field.Value = payload;
        return copy;
    }

    private static ApiRequest MutateJsonField(ApiRequest request, string field, string payload)
    {
        var copy = request.Clone();
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(copy.Body.Content);
            SetJsonField(node, field, payload);
            copy.Body.Content = node?.ToJsonString() ?? copy.Body.Content;
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return copy;
    }

    private static void SetJsonField(System.Text.Json.Nodes.JsonNode? node, string field, string payload)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj && obj.ContainsKey(field))
            obj[field] = payload;
    }

    private static IEnumerable<string> JsonStringFields(string json)
    {
        System.Text.Json.Nodes.JsonNode? node;
        try
        {
            node = System.Text.Json.Nodes.JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            yield break;
        }
        if (node is System.Text.Json.Nodes.JsonObject obj)
            foreach (var (key, value) in obj)
                if (value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out _))
                    yield return key;
    }

    private static bool HasAuth(ApiRequest request) =>
        request.Auth.Mode != AuthMode.None
        || request.Headers.Any(h => h.IsActive && h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));

    private static ApiRequest WithoutAuth(ApiRequest request)
    {
        var copy = request.Clone();
        copy.Auth = new AuthSettings { Mode = AuthMode.None };
        copy.Headers = copy.Headers.Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                                               && !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).ToList();
        return copy;
    }

    // ---- URL helpers -------------------------------------------------------------------------------

    private static (string Prefix, string Path, string Suffix) SplitUrl(string url)
    {
        var query = url.IndexOfAny(['?', '#']);
        var suffix = query >= 0 ? url[query..] : "";
        var withoutQuery = query >= 0 ? url[..query] : url;
        var schemeEnd = withoutQuery.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return ("", withoutQuery, suffix);
        var authorityEnd = withoutQuery.IndexOf('/', schemeEnd + 3);
        if (authorityEnd < 0)
            return (withoutQuery, "", suffix);
        return (withoutQuery[..authorityEnd], withoutQuery[authorityEnd..], suffix);
    }

    private static List<string> PathSegments(string url) => SplitUrl(url).Path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool LooksLikeValue(string segment) =>
        segment.StartsWith("{{") || segment.StartsWith('{') || segment.StartsWith(':')
        || long.TryParse(segment, out _) || Guid.TryParse(segment, out _);
}
