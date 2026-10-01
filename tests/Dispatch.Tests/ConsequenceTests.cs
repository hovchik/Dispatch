using System.Net;
using System.Net.WebSockets;
using System.Text;
using Dispatch.Application.Consequences;
using Dispatch.Application.Requests;
using Dispatch.Application.Running;
using Dispatch.Domain;
using Dispatch.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Server;

namespace Dispatch.Tests;

public class MessageExpectationUnitTests
{
    [Theory]
    [InlineData("""{"orderId":42,"status":"created"}""", "$.orderId", AssertionOperator.Equals, "42", true)]
    [InlineData("""{"orderId":42}""", "$.orderId", AssertionOperator.Equals, "43", false)]
    [InlineData("""{"items":[1,2,3]}""", "$.items", AssertionOperator.LengthEquals, "3", true)]
    [InlineData("order 42 shipped", "", AssertionOperator.Contains, "shipped", true)]
    [InlineData("not json", "$.orderId", AssertionOperator.Exists, "", false)]
    public void Matches_messages_with_assertion_operators(string content, string path, AssertionOperator op, string expected, bool match) =>
        Assert.Equal(match, ConsequenceSession.Matches(content, new MessageExpectation { Path = path, Operator = op }, path, expected));

    [Fact]
    public void Describes_expectations_readably()
    {
        var e = new MessageExpectation { Path = "$.orderId", Expected = "{{orderId}}", Channel = "orders/created", TimeoutMs = 2000 };
        Assert.Equal("Message on Order events [orders/created] where $.orderId == 42 within 2000 ms",
            ConsequenceSession.Describe(e, "Order events", s => s.Replace("{{orderId}}", "42")));
        Assert.StartsWith("No message on Order events", ConsequenceSession.Describe(new MessageExpectation { ExpectNone = true }, "Order events"));
    }

    [Fact]
    public void Expectations_survive_dispatch_export_and_import()
    {
        var listener = new ApiRequest { Name = "Events", Kind = RequestKind.Kafka };
        var trigger = new ApiRequest
        {
            Name = "Create", Expectations = [new MessageExpectation { ListenerId = listener.Id, Path = "$.id", Expected = "{{id}}", TimeoutMs = 1500, ExpectNone = true }]
        };
        var json = Application.Interop.DispatchFormat.ExportCollection(new RequestCollection { Name = "C", Requests = [trigger, listener] });
        var imported = Application.Interop.DispatchFormat.Import(json).Collections[0];

        var expectation = Assert.Single(imported.Requests.Single(r => r.Name == "Create").Expectations);
        Assert.Equal((listener.Id, "$.id", "{{id}}", 1500, true),
            (expectation.ListenerId, expectation.Path, expectation.Expected, expectation.TimeoutMs, expectation.ExpectNone));
        Assert.Equal(listener.Id, imported.Requests.Single(r => r.Name == "Events").Id);

        // Importing into the app gives requests new ids; the reference must follow.
        Application.Interop.RequestIdentity.Reassign(imported.Requests);
        Assert.NotEqual(listener.Id, imported.Requests.Single(r => r.Name == "Events").Id);
        Assert.Equal(imported.Requests.Single(r => r.Name == "Events").Id, imported.Requests.Single(r => r.Name == "Create").Expectations[0].ListenerId);
    }

    [Fact]
    public void Generated_docs_list_message_checks()
    {
        var listener = new ApiRequest { Name = "Order events", Kind = RequestKind.Kafka };
        var trigger = new ApiRequest { Name = "Create", Expectations = [new MessageExpectation { ListenerId = listener.Id, Path = "$.orderId", Expected = "{{orderId}}" }] };
        var collection = new RequestCollection { Name = "Shop", Requests = [trigger, listener] };
        Assert.Contains("Message on Order events where $.orderId == {{orderId}}", Application.Docs.DocsGenerator.MarkdownText(collection));
        Assert.Contains("Message on Order events where $.orderId == {{orderId}}", Application.Docs.DocsGenerator.Html(collection));
    }

    [Fact]
    public void Clone_and_resolve_keep_expectations_unresolved()
    {
        var request = new ApiRequest { Expectations = [new MessageExpectation { Expected = "{{orderId}}" }] };
        var resolved = RequestResolver.Resolve(request, new Dictionary<string, string> { ["orderId"] = "stale" });
        Assert.Equal("{{orderId}}", resolved.Expectations[0].Expected);
        Assert.NotSame(request.Expectations[0], request.Clone().Expectations[0]);
    }
}

/// <summary>An order API that publishes events to an in-process MQTT broker and to WebSocket clients.</summary>
[Collection("Console")]
public sealed class ConsequenceIntegrationTests : IAsyncLifetime
{
    private MqttServer _broker = null!;
    private int _mqttPort;
    private TestServer _api = null!;
    private ServiceProvider _services = null!;
    private readonly List<WebSocket> _sockets = [];
    private int _nextId = 100;

    public async Task InitializeAsync()
    {
        _mqttPort = HttpProtocolExecutorTests.FreePort();
        _broker = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(_mqttPort)
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointBoundIPV6Address(IPAddress.None).Build());
        await _broker.StartAsync();

        _api = await TestServer.StartAsync(app =>
        {
            // Publishes before it responds: the listener must already be subscribed.
            app.MapPost("/orders", async (HttpContext ctx) =>
            {
                var id = Interlocked.Increment(ref _nextId);
                var silent = ctx.Request.Query["silent"] == "1";
                if (!silent)
                {
                    await Publish("orders/created", $$"""{"orderId":{{id}},"status":"created"}""");
                    await Broadcast($$"""{"event":"order.created","orderId":{{id}}}""");
                }
                return Results.Json(new { id });
            });
            app.Map("/ws", async (HttpContext ctx) =>
            {
                using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
                lock (_sockets)
                    _sockets.Add(socket);
                var buffer = new byte[256];
                try
                {
                    while (socket.State == WebSocketState.Open)
                        if ((await socket.ReceiveAsync(buffer, ctx.RequestAborted)).MessageType == WebSocketMessageType.Close)
                            break;
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
                {
                }
                lock (_sockets)
                    _sockets.Remove(socket);
            });
        });
        _services = new ServiceCollection().AddDispatchEngine()
            .AddSingleton<Application.Abstractions.IHistoryRepository, NullHistory>()
            .BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        await _broker.StopAsync();
        _broker.Dispose();
        await _services.DisposeAsync();
    }

    private Task Publish(string topic, string payload) =>
        _broker.InjectApplicationMessage(new InjectedMqttApplicationMessage(
            new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build()) { SenderClientId = "api" });

    private async Task Broadcast(string text)
    {
        WebSocket[] sockets;
        lock (_sockets)
            sockets = _sockets.ToArray();
        foreach (var socket in sockets)
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private ApiRequest MqttListener() => new()
    {
        Name = "Order events", Kind = RequestKind.Mqtt, Url = $"mqtt://127.0.0.1:{_mqttPort}",
        // A publish-mode request still works as a listener: it is switched to subscribe.
        Protocol = new ProtocolSettings { Mqtt = new MqttSettings { Mode = MessagingMode.Publish, Topic = "orders/#", Payload = "x" } }
    };

    private ApiRequest WsListener() => new() { Name = "Live feed", Kind = RequestKind.WebSocket, Url = _api.BaseUrl.Replace("http", "ws") + "/ws" };

    private ApiRequest CreateOrder(params MessageExpectation[] expectations) => new()
    {
        Name = "Create order", Method = HttpVerb.Post, Url = $"{_api.BaseUrl}/orders",
        Extractions = [new ExtractionRule { Variable = "orderId", Source = ValueSource.JsonPath, Path = "$.id", Scope = VariableScope.Runtime }],
        Expectations = [.. expectations]
    };

    private Task<ApiResponse> SendAsync(ApiRequest trigger, params ApiRequest[] collection) =>
        _services.GetRequiredService<IRequestSender>().SendAsync(trigger, new SendOptions
        {
            CollectionRequests = [trigger, .. collection], RecordHistory = false
        }, CancellationToken.None);

    [Fact]
    public async Task Passes_when_the_request_causes_a_matching_message_on_mqtt_and_websocket()
    {
        var mqtt = MqttListener();
        var ws = WsListener();
        var trigger = CreateOrder(
            new MessageExpectation { ListenerId = mqtt.Id, Path = "$.orderId", Expected = "{{orderId}}", Channel = "orders/created", TimeoutMs = 3000 },
            new MessageExpectation { ListenerId = ws.Id, Path = "$.event", Expected = "order.created", TimeoutMs = 3000 },
            new MessageExpectation { ListenerId = mqtt.Id, Path = "$.status", Expected = "failed", ExpectNone = true, TimeoutMs = 300 });

        var response = await SendAsync(trigger, mqtt, ws);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(3, response.TestResults.Count);
        Assert.All(response.TestResults, t => Assert.True(t.Passed, $"{t.Name}: {t.Message}"));
        var orderId = response.VariableUpdates["orderId"];
        Assert.Contains($"$.orderId == {orderId}", response.TestResults[0].Name);
        Assert.StartsWith("after ", response.TestResults[0].Actual);
    }

    [Fact]
    public async Task Fails_with_details_when_no_matching_message_arrives()
    {
        var mqtt = MqttListener();
        var trigger = CreateOrder(new MessageExpectation { ListenerId = mqtt.Id, Path = "$.orderId", Expected = "-1", TimeoutMs = 400 });

        var response = await SendAsync(trigger, mqtt);

        var result = Assert.Single(response.TestResults);
        Assert.False(result.Passed);
        Assert.Contains("No matching message arrived within 400 ms", result.Message);
        Assert.Contains("1 other message(s) received", result.Message);
        Assert.Contains("\"status\":\"created\"", result.Message);
    }

    [Fact]
    public async Task Fails_when_the_request_causes_no_message_and_ignores_earlier_messages()
    {
        var mqtt = MqttListener();
        await Publish("orders/created", """{"orderId":0,"status":"created"}"""); // before the listener: never seen
        var trigger = CreateOrder(new MessageExpectation { ListenerId = mqtt.Id, Path = "$.status", Expected = "created", TimeoutMs = 300 });
        trigger.Url += "?silent=1";

        var response = await SendAsync(trigger, mqtt);

        var result = Assert.Single(response.TestResults);
        Assert.False(result.Passed);
        Assert.Contains("No messages were received at all", result.Message);
    }

    [Fact]
    public async Task Reports_listener_problems_as_failed_tests()
    {
        var http = new ApiRequest { Name = "Not a stream", Url = $"{_api.BaseUrl}/orders" };
        var down = new ApiRequest
        {
            Name = "Down", Kind = RequestKind.Mqtt, Url = $"mqtt://127.0.0.1:{HttpProtocolExecutorTests.FreePort()}",
            Protocol = new ProtocolSettings { Mqtt = new MqttSettings { Mode = MessagingMode.Subscribe, Topic = "x" } }
        };
        var trigger = CreateOrder(
            new MessageExpectation { ListenerId = Guid.NewGuid() },
            new MessageExpectation { ListenerId = http.Id },
            new MessageExpectation { ListenerId = down.Id, TimeoutMs = 300 });

        var response = await SendAsync(trigger, http, down);

        Assert.Equal(200, response.StatusCode);
        Assert.Collection(response.TestResults.Where(t => t.Name.Contains("Message on")),
            t => Assert.Contains("not found", t.Message),
            t => Assert.Contains("a listener must be", t.Message),
            t => Assert.Contains("could not subscribe", t.Message));
    }

    [Fact]
    public async Task Collection_runner_finds_listeners_outside_the_selected_requests()
    {
        var mqtt = MqttListener();
        var trigger = CreateOrder(new MessageExpectation { ListenerId = mqtt.Id, Path = "$.orderId", Expected = "{{orderId}}", TimeoutMs = 3000 });
        var runner = _services.GetRequiredService<CollectionRunner>();

        var report = await runner.RunAsync(new RunOptions { Requests = [trigger], CollectionRequests = [trigger, mqtt] });

        Assert.True(report.Passed, string.Join("; ", report.Results.SelectMany(r => r.Response.TestResults).Select(t => t.Message)));
        Assert.Single(report.Results);
    }

    [Fact]
    public async Task Cli_run_reports_message_checks_and_fails_on_a_missing_message()
    {
        var mqtt = MqttListener();
        mqtt.Name = "Order events";
        var ok = CreateOrder(new MessageExpectation { ListenerId = mqtt.Id, Path = "$.orderId", Expected = "{{orderId}}", TimeoutMs = 3000 });
        var missing = CreateOrder(new MessageExpectation { ListenerId = mqtt.Id, Path = "$.status", Expected = "shipped", TimeoutMs = 300 });
        missing.Name = "Ship order";
        var dir = Cli.TempDir();
        var file = Path.Combine(dir, "shop.dispatch.json");
        await File.WriteAllTextAsync(file, Application.Interop.DispatchFormat.ExportCollection(
            new RequestCollection { Name = "Shop", Requests = [ok, missing, mqtt] }));

        var (exit, output) = await Cli.RunAsync("run", file, "--request", "Create order", "--request", "Ship order",
            "--db", Path.Combine(dir, "db"), "--no-color");

        Assert.Equal(1, exit);
        Assert.Contains("Message on Order events where $.orderId ==", output);
        Assert.Contains("No matching message arrived within 300 ms", output);
    }
}
