using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Laws;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Spec;

public sealed class OpenApiOptions
{
    /// <summary>info.title; defaults to the collection name or the traffic's host.</summary>
    public string? Title { get; init; }

    public string Version { get; init; } = "1.0.0";

    /// <summary>servers[0].url; defaults to the collection's <c>baseUrl</c> variable or the host seen in the traffic.</summary>
    public string? Server { get; init; }

    /// <summary>Put request and response bodies as <c>example</c> next to the inferred schemas.</summary>
    public bool IncludeExamples { get; init; } = true;
}

/// <summary>
/// Writes an OpenAPI 3.0 document from what Dispatch already knows about an API: a collection (its requests, saved
/// examples and run history) or captured traffic (a HAR file). Paths are templated (<c>/users/42</c> becomes
/// <c>/users/{userId}</c>), query and header parameters are listed, request and response schemas are inferred from the
/// bodies seen (with formats and enumerations), and the requests' auth becomes security schemes.
/// </summary>
public static partial class OpenApiWriter
{
    private static readonly string[] IgnoredHeaders =
    [
        "content-type", "content-length", "accept", "accept-encoding", "accept-language", "authorization", "cookie", "host", "user-agent",
        "connection", "origin", "referer", "sec-fetch-dest", "sec-fetch-mode", "sec-fetch-site", "sec-ch-ua", "sec-ch-ua-mobile",
        "sec-ch-ua-platform", "pragma", "cache-control", "if-none-match", "if-modified-since", "upgrade-insecure-requests", "priority", "te"
    ];

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>One request/response pair the spec is built from.</summary>
    internal sealed record Sample(
        string Method,
        string Url,
        string Template,
        IReadOnlyList<KeyValueItem> Query,
        IReadOnlyList<KeyValueItem> Headers,
        string RequestContentType,
        string RequestBody,
        int? Status,
        string ResponseContentType,
        string ResponseBody,
        AuthSettings? Auth = null,
        string? Name = null,
        string? Description = null,
        string? Tag = null,
        IReadOnlyList<string>? OptionalQuery = null);

    // ---- Sources ------------------------------------------------------------------------------------------

    /// <summary>A collection's HTTP requests, their saved examples, and (optionally) history entries recorded for them.</summary>
    public static JsonObject FromCollection(RequestCollection collection, IReadOnlyList<HistoryEntry>? history = null, OpenApiOptions? options = null)
    {
        options ??= new OpenApiOptions();
        var variables = collection.Variables.Where(v => v.IsActive).GroupBy(v => v.Key.Trim()).ToDictionary(g => g.Key, g => g.Last().Value);
        var server = options.Server ?? ServerOf(collection, variables);
        var samples = new List<Sample>();
        var requests = collection.Requests.Where(r => r.Kind == RequestKind.Http).OrderBy(r => r.SortOrder).ToList();

        var observed = new Dictionary<Guid, List<Observation>>();
        foreach (var entry in history ?? [])
        {
            if (Observations.FromHistory(entry) is not { } observation)
                continue;
            // Only this collection's history: its own requests, or (for file collections, whose ids differ) traffic to the
            // same host that matches one of its endpoints. Another API with the same paths must not leak in.
            var target = requests.FirstOrDefault(r => r.Id == entry.Request.Id);
            if (target is null && (entry.Request.CollectionId == collection.Id || SameHost(observation.Url, server)))
                target = LawTests.FindRequest(observation.Endpoint, requests);
            if (target is null)
                continue;
            if (!observed.TryGetValue(target.Id, out var list))
                observed[target.Id] = list = [];
            list.Add(observation);
        }

        foreach (var request in requests)
        {
            // An absolute URL under the server keeps only the part after it (the server already holds /v1 and the like).
            var trimmedUrl = request.Url.Trim();
            var relativeUrl = trimmedUrl.StartsWith(server + "/", StringComparison.OrdinalIgnoreCase) ? trimmedUrl[server.Length..] : trimmedUrl;
            var path = Observations.PathOf(relativeUrl);
            var template = TemplateOf(path, relativeUrl);
            var sampleUrl = ResolveVariables(request.Url, variables);
            var query = QueryString.Parse(request.Url).Concat(request.QueryParams).Where(q => q.Key.Trim().Length > 0)
                .GroupBy(q => q.Key).Select(g => g.Last()).ToList();
            var optional = query.Where(q => !q.Enabled).Select(q => q.Key).ToList();
            var headers = request.Headers.Where(h => h.IsActive).ToList();
            var contentType = headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value
                              ?? ContentTypeOf(request.Body);
            var body = BodyText(request.Body);
            var method = request.Method.ToString().ToUpperInvariant();
            var tag = request.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            var any = false;
            foreach (var example in request.Examples)
            {
                any = true;
                samples.Add(new Sample(method, sampleUrl, template, query, headers, contentType, body, example.StatusCode, example.ContentType,
                    example.Body, request.Auth, request.Name, request.Description, tag, optional));
            }
            if (observed.TryGetValue(request.Id, out var runs))
            {
                foreach (var run in runs)
                {
                    any = true;
                    samples.Add(new Sample(method, run.Url, template, QueryString.Parse(run.Url).Where(q => q.Key.Length > 0).ToList(), headers, contentType,
                        run.RequestBody.Length > 0 ? run.RequestBody : body, run.Status, GuessContentType(run.ResponseBody), run.ResponseBody,
                        request.Auth, request.Name, request.Description, tag, optional));
                }
            }
            if (!any)
                samples.Add(new Sample(method, sampleUrl, template, query, headers, contentType, body, null, "", "", request.Auth, request.Name,
                    request.Description, tag, optional));
        }

        return Build(samples, options.Title ?? collection.Name, collection.Description, [server], options);
    }

    /// <summary>Traffic from a HAR file (browser devtools or the capture proxy). Static assets are skipped.</summary>
    public static JsonObject FromHar(JsonNode har, OpenApiOptions? options = null)
    {
        options ??= new OpenApiOptions();
        var observations = new List<Observation>();
        var extras = new List<(List<KeyValueItem> Headers, string RequestContentType, string ResponseContentType)>();
        foreach (var entry in har["log"]?["entries"]?.AsArray() ?? [])
        {
            var request = entry?["request"];
            var response = entry?["response"];
            if (request is null || response is null || response["status"]?.GetValue<int>() is not int status || status <= 0)
                continue;
            var url = request["url"]?.ToString() ?? "";
            var resourceType = entry?["_resourceType"]?.ToString();
            if (resourceType is "image" or "stylesheet" or "font" or "media" or "script"
                || StaticAssetRegex().IsMatch(url))
                continue;
            var headers = (request["headers"] as JsonArray ?? [])
                .Select(h => new KeyValueItem(h?["name"]?.ToString() ?? "", h?["value"]?.ToString() ?? ""))
                .Where(h => h.Key.Length > 0 && !h.Key.StartsWith(':')).ToList();
            var requestContentType = request["postData"]?["mimeType"]?.ToString()
                                     ?? headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            var responseContentType = response["content"]?["mimeType"]?.ToString()
                                      ?? (response["headers"] as JsonArray ?? []).FirstOrDefault(h => h?["name"]?.ToString()
                                          ?.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) == true)?["value"]?.ToString() ?? "";
            var at = DateTimeOffset.TryParse(entry!["startedDateTime"]?.ToString(), out var started) ? started : DateTimeOffset.MinValue;
            observations.Add(new Observation(at, request["method"]?.ToString() ?? "GET", url, request["postData"]?["text"]?.ToString() ?? "", status,
                response["content"]?["text"]?.ToString() ?? ""));
            extras.Add((headers, requestContentType, responseContentType));
        }
        if (observations.Count == 0)
            throw new FormatException("The HAR file has no HTTP exchanges to describe.");

        var templated = Observations.LearnTemplates(observations);
        var samples = new List<Sample>();
        for (var i = 0; i < templated.Count; i++)
        {
            var o = templated[i];
            var (headers, requestContentType, responseContentType) = extras[i];
            samples.Add(new Sample(o.Method, o.Url, TemplateOf(o.Template ?? Observations.PathTemplate(o.Url), o.Url),
                QueryString.Parse(o.Url).Where(q => q.Key.Length > 0).ToList(), headers, requestContentType, o.RequestBody, o.Status,
                responseContentType, o.ResponseBody, AuthFromHeaders(headers)));
        }

        var servers = options.Server is { } s ? [s] : HostsOf(samples);
        var title = options.Title ?? (Uri.TryCreate(servers[0], UriKind.Absolute, out var u) ? u.Host + " API" : "Captured API");
        return Build(samples, title, "Generated from captured traffic.", servers, options);
    }

    /// <summary>Observations from running a collection (see <see cref="Observations.FromRun"/>), without saved request details.</summary>
    public static JsonObject FromObservations(IReadOnlyList<Observation> observations, OpenApiOptions? options = null)
    {
        options ??= new OpenApiOptions();
        if (observations.Count == 0)
            throw new FormatException("No HTTP exchanges to describe.");
        var templated = Observations.LearnTemplates(observations);
        var samples = templated.Select(o => new Sample(o.Method, o.Url, TemplateOf(o.Template ?? Observations.PathTemplate(o.Url), o.Url),
            QueryString.Parse(o.Url).Where(q => q.Key.Length > 0).ToList(), [], GuessContentType(o.RequestBody), o.RequestBody, o.Status,
            GuessContentType(o.ResponseBody), o.ResponseBody)).ToList();
        var servers = options.Server is { } s ? [s] : HostsOf(samples);
        var title = options.Title ?? (Uri.TryCreate(servers[0], UriKind.Absolute, out var u) ? u.Host + " API" : "Observed API");
        return Build(samples, title, "Generated from observed traffic.", servers, options);
    }

    // ---- Output -------------------------------------------------------------------------------------------

    public static string ToJson(JsonNode spec) => spec.ToJsonString(Pretty);

    /// <summary>YAML 1.2 text of the document (block style; strings quoted only where YAML would misread them).</summary>
    public static string ToYaml(JsonNode spec)
    {
        var sb = new StringBuilder();
        WriteYaml(sb, spec, 0, false);
        return sb.ToString();
    }

    /// <summary>How many operations the document describes.</summary>
    public static int OperationCount(JsonNode spec) =>
        (spec["paths"] as JsonObject ?? []).Sum(p => (p.Value as JsonObject ?? []).Count(o => Methods.Contains(o.Key)));

    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    // ---- Building -----------------------------------------------------------------------------------------

    private static JsonObject Build(List<Sample> samples, string title, string description, IReadOnlyList<string> servers, OpenApiOptions options)
    {
        var securitySchemes = new JsonObject();
        var paths = new JsonObject();

        var operations = samples.GroupBy(s => (s.Template, Method: s.Method.ToLowerInvariant()))
            .OrderBy(g => samples.FindIndex(s => s.Template == g.Key.Template)).ThenBy(g => Array.IndexOf(Methods, g.Key.Method));
        foreach (var group in operations)
        {
            var (template, method) = group.Key;
            if (!Methods.Contains(method))
                continue;
            if (paths[template] is not JsonObject pathItem)
                paths[template] = pathItem = new JsonObject();
            pathItem[method] = Operation(template, method, group.ToList(), securitySchemes, options);
        }

        var spec = new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = title, ["version"] = options.Version }
        };
        if (description.Length > 0)
            ((JsonObject)spec["info"]!)["description"] = description;
        spec["servers"] = new JsonArray(servers.Select(s => (JsonNode)new JsonObject { ["url"] = s }).ToArray());
        spec["paths"] = paths;
        if (securitySchemes.Count > 0)
            spec["components"] = new JsonObject { ["securitySchemes"] = securitySchemes };
        return spec;
    }

    private static JsonObject Operation(string template, string method, List<Sample> samples, JsonObject securitySchemes, OpenApiOptions options)
    {
        var first = samples[0];
        var operation = new JsonObject();
        var named = samples.FirstOrDefault(s => s.Name is { Length: > 0 });
        operation["summary"] = named?.Name ?? $"{method.ToUpperInvariant()} {template}";
        if (samples.FirstOrDefault(s => s.Description is { Length: > 0 })?.Description is { } desc)
            operation["description"] = desc;
        operation["operationId"] = OperationId(method, template);
        var tag = samples.FirstOrDefault(s => s.Tag is { Length: > 0 })?.Tag
                  ?? template.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(s => !s.StartsWith('{') && !VersionRegex().IsMatch(s));
        if (tag is not null)
            operation["tags"] = new JsonArray(tag);

        var parameters = new JsonArray();
        foreach (var name in PathParameterNames(template))
        {
            var values = samples.Select(s => PathValue(s.Url, template, name)).Where(v => v is not null).Select(v => v!).Distinct().ToList();
            var schema = values.Count > 0 ? SchemaInferrer.Infer(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToList(), true) : new JsonObject { ["type"] = "string" };
            if (schema["enum"] is not null)
                schema.Remove("enum"); // ids are not enumerations
            var parameter = new JsonObject { ["name"] = name, ["in"] = "path", ["required"] = true, ["schema"] = schema };
            if (values.Count > 0 && options.IncludeExamples)
                parameter["example"] = values[0];
            parameters.Add(parameter);
        }
        foreach (var name in samples.SelectMany(s => s.Query.Select(q => q.Key)).Distinct(StringComparer.Ordinal))
        {
            var values = samples.SelectMany(s => s.Query.Where(q => q.Key == name).Select(q => q.Value)).Where(v => !IsVariable(v)).ToList();
            var optional = samples.Any(s => s.OptionalQuery?.Contains(name) == true) || samples.Any(s => s.Query.All(q => q.Key != name));
            var parameter = new JsonObject
            {
                ["name"] = name, ["in"] = "query", ["required"] = !optional,
                ["schema"] = values.Count > 0 ? SchemaInferrer.Infer(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToList(), true) : new JsonObject { ["type"] = "string" }
            };
            if (values.Count > 0 && options.IncludeExamples)
                parameter["example"] = values[0];
            parameters.Add(parameter);
        }
        foreach (var name in samples.SelectMany(s => s.Headers.Select(h => h.Key)).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(h => !IgnoredHeaders.Contains(h.ToLowerInvariant()) && !h.StartsWith("sec-", StringComparison.OrdinalIgnoreCase)))
        {
            var values = samples.SelectMany(s => s.Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value))
                .Where(v => !IsVariable(v)).ToList();
            var required = samples.All(s => s.Headers.Any(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)));
            var parameter = new JsonObject
            {
                ["name"] = name, ["in"] = "header", ["required"] = required,
                ["schema"] = values.Count > 0 ? SchemaInferrer.Infer(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToList(), true) : new JsonObject { ["type"] = "string" }
            };
            if (values.Count > 0 && options.IncludeExamples && !LooksSecret(name))
                parameter["example"] = values[0];
            parameters.Add(parameter);
        }
        if (parameters.Count > 0)
            operation["parameters"] = parameters;

        var bodies = samples.Where(s => s.RequestBody.Trim().Length > 0).ToList();
        if (bodies.Count > 0 && method is not ("get" or "head"))
        {
            var mediaType = bodies.Select(s => MediaType(s.RequestContentType, s.RequestBody)).FirstOrDefault(m => m.Length > 0) ?? "application/json";
            operation["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["content"] = new JsonObject { [mediaType] = Media(mediaType, bodies.Select(s => s.RequestBody).ToList(), options) }
            };
        }

        var responses = new JsonObject();
        foreach (var status in samples.Where(s => s.Status is > 0).GroupBy(s => s.Status!.Value).OrderBy(g => g.Key))
        {
            var response = new JsonObject { ["description"] = ReasonPhrase(status.Key) };
            var withBody = status.Where(s => s.ResponseBody.Trim().Length > 0).ToList();
            if (withBody.Count > 0)
            {
                var mediaType = withBody.Select(s => MediaType(s.ResponseContentType, s.ResponseBody)).FirstOrDefault(m => m.Length > 0) ?? "application/json";
                response["content"] = new JsonObject { [mediaType] = Media(mediaType, withBody.Select(s => s.ResponseBody).ToList(), options) };
            }
            responses[status.Key.ToString(CultureInfo.InvariantCulture)] = response;
        }
        if (responses.Count == 0)
            responses["200"] = new JsonObject { ["description"] = "OK" };
        operation["responses"] = responses;

        if (SecurityScheme(first.Auth, securitySchemes) is { } schemeName)
            operation["security"] = new JsonArray(new JsonObject { [schemeName] = new JsonArray() });

        return operation;
    }

    private static JsonObject Media(string mediaType, List<string> bodies, OpenApiOptions options)
    {
        var media = new JsonObject();
        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = bodies.Select(Parse).ToList();
            var json = parsed.Where(p => p is not null).ToList();
            if (json.Count > 0)
            {
                media["schema"] = SchemaInferrer.Infer(json, false);
                if (options.IncludeExamples)
                    media["example"] = json[0]!.DeepClone();
                return media;
            }
        }
        if (mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            var fields = bodies.Select(b => new JsonObject(QueryString.ParseForm(b).GroupBy(f => f.Key)
                .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)JsonValue.Create(g.Last().Value))))).Cast<JsonNode?>().ToList();
            media["schema"] = SchemaInferrer.Infer(fields, false);
            return media;
        }
        media["schema"] = new JsonObject { ["type"] = "string" };
        if (mediaType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) || mediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
            ((JsonObject)media["schema"]!)["format"] = "binary";
        else if (options.IncludeExamples)
            media["example"] = bodies[0].Length > 2000 ? bodies[0][..2000] : bodies[0];
        return media;
    }

    private static string? SecurityScheme(AuthSettings? auth, JsonObject schemes)
    {
        if (auth is null)
            return null;
        string name;
        JsonObject scheme;
        switch (auth.Mode)
        {
            case AuthMode.Bearer:
                (name, scheme) = ("bearerAuth", new JsonObject { ["type"] = "http", ["scheme"] = "bearer" });
                break;
            case AuthMode.Basic:
                (name, scheme) = ("basicAuth", new JsonObject { ["type"] = "http", ["scheme"] = "basic" });
                break;
            case AuthMode.Digest:
                (name, scheme) = ("digestAuth", new JsonObject { ["type"] = "http", ["scheme"] = "digest" });
                break;
            case AuthMode.ApiKey:
                (name, scheme) = ("apiKeyAuth", new JsonObject
                {
                    ["type"] = "apiKey",
                    ["name"] = auth.ApiKeyName.Length > 0 ? auth.ApiKeyName : "X-API-Key",
                    ["in"] = auth.ApiKeyLocation == ApiKeyLocation.QueryParam ? "query" : "header"
                });
                break;
            case AuthMode.OAuth2:
                var flow = new JsonObject();
                if (auth.OAuth2AuthUrl.Length > 0)
                    flow["authorizationUrl"] = auth.OAuth2AuthUrl;
                if (auth.OAuth2TokenUrl.Length > 0)
                    flow["tokenUrl"] = auth.OAuth2TokenUrl;
                flow["scopes"] = new JsonObject(auth.OAuth2Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => KeyValuePair.Create(s, (JsonNode?)JsonValue.Create(""))));
                var flowName = auth.OAuth2GrantType switch
                {
                    OAuth2GrantType.AuthorizationCode => "authorizationCode",
                    OAuth2GrantType.Password => "password",
                    _ => "clientCredentials"
                };
                (name, scheme) = ("oauth2", new JsonObject { ["type"] = "oauth2", ["flows"] = new JsonObject { [flowName] = flow } });
                break;
            default:
                return null;
        }
        schemes[name] ??= scheme;
        return name;
    }

    private static AuthSettings? AuthFromHeaders(IReadOnlyList<KeyValueItem> headers)
    {
        var authorization = headers.FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        if (authorization is not null)
        {
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return new AuthSettings { Mode = AuthMode.Bearer };
            if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                return new AuthSettings { Mode = AuthMode.Basic };
            if (authorization.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
                return new AuthSettings { Mode = AuthMode.Digest };
        }
        var apiKey = headers.FirstOrDefault(h => ApiKeyHeaderRegex().IsMatch(h.Key));
        return apiKey is null ? null : new AuthSettings { Mode = AuthMode.ApiKey, ApiKeyName = apiKey.Key, ApiKeyLocation = ApiKeyLocation.Header };
    }

    // ---- Paths and templates ------------------------------------------------------------------------------

    /// <summary>The server URL: the explicit option, the <c>baseUrl</c> variable, or the origin of the first absolute request URL.</summary>
    private static string ServerOf(RequestCollection collection, IReadOnlyDictionary<string, string> variables)
    {
        if (variables.TryGetValue("baseUrl", out var baseUrl) && baseUrl.Trim().Length > 0)
            return baseUrl.Trim().TrimEnd('/');
        foreach (var request in collection.Requests.Where(r => r.Kind == RequestKind.Http))
        {
            var url = request.Url.Trim();
            if (url.StartsWith("{{", StringComparison.Ordinal) && url.IndexOf("}}", StringComparison.Ordinal) is var end and > 0)
            {
                var name = url[2..end].Trim();
                if (variables.TryGetValue(name, out var value) && value.Trim().Length > 0)
                    return value.Trim().TrimEnd('/');
                continue;
            }
            if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
                return absolute.GetLeftPart(UriPartial.Authority);
        }
        return "http://localhost";
    }

    private static bool SameHost(string url, string server) =>
        Uri.TryCreate(url, UriKind.Absolute, out var a) && Uri.TryCreate(server, UriKind.Absolute, out var b)
        && a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    /// <summary>Replaces <c>{{variables}}</c> after the base part with collection values, so path parameters get real examples.</summary>
    private static string ResolveVariables(string url, IReadOnlyDictionary<string, string> variables)
    {
        var u = url.Trim();
        var start = u.StartsWith("{{", StringComparison.Ordinal) && u.IndexOf("}}", StringComparison.Ordinal) is var end and > 0 ? end + 2 : 0;
        var head = u[..start];
        var rest = u[start..];
        foreach (var (name, value) in variables)
            if (value.Length > 0 && !value.Contains("{{", StringComparison.Ordinal))
                rest = rest.Replace("{{" + name + "}}", value, StringComparison.Ordinal);
        return head + rest;
    }

    private static IReadOnlyList<string> HostsOf(List<Sample> samples)
    {
        var hosts = samples.Select(s => Uri.TryCreate(s.Url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : null)
            .Where(h => h is not null).GroupBy(h => h!).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        return hosts.Count > 0 ? hosts : ["http://localhost"];
    }

    /// <summary>
    /// Turns a path into an OpenAPI template: <c>{{petId}}</c> becomes <c>{petId}</c>, numeric / UUID / hex segments become
    /// <c>{id}</c> named after the preceding segment (<c>/users/42</c> is <c>/users/{userId}</c>).
    /// </summary>
    internal static string TemplateOf(string path, string? originalUrl = null)
    {
        var segments = path.Split('/');
        var original = originalUrl is null ? null : Observations.PathOf(originalUrl).Split('/');
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            if (s.Length == 0)
                continue;
            string? name = null;
            if (s.StartsWith("{{", StringComparison.Ordinal) && s.EndsWith("}}", StringComparison.Ordinal))
                name = Identifier(s[2..^2].Trim());
            else if (s.StartsWith('{') && s.EndsWith('}'))
                name = s == "{id}" ? IdName(segments, i) : Identifier(s[1..^1]);
            else if (original is not null && i < original.Length && original[i].StartsWith("{{", StringComparison.Ordinal) && original[i].EndsWith("}}", StringComparison.Ordinal))
                name = Identifier(original[i][2..^2].Trim());
            else if (Observations.IsIdSegment(s))
                name = IdName(segments, i);
            if (name is null)
                continue;
            var unique = name;
            for (var n = 2; !used.Add(unique); n++)
                unique = name + n;
            segments[i] = "{" + unique + "}";
        }
        var template = string.Join('/', segments).TrimEnd('/');
        return template.Length == 0 ? "/" : template.StartsWith('/') ? template : "/" + template;
    }

    /// <summary><c>/users/{…}</c> → <c>userId</c>; a parameter right after the root is <c>id</c>.</summary>
    private static string IdName(string[] segments, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var previous = segments[i];
            if (previous.Length == 0 || previous.StartsWith('{') || Observations.IsIdSegment(previous) || VersionRegex().IsMatch(previous))
                continue;
            var singular = Singular(Identifier(previous));
            return singular.Length == 0 ? "id" : char.ToLowerInvariant(singular[0]) + singular[1..] + "Id";
        }
        return "id";
    }

    private static string Singular(string word)
    {
        if (word.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && word.Length > 3)
            return word[..^3] + "y";
        if (word.EndsWith("ses", StringComparison.OrdinalIgnoreCase) || word.EndsWith("xes", StringComparison.OrdinalIgnoreCase)
                                                                      || word.EndsWith("shes", StringComparison.OrdinalIgnoreCase)
                                                                      || word.EndsWith("ches", StringComparison.OrdinalIgnoreCase))
            return word[..^2];
        if (word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && word.Length > 1)
            return word[..^1];
        return word;
    }

    /// <summary>A camelCase identifier made of the letters and digits of <paramref name="text"/>.</summary>
    private static string Identifier(string text)
    {
        var sb = new StringBuilder();
        var upper = false;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(upper && sb.Length > 0 ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
                upper = true;
        }
        return sb.ToString();
    }

    private static string OperationId(string method, string template)
    {
        var sb = new StringBuilder(method.ToLowerInvariant());
        foreach (var segment in template.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith('{'))
            {
                var name = Identifier(segment[1..^1]);
                sb.Append("By").Append(name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "Id");
            }
            else
            {
                var name = Identifier(segment);
                if (name.Length > 0)
                    sb.Append(char.ToUpperInvariant(name[0])).Append(name[1..]);
            }
        }
        return sb.ToString();
    }

    internal static IReadOnlyList<string> PathParameterNames(string template) =>
        template.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s.StartsWith('{') && s.EndsWith('}')).Select(s => s[1..^1]).ToList();

    /// <summary>The concrete value a sample URL has where the template has <c>{name}</c> (null for an unresolved variable).</summary>
    private static string? PathValue(string url, string template, string name)
    {
        var segments = Observations.PathOf(url).Split('/');
        var templateSegments = template.Split('/');
        var offset = segments.Length - templateSegments.Length;
        for (var i = 0; i < templateSegments.Length; i++)
        {
            if (templateSegments[i] != "{" + name + "}")
                continue;
            var index = i + offset;
            if (index < 0 || index >= segments.Length)
                return null;
            var value = Uri.UnescapeDataString(segments[index]);
            return value.Length == 0 || IsVariable(value) ? null : value;
        }
        return null;
    }

    private static bool IsVariable(string value) => value.Contains("{{", StringComparison.Ordinal);

    private static bool LooksSecret(string header) =>
        header.Contains("key", StringComparison.OrdinalIgnoreCase) || header.Contains("token", StringComparison.OrdinalIgnoreCase)
                                                                   || header.Contains("secret", StringComparison.OrdinalIgnoreCase)
                                                                   || header.Contains("auth", StringComparison.OrdinalIgnoreCase);

    // ---- Bodies -------------------------------------------------------------------------------------------

    private static string BodyText(RequestBody body) => body.Mode switch
    {
        BodyMode.Json or BodyMode.Xml or BodyMode.Text => body.Content,
        BodyMode.FormUrlEncoded or BodyMode.Multipart => string.Join('&', body.FormFields.Where(f => f.IsActive)
            .Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.IsFile ? "" : f.Value))),
        _ => ""
    };

    private static string ContentTypeOf(RequestBody body) => body.Mode switch
    {
        BodyMode.Json => "application/json",
        BodyMode.Xml => "application/xml",
        BodyMode.Text => "text/plain",
        BodyMode.FormUrlEncoded => "application/x-www-form-urlencoded",
        BodyMode.Multipart => "multipart/form-data",
        BodyMode.Binary => "application/octet-stream",
        _ => ""
    };

    private static string GuessContentType(string body)
    {
        var t = body.TrimStart();
        return t.Length == 0 ? "" : t.StartsWith('{') || t.StartsWith('[') ? "application/json" : t.StartsWith('<') ? "application/xml" : "text/plain";
    }

    /// <summary>The media type without parameters, guessed from the body when the header is missing.</summary>
    private static string MediaType(string contentType, string body)
    {
        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return type.Length > 0 ? type : GuessContentType(body);
    }

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK", 201 => "Created", 202 => "Accepted", 204 => "No Content", 301 => "Moved Permanently", 302 => "Found", 304 => "Not Modified",
        400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict",
        410 => "Gone", 412 => "Precondition Failed", 415 => "Unsupported Media Type", 422 => "Unprocessable Entity", 429 => "Too Many Requests",
        500 => "Internal Server Error", 502 => "Bad Gateway", 503 => "Service Unavailable", 504 => "Gateway Timeout",
        >= 200 and < 300 => "Success", >= 300 and < 400 => "Redirection", >= 400 and < 500 => "Client error", _ => "Server error"
    };

    // ---- YAML ---------------------------------------------------------------------------------------------

    private static void WriteYaml(StringBuilder sb, JsonNode? node, int indent, bool inSequence)
    {
        var pad = new string(' ', indent);
        switch (node)
        {
            case JsonObject { Count: 0 }:
                sb.Append("{}").Append('\n');
                break;
            case JsonArray { Count: 0 }:
                sb.Append("[]").Append('\n');
                break;
            case JsonObject obj:
            {
                var firstLine = inSequence; // "- key: value" keeps the first key on the dash line
                foreach (var (key, value) in obj)
                {
                    if (!firstLine)
                        sb.Append(pad);
                    firstLine = false;
                    sb.Append(YamlScalar(key)).Append(':');
                    WriteYamlValue(sb, value, indent);
                }
                break;
            }
            case JsonArray array:
            {
                var firstLine = inSequence;
                foreach (var item in array)
                {
                    if (!firstLine)
                        sb.Append(pad);
                    firstLine = false;
                    sb.Append("- ");
                    if (item is JsonObject { Count: > 0 } or JsonArray { Count: > 0 })
                        WriteYaml(sb, item, indent + 2, true);
                    else
                        WriteYaml(sb, item, indent + 2, false);
                }
                break;
            }
            default:
                sb.Append(YamlScalar(node)).Append('\n');
                break;
        }
    }

    private static void WriteYamlValue(StringBuilder sb, JsonNode? value, int indent)
    {
        switch (value)
        {
            case JsonObject { Count: > 0 }:
                sb.Append('\n');
                WriteYaml(sb, value, indent + 2, false);
                break;
            case JsonArray { Count: > 0 }:
                sb.Append('\n');
                WriteYaml(sb, value, indent, false);
                break;
            default:
                sb.Append(' ');
                WriteYaml(sb, value, indent, false);
                break;
        }
    }

    private static string YamlScalar(JsonNode? node)
    {
        if (node is null)
            return "null";
        if (node is JsonValue value)
        {
            switch (value.GetValueKind())
            {
                case JsonValueKind.String:
                    return YamlScalar(value.GetValue<string>());
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                case JsonValueKind.Null:
                    return "null";
                default:
                    return value.ToJsonString();
            }
        }
        return node.ToJsonString();
    }

    /// <summary>Plain when YAML reads it back as the same string; otherwise double-quoted (JSON escaping is valid YAML).</summary>
    internal static string YamlScalar(string text)
    {
        if (NeedsQuotes(text))
            return JsonSerializer.Serialize(text, Pretty);
        return text;
    }

    private static bool NeedsQuotes(string s)
    {
        if (s.Length == 0 || s.Length > 200 || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]))
            return true;
        if (s[0] is '-' or '?' or ':' or ',' or '[' or ']' or '{' or '}' or '#' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`')
            return true;
        if (s[^1] == ':' || s.Contains(": ", StringComparison.Ordinal) || s.Contains(" #", StringComparison.Ordinal) || s.Contains('\t'))
            return true;
        if (s.Any(c => c < ' ' || c == (char)0x7f || c == (char)0x2028 || c == (char)0x2029 || c == (char)0xFEFF))
            return true;
        if (ReservedWordRegex().IsMatch(s) || NumberLikeRegex().IsMatch(s) || TimestampLikeRegex().IsMatch(s))
            return true;
        return false;
    }

    [GeneratedRegex(@"^(true|false|null|yes|no|on|off|y|n|~)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedWordRegex();

    [GeneratedRegex(@"^[-+]?(\.?\d[\d_]*(\.\d*)?([eE][-+]?\d+)?|0x[0-9a-fA-F_]+|0o[0-7_]+|0b[01_]+|\.inf|\.nan|\d+(:\d+)+(\.\d*)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberLikeRegex();

    [GeneratedRegex(@"^\d{4}-\d{1,2}-\d{1,2}")]
    private static partial Regex TimestampLikeRegex();

    [GeneratedRegex(@"\.(png|jpe?g|gif|svg|ico|css|js|woff2?|ttf|map|webp)(\?|$)", RegexOptions.IgnoreCase)]
    private static partial Regex StaticAssetRegex();

    [GeneratedRegex(@"^v\d+(\.\d+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^(x-)?(api[-_]?key|api[-_]?token|access[-_]?token|auth[-_]?token|x-auth)$", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyHeaderRegex();
}

/// <summary>
/// Merges JSON samples into one JSON Schema (OpenAPI 3.0 flavour): object properties with <c>required</c> for the ones
/// present in every sample, array items, integer vs number, <c>nullable</c>, string formats (uuid, date-time, date,
/// email, uri) and enumerations for strings that take few repeated values.
/// </summary>
public static class SchemaInferrer
{
    private const int MaxDepth = 24;

    /// <summary>Schema that every sample conforms to. <paramref name="scalarsOnly"/> is for parameter values (strings in the URL).</summary>
    public static JsonObject Infer(IReadOnlyList<JsonNode?> samples, bool scalarsOnly = false) => Infer(samples, scalarsOnly, 0);

    private static JsonObject Infer(IReadOnlyList<JsonNode?> samples, bool scalarsOnly, int depth)
    {
        if (depth > MaxDepth || samples.Count == 0)
            return new JsonObject();
        var nullable = samples.Any(s => s is null || s is JsonValue v && v.GetValueKind() == JsonValueKind.Null);
        var present = samples.Where(s => s is not null && !(s is JsonValue v && v.GetValueKind() == JsonValueKind.Null)).ToList();
        if (present.Count == 0)
            return new JsonObject { ["nullable"] = true };

        // Integers also match "number", and the validator wants exactly one oneOf branch: fold both into number first.
        var byType = present.GroupBy(n => TypeOf(n) == "integer" && present.Any(p => TypeOf(p) == "number") ? "number" : TypeOf(n)).ToList();
        JsonObject schema;
        if (byType.Count == 1)
            schema = ForType(byType[0].Key, byType[0].ToList(), scalarsOnly, depth);
        else
        {
            // Strings that are sometimes numbers (or vice versa) in URL parameters are just strings.
            if (scalarsOnly)
                schema = new JsonObject { ["type"] = "string" };
            else
                schema = new JsonObject { ["oneOf"] = new JsonArray(byType.Select(g => (JsonNode)ForType(g.Key, g.ToList(), scalarsOnly, depth)).ToArray()) };
        }
        if (nullable)
            schema["nullable"] = true;
        return schema;
    }

    private static string TypeOf(JsonNode? node) => node switch
    {
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => IsInteger(v) ? "integer" : "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "null"
        },
        _ => "null"
    };

    private static bool IsInteger(JsonValue v) => v.TryGetValue<long>(out _) || v.TryGetValue<decimal>(out var d) && d == Math.Truncate(d) && Math.Abs(d) < long.MaxValue;

    private static JsonObject ForType(string type, List<JsonNode?> samples, bool scalarsOnly, int depth)
    {
        switch (type)
        {
            case "object":
            {
                var objects = samples.Cast<JsonObject>().ToList();
                var properties = new JsonObject();
                var required = new JsonArray();
                foreach (var name in objects.SelectMany(o => o.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
                {
                    var values = objects.Where(o => o.ContainsKey(name)).Select(o => o[name]).ToList();
                    properties[name] = Infer(values, false, depth + 1);
                    if (values.Count == objects.Count)
                        required.Add(name);
                }
                var schema = new JsonObject { ["type"] = "object" };
                if (properties.Count > 0)
                    schema["properties"] = properties;
                if (required.Count > 0 && required.Count < 64)
                    schema["required"] = required;
                return schema;
            }
            case "array":
            {
                var items = samples.Cast<JsonArray>().SelectMany(a => a).ToList();
                return new JsonObject { ["type"] = "array", ["items"] = items.Count == 0 ? new JsonObject() : Infer(items, false, depth + 1) };
            }
            case "integer":
                return new JsonObject { ["type"] = "integer" };
            case "number":
                return new JsonObject { ["type"] = "number" };
            case "boolean":
                return new JsonObject { ["type"] = "boolean" };
            default:
            {
                var strings = samples.Select(s => s!.GetValue<string>()).ToList();
                var schema = new JsonObject { ["type"] = "string" };
                if (scalarsOnly)
                {
                    // Parameter values arrive as text: recognise numbers and booleans.
                    if (strings.All(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                        return new JsonObject { ["type"] = "integer" };
                    if (strings.All(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                        return new JsonObject { ["type"] = "number" };
                    if (strings.All(s => s is "true" or "false"))
                        return new JsonObject { ["type"] = "boolean" };
                }
                if (Format(strings) is { } format)
                    schema["format"] = format;
                else if (Enumeration(strings) is { } values)
                    schema["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
                return schema;
            }
        }
    }

    /// <summary>The OpenAPI format every non-empty string has (using the law miner's recognisers), or null.</summary>
    internal static string? Format(IReadOnlyList<string> strings)
    {
        var nonEmpty = strings.Where(s => s.Length > 0).ToList();
        if (nonEmpty.Count == 0)
            return null;
        foreach (var rule in LawMiner.Formats)
            if (nonEmpty.All(s => rule.Regex.IsMatch(s)))
                return rule.Name switch
                {
                    "UUID" => "uuid",
                    "date-time" => "date-time",
                    "date" => "date",
                    "email address" => "email",
                    "URL" => "uri",
                    _ => null
                };
        return null;
    }

    /// <summary>Few short values, each seen more than once across at least four samples: an enumeration.</summary>
    internal static IReadOnlyList<string>? Enumeration(IReadOnlyList<string> strings)
    {
        if (strings.Count < 4)
            return null;
        var distinct = strings.GroupBy(s => s, StringComparer.Ordinal).ToList();
        if (distinct.Count > 10 || distinct.Count == strings.Count || distinct.Any(g => g.Count() < 2))
            return null;
        if (distinct.Any(g => g.Key.Length == 0 || g.Key.Length > 32 || g.Key.Any(char.IsWhiteSpace) || g.Key.All(char.IsDigit)))
            return null;
        return distinct.Select(g => g.Key).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}
