using Dispatch.Domain;
using Dispatch.Infrastructure.Protocols.Messaging;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Tests;

/// <summary>A fact that only runs when an environment variable points at a live broker.</summary>
public sealed class BrokerFactAttribute : FactAttribute
{
    public BrokerFactAttribute(string variable)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
            Skip = $"Set {variable} to run against a live broker (e.g. docker run -p 9092:9092 apache/kafka).";
    }
}

public class BrokerIntegrationTests
{
    [BrokerFact("DISPATCH_KAFKA")]
    public async Task Kafka_produce_then_consume_from_earliest()
    {
        var servers = Environment.GetEnvironmentVariable("DISPATCH_KAFKA")!;
        var topic = $"dispatch-{Guid.NewGuid():N}";
        var produced = await new KafkaExecutor().ExecuteAsync(new ApiRequest
        {
            Kind = RequestKind.Kafka,
            Url = servers,
            Protocol = new ProtocolSettings
            {
                Kafka = new KafkaSettings { Topic = topic, Key = "k1", Payload = """{"n":1}""", Headers = [new("h", "v")] }
            }
        }, new ExecutionContext(), CancellationToken.None);
        Assert.True(produced.IsSuccess, produced.ReasonPhrase);

        var consumed = await new KafkaExecutor().ExecuteAsync(new ApiRequest
        {
            Kind = RequestKind.Kafka,
            Url = servers,
            Protocol = new ProtocolSettings
            {
                Kafka = new KafkaSettings
                {
                    Mode = MessagingMode.Subscribe, Topic = topic, GroupId = "", AutoOffsetReset = "earliest",
                    ListenSeconds = 20, MaxMessages = 1
                }
            }
        }, new ExecutionContext(), CancellationToken.None);

        var message = Assert.Single(consumed.Messages, m => m.Direction == MessageDirection.Received);
        Assert.Equal("""{"n":1}""", message.Content);
        Assert.Contains("key=k1", message.Label);
        Assert.Contains("h=v", message.Label);
    }

    [BrokerFact("DISPATCH_AMQP")]
    public async Task Amqp_publish_and_subscribe_via_temporary_queue()
    {
        var url = Environment.GetEnvironmentVariable("DISPATCH_AMQP")!;
        var response = await new AmqpExecutor().ExecuteAsync(new ApiRequest
        {
            Kind = RequestKind.Amqp,
            Url = url,
            Protocol = new ProtocolSettings
            {
                Amqp = new AmqpSettings
                {
                    Mode = MessagingMode.PublishAndSubscribe, Exchange = "amq.topic", RoutingKey = "orders.created",
                    Payload = """{"id":7}""", Headers = [new("source", "dispatch")], ListenSeconds = 10, MaxMessages = 1
                }
            }
        }, new ExecutionContext(), CancellationToken.None);

        Assert.True(response.IsSuccess, response.ReasonPhrase);
        var message = Assert.Single(response.Messages, m => m.Direction == MessageDirection.Received);
        Assert.Equal("""{"id":7}""", message.Content);
        Assert.Contains("amq.topic/orders.created", message.Label);
        Assert.Contains("source=dispatch", message.Label);
    }
}
