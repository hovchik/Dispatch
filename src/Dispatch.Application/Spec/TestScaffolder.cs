using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Interop;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Spec;

public sealed class ScaffoldOptions
{
    /// <summary>Add a JSON-schema assertion for the documented success response body.</summary>
    public bool SchemaAssertions { get; init; } = true;

    /// <summary>Create one request per required parameter / body field that leaves it out and expects a 4xx.</summary>
    public bool Negatives { get; init; } = true;

    /// <summary>Folder (under the operation's folder) that holds the negative requests.</summary>
    public string NegativeFolder { get; init; } = "Negative tests";
}

public sealed class ScaffoldReport
{
    public required RequestCollection Collection { get; init; }

    /// <summary>Operations in the spec that got a happy-path request.</summary>
    public int Operations { get; init; }
    public int SchemaAssertions { get; init; }
    public int NegativeRequests { get; init; }
    public List<string> Notes { get; } = [];

    public string Summary =>
        $"{Operations} happy-path request(s) with status assertions, {SchemaAssertions} response schema assertion(s), {NegativeRequests} negative request(s).";
}

/// <summary>
/// Turns an OpenAPI / Swagger document into a test collection: the happy-path request of every operation (via the
/// OpenAPI importer) with a status assertion and a JSON-schema assertion on the response body, plus one negative request
/// per required parameter or required body field that omits it and expects a 4xx.
/// </summary>
public static class TestScaffolder
{
    private static readonly string[] Methods = ["get", "post", "put", "patch", "delete", "head", "options"];
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Imports the spec and adds the tests to the resulting collection.</summary>
    public static ScaffoldReport Scaffold(JsonNode spec, string? specLocation = null, ScaffoldOptions? options = null)
    {
        if (!OpenApi.IsOpenApi(spec))
            throw new FormatException("Not an OpenAPI or Swagger document (no 'openapi' or 'swagger' field).");
        var imported = OpenApi.Import(spec, specLocation);
        var collection = imported.Collections[0];
        collection.Name += " tests";
        var report = Apply(spec, collection, options);
        report.Notes.AddRange(imported.Warnings);
        return report;
    }

    /// <summary>
    /// Adds the tests to a collection that was imported from <paramref name="spec"/> (requests are matched to operations by
    /// method and templated URL). Requests that already have the assertion or negative are left alone, so this is safe to
    /// run twice.
    /// </summary>
    public static ScaffoldReport Apply(JsonNode spec, RequestCollection collection, ScaffoldOptions? options = null)
    {
        options ??= new ScaffoldOptions();
        var swagger2 = spec["swagger"] is not null;
        var operations = 0;
        var schemas = 0;
        var negatives = new List<ApiRequest>();
        var notes = new List<string>();

        foreach (var (path, pathItem) in spec["paths"] as JsonObject ?? [])
        {
            if (pathItem is not JsonObject item)
                continue;
            foreach (var method in Methods)
            {
                if (item[method] is not JsonObject operation)
                    continue;
                var request = FindRequest(collection, method, path);
                if (request is null)
                {
                    notes.Add($"No request for {method.ToUpperInvariant()} {path}; skipped.");
                    continue;
                }
                operations++;

                if (!request.Assertions.Any(a => a.Source == ValueSource.Status))
                    request.Assertions.Add(new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.LessThan, Expected = "400" });

                if (options.SchemaAssertions && !request.Assertions.Any(a => a.Source == ValueSource.JsonSchema)
                                             && SuccessSchema(spec, operation, swagger2) is { } schema)
                {
                    request.Assertions.Add(new Assertion
                    {
                        Source = ValueSource.JsonSchema,
                        Operator = AssertionOperator.IsValid,
                        Path = schema.ToJsonString(Pretty)
                    });
                    schemas++;
                }

                if (!options.Negatives)
                    continue;
                var parameters = Parameters(spec, item["parameters"] as JsonArray, operation["parameters"] as JsonArray);
                foreach (var parameter in parameters.Where(p => p["required"]?.GetValue<bool>() == true))
                {
                    var name = parameter["name"]?.ToString() ?? "";
                    var location = parameter["in"]?.ToString();
                    var negative = location switch
                    {
                        "query" => Negative(request, $"missing query parameter {name}", options, r => RemoveQueryParameter(r, name)),
                        "header" => Negative(request, $"missing header {name}", options, r => Remove(r.Headers, name)),
                        _ => null
                    };
                    if (negative is not null && IsNew(collection, negatives, negative))
                        negatives.Add(negative);
                }
                foreach (var field in RequiredBodyFields(spec, operation, parameters, swagger2))
                {
                    var negative = request.Body.Mode switch
                    {
                        BodyMode.Json => Negative(request, $"missing body field {field}", options, r => RemoveBodyField(r, field)),
                        BodyMode.FormUrlEncoded or BodyMode.Multipart => Negative(request, $"missing form field {field}", options, r => Remove(r.Body.FormFields, field)),
                        _ => null
                    };
                    if (negative is not null && IsNew(collection, negatives, negative))
                        negatives.Add(negative);
                }
            }
        }

        // Negatives go right after the request they derive from.
        var ordered = collection.Requests.OrderBy(r => r.SortOrder).ToList();
        var result = new List<ApiRequest>();
        foreach (var request in ordered)
        {
            result.Add(request);
            result.AddRange(negatives.Where(n => n.Description.StartsWith(OriginMarker(request), StringComparison.Ordinal)));
        }
        for (var i = 0; i < result.Count; i++)
            result[i].SortOrder = i;
        collection.Requests = result;

        var report = new ScaffoldReport { Collection = collection, Operations = operations, SchemaAssertions = schemas, NegativeRequests = negatives.Count };
        report.Notes.AddRange(notes);
        return report;
    }

    /// <summary>The imported request for an operation: same method, URL <c>{{baseUrl}}/path</c> with <c>{x}</c> as <c>{{x}}</c>.</summary>
    internal static ApiRequest? FindRequest(RequestCollection collection, string method, string path)
    {
        var expected = "{{baseUrl}}" + path.Replace("{", "{{").Replace("}", "}}");
        return collection.Requests.FirstOrDefault(r => r.Kind == RequestKind.Http
                                                       && r.Method.ToString().Equals(method, StringComparison.OrdinalIgnoreCase)
                                                       && QueryString.Split(r.Url).BaseUrl == expected);
    }

    private static ApiRequest? Negative(ApiRequest request, string what, ScaffoldOptions options, Func<ApiRequest, bool> mutate)
    {
        var negative = request.Clone(newIdentity: true);
        if (!mutate(negative))
            return null;
        negative.CollectionId = request.CollectionId;
        negative.Name = $"{request.Name} — {what}";
        negative.Description = $"{OriginMarker(request)}Expects a 4xx because of the {what}.";
        negative.Folder = request.Folder.Length > 0 ? request.Folder + "/" + options.NegativeFolder : options.NegativeFolder;
        negative.Assertions =
        [
            new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.GreaterOrEqual, Expected = "400" },
            new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.LessThan, Expected = "500" }
        ];
        negative.Extractions = [];
        negative.Expectations = [];
        negative.Examples = [];
        negative.TestScript = string.Empty;
        return negative;
    }

    /// <summary>A negative with this name is not created twice (so Apply can run on an already scaffolded collection).</summary>
    private static bool IsNew(RequestCollection collection, List<ApiRequest> pending, ApiRequest negative) =>
        !collection.Requests.Concat(pending).Any(r => r.Name == negative.Name);

    private static string OriginMarker(ApiRequest request) => $"Negative test for \"{request.Name}\". ";

    private static bool Remove(List<KeyValueItem> items, string name) =>
        items.RemoveAll(i => i.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Drops the parameter from the Params table and the URL (both hold it after an OpenAPI import).</summary>
    private static bool RemoveQueryParameter(ApiRequest request, string name)
    {
        var fromTable = Remove(request.QueryParams, name);
        var inUrl = QueryString.Parse(request.Url);
        var remaining = inUrl.Where(q => !q.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (remaining.Count == inUrl.Count && !fromTable)
            return false;
        request.Url = QueryString.WithParams(request.Url, remaining);
        return true;
    }

    private static bool RemoveBodyField(ApiRequest request, string field)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(request.Body.Content);
        }
        catch (JsonException)
        {
            return false;
        }
        if (body is not JsonObject obj || !obj.ContainsKey(field))
            return false;
        obj.Remove(field);
        request.Body.Content = obj.ToJsonString(Pretty);
        return true;
    }

    // ---- Spec reading ----------------------------------------------------------------------------------------

    private static List<JsonNode> Parameters(JsonNode spec, JsonArray? pathLevel, JsonArray? operationLevel) =>
        (pathLevel ?? []).Concat(operationLevel ?? []).Select(p => OpenApi.Resolve(spec, p)).OfType<JsonNode>()
        .GroupBy(p => (p["name"]?.ToString(), p["in"]?.ToString())).Select(g => g.Last()).ToList();

    /// <summary>The JSON schema of the first 2xx response (refs inlined), or null when the response has no JSON body.</summary>
    internal static JsonNode? SuccessSchema(JsonNode spec, JsonObject operation, bool swagger2)
    {
        var responses = operation["responses"] as JsonObject;
        var success = responses?.FirstOrDefault(r => r.Key.StartsWith('2') && r.Value is not null);
        if (success is not { Value: not null } response)
            return null;
        var resolved = OpenApi.Resolve(spec, response.Value);
        JsonNode? schema;
        if (swagger2)
            schema = resolved?["schema"];
        else
        {
            if (resolved?["content"] is not JsonObject content || content.Count == 0)
                return null;
            var media = content.FirstOrDefault(c => c.Key.Contains("json", StringComparison.OrdinalIgnoreCase));
            if (media.Value is null)
                return null;
            schema = media.Value["schema"];
        }
        return schema is null ? null : Mock.SchemaFaker.Inline(spec, schema);
    }

    /// <summary>Names the request body schema marks as required (top level, following $ref and allOf).</summary>
    internal static IReadOnlyList<string> RequiredBodyFields(JsonNode spec, JsonObject operation, List<JsonNode> parameters, bool swagger2)
    {
        JsonNode? schema = null;
        if (swagger2)
        {
            var body = parameters.FirstOrDefault(p => p["in"]?.ToString() == "body");
            if (body is not null)
                schema = body["schema"];
            else
                return parameters.Where(p => p["in"]?.ToString() == "formData" && p["required"]?.GetValue<bool>() == true)
                    .Select(p => p["name"]?.ToString() ?? "").Where(n => n.Length > 0).ToList();
        }
        else if (OpenApi.Resolve(spec, operation["requestBody"]) is { } requestBody && requestBody["content"] is JsonObject content && content.Count > 0)
        {
            var media = content.FirstOrDefault(c => c.Key.Contains("json", StringComparison.OrdinalIgnoreCase)) is { Value: not null } json ? json : content.First();
            schema = media.Value?["schema"];
        }
        var required = new List<string>();
        Collect(spec, schema, required, 0);
        return required.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void Collect(JsonNode spec, JsonNode? schema, List<string> required, int depth)
    {
        schema = OpenApi.Resolve(spec, schema);
        if (schema is null || depth > 8)
            return;
        foreach (var name in schema["required"] as JsonArray ?? [])
            if (name?.ToString() is { Length: > 0 } n)
                required.Add(n);
        foreach (var part in schema["allOf"] as JsonArray ?? [])
            Collect(spec, part, required, depth + 1);
    }
}
