using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Domain;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.Webhooks;

/// <summary>Text, HTML and JSON renderings of a set of received hooks (the viewer's export and <c>dispatch webhooks</c>).</summary>
public static class WebhookReportWriter
{
    /// <summary>One line per hook, oldest first.</summary>
    public static string Text(IReadOnlyList<ReceivedWebhook> hooks, Uri? baseUrl = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Webhooks · {hooks.Count} received" + (baseUrl is null ? "" : $" at {baseUrl}"));
        if (hooks.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var hook in hooks)
            sb.AppendLine(Line(hook));
        return sb.ToString().TrimEnd();
    }

    /// <summary>A live-log line: time, method, path, size and a body preview.</summary>
    public static string Line(ReceivedWebhook hook) =>
        $"  {hook.ReceivedAt:HH:mm:ss.fff}  {hook.Method,-6} {hook.PathAndQuery}  ({Bytes(hook.BodyBytes)}{(hook.Truncated ? ", truncated" : "")})" +
        (hook.HasBody ? $"  {hook.Preview(100)}" : "");

    public static string Html(IReadOnlyList<ReceivedWebhook> hooks, Uri? baseUrl = null)
    {
        var sb = Begin("Webhooks · Dispatch", "Received webhooks", $"{hooks.Count} HOOK(S)", hooks.Count > 0 ? true : null);
        sb.Append($"<p class=\"muted\">{(baseUrl is null ? "" : E(baseUrl.ToString()) + " · ")}{DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
        var byPath = hooks.GroupBy(h => h.Label).Select(g => (Label: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToList();
        sb.Append("<div class=\"stats\">")
            .Append(Stat("Hooks", hooks.Count.ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("Endpoints", byPath.Count.ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("First", hooks.Count == 0 ? "—" : hooks[0].ReceivedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)))
            .Append(Stat("Last", hooks.Count == 0 ? "—" : hooks[^1].ReceivedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)))
            .Append(Stat("Payload", Bytes(hooks.Sum(h => h.BodyBytes))))
            .Append("</div>");
        if (byPath.Count > 0)
        {
            sb.Append("<h2>Endpoints</h2><div class=\"card\">");
            var max = byPath[0].Count;
            foreach (var (label, count) in byPath)
                sb.Append(Bar(label, count, max));
            sb.Append("</div>");
        }
        sb.Append("<h2>Hooks</h2>");
        if (hooks.Count == 0)
            sb.Append("<p class=\"muted\">No callbacks were received.</p>");
        var i = 0;
        foreach (var hook in hooks)
        {
            sb.Append($"<details class=\"card\" style=\"margin:8px 0\"{(i++ < 3 ? " open" : "")}><summary><b>{E(hook.Method)}</b> <code>{E(hook.PathAndQuery)}</code>" +
                      $" <span class=\"muted\">{hook.ReceivedAt:HH:mm:ss.fff} · {Bytes(hook.BodyBytes)} · {E(hook.RemoteAddress)}</span></summary>");
            sb.Append("<table><tbody>");
            foreach (var h in hook.Headers)
                sb.Append($"<tr><td>{E(h.Key)}</td><td>{E(h.Value)}</td></tr>");
            sb.Append("</tbody></table>");
            if (hook.HasBody)
                sb.Append($"<pre><code>{E(hook.IsJson ? WebhookReplay.Pretty(hook.Body) : hook.Body)}</code></pre>");
            sb.Append("</details>");
        }
        sb.Append(End);
        return sb.ToString();
    }

    /// <summary>All hooks as a JSON array; <see cref="FromJson"/> reads it back.</summary>
    public static string Json(IReadOnlyList<ReceivedWebhook> hooks, Uri? baseUrl = null)
    {
        var root = new JsonObject
        {
            ["tool"] = "dispatch webhooks",
            ["generatedAt"] = DateTimeOffset.Now.ToString("o"),
            ["baseUrl"] = baseUrl?.ToString(),
            ["count"] = hooks.Count,
            ["hooks"] = new JsonArray(hooks.Select(ToJson).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static JsonObject ToJson(ReceivedWebhook hook)
    {
        var headers = new JsonObject();
        foreach (var h in hook.Headers)
            headers[h.Key] = h.Value;
        var node = new JsonObject
        {
            ["id"] = hook.Id,
            ["receivedAt"] = hook.ReceivedAt.ToString("o"),
            ["method"] = hook.Method,
            ["path"] = hook.Path,
            ["query"] = hook.Query,
            ["remoteAddress"] = hook.RemoteAddress,
            ["contentType"] = hook.ContentType,
            ["headers"] = headers,
            ["bodyBytes"] = hook.BodyBytes,
            ["truncated"] = hook.Truncated,
            ["body"] = hook.Body
        };
        if (hook.IsJson)
        {
            try
            {
                node["json"] = JsonNode.Parse(hook.Body);
            }
            catch (JsonException)
            {
                // Not valid JSON after all: the raw body is enough.
            }
        }
        return node;
    }

    /// <summary>Reads a file written by <see cref="Json"/> (or a bare array of hooks).</summary>
    public static List<ReceivedWebhook> FromJson(string json)
    {
        var root = JsonNode.Parse(json);
        var array = root as JsonArray ?? root?["hooks"] as JsonArray ?? [];
        var hooks = new List<ReceivedWebhook>();
        foreach (var item in array.OfType<JsonObject>())
        {
            var headers = new List<KeyValueItem>();
            if (item["headers"] is JsonObject h)
                foreach (var (key, value) in h)
                    headers.Add(new KeyValueItem(key, value?.ToString() ?? ""));
            var body = item["body"]?.GetValue<string>() ?? "";
            hooks.Add(new ReceivedWebhook
            {
                Id = Guid.TryParse(item["id"]?.ToString(), out var id) ? id : Guid.NewGuid(),
                ReceivedAt = DateTimeOffset.TryParse(item["receivedAt"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.Now,
                Method = item["method"]?.ToString() ?? "POST",
                Path = item["path"]?.ToString() ?? "/",
                Query = item["query"]?.ToString() ?? "",
                RemoteAddress = item["remoteAddress"]?.ToString() ?? "",
                ContentType = item["contentType"]?.ToString(),
                Headers = headers,
                Body = body,
                BodyBytes = item["bodyBytes"]?.GetValue<long>() ?? Encoding.UTF8.GetByteCount(body),
                Truncated = item["truncated"]?.GetValue<bool>() ?? false
            });
        }
        return hooks;
    }
}
