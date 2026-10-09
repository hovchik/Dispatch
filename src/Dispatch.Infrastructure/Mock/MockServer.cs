using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dispatch.Application.Grpc;
using Dispatch.Application.Mock;
using Dispatch.Application.Variables;
using Dispatch.Domain;
using Dispatch.Infrastructure.Protocols.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Dispatch.Infrastructure.Mock;

public sealed class MockServerOptions
{
    public int Port { get; init; } = 3000;

    /// <summary>Optional HTTP/2 cleartext port for gRPC mocks (gRPC needs HTTP/2; cleartext HTTP/2 needs its own port).</summary>
    public int? GrpcPort { get; init; }

    /// <summary>Listen on all interfaces instead of only localhost.</summary>
    public bool Public { get; init; }
    public int LatencyMs { get; init; }
    public int LatencyJitterMs { get; init; }

    /// <summary>Fraction (0-1) of requests answered with <see cref="ErrorStatus"/> to test client resilience.</summary>
    public double ErrorRate { get; init; }
    public int ErrorStatus { get; init; } = 500;

    /// <summary>Fraction (0-1) of requests whose connection is dropped without a response.</summary>
    public double DropRate { get; init; }
    public bool Cors { get; init; } = true;

    /// <summary>Generate fresh, realistic data on every request from the example's schema (or its shape).</summary>
    public bool DynamicData { get; init; }

    /// <summary>Remember created, updated and deleted items (POST / PUT / PATCH / DELETE) in memory.</summary>
    public bool Stateful { get; init; }

    /// <summary>Seed for generated data, for repeatable mocks.</summary>
    public int? Seed { get; init; }

    /// <summary>Replay speed for recorded WebSocket / SSE sessions: 1 = original timing, 0 = no delays.</summary>
    public double SessionSpeed { get; init; } = 1;
}

public sealed record MockLogEntry(DateTimeOffset Time, string Method, string Path, int Status, string? Matched, double Milliseconds, string? Note = null);

/// <summary>
/// Serves saved examples as a local API: HTTP (with SOAP action and GraphQL routing) and gRPC unary/server-streaming.
/// Example bodies may use <c>{{pathParam}}</c>, <c>{{queryParam}}</c> and dynamic variables like <c>{{$guid}}</c>.
/// </summary>
public sealed class MockServer(GrpcSchemaProvider? grpcSchemas = null) : IAsyncDisposable
{
    private WebApplication? _app;
    private MockRouteTable _routes = MockRouteTable.Build([]);
    private Dictionary<string, (ApiRequest Request, MethodDesc Method, ProtoJson Codec)> _grpc = new(StringComparer.Ordinal);
    private MockServerOptions _options = new();
    private MockState? _state;
    private SchemaFaker _schemaFaker = new();
    private readonly Lock _fakerGate = new();

    public event Action<MockLogEntry>? RequestHandled;

    public bool IsRunning => _app is not null;
    public Uri? BaseUrl { get; private set; }
    public Uri? GrpcUrl { get; private set; }
    public IReadOnlyList<MockRoute> Routes => _routes.Routes;
    public IReadOnlyCollection<string> GrpcRoutes => _grpc.Keys;
    public List<string> Warnings { get; } = [];

    public async Task StartAsync(IEnumerable<ApiRequest> requests, MockServerOptions options, CancellationToken ct = default)
    {
        if (_app is not null)
            throw new InvalidOperationException("The mock server is already running.");
        _options = options;
        var list = requests.ToList();
        _routes = MockRouteTable.Build(list);
        if (options.Seed is { } seed)
        {
            var random = new Random(seed);
            _schemaFaker = new SchemaFaker(new Faker(random), random);
        }
        _state = options.Stateful ? new MockState(_routes, (_, example) => RenderBody(example, new Dictionary<string, string>())) : null;
        _grpc = await BuildGrpcRoutesAsync(list, ct).ConfigureAwait(false);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            var address = options.Public ? IPAddress.Any : IPAddress.Loopback;
            k.Listen(address, options.Port, o => o.Protocols = HttpProtocols.Http1AndHttp2);
            if (options.GrpcPort is { } grpcPort)
                k.Listen(address, grpcPort, o => o.Protocols = HttpProtocols.Http2);
            k.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
        });
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(HandleAsync);
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // A port in use, or cancellation: release the host so the next Start can bind.
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _app = app;

        var urls = app.Urls.Select(u => new Uri(u.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost").Replace("127.0.0.1", "localhost"))).ToList();
        BaseUrl = urls.FirstOrDefault(u => options.Port == 0 || u.Port == options.Port) ?? urls.FirstOrDefault();
        GrpcUrl = options.GrpcPort is null ? null : urls.FirstOrDefault(u => u.Port == options.GrpcPort) ?? urls.LastOrDefault();
    }

    public async Task StopAsync()
    {
        if (_app is null)
            return;
        var app = _app;
        _app = null;
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>Forgets items created by stateful mocks.</summary>
    public void ResetState() => _state?.Reset();

    /// <summary>The stateful store as JSON (<c>{"/pets":[…]}</c>), or null when the server is not stateful.</summary>
    public JsonObject? ExportState() => _state?.Export();

    /// <summary>Loads collections into the stateful store (json-server style <c>{"pets":[…]}</c>); returns how many were loaded.</summary>
    public int ImportState(JsonObject data) => _state?.Import(data) ?? throw new InvalidOperationException("The mock server is not stateful.");

    /// <summary>
    /// The example body to send: generated from its schema (or its own shape) in dynamic mode, then with
    /// <c>{{placeholders}}</c> resolved. Hand-written templates (bodies containing <c>{{</c>) are never replaced.
    /// </summary>
    private string RenderBody(ResponseExample example, IReadOnlyDictionary<string, string> variables,
        IReadOnlyList<KeyValuePair<string, string>>? pathVariables = null)
    {
        var body = example.Body;
        if (_options.DynamicData && !body.Contains("{{", StringComparison.Ordinal))
        {
            try
            {
                JsonNode? generated = null;
                // Kestrel handles requests concurrently, but a seeded System.Random (shared by the faker) is not thread-safe.
                lock (_fakerGate)
                {
                    if (example.Schema.Trim().Length > 0)
                        generated = _schemaFaker.Generate(JsonNode.Parse(example.Schema));
                    else if (body.TrimStart() is ['{', ..] or ['[', ..])
                        generated = _schemaFaker.GenerateLike(JsonNode.Parse(body));
                }
                if (generated is JsonObject obj && pathVariables is { Count: > 0 })
                    ReflectPath(obj, pathVariables);
                if (generated is not null)
                    body = generated.ToJsonString();
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON after all: serve it as written.
            }
        }
        return VariableResolver.Resolve(body, variables);
    }

    /// <summary>
    /// Generated data agrees with the URL: <c>GET /users/42</c> answers <c>{"id":42,…}</c>, and <c>/users/7/orders/3</c>
    /// sets <c>userId</c> and <c>id</c>. A path parameter lands on the property of the same name, or on <c>id</c>
    /// when the parameter is id-like (<c>orderId</c>) and no property matches its name.
    /// </summary>
    private static void ReflectPath(JsonObject obj, IReadOnlyList<KeyValuePair<string, string>> pathVariables)
    {
        foreach (var (name, value) in pathVariables)
        {
            var key = obj.ContainsKey(name) ? name
                : name.EndsWith("id", StringComparison.OrdinalIgnoreCase) && obj.ContainsKey("id") ? "id"
                : null;
            if (key is null)
                continue;
            var numeric = obj[key] is JsonValue existing && existing.GetValueKind() == System.Text.Json.JsonValueKind.Number;
            obj[key] = numeric && long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? JsonValue.Create(number)
                : JsonValue.Create(value);
        }
    }

    /// <summary>
    /// Header values Kestrel accepts: it rejects non-ASCII and control characters, and request or example names
    /// ("Créer un utilisateur") end up in <c>X-Mock-Match</c>.
    /// </summary>
    internal static string HeaderSafe(string value)
    {
        if (value.All(c => c >= 0x20 && c < 0x7F || c == '\t'))
            return value;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(c >= 0x20 && c < 0x7F || c == '\t' ? c : '?');
        return sb.ToString();
    }

    /// <summary>Path and query parameters, plus <c>{{body.field}}</c>, <c>{{header.name}}</c> and <c>{{method}}</c> of the request.</summary>
    private static Dictionary<string, string> RequestVariables(MockMatch match, HttpRequest request, string body)
    {
        var variables = new Dictionary<string, string>(match.Variables, StringComparer.Ordinal)
        {
            ["method"] = request.Method,
            ["path"] = request.Path.Value ?? "/"
        };
        foreach (var header in request.Headers)
            variables["header." + header.Key.ToLowerInvariant()] = header.Value.ToString();
        try
        {
            if (body.TrimStart() is ['{', ..] && JsonNode.Parse(body) is JsonObject json)
                Flatten(json, "body.", variables, 0);
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return variables;

        static void Flatten(JsonObject obj, string prefix, Dictionary<string, string> into, int depth)
        {
            foreach (var (key, value) in obj)
            {
                if (value is JsonObject child && depth < 3)
                    Flatten(child, prefix + key + ".", into, depth + 1);
                into[prefix + key] = value switch
                {
                    null => "null",
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    _ => value.ToJsonString()
                };
            }
        }
    }

    private async Task<Dictionary<string, (ApiRequest, MethodDesc, ProtoJson)>> BuildGrpcRoutesAsync(List<ApiRequest> requests, CancellationToken ct)
    {
        var routes = new Dictionary<string, (ApiRequest, MethodDesc, ProtoJson)>(StringComparer.Ordinal);
        foreach (var request in requests.Where(r => r.Kind == RequestKind.Grpc && r.Examples.Count > 0))
        {
            if (grpcSchemas is null)
                break;
            try
            {
                var schema = await grpcSchemas.GetSchemaAsync(request, refresh: false, ct).ConfigureAwait(false);
                var (service, method) = schema.FindMethod(request.Protocol.Grpc.Service, request.Protocol.Grpc.Method);
                routes[$"/{service.FullName}/{method.Name}"] = (request, method, new ProtoJson(schema));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Warnings.Add($"gRPC mock for '{request.Name}' skipped: {ex.Message}");
            }
        }
        return routes;
    }

    private async Task HandleAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var request = context.Request;
        var response = context.Response;
        var path = request.Path.Value ?? "/";

        if (_options.Cors)
        {
            response.Headers.AccessControlAllowOrigin = request.Headers.Origin.Count > 0 ? request.Headers.Origin.ToString() : "*";
            response.Headers.AccessControlAllowCredentials = "true";
            response.Headers.AccessControlExposeHeaders = "*";
            if (HttpMethods.IsOptions(request.Method) && request.Headers.ContainsKey("Access-Control-Request-Method"))
            {
                response.Headers.AccessControlAllowMethods = "GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS";
                response.Headers.AccessControlAllowHeaders = request.Headers.AccessControlRequestHeaders.Count > 0
                    ? request.Headers.AccessControlRequestHeaders.ToString()
                    : "*";
                response.StatusCode = 204;
                Log(request.Method, path, 204, "CORS preflight", stopwatch);
                return;
            }
        }

        await SimulateLatencyAsync(context.RequestAborted).ConfigureAwait(false);

        if (_options.DropRate > 0 && Random.Shared.NextDouble() < _options.DropRate)
        {
            Log(request.Method, path, 0, null, stopwatch, "connection dropped (fault injection)");
            context.Abort();
            return;
        }

        if (request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true)
        {
            await HandleGrpcAsync(context, stopwatch).ConfigureAwait(false);
            return;
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            await ReplayWebSocketAsync(context, path, stopwatch).ConfigureAwait(false);
            return;
        }

        if (_options.ErrorRate > 0 && Random.Shared.NextDouble() < _options.ErrorRate)
        {
            response.StatusCode = _options.ErrorStatus;
            response.ContentType = "application/json";
            await response.WriteAsync($$"""{"error":"Injected failure","status":{{_options.ErrorStatus}}}""").ConfigureAwait(false);
            Log(request.Method, path, _options.ErrorStatus, null, stopwatch, "error injected");
            return;
        }

        string body;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);

        var query = request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var match = _routes.Match(request.Method, path, query, headers, body);

        // A recorded SSE session: for clients asking for an event stream, or GETs no HTTP route handles.
        if (HttpMethods.IsGet(request.Method)
            && (request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) || match is null)
            && _routes.Match(SessionMethods.Sse, path, query, headers, "") is { Example: { } sseExample } sse)
        {
            await ReplaySseAsync(context, path, sse.Request, sseExample, stopwatch).ConfigureAwait(false);
            return;
        }

        if (match is null)
        {
            response.StatusCode = 404;
            response.ContentType = "application/json";
            var available = new JsonArray(_routes.Routes.Select(r => (JsonNode?)JsonValue.Create($"{r.Method} {r.Template}")).ToArray());
            await response.WriteAsync(new JsonObject
            {
                ["error"] = $"No mock matches {request.Method} {path}",
                ["routes"] = available
            }.ToJsonString()).ConfigureAwait(false);
            Log(request.Method, path, 404, null, stopwatch, "no matching route");
            return;
        }

        if (_state?.Handle(request.Method, match, body, query, path) is { } reply)
        {
            response.StatusCode = reply.Status;
            response.Headers["X-Mock-Match"] = HeaderSafe($"{match.Request.Name} / {reply.Note}");
            foreach (var (key, value) in reply.Headers ?? new Dictionary<string, string>())
                response.Headers[key] = HeaderSafe(value);
            if (reply.Body.Length > 0 && !HttpMethods.IsHead(request.Method))
            {
                response.ContentType = "application/json";
                await response.WriteAsync(reply.Body).ConfigureAwait(false);
            }
            Log(request.Method, path, reply.Status, match.Request.Name, stopwatch, reply.Note);
            return;
        }

        if (match.Example is null)
        {
            response.StatusCode = 501;
            response.ContentType = "application/json";
            await response.WriteAsync($$"""{"error":"'{{match.Request.Name}}' has no saved example to serve."}""").ConfigureAwait(false);
            Log(request.Method, path, 501, match.Request.Name, stopwatch, "no example");
            return;
        }

        var example = match.Example;
        var variables = RequestVariables(match, request, body);
        response.StatusCode = example.StatusCode;
        foreach (var header in example.Headers.Where(h => h.IsActive && !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                                                          && !h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
            response.Headers[header.Key] = HeaderSafe(VariableResolver.Resolve(header.Value, variables));
        if (!string.IsNullOrEmpty(example.ContentType))
            response.ContentType = example.ContentType;
        response.Headers["X-Mock-Match"] = HeaderSafe($"{match.Request.Name} / {example.Name}");

        if (!HttpMethods.IsHead(request.Method) && (example.Body.Length > 0 || (_options.DynamicData && example.Schema.Trim().Length > 0)))
            await response.WriteAsync(RenderBody(example, variables, match.PathVariables)).ConfigureAwait(false);
        Log(request.Method, path, example.StatusCode, $"{match.Request.Name} / {example.Name}", stopwatch);
    }

    // ---- Recorded streaming sessions ---------------------------------------------------------------

    private SessionReplayOptions SessionReplayOptions => new() { Speed = _options.SessionSpeed };

    /// <summary>Replays a recorded WebSocket session, answering each client message with the matching recorded segment.</summary>
    private async Task ReplayWebSocketAsync(HttpContext context, string path, Stopwatch stopwatch)
    {
        var query = context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
        var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        if (_routes.Match(SessionMethods.WebSocket, path, query, headers, "") is not { Example: { } example } match)
        {
            context.Response.StatusCode = 404;
            Log("WS", path, 404, null, stopwatch, "no recorded WebSocket session for this path");
            return;
        }

        var name = $"{match.Request.Name} / {example.Name}";
        var protocols = context.WebSockets.WebSocketRequestedProtocols;
        using var socket = await context.WebSockets.AcceptWebSocketAsync(protocols.Count > 0 ? protocols[0] : null).ConfigureAwait(false);
        var player = new SessionPlayer(example.Session, SessionReplayOptions);
        Log("WS", path, 101, name, stopwatch, $"connected · replaying {player.SegmentCount} recorded exchange(s)");

        var ct = context.RequestAborted;
        var outgoing = System.Threading.Channels.Channel.CreateUnbounded<IReadOnlyList<ScheduledMessage>>();
        outgoing.Writer.TryWrite(player.Opening());

        // One sender, so replies keep their order and frames never interleave.
        var sending = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    foreach (var message in batch)
                    {
                        if (message.Delay > TimeSpan.Zero)
                            await Task.Delay(message.Delay, ct).ConfigureAwait(false);
                        if (socket.State != System.Net.WebSockets.WebSocketState.Open)
                            return;
                        await socket.SendAsync(Encoding.UTF8.GetBytes(message.Content), System.Net.WebSockets.WebSocketMessageType.Text, true, ct)
                            .ConfigureAwait(false);
                    }
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.WebSockets.WebSocketException)
            {
            }
        }, CancellationToken.None);

        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                using var frame = new MemoryStream();
                System.Net.WebSockets.WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    frame.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                if (received.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
                    break;
                }
                var reply = player.OnClientMessage(Encoding.UTF8.GetString(frame.ToArray()));
                outgoing.Writer.TryWrite(reply.Messages);
                Log("WS", path, 101, name, stopwatch, $"client message: {reply.How}");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.WebSockets.WebSocketException)
        {
        }
        outgoing.Writer.TryComplete();
        await sending.ConfigureAwait(false);
        Log("WS", path, 101, name, stopwatch, "disconnected");
    }

    /// <summary>Streams a recorded SSE session's events with their original timing, then ends the stream.</summary>
    private async Task ReplaySseAsync(HttpContext context, string path, ApiRequest request, ResponseExample example, Stopwatch stopwatch)
    {
        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Mock-Match"] = HeaderSafe($"{request.Name} / {example.Name}");
        await response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

        var events = new SessionPlayer(example.Session, SessionReplayOptions).Opening();
        var sent = 0;
        try
        {
            foreach (var message in events)
            {
                if (message.Delay > TimeSpan.Zero)
                    await Task.Delay(message.Delay, context.RequestAborted).ConfigureAwait(false);
                var frame = new StringBuilder();
                // Labels look like "event #id" (as recorded by the SSE client).
                var label = message.Label ?? "message";
                var hash = label.IndexOf(" #", StringComparison.Ordinal);
                var eventName = hash >= 0 ? label[..hash] : label;
                if (hash >= 0)
                    frame.Append("id: ").Append(label[(hash + 2)..]).Append('\n');
                if (eventName.Length > 0 && eventName != "message")
                    frame.Append("event: ").Append(eventName).Append('\n');
                foreach (var line in message.Content.Replace("\r\n", "\n").Split('\n'))
                    frame.Append("data: ").Append(line).Append('\n');
                frame.Append('\n');
                await response.WriteAsync(frame.ToString(), context.RequestAborted).ConfigureAwait(false);
                await response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                sent++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        Log("SSE", path, 200, $"{request.Name} / {example.Name}", stopwatch, $"replayed {sent} of {events.Count} event(s)");
    }

    private async Task HandleGrpcAsync(HttpContext context, Stopwatch stopwatch)
    {
        var path = context.Request.Path.Value ?? "";
        var response = context.Response;
        response.ContentType = "application/grpc";

        if (!_grpc.TryGetValue(path, out var route))
        {
            response.Headers["grpc-status"] = "12";
            response.Headers["grpc-message"] = Uri.EscapeDataString($"No mock for {path}");
            Log("gRPC", path, 12, null, stopwatch, "UNIMPLEMENTED");
            return;
        }

        // Drain the request stream (client-streaming calls send several frames).
        await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted).ConfigureAwait(false);

        var example = route.Request.Examples[0];
        if (example.StatusCode != 0 && example.StatusCode != 200)
        {
            response.Headers["grpc-status"] = example.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            response.Headers["grpc-message"] = Uri.EscapeDataString(example.Body);
            Log("gRPC", path, example.StatusCode, route.Request.Name, stopwatch);
            return;
        }

        var node = JsonNode.Parse(RenderBody(example, new Dictionary<string, string>()));
        var messages = route.Method.ServerStreaming && node is JsonArray array ? array.ToList() : [node];
        await response.StartAsync(context.RequestAborted).ConfigureAwait(false);
        var header = new byte[5];
        foreach (var message in messages)
        {
            var bytes = route.Codec.Encode(route.Method.OutputType, message);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)bytes.Length);
            await response.Body.WriteAsync(header, context.RequestAborted).ConfigureAwait(false);
            await response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            await response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
        response.AppendTrailer("grpc-status", "0");
        Log("gRPC", path, 0, route.Request.Name, stopwatch);
    }

    private async Task SimulateLatencyAsync(CancellationToken ct)
    {
        var delay = _options.LatencyMs + (_options.LatencyJitterMs > 0 ? Random.Shared.Next(0, _options.LatencyJitterMs + 1) : 0);
        if (delay > 0)
        {
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void Log(string method, string path, int status, string? matched, Stopwatch stopwatch, string? note = null) =>
        RequestHandled?.Invoke(new MockLogEntry(DateTimeOffset.Now, method, path, status, matched, stopwatch.Elapsed.TotalMilliseconds, note));
}
