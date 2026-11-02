using Dispatch.Domain;

namespace Dispatch.Application.Webhooks;

/// <summary>One HTTP callback recorded by the webhook receiver.</summary>
public sealed class ReceivedWebhook
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;
    public string Method { get; init; } = "POST";

    /// <summary>The request path, e.g. <c>/hooks/orders</c>.</summary>
    public string Path { get; init; } = "/";

    /// <summary>The query string without the leading <c>?</c>.</summary>
    public string Query { get; init; } = "";
    public List<KeyValueItem> Headers { get; init; } = [];
    public string Body { get; init; } = "";
    public string? ContentType { get; init; }
    public long BodyBytes { get; init; }

    /// <summary>True when the body was cut to the receiver's size limit.</summary>
    public bool Truncated { get; init; }
    public string RemoteAddress { get; init; } = "";

    public string PathAndQuery => Query.Length == 0 ? Path : $"{Path}?{Query}";

    /// <summary>What the message log shows as the channel: <c>POST /hooks/orders</c>.</summary>
    public string Label => $"{Method} {Path}";

    public bool HasBody => Body.Length > 0;

    public bool IsJson => ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                          || (ContentType is null && Body.TrimStart().StartsWith('{'));

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>The hook as a received stream message, so message checks can treat the receiver like any listener.</summary>
    public StreamMessage ToMessage() => new(ReceivedAt, MessageDirection.Received, Body, Label);

    /// <summary>A short one-line preview of the body for lists.</summary>
    public string BodyPreview => Preview();

    /// <summary>A short one-line preview of the body for lists and logs.</summary>
    public string Preview(int max = 120)
    {
        var single = Body.ReplaceLineEndings(" ").Trim();
        return single.Length > max ? single[..(max - 1)] + "…" : single;
    }
}
