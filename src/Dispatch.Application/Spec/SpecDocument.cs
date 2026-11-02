using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Interop;
using Dispatch.Application.Laws;

namespace Dispatch.Application.Spec;

/// <summary>A parameter of an operation (path, query, header or cookie).</summary>
public sealed record SpecParameter(string Name, string In, bool Required, JsonNode? Schema)
{
    public string Type => SchemaShape.TypeOf(Schema);
    public IReadOnlyList<string> Enum => SchemaShape.EnumOf(Schema);
    public string Key => $"{In}:{Name}";
}

/// <summary>One documented response of an operation. <see cref="Code"/> is "200", "2XX" or "default".</summary>
public sealed record SpecResponse(string Code, JsonNode? Schema, IReadOnlyList<string> MediaTypes, IReadOnlyList<string> RequiredHeaders)
{
    /// <summary>Whether an actual status code is described by this entry.</summary>
    public bool Matches(int status)
    {
        if (Code.Equals("default", StringComparison.OrdinalIgnoreCase))
            return false;
        if (int.TryParse(Code, out var exact))
            return exact == status;
        return Code.Length == 3 && Code[1..].Equals("XX", StringComparison.OrdinalIgnoreCase) && Code[0] == (char)('0' + status / 100);
    }
}

/// <summary>One operation (method + path template) of an OpenAPI 3 or Swagger 2 document, with <c>$ref</c>s resolved.</summary>
public sealed class SpecOperation
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string? OperationId { get; init; }
    public string? Summary { get; init; }
    public bool Deprecated { get; init; }
    public IReadOnlyList<SpecParameter> Parameters { get; init; } = [];
    public JsonNode? RequestSchema { get; init; }
    public bool RequestBodyRequired { get; init; }
    public IReadOnlyList<string> RequestMediaTypes { get; init; } = [];
    public IReadOnlyList<SpecResponse> Responses { get; init; } = [];

    /// <summary>Names of the security schemes the operation accepts (empty = anonymous).</summary>
    public IReadOnlyList<string> Security { get; init; } = [];

    /// <summary><c>GET /users/{id}</c>.</summary>
    public string Key => $"{Method} {Path}";

    /// <summary>Documented responses that stand for a concrete status or class (not "default").</summary>
    public IEnumerable<SpecResponse> ConcreteResponses => Responses.Where(r => !r.Code.Equals("default", StringComparison.OrdinalIgnoreCase));

    /// <summary>The schema of the first 2xx response (or the default one), where clients read their data from.</summary>
    public SpecResponse? SuccessResponse =>
        Responses.FirstOrDefault(r => r.Code.StartsWith('2')) ?? Responses.FirstOrDefault(r => r.Code.Equals("default", StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Key;
}

/// <summary>
/// A normalised OpenAPI 3.x / Swagger 2.0 document: its operations with parameters, request and response schemas, plus the
/// path matcher used to pair real traffic with operations. JSON only; YAML is converted by the caller (Infrastructure).
/// </summary>
public sealed class SpecDocument
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    public required JsonNode Root { get; init; }
    public string Title { get; init; } = "API";
    public string Version { get; init; } = "";
    public bool IsSwagger2 { get; init; }
    public IReadOnlyList<string> Servers { get; init; } = [];
    public IReadOnlyList<SpecOperation> Operations { get; init; } = [];

    /// <summary>Base paths of the servers (e.g. "/v1"), tried when matching request paths.</summary>
    public IReadOnlyList<string> BasePaths { get; init; } = [""];

    /// <summary>Security scheme name → type description, e.g. "apiKey header X-Key" or "http bearer".</summary>
    public IReadOnlyDictionary<string, string> SecuritySchemes { get; init; } = new Dictionary<string, string>();

    public static bool IsSpec(JsonNode? root) => root is JsonObject && OpenApi.IsOpenApi(root);

    public static SpecDocument Parse(JsonNode root)
    {
        if (!IsSpec(root))
            throw new FormatException("Not an OpenAPI 3.x or Swagger 2.0 document (no 'openapi' or 'swagger' field).");
        var swagger2 = root["swagger"] is not null;
        var globalSecurity = SecurityNames(root["security"]);
        var operations = new List<SpecOperation>();

        foreach (var (path, pathItemNode) in root["paths"] as JsonObject ?? [])
        {
            if (OpenApi.Resolve(root, pathItemNode) is not JsonObject item)
                continue;
            var pathParameters = Parameters(root, item["parameters"] as JsonArray, swagger2);
            foreach (var method in Methods)
            {
                if (item[method] is not JsonObject op)
                    continue;
                var parameters = pathParameters.Concat(Parameters(root, op["parameters"] as JsonArray, swagger2))
                    .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();

                JsonNode? requestSchema = null;
                var bodyRequired = false;
                var mediaTypes = new List<string>();
                if (swagger2)
                {
                    var body = (op["parameters"] as JsonArray ?? []).Select(p => OpenApi.Resolve(root, p)).FirstOrDefault(p => p?["in"]?.ToString() == "body");
                    if (body is not null)
                    {
                        requestSchema = OpenApi.Resolve(root, body["schema"]);
                        bodyRequired = body["required"]?.GetValue<bool>() == true;
                        mediaTypes.AddRange((op["consumes"] as JsonArray ?? root["consumes"] as JsonArray ?? []).Select(c => c?.ToString() ?? ""));
                    }
                    var form = parameters.Where(p => p.In == "formData").ToList();
                    if (form.Count > 0 && requestSchema is null)
                    {
                        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray() };
                        foreach (var f in form)
                        {
                            schema["properties"]![f.Name] = f.Schema?.DeepClone() ?? new JsonObject { ["type"] = "string" };
                            if (f.Required)
                                schema["required"]!.AsArray().Add(f.Name);
                        }
                        requestSchema = schema;
                        parameters = parameters.Where(p => p.In != "formData").ToList();
                    }
                }
                else if (OpenApi.Resolve(root, op["requestBody"]) is { } requestBody)
                {
                    bodyRequired = requestBody["required"]?.GetValue<bool>() == true;
                    var content = requestBody["content"] as JsonObject ?? [];
                    mediaTypes.AddRange(content.Select(c => c.Key));
                    var media = content.FirstOrDefault(c => c.Key.Contains("json", StringComparison.OrdinalIgnoreCase)).Value ?? content.FirstOrDefault().Value;
                    requestSchema = OpenApi.Resolve(root, media?["schema"]);
                }

                var responses = new List<SpecResponse>();
                foreach (var (code, responseNode) in op["responses"] as JsonObject ?? [])
                {
                    var response = OpenApi.Resolve(root, responseNode);
                    var headers = (response?["headers"] as JsonObject ?? [])
                        .Where(h => OpenApi.Resolve(root, h.Value)?["required"]?.GetValue<bool>() == true).Select(h => h.Key).ToList();
                    JsonNode? schema;
                    List<string> types;
                    if (swagger2)
                    {
                        schema = OpenApi.Resolve(root, response?["schema"]);
                        types = (op["produces"] as JsonArray ?? root["produces"] as JsonArray ?? []).Select(c => c?.ToString() ?? "").ToList();
                    }
                    else
                    {
                        var content = response?["content"] as JsonObject ?? [];
                        types = content.Select(c => c.Key).ToList();
                        var media = content.FirstOrDefault(c => c.Key.Contains("json", StringComparison.OrdinalIgnoreCase)).Value ?? content.FirstOrDefault().Value;
                        schema = OpenApi.Resolve(root, media?["schema"]);
                    }
                    responses.Add(new SpecResponse(code, schema, types, headers));
                }

                operations.Add(new SpecOperation
                {
                    Method = method.ToUpperInvariant(),
                    Path = NormalizePath(path),
                    OperationId = op["operationId"]?.ToString(),
                    Summary = op["summary"]?.ToString(),
                    Deprecated = op["deprecated"]?.GetValue<bool>() == true,
                    Parameters = parameters,
                    RequestSchema = requestSchema,
                    RequestBodyRequired = bodyRequired,
                    RequestMediaTypes = mediaTypes,
                    Responses = responses,
                    Security = op["security"] is { } s ? SecurityNames(s) : globalSecurity
                });
            }
        }

        var servers = swagger2
            ? [SwaggerServer(root)]
            : (root["servers"] as JsonArray ?? []).Select(s => s?["url"]?.ToString() ?? "").Where(u => u.Length > 0).ToList();
        var basePaths = servers.Select(ServerPath).Distinct().DefaultIfEmpty("").ToList();
        if (!basePaths.Contains(""))
            basePaths.Add(""); // traffic captured behind a gateway may not include the documented prefix

        var schemes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, scheme) in (swagger2 ? root["securityDefinitions"] : root["components"]?["securitySchemes"]) as JsonObject ?? [])
        {
            var resolved = OpenApi.Resolve(root, scheme);
            schemes[name] = string.Join(' ', new[] { resolved?["type"]?.ToString(), resolved?["scheme"]?.ToString(), resolved?["in"]?.ToString(), resolved?["name"]?.ToString() }
                .Where(p => !string.IsNullOrEmpty(p)));
        }

        return new SpecDocument
        {
            Root = root,
            Title = root["info"]?["title"]?.ToString() ?? "API",
            Version = root["info"]?["version"]?.ToString() ?? "",
            IsSwagger2 = swagger2,
            Servers = servers,
            BasePaths = basePaths,
            Operations = operations,
            SecuritySchemes = schemes
        };
    }

    public SpecOperation? Find(string method, string pathTemplate) =>
        Operations.FirstOrDefault(o => o.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && o.Path == NormalizePath(pathTemplate));

    /// <summary>
    /// The operation a real request hits, preferring templates with fewer parameters (<c>/users/me</c> before
    /// <c>/users/{id}</c>). <paramref name="method"/> may be null when unknown (then any method on the path matches, GET first).
    /// Returns the matching path template too, so "documented path, wrong method" can be told from "undocumented".
    /// </summary>
    public (SpecOperation? Operation, string? PathTemplate) Match(string? method, string requestPath)
    {
        var path = NormalizePath(Observations.PathOf(requestPath));
        string? templateMatch = null;
        foreach (var basePath in BasePaths)
        {
            if (basePath.Length > 0)
            {
                if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (path.Length > basePath.Length && path[basePath.Length] != '/')
                    continue;
            }
            var relative = basePath.Length > 0 ? NormalizePath(path[basePath.Length..]) : path;
            var candidates = Operations
                .Where(o => PathMatches(o.Path, relative))
                .OrderBy(o => o.Path.Count(c => c == '{'))
                .ToList();
            if (candidates.Count == 0)
                continue;
            templateMatch ??= candidates[0].Path;
            var byMethod = method is null
                ? candidates.OrderBy(o => o.Method == "GET" ? 0 : 1).FirstOrDefault()
                : candidates.FirstOrDefault(o => o.Method.Equals(method, StringComparison.OrdinalIgnoreCase));
            if (byMethod is not null)
                return (byMethod, byMethod.Path);
        }
        return (null, templateMatch);
    }

    /// <summary>Values of the path parameters of <paramref name="template"/> in <paramref name="requestPath"/> (after base path removal).</summary>
    public IReadOnlyDictionary<string, string> PathValues(SpecOperation operation, string requestPath)
    {
        var path = NormalizePath(Observations.PathOf(requestPath));
        foreach (var basePath in BasePaths.OrderByDescending(b => b.Length))
        {
            var relative = basePath.Length > 0 && path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ? NormalizePath(path[basePath.Length..]) : path;
            if (!PathMatches(operation.Path, relative))
                continue;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var t = operation.Path.Split('/');
            var p = relative.Split('/');
            for (var i = 0; i < t.Length && i < p.Length; i++)
                if (t[i].StartsWith('{') && t[i].EndsWith('}'))
                    values[t[i][1..^1]] = Uri.UnescapeDataString(p[i]);
            return values;
        }
        return new Dictionary<string, string>();
    }

    /// <summary>Segment-wise match: <c>{param}</c> in the template, or an unresolved <c>{{variable}}</c> in the request, match anything.</summary>
    internal static bool PathMatches(string template, string path)
    {
        var t = template.Split('/');
        var p = path.Split('/');
        if (t.Length != p.Length)
            return false;
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i].Contains('{') && t[i].Contains('}'))
            {
                // Partial templates like "file.{ext}" or "{a}-{b}".
                var pattern = "^" + Regex.Replace(Regex.Escape(t[i]), @"\\\{[^}]+}", "[^/]+") + "$";
                if (!Regex.IsMatch(p[i], pattern, RegexOptions.IgnoreCase))
                    return false;
                continue;
            }
            if (p[i].StartsWith("{{", StringComparison.Ordinal) && p[i].EndsWith("}}", StringComparison.Ordinal))
                continue;
            if (!string.Equals(t[i], Uri.UnescapeDataString(p[i]), StringComparison.OrdinalIgnoreCase) && !string.Equals(t[i], p[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>Leading slash, no trailing slash (except the root), no empty segments.</summary>
    public static string NormalizePath(string path)
    {
        var p = path.Trim();
        var query = p.IndexOfAny(['?', '#']);
        if (query >= 0)
            p = p[..query];
        if (!p.StartsWith('/'))
            p = "/" + p;
        p = Regex.Replace(p, "/{2,}", "/");
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    private static List<SpecParameter> Parameters(JsonNode root, JsonArray? array, bool swagger2)
    {
        var list = new List<SpecParameter>();
        foreach (var node in array ?? [])
        {
            if (OpenApi.Resolve(root, node) is not JsonObject p)
                continue;
            var location = p["in"]?.ToString() ?? "query";
            if (location == "body")
                continue;
            var schema = swagger2 ? p : OpenApi.Resolve(root, p["schema"]);
            list.Add(new SpecParameter(p["name"]?.ToString() ?? "", location, location == "path" || p["required"]?.GetValue<bool>() == true, schema));
        }
        return list;
    }

    private static IReadOnlyList<string> SecurityNames(JsonNode? security) =>
        (security as JsonArray ?? []).OfType<JsonObject>().SelectMany(o => o.Select(kv => kv.Key)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static string SwaggerServer(JsonNode root)
    {
        var scheme = (root["schemes"] as JsonArray)?.FirstOrDefault()?.ToString() ?? "https";
        var host = root["host"]?.ToString() ?? "localhost";
        return $"{scheme}://{host}{root["basePath"]?.ToString() ?? ""}".TrimEnd('/');
    }

    private static string ServerPath(string serverUrl)
    {
        var url = Regex.Replace(serverUrl, @"\{[^}]+}", "x");
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.AbsolutePath.TrimEnd('/');
        return url.StartsWith('/') ? url.TrimEnd('/') : "";
    }
}

/// <summary>Where a JSON field of a schema lives and what it allows; one per flattened path (array items are <c>[*]</c>).</summary>
public sealed record SchemaField(string Path, IReadOnlySet<string> Types, bool Required, bool Nullable, IReadOnlyList<string> Enum,
    string? Format, bool ReadOnly, bool WriteOnly)
{
    public string TypeText => Types.Count == 0 ? "any" : string.Join("|", Types.OrderBy(t => t, StringComparer.Ordinal));
}

/// <summary>Flattens JSON schemas (with <c>$ref</c>, <c>allOf</c>, <c>oneOf</c>/<c>anyOf</c>) into path → field maps for diffing and coverage.</summary>
public static class SchemaShape
{
    private const int MaxDepth = 12;

    public static string TypeOf(JsonNode? schema)
    {
        if (schema is null)
            return "any";
        var types = TypesOf(schema);
        return types.Count == 0 ? "any" : string.Join("|", types.OrderBy(t => t, StringComparer.Ordinal));
    }

    public static IReadOnlyList<string> EnumOf(JsonNode? schema) =>
        (schema?["enum"] as JsonArray ?? []).Select(e => e?.ToJsonString() ?? "null").ToList();

    /// <summary>Top-level required property names of an object schema (through $ref and allOf).</summary>
    public static IReadOnlyList<string> RequiredOf(JsonNode root, JsonNode? schema)
    {
        var fields = Flatten(root, schema);
        return fields.Values.Where(f => f.Required && !f.Path.Contains("[*]") && f.Path.Count(c => c == '.') == 1)
            .Select(f => f.Path[2..]).ToList();
    }

    /// <summary>Every path of the schema with its allowed types; <c>$</c> is the root.</summary>
    public static IReadOnlyDictionary<string, SchemaField> Flatten(JsonNode root, JsonNode? schema)
    {
        var fields = new Dictionary<string, SchemaField>(StringComparer.Ordinal);
        if (schema is not null)
            Walk(root, schema, "$", required: false, fields, 0, new HashSet<string>(StringComparer.Ordinal));
        return fields;
    }

    private static void Walk(JsonNode root, JsonNode? node, string path, bool required, Dictionary<string, SchemaField> fields, int depth, HashSet<string> refs)
    {
        if (depth > MaxDepth)
            return;
        // Recursive schemas (a tree node whose children have its own type) stop where the $ref chain repeats.
        var reference = node?["$ref"]?.ToString();
        if (reference is not null && !refs.Add(reference))
            return;
        try
        {
            WalkSchema(root, node, path, required, fields, depth, refs);
        }
        finally
        {
            if (reference is not null)
                refs.Remove(reference);
        }
    }

    private static void WalkSchema(JsonNode root, JsonNode? node, string path, bool required, Dictionary<string, SchemaField> fields, int depth, HashSet<string> refs)
    {
        var schema = OpenApi.Resolve(root, node);
        if (schema is not JsonObject s)
        {
            Add(fields, path, [], required, false, [], null, false, false);
            return;
        }

        if (s["allOf"] is JsonArray allOf && allOf.Count > 0)
        {
            var merged = Merge(root, allOf);
            foreach (var (k, v) in s.Where(kv => kv.Key != "allOf"))
                merged[k] ??= v?.DeepClone();
            Walk(root, merged, path, required, fields, depth + 1, refs);
            return;
        }
        if ((s["oneOf"] ?? s["anyOf"]) is JsonArray variants && variants.Count > 0)
        {
            // Union of the variants: a field is required only when every variant requires it.
            var perVariant = variants.Select(v =>
            {
                var sub = new Dictionary<string, SchemaField>(StringComparer.Ordinal);
                var combined = new JsonObject();
                foreach (var (k, val) in s.Where(kv => kv.Key is not ("oneOf" or "anyOf" or "discriminator")))
                    combined[k] = val?.DeepClone();
                if (OpenApi.Resolve(root, v) is JsonObject vo)
                    foreach (var (k, val) in vo)
                        combined[k] = val?.DeepClone();
                Walk(root, combined, path, required, sub, depth + 1, refs);
                return sub;
            }).ToList();
            foreach (var p in perVariant.SelectMany(d => d.Keys).Distinct())
            {
                var present = perVariant.Where(d => d.ContainsKey(p)).Select(d => d[p]).ToList();
                var union = new SchemaField(p, present.SelectMany(f => f.Types).ToHashSet(StringComparer.Ordinal),
                    present.Count == perVariant.Count && present.All(f => f.Required), present.Any(f => f.Nullable),
                    present.SelectMany(f => f.Enum).Distinct().ToList(), present.Select(f => f.Format).FirstOrDefault(f => f is not null),
                    present.All(f => f.ReadOnly), present.All(f => f.WriteOnly));
                fields[p] = fields.TryGetValue(p, out var existing) ? Combine(existing, union) : union;
            }
            return;
        }

        var types = TypesOf(s);
        var nullable = s["nullable"]?.GetValue<bool>() == true || (s["type"] as JsonArray)?.Any(t => t?.ToString() == "null") == true;
        Add(fields, path, types, required, nullable, EnumOf(s), s["format"]?.ToString(), s["readOnly"]?.GetValue<bool>() == true, s["writeOnly"]?.GetValue<bool>() == true);

        if (types.Contains("object") || s["properties"] is JsonObject)
        {
            var requiredNames = (s["required"] as JsonArray ?? []).Select(r => r?.ToString() ?? "").ToHashSet(StringComparer.Ordinal);
            foreach (var (name, property) in s["properties"] as JsonObject ?? [])
                Walk(root, property, path + "." + Segment(name), requiredNames.Contains(name), fields, depth + 1, refs);
            if (s["additionalProperties"] is JsonObject additional && additional.Count > 0)
                Walk(root, additional, path + ".*", false, fields, depth + 1, refs);
        }
        if (types.Contains("array") || s["items"] is not null)
            Walk(root, s["items"], path + "[*]", false, fields, depth + 1, refs);
    }

    private static string Segment(string name) =>
        Regex.IsMatch(name, @"^[A-Za-z_$][A-Za-z0-9_$-]*$") ? name : $"['{name.Replace("'", "\\'")}']";

    private static void Add(Dictionary<string, SchemaField> fields, string path, HashSet<string> types, bool required, bool nullable,
        IReadOnlyList<string> values, string? format, bool readOnly, bool writeOnly)
    {
        var field = new SchemaField(path, types, required, nullable, values, format, readOnly, writeOnly);
        fields[path] = fields.TryGetValue(path, out var existing) ? Combine(existing, field) : field;
    }

    private static SchemaField Combine(SchemaField a, SchemaField b) =>
        new(a.Path, a.Types.Union(b.Types).ToHashSet(StringComparer.Ordinal), a.Required || b.Required, a.Nullable || b.Nullable,
            a.Enum.Union(b.Enum).ToList(), a.Format ?? b.Format, a.ReadOnly && b.ReadOnly, a.WriteOnly && b.WriteOnly);

    private static JsonObject Merge(JsonNode root, JsonArray allOf)
    {
        var merged = new JsonObject { ["type"] = "object" };
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var part in allOf)
        {
            var resolved = OpenApi.Resolve(root, part) as JsonObject;
            if (resolved is null)
                continue;
            if (resolved["allOf"] is JsonArray nested)
                resolved = Merge(root, nested);
            foreach (var (k, v) in resolved)
            {
                switch (k)
                {
                    case "properties":
                        foreach (var (name, property) in v as JsonObject ?? [])
                            properties[name] = property?.DeepClone();
                        break;
                    case "required":
                        foreach (var r in v as JsonArray ?? [])
                            if (r is not null && !required.Any(x => x?.ToString() == r.ToString()))
                                required.Add(r.ToString());
                        break;
                    default:
                        merged[k] = v?.DeepClone();
                        break;
                }
            }
        }
        merged["properties"] = properties;
        merged["required"] = required;
        return merged;
    }

    private static HashSet<string> TypesOf(JsonNode schema)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        switch (schema["type"])
        {
            case JsonArray array:
                foreach (var t in array.Select(t => t?.ToString()).Where(t => t is not null && t != "null"))
                    set.Add(t!);
                break;
            case JsonValue value:
                set.Add(value.ToString());
                break;
            default:
                if (schema["properties"] is not null || schema["additionalProperties"] is not null)
                    set.Add("object");
                else if (schema["items"] is not null)
                    set.Add("array");
                else if (schema["enum"] is JsonArray e && e.Count > 0)
                    set.Add(e.All(x => x is JsonValue v && v.TryGetValue<string>(out _)) ? "string" : "any");
                break;
        }
        return set;
    }
}
