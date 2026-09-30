using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dispatch.Application.Grpc;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Protocols.Grpc;
using Dispatch.Tests.Interop;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Tests;

public sealed class ShowcaseService : Showcase.ShowcaseBase
{
    public override Task<Everything> Echo(Everything request, ServerCallContext context)
    {
        context.ResponseTrailers.Add("x-echo", "yes");
        return Task.FromResult(request);
    }

    public override async Task Count(CountRequest request, IServerStreamWriter<Tick> responseStream, ServerCallContext context)
    {
        for (var i = 1; i <= request.To; i++)
            await responseStream.WriteAsync(new Tick { N = i });
    }

    public override async Task<Total> Sum(IAsyncStreamReader<Number> requestStream, ServerCallContext context)
    {
        var total = new Total();
        await foreach (var n in requestStream.ReadAllAsync())
        {
            total.Sum += n.Value;
            total.Count++;
        }
        return total;
    }

    public override async Task Chat(IAsyncStreamReader<Line> requestStream, IServerStreamWriter<Line> responseStream, ServerCallContext context)
    {
        await foreach (var line in requestStream.ReadAllAsync())
            await responseStream.WriteAsync(new Line { Text = line.Text.ToUpperInvariant() });
    }

    public override Task<Google.Protobuf.WellKnownTypes.Empty> Fail(Google.Protobuf.WellKnownTypes.Empty request, ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.NotFound, "no such thing"));

    public override async Task<Google.Protobuf.WellKnownTypes.Empty> Slow(Google.Protobuf.WellKnownTypes.Empty request, ServerCallContext context)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), context.CancellationToken);
        return new Google.Protobuf.WellKnownTypes.Empty();
    }
}

public sealed class GrpcTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private readonly HttpClientPool _pool = new();

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync(app =>
        {
            app.MapGrpcService<ShowcaseService>();
            app.MapGrpcReflectionService();
        }, http2Only: true, services: s =>
        {
            s.AddGrpc();
            s.AddGrpcReflection();
        });
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _pool.Dispose();
    }

    private GrpcExecutor Executor() => new(_pool, new GrpcSchemaProvider(_pool));

    private ApiRequest Request(string method, string message, GrpcSchemaSource source = GrpcSchemaSource.ServerReflection) => new()
    {
        Kind = RequestKind.Grpc,
        Url = _server.Host,
        Headers = [new("x-tenant", "acme")],
        Protocol = new ProtocolSettings
        {
            Grpc = new GrpcSettings
            {
                SchemaSource = source,
                ProtoFiles = [Path.Combine(AppContext.BaseDirectory, "Protos", "interop.proto")],
                Service = "dispatch.interop.Showcase",
                Method = method,
                Message = message
            },
            Stream = new StreamSettings { ListenSeconds = 10 }
        }
    };

    private const string Everything = """
        {
          "name": "Ann", "small": -5, "big": "9007199254740993", "ubig": "18446744073709551615", "zig": -7,
          "ratio": 0.25, "approx": 1.5, "flag": true, "blob": "AQID", "color": "GREEN",
          "numbers": [1, 2, 300], "tags": ["a", "b"], "counts": {"x": 1, "y": 2},
          "byId": {"7": {"label": "seven", "colors": ["RED", "GREEN"]}},
          "nested": {"label": "n"}, "items": [{"label": "i1"}, {"label": "i2"}],
          "code": 42,
          "when": "2024-05-06T07:08:09.5Z", "took": "1.5s",
          "extra": {"k": [1, "two", null, {"deep": true}]},
          "maybe": "hello", "opt": 0, "f64": "12", "sf32": -3
        }
        """;

    [Theory]
    [InlineData(GrpcSchemaSource.ServerReflection)]
    [InlineData(GrpcSchemaSource.ProtoFiles)]
    public async Task Unary_round_trips_every_field_kind(GrpcSchemaSource source)
    {
        var response = await Executor().ExecuteAsync(Request("Echo", Everything, source), new ExecutionContext(), CancellationToken.None);

        Assert.True(response.IsSuccess, response.ReasonPhrase + response.Error);
        Assert.Equal(0, response.StatusCode);
        Assert.Contains(response.Trailers, t => t.Name == "x-echo" && t.Value == "yes");

        var expected = JsonNode.Parse(Everything)!.AsObject();
        var actual = JsonNode.Parse(response.Body)!.AsObject();
        foreach (var (key, value) in expected)
        {
            var got = actual[key];
            if (key == "when")
                Assert.Equal(DateTimeOffset.Parse("2024-05-06T07:08:09.5Z"), DateTimeOffset.Parse(got!.GetValue<string>()));
            else
                Assert.True(IsSubset(value, got), $"{key}: expected {value?.ToJsonString()} got {got?.ToJsonString()}");
        }
        Assert.Null(actual["text"]); // the other member of the oneof stays unset
    }

    /// <summary>Every property in <paramref name="expected"/> is in <paramref name="actual"/> (which may add defaults).</summary>
    private static bool IsSubset(JsonNode? expected, JsonNode? actual) => expected switch
    {
        JsonObject e => actual is JsonObject a && e.All(kv => a.ContainsKey(kv.Key) && IsSubset(kv.Value, a[kv.Key])),
        JsonArray e => actual is JsonArray a && e.Count == a.Count && e.Zip(a).All(p => IsSubset(p.First, p.Second)),
        _ => JsonNode.DeepEquals(expected, actual)
    };

    [Fact]
    public async Task Server_streaming_collects_every_message()
    {
        var response = await Executor().ExecuteAsync(Request("Count", """{"to": 4}"""), new ExecutionContext(), CancellationToken.None);

        Assert.True(response.IsSuccess, response.ReasonPhrase);
        Assert.Equal(4, response.Messages.Count(m => m.Direction == MessageDirection.Received));
        Assert.Equal(4, JsonNode.Parse(response.Body)!.AsArray().Count);
    }

    [Fact]
    public async Task Client_streaming_sends_a_json_array_of_messages()
    {
        var response = await Executor().ExecuteAsync(Request("Sum", """[{"value": 1}, {"value": "2"}, {"value": 39}]"""),
            new ExecutionContext(), CancellationToken.None);

        Assert.True(response.IsSuccess, response.ReasonPhrase);
        var total = JsonNode.Parse(response.Body)!;
        Assert.Equal("42", total["sum"]!.GetValue<string>());
        Assert.Equal(3, total["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Bidirectional_streaming_relays_typed_messages_interactively()
    {
        var outgoing = Channel.CreateUnbounded<string>();
        await outgoing.Writer.WriteAsync("""{"text": "hi"}""");
        await outgoing.Writer.WriteAsync("""{"text": "there"}""");
        outgoing.Writer.Complete();

        var response = await Executor().ExecuteAsync(Request("Chat", ""),
            new ExecutionContext { Outgoing = outgoing.Reader, Interactive = true }, CancellationToken.None);

        Assert.True(response.IsSuccess, response.ReasonPhrase);
        var received = response.Messages.Where(m => m.Direction == MessageDirection.Received)
            .Select(m => JsonNode.Parse(m.Content)!["text"]!.GetValue<string>()).ToList();
        Assert.Equal(["HI", "THERE"], received);
    }

    [Fact]
    public async Task Error_status_and_message_are_reported()
    {
        var response = await Executor().ExecuteAsync(Request("Fail", "{}"), new ExecutionContext(), CancellationToken.None);

        Assert.False(response.IsSuccess);
        Assert.Equal(5, response.StatusCode);
        Assert.Contains("NOT_FOUND", response.ReasonPhrase);
        Assert.Contains("no such thing", response.ReasonPhrase);
    }

    [Fact]
    public async Task Deadline_is_enforced()
    {
        var request = Request("Slow", "{}");
        request.Protocol.Grpc.DeadlineSeconds = 0.3;

        var response = await Executor().ExecuteAsync(request, new ExecutionContext(), CancellationToken.None);

        Assert.Equal(4, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_json_field_is_a_clear_build_error()
    {
        var ex = await Assert.ThrowsAsync<Dispatch.Application.Requests.RequestBuildException>(() =>
            Executor().ExecuteAsync(Request("Echo", """{"nope": 1}"""), new ExecutionContext(), CancellationToken.None));

        Assert.Contains("has no field 'nope'", ex.Message);
    }

    [Fact]
    public async Task Reflection_lists_services_and_builds_templates()
    {
        var schema = await new GrpcSchemaProvider(_pool).GetSchemaAsync(Request("Echo", "{}"), refresh: true, CancellationToken.None);

        var service = schema.Services["dispatch.interop.Showcase"];
        Assert.Equal(6, service.Methods.Count);
        Assert.Contains(service.Methods, m => m is { Name: "Chat", ClientStreaming: true, ServerStreaming: true });

        var template = JsonNode.Parse(new ProtoJson(schema).Template("dispatch.interop.Everything"))!;
        Assert.Equal("COLOR_UNSPECIFIED", template["color"]!.GetValue<string>());
        Assert.NotNull(template["text"]); // first member of the oneof
        Assert.Null(template["code"]);
    }

    [Fact]
    public void Codec_output_matches_the_official_protobuf_library()
    {
        var schema = GrpcSchemaProvider.LoadFromFiles(new GrpcSettings
        {
            ProtoFiles = [Path.Combine(AppContext.BaseDirectory, "Protos", "interop.proto")]
        });
        var bytes = new ProtoJson(schema).Encode("dispatch.interop.Everything", Everything);

        var parsed = Dispatch.Tests.Interop.Everything.Parser.ParseFrom(bytes);
        Assert.Equal("Ann", parsed.Name);
        Assert.Equal(9007199254740993L, parsed.Big);
        Assert.Equal(ulong.MaxValue, parsed.Ubig);
        Assert.Equal(-7, parsed.Zig);
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Blob.ToByteArray());
        Assert.Equal(Color.Green, parsed.Color);
        Assert.Equal(new[] { 1, 2, 300 }, parsed.Numbers);
        Assert.Equal("seven", parsed.ById[7].Label);
        Assert.Equal(42, parsed.Code);
        Assert.Equal(1.5, parsed.Took.ToTimeSpan().TotalSeconds);
        Assert.Equal("two", parsed.Extra.Fields["k"].ListValue.Values[1].StringValue);
        Assert.Equal("hello", parsed.Maybe);
        Assert.True(parsed.HasOpt);

        // And back: official bytes decode to the same JSON through the dynamic codec.
        var json = JsonNode.Parse(new ProtoJson(schema).DecodeToJson("dispatch.interop.Everything", parsed.ToByteArray()))!;
        Assert.Equal("9007199254740993", json["big"]!.GetValue<string>());
        Assert.Equal("GREEN", json["color"]!.GetValue<string>());
        Assert.True(json["extra"]!["k"]![3]!["deep"]!.GetValue<bool>());
    }
}

public class ProtoParserTests
{
    [Fact]
    public void Parses_nested_types_options_imports_and_resolves_relative_names()
    {
        const string source = """
            // comment
            syntax = "proto3";
            package acme.v1;
            import "google/protobuf/timestamp.proto";
            option java_package = "com.acme";

            message Order {
              option deprecated = true;
              reserved 5, 7 to 9;
              reserved "old";
              message Line { string sku = 1 [json_name = "SKU"]; Status status = 2; }
              enum Status { STATUS_UNKNOWN = 0; OPEN = 1 [deprecated = true]; }
              repeated Line lines = 1;
              google.protobuf.Timestamp created = 2;
              map<string, Line> by_sku = 3;
              /* block
                 comment */
              oneof ref { string external_id = 4; int64 internal_id = 6; }
            }

            service Orders {
              option (my.option) = { a: 1 };
              rpc Get (Order) returns (Order) { option (google.api.http) = { get: "/v1/orders/{id}" }; }
              rpc Watch (stream Order) returns (stream Order.Line);
            }
            """;

        var schema = ProtoSchema.Link(WellKnownProtos.Parsed().Append(ProtoParser.Parse(source, "orders.proto")));

        var order = schema.Message("acme.v1.Order");
        Assert.Equal("acme.v1.Order.Line", order.Fields.Single(f => f.Name == "lines").TypeName);
        Assert.Equal("google.protobuf.Timestamp", order.Fields.Single(f => f.Name == "created").TypeName);
        Assert.Equal(ProtoType.Enum, schema.Message("acme.v1.Order.Line").Fields[1].Type);
        Assert.Equal("SKU", schema.Message("acme.v1.Order.Line").Fields[0].EffectiveJsonName);
        Assert.True(schema.Message("acme.v1.Order.BySkuEntry").IsMapEntry);
        Assert.Equal(2, order.Fields.Count(f => f.OneofIndex == 0));

        var (_, watch) = schema.FindMethod("Orders", "Watch");
        Assert.True(watch.ClientStreaming && watch.ServerStreaming);
        Assert.Equal("acme.v1.Order.Line", watch.OutputType);
    }

    [Fact]
    public void Reports_line_numbers_on_syntax_errors()
    {
        var ex = Assert.Throws<FormatException>(() => ProtoParser.Parse("syntax = \"proto3\";\nmessage A {\n  string x = ;\n}", "bad.proto"));
        Assert.StartsWith("bad.proto:3:", ex.Message);
    }
}
