using System.Text.Json.Nodes;
using Dispatch.Application.Compare;
using Dispatch.Application.Diff;
using Dispatch.Application.Interop;
using Dispatch.Application.Load;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Tests;

public class EnvironmentComparerTests
{
    private static CompareSide Side(int status, string body, string contentType = "application/json", params (string Name, string Value)[] headers) =>
        new(status, $"{status} OK", 10, body.Length, null, body, contentType, headers.Select(h => new ResponseHeader(h.Name, h.Value)).ToList());

    private static readonly CompareOptions Options = new()
    {
        Requests = [], Left = new CompareTarget { Name = "a" }, Right = new CompareTarget { Name = "b" }, IgnorePaths = ["$.at", "$..id"]
    };

    [Fact]
    public void Rebase_moves_absolute_urls_onto_the_base_and_keeps_variable_urls()
    {
        var staging = new Uri("https://staging.example.com/api/");
        Assert.Equal("https://staging.example.com/api/v1/users?x=1", CompareTarget.Rebase("https://api.example.com/v1/users?x=1", staging));
        Assert.Equal("wss://staging.example.com/api/live", CompareTarget.Rebase("ws://api.example.com/live", staging));
        Assert.Equal("{{baseUrl}}/users", CompareTarget.Rebase("{{baseUrl}}/users", staging));
        // Placeholders in the path and query survive untouched (System.Uri would percent-encode the braces).
        Assert.Equal("https://staging.example.com/api/users/{{id}}?q={{term}}", CompareTarget.Rebase("https://api.example.com/users/{{id}}?q={{term}}", staging));
        Assert.Equal("https://staging.example.com/api", CompareTarget.Rebase("https://api.example.com", staging));
        Assert.Throws<ArgumentException>(() => CompareTarget.FromBaseUrl("staging"));
    }

    [Fact]
    public void Identical_bodies_with_only_ignored_or_volatile_differences_are_the_same()
    {
        var request = new ApiRequest { Name = "user", Url = "http://x/user" };
        var left = Side(200, """{"id":1,"name":"Ann","at":"2024-01-01"}""", headers: [("Date", "Mon"), ("X-Request-Id", "1"), ("X-Api-Version", "7")]);
        var right = Side(200, """{"id":2,"name":"Ann","at":"2025-01-01"}""", headers: [("Date", "Tue"), ("X-Request-Id", "2"), ("x-api-version", "7")]);

        var result = EnvironmentComparer.Analyze(request, left, right, Options);

        Assert.Equal(CompareOutcome.Same, result.Outcome);
        Assert.Empty(result.BodyChanges);
        Assert.Empty(result.HeaderChanges);
        Assert.Equal("identical", result.Summary);
    }

    [Fact]
    public void Status_body_and_header_differences_are_reported()
    {
        var request = new ApiRequest { Name = "user", Url = "http://x/user" };
        var left = Side(200, """{"id":1,"name":"Ann","email":"a@x"}""", headers: [("X-Api-Version", "7")]);
        var right = Side(201, """{"id":1,"name":"Bob","email":null,"role":"admin"}""", headers: [("X-Api-Version", "8")]);

        var result = EnvironmentComparer.Analyze(request, left, right, Options);

        Assert.Equal(CompareOutcome.Different, result.Outcome);
        Assert.True(result.StatusDiffers);
        Assert.Contains(result.BodyChanges, c => c.Kind == JsonChangeKind.Changed && c.Path == "$.name");
        Assert.Contains(result.BodyChanges, c => c.Kind == JsonChangeKind.TypeChanged && c.Path == "$.email");
        Assert.Contains(result.BodyChanges, c => c.Kind == JsonChangeKind.Added && c.Path == "$.role");
        Assert.Equal("~ x-api-version: 7 → 8", Assert.Single(result.HeaderChanges).ToString());
        Assert.Contains("status 200 OK → 201 OK", result.Summary);
        Assert.Contains("3 body change(s)", result.Summary);
    }

    [Fact]
    public void Text_bodies_fall_back_to_a_line_diff_and_errors_fail_the_request()
    {
        var request = new ApiRequest { Name = "home", Url = "http://x/" };
        var text = EnvironmentComparer.Analyze(request, Side(200, "a\nb\nc", "text/plain"), Side(200, "a\nB\nc", "text/plain"), Options);
        Assert.Equal(CompareOutcome.Different, text.Outcome);
        Assert.False(text.BodyIsJson);
        Assert.Equal(2, text.LineChanges);

        var failed = EnvironmentComparer.Analyze(request, Side(200, "{}"), new CompareSide(0, "error", 1, 0, "refused", "", null, []), Options);
        Assert.Equal(CompareOutcome.Failed, failed.Outcome);
        Assert.Equal("right failed", failed.Summary);
    }

    [Fact]
    public void Failed_checks_make_identical_responses_different()
    {
        var request = new ApiRequest { Name = "health", Url = "http://x/health" };
        var body = """{"status":"down"}""";
        var left = Side(500, body) with { Checks = [new TestResult("status is 200", false, "expected 200, got 500")] };
        var right = Side(500, body) with { Checks = [new TestResult("status is 200", false, "expected 200, got 500")] };

        var result = EnvironmentComparer.Analyze(request, left, right, Options);

        Assert.Equal(CompareOutcome.Different, result.Outcome);
        Assert.True(result.ChecksFailed);
        Assert.Equal("1 check(s) failed on left, 1 check(s) failed on right", result.Summary);
        var report = new CompareReport { LeftName = "a", RightName = "b", Results = [result] };
        Assert.True(report.HasDifferences);
        Assert.Contains(CompareReportWriter.Insights(report), i => i.Text.Contains("failed an assertion"));
        Assert.Contains("check failed on left: status is 200", CompareReportWriter.Text(report));
        Assert.Contains("check failed on right", CompareReportWriter.Html(report));
        Assert.True(JsonNode.Parse(CompareReportWriter.Json(report))!["requests"]![0]!["checksFailed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Compares_two_live_deployments_with_separate_variable_chains_and_renders_reports()
    {
        var bodyB = new { id = 7, name = "Ann", at = 2, plan = "pro" };
        await using var a = await TestServer.StartAsync(app =>
        {
            app.MapPost("/login", () => Results.Json(new { token = "tok-a" }));
            app.MapGet("/me", (HttpContext ctx) => Results.Json(new { id = 7, name = "Ann", at = 1, token = ctx.Request.Headers.Authorization.ToString() }));
        });
        await using var b = await TestServer.StartAsync(app =>
        {
            app.MapPost("/login", () => Results.Json(new { token = "tok-b" }));
            app.MapGet("/me", (HttpContext ctx) => Results.Json(new { id = 8, name = "Ann", at = 2, plan = "pro", token = ctx.Request.Headers.Authorization.ToString() }));
        });

        var login = new ApiRequest
        {
            Name = "login", Method = HttpVerb.Post, Url = a.BaseUrl + "/login",
            Extractions = [new ExtractionRule { Variable = "token", Source = ValueSource.JsonPath, Path = "$.token", Scope = VariableScope.Runtime }]
        };
        var me = new ApiRequest { Name = "me", Url = a.BaseUrl + "/me", Headers = [new("Authorization", "Bearer {{token}}")] };
        await using var services = new ServiceCollection().AddDispatchEngine()
            .AddSingleton<Application.Abstractions.IHistoryRepository, NullHistory>().BuildServiceProvider();

        var progress = new List<CompareRequestResult>();
        var report = await new EnvironmentComparer(services.GetRequiredService<IRequestSender>()).RunAsync(new CompareOptions
        {
            Requests = [login, me],
            Left = CompareTarget.FromBaseUrl(a.BaseUrl),
            Right = CompareTarget.FromBaseUrl(b.BaseUrl),
            IgnorePaths = ["$.at", "$.id"]
        }, new SyncProgress<CompareRequestResult>(progress.Add));

        Assert.Equal(2, progress.Count);
        Assert.Equal("DIFFERENT", report.Verdict);
        var loginResult = report.Results[0];
        Assert.Equal(CompareOutcome.Different, loginResult.Outcome);
        Assert.Equal("~ $.token: \"tok-a\" → \"tok-b\"", Assert.Single(loginResult.BodyChanges).ToString());
        var meResult = report.Results[1];
        // Each side used its own token, and the ignored paths hide the id / timestamp noise.
        Assert.Contains(meResult.BodyChanges, c => c.Path == "$.token" && c.Left == "\"Bearer tok-a\"" && c.Right == "\"Bearer tok-b\"");
        Assert.Contains(meResult.BodyChanges, c => c.Kind == JsonChangeKind.Added && c.Path == "$.plan");
        Assert.DoesNotContain(meResult.BodyChanges, c => c.Path is "$.at" or "$.id");

        var text = CompareReportWriter.Text(report);
        Assert.Contains("DIFFERENT", text);
        Assert.Contains("$.plan", text);
        var html = CompareReportWriter.Html(report);
        Assert.Contains("Compare environments", html);
        Assert.Contains("$.plan", html);
        var json = JsonNode.Parse(CompareReportWriter.Json(report))!;
        Assert.Equal("DIFFERENT", json["verdict"]!.GetValue<string>());
        Assert.Equal(2, json["totals"]!["different"]!.GetValue<int>());
        Assert.Contains(CompareReportWriter.Insights(report), i => i.Text.Contains("$.plan only on"));
    }
}

[Collection("Console")]
public class CompareCliTests
{
    [Fact]
    public async Task Compare_command_diffs_two_base_urls_and_fails_on_diff()
    {
        await using var a = await TestServer.StartAsync(app => app.MapGet("/user", () => Results.Json(new { id = 1, name = "Ann", at = 1 })));
        await using var b = await TestServer.StartAsync(app => app.MapGet("/user", () => Results.Json(new { id = 1, name = "Ann", at = 2 })));
        var dir = Cli.TempDir();
        var file = Path.Combine(dir, "api.dispatch.json");
        await File.WriteAllTextAsync(file, DispatchFormat.ExportCollection(new RequestCollection
        {
            Name = "Parity", Requests = [new ApiRequest { Name = "user", Url = a.BaseUrl + "/user" }]
        }));

        var same = await Cli.RunAsync("compare", file, "-e", a.BaseUrl, "-e", b.BaseUrl, "--ignore", "$.at", "--fail-on-diff",
            "-r", "cli,json,html", "-o", dir, "--db", Path.Combine(dir, "d.db"));
        Assert.Equal(0, same.Exit);
        Assert.Contains("IDENTICAL", same.Output);
        Assert.Single(Directory.GetFiles(dir, "parity-compare-*.json"));
        Assert.Single(Directory.GetFiles(dir, "parity-compare-*.html"));

        var differ = await Cli.RunAsync("compare", file, "-e", a.BaseUrl, "-e", b.BaseUrl, "--fail-on-diff", "--db", Path.Combine(dir, "d.db"));
        Assert.Equal(1, differ.Exit);
        Assert.Contains("~ $.at: 1 → 2", differ.Output);

        var usage = await Cli.RunAsync("compare", file, "-e", a.BaseUrl, "--db", Path.Combine(dir, "d.db"));
        Assert.Equal(2, usage.Exit);
        Assert.Contains("exactly two -e", usage.Output);
    }
}

public class LoadBaselineTests
{
    private static LoadReport Report(double p95, double rps, int errors = 0, int requests = 1000, params (string Name, double P95)[] perRequest) => new()
    {
        Duration = TimeSpan.FromSeconds(requests / rps),
        VirtualUsers = 10,
        TotalRequests = requests,
        Errors = errors,
        Latency = new LatencyStats(1, p95 / 2, p95 / 2, p95 * 0.9, p95, p95 * 1.5, p95 * 2),
        PerRequest = perRequest.Select(r => new LoadRequestStats(r.Name, requests / Math.Max(1, perRequest.Length), 0,
            new LatencyStats(1, r.P95 / 2, r.P95 / 2, r.P95 * 0.9, r.P95, r.P95 * 1.5, r.P95 * 2), new Dictionary<string, int> { ["200"] = 1 })).ToList()
    };

    [Fact]
    public void Baseline_round_trips_through_the_json_report()
    {
        var report = Report(120, 50, errors: 10, perRequest: [("login", 80), ("search", 200)]);
        var baseline = LoadBaseline.Parse(LoadBaseline.Serialize(report, "Shop"));

        Assert.Equal("Shop", baseline.Name);
        Assert.Equal(10, baseline.VirtualUsers);
        Assert.Equal(1000, baseline.TotalRequests);
        Assert.Equal(0.01, baseline.ErrorRate, 3);
        Assert.Equal(50, baseline.RequestsPerSecond, 1);
        Assert.Equal(120, baseline.Latency.P95);
        Assert.Equal(["login", "search"], baseline.PerRequest.Select(r => r.Name));
        Assert.Equal(200, baseline.PerRequest[1].Latency.P95);
        Assert.Throws<FormatException>(() => LoadBaseline.Parse("{\"hello\":1}"));
        Assert.Throws<FormatException>(() => LoadBaseline.Parse("not json"));
    }

    [Fact]
    public void Regressions_improvements_and_noise_are_told_apart()
    {
        var baseline = LoadBaseline.From(Report(100, 50, perRequest: [("login", 100), ("search", 100)]), "base");

        var regressed = LoadBaselineComparer.Compare(Report(100, 50, perRequest: [("login", 100), ("search", 140)]), baseline, 10);
        Assert.Equal("REGRESSED", regressed.Verdict);
        Assert.True(regressed.IsRegression);
        Assert.Equal("REGRESSED", regressed.PerRequest.Single(r => r.Name == "search").Verdict);
        Assert.Equal("UNCHANGED", regressed.PerRequest.Single(r => r.Name == "login").Verdict);
        Assert.Contains(regressed.Findings, f => f.StartsWith("search: p95: 100 ms → 140 ms (+40%)", StringComparison.Ordinal));

        var slowerThroughput = LoadBaselineComparer.Compare(Report(100, 30, perRequest: [("login", 100), ("search", 100)]), baseline, 10);
        Assert.Contains(slowerThroughput.Overall, m => m.Metric == "throughput" && m.Regressed);

        var improved = LoadBaselineComparer.Compare(Report(60, 80, perRequest: [("login", 60), ("search", 60)]), baseline, 10);
        Assert.Equal("IMPROVED", improved.Verdict);
        Assert.False(improved.IsRegression);

        // +3 ms on a 100 ms p95 is within 10 %; +12 % but under the 5 ms noise floor is not a regression either.
        var unchanged = LoadBaselineComparer.Compare(Report(103, 50, perRequest: [("login", 103), ("search", 103)]), baseline, 10);
        Assert.Equal("UNCHANGED", unchanged.Verdict);
        var tiny = LoadBaselineComparer.Compare(Report(4.4, 50), LoadBaseline.From(Report(4, 50), "b"), 5);
        Assert.False(tiny.IsRegression);

        var moreErrors = LoadBaselineComparer.Compare(Report(100, 50, errors: 30), baseline, 10);
        Assert.Contains(moreErrors.Overall, m => m.Metric == "error rate" && m.Regressed);

        var renamed = LoadBaselineComparer.Compare(Report(100, 50, perRequest: [("login", 100), ("browse", 100)]), baseline, 10);
        Assert.Equal("NEW", renamed.PerRequest.Single(r => r.Name == "browse").Verdict);
        Assert.Equal("MISSING", renamed.PerRequest.Single(r => r.Name == "search").Verdict);
    }

    [Fact]
    public void Percentages_parse_in_the_common_spellings()
    {
        Assert.Equal(10, LoadBaselineComparer.ParsePercent("10%"));
        Assert.Equal(10, LoadBaselineComparer.ParsePercent("10"));
        Assert.Equal(10, LoadBaselineComparer.ParsePercent("0.1"));
        Assert.Throws<FormatException>(() => LoadBaselineComparer.ParsePercent("ten"));
    }

    [Fact]
    public void Reports_include_the_comparison()
    {
        var baseline = LoadBaseline.From(Report(100, 50, perRequest: [("login", 100)]), "base");
        var report = Report(150, 50, perRequest: [("login", 150)]);
        var comparison = LoadBaselineComparer.Compare(report, baseline);

        var html = LoadReportWriter.Html(report, "Shop", comparison);
        Assert.Contains("Compared with baseline", html);
        Assert.Contains("REGRESSED", html);
        var json = JsonNode.Parse(LoadReportWriter.Json(report, "Shop", comparison))!;
        Assert.Equal("REGRESSED", json["baseline"]!["verdict"]!.GetValue<string>());
        Assert.Contains("p95", comparison.ToText());
    }
}

public class StreamingLoadTests
{
    /// <summary>Plays back a streaming session: connect info, a sent message and two replies 20 ms apart, as a real executor would log.</summary>
    private sealed class StreamSender(bool drop) : IRequestSender
    {
        public Task<ApiResponse> SendAsync(ApiRequest request, ApiEnvironment? environment, CancellationToken cancellationToken) => Respond();
        public Task<ApiResponse> SendAsync(ApiRequest request, SendOptions options, CancellationToken cancellationToken) => Respond();
        public Task<ApiResponse> SendWithScriptOnlyAsync(ApiRequest request, Dispatch.Application.Variables.VariableContext variables, CancellationToken ct) => Respond();

        private async Task<ApiResponse> Respond()
        {
            var start = DateTimeOffset.Now;
            await Task.Delay(30);
            var messages = new List<StreamMessage>
            {
                new(start.AddMilliseconds(5), MessageDirection.Info, "Connected to ws://x"),
                new(start.AddMilliseconds(6), MessageDirection.Sent, "hi"),
                new(start.AddMilliseconds(16), MessageDirection.Received, "echo:hi"),
                new(start.AddMilliseconds(26), MessageDirection.Received, "tick")
            };
            if (drop)
                messages.Add(new StreamMessage(start.AddMilliseconds(27), MessageDirection.Error, "Connection reset"));
            return new ApiResponse { Kind = RequestKind.WebSocket, Succeeded = true, Elapsed = TimeSpan.FromMilliseconds(30), Messages = messages };
        }
    }

    [Fact]
    public async Task Streaming_sessions_are_counted_as_connections_with_message_latency()
    {
        var request = new ApiRequest { Name = "live", Kind = RequestKind.WebSocket, Url = "ws://x" };
        var report = await new LoadTester(new StreamSender(drop: false)).RunAsync(new LoadOptions
        {
            Requests = [request], VirtualUsers = 2, IterationsPerUser = 3
        });

        var s = Assert.IsType<StreamingStats>(report.Streaming);
        Assert.Equal(6, s.Connections);
        Assert.Equal(0, s.ConnectErrors);
        Assert.Equal(0, s.Drops);
        Assert.Equal(6, s.MessagesSent);
        Assert.Equal(12, s.MessagesReceived);
        Assert.True(s.MessagesPerSecond > 0);
        // echo:hi arrived 10 ms after "hi" was sent; tick 10 ms after the previous message.
        Assert.InRange(s.MessageLatency.P50, 9, 11);
        Assert.Equal(0, report.Errors);
        Assert.Equal(6, report.Statuses["connected"]);
        // Connection setup time, not the session length, is the latency sample.
        Assert.InRange(report.Latency.Max, 0, 12);
        Assert.NotNull(report.PerRequest.Single().Streaming);
        Assert.Contains(LoadReportWriter.Insights(report), i => i.Text.StartsWith("Streaming: 6 connection(s)", StringComparison.Ordinal));
        Assert.Contains("streaming", report.ToText());
        var json = JsonNode.Parse(LoadReportWriter.Json(report, "ws"))!;
        Assert.Equal(12, json["streaming"]!["messagesReceived"]!.GetValue<int>());
        Assert.Contains("Streaming", LoadReportWriter.Html(report, "ws"));
    }

    [Fact]
    public async Task Streams_ended_early_by_the_server_are_drops()
    {
        var sender = new RequestSender([new FakeExecutor(_ => new ApiResponse
        {
            Kind = RequestKind.Sse, StatusCode = 200, Succeeded = true, Elapsed = TimeSpan.FromMilliseconds(20),
            Messages = [StreamMessage.Info("Connected; listening for events"), StreamMessage.Received("data: 1"), StreamMessage.Info("Stream ended by the server")]
        })], new NullHistory());
        var request = new ApiRequest { Name = "events", Kind = RequestKind.Sse, Url = "http://x/events" };

        var report = await new LoadTester(sender).RunAsync(new LoadOptions { Requests = [request], VirtualUsers = 1, IterationsPerUser = 2 });

        Assert.Equal(2, report.Streaming!.Drops);
        Assert.Equal(2, report.Errors);
        Assert.Contains("Stream ended by the server", Assert.Single(report.SampleErrors));
    }

    [Fact]
    public async Task Dropped_connections_are_errors()
    {
        var request = new ApiRequest { Name = "live", Kind = RequestKind.WebSocket, Url = "ws://x" };
        var report = await new LoadTester(new StreamSender(drop: true)).RunAsync(new LoadOptions
        {
            Requests = [request], VirtualUsers = 1, IterationsPerUser = 2
        });

        Assert.Equal(2, report.Streaming!.Drops);
        Assert.Equal(2, report.Errors);
        Assert.Equal(2, report.Statuses["dropped"]);
        Assert.Contains("Connection reset", Assert.Single(report.SampleErrors));
    }

    [Fact]
    public async Task Load_tests_a_real_websocket_endpoint_end_to_end()
    {
        await using var server = await TestServer.StartAsync(app => app.Map("/ws", async (HttpContext ctx) =>
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[1024];
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    break;
                await ws.SendAsync(buffer.AsMemory(0, result.Count), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }));
        var request = new ApiRequest
        {
            Name = "echo", Kind = RequestKind.WebSocket, Url = server.BaseUrl + "/ws",
            Protocol = new ProtocolSettings { Stream = new StreamSettings { InitialMessages = [new("m", "ping")], MaxMessages = 1, ListenSeconds = 5 } }
        };
        await using var services = new ServiceCollection().AddDispatchEngine()
            .AddSingleton<Application.Abstractions.IHistoryRepository, NullHistory>().BuildServiceProvider();

        var report = await services.GetRequiredService<LoadTester>().RunAsync(new LoadOptions
        {
            Requests = [request], VirtualUsers = 3, IterationsPerUser = 2, RunChecks = false
        });

        var s = Assert.IsType<StreamingStats>(report.Streaming);
        Assert.Equal(6, s.Connections);
        Assert.Equal(0, s.ConnectErrors);
        Assert.Equal(6, s.MessagesReceived);
        Assert.Equal(0, report.Errors);
    }
}
