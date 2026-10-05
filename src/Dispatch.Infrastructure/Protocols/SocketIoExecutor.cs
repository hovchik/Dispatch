using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Auth;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Infrastructure.Protocols;

/// <summary>
/// Socket.IO v4+ client (Engine.IO protocol 4 over WebSocket): connects to a namespace, emits an event with
/// arguments (and acknowledgement), answers pings, and logs incoming events.
/// </summary>
public sealed class SocketIoExecutor(WebSocketConnector connector) : IProtocolExecutor
{
    public IReadOnlyCollection<RequestKind> Kinds { get; } = [RequestKind.SocketIo];

    public async Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken)
    {
        var settings = request.Protocol.SocketIo;
        var uri = BuildUri(request);
        var ns = NormalizeNamespace(settings.Namespace);
        var listen = settings.ListenEvents.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        using var log = new MessageLog(context, settings.ListenSeconds, 0, cancellationToken);

        ClientWebSocket socket;
        try
        {
            socket = await connector.ConnectAsync(uri, AuthHeaders.Combined(request), [], request.Settings, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
        {
            return ApiResponse.Failed(ex is OperationCanceledException ? "Connection timed out." : WebSocketExecutor.Describe(ex),
                log.Stopwatch.Elapsed, uri.ToString(), RequestKind.SocketIo);
        }

        using (socket)
        {
            string? error = null;
            var connected = false;
            var ackId = 0;
            var sendLock = new SemaphoreSlim(1, 1);

            async Task SendRaw(string packet)
            {
                await sendLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await WebSocketConnector.SendTextAsync(socket, packet, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    sendLock.Release();
                }
            }

            async Task Emit(JsonArray eventAndArgs)
            {
                var id = Interlocked.Increment(ref ackId);
                await SendRaw($"42{NamespacePrefix(ns)}{id}{eventAndArgs.ToJsonString()}").ConfigureAwait(false);
                log.Sent(eventAndArgs.Count > 1 ? JoinArgs(eventAndArgs) : "(no arguments)",
                    $"emit {eventAndArgs[0]} (ack {id})");
            }

            try
            {
                Task outgoing = Task.CompletedTask;
                while (!log.Token.IsCancellationRequested)
                {
                    var received = await WebSocketConnector.ReceiveAsync(socket, log.Token).ConfigureAwait(false);
                    if (received is null)
                    {
                        log.Info("Server closed the connection");
                        break;
                    }

                    var packet = received.Value.Text;
                    if (packet.Length == 0)
                        continue;

                    switch (packet[0])
                    {
                        case '0': // Engine.IO open
                            log.Info("Engine.IO handshake: " + packet[1..]);
                            var auth = string.IsNullOrWhiteSpace(settings.AuthPayload) ? "" : settings.AuthPayload.Trim();
                            await SendRaw($"40{NamespacePrefix(ns)}{auth}").ConfigureAwait(false);
                            break;
                        case '2': // ping
                            await SendRaw("3" + packet[1..]).ConfigureAwait(false);
                            break;
                        case '1': // Engine.IO close
                            log.Info("Server closed the session");
                            log.Stop();
                            break;
                        case '4':
                            var (type, packetNs, id, json) = ParseSocketPacket(packet[1..]);
                            if (packetNs != ns)
                                break;
                            switch (type)
                            {
                                case '0': // CONNECT
                                    connected = true;
                                    log.Info($"Connected to namespace {ns} {json}".TrimEnd());
                                    log.Listening();
                                    if (!string.IsNullOrWhiteSpace(settings.Event))
                                        await Emit(BuildEvent(settings.Event, settings.Arguments)).ConfigureAwait(false);
                                    if (context.Outgoing is not null)
                                        outgoing = PumpOutgoingAsync(context.Outgoing, settings.Event, Emit, log);
                                    break;
                                case '4': // CONNECT_ERROR
                                    error = json;
                                    log.Failure("Connection refused: " + json);
                                    log.Stop();
                                    break;
                                case '1': // DISCONNECT
                                    log.Info("Disconnected by the server");
                                    log.Stop();
                                    break;
                                case '2': // EVENT
                                    if (JsonNode.Parse(json) is JsonArray { Count: > 0 } args)
                                    {
                                        var name = args[0]?.ToString() ?? "?";
                                        if (listen.Count == 0 || listen.Contains(name))
                                            log.Received(JoinArgs(args), name, received.Value.Bytes);
                                        if (id is not null)
                                            await SendRaw($"43{NamespacePrefix(ns)}{id}[]").ConfigureAwait(false);
                                    }
                                    break;
                                case '3': // ACK
                                    log.Received(json, $"ack {id}", received.Value.Bytes);
                                    break;
                                default:
                                    log.Received(packet, "packet");
                                    break;
                            }
                            break;
                    }
                }
                log.Stop();
                await outgoing.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is WebSocketException or JsonException)
            {
                error ??= ex.Message;
                log.Failure(ex.Message);
            }

            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await SendRaw($"41{NamespacePrefix(ns)}").ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }
            }
            await WebSocketConnector.CloseQuietlyAsync(socket).ConfigureAwait(false);

            return new ApiResponse
            {
                Kind = RequestKind.SocketIo,
                StatusCode = connected ? 101 : 0,
                ReasonPhrase = error is null ? (connected ? "Connected" : "Not connected") : "Connection error",
                Succeeded = connected && error is null,
                Elapsed = log.Stopwatch.Elapsed,
                SizeBytes = log.ReceivedBytes,
                Messages = log.Messages,
                EffectiveUrl = uri.ToString()
            };
        }
    }

    /// <summary>Typed messages: a JSON array is sent as-is (<c>["event", arg1]</c>); anything else is the argument of the default event.</summary>
    private static async Task PumpOutgoingAsync(ChannelReader<string> outgoing, string defaultEvent,
        Func<JsonArray, Task> emit, MessageLog log)
    {
        try
        {
            await foreach (var text in outgoing.ReadAllAsync(log.Token).ConfigureAwait(false))
            {
                var trimmed = text.Trim();
                if (trimmed.StartsWith('[') && TryParse(trimmed) is JsonArray { Count: > 0 } array)
                    await emit(array).ConfigureAwait(false);
                else
                    await emit(BuildEvent(defaultEvent.Length > 0 ? defaultEvent : "message", text)).ConfigureAwait(false);
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

    internal static Uri BuildUri(ApiRequest request)
    {
        var settings = request.Protocol.SocketIo;
        var baseUri = WebSocketConnector.ToWebSocketUri(request.Url);
        var path = string.IsNullOrWhiteSpace(settings.Path) ? "/socket.io/" : settings.Path.Trim();
        if (!path.StartsWith('/'))
            path = "/" + path;
        if (!path.EndsWith('/'))
            path += "/";
        var builder = new UriBuilder(baseUri) { Path = path };
        var query = baseUri.Query.TrimStart('?');
        builder.Query = (query.Length > 0 ? query + "&" : "") + "EIO=4&transport=websocket";
        return new Uri(AuthHeaders.ApplyQuery(builder.Uri.ToString(), request.Auth));
    }

    internal static JsonArray BuildEvent(string eventName, string arguments)
    {
        var array = new JsonArray(JsonValue.Create(eventName));
        var trimmed = arguments.Trim();
        if (trimmed.Length == 0)
            return array;
        var parsed = TryParse(trimmed);
        if (parsed is JsonArray args)
            foreach (var arg in args.ToList())
                array.Add(arg?.DeepClone());
        else
            array.Add(parsed ?? JsonValue.Create(arguments));
        return array;
    }

    /// <summary>Splits a Socket.IO packet (after the Engine.IO "4"): type, namespace, ack id, JSON payload.</summary>
    internal static (char Type, string Namespace, int? Id, string Json) ParseSocketPacket(string packet)
    {
        if (packet.Length == 0)
            return ('?', "/", null, "");
        var type = packet[0];
        var i = 1;
        var ns = "/";
        if (i < packet.Length && packet[i] == '/')
        {
            var comma = packet.IndexOf(',', i);
            ns = comma < 0 ? packet[i..] : packet[i..comma];
            i = comma < 0 ? packet.Length : comma + 1;
        }
        var start = i;
        while (i < packet.Length && char.IsAsciiDigit(packet[i]))
            i++;
        int? id = i > start ? int.Parse(packet[start..i]) : null;
        return (type, ns, id, packet[i..]);
    }

    private static string NormalizeNamespace(string ns)
    {
        ns = ns.Trim();
        return ns.Length == 0 || ns == "/" ? "/" : ns.StartsWith('/') ? ns : "/" + ns;
    }

    private static string NamespacePrefix(string ns) => ns == "/" ? "" : ns + ",";

    private static string JoinArgs(JsonArray eventAndArgs) =>
        eventAndArgs.Count <= 1 ? "" :
        eventAndArgs.Count == 2 ? eventAndArgs[1]?.ToJsonString() ?? "null" :
        new JsonArray(eventAndArgs.Skip(1).Select(a => a?.DeepClone()).ToArray()).ToJsonString();

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
