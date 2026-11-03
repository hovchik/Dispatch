using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Tests;

public class RequestNamingTests
{
    [Theory]
    [InlineData("New Request")]
    [InlineData("new request")]
    [InlineData("New GraphQL request")]
    [InlineData("New AMQP (RabbitMQ) request")]
    [InlineData("New Request (copy)")]
    [InlineData("New Request (copy) (copy)")]
    [InlineData("Untitled")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Recognises_generated_placeholder_names(string? name) =>
        Assert.True(RequestNaming.IsDefaultName(name));

    [Theory]
    [InlineData("Login")]
    [InlineData("GET /users")]
    [InlineData("New user")]
    [InlineData("New customer request")]
    [InlineData("New token request")]
    [InlineData("New Request v2")]
    [InlineData("Create new request token")]
    [InlineData("Requests")]
    public void Keeps_names_the_user_chose(string name) =>
        Assert.False(RequestNaming.IsDefaultName(name));

    [Fact]
    public void Default_names_match_what_new_tabs_are_called()
    {
        Assert.Equal("New Request", RequestNaming.DefaultNameFor(RequestKind.Http));
        Assert.Equal("New gRPC request", RequestNaming.DefaultNameFor(RequestKind.Grpc));
        foreach (var kind in Enum.GetValues<RequestKind>())
            Assert.True(RequestNaming.IsDefaultName(RequestNaming.DefaultNameFor(kind)), kind.ToString());
    }

    [Theory]
    [InlineData(HttpVerb.Get, "https://api.example.com/users?page=2", "GET /users")]
    [InlineData(HttpVerb.Post, "https://api.example.com/users/{id}/posts/", "POST /users/{id}/posts")]
    [InlineData(HttpVerb.Delete, "{{baseUrl}}/orders/42", "DELETE /orders/42")]
    [InlineData(HttpVerb.Get, "https://api.example.com/", "GET api.example.com")]
    [InlineData(HttpVerb.Get, "https://api.example.com", "GET api.example.com")]
    [InlineData(HttpVerb.Get, "localhost:5000/health#top", "GET /health")]
    [InlineData(HttpVerb.Get, "{{url}}", "GET {{url}}")]
    public void Names_http_requests_after_method_and_path(HttpVerb method, string url, string expected)
    {
        var request = new ApiRequest { Kind = RequestKind.Http, Method = method, Url = url };
        Assert.Equal(expected, RequestNaming.Suggest(request));
    }

    [Fact]
    public void Has_no_suggestion_without_a_url()
    {
        Assert.Null(RequestNaming.Suggest(new ApiRequest { Url = "" }));
        Assert.Null(RequestNaming.Suggest(new ApiRequest { Url = "   " }));
    }

    [Fact]
    public void Names_graphql_after_the_operation_when_one_is_set()
    {
        var request = new ApiRequest { Kind = RequestKind.GraphQl, Url = "https://api.example.com/graphql" };
        Assert.Equal("GraphQL /graphql", RequestNaming.Suggest(request));

        request.Protocol.GraphQl.OperationName = "GetViewer";
        Assert.Equal("GetViewer", RequestNaming.Suggest(request));
    }

    [Fact]
    public void Names_grpc_after_the_short_service_and_method()
    {
        var request = new ApiRequest { Kind = RequestKind.Grpc, Url = "localhost:5001" };
        Assert.Equal("gRPC localhost:5001", RequestNaming.Suggest(request));

        request.Protocol.Grpc.Service = "helloworld.Greeter";
        request.Protocol.Grpc.Method = "SayHello";
        Assert.Equal("Greeter/SayHello", RequestNaming.Suggest(request));
    }

    [Fact]
    public void Names_soap_after_the_operation()
    {
        var request = new ApiRequest { Kind = RequestKind.Soap, Url = "https://example.com/calc.asmx" };
        Assert.Equal("SOAP /calc.asmx", RequestNaming.Suggest(request));

        request.Protocol.Soap.Operation = "Add";
        Assert.Equal("Add", RequestNaming.Suggest(request));
    }

    [Fact]
    public void Names_messaging_requests_after_the_topic_or_queue()
    {
        var mqtt = new ApiRequest { Kind = RequestKind.Mqtt, Url = "mqtt://broker:1883" };
        Assert.Equal("MQTT broker:1883", RequestNaming.Suggest(mqtt));
        mqtt.Protocol.Mqtt.Topic = "sensors/temp";
        Assert.Equal("MQTT sensors/temp", RequestNaming.Suggest(mqtt));

        var kafka = new ApiRequest { Kind = RequestKind.Kafka, Url = "kafka:9092" };
        kafka.Protocol.Kafka.Topic = "orders";
        Assert.Equal("Kafka orders", RequestNaming.Suggest(kafka));

        var amqp = new ApiRequest { Kind = RequestKind.Amqp, Url = "amqp://rabbit" };
        amqp.Protocol.Amqp.Exchange = "events";
        Assert.Equal("AMQP events", RequestNaming.Suggest(amqp));
        amqp.Protocol.Amqp.Queue = "work";
        Assert.Equal("AMQP work", RequestNaming.Suggest(amqp));
    }

    [Theory]
    [InlineData(RequestKind.WebSocket, "wss://echo.example.com/socket", "WS /socket")]
    [InlineData(RequestKind.Sse, "https://example.com/events", "SSE /events")]
    [InlineData(RequestKind.SocketIo, "https://example.com", "Socket.IO example.com")]
    [InlineData(RequestKind.Tcp, "tcp://localhost:9000", "TCP localhost:9000")]
    [InlineData(RequestKind.Udp, "udp://localhost:9000", "UDP localhost:9000")]
    public void Names_streams_and_sockets_after_the_endpoint(RequestKind kind, string url, string expected) =>
        Assert.Equal(expected, RequestNaming.Suggest(new ApiRequest { Kind = kind, Url = url }));

    [Fact]
    public void Truncates_very_long_names()
    {
        var request = new ApiRequest { Url = "https://example.com/" + new string('a', 200) };
        var name = RequestNaming.Suggest(request)!;
        Assert.Equal(60, name.Length);
        Assert.EndsWith("…", name);
    }
}
