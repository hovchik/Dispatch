using System.Net.WebSockets;
using System.Text;
using Dispatch.Application.Mock;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure;
using Dispatch.Infrastructure.Mock;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Tests;

public class SessionPlayerTests
{
    private static SessionMessage Server(long at, string content, string? label = null) =>
        new() { AtMs = at, Direction = MessageDirection.Received, Content = content, Label = label };

    private static SessionMessage Client(long at, string content) => new() { AtMs = at, Direction = MessageDirection.Sent, Content = content };

    private static List<SessionMessage> Ticker() =>
    [
        Server(20, """{"type":"welcome"}"""),
        Client(100, """{"op":"subscribe","channel":"ticker","id":7}"""),
        Server(150, """{"id":7,"ok":true,"channel":"ticker"}"""),
        Server(300, """{"type":"tick","price":10,"channel":"{{message.channel}}"}"""),
        Client(500, "ping"),
        Server(510, "pong")
    ];

    [Fact]
    public void Splits_the_recording_into_an_opening_and_client_triggered_segments()
    {
        var player = new SessionPlayer(Ticker());
        Assert.Equal(2, player.SegmentCount);
        var opening = Assert.Single(player.Opening());
        Assert.Equal(TimeSpan.FromMilliseconds(20), opening.Delay);
        Assert.Equal("""{"type":"welcome"}""", opening.Content);
    }

    [Fact]
    public void Matches_client_messages_ignoring_ids_and_correlates_them_into_replies()
    {
        var player = new SessionPlayer(Ticker());
        var reply = player.OnClientMessage("""{"op":"subscribe","channel":"ticker","id":42}""");

        Assert.StartsWith("matched ignoring ids", reply.How);
        Assert.Equal(["""{"id":42,"ok":true,"channel":"ticker"}""", """{"type":"tick","price":10,"channel":"ticker"}"""],
            reply.Messages.Select(m => m.Content));
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150)], reply.Messages.Select(m => m.Delay));
    }

    [Fact]
    public void Prefers_exact_matches_then_falls_back_to_recorded_order()
    {
        var player = new SessionPlayer(Ticker(), new SessionReplayOptions { Speed = 0 });
        var ping = player.OnClientMessage("ping");
        Assert.StartsWith("exact match", ping.How);
        Assert.Equal("pong", Assert.Single(ping.Messages).Content);
        Assert.Equal(TimeSpan.Zero, ping.Messages[0].Delay);

        var other = player.OnClientMessage("something else");
        Assert.StartsWith("next in recorded order", other.How);
        Assert.Equal(2, other.Messages.Count);

        Assert.Equal("no recorded reply left", player.OnClientMessage("again").How);
    }

    [Fact]
    public void A_message_answered_before_gets_the_same_answer_again_instead_of_the_next_segment()
    {
        var player = new SessionPlayer(Ticker(), new SessionReplayOptions { Speed = 0 });
        Assert.Equal("pong", Assert.Single(player.OnClientMessage("ping").Messages).Content);

        var again = player.OnClientMessage("ping");
        Assert.StartsWith("exact match (repeated)", again.How);
        Assert.Equal("pong", Assert.Single(again.Messages).Content);

        // The subscribe segment is still unplayed and answers a re-subscribe with a new id, now and later.
        Assert.StartsWith("matched ignoring ids", player.OnClientMessage("""{"op":"subscribe","channel":"ticker","id":1}""").How);
        var resubscribe = player.OnClientMessage("""{"op":"subscribe","channel":"ticker","id":2}""");
        Assert.StartsWith("matched ignoring ids (repeated)", resubscribe.How);
        Assert.Equal("""{"id":2,"ok":true,"channel":"ticker"}""", resubscribe.Messages[0].Content);
    }

    [Fact]
    public void A_greeting_recorded_after_a_message_sent_on_connect_moves_to_the_opening()
    {
        var player = new SessionPlayer(
        [
            Client(1, """{"op":"subscribe","id":7}"""),
            Server(20, """{"type":"welcome"}"""),
            Server(25, """{"id":7,"ok":true}"""),
            Server(40, """{"type":"tick"}""")
        ]);
        Assert.Equal("""{"type":"welcome"}""", Assert.Single(player.Opening()).Content);
        Assert.Equal(["""{"id":9,"ok":true}""", """{"type":"tick"}"""],
            player.OnClientMessage("""{"op":"subscribe","id":9}""").Messages.Select(m => m.Content));
    }

    [Fact]
    public void Speed_scales_delays_and_long_gaps_are_capped()
    {
        var session = new List<SessionMessage> { Server(1000, "a"), Server(61_000, "b") };
        var fast = new SessionPlayer(session, new SessionReplayOptions { Speed = 2, MaxGap = TimeSpan.FromSeconds(10) }).Opening();
        Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5)], fast.Select(m => m.Delay));
    }

    [Fact]
    public void Recording_keeps_sent_and_received_messages_timed_from_connecting()
    {
        var t = DateTimeOffset.UtcNow;
        var log = new List<StreamMessage>
        {
            new(t, MessageDirection.Info, "Connected"),
            new(t.AddMilliseconds(40), MessageDirection.Received, "hello", "message #1"),
            new(t.AddMilliseconds(90), MessageDirection.Sent, "hi"),
            new(t.AddMilliseconds(95), MessageDirection.Error, "oops")
        };
        var session = SessionRecording.From(log);
        Assert.Equal([(40L, MessageDirection.Received, "hello"), (90L, MessageDirection.Sent, "hi")],
            session.Select(m => (m.AtMs, m.Direction, m.Content)));
        Assert.Equal("message #1", session[0].Label);
    }
}

/// <summary>Records real sessions with Dispatch's own clients, then replays them from the mock server.</summary>
public class SessionMockIntegrationTests
{
    private static ServiceProvider Services() => new ServiceCollection().AddDispatchEngine()
        .AddSingleton<Application.Abstractions.IHistoryRepository, NullHistory>().BuildServiceProvider();

    /// <summary>A live ticker: welcome on connect; a subscribe gets an ack (echoing its id) and two ticks.</summary>
    private static Task<TestServer> LiveTickerAsync() => TestServer.StartAsync(app =>
    {
        app.Map("/feed", async (HttpContext ctx) =>
        {
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            async Task Send(string text) => await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
            await Send("""{"type":"welcome"}""");
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
                var node = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count))!;
                await Send($$"""{"id":{{node["id"]}},"ok":true}""");
                await Send("""{"type":"tick","price":10}""");
                await Send("""{"type":"tick","price":11}""");
            }
        });
        app.MapGet("/prices", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("id: 1\nevent: price\ndata: {\"p\":10}\n\n");
            await ctx.Response.Body.FlushAsync();
            await Task.Delay(30);
            await ctx.Response.WriteAsync("data: line one\ndata: line two\n\n");
        });
    });

    [Fact]
    public async Task A_recorded_websocket_session_is_replayed_with_live_ids()
    {
        await using var live = await LiveTickerAsync();
        await using var services = Services();
        var sender = services.GetRequiredService<IRequestSender>();

        // 1. Record: connect, subscribe with id 7, listen briefly.
        var request = new ApiRequest
        {
            Name = "Ticker", Kind = RequestKind.WebSocket, Url = live.BaseUrl.Replace("http", "ws") + "/feed",
            Protocol = new ProtocolSettings { Stream = new StreamSettings { ListenSeconds = 1, InitialMessages = [new("sub", """{"op":"subscribe","id":7}""")] } }
        };
        var recorded = await sender.SendAsync(request, new SendOptions { RecordHistory = false }, CancellationToken.None);
        var session = SessionRecording.From(recorded.Messages);
        Assert.Equal(5, session.Count); // welcome, subscribe, ack, tick, tick
        request.Examples = [new ResponseExample { Name = "Session", Session = session }];

        // 2. Replay from the mock server, no live server involved.
        await using var mock = new MockServer();
        var port = HttpProtocolExecutorTests.FreePort();
        await mock.StartAsync([request], new MockServerOptions { Port = port, SessionSpeed = 0 });
        Assert.Contains(mock.Routes, r => r.Method == "WS" && r.Template == "/feed");

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        async Task<string> Receive()
        {
            var buffer = new byte[4096];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var r = await client.ReceiveAsync(buffer, timeout.Token);
            return Encoding.UTF8.GetString(buffer, 0, r.Count);
        }
        Assert.Equal("""{"type":"welcome"}""", await Receive());
        await client.SendAsync(Encoding.UTF8.GetBytes("""{"op":"subscribe","id":99}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal("""{"id":99,"ok":true}""", await Receive());
        Assert.Equal("""{"type":"tick","price":10}""", await Receive());
        Assert.Equal("""{"type":"tick","price":11}""", await Receive());
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task A_recorded_sse_stream_reads_back_the_same_events()
    {
        await using var live = await LiveTickerAsync();
        await using var services = Services();
        var sender = services.GetRequiredService<IRequestSender>();
        var request = new ApiRequest
        {
            Name = "Prices", Kind = RequestKind.Sse, Url = $"{live.BaseUrl}/prices",
            Protocol = new ProtocolSettings { Stream = new StreamSettings { ListenSeconds = 2, MaxMessages = 2 } }
        };
        var recorded = await sender.SendAsync(request, new SendOptions { RecordHistory = false }, CancellationToken.None);
        var original = recorded.Messages.Where(m => m.Direction == MessageDirection.Received).Select(m => (m.Content, m.Label)).ToList();
        Assert.Equal(2, original.Count);
        request.Examples = [new ResponseExample { Name = "Session", Session = SessionRecording.From(recorded.Messages) }];

        await using var mock = new MockServer();
        var port = HttpProtocolExecutorTests.FreePort();
        await mock.StartAsync([request], new MockServerOptions { Port = port, SessionSpeed = 0 });

        var replayRequest = request.Clone();
        replayRequest.Url = $"http://127.0.0.1:{port}/prices";
        var replayed = await sender.SendAsync(replayRequest, new SendOptions { RecordHistory = false }, CancellationToken.None);
        Assert.Equal(original, replayed.Messages.Where(m => m.Direction == MessageDirection.Received).Select(m => (m.Content, m.Label)));
        // The SSE event id carries over to later events (per the spec), in the recording and the replay alike.
        Assert.Equal(("line one\nline two", "message #1"), original[1]);
    }
}
