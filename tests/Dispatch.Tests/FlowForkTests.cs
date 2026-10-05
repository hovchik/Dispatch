using Dispatch.Application.Abstractions;
using Dispatch.Application.Flows;
using Dispatch.Domain;
using Dispatch.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Tests;

/// <summary>A shop API with call counters, and a flow: login → list orders → (if any) get each order → delay → done.</summary>
public sealed class ForkScenario : IAsyncDisposable
{
    private int _logins;
    private int _lists;
    private readonly List<string> _orderReads = [];

    public TestServer Server { get; private set; } = null!;
    public ServiceProvider Services { get; private set; } = null!;
    public List<ApiRequest> Requests { get; private set; } = [];
    public TestFlow Flow { get; private set; } = null!;
    public int Logins => _logins;
    public int Lists => _lists;
    public IReadOnlyList<string> OrderReads { get { lock (_orderReads) return _orderReads.ToList(); } }

    public static async Task<ForkScenario> StartAsync()
    {
        var s = new ForkScenario();
        s.Server = await TestServer.StartAsync(app =>
        {
            app.MapPost("/login", () => { Interlocked.Increment(ref s._logins); return Results.Json(new { token = "t-1" }); });
            app.MapGet("/orders", (HttpContext ctx) =>
            {
                Interlocked.Increment(ref s._lists);
                return ctx.Request.Headers.Authorization == "Bearer t-1"
                    ? Results.Json(new { items = new[] { new { id = 1 }, new { id = 2 } } })
                    : Results.StatusCode(401);
            });
            app.MapGet("/orders/{id}", (string id) =>
            {
                lock (s._orderReads)
                    s._orderReads.Add(id);
                return id is "1" or "2" ? Results.Json(new { id = int.Parse(id), status = "paid" }) : Results.NotFound();
            });
        });
        s.Services = new ServiceCollection().AddDispatchEngine().AddSingleton<IHistoryRepository, NullHistory>().BuildServiceProvider();

        var b = s.Server.BaseUrl;
        Assertion Ok() => new() { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "200" };
        var login = new ApiRequest
        {
            Name = "Login", Method = HttpVerb.Post, Url = $"{b}/login", Assertions = [Ok()],
            Extractions = [new ExtractionRule { Variable = "token", Path = "$.token", Scope = VariableScope.Runtime }]
        };
        var list = new ApiRequest
        {
            Name = "List orders", Url = $"{b}/orders", Headers = [new("Authorization", "Bearer {{token}}")], Assertions = [Ok()],
            Extractions = [new ExtractionRule { Variable = "orders", Source = ValueSource.JsonPath, Path = "$.items", Scope = VariableScope.Runtime }]
        };
        var get = new ApiRequest { Name = "Get order", Url = $"{b}/orders/{{{{orderId}}}}", Assertions = [Ok()] };
        s.Requests = [login, list, get];
        s.Flow = new TestFlow
        {
            Name = "Order check",
            Steps =
            [
                new FlowStep { Type = FlowStepType.Request, RequestId = login.Id },
                new FlowStep { Type = FlowStepType.Request, RequestId = list.Id },
                new FlowStep
                {
                    Type = FlowStepType.ForEach, Value = "{{orders}}", Variable = "order",
                    Children =
                    [
                        new FlowStep { Type = FlowStepType.Script, Value = "pm.variables.set('orderId', JSON.parse(pm.variables.get('order')).id);" },
                        new FlowStep { Type = FlowStepType.Request, RequestId = get.Id }
                    ]
                },
                new FlowStep { Type = FlowStepType.Delay, Milliseconds = 300 }
            ]
        };
        return s;
    }

    public Task<FlowResult> RunAsync(FlowFork? fork = null) =>
        Services.GetRequiredService<FlowRunner>().RunAsync(Flow, new FlowRunOptions { Requests = Requests, Fork = fork });

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await Services.DisposeAsync();
    }
}

public class FlowForkTests
{
    [Fact]
    public async Task Runs_record_every_request_with_its_occurrence()
    {
        await using var s = await ForkScenario.StartAsync();
        var result = await s.RunAsync();

        Assert.True(result.Passed, string.Join("\n", result.Events.Select(e => $"{e.StepName} {e.Detail}")));
        Assert.Equal(["Login", "List orders", "Get order", "Get order"], result.Recording.Exchanges.Select(e => e.StepName));
        Assert.Equal([1, 2, 3, 4], result.Recording.Exchanges.Select(e => e.Ordinal));
        Assert.Equal([1, 1, 1, 2], result.Recording.Exchanges.Select(e => e.Occurrence));
        Assert.Contains(result.Events, e => e.Exchange == 3 && e.StepName == "Get order");

        var roundTrip = FlowRecording.FromJson(result.Recording.ToJson());
        Assert.Equal(4, roundTrip.Exchanges.Count);
        Assert.Contains("\"id\":2", roundTrip.Find(4)!.Response.Body);
        Assert.True(roundTrip.Passed);
    }

    [Fact]
    public async Task Offline_fork_with_an_empty_list_skips_the_loop_without_touching_the_server()
    {
        await using var s = await ForkScenario.StartAsync();
        var original = await s.RunAsync();
        var (logins, lists, reads) = (s.Logins, s.Lists, s.OrderReads.Count);

        var replay = await s.RunAsync(new FlowFork
        {
            Recording = original.Recording, ForkOrdinal = 2, After = AfterFork.Recorded,
            Replacement = ForkPresets.EmptyArrays(original.Recording.Find(2)!.Response)
        });

        Assert.True(replay.ForkReached);
        Assert.Equal((logins, lists, reads), (s.Logins, s.Lists, s.OrderReads.Count)); // nothing was sent
        Assert.Equal([ExchangeOrigin.Recorded, ExchangeOrigin.Edited],
            replay.Events.Where(e => e.Origin is not null).Select(e => e.Origin!.Value));
        Assert.Contains(replay.Events, e => e.Detail.Contains("skipped in replay"));
        var diff = FlowRunDiff.Compare(original.Recording, replay);
        Assert.Contains(diff, d => d.Step == "Get order" && d.After == "did not run");
        Assert.Contains(diff, d => d.Step == "Get order (#2)" && d.After == "did not run");
    }

    [Fact]
    public async Task Live_fork_sends_the_downstream_requests_for_real()
    {
        await using var s = await ForkScenario.StartAsync();
        var original = await s.RunAsync();
        var logins = s.Logins;
        var edited = original.Recording.Find(2)!.Response.Clone();
        edited.Body = """{"items":[{"id":99}]}""";

        var replay = await s.RunAsync(new FlowFork { Recording = original.Recording, ForkOrdinal = 2, Replacement = edited });

        Assert.Equal(logins, s.Logins); // login came from the recording
        Assert.Equal("99", s.OrderReads[^1]); // the edited id went out live
        Assert.False(replay.Passed);
        var diff = FlowRunDiff.Compare(original.Recording, replay);
        Assert.Contains(diff, d => d.Step == "Flow" && d.Before == "passed" && d.After == "failed");
        Assert.Contains(diff, d => d.Step == "Get order" && d.Before.StartsWith("passed (200") && d.After.StartsWith("failed (404"));
        Assert.Contains(diff, d => d.Step == "Get order (#2)" && d.After == "did not run");
    }

    [Fact]
    public async Task Forks_one_occurrence_of_a_looped_request()
    {
        await using var s = await ForkScenario.StartAsync();
        var original = await s.RunAsync();
        var replay = await s.RunAsync(new FlowFork
        {
            Recording = original.Recording, ForkOrdinal = 4, After = AfterFork.Recorded,
            Replacement = ForkPresets.Status(original.Recording.Find(4)!.Response, 500, "Internal Server Error")
        });

        var gets = replay.Events.Where(e => e.StepName == "Get order" && e.Kind == FlowEventKind.StepFinished).ToList();
        Assert.Equal([ExchangeOrigin.Recorded, ExchangeOrigin.Edited], gets.Select(e => e.Origin!.Value));
        Assert.True(gets[0].Ok);
        Assert.False(gets[1].Ok);
        Assert.StartsWith("✎ 500", gets[1].Detail);
    }

    [Fact]
    public async Task Simulated_no_response_and_unreached_forks_are_reported()
    {
        await using var s = await ForkScenario.StartAsync();
        var original = await s.RunAsync();

        var timeout = await s.RunAsync(new FlowFork
        {
            Recording = original.Recording, ForkOrdinal = 1, After = AfterFork.Recorded,
            Replacement = ForkPresets.NoResponse(original.Recording.Find(1)!.Response)
        });
        Assert.False(timeout.Passed);
        Assert.Contains(timeout.Events, e => e.Detail.Contains("timed out"));

        // Fork #4 needs the second loop pass; with an edited list of one order it never happens.
        var shorter = original.Recording.Find(2)!.Response.Clone();
        shorter.Body = """{"items":[{"id":1}]}""";
        var trimmed = new FlowRecording { Exchanges = original.Recording.Exchanges.Select(e => e.Ordinal == 2 ? e with { Response = shorter } : e).ToList() };
        var unreached = await s.RunAsync(new FlowFork { Recording = trimmed, ForkOrdinal = 4, After = AfterFork.Recorded });
        Assert.False(unreached.ForkReached);
        Assert.Contains(unreached.Events, e => e.StepName == "Fork" && e.Detail.Contains("not reached"));
    }
}

[Collection("Console")]
public class CliFlowForkTests
{
    [Fact]
    public async Task Flow_command_records_and_replays_with_a_fork()
    {
        await using var s = await ForkScenario.StartAsync();
        var dir = Cli.TempDir();
        var db = Path.Combine(dir, "db");
        await using (var services = Dispatch.Cli.Program.BuildServices(db))
        {
            await services.GetRequiredService<Dispatch.Infrastructure.Persistence.DatabaseInitializer>().InitializeAsync();
            var collection = new RequestCollection { Name = "Shop" };
            await services.GetRequiredService<ICollectionRepository>().AddAsync(collection);
            foreach (var r in s.Requests)
            {
                r.CollectionId = collection.Id;
                await services.GetRequiredService<ICollectionRepository>().SaveRequestAsync(r);
            }
            s.Flow.CollectionId = collection.Id;
            await services.GetRequiredService<IFlowRepository>().SaveAsync(s.Flow);
        }
        var recording = Path.Combine(dir, "run.json");

        var (exit, output) = await Cli.RunAsync("flow", "Shop", "--db", db, "--no-color", "--record", recording);
        Assert.True(exit == 0, output);
        Assert.Contains("#2 List orders", output);
        Assert.Contains("Recorded 4 request(s)", output);

        var (replayExit, replayOutput) = await Cli.RunAsync("flow", "Shop", "--db", db, "--no-color", "--replay", recording,
            "--fork", "3", "--status", "503", "--offline");
        Assert.Equal(1, replayExit);
        Assert.Contains("request #3 edited, offline", replayOutput);
        Assert.Contains("Get order: passed (200 OK) → failed (503 Service Unavailable)", replayOutput);
        Assert.Contains("Flow: passed → failed", replayOutput);
    }
}
