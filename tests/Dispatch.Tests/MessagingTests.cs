using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Dispatch.Domain;
using Dispatch.Infrastructure.Protocols.Messaging;
using MQTTnet.Server;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Tests;

public sealed class MqttTests : IAsyncLifetime
{
    private MqttServer _server = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = HttpProtocolExecutorTests.FreePort();
        var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(_port)
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointBoundIPV6Address(IPAddress.None).Build();
        _server = new MqttServerFactory().CreateMqttServer(options);
        _server.ValidatingConnectionAsync += e =>
        {
            if (e.UserName == "bad")
                e.ReasonCode = MQTTnet.Protocol.MqttConnectReasonCode.BadUserNameOrPassword;
            return Task.CompletedTask;
        };
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        _server.Dispose();
    }

    private ApiRequest Request(MessagingMode mode, string topic = "dispatch/test", string payload = "hello") => new()
    {
        Kind = RequestKind.Mqtt,
        Url = $"mqtt://127.0.0.1:{_port}",
        Headers = [new("trace", "t1")],
        Protocol = new ProtocolSettings
        {
            Mqtt = new MqttSettings { Mode = mode, Topic = topic, Payload = payload, Qos = 1, ListenSeconds = 5, MaxMessages = 1 }
        }
    };

    [Fact]
    public async Task Publish_and_subscribe_receives_its_own_message_with_user_properties()
    {
        var response = await new MqttExecutor().ExecuteAsync(Request(MessagingMode.PublishAndSubscribe), new ExecutionContext(), CancellationToken.None);

        Assert.True(response.IsSuccess, response.ReasonPhrase);
        var received = Assert.Single(response.Messages, m => m.Direction == MessageDirection.Received);
        Assert.Equal("hello", received.Content);
        Assert.Contains("trace=t1", received.Label);
    }

    [Fact]
    public async Task Subscriber_sees_messages_from_another_client_via_wildcard()
    {
        var subscriber = Request(MessagingMode.Subscribe, "sensors/+/temp");
        var listening = new MqttExecutor().ExecuteAsync(subscriber, new ExecutionContext(), CancellationToken.None);
        await Task.Delay(500);
        var publish = await new MqttExecutor().ExecuteAsync(Request(MessagingMode.Publish, "sensors/kitchen/temp", "21.5"),
            new ExecutionContext(), CancellationToken.None);

        var response = await listening;
        Assert.True(publish.IsSuccess, publish.ReasonPhrase);
        Assert.Equal("21.5", Assert.Single(response.Messages, m => m.Direction == MessageDirection.Received).Content);
    }

    [Fact]
    public async Task Rejected_credentials_are_a_connection_error()
    {
        var request = Request(MessagingMode.Publish);
        request.Protocol.Mqtt.Username = "bad";

        var response = await new MqttExecutor().ExecuteAsync(request, new ExecutionContext(), CancellationToken.None);

        Assert.False(response.HasResponse);
        Assert.Contains("Could not connect", response.Error);
    }
}

public class SocketTests
{
    [Fact]
    public async Task Tcp_sends_payload_and_reads_until_the_server_closes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[1024];
            var n = await stream.ReadAsync(buffer);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("ECHO " + Encoding.UTF8.GetString(buffer, 0, n)));
        });

        var response = await new SocketExecutor().ExecuteAsync(new ApiRequest
        {
            Kind = RequestKind.Tcp,
            Url = $"tcp://127.0.0.1:{port}",
            Protocol = new ProtocolSettings { Socket = new SocketSettings { Payload = "PING", LineEnding = "\\r\\n" } }
        }, new ExecutionContext(), CancellationToken.None);
        await server;
        listener.Stop();

        Assert.True(response.IsSuccess, response.Error);
        Assert.Equal("ECHO PING\r\n", response.Body);
    }

    [Fact]
    public async Task Udp_hex_payload_round_trips()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var echo = Task.Run(async () =>
        {
            var datagram = await server.ReceiveAsync();
            await server.SendAsync(datagram.Buffer.Reverse().ToArray(), datagram.RemoteEndPoint);
        });

        var response = await new SocketExecutor().ExecuteAsync(new ApiRequest
        {
            Kind = RequestKind.Udp,
            Url = $"udp://127.0.0.1:{port}",
            Protocol = new ProtocolSettings { Socket = new SocketSettings { Payload = "01 02 FF", Encoding = PayloadEncoding.Hex, ReadTimeoutSeconds = 1 } }
        }, new ExecutionContext(), CancellationToken.None);
        await echo;

        var received = Assert.Single(response.Messages, m => m.Direction == MessageDirection.Received);
        Assert.Equal("hex: FF0201", received.Content);
    }

    [Fact]
    public async Task Tcp_connection_refused_is_an_error()
    {
        var response = await new SocketExecutor().ExecuteAsync(new ApiRequest
        {
            Kind = RequestKind.Tcp,
            Url = $"127.0.0.1:{HttpProtocolExecutorTests.FreePort()}"
        }, new ExecutionContext(), CancellationToken.None);

        Assert.False(response.HasResponse);
    }
}

public class BrokerConfigTests
{
    [Fact]
    public void Kafka_bootstrap_servers_and_sasl_ssl_config()
    {
        Assert.Equal("b1:9092,b2:9093", KafkaExecutor.BootstrapServers("kafka://b1:9092, b2:9093/"));

        var request = new ApiRequest
        {
            Settings = new RequestSettings { VerifySsl = false },
            Protocol = new ProtocolSettings
            {
                Kafka = new KafkaSettings
                {
                    SecurityProtocol = "SASL_SSL",
                    SaslMechanism = "SCRAM-SHA-512",
                    SaslUsername = "u",
                    SaslPassword = "p",
                    AutoOffsetReset = "earliest",
                    GroupId = "qa"
                }
            }
        };
        var consumer = KafkaExecutor.ConsumerConfig(request, "b1:9092");

        Assert.Equal(Confluent.Kafka.SecurityProtocol.SaslSsl, consumer.SecurityProtocol);
        Assert.Equal(Confluent.Kafka.SaslMechanism.ScramSha512, consumer.SaslMechanism);
        Assert.Equal(Confluent.Kafka.AutoOffsetReset.Earliest, consumer.AutoOffsetReset);
        Assert.False(consumer.EnableSslCertificateVerification);
        Assert.Equal("qa", consumer.GroupId);
    }

    [Fact]
    public async Task Kafka_unreachable_broker_fails_with_a_clear_message()
    {
        var response = await new KafkaExecutor().ExecuteAsync(new ApiRequest
        {
            Kind = RequestKind.Kafka,
            Url = $"127.0.0.1:{HttpProtocolExecutorTests.FreePort()}",
            Settings = new RequestSettings { TimeoutMs = 1500 },
            Protocol = new ProtocolSettings { Kafka = new KafkaSettings { Topic = "t", Payload = "x" } }
        }, new ExecutionContext(), CancellationToken.None);

        Assert.False(response.IsSuccess);
    }

    [Fact]
    public void Amqp_factory_reads_credentials_and_vhost_from_url()
    {
        var address = BrokerAddress.Parse("amqps://alice:s%40cret@rabbit.local/orders", "amqp", 5671);
        var factory = AmqpExecutor.CreateFactory(new ApiRequest(), address);

        Assert.Equal("alice", factory.UserName);
        Assert.Equal("s@cret", factory.Password);
        Assert.Equal("orders", factory.VirtualHost);
        Assert.Equal(5671, factory.Port);
        Assert.True(factory.Ssl.Enabled);
    }

    [Fact]
    public async Task Amqp_unreachable_broker_is_a_connection_error()
    {
        var response = await new AmqpExecutor().ExecuteAsync(new ApiRequest
        {
            Kind = RequestKind.Amqp,
            Url = $"amqp://127.0.0.1:{HttpProtocolExecutorTests.FreePort()}",
            Settings = new RequestSettings { TimeoutMs = 2000 },
            Protocol = new ProtocolSettings { Amqp = new AmqpSettings { RoutingKey = "q", Payload = "x" } }
        }, new ExecutionContext(), CancellationToken.None);

        Assert.False(response.HasResponse);
        Assert.Contains("Could not connect", response.Error);
    }
}
