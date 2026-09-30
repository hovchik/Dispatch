using System.Net.Security;
using System.Text;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Infrastructure.Protocols.Messaging;

/// <summary>
/// AMQP 0-9-1 (RabbitMQ): publish to an exchange / routing key and/or consume from a queue. Without a queue name,
/// subscribing creates a temporary queue bound to the exchange and routing key, so you can watch traffic.
/// URL: <c>amqp://user:pass@host:5672/vhost</c> or <c>amqps://...</c>.
/// </summary>
public sealed class AmqpExecutor : IProtocolExecutor
{
    public IReadOnlyCollection<RequestKind> Kinds { get; } = [RequestKind.Amqp];

    public async Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken)
    {
        var s = request.Protocol.Amqp;
        var address = BrokerAddress.Parse(request.Url, "amqp", request.Url.StartsWith("amqps", StringComparison.OrdinalIgnoreCase) ? 5671 : 5672);
        var consume = s.Mode is MessagingMode.Subscribe or MessagingMode.PublishAndSubscribe;
        var publish = s.Mode is MessagingMode.Publish or MessagingMode.PublishAndSubscribe;
        if (publish && string.IsNullOrWhiteSpace(s.Exchange) && string.IsNullOrWhiteSpace(s.RoutingKey))
            throw new RequestBuildException("Enter an exchange and/or routing key (the default exchange routes by queue name).");

        using var log = new MessageLog(context, consume ? s.ListenSeconds : 0, s.MaxMessages, cancellationToken);
        var factory = CreateFactory(request, address);
        var failed = false;
        var reason = "OK";

        IConnection connection;
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(request.Settings.TimeoutMs > 0 ? request.Settings.TimeoutMs : 15_000);
            connection = await factory.CreateConnectionAsync("dispatch", connectTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BrokerUnreachableException or OperationInterruptedException or OperationCanceledException
                                       or AuthenticationFailureException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            return ApiResponse.Failed($"Could not connect to {address}: {Describe(ex)}", log.Stopwatch.Elapsed, address.ToString(),
                RequestKind.Amqp);
        }

        await using (connection.ConfigureAwait(false))
        {
            log.Info($"Connected to {address} (vhost {factory.VirtualHost})");
            try
            {
                await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                if (consume)
                {
                    var queue = s.Queue.Trim();
                    if (queue.Length == 0)
                    {
                        var declared = await channel.QueueDeclareAsync("", durable: false, exclusive: true, autoDelete: true,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        queue = declared.QueueName;
                        if (!string.IsNullOrWhiteSpace(s.Exchange))
                            await channel.QueueBindAsync(queue, s.Exchange.Trim(), string.IsNullOrWhiteSpace(s.RoutingKey) ? "#" : s.RoutingKey.Trim(),
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                        log.Info($"Listening on temporary queue {queue}" + (s.Exchange.Length > 0 ? $" bound to {s.Exchange} ({s.RoutingKey})" : ""));
                    }
                    else
                    {
                        log.Info($"Consuming from queue {queue}");
                    }

                    var consumer = new AsyncEventingBasicConsumer(channel);
                    consumer.ReceivedAsync += (_, e) =>
                    {
                        var body = Encoding.UTF8.GetString(e.Body.Span);
                        var headers = e.BasicProperties.Headers is { Count: > 0 } h
                            ? " " + string.Join(" ", h.Select(x => $"{x.Key}={(x.Value is byte[] b ? Encoding.UTF8.GetString(b) : x.Value)}"))
                            : "";
                        log.Received(body, $"{e.Exchange}/{e.RoutingKey}" + headers, e.Body.Length);
                        return Task.CompletedTask;
                    };
                    await channel.BasicConsumeAsync(queue, autoAck: true, consumer, cancellationToken).ConfigureAwait(false);
                }

                if (publish)
                {
                    await PublishAsync(channel, s, request, s.Payload, cancellationToken).ConfigureAwait(false);
                    log.Sent(s.Payload, $"{(s.Exchange.Length == 0 ? "(default)" : s.Exchange)}/{s.RoutingKey}");
                    reason = "Published";
                }

                if (consume)
                {
                    if (context.Outgoing is not null)
                        _ = PumpOutgoingAsync(channel, s, request, context, log);
                    try
                    {
                        await Task.Delay(Timeout.Infinite, log.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
            catch (OperationInterruptedException ex)
            {
                failed = true;
                reason = Describe(ex);
                log.Failure(reason);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            try
            {
                await connection.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }
        log.Info("Disconnected");

        return new ApiResponse
        {
            Kind = RequestKind.Amqp,
            StatusCode = failed ? 1 : 0,
            ReasonPhrase = reason,
            Succeeded = !failed,
            Elapsed = log.Stopwatch.Elapsed,
            SizeBytes = log.ReceivedBytes,
            Messages = log.Messages,
            EffectiveUrl = address.ToString()
        };
    }

    private static async Task PumpOutgoingAsync(IChannel channel, AmqpSettings s, ApiRequest request, ExecutionContext context, MessageLog log)
    {
        try
        {
            await foreach (var text in context.Outgoing!.ReadAllAsync(log.Token).ConfigureAwait(false))
            {
                await PublishAsync(channel, s, request, text, log.Token).ConfigureAwait(false);
                log.Sent(text, $"{s.Exchange}/{s.RoutingKey}");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or OperationInterruptedException or ObjectDisposedException)
        {
        }
    }

    private static ValueTask PublishAsync(IChannel channel, AmqpSettings s, ApiRequest request, string payload, CancellationToken ct)
    {
        var properties = new BasicProperties
        {
            ContentType = string.IsNullOrWhiteSpace(s.ContentType) ? null : s.ContentType,
            DeliveryMode = s.Persistent ? DeliveryModes.Persistent : DeliveryModes.Transient,
            MessageId = Guid.NewGuid().ToString(),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };
        var headers = s.Headers.Concat(request.Headers).Where(h => h.IsActive).ToList();
        if (headers.Count > 0)
            properties.Headers = headers.ToDictionary(h => h.Key.Trim(), h => (object?)h.Value);
        return channel.BasicPublishAsync(s.Exchange.Trim(), s.RoutingKey.Trim(), mandatory: false, properties,
            Encoding.UTF8.GetBytes(payload), ct);
    }

    internal static ConnectionFactory CreateFactory(ApiRequest request, BrokerAddress address)
    {
        var vhost = Uri.UnescapeDataString(address.Path.TrimStart('/'));
        var factory = new ConnectionFactory
        {
            HostName = address.Host,
            Port = address.Port,
            UserName = address.UserName ?? (request.Auth.Username is { Length: > 0 } u ? u : "guest"),
            Password = address.Password ?? (request.Auth.Password is { Length: > 0 } p ? p : "guest"),
            VirtualHost = vhost.Length == 0 ? "/" : vhost,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(15),
            AutomaticRecoveryEnabled = false
        };
        if (address.Scheme == "amqps")
        {
            factory.Ssl = new SslOption
            {
                Enabled = true,
                ServerName = address.Host,
                AcceptablePolicyErrors = request.Settings.VerifySsl ? SslPolicyErrors.None
                    : SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch
                      | SslPolicyErrors.RemoteCertificateNotAvailable
            };
            if (!string.IsNullOrWhiteSpace(request.Settings.ClientCertificatePath))
                factory.Ssl.Certs = [Http.HttpClientPool.LoadCertificate(request.Settings)];
        }
        return factory;
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationInterruptedException oie when oie.ShutdownReason is { } r => $"{r.ReplyCode} {r.ReplyText}",
        BrokerUnreachableException b when b.InnerException is { } inner => inner.Message,
        _ => ex.Message
    };
}
