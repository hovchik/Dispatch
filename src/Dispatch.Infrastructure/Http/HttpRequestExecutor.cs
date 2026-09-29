using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Dispatch.Application.Abstractions;
using Dispatch.Domain;

namespace Dispatch.Infrastructure.Http;

public sealed class HttpRequestExecutor(IHttpClientFactory clientFactory) : IRequestExecutor
{
    public const string ClientName = "dispatch";

    /// <summary>Bodies larger than this are measured fully but only this much is kept for display.</summary>
    public const int MaxDisplayBytes = 5 * 1024 * 1024;

    static HttpRequestExecutor()
    {
        // Enables legacy charsets such as windows-1251 / iso-8859-x that some APIs still return.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<ApiResponse> ExecuteAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = clientFactory.CreateClient(ClientName);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var (bytes, totalSize, truncated) = await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var contentType = response.Content.Headers.ContentType;
            return new ApiResponse
            {
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase ?? response.StatusCode.ToString(),
                Elapsed = stopwatch.Elapsed,
                SizeBytes = totalSize,
                ContentType = contentType?.MediaType,
                Body = Decode(bytes, contentType),
                IsBodyTruncated = truncated,
                Headers = response.Headers
                    .Concat(response.Content.Headers)
                    .Select(h => new ResponseHeader(h.Key, string.Join(", ", h.Value)))
                    .ToList(),
                EffectiveUrl = response.RequestMessage?.RequestUri?.ToString()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApiResponse.Failed("Request cancelled.", stopwatch.Elapsed, request.RequestUri?.ToString());
        }
        catch (OperationCanceledException)
        {
            // HttpClient signals its own timeout as a TaskCanceledException without our token being cancelled.
            return ApiResponse.Failed($"Request timed out after {client.Timeout.TotalSeconds:0} s.",
                stopwatch.Elapsed, request.RequestUri?.ToString());
        }
        catch (HttpRequestException ex)
        {
            return ApiResponse.Failed(Describe(ex), stopwatch.Elapsed, request.RequestUri?.ToString());
        }
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

    private static string Decode(byte[] bytes, MediaTypeHeaderValue? contentType)
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
            || mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return false;

        return mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
               || mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
               || mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
               || mediaType.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
               || mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(HttpRequestException ex) => ex.InnerException switch
    {
        SocketException se => $"Could not connect: {se.Message}",
        AuthenticationException ae => $"SSL/TLS error: {ae.Message}",
        { } inner => $"{ex.Message} ({inner.Message})",
        _ => ex.Message
    };
}
