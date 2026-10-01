using System.Net;
using System.Text;
using Dispatch.Application.Formatting;
using Dispatch.Application.Interop;
using Dispatch.Application.Testing;
using Dispatch.Domain;

namespace Dispatch.Application.Docs;

public sealed class DocsOptions
{
    /// <summary>Show a cURL / grpcurl / websocat sample for each request.</summary>
    public bool IncludeCodeSamples { get; init; } = true;
    public bool IncludeExamples { get; init; } = true;
    public bool IncludeTests { get; init; } = true;
}

/// <summary>
/// API reference documentation for a collection: a single self-contained HTML page (sidebar, search, light/dark theme)
/// or Markdown. Descriptions are Markdown. Secret variables are never printed.
/// </summary>
public static class DocsGenerator
{
    // ---- HTML ----------------------------------------------------------------------------------------

    public static string Html(RequestCollection collection, DocsOptions? options = null)
    {
        options ??= new DocsOptions();
        var groups = Groups(collection);
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append($"<title>{E(collection.Name)} · API reference</title>\n<style>{Css}</style>\n</head>\n<body>\n");

        // Sidebar
        sb.Append("<nav id=\"nav\"><div class=\"brand\">").Append(E(collection.Name)).Append("</div>\n")
            .Append("<input id=\"search\" type=\"search\" placeholder=\"Search endpoints…\" autocomplete=\"off\">\n<ul>\n");
        foreach (var (folder, requests) in groups)
        {
            if (folder.Length > 0)
                sb.Append($"<li class=\"folder\">{E(folder)}</li>\n");
            foreach (var r in requests)
                sb.Append($"<li class=\"item\" data-search=\"{E((r.Name + " " + r.Url + " " + folder).ToLowerInvariant())}\"><a href=\"#{Anchor(r)}\">")
                    .Append($"<span class=\"badge {BadgeClass(r)}\">{E(Badge(r))}</span> {E(r.Name)}</a></li>\n");
        }
        sb.Append("</ul></nav>\n<main>\n");

        // Overview
        sb.Append($"<header><h1>{E(collection.Name)}</h1>\n");
        sb.Append($"<p class=\"meta\">{collection.Requests.Count} endpoint(s) · generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC by Dispatch</p>\n");
        if (collection.Description.Length > 0)
            sb.Append("<div class=\"md\">").Append(Markdown.ToHtml(collection.Description)).Append("</div>\n");
        var variables = collection.Variables.Where(v => v.IsActive).ToList();
        if (variables.Count > 0)
        {
            sb.Append("<h3>Variables</h3>\n<table><thead><tr><th>Name</th><th>Default value</th></tr></thead><tbody>\n");
            foreach (var v in variables)
                sb.Append($"<tr><td><code>{{{{{E(v.Key)}}}}}</code></td><td>{(v.IsSecret ? "<em>secret</em>" : $"<code>{E(v.Value)}</code>")}</td></tr>\n");
            sb.Append("</tbody></table>\n");
        }
        if (collection.SpecLocation.Length > 0)
            sb.Append($"<p class=\"meta\">OpenAPI document: <code>{E(collection.SpecLocation)}</code></p>\n");
        sb.Append("</header>\n");

        foreach (var (folder, requests) in groups)
        {
            if (folder.Length > 0)
                sb.Append($"<h2 class=\"group\">{E(folder)}</h2>\n");
            foreach (var r in requests)
                AppendRequest(sb, r, options, collection.Requests);
        }

        sb.Append("</main>\n<script>").Append(Script).Append("</script>\n</body></html>\n");
        return sb.ToString();
    }

    /// <summary>Enabled assertions and message checks, described in plain language.</summary>
    private static List<string> Checks(ApiRequest r, IReadOnlyList<ApiRequest> all) =>
        r.Assertions.Where(a => a.Enabled).Select(AssertionEvaluator.Describe)
            .Concat(r.Expectations.Where(e => e.Enabled).Select(e =>
                Consequences.ConsequenceSession.Describe(e, all.FirstOrDefault(x => x.Id == e.ListenerId)?.Name ?? "(missing listener)")))
            .ToList();

    private static void AppendRequest(StringBuilder sb, ApiRequest r, DocsOptions options, IReadOnlyList<ApiRequest> all)
    {
        sb.Append($"<section class=\"endpoint\" id=\"{Anchor(r)}\">\n");
        sb.Append($"<h3><span class=\"badge {BadgeClass(r)}\">{E(Badge(r))}</span> {E(r.Name)}</h3>\n");
        sb.Append($"<pre class=\"url\">{HighlightVariables(r.Url)}</pre>\n");
        if (r.Description.Length > 0)
            sb.Append("<div class=\"md\">").Append(Markdown.ToHtml(r.Description)).Append("</div>\n");

        switch (r.Kind)
        {
            case RequestKind.GraphQl:
                Code(sb, "Query", r.Protocol.GraphQl.Query, "graphql");
                if (r.Protocol.GraphQl.Variables.Trim().Length > 0)
                    Code(sb, "Variables", BodyFormatter.Pretty(r.Protocol.GraphQl.Variables, BodyFormat.Json), "json");
                break;
            case RequestKind.Grpc:
                sb.Append($"<p><strong>Method</strong> <code>{E(r.Protocol.Grpc.Service)}/{E(r.Protocol.Grpc.Method)}</code>{(r.Protocol.Grpc.UseTls ? " · TLS" : "")}</p>\n");
                Code(sb, "Message", BodyFormatter.Pretty(r.Protocol.Grpc.Message, BodyFormat.Json), "json");
                break;
            case RequestKind.Soap:
                if (r.Protocol.Soap.Action.Length > 0)
                    sb.Append($"<p><strong>SOAPAction</strong> <code>{E(r.Protocol.Soap.Action)}</code></p>\n");
                break;
            case RequestKind.Mqtt:
                sb.Append($"<p><strong>Topic</strong> <code>{E(r.Protocol.Mqtt.Topic)}</code> · QoS {r.Protocol.Mqtt.Qos}</p>\n");
                Code(sb, "Payload", r.Protocol.Mqtt.Payload, "");
                break;
            case RequestKind.Kafka:
                sb.Append($"<p><strong>Topic</strong> <code>{E(r.Protocol.Kafka.Topic)}</code></p>\n");
                Code(sb, "Payload", r.Protocol.Kafka.Payload, "");
                break;
            case RequestKind.Amqp:
                sb.Append($"<p><strong>Exchange</strong> <code>{E(r.Protocol.Amqp.Exchange)}</code> · routing key <code>{E(r.Protocol.Amqp.RoutingKey)}</code></p>\n");
                Code(sb, "Payload", r.Protocol.Amqp.Payload, "");
                break;
            case RequestKind.SocketIo:
                sb.Append($"<p><strong>Event</strong> <code>{E(r.Protocol.SocketIo.Event)}</code> · namespace <code>{E(r.Protocol.SocketIo.Namespace)}</code></p>\n");
                break;
        }

        Table(sb, "Query parameters", r.QueryParams);
        Table(sb, r.Kind == RequestKind.Grpc ? "Metadata" : "Headers", r.Headers);
        if (r.Auth.Mode != AuthMode.None)
            sb.Append($"<p><strong>Auth</strong> {E(AuthText(r.Auth))}</p>\n");

        if (r.Kind is RequestKind.Http or RequestKind.Soap)
        {
            switch (r.Body.Mode)
            {
                case BodyMode.Json or BodyMode.Xml or BodyMode.Text when r.Body.Content.Trim().Length > 0:
                    var format = r.Body.Mode == BodyMode.Json ? BodyFormat.Json : r.Body.Mode == BodyMode.Xml ? BodyFormat.Xml : BodyFormat.Text;
                    Code(sb, $"Body ({r.Body.Mode.ToString().ToLowerInvariant()})", BodyFormatter.Pretty(r.Body.Content, format), format.ToString().ToLowerInvariant());
                    break;
                case BodyMode.FormUrlEncoded or BodyMode.Multipart:
                    Table(sb, r.Body.Mode == BodyMode.Multipart ? "Body (multipart form)" : "Body (form)", r.Body.FormFields);
                    break;
                case BodyMode.Binary:
                    sb.Append("<p><strong>Body</strong> binary file</p>\n");
                    break;
            }
        }

        if (options.IncludeTests && Checks(r, all) is { Count: > 0 } checks)
        {
            sb.Append("<h4>Checks</h4>\n<ul class=\"checks\">\n");
            foreach (var check in checks)
                sb.Append("<li>").Append(E(check)).Append("</li>\n");
            sb.Append("</ul>\n");
        }

        if (options.IncludeExamples && r.Examples.Count > 0)
        {
            sb.Append("<h4>Responses</h4>\n");
            foreach (var example in r.Examples)
            {
                sb.Append($"<details{(example == r.Examples[0] ? " open" : "")}><summary><span class=\"status s{example.StatusCode / 100}\">{example.StatusCode}</span> {E(example.Name)}")
                    .Append(example.ContentType.Length > 0 ? $" <span class=\"meta\">{E(example.ContentType)}</span>" : "").Append("</summary>\n");
                if (example.Body.Length > 0)
                    sb.Append("<pre><code>").Append(E(BodyFormatter.Pretty(example.Body, example.ContentType))).Append("</code></pre>\n");
                sb.Append("</details>\n");
            }
        }

        if (options.IncludeCodeSamples && CodeGenerator.TargetsFor(r.Kind) is { Count: > 0 } targets)
        {
            var sample = SafeGenerate(r, targets[0]);
            if (sample.Length > 0)
                Code(sb, $"Example ({targets[0].ToString().ToLowerInvariant()})", sample, "shell");
        }
        sb.Append("</section>\n");
    }

    private static string SafeGenerate(ApiRequest r, CodeTarget target)
    {
        try
        {
            var copy = r.Clone();
            // Never leak credentials into published docs: show placeholders instead.
            copy.Auth = MaskAuth(copy.Auth);
            return CodeGenerator.Generate(copy, target);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or UriFormatException)
        {
            return "";
        }
    }

    private static AuthSettings MaskAuth(AuthSettings auth)
    {
        var masked = auth.Clone();
        static string Mask(string value) => value.Length == 0 || value.StartsWith("{{") ? value : "<redacted>";
        masked.Token = Mask(masked.Token);
        masked.Password = Mask(masked.Password);
        masked.ApiKeyValue = Mask(masked.ApiKeyValue);
        masked.OAuth2ClientSecret = Mask(masked.OAuth2ClientSecret);
        masked.OAuth2AccessToken = "";
        masked.OAuth2RefreshToken = "";
        masked.AwsSecretKey = Mask(masked.AwsSecretKey);
        return masked;
    }

    private static void Code(StringBuilder sb, string title, string code, string language)
    {
        if (code.Trim().Length == 0)
            return;
        sb.Append($"<h4>{E(title)}</h4>\n<pre><code{(language.Length > 0 ? $" class=\"language-{language}\"" : "")}>{E(code)}</code></pre>\n");
    }

    private static void Table(StringBuilder sb, string title, IEnumerable<KeyValueItem> rows)
    {
        var active = rows.Where(r => !string.IsNullOrWhiteSpace(r.Key)).ToList();
        if (active.Count == 0)
            return;
        sb.Append($"<h4>{E(title)}</h4>\n<table><thead><tr><th>Name</th><th>Value</th><th></th></tr></thead><tbody>\n");
        foreach (var row in active)
            sb.Append($"<tr><td><code>{E(row.Key)}</code></td><td>{(row.IsSecret ? "<em>secret</em>" : row.IsFile ? "<em>file</em>" : HighlightVariables(row.Value))}</td>")
                .Append($"<td class=\"meta\">{(row.Enabled ? "" : "optional")}</td></tr>\n");
        sb.Append("</tbody></table>\n");
    }

    private static string HighlightVariables(string text) =>
        System.Text.RegularExpressions.Regex.Replace(E(text), @"\{\{[^{}]+\}\}", m => $"<span class=\"var\">{m.Value}</span>");

    // ---- Markdown ------------------------------------------------------------------------------------

    public static string MarkdownText(RequestCollection collection, DocsOptions? options = null)
    {
        options ??= new DocsOptions();
        var sb = new StringBuilder();
        sb.Append($"# {collection.Name}\n\n");
        if (collection.Description.Length > 0)
            sb.Append(collection.Description.Trim()).Append("\n\n");

        var variables = collection.Variables.Where(v => v.IsActive).ToList();
        if (variables.Count > 0)
        {
            sb.Append("## Variables\n\n| Name | Default value |\n| --- | --- |\n");
            foreach (var v in variables)
                sb.Append($"| `{{{{{v.Key}}}}}` | {(v.IsSecret ? "_secret_" : $"`{Cell(v.Value)}`")} |\n");
            sb.Append('\n');
        }

        sb.Append("## Endpoints\n\n");
        foreach (var (folder, requests) in Groups(collection))
            foreach (var r in requests)
                sb.Append($"- [{Badge(r)} {r.Name}](#{Markdown.Slug(Badge(r) + " " + r.Name)})\n");
        sb.Append('\n');

        foreach (var (folder, requests) in Groups(collection))
        {
            if (folder.Length > 0)
                sb.Append($"## {folder}\n\n");
            foreach (var r in requests)
            {
                sb.Append($"### {Badge(r)} {r.Name}\n\n```\n{r.Url}\n```\n\n");
                if (r.Description.Length > 0)
                    sb.Append(r.Description.Trim()).Append("\n\n");
                MdTable(sb, "Query parameters", r.QueryParams);
                MdTable(sb, "Headers", r.Headers);
                if (r.Auth.Mode != AuthMode.None)
                    sb.Append($"**Auth:** {AuthText(r.Auth)}\n\n");
                if (r.Kind == RequestKind.GraphQl && r.Protocol.GraphQl.Query.Length > 0)
                    sb.Append($"**Query**\n\n```graphql\n{r.Protocol.GraphQl.Query.Trim()}\n```\n\n");
                if (r.Kind == RequestKind.Grpc)
                    sb.Append($"**Method:** `{r.Protocol.Grpc.Service}/{r.Protocol.Grpc.Method}`\n\n```json\n{BodyFormatter.Pretty(r.Protocol.Grpc.Message, BodyFormat.Json).Trim()}\n```\n\n");
                if (r.Kind is RequestKind.Http or RequestKind.Soap && r.Body.Mode is BodyMode.Json or BodyMode.Xml or BodyMode.Text && r.Body.Content.Trim().Length > 0)
                    sb.Append($"**Body**\n\n```{(r.Body.Mode == BodyMode.Json ? "json" : r.Body.Mode == BodyMode.Xml ? "xml" : "")}\n{BodyFormatter.Pretty(r.Body.Content, r.Body.Mode == BodyMode.Json ? BodyFormat.Json : BodyFormat.Text).Trim()}\n```\n\n");
                if (options.IncludeTests && Checks(r, collection.Requests) is { Count: > 0 } checks)
                {
                    sb.Append("**Checks**\n\n");
                    foreach (var check in checks)
                        sb.Append($"- {check}\n");
                    sb.Append('\n');
                }
                if (options.IncludeExamples)
                    foreach (var example in r.Examples)
                        sb.Append($"**Response {example.StatusCode}** {example.Name}\n\n```\n{BodyFormatter.Pretty(example.Body, example.ContentType).Trim()}\n```\n\n");
            }
        }
        return sb.ToString();
    }

    private static void MdTable(StringBuilder sb, string title, IEnumerable<KeyValueItem> rows)
    {
        var active = rows.Where(r => !string.IsNullOrWhiteSpace(r.Key)).ToList();
        if (active.Count == 0)
            return;
        sb.Append($"**{title}**\n\n| Name | Value |\n| --- | --- |\n");
        foreach (var row in active)
            sb.Append($"| `{Cell(row.Key)}` | {(row.IsSecret ? "_secret_" : Cell(row.Value))} |\n");
        sb.Append('\n');
    }

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");

    // ---- Shared --------------------------------------------------------------------------------------

    private static List<(string Folder, List<ApiRequest> Requests)> Groups(RequestCollection collection) =>
        collection.Requests
            .GroupBy(r => r.Folder)
            .OrderBy(g => g.Key.Length == 0 ? 0 : 1).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.OrderBy(r => r.SortOrder).ToList()))
            .ToList();

    public static string Badge(ApiRequest r) => r.Kind switch
    {
        RequestKind.Http => r.Method.ToString().ToUpperInvariant(),
        RequestKind.GraphQl => "GQL",
        RequestKind.Grpc => "gRPC",
        RequestKind.WebSocket => "WS",
        RequestKind.SocketIo => "SIO",
        _ => r.Kind.ToString().ToUpperInvariant()
    };

    private static string BadgeClass(ApiRequest r) => r.Kind == RequestKind.Http ? r.Method.ToString().ToLowerInvariant() : "other";

    private static string Anchor(ApiRequest r) => Markdown.Slug($"{r.Folder}-{r.Name}-{r.Id.ToString()[..8]}");

    private static string AuthText(AuthSettings auth) => auth.Mode switch
    {
        AuthMode.Bearer => "Bearer token",
        AuthMode.Basic => "HTTP Basic",
        AuthMode.ApiKey => $"API key in {(auth.ApiKeyLocation == ApiKeyLocation.Header ? "header" : "query parameter")} {auth.ApiKeyName}",
        AuthMode.OAuth2 => $"OAuth 2.0 ({auth.OAuth2GrantType})" + (auth.OAuth2Scope.Length > 0 ? $", scope {auth.OAuth2Scope}" : ""),
        AuthMode.AwsSigV4 => $"AWS Signature v4 ({auth.AwsService}, {auth.AwsRegion})",
        AuthMode.Digest => "HTTP Digest",
        AuthMode.Ntlm => "NTLM / Negotiate",
        _ => "None"
    };

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private const string Css = """
        :root{--bg:#fff;--fg:#1f2328;--muted:#656d76;--line:#d0d7de;--card:#f6f8fa;--accent:#0969da;--var:#8250df}
        @media (prefers-color-scheme:dark){:root{--bg:#0d1117;--fg:#e6edf3;--muted:#8d96a0;--line:#30363d;--card:#161b22;--accent:#4493f8;--var:#d2a8ff}}
        *{box-sizing:border-box}body{margin:0;font:15px/1.55 system-ui,-apple-system,Segoe UI,sans-serif;color:var(--fg);background:var(--bg);display:flex}
        nav{position:sticky;top:0;height:100vh;overflow:auto;width:290px;flex:none;border-right:1px solid var(--line);padding:16px;background:var(--card)}
        nav .brand{font-weight:700;font-size:17px;margin-bottom:10px}nav ul{list-style:none;padding:0;margin:0}
        nav li.folder{margin:14px 0 4px;font-size:12px;text-transform:uppercase;letter-spacing:.05em;color:var(--muted)}
        nav a{display:block;padding:3px 6px;border-radius:6px;color:var(--fg);text-decoration:none;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        nav a:hover{background:var(--bg)}#search{width:100%;padding:6px 8px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--fg);margin-bottom:8px}
        main{flex:1;min-width:0;padding:24px 40px;max-width:1100px}h1{margin-top:0}h2.group{border-bottom:1px solid var(--line);padding-bottom:6px;margin-top:40px}
        .endpoint{border:1px solid var(--line);border-radius:10px;padding:4px 20px 12px;margin:20px 0}
        .badge{display:inline-block;min-width:46px;text-align:center;font:600 11px/1.8 ui-monospace,monospace;border-radius:5px;padding:0 5px;color:#fff;background:#6e7781}
        .get{background:#1a7f37}.post{background:#9a6700}.put{background:#0969da}.patch{background:#8250df}.delete{background:#cf222e}.head,.options{background:#57606a}
        pre{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:10px 12px;overflow:auto;font:13px/1.45 ui-monospace,SFMono-Regular,Consolas,monospace}
        pre.url{white-space:pre-wrap;word-break:break-all}code{font:13px ui-monospace,SFMono-Regular,Consolas,monospace}.var{color:var(--var);font-weight:600}
        table{border-collapse:collapse;width:100%;margin:6px 0 12px}th,td{border:1px solid var(--line);padding:5px 8px;text-align:left;vertical-align:top}th{background:var(--card)}
        .meta{color:var(--muted);font-size:13px}details{margin:6px 0}summary{cursor:pointer}.status{font-weight:700}.s2{color:#1a7f37}.s3{color:#0969da}.s4{color:#9a6700}.s5{color:#cf222e}
        .md blockquote{border-left:3px solid var(--line);margin:0;padding-left:12px;color:var(--muted)}a{color:var(--accent)}
        @media (max-width:800px){body{display:block}nav{position:static;width:auto;height:auto}main{padding:16px}}
        """;

    private const string Script = """
        document.getElementById('search').addEventListener('input',function(e){var q=e.target.value.toLowerCase();
        document.querySelectorAll('nav li.item').forEach(function(li){li.style.display=li.dataset.search.indexOf(q)>=0?'':'none';});});
        """;
}
