namespace Dispatch.Domain;

public sealed record ResponseHeader(string Name, string Value);

/// <summary>One message in a streaming exchange (WebSocket frame, gRPC stream message, MQTT publish, SSE event, ...).</summary>
public sealed record StreamMessage(
    DateTimeOffset Timestamp,
    MessageDirection Direction,
    string Content,
    string? Label = null)
{
    public static StreamMessage Sent(string content, string? label = null) =>
        new(DateTimeOffset.Now, MessageDirection.Sent, content, label);

    public static StreamMessage Received(string content, string? label = null) =>
        new(DateTimeOffset.Now, MessageDirection.Received, content, label);

    public static StreamMessage Info(string content) => new(DateTimeOffset.Now, MessageDirection.Info, content);
    public static StreamMessage Failure(string content) => new(DateTimeOffset.Now, MessageDirection.Error, content);
}

/// <summary>
/// Where the time went. Phases that didn't happen (e.g. DNS on a pooled connection) are null. <see cref="FirstByte"/>
/// is the wait for the first response byte after connecting, including any TLS handshake.
/// </summary>
public sealed record ResponseTimings(
    TimeSpan? Dns = null,
    TimeSpan? Connect = null,
    TimeSpan? FirstByte = null,
    TimeSpan? Download = null);

/// <summary>
/// Result of executing a request. Either a server response or a transport-level <see cref="Error"/>.
/// Protocols without HTTP status codes put their own status here (gRPC status code, 0 for broker publishes, ...).
/// </summary>
public sealed class ApiResponse
{
    public RequestKind Kind { get; init; } = RequestKind.Http;
    public int StatusCode { get; init; }
    public string ReasonPhrase { get; init; } = string.Empty;

    /// <summary>Overrides the HTTP success rule (2xx/3xx) for protocols with their own notion of success.</summary>
    public bool? Succeeded { get; init; }
    public TimeSpan Elapsed { get; init; }
    public long SizeBytes { get; init; }
    public string? ContentType { get; init; }
    public string Body { get; init; } = string.Empty;
    public bool IsBodyTruncated { get; init; }
    public IReadOnlyList<ResponseHeader> Headers { get; init; } = [];

    /// <summary>gRPC trailers and similar end-of-call metadata.</summary>
    public IReadOnlyList<ResponseHeader> Trailers { get; init; } = [];

    /// <summary>Streaming protocols: every message sent and received, in order.</summary>
    public IReadOnlyList<StreamMessage> Messages { get; init; } = [];
    public ResponseTimings? Timings { get; init; }

    /// <summary>The request exactly as sent (after variables, auth and scripts), for debugging.</summary>
    public string? RawRequest { get; init; }

    /// <summary>The final URL after variable resolution (and redirects), useful for debugging.</summary>
    public string? EffectiveUrl { get; init; }

    /// <summary>Set when no response was received (DNS failure, timeout, invalid URL, ...).</summary>
    public string? Error { get; init; }

    /// <summary>Assertion and script test outcomes (filled in by the sender).</summary>
    public IReadOnlyList<TestResult> TestResults { get; set; } = [];

    /// <summary>Console output of pre-request and test scripts.</summary>
    public IReadOnlyList<string> ScriptLog { get; set; } = [];

    /// <summary>Variables set by extraction rules and scripts during this send.</summary>
    public IReadOnlyDictionary<string, string> VariableUpdates { get; set; } = new Dictionary<string, string>();

    /// <summary>Set when an OAuth 2.0 token was fetched or refreshed, so the caller can cache it on the request.</summary>
    public AuthSettings? RefreshedAuth { get; set; }

    public bool HasResponse => Error is null;
    public bool IsSuccess => HasResponse && (Succeeded ?? StatusCode is >= 200 and < 400);
    public bool AllTestsPassed => TestResults.All(t => t.Passed);

    public static ApiResponse Failed(string error, TimeSpan elapsed, string? url = null, RequestKind kind = RequestKind.Http) =>
        new() { Error = error, Elapsed = elapsed, EffectiveUrl = url, Kind = kind };

    /// <summary>A copy with a different body (e.g. a JSON view of a binary protocol payload).</summary>
    public ApiResponse WithBody(string body, string? contentType = null) => new()
    {
        Kind = Kind, StatusCode = StatusCode, ReasonPhrase = ReasonPhrase, Succeeded = Succeeded, Elapsed = Elapsed,
        SizeBytes = SizeBytes, ContentType = contentType ?? ContentType, Body = body, IsBodyTruncated = IsBodyTruncated,
        Headers = Headers, Trailers = Trailers, Messages = Messages, Timings = Timings, RawRequest = RawRequest,
        EffectiveUrl = EffectiveUrl, Error = Error, TestResults = TestResults, ScriptLog = ScriptLog,
        VariableUpdates = VariableUpdates
    };
}
