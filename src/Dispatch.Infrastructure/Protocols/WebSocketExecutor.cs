using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Auth;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Infrastructure.Protocols;

/// <summary>Opens WebSocket connections using the same transport settings (TLS, proxy, client certificate) as HTTP.</summary>
public sealed class WebSocketConnector(IHttpClientSource clients)
{
    public async Task<ClientWebSocket> ConnectAsync(Uri uri, IEnumerable<KeyValuePair<string, string>> headers,
        IEnumerable<string> subprotocols, RequestSettings settings, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        foreach (var (name, value) in headers)
            socket.Options.SetRequestHeader(name, value);
        foreach (var protocol in subprotocols.Where(p => p.Length > 0))
            socket.Options.AddSubProtocol(protocol);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.TimeoutMs > 0 ? TimeSpan.FromMilliseconds(settings.TimeoutMs) : TimeSpan.FromSeconds(30));
        try
        {
            await socket.ConnectAsync(uri, clients.GetClient(settings), timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>ws(s):// URL from what the user typed (http(s):// and bare hosts are accepted).</summary>
    public static Uri ToWebSocketUri(string url)
    {
        url = url.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            url = "ws://" + url[7..];
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "wss://" + url[8..];
        else if (!url.Contains("://", StringComparison.Ordinal))
            url = "ws://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
            throw new RequestBuildException($"Invalid WebSocket URL: {url}");
        return uri;
    }

    /// <summary>Reads one whole message. Returns null when the server closed the connection.</summary>
    public static async Task<(string Text, bool Binary, int Bytes)?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            var bytes = message.ToArray();
            return result.MessageType == WebSocketMessageType.Binary
                ? ($"[binary {bytes.Length:N0} bytes] {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 256)))}", true, bytes.Length)
                : (Encoding.UTF8.GetString(bytes), false, bytes.Length);
        }
    }

    public static Task SendTextAsync(WebSocket socket, string text, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    /// <summary>
    /// Ends the session gracefully when <paramref name="stop"/> fires (user disconnect, listen window over, message limit):
    /// runs <paramref name="beforeClose"/>, then sends a Close frame so the pending receive completes with the server's
    /// Close frame and its status. Cancelling the receive instead would abort the socket without any Close handshake.
    /// If the server does not answer within 2 s the socket is aborted, which also ends the receive.
    /// </summary>
    public static CancellationTokenRegistration CloseOnStop(WebSocket socket, CancellationToken stop, Func<Task>? beforeClose = null) =>
        stop.UnsafeRegister(_ => _ = CloseOrAbortAsync(socket, beforeClose), null);

    private static async Task CloseOrAbortAsync(WebSocket socket, Func<Task>? beforeClose)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                if (beforeClose is not null && socket.State == WebSocketState.Open)
                    await beforeClose().WaitAsync(timeout.Token).ConfigureAwait(false);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Bye", timeout.Token).ConfigureAwait(false);
            }
            await Task.Delay(Timeout.Infinite, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
        try
        {
            if (socket.State is not (WebSocketState.Closed or WebSocketState.Aborted))
                socket.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public static async Task CloseQuietlyAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Bye", cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// Raw WebSocket sessions: sends the initial messages, then relays messages typed in the UI while logging everything
/// received until the user disconnects (interactive) or the listen time / message limit is reached.
/// </summary>
public sealed class WebSocketExecutor(WebSocketConnector connector) : IProtocolExecutor
{
    public IReadOnlyCollection<RequestKind> Kinds { get; } = [RequestKind.WebSocket];

    public async Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken)
    {
        var settings = request.Protocol.Stream;
        var uri = WebSocketConnector.ToWebSocketUri(AuthHeaders.ApplyQuery(request.Url, request.Auth));
        using var log = new MessageLog(context, settings.ListenSeconds, settings.MaxMessages, cancellationToken);

        ClientWebSocket socket;
        try
        {
            socket = await connector.ConnectAsync(uri, AuthHeaders.Combined(request),
                settings.Subprotocols.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                request.Settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApiResponse.Failed("Connection cancelled.", log.Stopwatch.Elapsed, uri.ToString(), RequestKind.WebSocket);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
        {
            var reason = ex is OperationCanceledException ? "Connection timed out." : Describe(ex);
            return ApiResponse.Failed(reason, log.Stopwatch.Elapsed, uri.ToString(), RequestKind.WebSocket);
        }

        using (socket)
        {
            var connectedIn = log.Stopwatch.Elapsed;
            log.Info($"Connected to {uri}" + (socket.SubProtocol is { Length: > 0 } p ? $" (subprotocol {p})" : ""));

            try
            {
                foreach (var message in settings.InitialMessages.Where(m => m.Enabled && m.Value.Length > 0))
                {
                    await WebSocketConnector.SendTextAsync(socket, message.Value, log.Token).ConfigureAwait(false);
                    log.Sent(message.Value);
                }
                log.Listening();

                var sending = context.Outgoing is null ? Task.CompletedTask : PumpOutgoingAsync(socket, context.Outgoing, log);
                // The receive itself is never cancelled: stopping sends a Close frame and the loop ends with the server's reply.
                using (WebSocketConnector.CloseOnStop(socket, log.Token))
                {
                    while (true)
                    {
                        var received = await WebSocketConnector.ReceiveAsync(socket, CancellationToken.None).ConfigureAwait(false);
                        if (received is null)
                        {
                            var closeInfo = $"{socket.CloseStatus} {socket.CloseStatusDescription}".Trim();
                            log.Info(log.Token.IsCancellationRequested ? $"Connection closed: {closeInfo}" : $"Server closed the connection: {closeInfo}");
                            break;
                        }
                        log.Received(received.Value.Text, received.Value.Binary ? "binary" : null, received.Value.Bytes);
                    }
                }
                log.Stop();
                await sending.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disconnected by the user, or the listen window ended.
            }
            catch (WebSocketException ex) when (!log.Token.IsCancellationRequested)
            {
                log.Failure(Describe(ex));
            }
            catch (WebSocketException)
            {
                // The server dropped the connection instead of answering our Close frame: we were leaving anyway.
            }

            await WebSocketConnector.CloseQuietlyAsync(socket).ConfigureAwait(false);
            log.Info("Disconnected");

            return new ApiResponse
            {
                Kind = RequestKind.WebSocket,
                StatusCode = 101,
                ReasonPhrase = socket.CloseStatus is { } status ? $"Closed ({(int)status} {status})" : "Switching Protocols",
                Succeeded = true,
                Elapsed = log.Stopwatch.Elapsed,
                SizeBytes = log.ReceivedBytes,
                Messages = log.Messages,
                Headers = socket.HttpResponseHeaders?.Select(h => new ResponseHeader(h.Key, string.Join(", ", h.Value))).ToList() ?? [],
                Timings = new ResponseTimings(Connect: connectedIn),
                EffectiveUrl = uri.ToString()
            };
        }
    }

    private static async Task PumpOutgoingAsync(WebSocket socket, ChannelReader<string> outgoing, MessageLog log)
    {
        try
        {
            await foreach (var text in outgoing.ReadAllAsync(log.Token).ConfigureAwait(false))
            {
                await WebSocketConnector.SendTextAsync(socket, text, log.Token).ConfigureAwait(false);
                log.Sent(text);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            log.Failure("Send failed: " + ex.Message);
        }
    }

    internal static string Describe(Exception ex) => ex.InnerException is { } inner && ex.Message.Length < 80
        ? $"{ex.Message} ({inner.Message})"
        : ex.Message;
}
