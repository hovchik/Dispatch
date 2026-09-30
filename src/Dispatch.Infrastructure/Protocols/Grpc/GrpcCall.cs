using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;

namespace Dispatch.Infrastructure.Protocols.Grpc;

public sealed class GrpcException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// A raw gRPC call over HTTP/2: length-prefixed frames in both directions, metadata as headers, status in trailers.
/// Supports all four call types; requests are written as they are produced (full duplex).
/// </summary>
public sealed class GrpcCall : IAsyncDisposable
{
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    private static readonly string[] StatusNames =
    [
        "OK", "CANCELLED", "UNKNOWN", "INVALID_ARGUMENT", "DEADLINE_EXCEEDED", "NOT_FOUND", "ALREADY_EXISTS",
        "PERMISSION_DENIED", "RESOURCE_EXHAUSTED", "FAILED_PRECONDITION", "ABORTED", "OUT_OF_RANGE", "UNIMPLEMENTED",
        "INTERNAL", "UNAVAILABLE", "DATA_LOSS", "UNAUTHENTICATED"
    ];

    private readonly Channel<byte[]> _requests = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts;
    private readonly Task<HttpResponseMessage> _responseTask;
    private HttpResponseMessage? _response;

    private readonly CancellationToken _userToken;
    private readonly bool _hasDeadline;

    private GrpcCall(HttpClient client, HttpRequestMessage message, CancellationTokenSource cts, CancellationToken userToken,
        bool hasDeadline)
    {
        _cts = cts;
        _userToken = userToken;
        _hasDeadline = hasDeadline;
        message.Content = new FrameContent(_requests.Reader);
        _responseTask = client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
    }

    public static string StatusName(int code) => code >= 0 && code < StatusNames.Length ? StatusNames[code] : $"STATUS_{code}";

    /// <summary>Base URI of the server: explicit http(s):// is kept; otherwise TLS decides the scheme.</summary>
    public static Uri BaseUri(string url, bool useTls)
    {
        url = url.Trim().TrimEnd('/');
        if (url.StartsWith("grpc://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url[7..];
        else if (url.StartsWith("grpcs://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[8..];
        else if (!url.Contains("://", StringComparison.Ordinal))
            url = (useTls ? "https://" : "http://") + url;
        if (!Uri.TryCreate(url + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Invalid gRPC server address: {url}");
        return uri;
    }

    public static GrpcCall Start(IHttpClientSource clients, RequestSettings settings, Uri baseUri, string fullMethod,
        IEnumerable<KeyValuePair<string, string>> metadata, TimeSpan? deadline, CancellationToken cancellationToken)
    {
        var client = clients.GetClient(settings);
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, fullMethod.TrimStart('/')))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        message.Headers.TryAddWithoutValidation("TE", "trailers");
        message.Headers.TryAddWithoutValidation("User-Agent", "dispatch-grpc/0.2");
        message.Headers.TryAddWithoutValidation("grpc-accept-encoding", "identity,gzip");
        foreach (var (key, value) in metadata)
        {
            var name = key.Trim().ToLowerInvariant();
            if (name.Length == 0 || name is "content-type" or "te" or "grpc-timeout")
                continue;
            message.Headers.TryAddWithoutValidation(name, value);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (deadline is { } d && d > TimeSpan.Zero)
        {
            message.Headers.TryAddWithoutValidation("grpc-timeout", $"{Math.Max(1, (long)d.TotalMilliseconds)}m");
            cts.CancelAfter(d);
        }
        return new GrpcCall(client, message, cts, cancellationToken, deadline is { } dl && dl > TimeSpan.Zero);
    }

    public ValueTask WriteAsync(byte[] message) => _requests.Writer.WriteAsync(message, _cts.Token);

    /// <summary>Half-closes the request stream (no more messages).</summary>
    public void CompleteRequests() => _requests.Writer.TryComplete();

    public async Task<HttpResponseMessage> GetResponseAsync()
    {
        try
        {
            return _response ??= await _responseTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsDeadline)
        {
            throw new GrpcException(4, "Deadline exceeded");
        }
    }

    /// <summary>True when the call was cancelled by its deadline rather than by the user.</summary>
    public bool IsDeadline => _hasDeadline && _cts.IsCancellationRequested && !_userToken.IsCancellationRequested;

    public IReadOnlyList<ResponseHeader> ResponseHeaders => _response is null
        ? []
        : _response.Headers.Concat(_response.Content.Headers)
            .Select(h => new ResponseHeader(h.Key, string.Join(", ", h.Value)))
            .ToList();

    /// <summary>Reads response messages until the server ends the stream.</summary>
    public async IAsyncEnumerable<byte[]> ReadResponsesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync().ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var compressed = response.Headers.TryGetValues("grpc-encoding", out var encodings) && encodings.Contains("gzip");
        await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        var header = new byte[5];
        while (true)
        {
            if (!await ReadExactlyOrEndAsync(stream, header, linked.Token).ConfigureAwait(false))
                yield break;
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
            if (length > MaxMessageBytes)
                throw new GrpcException(8, $"Response message of {length:N0} bytes exceeds the {MaxMessageBytes:N0} byte limit.");
            var payload = new byte[length];
            if (length > 0 && !await ReadExactlyOrEndAsync(stream, payload, linked.Token).ConfigureAwait(false))
                throw new GrpcException(13, "The response stream ended in the middle of a message.");
            if (header[0] == 1)
            {
                if (!compressed)
                    throw new GrpcException(13, "Received a compressed message without grpc-encoding.");
                payload = Gunzip(payload);
            }
            yield return payload;
        }
    }

    /// <summary>The call status: from trailers, or from headers for a "trailers-only" response.</summary>
    public (int Code, string Message, IReadOnlyList<ResponseHeader> Trailers) GetStatus()
    {
        var trailers = _response?.TrailingHeaders.Select(h => new ResponseHeader(h.Key, string.Join(", ", h.Value))).ToList()
                       ?? [];
        string? status = null;
        string? message = null;
        if (_response is not null)
        {
            if (_response.TrailingHeaders.TryGetValues("grpc-status", out var s))
                status = s.FirstOrDefault();
            else if (_response.Headers.TryGetValues("grpc-status", out var hs))
                status = hs.FirstOrDefault();
            if (_response.TrailingHeaders.TryGetValues("grpc-message", out var m) || _response.Headers.TryGetValues("grpc-message", out m))
                message = Uri.UnescapeDataString(m.FirstOrDefault() ?? "");
        }

        if (status is null)
        {
            if (_response is { IsSuccessStatusCode: false } r)
                return (HttpStatusToGrpc(r.StatusCode), $"HTTP {(int)r.StatusCode} {r.ReasonPhrase}", trailers);
            return (13, "Missing grpc-status (is this a gRPC server?)", trailers);
        }
        return (int.TryParse(status, out var code) ? code : 2, message ?? "", trailers);
    }

    private static int HttpStatusToGrpc(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => 13,
        HttpStatusCode.Unauthorized => 16,
        HttpStatusCode.Forbidden => 7,
        HttpStatusCode.NotFound => 12,
        HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout => 14,
        _ => 2
    };

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                return read == 0 ? false : throw new GrpcException(13, "Truncated gRPC frame.");
            read += n;
        }
        return true;
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    public void Cancel() => _cts.Cancel();

    public async ValueTask DisposeAsync()
    {
        _requests.Writer.TryComplete();
        if (!_responseTask.IsCompleted)
            await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _responseTask.ConfigureAwait(false);
        }
        catch
        {
            // Already reported through the call's status.
        }
        _response?.Dispose();
        _cts.Dispose();
    }

    /// <summary>Request body that writes each queued message as a gRPC frame and flushes, so streaming works.</summary>
    private sealed class FrameContent : HttpContent
    {
        private readonly ChannelReader<byte[]> _messages;

        public FrameContent(ChannelReader<byte[]> messages)
        {
            _messages = messages;
            Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var header = new byte[5];
            await foreach (var message in _messages.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                header[0] = 0;
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)message.Length);
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
