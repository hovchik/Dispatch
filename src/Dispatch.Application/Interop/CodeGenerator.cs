using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Auth;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Interop;

public enum CodeTarget
{
    Curl,
    HttPie,
    CSharp,
    Python,
    JavaScript,
    Go,
    Grpcurl,
    Websocat
}

/// <summary>
/// Generates code snippets that reproduce a request. Pass a variable-resolved request to get runnable code, or the
/// original to keep <c>{{placeholders}}</c>.
/// </summary>
public static class CodeGenerator
{
    public static IReadOnlyList<CodeTarget> TargetsFor(RequestKind kind) => kind switch
    {
        RequestKind.Grpc => [CodeTarget.Grpcurl],
        RequestKind.WebSocket => [CodeTarget.Websocat, CodeTarget.JavaScript],
        RequestKind.Http or RequestKind.GraphQl or RequestKind.Soap or RequestKind.Sse =>
            [CodeTarget.Curl, CodeTarget.HttPie, CodeTarget.CSharp, CodeTarget.Python, CodeTarget.JavaScript, CodeTarget.Go],
        _ => []
    };

    public static string Generate(ApiRequest request, CodeTarget target)
    {
        var http = ToHttp(request);
        return target switch
        {
            CodeTarget.Curl => ToCurl(http),
            CodeTarget.HttPie => ToHttPie(http),
            CodeTarget.CSharp => ToCSharp(http),
            CodeTarget.Python => ToPython(http),
            CodeTarget.JavaScript when request.Kind == RequestKind.WebSocket => ToJavaScriptWebSocket(request),
            CodeTarget.JavaScript => ToJavaScript(http),
            CodeTarget.Go => ToGo(http),
            CodeTarget.Grpcurl => ToGrpcurl(request),
            CodeTarget.Websocat => ToWebsocat(request),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
    }

    private sealed record HttpShape(string Method, string Url, List<KeyValuePair<string, string>> Headers, string? Body,
        List<KeyValueItem>? Form, bool Multipart, string? BinaryFile, bool Insecure);

    /// <summary>GraphQL and SOAP are sent as HTTP; flatten every HTTP-like request to method, URL, headers and body.</summary>
    private static HttpShape ToHttp(ApiRequest request)
    {
        var headers = AuthHeaders.Combined(request);
        var url = AuthHeaders.ApplyQuery(QueryString.WithParams(request.Url, request.QueryParams.Count > 0 ? request.QueryParams : QueryString.Parse(request.Url)), request.Auth);
        var method = request.Method.ToString().ToUpperInvariant();
        string? body = null;
        List<KeyValueItem>? form = null;
        var multipart = false;
        string? binary = null;

        void SetContentType(string value)
        {
            if (!headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                headers.Add(new("Content-Type", value));
        }

        switch (request.Kind)
        {
            case RequestKind.GraphQl:
                method = "POST";
                var payload = new JsonObject { ["query"] = request.Protocol.GraphQl.Query };
                if (!string.IsNullOrWhiteSpace(request.Protocol.GraphQl.Variables))
                {
                    try { payload["variables"] = JsonNode.Parse(request.Protocol.GraphQl.Variables); }
                    catch (JsonException) { payload["variables"] = request.Protocol.GraphQl.Variables; }
                }
                if (!string.IsNullOrWhiteSpace(request.Protocol.GraphQl.OperationName))
                    payload["operationName"] = request.Protocol.GraphQl.OperationName;
                body = payload.ToJsonString();
                SetContentType("application/json");
                break;
            case RequestKind.Soap:
                method = "POST";
                body = request.Body.Content;
                if (request.Protocol.Soap.Version == SoapVersion.Soap12)
                    SetContentType($"application/soap+xml; charset=utf-8; action=\"{request.Protocol.Soap.Action}\"");
                else
                {
                    SetContentType("text/xml; charset=utf-8");
                    headers.Add(new("SOAPAction", $"\"{request.Protocol.Soap.Action}\""));
                }
                break;
            case RequestKind.Sse:
                headers.Add(new("Accept", "text/event-stream"));
                break;
            default:
                switch (request.Body.Mode)
                {
                    case BodyMode.Json: body = request.Body.Content; SetContentType("application/json"); break;
                    case BodyMode.Xml: body = request.Body.Content; SetContentType("application/xml"); break;
                    case BodyMode.Text: body = request.Body.Content; SetContentType("text/plain"); break;
                    case BodyMode.FormUrlEncoded:
                        form = request.Body.FormFields.Where(f => f.IsActive).ToList();
                        SetContentType("application/x-www-form-urlencoded");
                        break;
                    case BodyMode.Multipart:
                        form = request.Body.FormFields.Where(f => f.IsActive).ToList();
                        multipart = true;
                        headers.RemoveAll(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
                        break;
                    case BodyMode.Binary:
                        binary = request.Body.FilePath;
                        break;
                }
                break;
        }
        return new HttpShape(method, url, headers, body, form, multipart, binary, !request.Settings.VerifySsl);
    }

    private static string FormEncoded(IEnumerable<KeyValueItem> form) =>
        string.Join("&", form.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));

    // ---- Shell -------------------------------------------------------------------------------------------

    private static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string ToCurl(HttpShape r)
    {
        var sb = new StringBuilder("curl");
        // curl implies GET without a body and POST with one; only spell out other methods.
        var hasBody = r.Body is not null || r.Form is not null || r.BinaryFile is not null;
        if ((hasBody && r.Method != "POST") || (!hasBody && r.Method != "GET"))
            sb.Append(" -X ").Append(r.Method);
        sb.Append(' ').Append(Sh(r.Url));
        foreach (var (k, v) in r.Headers)
            sb.Append(" \\\n  -H ").Append(Sh($"{k}: {v}"));
        if (r.Body is not null)
            sb.Append(" \\\n  --data-raw ").Append(Sh(r.Body));
        if (r.Form is not null && !r.Multipart)
            foreach (var f in r.Form)
                sb.Append(" \\\n  --data-urlencode ").Append(Sh($"{f.Key}={f.Value}"));
        if (r.Form is not null && r.Multipart)
            foreach (var f in r.Form)
                sb.Append(" \\\n  -F ").Append(Sh(f.IsFile ? $"{f.Key}=@{f.Value}" : $"{f.Key}={f.Value}"));
        if (r.BinaryFile is not null)
            sb.Append(" \\\n  --data-binary ").Append(Sh("@" + r.BinaryFile));
        if (r.Insecure)
            sb.Append(" \\\n  --insecure");
        return sb.ToString();
    }

    private static string ToHttPie(HttpShape r)
    {
        var sb = new StringBuilder("http");
        if (r.Form is not null)
            sb.Append(r.Multipart ? " --multipart" : " --form");
        if (r.Insecure)
            sb.Append(" --verify=no");
        sb.Append(' ').Append(r.Method).Append(' ').Append(Sh(r.Url));
        foreach (var (k, v) in r.Headers)
            sb.Append(" \\\n  ").Append(Sh($"{k}:{v}"));
        foreach (var f in r.Form ?? [])
            sb.Append(" \\\n  ").Append(Sh(f.IsFile ? $"{f.Key}@{f.Value}" : $"{f.Key}={f.Value}"));
        var prefix = r.Body is not null ? $"echo {Sh(r.Body)} | " : r.BinaryFile is not null ? $"cat {Sh(r.BinaryFile)} | " : "";
        return prefix + sb;
    }

    // ---- C# ----------------------------------------------------------------------------------------------

    private static string Cs(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    private static string ToCSharp(HttpShape r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using var client = new HttpClient();");
        sb.AppendLine($"using var request = new HttpRequestMessage(new HttpMethod({Cs(r.Method)}), {Cs(r.Url)});");
        string? contentType = null;
        foreach (var (k, v) in r.Headers)
        {
            if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                contentType = v;
            else
                sb.AppendLine($"request.Headers.TryAddWithoutValidation({Cs(k)}, {Cs(v)});");
        }
        if (r.Body is not null)
        {
            sb.AppendLine($"request.Content = new StringContent({Cs(r.Body)});");
            if (contentType is not null)
                sb.AppendLine($"request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse({Cs(contentType)});");
        }
        else if (r.Form is not null && !r.Multipart)
        {
            sb.AppendLine("request.Content = new FormUrlEncodedContent(new Dictionary<string, string>");
            sb.AppendLine("{");
            foreach (var f in r.Form)
                sb.AppendLine($"    [{Cs(f.Key)}] = {Cs(f.Value)},");
            sb.AppendLine("});");
        }
        else if (r.Form is not null)
        {
            sb.AppendLine("var form = new MultipartFormDataContent();");
            foreach (var f in r.Form)
                sb.AppendLine(f.IsFile
                    ? $"form.Add(new ByteArrayContent(File.ReadAllBytes({Cs(f.Value)})), {Cs(f.Key)}, {Cs(Path.GetFileName(f.Value))});"
                    : $"form.Add(new StringContent({Cs(f.Value)}), {Cs(f.Key)});");
            sb.AppendLine("request.Content = form;");
        }
        else if (r.BinaryFile is not null)
        {
            sb.AppendLine($"request.Content = new ByteArrayContent(File.ReadAllBytes({Cs(r.BinaryFile)}));");
        }
        sb.AppendLine();
        sb.AppendLine("using var response = await client.SendAsync(request);");
        sb.AppendLine("Console.WriteLine((int)response.StatusCode);");
        sb.Append("Console.WriteLine(await response.Content.ReadAsStringAsync());");
        return sb.ToString();
    }

    // ---- Python ------------------------------------------------------------------------------------------

    private static string Py(string s) => JsonSerializer.Serialize(s);

    private static string ToPython(HttpShape r)
    {
        var sb = new StringBuilder("import requests\n\n");
        sb.AppendLine($"url = {Py(r.Url)}");
        sb.AppendLine("headers = {");
        foreach (var (k, v) in r.Headers.Where(h => !(r.Multipart && h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))))
            sb.AppendLine($"    {Py(k)}: {Py(v)},");
        sb.AppendLine("}");
        var args = new List<string> { "url", "headers=headers" };
        if (r.Body is not null)
        {
            sb.AppendLine($"data = {Py(r.Body)}");
            args.Add("data=data.encode(\"utf-8\")");
        }
        else if (r.Form is not null && !r.Multipart)
        {
            sb.AppendLine("data = {");
            foreach (var f in r.Form)
                sb.AppendLine($"    {Py(f.Key)}: {Py(f.Value)},");
            sb.AppendLine("}");
            args.Add("data=data");
        }
        else if (r.Form is not null)
        {
            sb.AppendLine("files = {");
            foreach (var f in r.Form)
                sb.AppendLine(f.IsFile ? $"    {Py(f.Key)}: open({Py(f.Value)}, \"rb\")," : $"    {Py(f.Key)}: (None, {Py(f.Value)}),");
            sb.AppendLine("}");
            args.Add("files=files");
        }
        else if (r.BinaryFile is not null)
        {
            args.Add($"data=open({Py(r.BinaryFile)}, \"rb\")");
        }
        if (r.Insecure)
            args.Add("verify=False");
        sb.AppendLine();
        sb.AppendLine($"response = requests.request({Py(r.Method)}, {string.Join(", ", args)})");
        sb.AppendLine("print(response.status_code)");
        sb.Append("print(response.text)");
        return sb.ToString();
    }

    // ---- JavaScript --------------------------------------------------------------------------------------

    private static string ToJavaScript(HttpShape r)
    {
        var sb = new StringBuilder();
        string? bodyExpression = null;
        if (r.Body is not null)
            bodyExpression = Py(r.Body);
        else if (r.Form is not null && !r.Multipart)
            bodyExpression = $"new URLSearchParams({{ {string.Join(", ", r.Form.Select(f => $"{Py(f.Key)}: {Py(f.Value)}"))} }})";
        else if (r.Form is not null)
        {
            sb.AppendLine("const form = new FormData();");
            foreach (var f in r.Form)
                sb.AppendLine(f.IsFile
                    ? $"form.append({Py(f.Key)}, fileInput.files[0]); // {f.Value}"
                    : $"form.append({Py(f.Key)}, {Py(f.Value)});");
            bodyExpression = "form";
        }

        sb.AppendLine($"const response = await fetch({Py(r.Url)}, {{");
        sb.AppendLine($"  method: {Py(r.Method)},");
        var headers = r.Headers.Where(h => !(r.Multipart && h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))).ToList();
        if (headers.Count > 0)
        {
            sb.AppendLine("  headers: {");
            foreach (var (k, v) in headers)
                sb.AppendLine($"    {Py(k)}: {Py(v)},");
            sb.AppendLine("  },");
        }
        if (bodyExpression is not null)
            sb.AppendLine($"  body: {bodyExpression},");
        sb.AppendLine("});");
        sb.AppendLine("console.log(response.status);");
        sb.Append("console.log(await response.text());");
        return sb.ToString();
    }

    private static string ToJavaScriptWebSocket(ApiRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"const socket = new WebSocket({Py(request.Url)}{(string.IsNullOrWhiteSpace(request.Protocol.Stream.Subprotocols) ? "" : $", {Py(request.Protocol.Stream.Subprotocols)}")});");
        sb.AppendLine("socket.addEventListener(\"open\", () => {");
        foreach (var m in request.Protocol.Stream.InitialMessages.Where(m => m.Enabled))
            sb.AppendLine($"  socket.send({Py(m.Value)});");
        sb.AppendLine("});");
        sb.Append("socket.addEventListener(\"message\", (event) => console.log(event.data));");
        return sb.ToString();
    }

    // ---- Go ----------------------------------------------------------------------------------------------

    private static string Go(string s) => "`" + s.Replace("`", "` + \"`\" + `") + "`";

    private static string ToGo(HttpShape r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("package main");
        sb.AppendLine();
        sb.AppendLine("import (");
        sb.AppendLine("\t\"fmt\"");
        sb.AppendLine("\t\"io\"");
        sb.AppendLine("\t\"net/http\"");
        if (r.Body is not null || r.Form is not null)
            sb.AppendLine("\t\"strings\"");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("func main() {");
        var body = "nil";
        if (r.Body is not null)
            body = $"strings.NewReader({Go(r.Body)})";
        else if (r.Form is not null && !r.Multipart)
            body = $"strings.NewReader({Go(FormEncoded(r.Form))})";
        else if (r.Form is not null)
            body = "strings.NewReader(\"\") // build a multipart body with mime/multipart";
        sb.AppendLine($"\treq, err := http.NewRequest({Py(r.Method)}, {Py(r.Url)}, {body})");
        sb.AppendLine("\tif err != nil {\n\t\tpanic(err)\n\t}");
        foreach (var (k, v) in r.Headers)
            sb.AppendLine($"\treq.Header.Set({Py(k)}, {Py(v)})");
        sb.AppendLine();
        sb.AppendLine("\tres, err := http.DefaultClient.Do(req)");
        sb.AppendLine("\tif err != nil {\n\t\tpanic(err)\n\t}");
        sb.AppendLine("\tdefer res.Body.Close()");
        sb.AppendLine("\tbody, _ := io.ReadAll(res.Body)");
        sb.AppendLine("\tfmt.Println(res.StatusCode)");
        sb.AppendLine("\tfmt.Println(string(body))");
        sb.Append('}');
        return sb.ToString();
    }

    // ---- gRPC / WebSocket CLIs ---------------------------------------------------------------------------

    private static string ToGrpcurl(ApiRequest request)
    {
        var g = request.Protocol.Grpc;
        var sb = new StringBuilder("grpcurl");
        if (!g.UseTls && !request.Url.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            sb.Append(" -plaintext");
        if (!request.Settings.VerifySsl)
            sb.Append(" -insecure");
        if (g.SchemaSource == GrpcSchemaSource.ProtoFiles)
        {
            foreach (var path in g.ImportPaths)
                sb.Append(" -import-path ").Append(Sh(path));
            foreach (var proto in g.ProtoFiles)
                sb.Append(" -proto ").Append(Sh(proto));
        }
        foreach (var (k, v) in AuthHeaders.Combined(request))
            sb.Append(" \\\n  -H ").Append(Sh($"{k}: {v}"));
        if (g.DeadlineSeconds > 0)
            sb.Append(" -max-time ").Append(g.DeadlineSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(g.Message))
            sb.Append(" \\\n  -d ").Append(Sh(g.Message));
        var host = request.Url.Replace("http://", "").Replace("https://", "").Replace("grpc://", "").TrimEnd('/');
        sb.Append(" \\\n  ").Append(host).Append(' ').Append(g.Service).Append('/').Append(g.Method);
        return sb.ToString();
    }

    private static string ToWebsocat(ApiRequest request)
    {
        var sb = new StringBuilder("websocat");
        foreach (var (k, v) in AuthHeaders.Combined(request))
            sb.Append(" -H ").Append(Sh($"{k}: {v}"));
        if (!string.IsNullOrWhiteSpace(request.Protocol.Stream.Subprotocols))
            sb.Append(" --protocol ").Append(Sh(request.Protocol.Stream.Subprotocols));
        sb.Append(' ').Append(Sh(request.Url));
        var initial = request.Protocol.Stream.InitialMessages.Where(m => m.Enabled).Select(m => m.Value).ToList();
        return initial.Count == 0 ? sb.ToString() : $"printf '%s\\n' {string.Join(" ", initial.Select(Sh))} | {sb}";
    }
}
