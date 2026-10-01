using Dispatch.Domain;

namespace Dispatch.Application.Capture;

/// <summary>One request/response pair recorded by the capture proxy.</summary>
public sealed class CapturedExchange
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = string.Empty;
    public bool Secure { get; init; }
    public List<KeyValueItem> RequestHeaders { get; init; } = [];
    public string RequestBody { get; init; } = string.Empty;
    public string? RequestContentType { get; init; }

    public int StatusCode { get; set; }
    public string ReasonPhrase { get; set; } = string.Empty;
    public List<KeyValueItem> ResponseHeaders { get; set; } = [];
    public string ResponseBody { get; set; } = string.Empty;
    public string? ResponseContentType { get; set; }
    public long ResponseSize { get; set; }
    public double ElapsedMs { get; set; }
    public string? Error { get; set; }

    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : "";
    public string Path => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : Url;
    public bool IsSuccess => Error is null && StatusCode is >= 200 and < 400;
}
