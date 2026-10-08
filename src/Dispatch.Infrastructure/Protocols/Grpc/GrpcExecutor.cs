using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Auth;
using Dispatch.Application.Grpc;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Infrastructure.Protocols.Grpc;

/// <summary>
/// Dynamic gRPC client: JSON in, JSON out, using a schema from .proto files or server reflection.
/// Unary and server-streaming calls send the request message; client and bidirectional streaming send a JSON array
/// of messages and/or messages typed in the UI while the call is open.
/// </summary>
public sealed class GrpcExecutor(IHttpClientSource clients, GrpcSchemaProvider schemas) : IProtocolExecutor
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public IReadOnlyCollection<RequestKind> Kinds { get; } = [RequestKind.Grpc];

    public async Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken)
    {
        var settings = request.Protocol.Grpc;
        if (string.IsNullOrWhiteSpace(request.Url))
            throw new RequestBuildException("Enter the gRPC server address, e.g. localhost:5001.");
        if (string.IsNullOrWhiteSpace(settings.Service) || string.IsNullOrWhiteSpace(settings.Method))
            throw new RequestBuildException("Choose a service and method.");

        Uri baseUri;
        try
        {
            baseUri = GrpcCall.BaseUri(request.Url, settings.UseTls);
        }
        catch (ArgumentException ex)
        {
            throw new RequestBuildException(ex.Message);
        }

        var stopwatch = Stopwatch.StartNew();
        ProtoSchema schema;
        MethodDesc method;
        ServiceDesc service;
        try
        {
            schema = await schemas.GetSchemaAsync(request, refresh: false, cancellationToken).ConfigureAwait(false);
            (service, method) = schema.FindMethod(settings.Service, settings.Method);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApiResponse.Failed($"Could not load the gRPC schema: {Describe(ex)}", stopwatch.Elapsed, baseUri.ToString(),
                RequestKind.Grpc);
        }

        var codec = new ProtoJson(schema);
        List<byte[]> initial;
        try
        {
            initial = EncodeInitialMessages(codec, method, settings.Message);
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or OverflowException)
        {
            throw new RequestBuildException($"Request message: {ex.Message}");
        }

        var streamSettings = request.Protocol.Stream;
        using var log = new MessageLog(context, method.ServerStreaming || method.ClientStreaming ? streamSettings.ListenSeconds : 0,
            streamSettings.MaxMessages, cancellationToken);
        var fullMethod = $"{service.FullName}/{method.Name}";
        var streaming = method.ServerStreaming || method.ClientStreaming;
        var deadline = settings.DeadlineSeconds > 0
            ? TimeSpan.FromSeconds(settings.DeadlineSeconds)
            : request.Settings.TimeoutMs > 0 ? TimeSpan.FromMilliseconds(request.Settings.TimeoutMs)
            // Streams listen for as long as the user wants; a unary call must not hang forever on a silent server.
            : !streaming ? HttpRequestExecutor.DefaultTimeout : (TimeSpan?)null;

        await using var call = GrpcCall.Start(clients, request.Settings, baseUri, fullMethod, AuthHeaders.Combined(request),
            deadline, log.Token);

        var responses = new List<JsonNode?>();
        string? transportError = null;
        var stoppedByUs = false;
        try
        {
            foreach (var message in initial)
            {
                await call.WriteAsync(message).ConfigureAwait(false);
                log.Sent(codec.DecodeToJson(method.InputType, message), "request");
            }

            Task pump = Task.CompletedTask;
            if (method.ClientStreaming && context.Outgoing is not null && context.Interactive)
                pump = PumpOutgoingAsync(call, codec, method, context.Outgoing, log);
            else
                call.CompleteRequests();

            await foreach (var bytes in call.ReadResponsesAsync(log.Token).ConfigureAwait(false))
            {
                var json = codec.Decode(method.OutputType, bytes);
                responses.Add(json);
                log.Received(json?.ToJsonString(Indented) ?? "null", "response", bytes.Length);
            }
            call.CompleteRequests();
            // The server has finished; stop waiting for messages typed in the UI, or the call would never return.
            log.Stop();
            await pump.ConfigureAwait(false);
        }
        catch (GrpcException ex)
        {
            transportError = ex.Status == 4 ? null : ex.Message;
        }
        catch (OperationCanceledException) when (call.IsDeadline)
        {
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Listen window over / message limit reached: end the call on our side.
            stoppedByUs = true;
            call.Cancel();
            log.Info("Stopped listening");
        }
        catch (OperationCanceledException)
        {
            log.Info("Cancelled");
            call.Cancel();
        }
        catch (HttpRequestException ex)
        {
            return ApiResponse.Failed(HttpRequestExecutor.Describe(ex), stopwatch.Elapsed, baseUri + fullMethod, RequestKind.Grpc);
        }
        catch (FormatException ex)
        {
            transportError = "Could not decode the response: " + ex.Message;
        }

        int code;
        string statusMessage;
        IReadOnlyList<ResponseHeader> trailers;
        if (call.IsDeadline)
            (code, statusMessage, trailers) = (4, "Deadline exceeded", []);
        else if (stoppedByUs && !call.HasGrpcStatus)
            // We ended the stream on purpose before the server sent its status: that is not a failure.
            (code, statusMessage, trailers) = (0, "", []);
        else
            (code, statusMessage, trailers) = call.GetStatus();
        if (transportError is not null && code == 0)
            (code, statusMessage) = (13, transportError);

        var body = method.ServerStreaming
            ? new JsonArray(responses.Select(r => r?.DeepClone()).ToArray()).ToJsonString(Indented)
            : responses.Count > 0 ? responses[0]?.ToJsonString(Indented) ?? "null" : "";

        if (code != 0)
            log.Failure($"{GrpcCall.StatusName(code)}: {statusMessage}");

        return new ApiResponse
        {
            Kind = RequestKind.Grpc,
            StatusCode = code,
            ReasonPhrase = GrpcCall.StatusName(code) + (statusMessage.Length > 0 && code != 0 ? $" · {statusMessage}" : ""),
            Succeeded = code == 0,
            Elapsed = stopwatch.Elapsed,
            SizeBytes = log.ReceivedBytes,
            ContentType = "application/json",
            Body = body,
            Headers = call.ResponseHeaders,
            Trailers = trailers,
            Messages = method.ServerStreaming || method.ClientStreaming ? log.Messages : [],
            RawRequest = $"POST {baseUri}{fullMethod} (HTTP/2)\ncontent-type: application/grpc\n" +
                         string.Join("\n", AuthHeaders.Combined(request).Select(h => $"{h.Key.ToLowerInvariant()}: {h.Value}")) +
                         $"\n\n{method.Kind} call, {initial.Count} message(s):\n" +
                         string.Join("\n", initial.Select(m => codec.DecodeToJson(method.InputType, m))),
            EffectiveUrl = baseUri + fullMethod
        };
    }

    private static List<byte[]> EncodeInitialMessages(ProtoJson codec, MethodDesc method, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return method.ClientStreaming ? [] : [codec.Encode(method.InputType, "{}")];

        var node = JsonNode.Parse(json);
        if (method.ClientStreaming && node is JsonArray array)
            return array.Select(item => codec.Encode(method.InputType, item)).ToList();
        return [codec.Encode(method.InputType, node)];
    }

    /// <summary>Messages typed while a client/bidi-streaming call is open; completing the channel half-closes the call.</summary>
    private static async Task PumpOutgoingAsync(GrpcCall call, ProtoJson codec, MethodDesc method, ChannelReader<string> outgoing,
        MessageLog log)
    {
        try
        {
            await foreach (var text in outgoing.ReadAllAsync(log.Token).ConfigureAwait(false))
            {
                byte[] bytes;
                try
                {
                    bytes = codec.Encode(method.InputType, text);
                }
                catch (FormatException ex)
                {
                    log.Failure("Not sent: " + ex.Message);
                    continue;
                }
                await call.WriteAsync(bytes).ConfigureAwait(false);
                log.Sent(codec.DecodeToJson(method.InputType, bytes), "request");
            }
            log.Info("Request stream completed");
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
            // The call was already half-closed (the server finished first); a message typed after that is dropped.
            log.Info("Not sent: the call has ended");
        }
        finally
        {
            call.CompleteRequests();
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException http => HttpRequestExecutor.Describe(http),
        GrpcException g => $"{GrpcCall.StatusName(g.Status)}: {g.Message}",
        _ => ex.Message
    };
}
