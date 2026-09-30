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
        app.Run(HandleAsync);
        await app.StartAsync(ct).ConfigureAwait(false);
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

        if (match.Example is null)
        {
            response.StatusCode = 501;
            response.ContentType = "application/json";
            await response.WriteAsync($$"""{"error":"'{{match.Request.Name}}' has no saved example to serve."}""").ConfigureAwait(false);
            Log(request.Method, path, 501, match.Request.Name, stopwatch, "no example");
            return;
        }

        var example = match.Example;
        response.StatusCode = example.StatusCode;
        foreach (var header in example.Headers.Where(h => h.IsActive && !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                                                          && !h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
            response.Headers[header.Key] = VariableResolver.Resolve(header.Value, match.Variables);
        if (!string.IsNullOrEmpty(example.ContentType))
            response.ContentType = example.ContentType;
        response.Headers["X-Mock-Match"] = $"{match.Request.Name} / {example.Name}";

        if (!HttpMethods.IsHead(request.Method) && example.Body.Length > 0)
            await response.WriteAsync(VariableResolver.Resolve(example.Body, match.Variables)).ConfigureAwait(false);
        Log(request.Method, path, example.StatusCode, $"{match.Request.Name} / {example.Name}", stopwatch);
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

        var node = JsonNode.Parse(VariableResolver.Resolve(example.Body, new Dictionary<string, string>()));
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
