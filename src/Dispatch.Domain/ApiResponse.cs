namespace Dispatch.Domain;

public sealed record ResponseHeader(string Name, string Value);

/// <summary>Result of executing a request. Either a server response or a transport-level <see cref="Error"/>.</summary>
public sealed class ApiResponse
{
    public int StatusCode { get; init; }
    public string ReasonPhrase { get; init; } = string.Empty;
    public TimeSpan Elapsed { get; init; }
    public long SizeBytes { get; init; }
    public string? ContentType { get; init; }
    public string Body { get; init; } = string.Empty;
    public bool IsBodyTruncated { get; init; }
    public IReadOnlyList<ResponseHeader> Headers { get; init; } = [];

    /// <summary>The final URL after variable resolution (and redirects), useful for debugging.</summary>
    public string? EffectiveUrl { get; init; }

    /// <summary>Set when no HTTP response was received (DNS failure, timeout, invalid URL, ...).</summary>
    public string? Error { get; init; }

    public bool HasResponse => Error is null;

    public static ApiResponse Failed(string error, TimeSpan elapsed, string? url = null) =>
        new() { Error = error, Elapsed = elapsed, EffectiveUrl = url };
}
