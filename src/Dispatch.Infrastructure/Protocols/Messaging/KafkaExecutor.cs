using System.Text;
using Confluent.Kafka;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Infrastructure.Protocols.Messaging;

/// <summary>
/// Apache Kafka: produce a record (key, value, headers) and/or consume from a topic for a while.
/// The URL is the bootstrap server list, e.g. <c>localhost:9092</c> or <c>kafka://b1:9092,b2:9092</c>.
/// </summary>
public sealed class KafkaExecutor : IProtocolExecutor
{
    public IReadOnlyCollection<RequestKind> Kinds { get; } = [RequestKind.Kafka];

    public async Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken)
    {
        var s = request.Protocol.Kafka;
        var servers = BootstrapServers(request.Url);
        if (string.IsNullOrWhiteSpace(s.Topic))
            throw new RequestBuildException("Enter a topic.");

        var consume = s.Mode is MessagingMode.Subscribe or MessagingMode.PublishAndSubscribe;
        var produce = s.Mode is MessagingMode.Publish or MessagingMode.PublishAndSubscribe;
        using var log = new MessageLog(context, consume ? s.ListenSeconds : 0, s.MaxMessages, cancellationToken);
        var failed = false;
        var reason = "OK";
        var errors = new List<string>();

        IConsumer<string?, string>? consumer = null;
        Task consuming = Task.CompletedTask;
        string? consumeError = null;
        try
        {
            if (consume)
            {
                consumer = new ConsumerBuilder<string?, string>(ConsumerConfig(request, servers))
                    .SetErrorHandler((_, e) => { if (e.IsFatal || e.IsBrokerError) lock (errors) errors.Add(e.Reason); })
                    .SetPartitionsAssignedHandler((_, partitions) =>
                    {
                        log.Info($"Assigned {string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition.Value}]"))}");
                        log.Listening();
                    })
                    .Build();
                consumer.Subscribe(s.Topic);
                log.Info($"Subscribed to {s.Topic} as group '{s.GroupId}' ({s.AutoOffsetReset})");
                consuming = Task.Run(() => consumeError = ConsumeLoop(consumer, log), CancellationToken.None);
            }

            if (produce)
            {
                using var producer = new ProducerBuilder<string?, string>(ProducerConfig(request, servers))
                    .SetErrorHandler((_, e) => { if (e.IsFatal) lock (errors) errors.Add(e.Reason); })
                    .Build();
                var message = new Message<string?, string>
                {
                    Key = string.IsNullOrEmpty(s.Key) ? null : s.Key,
                    Value = s.Payload,
                    Headers = []
                };
                foreach (var header in s.Headers.Concat(request.Headers).Where(h => h.IsActive))
                    message.Headers.Add(header.Key.Trim(), Encoding.UTF8.GetBytes(header.Value));

                using var produceTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                produceTimeout.CancelAfter(request.Settings.TimeoutMs > 0 ? request.Settings.TimeoutMs : 30_000);
                var result = await producer.ProduceAsync(s.Topic, message, produceTimeout.Token).ConfigureAwait(false);
                log.Sent(s.Payload, $"{result.Topic}[{result.Partition.Value}]@{result.Offset.Value}" +
                                    (message.Key is null ? "" : $" key={message.Key}"));
                reason = $"Delivered to partition {result.Partition.Value} at offset {result.Offset.Value}";

                if (context.Outgoing is not null && consume)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var text in context.Outgoing.ReadAllAsync(log.Token).ConfigureAwait(false))
                            {
                                var r = await producer.ProduceAsync(s.Topic, new Message<string?, string> { Key = message.Key, Value = text },
                                    log.Token).ConfigureAwait(false);
                                log.Sent(text, $"{r.Topic}[{r.Partition.Value}]@{r.Offset.Value}");
                            }
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or KafkaException or ObjectDisposedException)
                        {
                        }
                    }, CancellationToken.None);
                }

                if (consume)
                    await consuming.ConfigureAwait(false);
            }
            else
            {
                await consuming.ConfigureAwait(false);
            }
        }
        catch (ProduceException<string?, string> ex)
        {
            failed = true;
            reason = $"Produce failed: {ex.Error.Reason}";
            log.Failure(reason);
        }
        catch (KafkaException ex)
        {
            failed = true;
            reason = ex.Error.Reason;
            log.Failure(reason);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !consume)
        {
            failed = true;
            reason = "Timed out waiting for the broker to acknowledge the record.";
            log.Failure(reason);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            log.Stop();
            await consuming.ConfigureAwait(false);
            if (consumer is not null)
            {
                try
                {
                    consumer.Close();
                }
                catch (KafkaException)
                {
                }
                consumer.Dispose();
            }
        }

        if (consumeError is not null && !failed)
        {
            failed = true;
            reason = consumeError;
        }
        lock (errors)
        {
            if (errors.Count > 0 && log.ReceivedCount == 0 && !failed)
            {
                failed = true;
                reason = errors[0];
                log.Failure(reason);
            }
        }

        return new ApiResponse
        {
            Kind = RequestKind.Kafka,
            StatusCode = failed ? 1 : 0,
            ReasonPhrase = reason,
            Succeeded = !failed,
            Elapsed = log.Stopwatch.Elapsed,
            SizeBytes = log.ReceivedBytes,
            Messages = log.Messages,
            EffectiveUrl = servers
        };
    }

    /// <summary>Consumes until the log stops. Returns the reason when consuming ended early because of a fatal error.</summary>
    private static string? ConsumeLoop(IConsumer<string?, string> consumer, MessageLog log)
    {
        try
        {
            while (!log.Token.IsCancellationRequested)
            {
                ConsumeResult<string?, string>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                }
                catch (ConsumeException ex)
                {
                    // Most consume errors (a transient broker error, an undecodable record) are per-message: report and go on.
                    log.Failure(ex.Error.Reason);
                    if (ex.Error.IsFatal)
                        return ex.Error.Reason;
                    continue;
                }
                if (result?.Message is null)
                    continue;
                var headers = result.Message.Headers is { Count: > 0 } h
                    ? " " + string.Join(" ", h.Select(x => $"{x.Key}={(x.GetValueBytes() is { } b ? Encoding.UTF8.GetString(b) : "")}"))
                    : "";
                log.Received(result.Message.Value ?? "",
                    $"{result.Topic}[{result.Partition.Value}]@{result.Offset.Value}" +
                    (result.Message.Key is null ? "" : $" key={result.Message.Key}") + headers,
                    Encoding.UTF8.GetByteCount(result.Message.Value ?? ""));
            }
        }
        catch (ObjectDisposedException)
        {
        }
        return null;
    }

    internal static string BootstrapServers(string url)
    {
        var servers = string.Join(",", url.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Contains("://", StringComparison.Ordinal) ? s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..] : s)
            .Select(s => s.TrimEnd('/')));
        if (servers.Length == 0)
            throw new RequestBuildException("Enter the bootstrap servers, e.g. localhost:9092.");
        return servers;
    }

    private static void ApplySecurity(ClientConfig config, ApiRequest request)
    {
        var s = request.Protocol.Kafka;
        config.SecurityProtocol = s.SecurityProtocol.ToUpperInvariant() switch
        {
            "SSL" => SecurityProtocol.Ssl,
            "SASL_PLAINTEXT" => SecurityProtocol.SaslPlaintext,
            "SASL_SSL" => SecurityProtocol.SaslSsl,
            _ => SecurityProtocol.Plaintext
        };
        if (config.SecurityProtocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl)
        {
            config.SaslMechanism = s.SaslMechanism.ToUpperInvariant() switch
            {
                "SCRAM-SHA-256" => SaslMechanism.ScramSha256,
                "SCRAM-SHA-512" => SaslMechanism.ScramSha512,
                "OAUTHBEARER" => SaslMechanism.OAuthBearer,
                _ => SaslMechanism.Plain
            };
            config.SaslUsername = s.SaslUsername;
            config.SaslPassword = s.SaslPassword;
        }
        if (config.SecurityProtocol is SecurityProtocol.Ssl or SecurityProtocol.SaslSsl)
        {
            config.EnableSslCertificateVerification = request.Settings.VerifySsl;
            if (!request.Settings.VerifySsl)
                config.SslEndpointIdentificationAlgorithm = SslEndpointIdentificationAlgorithm.None;
            if (!string.IsNullOrWhiteSpace(request.Settings.ClientCertificatePath))
            {
                config.SslCertificateLocation = request.Settings.ClientCertificatePath;
                config.SslKeyLocation = string.IsNullOrWhiteSpace(request.Settings.ClientCertificateKeyPath)
                    ? request.Settings.ClientCertificatePath
                    : request.Settings.ClientCertificateKeyPath;
                if (!string.IsNullOrEmpty(request.Settings.ClientCertificatePassword))
                    config.SslKeyPassword = request.Settings.ClientCertificatePassword;
            }
        }
        config.SocketTimeoutMs = 30_000;
    }

    internal static ProducerConfig ProducerConfig(ApiRequest request, string servers)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = servers,
            ClientId = "dispatch",
            MessageTimeoutMs = request.Settings.TimeoutMs > 0 ? request.Settings.TimeoutMs : 30_000,
            Acks = Acks.All
        };
        ApplySecurity(config, request);
        return config;
    }

    internal static ConsumerConfig ConsumerConfig(ApiRequest request, string servers)
    {
        var s = request.Protocol.Kafka;
        var config = new ConsumerConfig
        {
            BootstrapServers = servers,
            ClientId = "dispatch",
            GroupId = string.IsNullOrWhiteSpace(s.GroupId) ? $"dispatch-{Guid.NewGuid():N}" : s.GroupId,
            AutoOffsetReset = s.AutoOffsetReset.Equals("earliest", StringComparison.OrdinalIgnoreCase)
                ? AutoOffsetReset.Earliest
                : AutoOffsetReset.Latest,
            EnableAutoCommit = true,
            SessionTimeoutMs = 10_000
        };
        ApplySecurity(config, request);
        return config;
    }
}
