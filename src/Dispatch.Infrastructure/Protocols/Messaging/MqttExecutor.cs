using System.Buffers;
using System.Security.Authentication;
using System.Text;
using System.Threading.Channels;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Infrastructure.Protocols.Messaging;

/// <summary>
/// MQTT 3.1.1 / 5 client: publish, subscribe, or both (subscribe first, then publish — handy for request/response
/// topics). Addresses: <c>mqtt://</c>, <c>mqtts://</c> (TLS), <c>ws://</c> / <c>wss://</c> (MQTT over WebSocket).
/// </summary>
public sealed class MqttExecutor : IProtocolExecutor
{
    public IReadOnlyCollection<RequestKind> Kinds { get; } = [RequestKind.Mqtt];

    public async Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken)
    {
        var s = request.Protocol.Mqtt;
        var address = BrokerAddress.Parse(request.Url, s.UseTls ? "mqtts" : "mqtt", s.UseTls ? 8883 : 1883);
        if (string.IsNullOrWhiteSpace(s.Topic))
            throw new RequestBuildException("Enter a topic.");
        var subscribe = s.Mode is MessagingMode.Subscribe or MessagingMode.PublishAndSubscribe;
        var publish = s.Mode is MessagingMode.Publish or MessagingMode.PublishAndSubscribe;

        using var log = new MessageLog(context, subscribe ? s.ListenSeconds : 0, s.MaxMessages, cancellationToken);
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();

        client.ApplicationMessageReceivedAsync += e =>
        {
            var message = e.ApplicationMessage;
            var payload = message.Payload.IsEmpty ? "" : Encoding.UTF8.GetString(message.Payload.ToArray());
            var properties = message.UserProperties is { Count: > 0 } props
                ? " " + string.Join(" ", props.Select(p => $"{p.Name}={p.Value}"))
                : "";
            log.Received(payload, $"{message.Topic} (QoS {(int)message.QualityOfServiceLevel}{(message.Retain ? ", retained" : "")}){properties}",
                message.Payload.Length);
            return Task.CompletedTask;
        };

        var options = BuildOptions(request, address);
        MqttClientConnectResult connect;
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(request.Settings.TimeoutMs > 0 ? request.Settings.TimeoutMs : 15_000);
            connect = await client.ConnectAsync(options, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return ApiResponse.Failed($"Could not connect to {address}: {Unwrap(ex)}", log.Stopwatch.Elapsed, address.ToString(), RequestKind.Mqtt);
        }
        if (connect.ResultCode != MqttClientConnectResultCode.Success)
            return ApiResponse.Failed($"Could not connect to {address}: {connect.ResultCode} {connect.ReasonString}".TrimEnd(),
                log.Stopwatch.Elapsed, address.ToString(), RequestKind.Mqtt);
        log.Info($"Connected to {address} ({connect.ResultCode})");

        var failed = false;
        var reason = "OK";
        try
        {
            if (subscribe)
            {
                var result = await client.SubscribeAsync(factory.CreateSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(s.Topic).WithQualityOfServiceLevel((MqttQualityOfServiceLevel)Math.Clamp(s.Qos, 0, 2)))
                    .Build(), cancellationToken).ConfigureAwait(false);
                var granted = result.Items.FirstOrDefault()?.ResultCode;
                log.Info($"Subscribed to {s.Topic} ({granted})");
                if (granted is > MqttClientSubscribeResultCode.GrantedQoS2)
                {
                    failed = true;
                    reason = $"Subscribe refused: {granted}";
                }
            }

            if (publish && !failed)
            {
                var result = await PublishAsync(client, s, request, s.Payload, cancellationToken).ConfigureAwait(false);
                log.Sent(s.Payload, $"{s.Topic} (QoS {s.Qos}{(s.Retain ? ", retain" : "")})");
                if (!result.IsSuccess)
                {
                    failed = true;
                    reason = $"Publish failed: {result.ReasonCode} {result.ReasonString}".Trim();
                    log.Failure(reason);
                }
            }

            if (subscribe && !failed)
            {
                var pump = context.Outgoing is null ? Task.CompletedTask : PumpOutgoingAsync(client, s, request, context.Outgoing, log);
                try
                {
                    await Task.Delay(Timeout.Infinite, log.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                await pump.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            reason = Unwrap(ex);
            log.Failure(reason);
        }

        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort.
        }
        log.Info("Disconnected");

        return new ApiResponse
        {
            Kind = RequestKind.Mqtt,
            StatusCode = failed ? 1 : 0,
            ReasonPhrase = reason,
            Succeeded = !failed,
            Elapsed = log.Stopwatch.Elapsed,
            SizeBytes = log.ReceivedBytes,
            Messages = log.Messages,
            EffectiveUrl = address.ToString()
        };
    }

    private static MqttClientOptions BuildOptions(ApiRequest request, BrokerAddress address)
    {
        var s = request.Protocol.Mqtt;
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(string.IsNullOrWhiteSpace(s.ClientId) ? $"dispatch-{Guid.NewGuid():N}"[..23] : s.ClientId)
            .WithCleanSession(s.CleanSession)
            .WithTimeout(TimeSpan.FromSeconds(15))
            .WithProtocolVersion(s.ProtocolVersion switch
            {
                3 => MqttProtocolVersion.V310,
                4 => MqttProtocolVersion.V311,
                _ => MqttProtocolVersion.V500
            });

        builder = address.Scheme is "ws" or "wss"
            ? builder.WithWebSocketServer(o => o.WithUri($"{address.Scheme}://{address.Host}:{address.Port}{address.Path}"))
            : builder.WithTcpServer(address.Host, address.Port);

        var user = string.IsNullOrWhiteSpace(s.Username) ? address.UserName : s.Username;
        var password = string.IsNullOrWhiteSpace(s.Password) ? address.Password : s.Password;
        if (!string.IsNullOrEmpty(user))
            builder = builder.WithCredentials(user, password);

        if (s.UseTls || address.Scheme is "mqtts" or "ssl" or "wss")
        {
            builder = builder.WithTlsOptions(o =>
            {
                o.UseTls();
                o.WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);
                if (!request.Settings.VerifySsl)
                    o.WithCertificateValidationHandler(_ => true);
                if (!string.IsNullOrWhiteSpace(request.Settings.ClientCertificatePath))
                    o.WithClientCertificates([Http.HttpClientPool.LoadCertificate(request.Settings)]);
            });
        }
        return builder.Build();
    }

    private static Task<MqttClientPublishResult> PublishAsync(IMqttClient client, MqttSettings s, ApiRequest request, string payload,
        CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(s.Topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)Math.Clamp(s.Qos, 0, 2))
            .WithRetainFlag(s.Retain);
        // MQTT 5 user properties come from the request's header rows.
        foreach (var header in request.Headers.Where(h => h.IsActive))
            message = message.WithUserProperty(header.Key.Trim(), header.Value);
        return client.PublishAsync(message.Build(), ct);
    }

    private static async Task PumpOutgoingAsync(IMqttClient client, MqttSettings s, ApiRequest request, ChannelReader<string> outgoing,
        MessageLog log)
    {
        try
        {
            await foreach (var text in outgoing.ReadAllAsync(log.Token).ConfigureAwait(false))
            {
                var result = await PublishAsync(client, s, request, text, log.Token).ConfigureAwait(false);
                log.Sent(text, s.Topic);
                if (!result.IsSuccess)
                    log.Failure($"Publish failed: {result.ReasonCode}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Unwrap(Exception ex) => ex.InnerException is { } inner && inner.Message != ex.Message
        ? $"{ex.Message} ({inner.Message})"
        : ex.Message;
}
