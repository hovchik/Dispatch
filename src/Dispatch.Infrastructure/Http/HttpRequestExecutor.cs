using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Dispatch.Application.Abstractions;
using Dispatch.Domain;

namespace Dispatch.Infrastructure.Http;

public sealed class HttpRequestExecutor(IHttpClientSource clients) : IRequestExecutor
{
    /// <summary>Bodies larger than this are measured fully but only this much is kept for display.</summary>
    public const int MaxDisplayBytes = 5 * 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

    static HttpRequestExecutor()
    {
        // Enables legacy charsets such as windows-1251 / iso-8859-x that some APIs still return.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public Task<ApiResponse> ExecuteAsync(HttpRequestMessage request, RequestSettings settings, CancellationToken cancellationToken) =>
        ExecuteAsync(request, settings, null, cancellationToken);

    public async Task<ApiResponse> ExecuteAsync(HttpRequestMessage request, RequestSettings settings,
        NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        var client = clients.GetClient(settings, credentials);
        var timeout = settings.TimeoutMs > 0
            ? TimeSpan.FromMilliseconds(settings.TimeoutMs)
            : client.Timeout != Timeout.InfiniteTimeSpan ? client.Timeout : DefaultTimeout;

        var timings = new ConnectionTimings();
        request.Options.Set(ConnectionTimings.Key, timings);
        ApplyVersion(request, settings.HttpVersion);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        var startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            var headersAt = Stopwatch.GetTimestamp();

            var (bytes, totalSize, truncated) = await ReadBodyAsync(response.Content, timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var contentType = response.Content.Headers.ContentType;
            return new ApiResponse
            {
                Kind = RequestKind.Http,
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase ?? response.StatusCode.ToString(),
                Elapsed = stopwatch.Elapsed,
                SizeBytes = totalSize,
                ContentType = contentType?.MediaType,
                Body = Decode(bytes, contentType),
                BodyBytes = IsBinary(contentType?.MediaType) && bytes.Length > 0 ? bytes : null,
                IsBodyTruncated = truncated,
                Headers = response.Headers
                    .Concat(response.Content.Headers)
                    .Select(h => new ResponseHeader(h.Key, string.Join(", ", h.Value)))
                    .ToList(),
                Trailers = response.TrailingHeaders.Select(h => new ResponseHeader(h.Key, string.Join(", ", h.Value))).ToList(),
                Timings = BuildTimings(timings, startTimestamp, headersAt, stopwatch.Elapsed),
                EffectiveUrl = response.RequestMessage?.RequestUri?.ToString()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApiResponse.Failed("Request cancelled.", stopwatch.Elapsed, request.RequestUri?.ToString());
        }
        catch (OperationCanceledException)
        {
            return ApiResponse.Failed($"Request timed out after {timeout.TotalSeconds:0.##} s.",
                stopwatch.Elapsed, request.RequestUri?.ToString());
        }
        catch (HttpRequestException ex)
        {
            return ApiResponse.Failed(Describe(ex), stopwatch.Elapsed, request.RequestUri?.ToString());
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or FileNotFoundException
                                       or System.Security.Cryptography.CryptographicException)
        {
            return ApiResponse.Failed(ex.Message, stopwatch.Elapsed, request.RequestUri?.ToString());
        }
    }

    private static void ApplyVersion(HttpRequestMessage request, HttpVersionPreference preference)
    {
        switch (preference)
        {
            case HttpVersionPreference.Http11:
                request.Version = HttpVersion.Version11;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                break;
            case HttpVersionPreference.Http2:
                request.Version = HttpVersion.Version20;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                break;
            case HttpVersionPreference.Http3:
                request.Version = HttpVersion.Version30;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                break;
            default:
                request.Version = HttpVersion.Version11;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
                break;
        }
    }

    /// <summary>
    /// DNS and TCP connect are measured by the connect callback (only when a new connection was opened). "First byte"
    /// is the wait from connection (or start, on a pooled connection) to response headers, which includes any TLS
    /// handshake and server processing time.
    /// </summary>
    private static ResponseTimings BuildTimings(ConnectionTimings timings, long start, long headersAt, TimeSpan total)
    {
        var waitFrom = timings.ConnectedAt != 0 ? timings.ConnectedAt : start;
        var toHeaders = Stopwatch.GetElapsedTime(start, headersAt);
        return new ResponseTimings(
            Dns: timings.Dns,
            Connect: timings.Connect,
            FirstByte: Stopwatch.GetElapsedTime(waitFrom, headersAt),
            Download: total > toHeaders ? total - toHeaders : TimeSpan.Zero);
    }

    private static async Task<(byte[] Bytes, long TotalSize, bool Truncated)> ReadBodyAsync(
        HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            var room = MaxDisplayBytes - (int)buffer.Length;
            if (room > 0)
                buffer.Write(chunk, 0, Math.Min(room, read));
            total += read;
        }
        return (buffer.ToArray(), total, total > buffer.Length);
    }

    internal static string Decode(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        if (bytes.Length == 0)
            return string.Empty;

        if (IsBinary(contentType?.MediaType))
            return $"[Binary content: {contentType?.MediaType}, {bytes.Length:N0} bytes shown]";

        var encoding = Encoding.UTF8;
        if (contentType?.CharSet is { Length: > 0 } charset)
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { /* unknown charset: fall back to UTF-8 */ }
        }
        return encoding.GetString(bytes);
    }

    private static bool IsBinary(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
            return false;
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("graphql", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return false;

        return mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
               || mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
               || mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
               || mediaType.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
               || mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string Describe(HttpRequestException ex) => ex.InnerException switch
    {
        SocketException se => $"Could not connect: {se.Message}",
        AuthenticationException ae => $"SSL/TLS error: {ae.Message}",
        { } inner => $"{ex.Message} ({inner.Message})",
        _ => ex.Message
    };
}
