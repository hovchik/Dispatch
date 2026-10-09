using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Domain;

namespace Dispatch.Application.Mock;

public sealed record MockRoute(ApiRequest Request, string Method, Regex Pattern, IReadOnlyList<string> Parameters, string Template)
{
    /// <summary>Routes with more literal segments win: /users/me before /users/{{id}}.</summary>
    public int Specificity => Template.Split('/').Count(s => s.Length > 0 && !s.StartsWith('{') && !s.StartsWith(':'));

    /// <summary>
    /// For GraphQL requests sharing one URL: the operation name (from the settings or the query), else the first root
    /// field (<c>{ users { … } }</c> → <c>users</c>). Null when the request says nothing about its operation.
    /// </summary>
    public string? Operation { get; init; }
}

/// <summary>Route "methods" for replayed streaming sessions.</summary>
public static class SessionMethods
{
    public const string WebSocket = "WS";
    public const string Sse = "SSE";
}

/// <summary>
/// A matched request. <see cref="Variables"/> holds the path parameters and the query string (path first);
/// <see cref="PathVariables"/> only the path parameters, in template order.
/// </summary>
public sealed record MockMatch(ApiRequest Request, ResponseExample? Example, IReadOnlyDictionary<string, string> Variables, string Template,
    IReadOnlyList<KeyValuePair<string, string>> PathVariables);

/// <summary>
/// Maps incoming requests to saved requests and their examples. URL templates are taken from the requests with the
/// base URL removed; <c>{{var}}</c>, <c>{var}</c> and <c>:var</c> path segments match anything and are captured.
/// Several requests may share a method and path (variants with different match rules, or GraphQL operations on one
/// endpoint): their examples are chosen from together. A client can also ask for a particular example with
/// <c>Prefer: example=Name</c>, <c>Prefer: code=404</c>, <c>X-Mock-Example</c> or <c>X-Mock-Status</c>.
/// </summary>
public sealed partial class MockRouteTable
{
    private readonly List<MockRoute> _routes;

    private MockRouteTable(List<MockRoute> routes) => _routes = routes;

    public IReadOnlyList<MockRoute> Routes => _routes;

    [GeneratedRegex(@"^\{\{[^}]+\}\}|^[a-z][a-z0-9+.-]*://[^/]+", RegexOptions.IgnoreCase)]
    private static partial Regex OriginRegex();

    [GeneratedRegex(@"^(?:\{\{\s*([^}\s]+)\s*\}\}|\{([^}]+)\}|:([A-Za-z_]\w*))$")]
    private static partial Regex ParameterSegment();

    [GeneratedRegex(@"^\s*(?:query|mutation|subscription)\s+([A-Za-z_]\w*)")]
    private static partial Regex OperationNameRegex();

    [GeneratedRegex(@"^\s*(?:(?:query|mutation|subscription)\b[^{]*)?\{\s*(?:[A-Za-z_]\w*\s*:\s*)?([A-Za-z_]\w*)")]
    private static partial Regex RootFieldRegex();

    public static MockRouteTable Build(IEnumerable<ApiRequest> requests)
    {
        var routes = new List<MockRoute>();
        // Streaming requests become routes once they have a recorded session to replay.
        foreach (var request in requests.Where(r => r.Kind is RequestKind.Http or RequestKind.GraphQl or RequestKind.Soap
                                                    || r.Kind is RequestKind.WebSocket or RequestKind.Sse && r.Examples.Any(e => e.Session.Count > 0)))
        {
            var template = PathTemplate(request.Url);
            var parameters = new List<string>();
            var pattern = "^" + string.Join("/", template.Split('/').Select(segment =>
            {
                var match = ParameterSegment().Match(segment);
                if (!match.Success)
                    return Regex.Escape(segment);
                var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
                parameters.Add(name);
                return "([^/]+)";
            })) + "/?$";
            var method = request.Kind switch
            {
                RequestKind.Http => request.Method.ToString().ToUpperInvariant(),
                RequestKind.WebSocket => SessionMethods.WebSocket,
                RequestKind.Sse => SessionMethods.Sse,
                _ => "POST"
            };
            var operation = request.Kind == RequestKind.GraphQl
                ? GraphQlOperation(request.Protocol.GraphQl.OperationName, request.Protocol.GraphQl.Query)
                : null;
            routes.Add(new MockRoute(request, method, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), parameters, template)
            {
                Operation = operation
            });
        }
        return new MockRouteTable(routes.OrderByDescending(r => r.Specificity).ThenBy(r => r.Parameters.Count).ToList());
    }

    /// <summary>The operation a GraphQL document identifies: its name, else its first root field.</summary>
    public static string? GraphQlOperation(string? operationName, string? query)
    {
        if (!string.IsNullOrWhiteSpace(operationName))
            return operationName.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return null;
        var named = OperationNameRegex().Match(query);
        if (named.Success)
            return named.Groups[1].Value;
        var root = RootFieldRegex().Match(query);
        return root.Success ? root.Groups[1].Value : null;
    }

    /// <summary>The path part of a request URL: <c>{{baseUrl}}/users/{{id}}?x=1</c> → <c>/users/{{id}}</c>.</summary>
    public static string PathTemplate(string url)
    {
        var path = url.Trim();
        // Strip the origin: a scheme://host, or a leading {{variable}} (usually {{baseUrl}}), repeatedly.
        for (var guard = 0; guard < 3; guard++)
        {
            var origin = OriginRegex().Match(path);
            if (!origin.Success || (origin.Value.StartsWith("{{") && origin.Length < path.Length && path[origin.Length] != '/'))
                break;
            path = path[origin.Length..];
        }
        path = path.Split('?', '#')[0];
        return path.StartsWith('/') ? path.TrimEnd('/') is { Length: > 0 } p ? p : "/" : "/" + path.TrimEnd('/');
    }

    public MockMatch? Match(string method, string path, IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers, string body)
    {
        path = path.TrimEnd('/');
        if (path.Length == 0)
            path = "/";

        var candidates = Candidates(method, path, headers, body);
        // HEAD is answered by the GET route (the server sends no body).
        if (candidates.Count == 0 && method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            candidates = Candidates("GET", path, headers, body);
        if (candidates.Count == 0)
            return null;

        var (route, match) = candidates[0];
        var pathVariables = new List<KeyValuePair<string, string>>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < route.Parameters.Count; i++)
        {
            var value = Uri.UnescapeDataString(match.Groups[i + 1].Value);
            pathVariables.Add(new KeyValuePair<string, string>(route.Parameters[i], value));
            variables[route.Parameters[i]] = value;
        }
        foreach (var (k, v) in query)
            variables.TryAdd(k, v);

        // Match rules see the path parameters as well as the query string: a rule "id = 404" on /users/{{id}}.
        var ruleValues = new Dictionary<string, string>(query, StringComparer.Ordinal);
        foreach (var (k, v) in pathVariables)
            ruleValues[k] = v;

        var (chosenRequest, example) = ChooseExample(candidates.Select(c => c.Route.Request).ToList(), ruleValues, headers, body);
        return new MockMatch(chosenRequest, example, variables, route.Template, pathVariables);
    }

    /// <summary>
    /// Every route answering this method and path, best first: the most specific template, and among routes sharing
    /// that template, GraphQL operations matching the request body before the rest.
    /// </summary>
    private List<(MockRoute Route, Match Match)> Candidates(string method, string path, IReadOnlyDictionary<string, string> headers, string body)
    {
        var list = new List<(MockRoute Route, Match Match)>();
        string? template = null;
        foreach (var route in _routes)
        {
            if (!route.Method.Equals(method, StringComparison.OrdinalIgnoreCase))
                continue;
            if (template is not null && !string.Equals(route.Template, template, StringComparison.OrdinalIgnoreCase))
                continue;
            var match = route.Pattern.Match(path);
            if (!match.Success)
                continue;
            // SOAP operations share a URL; tell them apart by SOAPAction.
            if (route.Request.Kind == RequestKind.Soap && !SoapActionMatches(route.Request, headers))
                continue;
            template ??= route.Template;
            list.Add((route, match));
        }
        if (list.Count > 1 && list.Any(c => c.Route.Operation is not null))
        {
            var requested = RequestedGraphQlOperation(body);
            int Score((MockRoute Route, Match Match) c) => c.Route.Operation is null || requested is null ? 1
                : string.Equals(c.Route.Operation, requested, StringComparison.Ordinal) ? 2 : 0;
            var best = list.Max(Score);
            if (best > 0)
                list = list.Where(c => Score(c) == best).ToList();
        }
        return list;
    }

    /// <summary>The operation a GraphQL request body asks for (<c>operationName</c>, else parsed from <c>query</c>).</summary>
    private static string? RequestedGraphQlOperation(string body)
    {
        if (body.TrimStart() is not ['{', ..])
            return null;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject payload)
                return null;
            return GraphQlOperation(payload["operationName"]?.ToString(), payload["query"]?.ToString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool SoapActionMatches(ApiRequest request, IReadOnlyDictionary<string, string> headers)
    {
        var expected = request.Protocol.Soap.Action.Trim();
        if (expected.Length == 0)
            return true;
        var action = headers.GetValueOrDefault("SOAPAction")?.Trim('"')
                     ?? Regex.Match(headers.GetValueOrDefault("Content-Type") ?? "", "action=\"?([^\";]+)").Groups[1].Value;
        return action.Length == 0 || action == expected;
    }

    /// <summary>
    /// The example the client asked for (<c>Prefer</c> / <c>X-Mock-*</c> headers), else the example whose match rules
    /// all pass (most rules first, across every candidate request), else the first candidate's first example without
    /// rules, else its first example.
    /// </summary>
    private static (ApiRequest Request, ResponseExample? Example) ChooseExample(IReadOnlyList<ApiRequest> candidates,
        IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string> headers, string body)
    {
        static int RuleCount(ResponseExample e) =>
            e.MatchQuery.Count(q => q.IsActive) + e.MatchHeaders.Count(h => h.IsActive) + (e.MatchBodyContains.Length > 0 ? 1 : 0);

        bool Matches(ResponseExample e) =>
            e.MatchQuery.Where(q => q.IsActive).All(q => values.TryGetValue(q.Key, out var v) && ValueMatches(q.Value, v))
            && e.MatchHeaders.Where(h => h.IsActive).All(h => headers.TryGetValue(h.Key, out var v) && ValueMatches(h.Value, v))
            && (e.MatchBodyContains.Length == 0 || body.Contains(e.MatchBodyContains, StringComparison.Ordinal));

        var first = candidates[0];
        // Streaming routes only replay examples that carry a recorded session.
        if (first.Kind is RequestKind.WebSocket or RequestKind.Sse)
            return (first, first.Examples.Where(e => e.Session.Count > 0 && RuleCount(e) > 0 && Matches(e)).OrderByDescending(RuleCount).FirstOrDefault()
                           ?? first.Examples.FirstOrDefault(e => e.Session.Count > 0));

        var all = candidates.SelectMany(r => r.Examples.Select(e => (Request: r, Example: e))).ToList();
        var (preferredName, preferredStatus) = Preference(headers);
        if (preferredName is not null || preferredStatus is not null)
        {
            var preferred = all.Where(x => RuleCount(x.Example) == 0 || Matches(x.Example))
                .FirstOrDefault(x => (preferredName is null || string.Equals(x.Example.Name.Trim(), preferredName, StringComparison.OrdinalIgnoreCase))
                                     && (preferredStatus is null || x.Example.StatusCode == preferredStatus));
            if (preferred.Example is not null)
                return preferred;
        }

        var ruled = all.Where(x => RuleCount(x.Example) > 0 && Matches(x.Example)).OrderByDescending(x => RuleCount(x.Example)).FirstOrDefault();
        if (ruled.Example is not null)
            return ruled;
        return (first, first.Examples.FirstOrDefault(e => RuleCount(e) == 0) ?? first.Examples.FirstOrDefault());
    }

    /// <summary>Rule values match exactly; <c>*</c> matches any value (the parameter or header just has to be there).</summary>
    private static bool ValueMatches(string rule, string actual) => rule == "*" || rule == actual;

    /// <summary>
    /// What the client asked for: <c>Prefer: example=Not found</c>, <c>Prefer: code=404</c> (both may be combined with
    /// commas, as Prism does), or the <c>X-Mock-Example</c> / <c>X-Mock-Status</c> headers.
    /// </summary>
    private static (string? Name, int? Status) Preference(IReadOnlyDictionary<string, string> headers)
    {
        string? name = headers.GetValueOrDefault("X-Mock-Example")?.Trim() is { Length: > 0 } n ? n : null;
        int? status = int.TryParse(headers.GetValueOrDefault("X-Mock-Status"), out var s) ? s : null;
        if (headers.GetValueOrDefault("Prefer") is { } prefer)
            foreach (var token in prefer.Split(',', ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = token.IndexOf('=');
                if (eq < 0)
                    continue;
                var key = token[..eq].Trim();
                var value = token[(eq + 1)..].Trim().Trim('"');
                if (key.Equals("example", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                    name ??= value;
                else if ((key.Equals("code", StringComparison.OrdinalIgnoreCase) || key.Equals("status", StringComparison.OrdinalIgnoreCase))
                         && int.TryParse(value, out var code))
                    status ??= code;
            }
        return (name, status);
    }
}
