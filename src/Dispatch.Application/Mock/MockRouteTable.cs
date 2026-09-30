using System.Text.RegularExpressions;
using Dispatch.Domain;

namespace Dispatch.Application.Mock;

public sealed record MockRoute(ApiRequest Request, string Method, Regex Pattern, IReadOnlyList<string> Parameters, string Template)
{
    /// <summary>Routes with more literal segments win: /users/me before /users/{{id}}.</summary>
    public int Specificity => Template.Split('/').Count(s => s.Length > 0 && !s.StartsWith('{') && !s.StartsWith(':'));
}

public sealed record MockMatch(ApiRequest Request, ResponseExample? Example, IReadOnlyDictionary<string, string> Variables, string Template);

/// <summary>
/// Maps incoming requests to saved requests and their examples. URL templates are taken from the requests with the
/// base URL removed; <c>{{var}}</c>, <c>{var}</c> and <c>:var</c> path segments match anything and are captured.
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

    public static MockRouteTable Build(IEnumerable<ApiRequest> requests)
    {
        var routes = new List<MockRoute>();
        foreach (var request in requests.Where(r => r.Kind is RequestKind.Http or RequestKind.GraphQl or RequestKind.Soap))
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
            var method = request.Kind == RequestKind.Http ? request.Method.ToString().ToUpperInvariant() : "POST";
            routes.Add(new MockRoute(request, method, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), parameters, template));
        }
        return new MockRouteTable(routes.OrderByDescending(r => r.Specificity).ThenBy(r => r.Parameters.Count).ToList());
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

        foreach (var route in _routes)
        {
            if (!route.Method.Equals(method, StringComparison.OrdinalIgnoreCase))
                continue;
            var match = route.Pattern.Match(path);
            if (!match.Success)
                continue;
            // SOAP operations share a URL; tell them apart by SOAPAction.
            if (route.Request.Kind == RequestKind.Soap && !SoapActionMatches(route.Request, headers))
                continue;

            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < route.Parameters.Count; i++)
                variables[route.Parameters[i]] = Uri.UnescapeDataString(match.Groups[i + 1].Value);
            foreach (var (k, v) in query)
                variables.TryAdd(k, v);

            return new MockMatch(route.Request, ChooseExample(route.Request, query, headers, body), variables, route.Template);
        }
        return null;
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

    /// <summary>The example whose match rules all pass (most rules first), else the first example without rules.</summary>
    private static ResponseExample? ChooseExample(ApiRequest request, IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers, string body)
    {
        static int RuleCount(ResponseExample e) =>
            e.MatchQuery.Count(q => q.IsActive) + e.MatchHeaders.Count(h => h.IsActive) + (e.MatchBodyContains.Length > 0 ? 1 : 0);

        bool Matches(ResponseExample e) =>
            e.MatchQuery.Where(q => q.IsActive).All(q => query.TryGetValue(q.Key, out var v) && v == q.Value)
            && e.MatchHeaders.Where(h => h.IsActive).All(h => headers.TryGetValue(h.Key, out var v) && v == h.Value)
            && (e.MatchBodyContains.Length == 0 || body.Contains(e.MatchBodyContains, StringComparison.Ordinal));

        return request.Examples.Where(e => RuleCount(e) > 0 && Matches(e)).OrderByDescending(RuleCount).FirstOrDefault()
               ?? request.Examples.FirstOrDefault(e => RuleCount(e) == 0)
               ?? request.Examples.FirstOrDefault();
    }
}
