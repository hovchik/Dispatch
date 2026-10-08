using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Dispatch.Application.Capture;
using Dispatch.Domain;

namespace Dispatch.Infrastructure.Capture;

public sealed class CaptureProxyOptions
{
    public int Port { get; init; } = 8899;
    public bool Public { get; init; }

    /// <summary>Only record exchanges whose host contains this (case-insensitive); empty records everything.</summary>
    public string HostFilter { get; init; } = string.Empty;

    /// <summary>Decrypt HTTPS by terminating TLS with per-host certs (the CA must be trusted by the client).</summary>
    public bool DecryptHttps { get; init; } = true;
    public int MaxBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>Sees every request and may replace responses before they reach the client (client fuzzing).</summary>
    public Dispatch.Application.ClientFuzz.IResponseInterceptor? Interceptor { get; init; }
}

/// <summary>
/// A capturing forward proxy. Point a browser, app or <c>HTTP(S)_PROXY</c> at it and every request/response is recorded.
/// Plain HTTP is proxied in absolute-URI form; HTTPS uses CONNECT, and is decrypted by minting a per-host certificate
/// from the local CA (clients must trust the CA to avoid warnings). Record into history, then save to a collection.
/// </summary>
public sealed class CaptureProxy(CertificateAuthority authority) : IAsyncDisposable
{
    /// <summary>Request bodies are buffered before forwarding; larger ones are refused with 413.</summary>
    private const long MaxForwardBodyBytes = 256L * 1024 * 1024;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private CaptureProxyOptions _options = new();
    private readonly HttpClient _client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseProxy = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });

    public event Action<CapturedExchange>? Captured;
    public bool IsRunning => _listener is not null;
    public int Port { get; private set; }
    public string CaCertificatePem => authority.CaCertificatePem;

    public Task StartAsync(CaptureProxyOptions options)
    {
        if (_listener is not null)
            throw new InvalidOperationException("The capture proxy is already running.");
        _options = options;
        _listener = new TcpListener(options.Public ? IPAddress.Any : IPAddress.Loopback, options.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_listener, _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;
        await _cts.CancelAsync();
        _listener?.Stop();
        _listener = null;
        _cts.Dispose();
        _cts = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                await using var network = client.GetStream();
                var requestLine = await ReadLineAsync(network, ct).ConfigureAwait(false);
                if (requestLine is null)
                    return;
                var parts = requestLine.Split(' ');
                if (parts.Length < 3)
                    return;

                if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                    await HandleConnectAsync(network, parts[1], ct).ConfigureAwait(false);
                else
                    await HandlePlainAsync(network, parts[0], parts[1], ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A client that hangs up mid-request, or sends garbage (a bad chunk size, an invalid URL, ...), is normal for a
            // proxy. The task is fire-and-forget, so nothing may escape here.
        }
    }

    /// <summary>
    /// Host and port of a CONNECT target (<c>host:443</c>, <c>[::1]:8443</c>, <c>host</c>). Splitting on ':' would mangle
    /// IPv6 literals.
    /// </summary>
    internal static bool TryParseConnectAuthority(string authority, out string host, out int port)
    {
        host = "";
        port = 443;
        if (authority.Length == 0 || authority.Contains('/') || !Uri.TryCreate("https://" + authority, UriKind.Absolute, out var uri)
            || uri.HostNameType == UriHostNameType.Unknown || uri.IdnHost.Length == 0)
            return false;
        host = uri.IdnHost;
        port = uri.Port;
        return true;
    }

    // ---- Plain HTTP (absolute-form request target) -------------------------------------------------

    private async Task HandlePlainAsync(Stream stream, string method, string absoluteUri, CancellationToken ct)
    {
        var headers = await ReadHeadersAsync(stream, ct).ConfigureAwait(false);
        var body = await ReadBodyAsync(stream, headers, ct).ConfigureAwait(false);
        var response = body is null
            ? PayloadTooLarge
            : await ForwardAsync(method, absoluteUri, headers, body, secure: false, ct).ConfigureAwait(false);
        await WriteResponseAsync(stream, response, ct).ConfigureAwait(false);
    }

    // ---- HTTPS via CONNECT -------------------------------------------------------------------------

    private async Task HandleConnectAsync(Stream clientStream, string authority2, CancellationToken ct)
    {
        await ReadHeadersAsync(clientStream, ct).ConfigureAwait(false); // drain CONNECT headers
        if (!TryParseConnectAuthority(authority2, out var host, out var port))
        {
            await WriteAsync(clientStream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
            return;
        }

        await WriteAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", ct).ConfigureAwait(false);

        if (!_options.DecryptHttps)
        {
            // Blind tunnel: pipe bytes both ways without decrypting (nothing is recorded).
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(host, port, ct).ConfigureAwait(false);
            await using var upstreamStream = upstream.GetStream();
            await Task.WhenAny(clientStream.CopyToAsync(upstreamStream, ct), upstreamStream.CopyToAsync(clientStream, ct)).ConfigureAwait(false);
            return;
        }

        var leaf = authority.GetCertificate(host);
        await using var tls = new SslStream(clientStream, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = leaf,
            ClientCertificateRequired = false
        }, ct).ConfigureAwait(false);

        // Keep handling requests on the decrypted, keep-alive connection.
        while (!ct.IsCancellationRequested)
        {
            var line = await ReadLineAsync(tls, ct).ConfigureAwait(false);
            if (line is null)
                break;
            var parts = line.Split(' ');
            if (parts.Length < 3)
                break;
            var headers = await ReadHeadersAsync(tls, ct).ConfigureAwait(false);
            var body = await ReadBodyAsync(tls, headers, ct).ConfigureAwait(false);
            var url = $"https://{authority2}{parts[1]}";
            var response = body is null
                ? PayloadTooLarge
                : await ForwardAsync(parts[0], url, headers, body, secure: true, ct).ConfigureAwait(false);
            await WriteResponseAsync(tls, response, ct).ConfigureAwait(false);
            if (ResponseClosesConnection(response))
                break;
        }
    }

    // ---- Forwarding and recording ------------------------------------------------------------------

    private async Task<ProxyResponse> ForwardAsync(string method, string url, List<KeyValueItem> headers, byte[] body,
        bool secure, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var record = ShouldRecord(url);
        var exchange = record
            ? new CapturedExchange
            {
                Timestamp = started, Method = method, Url = url, Secure = secure,
                RequestHeaders = headers.Select(h => h.Clone()).ToList(),
                RequestBody = body.Length > 0 ? SafeText(body) : "",
                RequestContentType = HeaderValue(headers, "Content-Type")
            }
            : null;

        _options.Interceptor?.OnRequest(method, url, body.Length > 0 ? SafeText(body) : "", started);
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (body.Length > 0)
                request.Content = new ByteArrayContent(body);
            foreach (var h in headers)
            {
                if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || h.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!request.Headers.TryAddWithoutValidation(h.Key, h.Value))
                    request.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var result = new ProxyResponse((int)response.StatusCode, response.ReasonPhrase ?? "",
                CollectHeaders(response), responseBody);

            // Client fuzzing: the interceptor may hand the client a changed (or delayed) response instead.
            if (_options.Interceptor?.Intercept(new Dispatch.Application.ClientFuzz.InterceptedResponse(method, url, result.Status,
                    response.Content.Headers.ContentType?.ToString(), SafeText(responseBody)), DateTimeOffset.Now) is { } changed)
            {
                if (changed.Delay > TimeSpan.Zero)
                    await Task.Delay(changed.Delay, ct).ConfigureAwait(false);
                result = new ProxyResponse(changed.Status, changed.Reason,
                    result.Headers.Where(h => h.Name is not ("Content-Encoding" or "ETag" or "Content-MD5" or "Last-Modified"))
                        .Append(new ProxyHeader("X-Dispatch-Fuzz", new string(changed.Note.Where(c => c is >= ' ' and <= '~').Take(200).ToArray()))).ToList(),
                    Encoding.UTF8.GetBytes(changed.Body));
                responseBody = result.Body;
            }

            if (exchange is not null)
            {
                exchange.StatusCode = result.Status;
                exchange.ReasonPhrase = result.Reason;
                // (When fuzzing, the exchange shows what the client actually received.)
                exchange.ResponseHeaders = result.Headers.Select(h => new KeyValueItem(h.Name, h.Value)).ToList();
                exchange.ResponseContentType = response.Content.Headers.ContentType?.ToString();
                exchange.ResponseSize = responseBody.Length;
                exchange.ResponseBody = SafeText(responseBody);
                exchange.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                Publish(exchange);
            }
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            if (exchange is not null)
            {
                exchange.Error = ex.Message;
                exchange.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                Publish(exchange);
            }
            var message = Encoding.UTF8.GetBytes($"Capture proxy could not reach the upstream server: {ex.Message}");
            return new ProxyResponse(502, "Bad Gateway",
                [new ProxyHeader("Content-Type", "text/plain"), new ProxyHeader("Content-Length", message.Length.ToString())], message);
        }
    }

    private bool ShouldRecord(string url) =>
        _options.HostFilter.Length == 0 ||
        (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Contains(_options.HostFilter, StringComparison.OrdinalIgnoreCase));

    private void Publish(CapturedExchange exchange) => Captured?.Invoke(exchange);

    private string SafeText(byte[] bytes)
    {
        var slice = bytes.Length > _options.MaxBodyBytes ? bytes[.._options.MaxBodyBytes] : bytes;
        // Heuristic: treat as binary if it has NUL bytes in the first chunk.
        if (Array.IndexOf(slice, (byte)0, 0, Math.Min(slice.Length, 512)) >= 0)
            return $"[binary, {bytes.Length} bytes]";
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(slice);
        }
        catch (DecoderFallbackException)
        {
            return $"[binary, {bytes.Length} bytes]";
        }
    }

    /// <summary>One line per value: joining them with ", " would break Set-Cookie (whose values contain commas) for browsers.</summary>
    private static List<ProxyHeader> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new List<ProxyHeader>();
        foreach (var h in response.Headers)
            foreach (var value in h.Value)
                headers.Add(new ProxyHeader(h.Key, value));
        foreach (var h in response.Content.Headers)
            if (!h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                foreach (var value in h.Value)
                    headers.Add(new ProxyHeader(h.Key, value));
        return headers;
    }

    private static bool ResponseClosesConnection(ProxyResponse response) =>
        response.Headers.Any(h => h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                                  && h.Value.Contains("close", StringComparison.OrdinalIgnoreCase));

    private static string? HeaderValue(List<KeyValueItem> headers, string name) =>
        headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    // ---- Raw HTTP/1.1 parsing / writing ------------------------------------------------------------

    private sealed record ProxyHeader(string Name, string Value);
    private sealed record ProxyResponse(int Status, string Reason, List<ProxyHeader> Headers, byte[] Body);

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(128);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0)
                return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());
            if (one[0] == '\n')
                break;
            if (one[0] != '\r')
                buffer.Add(one[0]);
            if (buffer.Count > 64 * 1024)
                throw new IOException("Request line too long.");
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private static async Task<List<KeyValueItem>> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        var headers = new List<KeyValueItem>();
        while (true)
        {
            var line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line))
                break;
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers.Add(new KeyValueItem(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return headers;
    }

    /// <summary>The request body, or null when it is too large to forward (the caller answers 413).</summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream stream, List<KeyValueItem> headers, CancellationToken ct)
    {
        var transferEncoding = HeaderValue(headers, "Transfer-Encoding");
        if (transferEncoding is not null && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            return await ReadChunkedAsync(stream, ct).ConfigureAwait(false);

        if (HeaderValue(headers, "Content-Length") is { } lengthText && long.TryParse(lengthText, out var length) && length > 0)
        {
            // The whole body is forwarded upstream (MaxBodyBytes only caps what is recorded); a truncated body must never
            // be passed off as the full request. Bodies we cannot hold in memory are refused with 413 instead.
            if (length > MaxForwardBodyBytes)
                return null;
            var buffer = new byte[(int)length];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                offset += read;
            }
            return offset == buffer.Length ? buffer : buffer[..offset];
        }
        return [];
    }

    private static readonly ProxyResponse PayloadTooLarge = new(413, "Payload Too Large",
        [new ProxyHeader("Content-Type", "text/plain"), new ProxyHeader("Connection", "close")],
        Encoding.UTF8.GetBytes($"Capture proxy: request bodies over {MaxForwardBodyBytes:N0} bytes are not forwarded."));

    private static async Task<byte[]> ReadChunkedAsync(Stream stream, CancellationToken ct)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            if (sizeLine is null)
                break;
            var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
            if (size == 0)
            {
                await ReadLineAsync(stream, ct).ConfigureAwait(false); // trailing CRLF
                break;
            }
            var chunk = new byte[size];
            var offset = 0;
            while (offset < size)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(offset), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                offset += read;
            }
            body.Write(chunk, 0, offset);
            await ReadLineAsync(stream, ct).ConfigureAwait(false); // CRLF after chunk
        }
        return body.ToArray();
    }

    private static async Task WriteResponseAsync(Stream stream, ProxyResponse response, CancellationToken ct)
    {
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {response.Status} {response.Reason}\r\n");
        foreach (var h in response.Headers.Where(h => !h.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
            head.Append($"{h.Name}: {h.Value}\r\n");
        if (!response.Headers.Any(h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
            head.Append($"Content-Length: {response.Body.Length}\r\n");
        head.Append("\r\n");
        await WriteAsync(stream, head.ToString(), ct).ConfigureAwait(false);
        if (response.Body.Length > 0)
            await stream.WriteAsync(response.Body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static Task WriteAsync(Stream stream, string text, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();
}
