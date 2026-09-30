using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Interop;

/// <summary>Postman Collection v2.0/v2.1 and Postman environment import / export.</summary>
public static class Postman
{
    private const string SchemaV21 = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool IsCollection(JsonNode root) =>
        root["info"]?["schema"]?.ToString().Contains("getpostman.com", StringComparison.OrdinalIgnoreCase) == true
        || (root["info"] is not null && root["item"] is JsonArray);

    public static bool IsEnvironment(JsonNode root) =>
        root["values"] is JsonArray && (root["_postman_variable_scope"] is not null || root["name"] is not null) && root["item"] is null;

    // ---- Import ------------------------------------------------------------------------------------------

    public static ImportResult Import(JsonNode root)
    {
        var result = new ImportResult { Format = "Postman" };
        if (IsEnvironment(root))
        {
            result.Environments.Add(ImportEnvironment(root));
            return result;
        }

        var collection = new RequestCollection
        {
            Name = root["info"]?["name"]?.ToString() ?? "Postman collection",
            Description = Description(root["info"]?["description"]),
            Variables = (root["variable"] as JsonArray ?? []).Select(Variable).Where(v => v.Key.Length > 0).ToList()
        };

        var collectionAuth = root["auth"] is JsonObject a ? ImportAuth(a, result) : null;
        var collectionScripts = Scripts(root["event"]);
        var order = 0;
        foreach (var item in root["item"] as JsonArray ?? [])
            ImportItem(item!, "", collectionAuth, collectionScripts, collection, result, ref order);

        result.Collections.Add(collection);
        return result;
    }

    private static void ImportItem(JsonNode item, string folder, AuthSettings? inheritedAuth,
        (string Pre, string Test) inheritedScripts, RequestCollection collection, ImportResult result, ref int order)
    {
        var name = item["name"]?.ToString() ?? "Request";
        var scripts = Scripts(item["event"]);
        var combinedScripts = (Join(inheritedScripts.Pre, scripts.Pre), Join(inheritedScripts.Test, scripts.Test));

        if (item["item"] is JsonArray children)
        {
            var auth = item["auth"] is JsonObject folderAuth ? ImportAuth(folderAuth, result) : inheritedAuth;
            var path = folder.Length == 0 ? name : $"{folder}/{name}";
            foreach (var child in children)
                ImportItem(child!, path, auth, combinedScripts, collection, result, ref order);
            return;
        }

        var r = item["request"];
        if (r is null)
            return;

        var request = new ApiRequest
        {
            Name = name,
            Folder = folder,
            SortOrder = order++,
            CollectionId = collection.Id,
            Description = Description(r["description"] ?? item["description"]),
            PreRequestScript = combinedScripts.Item1,
            TestScript = combinedScripts.Item2
        };

        if (r is JsonValue shorthand) // "request": "https://..."
        {
            request.Url = shorthand.ToString();
        }
        else
        {
            request.Method = Enum.TryParse<HttpVerb>(r["method"]?.ToString(), ignoreCase: true, out var m) ? m : HttpVerb.Get;
            request.Url = Url(r["url"], out var disabledQuery);
            request.QueryParams = QueryString.Parse(request.Url).Concat(disabledQuery).ToList();
            request.Headers = (r["header"] as JsonArray ?? []).Select(h => new KeyValueItem(
                h?["key"]?.ToString() ?? "", h?["value"]?.ToString() ?? "", h?["disabled"]?.GetValue<bool>() != true)).ToList();
            request.Auth = r["auth"] is JsonObject auth ? ImportAuth(auth, result) : inheritedAuth?.Clone() ?? new AuthSettings();
            ImportBody(r["body"], request, result);
        }

        foreach (var response in item["response"] as JsonArray ?? [])
        {
            request.Examples.Add(new ResponseExample
            {
                Name = response?["name"]?.ToString() ?? "Example",
                StatusCode = response?["code"]?.GetValue<int>() ?? 200,
                Body = response?["body"]?.ToString() ?? "",
                Headers = (response?["header"] as JsonArray ?? []).Select(h => new KeyValueItem(h?["key"]?.ToString() ?? "", h?["value"]?.ToString() ?? "")).ToList(),
                ContentType = (response?["header"] as JsonArray ?? [])
                    .FirstOrDefault(h => h?["key"]?.ToString().Equals("Content-Type", StringComparison.OrdinalIgnoreCase) == true)?["value"]?.ToString()
                    ?? "application/json"
            });
        }

        collection.Requests.Add(request);
    }

    private static void ImportBody(JsonNode? body, ApiRequest request, ImportResult result)
    {
        if (body is null || body["disabled"]?.GetValue<bool>() == true)
            return;
        switch (body["mode"]?.ToString())
        {
            case "raw":
                var raw = body["raw"]?.ToString() ?? "";
                var language = body["options"]?["raw"]?["language"]?.ToString();
                var contentType = request.Headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
                var mode = language switch
                {
                    "json" => BodyMode.Json,
                    "xml" => BodyMode.Xml,
                    _ when contentType.Contains("json") => BodyMode.Json,
                    _ when contentType.Contains("xml") => BodyMode.Xml,
                    _ => BodyMode.Text
                };
                request.Body = new RequestBody { Mode = mode, Content = raw };
                // A SOAP envelope posted with an action header is imported as a SOAP request.
                if (mode == BodyMode.Xml && raw.Contains("Envelope", StringComparison.Ordinal)
                                          && (raw.Contains("schemas.xmlsoap.org/soap/envelope", StringComparison.Ordinal)
                                              || raw.Contains("www.w3.org/2003/05/soap-envelope", StringComparison.Ordinal)))
                {
                    request.Kind = RequestKind.Soap;
                    request.Protocol.Soap.Version = raw.Contains("2003/05/soap-envelope") ? SoapVersion.Soap12 : SoapVersion.Soap11;
                    var soapAction = request.Headers.FirstOrDefault(h => h.Key.Equals("SOAPAction", StringComparison.OrdinalIgnoreCase));
                    if (soapAction is not null)
                    {
                        request.Protocol.Soap.Action = soapAction.Value.Trim('"');
                        request.Headers.Remove(soapAction);
                    }
                }
                break;
            case "urlencoded":
                request.Body = new RequestBody { Mode = BodyMode.FormUrlEncoded, FormFields = Fields(body["urlencoded"]) };
                break;
            case "formdata":
                request.Body = new RequestBody
                {
                    Mode = BodyMode.Multipart,
                    FormFields = (body["formdata"] as JsonArray ?? []).Select(f => new KeyValueItem(
                        f?["key"]?.ToString() ?? "",
                        f?["type"]?.ToString() == "file" ? f["src"] is JsonArray srcs ? srcs.FirstOrDefault()?.ToString() ?? "" : f["src"]?.ToString() ?? "" : f?["value"]?.ToString() ?? "",
                        f?["disabled"]?.GetValue<bool>() != true) { IsFile = f?["type"]?.ToString() == "file" }).ToList()
                };
                break;
            case "file":
                request.Body = new RequestBody { Mode = BodyMode.Binary, FilePath = body["file"]?["src"]?.ToString() ?? "" };
                break;
            case "graphql":
                request.Kind = RequestKind.GraphQl;
                request.Protocol.GraphQl = new GraphQlSettings
                {
                    Query = body["graphql"]?["query"]?.ToString() ?? "",
                    Variables = body["graphql"]?["variables"]?.ToString() ?? ""
                };
                request.Method = HttpVerb.Post;
                break;
            default:
                if (body["mode"] is not null)
                    result.Warnings.Add($"{request.Name}: unsupported body mode '{body["mode"]}'.");
                break;
        }
    }

    private static string Url(JsonNode? url, out List<KeyValueItem> disabledQuery)
    {
        disabledQuery = [];
        switch (url)
        {
            case null:
                return "";
            case JsonValue v:
                return v.ToString();
        }
        var raw = url["raw"]?.ToString();
        if (url["query"] is JsonArray query)
            disabledQuery = query.Where(q => q?["disabled"]?.GetValue<bool>() == true)
                .Select(q => new KeyValueItem(q?["key"]?.ToString() ?? "", q?["value"]?.ToString() ?? "", enabled: false)).ToList();

        if (!string.IsNullOrEmpty(raw))
        {
            // Postman path variables (:id) become Dispatch variables ({{id}}) when a value is given.
            foreach (var variable in url["variable"] as JsonArray ?? [])
            {
                var key = variable?["key"]?.ToString();
                if (!string.IsNullOrEmpty(key))
                    raw = raw.Replace(":" + key, variable?["value"]?.ToString() is { Length: > 0 } value ? value : "{{" + key + "}}");
            }
            return raw;
        }

        var host = url["host"] is JsonArray h ? string.Join(".", h.Select(x => x?.ToString())) : url["host"]?.ToString() ?? "";
        var path = url["path"] is JsonArray p ? string.Join("/", p.Select(x => x?.ToString())) : url["path"]?.ToString() ?? "";
        var protocol = url["protocol"]?.ToString();
        var result = (protocol is null ? "" : protocol + "://") + host + (url["port"] is { } port ? ":" + port : "") + (path.Length > 0 ? "/" + path : "");
        var active = (url["query"] as JsonArray ?? []).Where(q => q?["disabled"]?.GetValue<bool>() != true).ToList();
        if (active.Count > 0)
            result += "?" + string.Join("&", active.Select(q => $"{q?["key"]}={q?["value"]}"));
        return result;
    }

    private static AuthSettings ImportAuth(JsonObject auth, ImportResult result)
    {
        var type = auth["type"]?.ToString() ?? "noauth";
        string P(string key)
        {
            // v2.1: array of {key, value}; v2.0: object.
            var section = auth[type];
            if (section is JsonArray array)
                return array.FirstOrDefault(x => x?["key"]?.ToString() == key)?["value"]?.ToString() ?? "";
            return section?[key]?.ToString() ?? "";
        }

        switch (type)
        {
            case "bearer":
                return new AuthSettings { Mode = AuthMode.Bearer, Token = P("token") };
            case "basic":
                return new AuthSettings { Mode = AuthMode.Basic, Username = P("username"), Password = P("password") };
            case "digest":
                return new AuthSettings { Mode = AuthMode.Digest, Username = P("username"), Password = P("password") };
            case "ntlm":
                return new AuthSettings { Mode = AuthMode.Ntlm, Username = P("username"), Password = P("password"), Domain = P("domain") };
            case "apikey":
                return new AuthSettings
                {
                    Mode = AuthMode.ApiKey,
                    ApiKeyName = P("key") is { Length: > 0 } k ? k : "X-API-Key",
                    ApiKeyValue = P("value"),
                    ApiKeyLocation = P("in") == "query" ? ApiKeyLocation.QueryParam : ApiKeyLocation.Header
                };
            case "awsv4":
                return new AuthSettings
                {
                    Mode = AuthMode.AwsSigV4,
                    AwsAccessKey = P("accessKey"),
                    AwsSecretKey = P("secretKey"),
                    AwsSessionToken = P("sessionToken"),
                    AwsRegion = P("region") is { Length: > 0 } region ? region : "us-east-1",
                    AwsService = P("service") is { Length: > 0 } service ? service : "execute-api"
                };
            case "oauth2":
                var grant = P("grant_type");
                return new AuthSettings
                {
                    Mode = AuthMode.OAuth2,
                    OAuth2GrantType = grant switch
                    {
                        "authorization_code" or "authorization_code_with_pkce" => OAuth2GrantType.AuthorizationCode,
                        "password_credentials" => OAuth2GrantType.Password,
                        _ => OAuth2GrantType.ClientCredentials
                    },
                    OAuth2TokenUrl = P("accessTokenUrl"),
                    OAuth2AuthUrl = P("authUrl"),
                    OAuth2ClientId = P("clientId"),
                    OAuth2ClientSecret = P("clientSecret"),
                    OAuth2Scope = P("scope"),
                    OAuth2RedirectUri = P("redirect_uri") is { Length: > 0 } redirect ? redirect : new AuthSettings().OAuth2RedirectUri,
                    OAuth2UsePkce = grant == "authorization_code_with_pkce" || grant == "authorization_code",
                    OAuth2CredentialsInBody = P("client_authentication") == "body",
                    Username = P("username"),
                    Password = P("password")
                };
            case "noauth" or "inherit":
                return new AuthSettings();
            default:
                result.Warnings.Add($"Unsupported auth type '{type}' was skipped.");
                return new AuthSettings();
        }
    }

    private static (string Pre, string Test) Scripts(JsonNode? events)
    {
        string Exec(string listen) => string.Join("\n", (events as JsonArray ?? [])
            .Where(e => e?["listen"]?.ToString() == listen)
            .Select(e => e?["script"]?["exec"] switch
            {
                JsonArray lines => string.Join("\n", lines.Select(l => l?.ToString())),
                JsonNode n => n.ToString(),
                null => ""
            }));
        return (Exec("prerequest"), Exec("test"));
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "\n" + b;

    private static List<KeyValueItem> Fields(JsonNode? array) =>
        (array as JsonArray ?? []).Select(f => new KeyValueItem(f?["key"]?.ToString() ?? "", f?["value"]?.ToString() ?? "",
            f?["disabled"]?.GetValue<bool>() != true)).ToList();

    private static KeyValueItem Variable(JsonNode? v) => new(v?["key"]?.ToString() ?? v?["id"]?.ToString() ?? "",
        v?["value"]?.ToString() ?? "", v?["disabled"]?.GetValue<bool>() != true && v?["enabled"]?.GetValue<bool>() != false)
    {
        IsSecret = v?["type"]?.ToString() == "secret"
    };

    private static string Description(JsonNode? node) => node switch
    {
        JsonValue v => v.ToString(),
        JsonObject o => o["content"]?.ToString() ?? "",
        _ => ""
    };

    public static ApiEnvironment ImportEnvironment(JsonNode root) => new()
    {
        Name = root["name"]?.ToString() ?? "Postman environment",
        Variables = (root["values"] as JsonArray ?? []).Select(Variable).Where(v => v.Key.Length > 0).ToList()
    };

    // ---- Export ------------------------------------------------------------------------------------------

    public static string Export(RequestCollection collection)
    {
        var root = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["_postman_id"] = collection.Id.ToString(),
                ["name"] = collection.Name,
                ["description"] = collection.Description,
                ["schema"] = SchemaV21
            },
            ["item"] = new JsonArray(),
            ["variable"] = new JsonArray(collection.Variables.Select(v => (JsonNode?)new JsonObject
            {
                ["key"] = v.Key,
                ["value"] = v.IsSecret ? "" : v.Value,
                ["disabled"] = !v.Enabled
            }).ToArray())
        };

        var folders = new Dictionary<string, JsonArray>(StringComparer.Ordinal) { [""] = root["item"]!.AsArray() };
        JsonArray FolderItems(string path)
        {
            if (folders.TryGetValue(path, out var items))
                return items;
            var slash = path.LastIndexOf('/');
            var parent = FolderItems(slash < 0 ? "" : path[..slash]);
            var folder = new JsonObject { ["name"] = slash < 0 ? path : path[(slash + 1)..], ["item"] = new JsonArray() };
            parent.Add(folder);
            return folders[path] = folder["item"]!.AsArray();
        }

        foreach (var request in collection.Requests.OrderBy(r => r.SortOrder))
            FolderItems(request.Folder.Trim('/')).Add(ExportRequest(request));

        return root.ToJsonString(Pretty);
    }

    private static JsonObject ExportRequest(ApiRequest r)
    {
        var headers = r.Headers.Select(h => (JsonNode?)new JsonObject { ["key"] = h.Key, ["value"] = h.Value, ["disabled"] = !h.Enabled }).ToList();
        JsonObject? body = null;
        var method = r.Method.ToString().ToUpperInvariant();

        switch (r.Kind)
        {
            case RequestKind.GraphQl:
                method = "POST";
                body = new JsonObject
                {
                    ["mode"] = "graphql",
                    ["graphql"] = new JsonObject { ["query"] = r.Protocol.GraphQl.Query, ["variables"] = r.Protocol.GraphQl.Variables }
                };
                break;
            case RequestKind.Soap:
                method = "POST";
                headers.Add(new JsonObject { ["key"] = "Content-Type", ["value"] = r.Protocol.Soap.Version == SoapVersion.Soap12 ? "application/soap+xml; charset=utf-8" : "text/xml; charset=utf-8" });
                if (r.Protocol.Soap.Version == SoapVersion.Soap11)
                    headers.Add(new JsonObject { ["key"] = "SOAPAction", ["value"] = $"\"{r.Protocol.Soap.Action}\"" });
                body = Raw(r.Body.Content, "xml");
                break;
            default:
                body = r.Body.Mode switch
                {
                    BodyMode.Json => Raw(r.Body.Content, "json"),
                    BodyMode.Xml => Raw(r.Body.Content, "xml"),
                    BodyMode.Text => Raw(r.Body.Content, "text"),
                    BodyMode.FormUrlEncoded => new JsonObject { ["mode"] = "urlencoded", ["urlencoded"] = FieldsNode(r.Body.FormFields, false) },
                    BodyMode.Multipart => new JsonObject { ["mode"] = "formdata", ["formdata"] = FieldsNode(r.Body.FormFields, true) },
                    BodyMode.Binary => new JsonObject { ["mode"] = "file", ["file"] = new JsonObject { ["src"] = r.Body.FilePath } },
                    _ => null
                };
                break;
        }

        var request = new JsonObject
        {
            ["method"] = method,
            ["header"] = new JsonArray(headers.ToArray()),
            ["url"] = new JsonObject
            {
                ["raw"] = r.Url,
                ["query"] = new JsonArray((r.QueryParams.Count > 0 ? r.QueryParams : QueryString.Parse(r.Url))
                    .Select(q => (JsonNode?)new JsonObject { ["key"] = q.Key, ["value"] = q.Value, ["disabled"] = !q.Enabled }).ToArray())
            },
            ["description"] = r.Description
        };
        if (body is not null)
            request["body"] = body;
        if (ExportAuth(r.Auth) is { } auth)
            request["auth"] = auth;

        var item = new JsonObject { ["name"] = r.Name, ["request"] = request };
        var events = new JsonArray();
        if (!string.IsNullOrWhiteSpace(r.PreRequestScript))
            events.Add(Event("prerequest", r.PreRequestScript));
        var tests = TestScriptWithAssertions(r);
        if (!string.IsNullOrWhiteSpace(tests))
            events.Add(Event("test", tests));
        if (events.Count > 0)
            item["event"] = events;
        if (r.Examples.Count > 0)
            item["response"] = new JsonArray(r.Examples.Select(e => (JsonNode?)new JsonObject
            {
                ["name"] = e.Name,
                ["code"] = e.StatusCode,
                ["header"] = new JsonArray(e.Headers.Append(new KeyValueItem("Content-Type", e.ContentType))
                    .Select(h => (JsonNode?)new JsonObject { ["key"] = h.Key, ["value"] = h.Value }).ToArray()),
                ["body"] = e.Body
            }).ToArray());
        return item;
    }

    /// <summary>No-code assertions become equivalent pm.test calls so the export behaves the same in Postman.</summary>
    private static string TestScriptWithAssertions(ApiRequest r)
    {
        var lines = new List<string>();
        foreach (var a in r.Assertions.Where(a => a.Enabled))
        {
            var name = JsonSerializer.Serialize(Testing.AssertionEvaluator.Describe(a));
            var expected = JsonSerializer.Serialize(a.Expected);
            var line = (a.Source, a.Operator) switch
            {
                (ValueSource.Status, AssertionOperator.Equals) => $"pm.test({name}, () => pm.response.to.have.status({a.Expected}));",
                (ValueSource.ResponseTime, AssertionOperator.LessThan or AssertionOperator.LessOrEqual) =>
                    $"pm.test({name}, () => pm.expect(pm.response.responseTime).to.be.at.most({a.Expected}));",
                (ValueSource.Header, AssertionOperator.Exists) => $"pm.test({name}, () => pm.response.to.have.header({JsonSerializer.Serialize(a.Path)}));",
                (ValueSource.Header, AssertionOperator.Equals) =>
                    $"pm.test({name}, () => pm.expect(pm.response.headers.get({JsonSerializer.Serialize(a.Path)})).to.equal({expected}));",
                (ValueSource.Body, AssertionOperator.Contains) => $"pm.test({name}, () => pm.expect(pm.response.text()).to.include({expected}));",
                (ValueSource.JsonPath, AssertionOperator.Equals) when SimplePath(a.Path) is { } p =>
                    $"pm.test({name}, () => pm.expect(String(pm.response.json(){p})).to.equal({expected}));",
                (ValueSource.JsonPath, AssertionOperator.Exists) when SimplePath(a.Path) is { } p =>
                    $"pm.test({name}, () => pm.expect(pm.response.json(){p}).to.exist);",
                _ => $"// Dispatch assertion not translated: {Testing.AssertionEvaluator.Describe(a)}"
            };
            lines.Add(line);
        }
        foreach (var e in r.Extractions.Where(e => e.Enabled && e.Source == ValueSource.JsonPath && SimplePath(e.Path) is not null))
            lines.Add($"pm.environment.set({JsonSerializer.Serialize(e.Variable)}, pm.response.json(){SimplePath(e.Path)});");
        if (!string.IsNullOrWhiteSpace(r.TestScript))
            lines.Add(r.TestScript);
        return string.Join("\n", lines);
    }

    /// <summary>$.a.b[0].c → .a.b[0].c (only simple member/index paths).</summary>
    private static string? SimplePath(string path)
    {
        var p = path.Trim();
        if (p.StartsWith('$'))
            p = p[1..];
        if (p.Length > 0 && !p.StartsWith('.') && !p.StartsWith('['))
            p = "." + p;
        return System.Text.RegularExpressions.Regex.IsMatch(p, @"^(\.[A-Za-z_$][\w$]*|\[\d+\])*$") ? p : null;
    }

    private static JsonObject Event(string listen, string script) => new()
    {
        ["listen"] = listen,
        ["script"] = new JsonObject
        {
            ["type"] = "text/javascript",
            ["exec"] = new JsonArray(script.Split('\n').Select(l => (JsonNode?)JsonValue.Create(l.TrimEnd('\r'))).ToArray())
        }
    };

    private static JsonObject Raw(string content, string language) => new()
    {
        ["mode"] = "raw",
        ["raw"] = content,
        ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = language } }
    };

    private static JsonArray FieldsNode(IEnumerable<KeyValueItem> fields, bool multipart) => new(fields.Select(f =>
    {
        var node = new JsonObject { ["key"] = f.Key, ["disabled"] = !f.Enabled };
        if (multipart && f.IsFile)
        {
            node["type"] = "file";
            node["src"] = f.Value;
        }
        else
        {
            node["value"] = f.Value;
            if (multipart)
                node["type"] = "text";
        }
        return (JsonNode?)node;
    }).ToArray());

    private static JsonObject? ExportAuth(AuthSettings auth)
    {
        JsonArray Params(params (string Key, string Value)[] values) =>
            new(values.Select(v => (JsonNode?)new JsonObject { ["key"] = v.Key, ["value"] = v.Value, ["type"] = "string" }).ToArray());

        return auth.Mode switch
        {
            AuthMode.Bearer => new JsonObject { ["type"] = "bearer", ["bearer"] = Params(("token", auth.Token)) },
            AuthMode.Basic => new JsonObject { ["type"] = "basic", ["basic"] = Params(("username", auth.Username), ("password", auth.Password)) },
            AuthMode.Digest => new JsonObject { ["type"] = "digest", ["digest"] = Params(("username", auth.Username), ("password", auth.Password)) },
            AuthMode.Ntlm => new JsonObject { ["type"] = "ntlm", ["ntlm"] = Params(("username", auth.Username), ("password", auth.Password), ("domain", auth.Domain)) },
            AuthMode.ApiKey => new JsonObject
            {
                ["type"] = "apikey",
                ["apikey"] = Params(("key", auth.ApiKeyName), ("value", auth.ApiKeyValue), ("in", auth.ApiKeyLocation == ApiKeyLocation.QueryParam ? "query" : "header"))
            },
            AuthMode.AwsSigV4 => new JsonObject
            {
                ["type"] = "awsv4",
                ["awsv4"] = Params(("accessKey", auth.AwsAccessKey), ("secretKey", auth.AwsSecretKey), ("sessionToken", auth.AwsSessionToken),
                    ("region", auth.AwsRegion), ("service", auth.AwsService))
            },
            AuthMode.OAuth2 => new JsonObject
            {
                ["type"] = "oauth2",
                ["oauth2"] = Params(
                    ("grant_type", auth.OAuth2GrantType switch
                    {
                        OAuth2GrantType.AuthorizationCode => auth.OAuth2UsePkce ? "authorization_code_with_pkce" : "authorization_code",
                        OAuth2GrantType.Password => "password_credentials",
                        _ => "client_credentials"
                    }),
                    ("accessTokenUrl", auth.OAuth2TokenUrl), ("authUrl", auth.OAuth2AuthUrl), ("clientId", auth.OAuth2ClientId),
                    ("clientSecret", auth.OAuth2ClientSecret), ("scope", auth.OAuth2Scope), ("redirect_uri", auth.OAuth2RedirectUri),
                    ("client_authentication", auth.OAuth2CredentialsInBody ? "body" : "header"))
            },
            _ => null
        };
    }

    public static string ExportEnvironment(ApiEnvironment environment) => new JsonObject
    {
        ["id"] = environment.Id.ToString(),
        ["name"] = environment.Name,
        ["values"] = new JsonArray(environment.Variables.Select(v => (JsonNode?)new JsonObject
        {
            ["key"] = v.Key,
            ["value"] = v.IsSecret ? "" : v.Value,
            ["type"] = v.IsSecret ? "secret" : "default",
            ["enabled"] = v.Enabled
        }).ToArray()),
        ["_postman_variable_scope"] = "environment"
    }.ToJsonString(Pretty);
}
