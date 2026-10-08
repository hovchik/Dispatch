using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Protocols;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Tests;

public sealed class StreamingProtocolTests : IDisposable
{
    private readonly HttpClientPool _pool = new();

    public void Dispose() => _pool.Dispose();

    private static async Task<string?> Receive(WebSocket ws)
    {
        var buffer = new byte[8192];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        return result.MessageType == WebSocketMessageType.Close ? null : Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static Task Send(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    [Fact]
    public async Task WebSocket_sends_initial_and_typed_messages_and_logs_echoes()
    {
        await using var server = await TestServer.StartAsync(app => app.Map("/ws", async (HttpContext ctx) =>
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            Assert.Equal("Bearer t0k", ctx.Request.Headers.Authorization.ToString());
            while (await Receive(ws) is { } text)
                await Send(ws, "echo:" + text);
        }));

        var outgoing = Channel.CreateUnbounded<string>();
        var request = new ApiRequest
        {
            Kind = RequestKind.WebSocket,
            Url = server.BaseUrl + "/ws",
            Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "t0k" },
            Protocol = new ProtocolSettings
            {
                Stream = new StreamSettings { InitialMessages = [new("hello", "hi")], MaxMessages = 2, ListenSeconds = 10 }
            }
        };
        var progress = new List<StreamMessage>();
        var context = new ExecutionContext { Outgoing = outgoing.Reader, Progress = new SyncProgress<StreamMessage>(progress.Add) };
        await outgoing.Writer.WriteAsync("typed");

        var response = await new WebSocketExecutor(new WebSocketConnector(_pool)).ExecuteAsync(request, context, CancellationToken.None);

        Assert.True(response.IsSuccess, response.Error);
        var received = response.Messages.Where(m => m.Direction == MessageDirection.Received).Select(m => m.Content).ToList();
        Assert.Equal(["echo:hi", "echo:typed"], received);
        Assert.Contains(response.Messages, m => m.Direction == MessageDirection.Sent && m.Content == "typed");
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task WebSocket_connection_failure_is_an_error_response()
    {
        var request = new ApiRequest { Kind = RequestKind.WebSocket, Url = $"ws://127.0.0.1:{HttpProtocolExecutorTests.FreePort()}/" };

        var response = await new WebSocketExecutor(new WebSocketConnector(_pool)).ExecuteAsync(request, new ExecutionContext(), CancellationToken.None);

        Assert.False(response.HasResponse);
    }

    [Fact]
    public async Task Sse_parses_named_events_multiline_data_and_comments()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/events", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync(": keep-alive\n\nevent: greeting\nid: 1\ndata: hello\ndata: world\n\ndata: {\"n\":2}\n\n");
            await ctx.Response.Body.FlushAsync();
        }));

        var request = new ApiRequest
        {
            Kind = RequestKind.Sse,
            Url = server.BaseUrl + "/events",
            Protocol = new ProtocolSettings { Stream = new StreamSettings { ListenSeconds = 5 } }
        };

        var response = await new SseExecutor(new RequestMessageBuilder(), _pool).ExecuteAsync(request, new ExecutionContext(), CancellationToken.None);

        var events = response.Messages.Where(m => m.Direction == MessageDirection.Received).ToList();
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, events.Count);
        Assert.Equal("hello\nworld", events[0].Content);
        Assert.Equal("greeting #1", events[0].Label);
        Assert.Equal("{\"n\":2}", events[1].Content);
    }

    [Fact]
    public async Task GraphQl_query_posts_json_and_flags_errors_in_body()
    {
        await using var server = await TestServer.StartAsync(app => app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            var body = await JsonNode.ParseAsync(ctx.Request.Body);
            var query = body!["query"]!.GetValue<string>();
            ctx.Response.ContentType = "application/json";
            if (query.Contains("broken"))
                await ctx.Response.WriteAsync("""{"errors":[{"message":"Cannot query field broken"}]}""");
            else
                await ctx.Response.WriteAsync("{\"data\":{\"user\":{\"id\":\"" + body["variables"]!["id"] + "\"}}}");
        }));

        var executor = new GraphQlExecutor(HttpExecutor(), new WebSocketConnector(_pool));
        var ok = await executor.ExecuteAsync(GraphQlRequest(server, "query($id: ID!) { user(id: $id) { id } }", """{"id":"7"}"""),
            new ExecutionContext(), CancellationToken.None);
        var failed = await executor.ExecuteAsync(GraphQlRequest(server, "{ broken }", ""), new ExecutionContext(), CancellationToken.None);

        Assert.True(ok.IsSuccess);
        Assert.Contains("\"id\":\"7\"", ok.Body);
        Assert.False(failed.IsSuccess);
        Assert.Contains("Cannot query field broken", failed.ReasonPhrase);
    }

    [Fact]
    public async Task GraphQl_subscription_uses_graphql_transport_ws()
    {
        await using var server = await TestServer.StartAsync(app => app.Map("/graphql", async (HttpContext ctx) =>
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync("graphql-transport-ws");
            var init = JsonNode.Parse((await Receive(ws))!)!;
            Assert.Equal("connection_init", init["type"]!.GetValue<string>());
            Assert.Equal("Bearer abc", init["payload"]!["Authorization"]!.GetValue<string>());
            await Send(ws, """{"type":"connection_ack"}""");
            var subscribe = JsonNode.Parse((await Receive(ws))!)!;
            Assert.Equal("subscribe", subscribe["type"]!.GetValue<string>());
            for (var i = 1; i <= 3; i++)
                await Send(ws, "{\"id\":\"1\",\"type\":\"next\",\"payload\":{\"data\":{\"tick\":" + i + "}}}");
            await Send(ws, """{"id":"1","type":"complete"}""");
            await Receive(ws);
        }));

        var request = GraphQlRequest(server, "subscription { tick }", "");
        request.Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "abc" };
        var executor = new GraphQlExecutor(HttpExecutor(), new WebSocketConnector(_pool));

        var response = await executor.ExecuteAsync(request, new ExecutionContext(), CancellationToken.None);

        Assert.True(response.IsSuccess, response.ReasonPhrase);
        Assert.Equal(3, response.Messages.Count(m => m.Direction == MessageDirection.Received));
        Assert.Equal(3, JsonNode.Parse(response.Body)!.AsArray().Count);
    }

    [Fact]
    public async Task SocketIo_connects_emits_with_ack_answers_ping_and_logs_events()
    {
        var pongReceived = new TaskCompletionSource();
        await using var server = await TestServer.StartAsync(app => app.Map("/socket.io/", async (HttpContext ctx) =>
        {
            Assert.Equal("4", ctx.Request.Query["EIO"]);
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await Send(ws, """0{"sid":"s1","pingInterval":25000,"pingTimeout":20000}""");
            Assert.Equal("40/chat,{\"token\":\"x\"}", await Receive(ws));
            await Send(ws, """40/chat,{"sid":"n1"}""");
            var emit = await Receive(ws);
            Assert.Equal("""42/chat,1["join",{"room":"a"}]""", emit);
            await Send(ws, """43/chat,1[{"ok":true}]""");
            await Send(ws, "2");
            Assert.Equal("3", await Receive(ws));
            pongReceived.SetResult();
            await Send(ws, """42/chat,["message",{"text":"hi"}]""");
            await Send(ws, """42/chat,["ignored",1]""");
            await Send(ws, """41/chat,""");
            await Receive(ws);
        }));

        var request = new ApiRequest
        {
            Kind = RequestKind.SocketIo,
            Url = server.BaseUrl,
            Protocol = new ProtocolSettings
            {
                SocketIo = new SocketIoSettings
                {
                    Namespace = "chat",
                    Event = "join",
                    Arguments = """{"room":"a"}""",
                    AuthPayload = """{"token":"x"}""",
                    ListenEvents = "message",
                    ListenSeconds = 10
                }
            }
        };

        var response = await new SocketIoExecutor(new WebSocketConnector(_pool)).ExecuteAsync(request, new ExecutionContext(), CancellationToken.None);
        await pongReceived.Task;

        Assert.True(response.IsSuccess, response.ReasonPhrase);
        var received = response.Messages.Where(m => m.Direction == MessageDirection.Received).ToList();
        Assert.Contains(received, m => m.Label == "ack 1" && m.Content.Contains("\"ok\":true"));
        Assert.Contains(received, m => m.Label == "message" && m.Content == """{"text":"hi"}""");
        Assert.DoesNotContain(received, m => m.Label == "ignored");
    }

    [Fact]
    public void SocketIo_packet_parsing()
    {
        Assert.Equal(('2', "/", (int?)null, """["a"]"""), SocketIoExecutor.ParseSocketPacket("""2["a"]"""));
        Assert.Equal(('3', "/admin", (int?)12, "[1]"), SocketIoExecutor.ParseSocketPacket("3/admin,12[1]"));
        Assert.Equal("""["ev",1,2]""", SocketIoExecutor.BuildEvent("ev", "[1,2]").ToJsonString());
    }

    private HttpProtocolExecutor HttpExecutor() =>
        new(new RequestMessageBuilder(), new HttpRequestExecutor(_pool));

    private static ApiRequest GraphQlRequest(TestServer server, string query, string variables) => new()
    {
        Kind = RequestKind.GraphQl,
        Url = server.BaseUrl + "/graphql",
        Protocol = new ProtocolSettings
        {
            GraphQl = new GraphQlSettings { Query = query, Variables = variables },
            Stream = new StreamSettings { ListenSeconds = 10 }
        }
    };
}

/// <summary>IProgress that invokes synchronously (Progress&lt;T&gt; posts to a sync context / thread pool).</summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Lock _gate = new();

    public void Report(T value)
    {
        lock (_gate)
            handler(value);
    }
}

public sealed class WebSocketCloseTests : IDisposable
{
    private readonly HttpClientPool _pool = new();

    public void Dispose() => _pool.Dispose();

    [Fact]
    public async Task Stopping_sends_a_close_frame_and_records_the_servers_close_status()
    {
        var serverSawClose = new TaskCompletionSource<WebSocketCloseStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TestServer.StartAsync(app => app.Map("/ws", async (HttpContext ctx) =>
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[8192];
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    serverSawClose.TrySetResult(result.CloseStatus);
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye from server", CancellationToken.None);
                    return;
                }
                await ws.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }));
        var request = new ApiRequest
        {
            Kind = RequestKind.WebSocket,
            Url = server.BaseUrl + "/ws",
            Protocol = new ProtocolSettings
            {
                Stream = new StreamSettings { InitialMessages = [new("hello", "hi")], MaxMessages = 1, ListenSeconds = 10 }
            }
        };

        var response = await new WebSocketExecutor(new WebSocketConnector(_pool))
            .ExecuteAsync(request, new ExecutionContext(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(response.IsSuccess, response.Error);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await serverSawClose.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Closed (1000 NormalClosure)", response.ReasonPhrase);
        Assert.Contains(response.Messages, m => m.Direction == MessageDirection.Info && m.Content.Contains("bye from server"));
        Assert.DoesNotContain(response.Messages, m => m.Direction == MessageDirection.Error);
    }

    [Fact]
    public async Task User_cancel_still_ends_the_session_when_the_server_never_answers_the_close()
    {
        var holdServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TestServer.StartAsync(app => app.Map("/ws", async (HttpContext ctx) =>
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await holdServer.Task; // never reads, never replies to the Close frame
        }));
        var request = new ApiRequest
        {
            Kind = RequestKind.WebSocket,
            Url = server.BaseUrl + "/ws",
            Protocol = new ProtocolSettings { Stream = new StreamSettings { ListenSeconds = 60 } }
        };
        using var cts = new CancellationTokenSource();
        var cancelledAt = DateTime.MaxValue;
        // The user disconnects as soon as the session is up (a timer could fire before the connect completes on a busy machine).
        var context = new ExecutionContext { Interactive = true, Listening = () => { cancelledAt = DateTime.UtcNow; cts.Cancel(); } };

        var response = await new WebSocketExecutor(new WebSocketConnector(_pool))
            .ExecuteAsync(request, context, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10));
        holdServer.SetResult();

        Assert.True(response.IsSuccess, response.Error);
        Assert.InRange(DateTime.UtcNow - cancelledAt, TimeSpan.Zero, TimeSpan.FromSeconds(6)); // the 2 s abort fallback, with slack
        Assert.Contains(response.Messages, m => m.Content == "Disconnected");
    }
}
