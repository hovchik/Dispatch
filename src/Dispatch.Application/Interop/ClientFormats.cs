using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Interop;

/// <summary>Insomnia v5 exports (YAML, <c>type: collection.insomnia.rest/5.0</c>), already converted to JSON.</summary>
public static class InsomniaV5
{
    public static bool IsExport(JsonNode root) =>
        root is JsonObject && root["type"]?.ToString().StartsWith("collection.insomnia.rest/", StringComparison.Ordinal) == true;

    public static ImportResult Import(JsonNode root)
    {
        var result = new ImportResult { Format = "Insomnia" };
        var collection = new RequestCollection { Name = root["name"]?.ToString() ?? "Insomnia", Description = root["meta"]?["description"]?.ToString() ?? "" };
        var order = 0;
        AddItems(root["collection"], "", collection, ref order);
        result.Collections.Add(collection);

        if (root["environments"] is JsonObject env)
        {
            AddEnvironment(env, result);
            foreach (var sub in env["subEnvironments"] as JsonArray ?? [])
                if (sub is JsonObject s)
                    AddEnvironment(s, result);
        }
        return result;
    }

    private static void AddItems(JsonNode? items, string folder, RequestCollection collection, ref int order)
    {
        foreach (var item in items as JsonArray ?? [])
        {
            if (item is null)
                continue;
            var name = item["name"]?.ToString() ?? "Request";
            if (item["children"] is JsonArray children)
            {
                AddItems(children, folder.Length == 0 ? name : $"{folder}/{name}", collection, ref order);
                continue;
            }
            if (item["url"] is null)
                continue;

            var request = new ApiRequest
            {
                Name = name,
                Folder = folder,
                CollectionId = collection.Id,
                SortOrder = order++,
                Url = Insomnia.ConvertTemplates(item["url"]!.ToString()),
                Description = item["meta"]?["description"]?.ToString() ?? item["description"]?.ToString() ?? "",
                Headers = Insomnia.Pairs(item["headers"]),
                Method = Enum.TryParse<HttpVerb>(item["method"]?.ToString(), true, out var m) ? m : HttpVerb.Get
            };
            request.QueryParams = Insomnia.Pairs(item["parameters"]);
            if (request.QueryParams.Count > 0)
                request.Url = QueryString.WithParams(request.Url, request.QueryParams);
            Insomnia.ApplyBody(request, item["body"]);
            request.Auth = Insomnia.ImportAuth(item["authentication"]);
            request.PreRequestScript = item["scripts"]?["preRequest"]?.ToString() ?? "";
            request.TestScript = item["scripts"]?["afterResponse"]?.ToString() ?? "";
            collection.Requests.Add(request);
        }
    }

    private static void AddEnvironment(JsonObject env, ImportResult result)
    {
        if (env["data"] is not JsonObject data || data.Count == 0)
            return;
        result.Environments.Add(new ApiEnvironment
        {
            Name = env["name"]?.ToString() ?? "Environment",
            Variables = data.Select(kv => new KeyValueItem(kv.Key, kv.Value is JsonValue v ? v.ToString() : kv.Value?.ToJsonString() ?? "")).ToList()
        });
    }
}

/// <summary>Thunder Client (VS Code) collection and environment exports.</summary>
public static class ThunderClient
{
    public static bool IsExport(JsonNode root) =>
        root is JsonObject && root["clientName"]?.ToString() == "Thunder Client";

    public static ImportResult Import(JsonNode root)
    {
        var result = new ImportResult { Format = "Thunder Client" };
        if (root["environmentName"] is not null || (root["data"] is JsonArray && root["requests"] is null))
        {
            result.Environments.Add(new ApiEnvironment
            {
                Name = root["environmentName"]?.ToString() ?? root["name"]?.ToString() ?? "Thunder Client",
                Variables = Pairs(root["data"])
            });
            return result;
        }

        var collection = new RequestCollection { Name = root["collectionName"]?.ToString() ?? "Thunder Client" };
        var folders = (root["folders"] as JsonArray ?? []).Where(f => f?["_id"] is not null)
            .ToDictionary(f => f!["_id"]!.ToString(), f => f!);
        var requests = (root["requests"] as JsonArray ?? []).Where(r => r is not null).Select(r => r!)
            .OrderBy(r => r["sortNum"]?.GetValue<double>() ?? 0).ToList();

        foreach (var r in requests)
        {
            var request = new ApiRequest
            {
                Name = r["name"]?.ToString() ?? "Request",
                Folder = FolderPath(r["containerId"]?.ToString(), folders),
                CollectionId = collection.Id,
                SortOrder = collection.Requests.Count,
                Url = r["url"]?.ToString() ?? "",
                Method = Enum.TryParse<HttpVerb>(r["method"]?.ToString(), true, out var m) ? m : HttpVerb.Get,
                Headers = Pairs(r["headers"])
            };
            // Query params are already part of the URL; path params ({id}) are left as written.
            request.QueryParams = Pairs((r["params"] as JsonArray)?.Where(p => p?["isPath"]?.GetValue<bool>() != true).Select(p => p?.DeepClone()).ToArray() is { } q
                ? new JsonArray(q) : null);

            var body = r["body"];
            switch (body?["type"]?.ToString())
            {
                case "json":
                    request.Body = new RequestBody { Mode = BodyMode.Json, Content = body["raw"]?.ToString() ?? "" };
                    break;
                case "xml":
                    request.Body = new RequestBody { Mode = BodyMode.Xml, Content = body["raw"]?.ToString() ?? "" };
                    break;
                case "text":
                    request.Body = new RequestBody { Mode = BodyMode.Text, Content = body["raw"]?.ToString() ?? "" };
                    break;
                case "formencoded":
                    request.Body = new RequestBody { Mode = BodyMode.FormUrlEncoded, FormFields = Pairs(body["form"]) };
                    break;
                case "formdata":
                    var fields = Pairs(body["form"]);
                    fields.AddRange(Pairs(body["files"]).Select(f => { f.IsFile = true; return f; }));
                    request.Body = new RequestBody { Mode = BodyMode.Multipart, FormFields = fields };
                    break;
                case "binary":
                    request.Body = new RequestBody { Mode = BodyMode.Binary, FilePath = body["binary"]?.ToString() ?? "" };
                    break;
                case "graphql":
                    request.Kind = RequestKind.GraphQl;
                    request.Protocol.GraphQl.Query = body["graphql"]?["query"]?.ToString() ?? "";
                    request.Protocol.GraphQl.Variables = body["graphql"]?["variables"]?.ToString() ?? "";
                    break;
            }

            request.Auth = ImportAuth(r["auth"]);
            collection.Requests.Add(request);
        }
        result.Collections.Add(collection);
        return result;
    }

    private static string FolderPath(string? containerId, Dictionary<string, JsonNode> folders)
    {
        var parts = new List<string>();
        for (var guard = 0; !string.IsNullOrEmpty(containerId) && guard < 64 && folders.TryGetValue(containerId, out var folder); guard++)
        {
            parts.Insert(0, folder["name"]?.ToString() ?? "Folder");
            containerId = folder["containerId"]?.ToString();
        }
        return string.Join("/", parts);
    }

    private static List<KeyValueItem> Pairs(JsonNode? array) =>
        (array as JsonArray ?? []).Select(p => new KeyValueItem(p?["name"]?.ToString() ?? "", p?["value"]?.ToString() ?? "",
            p?["isDisabled"]?.GetValue<bool>() != true)).Where(p => p.Key.Length > 0).ToList();

    private static AuthSettings ImportAuth(JsonNode? auth)
    {
        string S(params string[] path)
        {
            var node = auth;
            foreach (var key in path)
                node = node?[key];
            return node?.ToString() ?? "";
        }
        return auth?["type"]?.ToString() switch
        {
            "bearer" => new AuthSettings { Mode = AuthMode.Bearer, Token = S("bearer") },
            "basic" => new AuthSettings { Mode = AuthMode.Basic, Username = S("basic", "username"), Password = S("basic", "password") },
            "ntlm" => new AuthSettings { Mode = AuthMode.Ntlm, Username = S("ntlm", "username"), Password = S("ntlm", "password"), Domain = S("ntlm", "domain") },
            "aws" => new AuthSettings
            {
                Mode = AuthMode.AwsSigV4, AwsAccessKey = S("aws", "accessKeyId"), AwsSecretKey = S("aws", "secretKey"),
                AwsSessionToken = S("aws", "sessionToken"), AwsRegion = S("aws", "region"), AwsService = S("aws", "service")
            },
            "oauth2" => new AuthSettings
            {
                Mode = AuthMode.OAuth2,
                OAuth2GrantType = S("oauth2", "grantType") switch
                {
                    "authorization_code" => OAuth2GrantType.AuthorizationCode,
                    "password" => OAuth2GrantType.Password,
                    _ => OAuth2GrantType.ClientCredentials
                },
                OAuth2TokenUrl = S("oauth2", "tokenUrl"), OAuth2AuthUrl = S("oauth2", "authUrl"), OAuth2ClientId = S("oauth2", "clientId"),
                OAuth2ClientSecret = S("oauth2", "clientSecret"), OAuth2Scope = S("oauth2", "scope"),
                Username = S("oauth2", "username"), Password = S("oauth2", "password")
            },
            _ => new AuthSettings()
        };
    }
}

/// <summary>Hoppscotch collection exports (a collection object or an array of them) and environments.</summary>
public static class Hoppscotch
{
    public static bool IsCollection(JsonNode root) => root switch
    {
        JsonArray array => array.Count > 0 && array.All(c => c is JsonObject o && LooksLikeCollection(o)),
        JsonObject o => LooksLikeCollection(o) && o["v"] is not null,
        _ => false
    };

    /// <summary><c>[{ "name": "...", "variables": [{ "key": ..., "value": ... }] }]</c>, possibly a single object.</summary>
    public static bool IsEnvironment(JsonNode root) => root switch
    {
        JsonArray array => array.Count > 0 && array.All(e => e is JsonObject o && LooksLikeEnvironment(o)),
        JsonObject o => LooksLikeEnvironment(o) && o["v"] is not null,
        _ => false
    };

    private static bool LooksLikeCollection(JsonObject o) => o["folders"] is JsonArray && o["requests"] is JsonArray && o["name"] is not null;

    private static bool LooksLikeEnvironment(JsonObject o) =>
        o["name"] is not null && o["variables"] is JsonArray vars && o["requests"] is null && vars.All(v => v?["key"] is not null);

    public static ImportResult Import(JsonNode root)
    {
        var result = new ImportResult { Format = "Hoppscotch" };
        var items = root is JsonArray array ? array.Where(n => n is not null).Select(n => n!).ToList() : [root];

        if (IsEnvironment(root))
        {
            foreach (var env in items)
                result.Environments.Add(new ApiEnvironment
                {
                    Name = env["name"]?.ToString() ?? "Hoppscotch",
                    Variables = (env["variables"] as JsonArray ?? []).Select(v => new KeyValueItem(v?["key"]?.ToString() ?? "",
                        v?["value"]?.ToString() ?? v?["currentValue"]?.ToString() ?? v?["initialValue"]?.ToString() ?? "")).Where(v => v.Key.Length > 0).ToList()
                });
            return result;
        }

        foreach (var root2 in items)
        {
            var collection = new RequestCollection { Name = root2["name"]?.ToString() ?? "Hoppscotch", Description = root2["description"]?.ToString() ?? "" };
            AddFolder(root2, "", collection);
            result.Collections.Add(collection);
        }
        return result;
    }

    private static void AddFolder(JsonNode folder, string path, RequestCollection collection)
    {
        foreach (var r in folder["requests"] as JsonArray ?? [])
            if (r is not null)
                collection.Requests.Add(ToRequest(r, path, collection));
        foreach (var child in folder["folders"] as JsonArray ?? [])
        {
            if (child is null)
                continue;
            var name = child["name"]?.ToString() ?? "Folder";
            AddFolder(child, path.Length == 0 ? name : $"{path}/{name}", collection);
        }
    }

    private static ApiRequest ToRequest(JsonNode r, string folder, RequestCollection collection)
    {
        var request = new ApiRequest
        {
            Name = r["name"]?.ToString() ?? "Request",
            Folder = folder,
            CollectionId = collection.Id,
            SortOrder = collection.Requests.Count,
            Description = r["description"]?.ToString() ?? "",
            Headers = Pairs(r["headers"]),
            PreRequestScript = r["preRequestScript"]?.ToString() ?? "",
            TestScript = r["testScript"]?.ToString() ?? ""
        };

        // GraphQL collections store url + query instead of endpoint + method.
        if (r["query"] is not null && r["endpoint"] is null)
        {
            request.Kind = RequestKind.GraphQl;
            request.Method = HttpVerb.Post;
            request.Url = ConvertTemplates(r["url"]?.ToString() ?? "");
            request.Protocol.GraphQl.Query = r["query"]?.ToString() ?? "";
            request.Protocol.GraphQl.Variables = r["variables"]?.ToString() ?? "";
            request.Auth = ImportAuth(r["auth"]);
            return request;
        }

        request.Url = ConvertTemplates(r["endpoint"]?.ToString() ?? "");
        request.Method = Enum.TryParse<HttpVerb>(r["method"]?.ToString(), true, out var m) ? m : HttpVerb.Get;
        request.QueryParams = Pairs(r["params"]);
        if (request.QueryParams.Count > 0)
            request.Url = QueryString.WithParams(request.Url, request.QueryParams);

        var contentType = r["body"]?["contentType"]?.ToString() ?? "";
        var body = r["body"]?["body"];
        if (contentType.Length > 0 && body is not null)
        {
            if (contentType == "multipart/form-data" && body is JsonArray parts)
                request.Body = new RequestBody
                {
                    Mode = BodyMode.Multipart,
                    FormFields = parts.Select(p => new KeyValueItem(p?["key"]?.ToString() ?? "",
                        p?["value"] is JsonArray files ? string.Join(",", files) : ConvertTemplates(p?["value"]?.ToString() ?? ""),
                        p?["active"]?.GetValue<bool>() != false) { IsFile = p?["isFile"]?.GetValue<bool>() == true }).Where(p => p.Key.Length > 0).ToList()
                };
            else if (contentType == "application/x-www-form-urlencoded")
                request.Body = new RequestBody { Mode = BodyMode.FormUrlEncoded, FormFields = ParseLines(body.ToString()) };
            else
                request.Body = new RequestBody
                {
                    Mode = contentType.Contains("json") ? BodyMode.Json : contentType.Contains("xml") ? BodyMode.Xml : BodyMode.Text,
                    Content = ConvertTemplates(body.ToString())
                };
        }

        request.Auth = ImportAuth(r["auth"]);
        return request;
    }

    /// <summary>Old Hoppscotch versions use <c>&lt;&lt;var&gt;&gt;</c>; newer ones already use <c>{{var}}</c>.</summary>
    internal static string ConvertTemplates(string text) => Regex.Replace(text, @"<<\s*([\w.-]+)\s*>>", "{{$1}}");

    /// <summary>Form bodies are stored as <c>key: value</c> lines; a leading <c>#</c> disables a line.</summary>
    private static List<KeyValueItem> ParseLines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l =>
        {
            var enabled = !l.StartsWith('#');
            l = l.TrimStart('#').Trim();
            var colon = l.IndexOf(':');
            return colon < 0 ? new KeyValueItem(l, "", enabled) : new KeyValueItem(l[..colon].Trim(), ConvertTemplates(l[(colon + 1)..].Trim()), enabled);
        }).Where(p => p.Key.Length > 0).ToList();

    private static List<KeyValueItem> Pairs(JsonNode? array) =>
        (array as JsonArray ?? []).Select(p => new KeyValueItem(p?["key"]?.ToString() ?? "", ConvertTemplates(p?["value"]?.ToString() ?? ""),
            p?["active"]?.GetValue<bool>() != false)).Where(p => p.Key.Length > 0).ToList();

    private static AuthSettings ImportAuth(JsonNode? auth)
    {
        if (auth is null || auth["authActive"]?.GetValue<bool>() == false)
            return new AuthSettings();
        string S(string key) => ConvertTemplates(auth[key]?.ToString() ?? "");
        return auth["authType"]?.ToString() switch
        {
            "bearer" => new AuthSettings { Mode = AuthMode.Bearer, Token = S("token") },
            "basic" => new AuthSettings { Mode = AuthMode.Basic, Username = S("username"), Password = S("password") },
            "digest" => new AuthSettings { Mode = AuthMode.Digest, Username = S("username"), Password = S("password") },
            "api-key" => new AuthSettings
            {
                Mode = AuthMode.ApiKey, ApiKeyName = S("key"), ApiKeyValue = S("value"),
                ApiKeyLocation = S("addTo").Contains("QUERY", StringComparison.OrdinalIgnoreCase) ? ApiKeyLocation.QueryParam : ApiKeyLocation.Header
            },
            "aws-signature" => new AuthSettings
            {
                Mode = AuthMode.AwsSigV4, AwsAccessKey = S("accessKey"), AwsSecretKey = S("secretKey"),
                AwsSessionToken = S("serviceToken"), AwsRegion = S("region"), AwsService = S("serviceName")
            },
            "oauth-2" => new AuthSettings
            {
                Mode = AuthMode.OAuth2,
                OAuth2GrantType = auth["grantTypeInfo"]?["grantType"]?.ToString() switch
                {
                    "AUTHORIZATION_CODE" => OAuth2GrantType.AuthorizationCode,
                    "PASSWORD" => OAuth2GrantType.Password,
                    _ => OAuth2GrantType.ClientCredentials
                },
                OAuth2TokenUrl = ConvertTemplates(auth["grantTypeInfo"]?["tokenEndpoint"]?.ToString() ?? ""),
                OAuth2AuthUrl = ConvertTemplates(auth["grantTypeInfo"]?["authEndpoint"]?.ToString() ?? ""),
                OAuth2ClientId = ConvertTemplates(auth["grantTypeInfo"]?["clientID"]?.ToString() ?? ""),
                OAuth2ClientSecret = ConvertTemplates(auth["grantTypeInfo"]?["clientSecret"]?.ToString() ?? ""),
                OAuth2Scope = ConvertTemplates(auth["grantTypeInfo"]?["scopes"]?.ToString() ?? ""),
                Token = S("token")
            },
            _ => new AuthSettings()
        };
    }
}

/// <summary>
/// Bruno collections: a folder of <c>.bru</c> files with a <c>bruno.json</c>, a single <c>.bru</c> file, or the JSON
/// produced by Bruno's "Export collection".
/// </summary>
public static class Bruno
{
    public const string ScriptWarning = "Bruno scripts use the bru/res API; they were imported as-is and may need porting to pm.*.";

    public static bool IsJsonExport(JsonNode root) =>
        root is JsonObject && root["items"] is JsonArray items && root["name"] is not null
        && (items.Count == 0 || items.Any(i => i?["type"]?.ToString() is "http-request" or "graphql-request" or "folder"));

    public static bool LooksLikeBru(string text) =>
        Regex.IsMatch(text, @"^meta\s*\{", RegexOptions.Multiline) && Regex.IsMatch(text, @"^(get|post|put|patch|delete|head|options|graphql)\s*\{", RegexOptions.Multiline);

    public static bool IsCollectionFolder(string folder) => File.Exists(Path.Combine(folder, "bruno.json"));

    // ---- .bru files ---------------------------------------------------------------------------------------

    public static ImportResult ImportFolder(string folder)
    {
        var result = new ImportResult { Format = "Bruno" };
        var name = Path.GetFileName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "bruno.json")))?["name"]?.ToString() is { Length: > 0 } configured)
                name = configured;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
        }

        var collection = new RequestCollection { Name = name };
        var environmentsDir = Path.GetFullPath(Path.Combine(folder, "environments"));
        var entries = new List<(string Folder, int Seq, ApiRequest Request)>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.bru", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            var relativeDir = Path.GetRelativePath(folder, Path.GetDirectoryName(full)!).Replace('\\', '/');
            if (full.StartsWith(environmentsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                result.Environments.Add(ParseEnvironment(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file)));
                continue;
            }
            if (Path.GetFileName(file) is "folder.bru" or "collection.bru")
                continue;
            var blocks = Parse(File.ReadAllText(file));
            if (ToRequest(blocks, Path.GetFileNameWithoutExtension(file)) is not { } request)
                continue;
            request.Folder = relativeDir == "." ? "" : relativeDir;
            entries.Add((request.Folder, int.TryParse(Value(blocks, "meta", "seq"), out var seq) ? seq : int.MaxValue, request));
        }

        foreach (var (_, _, request) in entries.OrderBy(e => e.Folder, StringComparer.Ordinal).ThenBy(e => e.Seq).ThenBy(e => e.Request.Name))
            Add(collection, request);
        Warn(result, collection);
        result.Collections.Add(collection);
        return result;
    }

    public static ImportResult ImportBru(string text, string name)
    {
        var collection = new RequestCollection { Name = name };
        var result = ImportResult.Single("Bruno", collection);
        if (ToRequest(Parse(text), name) is { } request)
            Add(collection, request);
        Warn(result, collection);
        return result;
    }

    private static void Add(RequestCollection collection, ApiRequest request)
    {
        request.CollectionId = collection.Id;
        request.SortOrder = collection.Requests.Count;
        collection.Requests.Add(request);
    }

    private static void Warn(ImportResult result, RequestCollection collection)
    {
        if (collection.Requests.Any(r => r.PreRequestScript.Contains("bru.", StringComparison.Ordinal) || r.TestScript.Contains("bru.", StringComparison.Ordinal)
                                         || r.TestScript.Contains("res.", StringComparison.Ordinal)))
            result.Warnings.Add(ScriptWarning);
    }

    /// <summary>Splits a .bru file into its top-level blocks: <c>name {</c> … <c>}</c> (or <c>[</c> … <c>]</c> for lists).</summary>
    internal static Dictionary<string, string> Parse(string text)
    {
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var header = Regex.Match(lines[i], @"^([\w:.-]+)\s*([\{\[])\s*$");
            if (!header.Success)
                continue;
            var close = header.Groups[2].Value == "{" ? "}" : "]";
            var body = new List<string>();
            for (i++; i < lines.Length && lines[i].TrimEnd() != close; i++)
                body.Add(lines[i].StartsWith("  ", StringComparison.Ordinal) ? lines[i][2..] : lines[i].TrimStart());
            blocks[header.Groups[1].Value] = string.Join("\n", body).Trim('\n');
        }
        return blocks;
    }

    /// <summary><c>key: value</c> lines; a leading <c>~</c> means disabled.</summary>
    internal static List<KeyValueItem> Pairs(Dictionary<string, string> blocks, string block) =>
        !blocks.TryGetValue(block, out var body) ? [] : body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l =>
        {
            var enabled = !l.StartsWith('~');
            l = l.TrimStart('~');
            var colon = l.IndexOf(':');
            return colon < 0 ? new KeyValueItem(l, "", enabled) : new KeyValueItem(l[..colon].Trim(), l[(colon + 1)..].Trim(), enabled);
        }).Where(p => p.Key.Length > 0).ToList();

    private static string Value(Dictionary<string, string> blocks, string block, string key) =>
        Pairs(blocks, block).FirstOrDefault(p => p.Key == key)?.Value ?? "";

    private static ApiRequest? ToRequest(Dictionary<string, string> blocks, string fallbackName)
    {
        var verb = new[] { "get", "post", "put", "patch", "delete", "head", "options", "graphql" }.FirstOrDefault(blocks.ContainsKey);
        if (verb is null)
            return null;
        var name = Value(blocks, "meta", "name");
        var request = new ApiRequest
        {
            Name = name.Length > 0 ? name : fallbackName,
            Url = Value(blocks, verb, "url"),
            Method = verb == "graphql" ? HttpVerb.Post : Enum.Parse<HttpVerb>(verb, ignoreCase: true),
            Headers = Pairs(blocks, "headers"),
            Description = blocks.GetValueOrDefault("docs", ""),
            PreRequestScript = blocks.GetValueOrDefault("script:pre-request", "")
        };
        request.QueryParams = Pairs(blocks, "params:query");
        if (request.QueryParams.Count > 0)
            request.Url = QueryString.WithParams(request.Url, request.QueryParams);
        request.TestScript = string.Join("\n\n", new[] { blocks.GetValueOrDefault("script:post-response", ""), blocks.GetValueOrDefault("tests", "") }
            .Where(s => s.Length > 0));

        var bodyType = Value(blocks, verb, "body");
        if (verb == "graphql" || bodyType == "graphql")
        {
            request.Kind = RequestKind.GraphQl;
            request.Protocol.GraphQl.Query = blocks.GetValueOrDefault("body:graphql", "");
            request.Protocol.GraphQl.Variables = blocks.GetValueOrDefault("body:graphql:vars", "");
        }
        else
        {
            request.Body = bodyType switch
            {
                "json" => new RequestBody { Mode = BodyMode.Json, Content = blocks.GetValueOrDefault("body:json", "") },
                "xml" => new RequestBody { Mode = BodyMode.Xml, Content = blocks.GetValueOrDefault("body:xml", "") },
                "text" => new RequestBody { Mode = BodyMode.Text, Content = blocks.GetValueOrDefault("body:text", "") },
                "formUrlEncoded" => new RequestBody { Mode = BodyMode.FormUrlEncoded, FormFields = Pairs(blocks, "body:form-urlencoded") },
                "multipartForm" => new RequestBody
                {
                    Mode = BodyMode.Multipart,
                    FormFields = Pairs(blocks, "body:multipart-form").Select(f =>
                    {
                        var file = Regex.Match(f.Value, @"^@file\((.*)\)$");
                        if (file.Success)
                        {
                            f.Value = file.Groups[1].Value.Split('|')[0];
                            f.IsFile = true;
                        }
                        return f;
                    }).ToList()
                },
                _ => new RequestBody()
            };
        }

        request.Auth = Value(blocks, verb, "auth") switch
        {
            "bearer" => new AuthSettings { Mode = AuthMode.Bearer, Token = Value(blocks, "auth:bearer", "token") },
            "basic" => new AuthSettings { Mode = AuthMode.Basic, Username = Value(blocks, "auth:basic", "username"), Password = Value(blocks, "auth:basic", "password") },
            "digest" => new AuthSettings { Mode = AuthMode.Digest, Username = Value(blocks, "auth:digest", "username"), Password = Value(blocks, "auth:digest", "password") },
            "ntlm" => new AuthSettings
            {
                Mode = AuthMode.Ntlm, Username = Value(blocks, "auth:ntlm", "username"), Password = Value(blocks, "auth:ntlm", "password"),
                Domain = Value(blocks, "auth:ntlm", "domain")
            },
            "apikey" => new AuthSettings
            {
                Mode = AuthMode.ApiKey, ApiKeyName = Value(blocks, "auth:apikey", "key"), ApiKeyValue = Value(blocks, "auth:apikey", "value"),
                ApiKeyLocation = Value(blocks, "auth:apikey", "placement") == "queryparams" ? ApiKeyLocation.QueryParam : ApiKeyLocation.Header
            },
            "awsv4" => new AuthSettings
            {
                Mode = AuthMode.AwsSigV4, AwsAccessKey = Value(blocks, "auth:awsv4", "accessKeyId"), AwsSecretKey = Value(blocks, "auth:awsv4", "secretAccessKey"),
                AwsSessionToken = Value(blocks, "auth:awsv4", "sessionToken"), AwsRegion = Value(blocks, "auth:awsv4", "region"),
                AwsService = Value(blocks, "auth:awsv4", "service")
            },
            "oauth2" => new AuthSettings
            {
                Mode = AuthMode.OAuth2,
                OAuth2GrantType = Value(blocks, "auth:oauth2", "grant_type") switch
                {
                    "authorization_code" => OAuth2GrantType.AuthorizationCode,
                    "password" => OAuth2GrantType.Password,
                    _ => OAuth2GrantType.ClientCredentials
                },
                OAuth2TokenUrl = Value(blocks, "auth:oauth2", "access_token_url"), OAuth2AuthUrl = Value(blocks, "auth:oauth2", "authorization_url"),
                OAuth2ClientId = Value(blocks, "auth:oauth2", "client_id"), OAuth2ClientSecret = Value(blocks, "auth:oauth2", "client_secret"),
                OAuth2Scope = Value(blocks, "auth:oauth2", "scope"),
                Username = Value(blocks, "auth:oauth2", "username"), Password = Value(blocks, "auth:oauth2", "password")
            },
            _ => new AuthSettings()
        };
        return request;
    }

    internal static ApiEnvironment ParseEnvironment(string text, string name)
    {
        var blocks = Parse(text);
        var variables = Pairs(blocks, "vars");
        // Secret values are not stored in the file: keep the names so they can be filled in.
        if (blocks.TryGetValue("vars:secret", out var secrets))
            foreach (var secret in secrets.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                variables.Add(new KeyValueItem(secret.TrimStart('~'), "", !secret.StartsWith('~')));
        return new ApiEnvironment { Name = name, Variables = variables };
    }

    // ---- JSON export --------------------------------------------------------------------------------------

    public static ImportResult ImportJson(JsonNode root)
    {
        var result = new ImportResult { Format = "Bruno" };
        var collection = new RequestCollection { Name = root["name"]?.ToString() ?? "Bruno" };
        AddJsonItems(root["items"], "", collection);
        Warn(result, collection);
        result.Collections.Add(collection);

        foreach (var env in root["environments"] as JsonArray ?? [])
            if (env is not null)
                result.Environments.Add(new ApiEnvironment
                {
                    Name = env["name"]?.ToString() ?? "Environment",
                    Variables = JsonPairs(env["variables"])
                });
        return result;
    }

    private static void AddJsonItems(JsonNode? items, string folder, RequestCollection collection)
    {
        var ordered = (items as JsonArray ?? []).Where(i => i is not null).Select(i => i!)
            .OrderBy(i => i["seq"]?.GetValue<double>() ?? double.MaxValue).ToList();
        foreach (var item in ordered)
        {
            var name = item["name"]?.ToString() ?? "Request";
            var type = item["type"]?.ToString();
            if (type == "folder")
            {
                AddJsonItems(item["items"], folder.Length == 0 ? name : $"{folder}/{name}", collection);
                continue;
            }
            if (type is not ("http-request" or "graphql-request") || item["request"] is not { } r)
                continue;

            var request = new ApiRequest
            {
                Name = name,
                Folder = folder,
                Url = r["url"]?.ToString() ?? "",
                Method = Enum.TryParse<HttpVerb>(r["method"]?.ToString(), true, out var m) ? m : HttpVerb.Get,
                Headers = JsonPairs(r["headers"]),
                Description = r["docs"]?.ToString() ?? "",
                PreRequestScript = r["script"]?["req"]?.ToString() ?? "",
                TestScript = string.Join("\n\n", new[] { r["script"]?["res"]?.ToString() ?? "", r["tests"]?.ToString() ?? "" }.Where(s => s.Length > 0))
            };
            var query = JsonPairs((r["params"] as JsonArray)?.Where(p => p?["type"]?.ToString() != "path").Select(p => p?.DeepClone()).ToArray() is { } q
                ? new JsonArray(q) : null);
            if (query.Count > 0 && !request.Url.Contains('?'))
                request.Url = QueryString.WithParams(request.Url, query);
            request.QueryParams = query;

            var body = r["body"];
            if (type == "graphql-request" || body?["mode"]?.ToString() == "graphql")
            {
                request.Kind = RequestKind.GraphQl;
                request.Protocol.GraphQl.Query = body?["graphql"]?["query"]?.ToString() ?? "";
                request.Protocol.GraphQl.Variables = body?["graphql"]?["variables"]?.ToString() ?? "";
            }
            else
            {
                request.Body = body?["mode"]?.ToString() switch
                {
                    "json" => new RequestBody { Mode = BodyMode.Json, Content = body["json"]?.ToString() ?? "" },
                    "xml" => new RequestBody { Mode = BodyMode.Xml, Content = body["xml"]?.ToString() ?? "" },
                    "text" => new RequestBody { Mode = BodyMode.Text, Content = body["text"]?.ToString() ?? "" },
                    "formUrlEncoded" => new RequestBody { Mode = BodyMode.FormUrlEncoded, FormFields = JsonPairs(body["formUrlEncoded"]) },
                    "multipartForm" => new RequestBody
                    {
                        Mode = BodyMode.Multipart,
                        FormFields = (body["multipartForm"] as JsonArray ?? []).Select(p => new KeyValueItem(p?["name"]?.ToString() ?? "",
                            p?["value"] is JsonArray files ? string.Join(",", files) : p?["value"]?.ToString() ?? "",
                            p?["enabled"]?.GetValue<bool>() != false) { IsFile = p?["type"]?.ToString() == "file" }).Where(p => p.Key.Length > 0).ToList()
                    },
                    _ => new RequestBody()
                };
            }

            var auth = r["auth"];
            string A(string section, string key) => auth?[section]?[key]?.ToString() ?? "";
            request.Auth = auth?["mode"]?.ToString() switch
            {
                "bearer" => new AuthSettings { Mode = AuthMode.Bearer, Token = A("bearer", "token") },
                "basic" => new AuthSettings { Mode = AuthMode.Basic, Username = A("basic", "username"), Password = A("basic", "password") },
                "digest" => new AuthSettings { Mode = AuthMode.Digest, Username = A("digest", "username"), Password = A("digest", "password") },
                "apikey" => new AuthSettings
                {
                    Mode = AuthMode.ApiKey, ApiKeyName = A("apikey", "key"), ApiKeyValue = A("apikey", "value"),
                    ApiKeyLocation = A("apikey", "placement") == "queryparams" ? ApiKeyLocation.QueryParam : ApiKeyLocation.Header
                },
                _ => new AuthSettings()
            };
            Add(collection, request);
        }
    }

    private static List<KeyValueItem> JsonPairs(JsonNode? array) =>
        (array as JsonArray ?? []).Select(p => new KeyValueItem(p?["name"]?.ToString() ?? "", p?["value"]?.ToString() ?? "",
            p?["enabled"]?.GetValue<bool>() != false)).Where(p => p.Key.Length > 0).ToList();
}
