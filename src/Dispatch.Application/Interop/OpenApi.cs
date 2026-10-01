using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Interop;

/// <summary>
/// OpenAPI 3.x and Swagger 2.0 (JSON; YAML is converted by the caller). Creates one request per operation, grouped in
/// folders by tag, with <c>{{baseUrl}}</c>, path/query/header parameters as variables or examples, example bodies,
/// auth from security schemes, a status assertion and a contract assertion against the spec.
/// </summary>
public static class OpenApi
{
    private static readonly string[] Methods = ["get", "post", "put", "patch", "delete", "head", "options"];
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool IsOpenApi(JsonNode root) => root["openapi"] is not null || root["swagger"] is not null;

    public static ImportResult Import(JsonNode root, string? specLocation = null)
    {
        var swagger2 = root["swagger"] is not null;
        var result = new ImportResult { Format = swagger2 ? "Swagger 2.0" : "OpenAPI " + root["openapi"] };
        var collection = new RequestCollection
        {
            Name = root["info"]?["title"]?.ToString() ?? "API",
            Description = root["info"]?["description"]?.ToString() ?? "",
            SpecLocation = specLocation ?? ""
        };

        var baseUrl = swagger2 ? SwaggerBaseUrl(root) : OpenApiBaseUrl(root, collection);
        collection.Variables.Insert(0, new KeyValueItem("baseUrl", baseUrl));

        var securitySchemes = (swagger2 ? root["securityDefinitions"] : root["components"]?["securitySchemes"]) as JsonObject;
        var globalSecurity = root["security"] as JsonArray;
        var order = 0;

        foreach (var (path, pathItem) in root["paths"] as JsonObject ?? [])
        {
            if (pathItem is not JsonObject item)
                continue;
            var pathParameters = item["parameters"] as JsonArray;

            foreach (var method in Methods)
            {
                if (item[method] is not JsonObject operation)
                    continue;

                var request = new ApiRequest
                {
                    Name = operation["summary"]?.ToString() is { Length: > 0 } summary ? summary
                        : operation["operationId"]?.ToString() ?? $"{method.ToUpperInvariant()} {path}",
                    Description = operation["description"]?.ToString() ?? "",
                    Method = Enum.Parse<HttpVerb>(method, ignoreCase: true),
                    Folder = (operation["tags"] as JsonArray)?.FirstOrDefault()?.ToString() ?? "",
                    CollectionId = collection.Id,
                    SortOrder = order++
                };

                var url = "{{baseUrl}}" + path;
                var parameters = Resolve(root, pathParameters).Concat(Resolve(root, operation["parameters"] as JsonArray))
                    .GroupBy(p => (p["name"]?.ToString(), p["in"]?.ToString()))
                    .Select(g => g.Last()); // operation-level parameters override path-level ones

                foreach (var p in parameters)
                {
                    var name = p["name"]?.ToString() ?? "";
                    var location = p["in"]?.ToString();
                    var schema = swagger2 ? p : Resolve(root, p["schema"]);
                    var example = ExampleText(root, p["example"] ?? schema?["example"] ?? FirstExample(p["examples"]), schema);
                    var required = p["required"]?.GetValue<bool>() == true;
                    switch (location)
                    {
                        case "path":
                            // Path parameters become variables so they're easy to change per environment.
                            url = url.Replace("{" + name + "}", "{{" + name + "}}");
                            if (!collection.Variables.Any(v => v.Key == name))
                                collection.Variables.Add(new KeyValueItem(name, example));
                            break;
                        case "query":
                            request.QueryParams.Add(new KeyValueItem(name, example, required));
                            break;
                        case "header":
                            request.Headers.Add(new KeyValueItem(name, example, required));
                            break;
                        case "cookie":
                            request.Headers.Add(new KeyValueItem("Cookie", $"{name}={example}", required));
                            break;
                        case "body" when swagger2:
                            request.Body = new RequestBody { Mode = BodyMode.Json, Content = Json(ExampleNode(root, Resolve(root, p["schema"]), 0)) };
                            break;
                        case "formData" when swagger2:
                            request.Body.Mode = p["type"]?.ToString() == "file" || request.Body.Mode == BodyMode.Multipart
                                ? BodyMode.Multipart
                                : BodyMode.FormUrlEncoded;
                            request.Body.FormFields.Add(new KeyValueItem(name, example) { IsFile = p["type"]?.ToString() == "file" });
                            break;
                    }
                }
                request.Url = QueryString.WithParams(url, request.QueryParams);

                if (!swagger2 && Resolve(root, operation["requestBody"]) is { } requestBody)
                    ImportRequestBody(root, requestBody, request);

                var security = operation["security"] as JsonArray ?? globalSecurity;
                if (security is not null && securitySchemes is not null)
                    request.Auth = ImportSecurity(security, securitySchemes, swagger2, collection);

                var expectedStatus = (operation["responses"] as JsonObject)?.Select(r => r.Key)
                    .FirstOrDefault(code => code.StartsWith('2'));
                if (expectedStatus is not null && int.TryParse(expectedStatus, out _))
                    request.Assertions.Add(new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = expectedStatus });
                if (!string.IsNullOrEmpty(specLocation))
                    request.Assertions.Add(new Assertion { Source = ValueSource.Contract, Operator = AssertionOperator.IsValid, Enabled = true });

                foreach (var (code, response) in operation["responses"] as JsonObject ?? [])
                {
                    if (!int.TryParse(code, out var status))
                        continue;
                    var resolved = Resolve(root, response);
                    var (contentType, body) = swagger2
                        ? ("application/json", resolved?["schema"] is { } s ? Json(ExampleNode(root, Resolve(root, s), 0)) : "")
                        : ResponseExample(root, resolved);
                    var schema = swagger2 ? resolved?["schema"] : ResponseSchema(resolved);
                    if (body.Length > 0)
                        request.Examples.Add(new ResponseExample
                        {
                            Name = resolved?["description"]?.ToString() is { Length: > 0 } d ? $"{code} {d}" : code,
                            StatusCode = status,
                            ContentType = contentType,
                            Body = body,
                            Schema = schema is null ? "" : Mock.SchemaFaker.Inline(root, schema)?.ToJsonString() ?? ""
                        });
                }

                collection.Requests.Add(request);
            }
        }

        result.Collections.Add(collection);
        return result;
    }

    private static string OpenApiBaseUrl(JsonNode root, RequestCollection collection)
    {
        var server = (root["servers"] as JsonArray)?.FirstOrDefault();
        var url = server?["url"]?.ToString() ?? "http://localhost";
        foreach (var (name, variable) in server?["variables"] as JsonObject ?? [])
        {
            var value = variable?["default"]?.ToString() ?? "";
            url = url.Replace("{" + name + "}", "{{" + name + "}}");
            collection.Variables.Add(new KeyValueItem(name, value));
        }
        return url.TrimEnd('/');
    }

    private static string SwaggerBaseUrl(JsonNode root)
    {
        var scheme = (root["schemes"] as JsonArray)?.FirstOrDefault()?.ToString() ?? "https";
        var host = root["host"]?.ToString() ?? "localhost";
        var basePath = root["basePath"]?.ToString() ?? "";
        return $"{scheme}://{host}{basePath}".TrimEnd('/');
    }

    private static void ImportRequestBody(JsonNode root, JsonNode requestBody, ApiRequest request)
    {
        if (requestBody["content"] is not JsonObject content || content.Count == 0)
            return;
        var (mediaType, media) = content.FirstOrDefault(c => c.Key.Contains("json")) is { Value: not null } json ? json
            : content.First();
        var schema = Resolve(root, media?["schema"]);
        var example = media?["example"] ?? FirstExample(media?["examples"], root);

        if (mediaType.Contains("json"))
        {
            request.Body = new RequestBody { Mode = BodyMode.Json, Content = Json(example ?? ExampleNode(root, schema, 0)) };
            request.Headers.Add(new KeyValueItem("Content-Type", mediaType));
        }
        else if (mediaType.Contains("x-www-form-urlencoded") || mediaType.StartsWith("multipart/", StringComparison.Ordinal))
        {
            var fields = new List<KeyValueItem>();
            foreach (var (name, property) in Resolve(root, schema?["properties"]) as JsonObject ?? [])
            {
                var propertySchema = Resolve(root, property);
                var isFile = propertySchema?["format"]?.ToString() == "binary";
                fields.Add(new KeyValueItem(name, isFile ? "" : ExampleText(root, propertySchema?["example"], propertySchema)) { IsFile = isFile });
            }
            request.Body = new RequestBody
            {
                Mode = mediaType.StartsWith("multipart/", StringComparison.Ordinal) ? BodyMode.Multipart : BodyMode.FormUrlEncoded,
                FormFields = fields
            };
        }
        else if (mediaType.Contains("xml"))
        {
            request.Body = new RequestBody { Mode = BodyMode.Xml, Content = example?.ToString() ?? "<request/>" };
        }
        else if (mediaType == "application/octet-stream")
        {
            request.Body = new RequestBody { Mode = BodyMode.Binary };
        }
        else
        {
            request.Body = new RequestBody { Mode = BodyMode.Text, Content = example?.ToString() ?? "" };
            request.Headers.Add(new KeyValueItem("Content-Type", mediaType));
        }
    }

    private static (string ContentType, string Body) ResponseExample(JsonNode root, JsonNode? response)
    {
        if (response?["content"] is not JsonObject content || content.Count == 0)
            return ("", "");
        var (mediaType, media) = content.FirstOrDefault(c => c.Key.Contains("json")) is { Value: not null } json ? json : content.First();
        var example = media?["example"] ?? FirstExample(media?["examples"], root)
                      ?? ExampleNode(root, Resolve(root, media?["schema"]), 0);
        return (mediaType, example is JsonValue v && v.TryGetValue<string>(out var s) && !mediaType.Contains("json") ? s : Json(example));
    }

    private static JsonNode? ResponseSchema(JsonNode? response)
    {
        if (response?["content"] is not JsonObject content || content.Count == 0)
            return null;
        var media = content.FirstOrDefault(c => c.Key.Contains("json")).Value ?? content.First().Value;
        return media?["schema"];
    }

    private static AuthSettings ImportSecurity(JsonArray security, JsonObject schemes, bool swagger2, RequestCollection collection)
    {
        foreach (var requirement in security.OfType<JsonObject>())
        {
            foreach (var (name, scopes) in requirement)
            {
                if (schemes[name] is not JsonObject scheme)
                    continue;
                var type = scheme["type"]?.ToString();
                switch (type)
                {
                    case "http" when scheme["scheme"]?.ToString()?.Equals("bearer", StringComparison.OrdinalIgnoreCase) == true:
                        EnsureVariable(collection, "token");
                        return new AuthSettings { Mode = AuthMode.Bearer, Token = "{{token}}" };
                    case "http" when scheme["scheme"]?.ToString()?.Equals("basic", StringComparison.OrdinalIgnoreCase) == true:
                    case "basic":
                        EnsureVariable(collection, "username");
                        EnsureVariable(collection, "password");
                        return new AuthSettings { Mode = AuthMode.Basic, Username = "{{username}}", Password = "{{password}}" };
                    case "http" when scheme["scheme"]?.ToString()?.Equals("digest", StringComparison.OrdinalIgnoreCase) == true:
                        return new AuthSettings { Mode = AuthMode.Digest, Username = "{{username}}", Password = "{{password}}" };
                    case "apiKey":
                        var keyName = scheme["name"]?.ToString() ?? "X-API-Key";
                        var variable = "apiKey";
                        EnsureVariable(collection, variable);
                        return new AuthSettings
                        {
                            Mode = AuthMode.ApiKey,
                            ApiKeyName = keyName,
                            ApiKeyValue = "{{" + variable + "}}",
                            ApiKeyLocation = scheme["in"]?.ToString() == "query" ? ApiKeyLocation.QueryParam : ApiKeyLocation.Header
                        };
                    case "oauth2":
                        EnsureVariable(collection, "clientId");
                        EnsureVariable(collection, "clientSecret");
                        var auth = new AuthSettings
                        {
                            Mode = AuthMode.OAuth2,
                            OAuth2ClientId = "{{clientId}}",
                            OAuth2ClientSecret = "{{clientSecret}}",
                            OAuth2Scope = string.Join(" ", (scopes as JsonArray ?? []).Select(s => s?.ToString()))
                        };
                        if (swagger2)
                        {
                            auth.OAuth2TokenUrl = scheme["tokenUrl"]?.ToString() ?? "";
                            auth.OAuth2AuthUrl = scheme["authorizationUrl"]?.ToString() ?? "";
                            auth.OAuth2GrantType = scheme["flow"]?.ToString() switch
                            {
                                "accessCode" => OAuth2GrantType.AuthorizationCode,
                                "password" => OAuth2GrantType.Password,
                                _ => OAuth2GrantType.ClientCredentials
                            };
                        }
                        else if (scheme["flows"] is JsonObject flows)
                        {
                            var (flowName, flow) = flows.First();
                            auth.OAuth2TokenUrl = flow?["tokenUrl"]?.ToString() ?? "";
                            auth.OAuth2AuthUrl = flow?["authorizationUrl"]?.ToString() ?? "";
                            auth.OAuth2DeviceUrl = flow?["deviceAuthorizationUrl"]?.ToString() ?? "";
                            auth.OAuth2GrantType = flowName switch
                            {
                                "authorizationCode" => OAuth2GrantType.AuthorizationCode,
                                "password" => OAuth2GrantType.Password,
                                _ => OAuth2GrantType.ClientCredentials
                            };
                        }
                        return auth;
                }
            }
        }
        return new AuthSettings();
    }

    private static void EnsureVariable(RequestCollection collection, string name)
    {
        if (!collection.Variables.Any(v => v.Key == name))
            collection.Variables.Add(new KeyValueItem(name, "") { IsSecret = name is "token" or "password" or "apiKey" or "clientSecret" });
    }

    // ---- $ref and examples -------------------------------------------------------------------------------

    /// <summary>Follows a local <c>$ref</c> (possibly chained).</summary>
    public static JsonNode? Resolve(JsonNode root, JsonNode? node)
    {
        for (var guard = 0; guard < 32 && node?["$ref"] is JsonValue r && r.TryGetValue<string>(out var reference); guard++)
            node = Pointer(root, reference);
        return node;
    }

    private static IEnumerable<JsonNode> Resolve(JsonNode root, JsonArray? parameters) =>
        (parameters ?? []).Select(p => Resolve(root, p)).OfType<JsonNode>();

    public static JsonNode? Pointer(JsonNode root, string reference)
    {
        if (!reference.StartsWith('#'))
            return null;
        JsonNode? node = root;
        foreach (var raw in reference[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = Uri.UnescapeDataString(raw).Replace("~1", "/").Replace("~0", "~");
            node = node is JsonObject o ? o[part] : node is JsonArray a && int.TryParse(part, out var i) && i < a.Count ? a[i] : null;
            if (node is null)
                return null;
        }
        return node;
    }

    private static JsonNode? FirstExample(JsonNode? examples, JsonNode? root = null) =>
        examples is JsonObject o && o.Count > 0
            ? (root is null ? o.First().Value : Resolve(root, o.First().Value))?["value"]
            : examples is JsonArray { Count: > 0 } a ? a[0] : null;

    private static string ExampleText(JsonNode root, JsonNode? example, JsonNode? schema)
    {
        var node = example ?? ExampleNode(root, schema, 0);
        return node switch
        {
            null => "",
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString()
        };
    }

    /// <summary>A plausible example value for a schema: explicit examples/defaults/enums first, then by type and format.</summary>
    public static JsonNode? ExampleNode(JsonNode root, JsonNode? schema, int depth)
    {
        schema = Resolve(root, schema);
        if (schema is null || depth > 6)
            return null;

        if (schema["example"] is { } example)
            return example.DeepClone();
        if (schema["examples"] is JsonArray { Count: > 0 } examples)
            return examples[0]?.DeepClone();
        if (schema["default"] is { } defaultValue)
            return defaultValue.DeepClone();
        if (schema["const"] is { } constant)
            return constant.DeepClone();
        if (schema["enum"] is JsonArray { Count: > 0 } values)
            return values[0]?.DeepClone();

        if (schema["allOf"] is JsonArray allOf)
        {
            var merged = new JsonObject();
            foreach (var part in allOf)
                if (ExampleNode(root, part, depth + 1) is JsonObject partObject)
                    foreach (var (k, v) in partObject)
                        merged[k] = v?.DeepClone();
            return merged;
        }
        if ((schema["oneOf"] ?? schema["anyOf"]) is JsonArray { Count: > 0 } choices)
            return ExampleNode(root, choices[0], depth + 1);

        var type = schema["type"] switch
        {
            JsonArray types => types.Select(t => t?.ToString()).FirstOrDefault(t => t != "null"),
            JsonNode t => t.ToString(),
            null when schema["properties"] is not null => "object",
            null when schema["items"] is not null => "array",
            _ => null
        };
        var format = schema["format"]?.ToString();

        switch (type)
        {
            case "object":
                var obj = new JsonObject();
                foreach (var (name, property) in schema["properties"] as JsonObject ?? [])
                {
                    var resolved = Resolve(root, property);
                    if (resolved?["readOnly"]?.GetValue<bool>() == true)
                        continue; // read-only fields don't belong in requests
                    obj[name] = ExampleNode(root, resolved, depth + 1);
                }
                if (obj.Count == 0 && schema["additionalProperties"] is JsonObject additional)
                    obj["key"] = ExampleNode(root, additional, depth + 1);
                return obj;
            case "array":
                return new JsonArray(ExampleNode(root, schema["items"], depth + 1));
            case "integer":
                return JsonValue.Create(schema["minimum"] is JsonValue min ? (long)min.GetValue<double>() : 0L);
            case "number":
                return JsonValue.Create(schema["minimum"] is JsonValue minNumber ? minNumber.GetValue<double>() : 0.0);
            case "boolean":
                return JsonValue.Create(true);
            case "string":
                return JsonValue.Create(format switch
                {
                    "date-time" => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    "date" => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "email" => "user@example.com",
                    "uuid" => "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    "uri" or "url" => "https://example.com",
                    "hostname" => "example.com",
                    "ipv4" => "192.168.0.1",
                    "ipv6" => "::1",
                    "byte" => "U3dhZ2dlcg==",
                    "password" => "********",
                    _ => schema["minLength"] is JsonValue ml && ml.GetValue<double>() > 6 ? new string('x', (int)ml.GetValue<double>()) : "string"
                });
            default:
                return null;
        }
    }

    private static string Json(JsonNode? node) => node?.ToJsonString(Pretty) ?? "";
}
