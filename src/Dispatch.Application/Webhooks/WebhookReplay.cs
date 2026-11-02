using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Domain;

namespace Dispatch.Application.Webhooks;

/// <summary>Turns received hooks back into requests (replay) and saved examples.</summary>
public static class WebhookReplay
{
    /// <summary>Headers that describe the original connection, not the callback, and must not be re-sent.</summary>
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Expect", "Keep-Alive", "Upgrade", "Proxy-Connection",
        "Accept-Encoding"
    };

    /// <summary>
    /// A request that re-sends the callback (same method, path, query, headers and body) to <paramref name="targetBaseUrl"/>,
    /// e.g. <c>http://localhost:8080</c>, so your own handler receives exactly what the provider sent.
    /// </summary>
    public static ApiRequest ToRequest(ReceivedWebhook hook, string targetBaseUrl)
    {
        var target = targetBaseUrl.Trim();
        if (target.Length == 0)
            target = "http://localhost:8080";
        if (!target.Contains("://", StringComparison.Ordinal))
            target = "http://" + target;
        var url = target.TrimEnd('/') + hook.PathAndQuery;
        var request = new ApiRequest
        {
            Name = $"Webhook {hook.Label}",
            Kind = RequestKind.Http,
            Method = Verb(hook.Method),
            Url = url,
            Headers = hook.Headers.Where(h => !HopByHop.Contains(h.Key)).Select(h => new KeyValueItem(h.Key, h.Value)).ToList(),
            Description = $"Replay of a callback received at {hook.ReceivedAt:yyyy-MM-dd HH:mm:ss} from {hook.RemoteAddress}."
        };
        if (hook.HasBody)
            request.Body = new RequestBody { Mode = BodyMode(hook), Content = hook.Body };
        return request;
    }

    /// <summary>The callback as a saved example: documents the payload and lets the mock server serve it back.</summary>
    public static ResponseExample ToExample(ReceivedWebhook hook) => new()
    {
        Name = $"{hook.Label} · {hook.ReceivedAt:HH:mm:ss}",
        StatusCode = 200,
        ContentType = hook.ContentType ?? (hook.IsJson ? "application/json" : "text/plain"),
        Body = hook.IsJson ? Pretty(hook.Body) : hook.Body,
        Headers = hook.Headers.Where(h => !HopByHop.Contains(h.Key) && !h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            .Select(h => new KeyValueItem(h.Key, h.Value)).ToList()
    };

    /// <summary>
    /// The request in <paramref name="collection"/> that stands for this callback (same method and path), or a new one
    /// addressed to <c>{{webhookTarget}}</c> so it can be replayed against any environment.
    /// </summary>
    public static ApiRequest FindOrCreateRequest(RequestCollection collection, ReceivedWebhook hook)
    {
        var verb = Verb(hook.Method);
        var existing = collection.Requests.FirstOrDefault(r => r.Kind == RequestKind.Http && r.Method == verb && PathOf(r.Url) is { } p
            && WebhookMatcher.Matches(hook.Method, p, hook.Path));
        if (existing is not null)
            return existing;
        var request = ToRequest(hook, "{{webhookTarget}}");
        request.Url = "{{webhookTarget}}" + hook.Path;
        request.CollectionId = collection.Id;
        request.Folder = "Webhooks";
        request.Description = $"Callback recorded by the webhook receiver. Set `webhookTarget` to the handler to replay against.";
        return request;
    }

    private static string? PathOf(string url)
    {
        var withoutQuery = url.Split('?')[0];
        var scheme = withoutQuery.IndexOf("://", StringComparison.Ordinal);
        var start = scheme < 0 ? withoutQuery.IndexOf("}}", StringComparison.Ordinal) is var v && v >= 0 ? v + 2 : -1 : withoutQuery.IndexOf('/', scheme + 3);
        if (start < 0)
            return scheme < 0 && withoutQuery.StartsWith('/') ? withoutQuery : null;
        return withoutQuery[start..] is { Length: > 0 } path ? path : "/";
    }

    private static HttpVerb Verb(string method) => Enum.TryParse<HttpVerb>(method, true, out var verb) ? verb : HttpVerb.Post;

    private static BodyMode BodyMode(ReceivedWebhook hook) => hook.IsJson ? Domain.BodyMode.Json
        : hook.ContentType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true ? Domain.BodyMode.Xml
        : hook.ContentType?.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true ? Domain.BodyMode.FormUrlEncoded
        : Domain.BodyMode.Text;

    public static string Pretty(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
