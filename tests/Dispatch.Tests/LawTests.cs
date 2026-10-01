using System.Text.Json.Nodes;
using Dispatch.Application.Laws;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Tests;

internal static class LawData
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    public static string Order(int i, string status = "paid", bool datesInOrder = true)
    {
        var lines = Enumerable.Range(1, 1 + i % 3).Select(n => new JsonObject { ["sku"] = $"S{n}", ["price"] = 5 * n + i, ["qty"] = 1 }).ToList();
        var total = lines.Sum(l => l["price"]!.GetValue<int>());
        var created = Start.AddDays(-i);
        var updated = datesInOrder ? created.AddHours(i + 1) : created.AddHours(-1);
        return new JsonObject
        {
            ["id"] = $"0000000{i % 10}-aaaa-bbbb-cccc-00000000000{i % 10}", ["status"] = status, ["total"] = total,
            ["createdAt"] = created.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["updatedAt"] = updated.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray())
        }.ToJsonString();
    }

    /// <summary>GET /orders?limit=N: a page of orders with a count, observed 12 times.</summary>
    public static List<Observation> OrderPages(Func<int, string>? statusOf = null)
    {
        var list = new List<Observation>();
        for (var page = 0; page < 12; page++)
        {
            var limit = 2 + page % 3;
            var size = page % 4 == 0 ? limit : Math.Max(1, limit - 1);
            var orders = Enumerable.Range(page * 5, size).Select(i => JsonNode.Parse(Order(i, statusOf?.Invoke(i) ?? (i % 2 == 0 ? "paid" : "shipped")))!).ToArray();
            var body = new JsonObject { ["items"] = new JsonArray(orders), ["count"] = size, ["totalCount"] = 100 }.ToJsonString();
            list.Add(new Observation(Start.AddMinutes(page), "GET", $"https://shop.test/api/orders?limit={limit}", "", 200, body));
        }
        return list;
    }

    /// <summary>Users: create, read back, re-read (stable apart from fetchedAt), delete, read (404).</summary>
    public static List<Observation> UserLifecycle(bool lowercaseOnRead = false)
    {
        var list = new List<Observation>();
        var t = Start;
        DateTimeOffset Next() => t = t.AddSeconds(1);
        for (var i = 1; i <= 3; i++)
        {
            var id = $"u{i}00";
            var sent = $$"""{"name":"User {{i}}","email":"user{{i}}@test.dev"}""";
            list.Add(new Observation(Next(), "POST", "https://shop.test/api/users", sent, 201,
                $$"""{"id":"{{id}}","name":"User {{i}}","email":"user{{i}}@test.dev"}"""));
            var name = lowercaseOnRead && i == 2 ? $"user {i}" : $"User {i}";
            for (var read = 0; read < 2; read++)
                list.Add(new Observation(Next(), "GET", $"https://shop.test/api/users/{id}", "", 200,
                    $$"""{"id":"{{id}}","name":"{{name}}","email":"user{{i}}@test.dev","fetchedAt":"{{t:O}}"}"""));
        }
        list.Add(new Observation(Next(), "DELETE", "https://shop.test/api/users/u100", "", 204, ""));
        list.Add(new Observation(Next(), "GET", "https://shop.test/api/users/u100", "", 404, """{"error":"not found"}"""));
        return list;
    }
}

public class ObservationTests
{
    [Theory]
    [InlineData("https://x.test/api/users/42?x=1", "/api/users/{id}")]
    [InlineData("https://x.test/api/orders/3f2b8c1e-1d2a-4b5c-9e8f-0a1b2c3d4e5f/lines", "/api/orders/{id}/lines")]
    [InlineData("{{base}}/users/{{userId}}", "/users/{id}")]
    [InlineData("localhost:8080/health", "/health")]
    [InlineData("https://x.test/", "/")]
    [InlineData("https://x.test/v2/users/u100", "/v2/users/{id}")]
    [InlineData("https://x.test/oauth/token", "/oauth/token")]
    public void Path_templates_hide_ids(string url, string template) => Assert.Equal(template, Observations.PathTemplate(url));

    [Fact]
    public void Learns_slug_parameters_from_the_traffic()
    {
        var at = DateTimeOffset.UnixEpoch;
        Observation Get(string path) => new(at, "GET", "https://x.test" + path, "", 200, "{}");
        var learned = Observations.LearnTemplates([Get("/users/alice"), Get("/users/bob"), Get("/users/carol"), Get("/users/me/settings"), Get("/health")]);
        Assert.Equal(["GET /users/{id}", "GET /users/{id}", "GET /users/{id}", "GET /users/me/settings", "GET /health"], learned.Select(o => o.Endpoint));
    }

    [Fact]
    public void Reads_har_entries_in_time_order()
    {
        var har = JsonNode.Parse("""
            {"log":{"entries":[
              {"startedDateTime":"2026-09-01T10:00:02Z","request":{"method":"GET","url":"https://a.test/x"},"response":{"status":200,"content":{"text":"{}"}}},
              {"startedDateTime":"2026-09-01T10:00:01Z","request":{"method":"POST","url":"https://a.test/x","postData":{"text":"{\"a\":1}"}},"response":{"status":201,"content":{"text":"{\"id\":1}"}}}
            ]}}
            """)!;
        var observations = Observations.FromHar(har);
        Assert.Equal(["POST /x", "GET /x"], observations.Select(o => o.Endpoint));
        Assert.Equal("{\"a\":1}", observations[0].RequestBody);
    }

    [Fact]
    public void History_snapshots_keep_the_resolved_url_and_body()
    {
        var snapshot = ResponseSnapshot.From(new ApiResponse
        {
            StatusCode = 201, EffectiveUrl = "https://a.test/users", RawRequest = "POST /users HTTP/1.1\r\nHost: a.test\r\n\r\n{\"name\":\"Ann\"}"
        });
        Assert.Equal("https://a.test/users", snapshot.EffectiveUrl);
        Assert.Equal("{\"name\":\"Ann\"}", snapshot.RequestBody);
    }
}

public class LawMinerTests
{
    [Fact]
    public void Infers_shape_enums_formats_numbers_and_relations()
    {
        var report = LawMiner.Mine(LawData.OrderPages(), source: "test");
        var laws = report.Laws.Where(l => l.Endpoint == "GET /api/orders").Select(l => l.Description).ToList();

        Assert.Empty(report.Anomalies);
        Assert.Contains(laws, d => d.StartsWith("$.items[*] always has id (string), status (string), total (number)"));
        Assert.Contains("$.items[*].status is one of \"paid\", \"shipped\"", laws);
        Assert.Contains("$.items[*].id is always a UUID", laws);
        Assert.Contains("$.items[*].createdAt is always a date-time", laws);
        Assert.Contains("$.items[*].total is never negative", laws);
        Assert.Contains("$.items[*].createdAt is never after $.items[*].updatedAt", laws);
        Assert.Contains("$.count always equals the number of $.items", laws);
        Assert.Contains("$.totalCount is never less than the number of $.items returned", laws);
        Assert.Contains("$.items[*].total always equals the sum of $.items[*].lines[*].price", laws);
        Assert.Contains("$.items never has more items than the limit query parameter", laws);
        // Not laws: quantities are always 1 but that's a constant, not an enum of strings; skus vary per line.
        Assert.DoesNotContain(laws, d => d.Contains("updatedAt is never after"));
    }

    [Fact]
    public void Reports_near_misses_as_anomalies()
    {
        var report = LawMiner.Mine(LawData.OrderPages(i => i == 11 ? "shiped" : i % 2 == 0 ? "paid" : "shipped"));
        var anomaly = Assert.Single(report.Anomalies, a => a.Kind == LawKind.Enum);
        Assert.Contains("\"shiped\" (a likely typo)", anomaly.Description);
        Assert.Contains("shiped", anomaly.Counterexamples.Single());
    }

    [Fact]
    public void Infers_echo_round_trip_delete_and_stable_get_laws()
    {
        var report = LawMiner.Mine(LawData.UserLifecycle(), new LawOptions { MinSamples = 2 });

        Assert.Contains(report.Laws, l => l is { Kind: LawKind.Echo, Endpoint: "POST /api/users" } && l.Description.Contains("name, email"));
        var roundTrip = Assert.Single(report.Laws, l => l.Kind == LawKind.RoundTrip);
        Assert.Equal("Resources created by POST /api/users can be read back at GET /api/users/{id} with the submitted values", roundTrip.Description);
        Assert.Equal(3, roundTrip.Support);
        var delete = Assert.Single(report.Laws, l => l.Kind == LawKind.DeleteGone);
        Assert.False(delete.IsAnomaly);
        var stable = Assert.Single(report.Laws, l => l.Kind == LawKind.StableGet);
        Assert.Contains("apart from $.fetchedAt", stable.Description);
        Assert.Contains("ignore $.fetchedAt", stable.Advice);
    }

    [Fact]
    public void Round_trip_mismatches_are_counterexamples()
    {
        var report = LawMiner.Mine(LawData.UserLifecycle(lowercaseOnRead: true), new LawOptions { MinSamples = 2 });
        var roundTrip = Assert.Single(report.Laws, l => l.Kind == LawKind.RoundTrip);
        Assert.True(roundTrip.IsAnomaly);
        Assert.Contains("name: sent \"User 2\", read \"user 2\"", roundTrip.Counterexamples.Single());
    }

    [Fact]
    public void Too_few_samples_give_no_laws()
    {
        var report = LawMiner.Mine(LawData.OrderPages().Take(2).ToList());
        Assert.DoesNotContain(report.Laws, l => l.Kind is LawKind.Shape or LawKind.Enum);
        Assert.Contains("observation", report.Summary);
    }
}

public class LawApplyTests
{
    [Fact]
    public void Finds_the_saved_request_by_method_and_path_suffix()
    {
        var list = new ApiRequest { Name = "List", Url = "{{base}}/orders?limit=5" };
        var one = new ApiRequest { Name = "One", Url = "{{base}}/orders/{{id}}" };
        var create = new ApiRequest { Name = "Create", Method = HttpVerb.Post, Url = "{{base}}/orders" };
        ApiRequest[] all = [list, one, create];

        Assert.Same(list, LawTests.FindRequest("GET /api/orders", all));
        Assert.Same(one, LawTests.FindRequest("GET /api/orders/{id}", all));
        Assert.Same(create, LawTests.FindRequest("POST /api/orders", all));
        Assert.Null(LawTests.FindRequest("DELETE /api/orders/{id}", all));
    }

    [Fact]
    public void Applying_twice_adds_the_test_once()
    {
        var report = LawMiner.Mine(LawData.OrderPages());
        var law = report.Laws.First(l => l.Script is not null);
        var request = new ApiRequest { TestScript = "// mine" };
        Assert.True(LawTests.Apply(law, request));
        Assert.False(LawTests.Apply(law, request));
        Assert.StartsWith("// mine\n\n// ", request.TestScript);
        Assert.True(LawTests.IsApplied(law, request));
    }
}

/// <summary>The generated pm.test snippets run in the real script engine: they pass on good data and fail on bad.</summary>
public class LawScriptExecutionTests
{
    [Fact]
    public async Task Generated_tests_pass_on_conforming_responses_and_fail_on_violations()
    {
        var pages = LawData.OrderPages();
        var report = LawMiner.Mine(pages);
        var good = pages[0].ResponseBody;
        var bad = JsonNode.Parse(good)!;
        bad["items"]![0]!["status"] = "lost";
        bad["items"]![0]!["total"] = -1;
        bad["count"] = 99;
        var body = good;
        await using var server = await TestServer.StartAsync(app => app.MapGet("/api/orders", () => Results.Text(body, "application/json")));
        await using var services = new ServiceCollection().AddDispatchEngine()
            .AddSingleton<Application.Abstractions.IHistoryRepository, NullHistory>().BuildServiceProvider();
        var sender = services.GetRequiredService<IRequestSender>();

        var request = new ApiRequest { Name = "Orders", Url = $"{server.BaseUrl}/api/orders?limit=2" };
        foreach (var law in report.Laws.Where(l => l.Endpoint == "GET /api/orders"))
            LawTests.Apply(law, request);
        Assert.True(request.TestScript.Length > 0);

        var passing = await sender.SendAsync(request, new SendOptions { RecordHistory = false }, CancellationToken.None);
        Assert.NotEmpty(passing.TestResults);
        Assert.All(passing.TestResults, t => Assert.True(t.Passed, $"{t.Name}: {t.Message}"));

        body = bad.ToJsonString();
        var failing = await sender.SendAsync(request, new SendOptions { RecordHistory = false }, CancellationToken.None);
        var failed = failing.TestResults.Where(t => !t.Passed).Select(t => t.Name).ToList();
        Assert.Contains(failed, n => n.Contains("status is one of"));
        Assert.Contains(failed, n => n.Contains("total is not negative"));
        Assert.Contains(failed, n => n.Contains("count == items.length"));
    }
}

[Collection("Console")]
public class CliLawsTests
{
    private static string Har(IEnumerable<Observation> observations) => new JsonObject
    {
        ["log"] = new JsonObject
        {
            ["entries"] = new JsonArray(observations.Select(o => (JsonNode)new JsonObject
            {
                ["startedDateTime"] = o.At.ToString("O"),
                ["request"] = new JsonObject { ["method"] = o.Method, ["url"] = o.Url },
                ["response"] = new JsonObject { ["status"] = o.Status, ["content"] = new JsonObject { ["text"] = o.ResponseBody } }
            }).ToArray())
        }
    }.ToJsonString();

    [Fact]
    public async Task Laws_from_a_har_file_fail_on_anomalies()
    {
        var dir = Cli.TempDir();
        var file = Path.Combine(dir, "traffic.har");
        await File.WriteAllTextAsync(file, Har(LawData.OrderPages(i => i == 11 ? "shiped" : i % 2 == 0 ? "paid" : "shipped")));

        var (exit, output) = await Cli.RunAsync("laws", file, "--db", Path.Combine(dir, "db"), "--no-color", "--fail-on-anomaly");

        Assert.Equal(1, exit);
        Assert.Contains("Anomalies (1)", output);
        Assert.Contains("\"shiped\" (a likely typo)", output);
        Assert.Contains("GET /api/orders", output);
    }

    [Fact]
    public async Task Laws_from_collection_runs_are_written_as_tests_that_then_pass()
    {
        var calls = 0;
        await using var server = await TestServer.StartAsync(app => app.MapGet("/api/items", (HttpContext ctx) =>
        {
            var n = Interlocked.Increment(ref calls);
            var limit = int.Parse(ctx.Request.Query["limit"].ToString());
            var items = Enumerable.Range(0, Math.Min(limit, 1 + n % 3))
                .Select(i => new { id = Guid.NewGuid(), state = i % 2 == 0 ? "open" : "closed", price = 3 + i + n });
            return Results.Json(new { items, count = items.Count() });
        }));
        var dir = Cli.TempDir();
        var file = Path.Combine(dir, "items.dispatch.json");
        await File.WriteAllTextAsync(file, Application.Interop.DispatchFormat.ExportCollection(new RequestCollection
        {
            Name = "Items", Requests = [new ApiRequest { Name = "List items", Url = $"{server.BaseUrl}/api/items?limit=3" }]
        }));
        var db = Path.Combine(dir, "db");

        var (exit, output) = await Cli.RunAsync("laws", file, "--db", db, "--runs", "5", "--write", "--no-color");
        Assert.Equal(0, exit);
        Assert.Contains("$.count always equals the number of $.items", output);
        Assert.Contains("Added tests to 1 request(s)", output);
        var saved = await File.ReadAllTextAsync(file);
        Assert.Contains("API law:", saved);

        var (runExit, runOutput) = await Cli.RunAsync("run", file, "--db", db, "--no-color");
        Assert.True(runExit == 0, runOutput);
        Assert.Contains("API law:", runOutput);
    }
}
