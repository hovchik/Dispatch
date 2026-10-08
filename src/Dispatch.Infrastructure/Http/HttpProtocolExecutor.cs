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
        var useJar = request.Settings.UseCookieJar && cookies is not null;
        var userCookieHeader = message.Headers.Contains("Cookie");
        if (useJar && !userCookieHeader && cookies!.GetCookieHeader(uri) is { Length: > 0 } cookieHeader)
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
        if (useJar && request.Settings.FollowRedirects)
        {
            // HttpClient drops intermediate 3xx responses (and their Set-Cookie) when it follows redirects itself, and would
            // re-send the Cookie header we added to every hop. Follow them here instead, one hop at a time.
            response = await SendFollowingRedirectsAsync(message, request.Settings, credentials, userCookieHeader, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            response = await SendOnceAsync(message, request.Settings, credentials, cancellationToken).ConfigureAwait(false);
            if (useJar)
                StoreCookies(response, new Uri(response.EffectiveUrl ?? uri.ToString()));
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
            BodyBytes = response.BodyBytes,
            Headers = response.Headers,
            Trailers = response.Trailers,
            Messages = response.Messages,
            Timings = response.Timings,
            EffectiveUrl = response.EffectiveUrl ?? uri.ToString(),
            Error = response.Error,
            RawRequest = raw
        };
    }

    private Task<ApiResponse> SendOnceAsync(HttpRequestMessage message, RequestSettings settings, NetworkCredential? credentials,
        CancellationToken ct) =>
        credentials is not null && executor is HttpRequestExecutor http
            ? http.ExecuteAsync(message, settings, credentials, ct)
            : executor.ExecuteAsync(message, settings, ct);

    /// <summary>
    /// Sends with automatic redirects off and follows 3xx responses manually: Set-Cookie from every hop goes into the jar,
    /// each hop gets the jar's cookies for its own URL, 301/302/303 switch to GET without a body (except for GET/HEAD),
    /// 307/308 keep method and body, and Authorization is not forwarded to another origin.
    /// </summary>
    private async Task<ApiResponse> SendFollowingRedirectsAsync(HttpRequestMessage first, RequestSettings settings,
        NetworkCredential? credentials, bool userCookieHeader, CancellationToken ct)
    {
        var hopSettings = settings.Clone();
        hopSettings.FollowRedirects = false;
        var origin = first.RequestUri!;
        var elapsed = TimeSpan.Zero;

        var message = first;
        try
        {
            for (var hop = 0; ; hop++)
            {
                var uri = message.RequestUri!;
                var response = await SendOnceAsync(message, hopSettings, credentials, ct).ConfigureAwait(false);
                elapsed += response.Elapsed;
                if (!response.HasResponse)
                    return response;

                StoreCookies(response, uri);
                var status = response.StatusCode;
                var location = response.Headers.FirstOrDefault(h => string.Equals(h.Name, "Location", StringComparison.OrdinalIgnoreCase))?.Value;
                if (status is not (301 or 302 or 303 or 307 or 308) || string.IsNullOrWhiteSpace(location)
                    || hop >= Math.Max(1, settings.MaxRedirects)
                    || !Uri.TryCreate(uri, location, out var target) || target.Scheme is not ("http" or "https"))
                    return WithElapsed(response, elapsed, uri);

                var next = await BuildHopAsync(first, message, target, status, origin, userCookieHeader, ct).ConfigureAwait(false);
                if (!ReferenceEquals(message, first))
                    message.Dispose();
                message = next;
            }
        }
        finally
        {
            if (!ReferenceEquals(message, first))
                message.Dispose();
        }
    }

    private async Task<HttpRequestMessage> BuildHopAsync(HttpRequestMessage first, HttpRequestMessage previous, Uri target, int status,
        Uri origin, bool userCookieHeader, CancellationToken ct)
    {
        var keepMethodAndBody = status is 307 or 308 || first.Method == HttpMethod.Get || first.Method == HttpMethod.Head;
        var next = new HttpRequestMessage(keepMethodAndBody ? first.Method : HttpMethod.Get, target)
        {
            Version = first.Version,
            VersionPolicy = first.VersionPolicy
        };

        var sameOrigin = Uri.Compare(origin, target, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
        foreach (var header in first.Headers)
        {
            if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                || (!sameOrigin && header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
                continue;
            next.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (userCookieHeader && sameOrigin)
            next.Headers.TryAddWithoutValidation("Cookie", first.Headers.GetValues("Cookie"));
        else if (cookies!.GetCookieHeader(target) is { Length: > 0 } cookieHeader)
            next.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

        if (keepMethodAndBody && first.Content is not null)
        {
            // The body was already buffered to describe the raw request, so it can be read again.
            var bytes = await first.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            next.Content = new ByteArrayContent(bytes);
            foreach (var header in first.Content.Headers)
                next.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return next;
    }

    private void StoreCookies(ApiResponse response, Uri uri)
    {
        if (!response.HasResponse)
            return;
        var setCookies = response.Headers
            .Where(h => string.Equals(h.Name, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Value)
            .ToList();
        if (setCookies.Count > 0)
            cookies!.Store(new Uri(response.EffectiveUrl ?? uri.ToString()), setCookies);
    }

    private static ApiResponse WithElapsed(ApiResponse response, TimeSpan elapsed, Uri uri) => new()
    {
        Kind = response.Kind,
        StatusCode = response.StatusCode,
        ReasonPhrase = response.ReasonPhrase,
        Succeeded = response.Succeeded,
        Elapsed = elapsed,
        SizeBytes = response.SizeBytes,
        ContentType = response.ContentType,
        Body = response.Body,
        IsBodyTruncated = response.IsBodyTruncated,
        BodyBytes = response.BodyBytes,
        Headers = response.Headers,
        Trailers = response.Trailers,
        Messages = response.Messages,
        Timings = response.Timings,
        EffectiveUrl = response.EffectiveUrl ?? uri.ToString(),
        Error = response.Error
    };

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
