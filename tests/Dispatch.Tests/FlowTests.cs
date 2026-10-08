using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json.Nodes;
using Dispatch.Application.Flows;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dispatch.Tests;

public class FlowRunnerTests
{
    private static FlowStep Request(Guid id, bool continueOnError = false) =>
        new() { Type = FlowStepType.Request, RequestId = id, ContinueOnError = continueOnError };

    private static (FlowRunner Runner, List<ApiRequest> Requests) RunnerWith(params ApiRequest[] requests)
    {
        var responder = new Dictionary<Guid, int>();
        var sender = new RequestSender([new FakeExecutor(r => new ApiResponse
        {
            StatusCode = 200, Succeeded = true, Body = $$"""{"n":{{responder.GetValueOrDefault(r.Id)}}}""",
            ContentType = "application/json"
        })], new NullHistory(), scripts: new Dispatch.Infrastructure.Scripting.JintScriptRunner());
        return (new FlowRunner(sender), requests.ToList());
    }

    [Fact]
    public async Task Runs_requests_in_order_and_shares_extracted_variables()
    {
        var login = new ApiRequest
        {
            Id = Guid.NewGuid(), Name = "Login", Url = "http://x/login",
            Extractions = [new ExtractionRule { Variable = "token", Source = ValueSource.JsonPath, Path = "$.n", Scope = VariableScope.Runtime }]
        };
        var use = new ApiRequest { Id = Guid.NewGuid(), Name = "Use", Url = "http://x/use?t={{token}}", QueryParams = [new("t", "{{token}}")] };
        var (runner, requests) = RunnerWith(login, use);
        var flow = new TestFlow { Name = "F", Steps = [Request(login.Id), Request(use.Id)] };

        var result = await runner.RunAsync(flow, new FlowRunOptions { Requests = requests });

        Assert.True(result.Passed);
        Assert.Equal(2, result.RequestsSent);
        Assert.Equal(2, result.Events.Count(e => e.Kind == FlowEventKind.StepFinished));
    }

    [Fact]
    public async Task If_repeat_foreach_and_setvariable_control_execution()
    {
        var req = new ApiRequest { Id = Guid.NewGuid(), Name = "Ping", Url = "http://x/ping" };
        var (runner, requests) = RunnerWith(req);
        var flow = new TestFlow
        {
            Name = "F",
            Steps =
            [
                new FlowStep { Type = FlowStepType.SetVariable, Variable = "env", Value = "prod" },
                new FlowStep
                {
                    Type = FlowStepType.If, Condition = new FlowCondition { Left = "{{env}}", Comparison = FlowComparison.Equals, Right = "prod" },
                    Children = [Request(req.Id)]
                },
                new FlowStep
                {
                    Type = FlowStepType.If, Condition = new FlowCondition { Left = "{{env}}", Comparison = FlowComparison.Equals, Right = "dev" },
                    Children = [Request(req.Id)]
                },
                new FlowStep { Type = FlowStepType.Repeat, Count = 3, Children = [Request(req.Id)] },
                new FlowStep { Type = FlowStepType.ForEach, Variable = "id", Value = "[1,2]", Children = [Request(req.Id)] }
            ]
        };

        var result = await runner.RunAsync(flow, new FlowRunOptions { Requests = requests });

        // 1 (if prod) + 0 (if dev) + 3 (repeat) + 2 (foreach) = 6 requests
        Assert.Equal(6, result.RequestsSent);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Until_retries_until_condition_then_continues()
    {
        var req = new ApiRequest
        {
            Id = Guid.NewGuid(), Name = "Poll", Url = "http://x/poll",
            // Capture attempt number into a variable via a test script.
            TestScript = "pm.variables.set('done', pm.variables.get('$attempt') === '3' ? 'yes' : 'no');"
        };
        var (runner, requests) = RunnerWith(req);
        var flow = new TestFlow
        {
            Name = "F",
            Steps =
            [
                new FlowStep
                {
                    Type = FlowStepType.Until, MaxAttempts = 5, Milliseconds = 0,
                    Condition = new FlowCondition { Left = "{{done}}", Comparison = FlowComparison.Equals, Right = "yes" },
                    Children = [Request(req.Id)]
                }
            ]
        };

        var result = await runner.RunAsync(flow, new FlowRunOptions { Requests = requests });
        Assert.True(result.Passed);
        Assert.Equal(3, result.RequestsSent);
    }

    [Fact]
    public async Task Stop_step_can_fail_the_flow_conditionally()
    {
        var req = new ApiRequest { Id = Guid.NewGuid(), Name = "A", Url = "http://x/a" };
        var (runner, requests) = RunnerWith(req);
        var flow = new TestFlow
        {
            Name = "F",
            Steps =
            [
                new FlowStep { Type = FlowStepType.SetVariable, Variable = "code", Value = "500" },
                new FlowStep
                {
                    Type = FlowStepType.Stop, Fail = true, Value = "server is down",
                    Condition = new FlowCondition { Left = "{{code}}", Comparison = FlowComparison.GreaterThan, Right = "499" }
                },
                Request(req.Id)
            ]
        };

        var result = await runner.RunAsync(flow, new FlowRunOptions { Requests = requests });
        Assert.False(result.Passed);
        Assert.True(result.Stopped);
        Assert.Equal("server is down", result.StopReason);
        Assert.Equal(0, result.RequestsSent); // stopped before the request
    }

    [Fact]
    public async Task A_failing_request_stops_the_flow_unless_continue_on_error()
    {
        var sender = new RequestSender([new FakeExecutor(_ => ApiResponse.Failed("boom", TimeSpan.Zero))], new NullHistory());
        var runner = new FlowRunner(sender);
        var req = new ApiRequest { Id = Guid.NewGuid(), Name = "Boom", Url = "http://x" };
        var after = new ApiRequest { Id = Guid.NewGuid(), Name = "After", Url = "http://x" };

        var stops = await runner.RunAsync(new TestFlow { Steps = [Request(req.Id), Request(after.Id)] },
            new FlowRunOptions { Requests = [req, after] });
        Assert.False(stops.Passed);
        Assert.True(stops.Stopped);

        var continues = await runner.RunAsync(new TestFlow { Steps = [Request(req.Id, continueOnError: true), Request(after.Id, continueOnError: true)] },
            new FlowRunOptions { Requests = [req, after] });
        Assert.Equal(2, continues.RequestsSent);
        Assert.False(continues.Passed); // a request errored, so the flow still fails
    }

    [Fact]
    public void Conditions_compare_numbers_strings_and_regex()
    {
        var vars = Dispatch.Application.Variables.VariableContext.For(null);
        vars.Set("x", "42", VariableScope.Runtime);
        Assert.True(FlowRunner.Evaluate(new FlowCondition { Left = "{{x}}", Comparison = FlowComparison.GreaterThan, Right = "10" }, vars));
        Assert.True(FlowRunner.Evaluate(new FlowCondition { Left = "{{x}}", Comparison = FlowComparison.Equals, Right = "42" }, vars));
        Assert.True(FlowRunner.Evaluate(new FlowCondition { Left = "{{x}}", Comparison = FlowComparison.Matches, Right = @"^\d+$" }, vars));
        Assert.False(FlowRunner.Evaluate(new FlowCondition { Left = "", Comparison = FlowComparison.IsTruthy }, vars));
    }

    [Fact]
    public void Numeric_conditions_ignore_the_os_culture()
    {
        var vars = Dispatch.Application.Variables.VariableContext.For(null);
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // de-DE reads "1.5" as 15 (thousands separator); conditions must use the invariant culture.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.False(FlowRunner.Evaluate(new FlowCondition { Left = "1.5", Comparison = FlowComparison.GreaterThan, Right = "10" }, vars));
            Assert.True(FlowRunner.Evaluate(new FlowCondition { Left = "1.5", Comparison = FlowComparison.LessThan, Right = "2" }, vars));
            Assert.True(FlowRunner.Evaluate(new FlowCondition { Left = "1.50", Comparison = FlowComparison.Equals, Right = "1.5" }, vars));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Step_limit_guards_against_runaway_loops()
    {
        var req = new ApiRequest { Id = Guid.NewGuid(), Name = "x", Url = "http://x" };
        var (runner, requests) = RunnerWith(req);
        // Until with a condition that never holds and MaxAttempts above the step cap.
        var flow = new TestFlow
        {
            Steps = [new FlowStep { Type = FlowStepType.Until, MaxAttempts = 10000, Milliseconds = 0,
                Condition = new FlowCondition { Left = "nope", Comparison = FlowComparison.Equals, Right = "yes" }, Children = [Request(req.Id)] }]
        };
        var result = await runner.RunAsync(flow, new FlowRunOptions { Requests = requests, MaxSteps = 50 });
        Assert.True(result.Stopped);
        Assert.False(result.Passed);
    }
}

[Collection("Console")]
public class CliFlowTests
{
    [Fact]
    public async Task Flow_command_runs_a_saved_flow_against_a_live_server()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapPost("/login", () => Results.Json(new { token = "t-123" }));
            app.MapGet("/me", (HttpContext ctx) => ctx.Request.Headers.Authorization == "Bearer t-123"
                ? Results.Json(new { user = "ada" }) : Results.StatusCode(401));
        });
        var dir = Cli.TempDir();
        var db = Path.Combine(dir, "db");

        // Build a collection with two requests and a flow, persisted to the DB the CLI will read.
        var services = Dispatch.Cli.Program.BuildServices(db);
        await services.GetRequiredService<Dispatch.Infrastructure.Persistence.DatabaseInitializer>().InitializeAsync();
        var collections = services.GetRequiredService<Dispatch.Application.Abstractions.ICollectionRepository>();
        var flows = services.GetRequiredService<Dispatch.Application.Abstractions.IFlowRepository>();

        var login = new ApiRequest { Name = "Login", Method = HttpVerb.Post, Url = $"{server.BaseUrl}/login",
            Extractions = [new ExtractionRule { Variable = "token", Source = ValueSource.JsonPath, Path = "$.token", Scope = VariableScope.Runtime }] };
        var me = new ApiRequest { Name = "Me", Url = $"{server.BaseUrl}/me",
            Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "{{token}}" },
            Assertions = [new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "200" }] };
        var collection = new Dispatch.Domain.RequestCollection { Name = "Auth", Requests = [] };
        await collections.AddAsync(collection);
        login.CollectionId = me.CollectionId = collection.Id;
        await collections.SaveRequestAsync(login);
        await collections.SaveRequestAsync(me);
        await flows.SaveAsync(new TestFlow { Name = "Smoke", CollectionId = collection.Id,
            Steps = [new FlowStep { Type = FlowStepType.Request, RequestId = login.Id },
                     new FlowStep { Type = FlowStepType.Request, RequestId = me.Id }] });
        await services.DisposeAsync();

        var (exit, output) = await Cli.RunAsync("flow", "Auth", "--db", db, "--no-color");
        Assert.Equal(0, exit);
        Assert.Contains("PASSED", output);
        Assert.Contains("Login", output);
        Assert.Contains("Me", output);
    }
}
