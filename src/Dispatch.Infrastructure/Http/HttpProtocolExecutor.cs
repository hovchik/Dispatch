using System.Net;
using System.Text;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Auth;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Infrastructure.Http;

/// <summary>
/// Plain HTTP requests: builds the message, applies auth that needs the final message (AWS SigV4, Digest, NTLM),
/// the cookie jar, and captures the raw request for the timeline view.
/// </summary>
public sealed class HttpProtocolExecutor(
    IRequestMessageBuilder builder,
    IRequestExecutor executor,
    ICookieJar? cookies = null) : IProtocolExecutor
{
    private const int MaxRawBodyChars = 64 * 1024;

    public IReadOnlyCollection<RequestKind> Kinds { get; } = [RequestKind.Http];

    public Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken);

    /// <summary>Sends a (resolved) HTTP request. Also used by GraphQL and SOAP, which are HTTP underneath.</summary>
    public async Task<ApiResponse> SendAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        // Variables are already resolved; the builder still validates the URL and headers.
        using var message = builder.Build(request, new Dictionary<string, string>());

        var uri = message.RequestUri!;
        if (request.Settings.UseCookieJar && cookies is not null && !message.Headers.Contains("Cookie")
            && cookies.GetCookieHeader(uri) is { Length: > 0 } cookieHeader)
            message.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

        NetworkCredential? credentials = null;
        switch (request.Auth.Mode)
        {
            case AuthMode.AwsSigV4:
                try
                {
                    await AwsSigV4Signer.SignAsync(message, request.Auth, ct: cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    throw new RequestBuildException(ex.Message);
                }
                break;
            case AuthMode.Digest:
            case AuthMode.Ntlm:
                credentials = new NetworkCredential(request.Auth.Username, request.Auth.Password, request.Auth.Domain);
                break;
        }

        var raw = await DescribeAsync(message, cancellationToken).ConfigureAwait(false);

        ApiResponse response;
        if (credentials is not null && executor is HttpRequestExecutor http)
            response = await http.ExecuteAsync(message, request.Settings, credentials, cancellationToken).ConfigureAwait(false);
        else
            response = await executor.ExecuteAsync(message, request.Settings, cancellationToken).ConfigureAwait(false);

        if (response.HasResponse && request.Settings.UseCookieJar && cookies is not null)
        {
            var setCookies = response.Headers
                .Where(h => string.Equals(h.Name, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                .Select(h => h.Value)
                .ToList();
            if (setCookies.Count > 0)
                cookies.Store(new Uri(response.EffectiveUrl ?? uri.ToString()), setCookies);
        }

        return new ApiResponse
        {
            Kind = response.Kind,
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Succeeded = response.Succeeded,
            Elapsed = response.Elapsed,
            SizeBytes = response.SizeBytes,
            ContentType = response.ContentType,
            Body = response.Body,
            IsBodyTruncated = response.IsBodyTruncated,
            Headers = response.Headers,
            Trailers = response.Trailers,
            Messages = response.Messages,
            Timings = response.Timings,
            EffectiveUrl = response.EffectiveUrl ?? uri.ToString(),
            Error = response.Error,
            RawRequest = raw
        };
    }

    /// <summary>The request as it goes on the wire (HTTP/1.1 notation), body capped.</summary>
    public static async Task<string> DescribeAsync(HttpRequestMessage message, CancellationToken ct)
    {
        var uri = message.RequestUri!;
        var sb = new StringBuilder();
        sb.Append(message.Method.Method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}").Append("\r\n");
        foreach (var header in message.Headers)
            sb.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
        if (message.Content is not null)
        {
            foreach (var header in message.Content.Headers)
                sb.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
            sb.Append("\r\n");
            var body = await message.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var mediaType = message.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) || !LooksLikeText(body))
                sb.Append($"[{body.Length:N0} bytes]");
            else
            {
                var text = Encoding.UTF8.GetString(body);
                sb.Append(text.Length > MaxRawBodyChars ? text[..MaxRawBodyChars] + "\n[truncated]" : text);
            }
        }
        return sb.ToString();
    }

    private static bool LooksLikeText(byte[] bytes) =>
        bytes.Take(4096).All(b => b >= 0x09 && (b != 0x7F) && (b >= 0x20 || b is 0x09 or 0x0A or 0x0D));
}
