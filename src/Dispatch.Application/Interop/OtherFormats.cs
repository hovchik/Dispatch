using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Interop;

/// <summary>Insomnia v4 exports (JSON).</summary>
public static class Insomnia
{
    public static bool IsExport(JsonNode root) => root["_type"]?.ToString() == "export" && root["resources"] is JsonArray;

    public static ImportResult Import(JsonNode root)
    {
        var result = new ImportResult { Format = "Insomnia" };
        var resources = (root["resources"] as JsonArray ?? []).Where(r => r is not null).Select(r => r!).ToList();
        var byId = resources.Where(r => r["_id"] is not null).ToDictionary(r => r["_id"]!.ToString(), r => r);

        foreach (var workspace in resources.Where(r => r["_type"]?.ToString() == "workspace"))
        {
            var collection = new RequestCollection { Name = workspace["name"]?.ToString() ?? "Insomnia", Description = workspace["description"]?.ToString() ?? "" };
            var workspaceId = workspace["_id"]!.ToString();
            var order = 0;

            foreach (var r in resources.Where(r => r["_type"]?.ToString() is "request" or "grpc_request" or "websocket_request"
                                                   && BelongsTo(r, workspaceId, byId)))
            {
                var type = r["_type"]!.ToString();
                var request = new ApiRequest
                {
                    Name = r["name"]?.ToString() ?? "Request",
                    Folder = FolderPath(r, workspaceId, byId),
                    CollectionId = collection.Id,
                    SortOrder = order++,
                    Url = ConvertTemplates(r["url"]?.ToString() ?? ""),
                    Description = r["description"]?.ToString() ?? "",
                    Headers = Pairs(r["headers"]),
                    Kind = type switch { "grpc_request" => RequestKind.Grpc, "websocket_request" => RequestKind.WebSocket, _ => RequestKind.Http }
                };
                request.Method = Enum.TryParse<HttpVerb>(r["method"]?.ToString(), true, out var m) ? m : HttpVerb.Get;
                request.QueryParams = Pairs(r["parameters"]);
                if (request.QueryParams.Count > 0)
                    request.Url = QueryString.WithParams(request.Url, request.QueryParams);

                var body = r["body"];
                var mime = body?["mimeType"]?.ToString() ?? "";
                if (mime == "application/graphql")
                {
                    request.Kind = RequestKind.GraphQl;
                    var gql = JsonNode.Parse(body?["text"]?.ToString() ?? "{}");
                    request.Protocol.GraphQl.Query = gql?["query"]?.ToString() ?? "";
                    request.Protocol.GraphQl.Variables = gql?["variables"]?.ToJsonString() ?? "";
                }
                else if (mime is "application/x-www-form-urlencoded")
                    request.Body = new RequestBody { Mode = BodyMode.FormUrlEncoded, FormFields = Pairs(body?["params"]) };
                else if (mime is "multipart/form-data")
                    request.Body = new RequestBody
                    {
                        Mode = BodyMode.Multipart,
                        FormFields = (body?["params"] as JsonArray ?? []).Select(p => new KeyValueItem(p?["name"]?.ToString() ?? "",
                            p?["type"]?.ToString() == "file" ? p["fileName"]?.ToString() ?? "" : ConvertTemplates(p?["value"]?.ToString() ?? ""),
                            p?["disabled"]?.GetValue<bool>() != true) { IsFile = p?["type"]?.ToString() == "file" }).ToList()
                    };
                else if (body?["text"] is { } text)
                    request.Body = new RequestBody
                    {
                        Mode = mime.Contains("json") ? BodyMode.Json : mime.Contains("xml") ? BodyMode.Xml : BodyMode.Text,
                        Content = ConvertTemplates(text.ToString())
                    };

                if (type == "grpc_request")
                {
                    request.Protocol.Grpc.Message = body?["text"]?.ToString() ?? "{}";
                    var method = r["protoMethodName"]?.ToString()?.Trim('/') ?? "";
                    var slash = method.LastIndexOf('/');
                    if (slash > 0)
                    {
                        request.Protocol.Grpc.Service = method[..slash];
                        request.Protocol.Grpc.Method = method[(slash + 1)..];
                    }
                }

                request.Auth = ImportAuth(r["authentication"]);
                collection.Requests.Add(request);
            }
            result.Collections.Add(collection);

            foreach (var env in resources.Where(e => e["_type"]?.ToString() == "environment" && BelongsTo(e, workspaceId, byId)))
            {
                if (env["data"] is not JsonObject data || data.Count == 0)
                    continue;
                result.Environments.Add(new ApiEnvironment
                {
                    Name = env["name"]?.ToString() ?? "Environment",
                    Variables = data.Select(kv => new KeyValueItem(kv.Key, kv.Value is JsonValue v ? v.ToString() : kv.Value?.ToJsonString() ?? "")).ToList()
                });
            }
        }
        return result;
    }

    /// <summary>Insomnia uses Nunjucks: {{ _.baseUrl }} → {{baseUrl}}.</summary>
    internal static string ConvertTemplates(string text) =>
        Regex.Replace(text, @"\{\{\s*_\.([\w.-]+)\s*\}\}", "{{$1}}");

    private static bool BelongsTo(JsonNode resource, string workspaceId, Dictionary<string, JsonNode> byId)
    {
        var parent = resource["parentId"]?.ToString();
        for (var guard = 0; parent is not null && guard < 64; guard++)
        {
            if (parent == workspaceId)
                return true;
            parent = byId.TryGetValue(parent, out var p) ? p["parentId"]?.ToString() : null;
        }
        return false;
    }

    private static string FolderPath(JsonNode resource, string workspaceId, Dictionary<string, JsonNode> byId)
    {
        var parts = new List<string>();
        var parent = resource["parentId"]?.ToString();
        for (var guard = 0; parent is not null && parent != workspaceId && guard < 64; guard++)
        {
            if (!byId.TryGetValue(parent, out var group))
                break;
            parts.Insert(0, group["name"]?.ToString() ?? "Folder");
            parent = group["parentId"]?.ToString();
        }
        return string.Join("/", parts);
    }

    private static List<KeyValueItem> Pairs(JsonNode? array) =>
        (array as JsonArray ?? []).Select(p => new KeyValueItem(p?["name"]?.ToString() ?? "", ConvertTemplates(p?["value"]?.ToString() ?? ""),
            p?["disabled"]?.GetValue<bool>() != true)).Where(p => p.Key.Length > 0).ToList();

    private static AuthSettings ImportAuth(JsonNode? auth)
    {
        string S(string key) => ConvertTemplates(auth?[key]?.ToString() ?? "");
        return auth?["type"]?.ToString() switch
        {
            "bearer" => new AuthSettings { Mode = AuthMode.Bearer, Token = S("token") },
            "basic" => new AuthSettings { Mode = AuthMode.Basic, Username = S("username"), Password = S("password") },
            "digest" => new AuthSettings { Mode = AuthMode.Digest, Username = S("username"), Password = S("password") },
            "ntlm" => new AuthSettings { Mode = AuthMode.Ntlm, Username = S("username"), Password = S("password") },
            "apikey" => new AuthSettings
            {
                Mode = AuthMode.ApiKey, ApiKeyName = S("key"), ApiKeyValue = S("value"),
                ApiKeyLocation = S("addTo") == "queryParams" ? ApiKeyLocation.QueryParam : ApiKeyLocation.Header
            },
            "iam" => new AuthSettings
            {
                Mode = AuthMode.AwsSigV4, AwsAccessKey = S("accessKeyId"), AwsSecretKey = S("secretAccessKey"),
                AwsSessionToken = S("sessionToken"), AwsRegion = S("region"), AwsService = S("service")
            },
            "oauth2" => new AuthSettings
            {
                Mode = AuthMode.OAuth2,
                OAuth2GrantType = S("grantType") switch
                {
                    "authorization_code" => OAuth2GrantType.AuthorizationCode,
                    "password" => OAuth2GrantType.Password,
                    _ => OAuth2GrantType.ClientCredentials
                },
                OAuth2TokenUrl = S("accessTokenUrl"), OAuth2AuthUrl = S("authorizationUrl"), OAuth2ClientId = S("clientId"),
                OAuth2ClientSecret = S("clientSecret"), OAuth2Scope = S("scope"), OAuth2UsePkce = auth?["usePkce"]?.GetValue<bool>() == true,
                Username = S("username"), Password = S("password")
            },
            _ => new AuthSettings()
        };
    }
}

/// <summary>HTTP Archive (HAR 1.2) files saved from browser dev tools.</summary>
public static class Har
{
    public static bool IsHar(JsonNode root) => root["log"]?["entries"] is JsonArray;

    public static ImportResult Import(JsonNode root, bool skipStaticAssets = true)
    {
        var collection = new RequestCollection { Name = "HAR import " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        var order = 0;
        foreach (var entry in root["log"]!["entries"]!.AsArray())
        {
            var r = entry?["request"];
            if (r is null)
                continue;
            var url = r["url"]?.ToString() ?? "";
            var resourceType = entry?["_resourceType"]?.ToString();
            if (skipStaticAssets && (resourceType is "image" or "stylesheet" or "font" or "media" or "script"
                                     || Regex.IsMatch(url, @"\.(png|jpe?g|gif|svg|ico|css|js|woff2?|ttf|map)(\?|$)", RegexOptions.IgnoreCase)))
                continue;

            var request = new ApiRequest
            {
                Name = $"{r["method"]} {new Uri(url, UriKind.RelativeOrAbsolute).GetComponentsSafe()}",
                CollectionId = collection.Id,
                SortOrder = order++,
                Method = Enum.TryParse<HttpVerb>(r["method"]?.ToString(), true, out var m) ? m : HttpVerb.Get,
                Url = url,
                QueryParams = QueryString.Parse(url).ToList(),
                Headers = (r["headers"] as JsonArray ?? [])
                    .Select(h => new KeyValueItem(h?["name"]?.ToString() ?? "", h?["value"]?.ToString() ?? ""))
                    // HTTP/2 pseudo-headers and hop-by-hop headers are set by the client, not the user.
                    .Where(h => h.Key.Length > 0 && !h.Key.StartsWith(':') && !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                                && !h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    .ToList()
            };

            var post = r["postData"];
            var mime = post?["mimeType"]?.ToString() ?? "";
            if (post?["params"] is JsonArray { Count: > 0 } parameters)
            {
                request.Body = new RequestBody
                {
                    Mode = mime.StartsWith("multipart", StringComparison.OrdinalIgnoreCase) ? BodyMode.Multipart : BodyMode.FormUrlEncoded,
                    FormFields = parameters.Select(p => new KeyValueItem(p?["name"]?.ToString() ?? "", p?["value"]?.ToString() ?? p?["fileName"]?.ToString() ?? "")
                        { IsFile = p?["fileName"] is not null }).ToList()
                };
            }
            else if (post?["text"] is { } text)
            {
                request.Body = new RequestBody
                {
                    Mode = mime.Contains("json") ? BodyMode.Json : mime.Contains("xml") ? BodyMode.Xml
                        : mime.Contains("x-www-form-urlencoded") ? BodyMode.FormUrlEncoded : BodyMode.Text,
                    Content = text.ToString()
                };
                if (request.Body.Mode == BodyMode.FormUrlEncoded)
                    request.Body.FormFields = QueryString.Parse("?" + request.Body.Content).ToList();
            }

            if (entry?["response"] is { } response && response["status"]?.GetValue<int>() is int status and > 0)
            {
                request.Examples.Add(new ResponseExample
                {
                    Name = "Recorded",
                    StatusCode = status,
                    ContentType = response["content"]?["mimeType"]?.ToString() ?? "",
                    Body = response["content"]?["text"]?.ToString() ?? ""
                });
            }
            collection.Requests.Add(request);
        }
        return ImportResult.Single("HAR", collection);
    }

    private static string GetComponentsSafe(this Uri uri) =>
        uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?')[0];
}

/// <summary>
/// <c>.http</c> / <c>.rest</c> files (JetBrains HTTP Client, VS Code REST Client, Visual Studio): requests separated by
/// <c>###</c>, file variables (<c>@name = value</c>), <c># @name</c> request names, headers and bodies.
/// </summary>
public static class HttpFile
{
    private static readonly Regex RequestLine = new(@"^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS|CONNECT|TRACE)\s+(\S+)(\s+HTTP/[\d.]+)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksLikeHttpFile(string text) =>
        text.Split('\n').Any(l => RequestLine.IsMatch(l.Trim())) && !text.TrimStart().StartsWith('{') && !Curl.LooksLikeCurl(text);

    public static ImportResult Import(string text, string name = "HTTP file")
    {
        var collection = new RequestCollection { Name = name };
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<List<string>> { new() };
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("###", StringComparison.Ordinal))
            {
                blocks.Add([line]);
                continue;
            }
            blocks[^1].Add(line);
        }

        var order = 0;
        foreach (var block in blocks)
        {
            string? requestName = null;
            var i = 0;
            // Separator line may carry a name: "### Get users".
            if (block.Count > 0 && block[0].TrimStart().StartsWith("###", StringComparison.Ordinal))
            {
                var title = block[0].Trim().TrimStart('#').Trim();
                if (title.Length > 0)
                    requestName = title;
                i = 1;
            }

            // Preamble: comments, @name, file variables.
            for (; i < block.Count; i++)
            {
                var line = block[i].Trim();
                if (line.Length == 0)
                    continue;
                var variable = Regex.Match(line, @"^@([\w.-]+)\s*=\s*(.*)$");
                if (variable.Success)
                {
                    collection.Variables.Add(new KeyValueItem(variable.Groups[1].Value, variable.Groups[2].Value.Trim()));
                    continue;
                }
                var nameTag = Regex.Match(line, @"^(#|//)\s*@name\s+(.+)$");
                if (nameTag.Success)
                {
                    requestName = nameTag.Groups[2].Value.Trim();
                    continue;
                }
                if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                break;
            }
            if (i >= block.Count)
                continue;

            var match = RequestLine.Match(block[i].Trim());
            string method, url;
            if (match.Success)
            {
                method = match.Groups[1].Value;
                url = match.Groups[2].Value;
            }
            else if (block[i].Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                method = "GET";
                url = block[i].Trim();
            }
            else
            {
                continue;
            }
            i++;

            // Continuation lines of the URL (query on following lines starting with ? or &).
            while (i < block.Count && block[i].TrimStart() is var cont && (cont.StartsWith('?') || cont.StartsWith('&')))
            {
                url += cont.Trim();
                i++;
            }

            var request = new ApiRequest
            {
                Method = Enum.TryParse<HttpVerb>(method, true, out var verb) ? verb : HttpVerb.Get,
                Url = url,
                QueryParams = QueryString.Parse(url).ToList(),
                CollectionId = collection.Id,
                SortOrder = order++
            };

            for (; i < block.Count && block[i].Trim().Length > 0; i++)
            {
                var header = block[i];
                var colon = header.IndexOf(':');
                if (colon > 0)
                    request.Headers.Add(new KeyValueItem(header[..colon].Trim(), header[(colon + 1)..].Trim()));
            }

            var bodyLines = block.Skip(i + 1)
                .TakeWhile(l => !l.TrimStart().StartsWith("> {%", StringComparison.Ordinal) && !l.TrimStart().StartsWith("<> ", StringComparison.Ordinal))
                .ToList();
            var body = string.Join("\n", bodyLines).Trim();
            if (body.Length > 0)
            {
                var contentType = request.Headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
                request.Body = new RequestBody
                {
                    Mode = contentType.Contains("json") || body.StartsWith('{') || body.StartsWith('[') ? BodyMode.Json
                        : contentType.Contains("xml") || body.StartsWith('<') ? BodyMode.Xml : BodyMode.Text,
                    Content = body
                };
                if (contentType.Contains("x-www-form-urlencoded"))
                {
                    request.Body.Mode = BodyMode.FormUrlEncoded;
                    request.Body.FormFields = QueryString.Parse("?" + body.Replace("\n", "")).ToList();
                }
                if (contentType.Contains("graphql") || body.StartsWith("query ", StringComparison.Ordinal) && request.Headers.Any(h => h.Key.Equals("X-REQUEST-TYPE", StringComparison.OrdinalIgnoreCase)))
                {
                    request.Kind = RequestKind.GraphQl;
                    request.Protocol.GraphQl.Query = body;
                }
            }

            request.Name = requestName ?? $"{request.Method.ToString().ToUpperInvariant()} {url}";
            collection.Requests.Add(request);
        }
        return ImportResult.Single(".http", collection);
    }

    public static string Export(RequestCollection collection)
    {
        var sb = new StringBuilder();
        foreach (var v in collection.Variables.Where(v => v.IsActive && !v.IsSecret))
            sb.Append('@').Append(v.Key).Append(" = ").AppendLine(v.Value);
        if (collection.Variables.Count > 0)
            sb.AppendLine();

        foreach (var r in collection.Requests.OrderBy(r => r.SortOrder).Where(r => r.Kind is RequestKind.Http or RequestKind.GraphQl or RequestKind.Soap))
        {
            sb.Append("### ").AppendLine(r.Name);
            sb.Append(r.Kind == RequestKind.Http ? r.Method.ToString().ToUpperInvariant() : "POST").Append(' ').Append(r.Url).AppendLine(" HTTP/1.1");
            foreach (var (k, v) in Auth.AuthHeaders.Combined(r))
                sb.Append(k).Append(": ").AppendLine(v);
            string? body = r.Kind switch
            {
                RequestKind.GraphQl => new JsonObject
                {
                    ["query"] = r.Protocol.GraphQl.Query,
                    ["variables"] = string.IsNullOrWhiteSpace(r.Protocol.GraphQl.Variables) ? null : JsonNode.Parse(r.Protocol.GraphQl.Variables)
                }.ToJsonString(),
                _ => r.Body.Mode switch
                {
                    BodyMode.Json or BodyMode.Xml or BodyMode.Text => r.Body.Content,
                    BodyMode.FormUrlEncoded => string.Join("&", r.Body.FormFields.Where(f => f.IsActive).Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}")),
                    _ => null
                }
            };
            var contentType = r.Kind == RequestKind.GraphQl ? "application/json"
                : r.Kind == RequestKind.Soap ? "text/xml; charset=utf-8" : DefaultHeaders.ContentTypeFor(r.Body.Mode);
            if (body is not null && contentType is not null && !r.Headers.Any(h => h.IsActive && h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                sb.Append("Content-Type: ").AppendLine(contentType);
            if (r.Kind == RequestKind.Soap && r.Protocol.Soap.Version == SoapVersion.Soap11)
                sb.Append("SOAPAction: \"").Append(r.Protocol.Soap.Action).AppendLine("\"");
            if (body is not null)
                sb.AppendLine().AppendLine(body);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
