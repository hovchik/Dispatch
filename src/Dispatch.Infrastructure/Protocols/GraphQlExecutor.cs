using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Auth;
using Dispatch.Application.GraphQl;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Infrastructure.Protocols;

/// <summary>
/// GraphQL over HTTP (queries and mutations as a JSON POST) and over WebSocket for subscriptions, supporting both
/// <c>graphql-transport-ws</c> and the legacy Apollo <c>graphql-ws</c> protocol.
/// </summary>
public sealed partial class GraphQlExecutor(HttpProtocolExecutor http, WebSocketConnector connector) : IProtocolExecutor
{
    private const string TransportWs = "graphql-transport-ws";
    private const string LegacyWs = "graphql-ws";

    [GeneratedRegex(@"^\s*(?:#[^\n]*\n\s*)*subscription\b", RegexOptions.IgnoreCase)]
    private static partial Regex SubscriptionRegex();

    public IReadOnlyCollection<RequestKind> Kinds { get; } = [RequestKind.GraphQl];

    public Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request.Protocol.GraphQl);
        return SubscriptionRegex().IsMatch(request.Protocol.GraphQl.Query)
            ? SubscribeAsync(request, payload, context, cancellationToken)
            : QueryAsync(request, payload, cancellationToken);
    }

    public static JsonObject BuildPayload(GraphQlSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Query))
            throw new RequestBuildException("Enter a GraphQL query.");

        var payload = new JsonObject { ["query"] = settings.Query };
        if (!string.IsNullOrWhiteSpace(settings.Variables))
        {
            try
            {
                payload["variables"] = JsonNode.Parse(settings.Variables);
            }
            catch (JsonException ex)
            {
                throw new RequestBuildException($"GraphQL variables are not valid JSON: {ex.Message}");
            }
        }
        if (!string.IsNullOrWhiteSpace(settings.OperationName))
            payload["operationName"] = settings.OperationName.Trim();
        return payload;
    }

    private async Task<ApiResponse> QueryAsync(ApiRequest request, JsonObject payload, CancellationToken ct)
    {
        var httpRequest = request.Clone();
        httpRequest.Kind = RequestKind.Http;
        httpRequest.Method = HttpVerb.Post;
        httpRequest.Body = new RequestBody { Mode = BodyMode.Json, Content = payload.ToJsonString() };
        if (!httpRequest.Headers.Any(h => h.IsActive && string.Equals(h.Key, "Accept", StringComparison.OrdinalIgnoreCase)))
            httpRequest.Headers.Add(new KeyValueItem("Accept", "application/graphql-response+json, application/json"));

        var response = await http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        if (!response.HasResponse)
            return Retag(response, null);

        // GraphQL reports failures in the body with HTTP 200; surface them as a failed response.
        string? errorSummary = null;
        try
        {
            if (JsonNode.Parse(response.Body)?["errors"] is JsonArray { Count: > 0 } errors)
                errorSummary = $"{errors.Count} error(s): {errors[0]?["message"]}";
        }
        catch (JsonException)
        {
        }
        return Retag(response, errorSummary);
    }

    private static ApiResponse Retag(ApiResponse r, string? graphQlError) => new()
    {
        Kind = RequestKind.GraphQl,
        StatusCode = r.StatusCode,
        ReasonPhrase = graphQlError is null ? r.ReasonPhrase : $"{r.ReasonPhrase} · {graphQlError}",
        Succeeded = graphQlError is null ? null : false,
        Elapsed = r.Elapsed,
        SizeBytes = r.SizeBytes,
        ContentType = r.ContentType,
        Body = r.Body,
        IsBodyTruncated = r.IsBodyTruncated,
        BodyBytes = r.BodyBytes,
        Headers = r.Headers,
        Timings = r.Timings,
        RawRequest = r.RawRequest,
        EffectiveUrl = r.EffectiveUrl,
        Error = r.Error
    };

    private async Task<ApiResponse> SubscribeAsync(ApiRequest request, JsonObject payload, ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = request.Protocol.GraphQl;
        var url = string.IsNullOrWhiteSpace(settings.SubscriptionUrl) ? request.Url : settings.SubscriptionUrl;
        var uri = WebSocketConnector.ToWebSocketUri(AuthHeaders.ApplyQuery(url, request.Auth));
        var stream = request.Protocol.Stream;
        using var log = new MessageLog(context, stream.ListenSeconds, stream.MaxMessages, cancellationToken);
        var headers = AuthHeaders.Combined(request);

        ClientWebSocket socket;
        try
        {
            socket = await connector.ConnectAsync(uri, headers, [TransportWs, LegacyWs], request.Settings, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
        {
            return ApiResponse.Failed(ex is OperationCanceledException ? "Connection timed out." : WebSocketExecutor.Describe(ex),
                log.Stopwatch.Elapsed, uri.ToString(), RequestKind.GraphQl);
        }

        using (socket)
        {
            var legacy = socket.SubProtocol == LegacyWs;
            log.Info($"Connected ({socket.SubProtocol ?? TransportWs})");
            string? error = null;
            var data = new JsonArray();

            try
            {
                // Auth headers are also sent as connection params: browsers can't set WebSocket headers, so servers read them here.
                var init = new JsonObject { ["type"] = "connection_init" };
                if (headers.Count > 0)
                    init["payload"] = new JsonObject(headers.Select(h => KeyValuePair.Create(h.Key, (JsonNode?)h.Value)));
                await Send(init).ConfigureAwait(false);

                var subscribed = false;
                // Stopping ends the subscription ("complete"/"stop") and sends a Close frame; the loop ends with the server's reply.
                using var closeOnStop = WebSocketConnector.CloseOnStop(socket, log.Token,
                    () => socket.State == WebSocketState.Open
                        ? Send(new JsonObject { ["id"] = "1", ["type"] = legacy ? "stop" : "complete" })
                        : Task.CompletedTask);
                while (true)
                {
                    var received = await WebSocketConnector.ReceiveAsync(socket, CancellationToken.None).ConfigureAwait(false);
                    if (received is null)
                    {
                        log.Info(log.Token.IsCancellationRequested ? "Connection closed" : "Server closed the connection");
                        break;
                    }

                    var message = JsonNode.Parse(received.Value.Text);
                    var type = message?["type"]?.GetValue<string>();
                    switch (type)
                    {
                        case "connection_ack" when !subscribed:
                            log.Info("Connection acknowledged");
                            subscribed = true;
                            await Send(new JsonObject
                            {
                                ["id"] = "1",
                                ["type"] = legacy ? "start" : "subscribe",
                                ["payload"] = payload.DeepClone()
                            }).ConfigureAwait(false);
                            break;
                        case "ping":
                            await Send(new JsonObject { ["type"] = "pong" }, record: false).ConfigureAwait(false);
                            break;
                        case "ka" or "pong":
                            break;
                        case "next" or "data":
                            var result = message?["payload"];
                            data.Add(result?.DeepClone());
                            log.Received(result?.ToJsonString() ?? "null", "next", received.Value.Bytes);
                            break;
                        case "error" or "connection_error":
                            error = message?["payload"]?.ToJsonString() ?? "error";
                            log.Failure(error);
                            log.Stop();
                            break;
                        case "complete":
                            log.Info("Subscription completed by the server");
                            log.Stop();
                            break;
                        default:
                            log.Received(received.Value.Text, type);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException) when (log.Token.IsCancellationRequested)
            {
                // The server dropped the connection instead of answering our Close frame: we were leaving anyway.
            }
            catch (Exception ex) when (ex is WebSocketException or JsonException)
            {
                error = ex.Message;
                log.Failure(ex.Message);
            }

            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await Send(new JsonObject { ["id"] = "1", ["type"] = legacy ? "stop" : "complete" }).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
            await WebSocketConnector.CloseQuietlyAsync(socket).ConfigureAwait(false);

            return new ApiResponse
            {
                Kind = RequestKind.GraphQl,
                StatusCode = 101,
                ReasonPhrase = error is null ? "Subscription" : "Subscription error",
                Succeeded = error is null,
                Elapsed = log.Stopwatch.Elapsed,
                SizeBytes = log.ReceivedBytes,
                ContentType = "application/json",
                Body = data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                Messages = log.Messages,
                EffectiveUrl = uri.ToString()
            };

            async Task Send(JsonObject message, bool record = true)
            {
                var text = message.ToJsonString();
                await WebSocketConnector.SendTextAsync(socket, text, CancellationToken.None).ConfigureAwait(false);
                if (record)
                    log.Sent(text, message["type"]?.GetValue<string>());
            }
        }
    }

    /// <summary>Runs the standard introspection query and parses the schema.</summary>
    public async Task<GraphQlSchema> IntrospectAsync(ApiRequest request, CancellationToken ct)
    {
        var introspection = request.Clone();
        introspection.Protocol.GraphQl = new GraphQlSettings { Query = GraphQlSchema.IntrospectionQuery };
        var response = await QueryAsync(introspection, BuildPayload(introspection.Protocol.GraphQl), ct).ConfigureAwait(false);
        if (!response.HasResponse)
            throw new InvalidOperationException(response.Error);
        if (response.StatusCode >= 400)
            throw new InvalidOperationException($"Introspection failed: {response.StatusCode} {response.ReasonPhrase}");
        return GraphQlSchema.Parse(response.Body);
    }
}
