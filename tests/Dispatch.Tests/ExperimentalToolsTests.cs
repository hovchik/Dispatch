using Dispatch.Application.Minimize;
using Dispatch.Application.RateLimits;
using Dispatch.Application.Reporting;
using Dispatch.Domain;
using Dispatch.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Tests;

public class MinimizerUnitTests
{
    [Fact]
    public void Builds_units_for_headers_cookies_query_auth_and_json_members()
    {
        var request = new ApiRequest
        {
            Method = HttpVerb.Post, Url = "https://x/orders?trace=1&page=2",
            QueryParams = [new("trace", "1"), new("page", "2")],
            Headers = [new("X-Api-Key", "k"), new("Cookie", "a=1; session=abc"), new("X-Off", "1", enabled: false)],
            Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "t" },
            Body = new RequestBody { Mode = BodyMode.Json, Content = """{"customer":{"email":"a@b.c"},"items":[{"sku":"x"}]}""" }
        };
        var units = RequestMinimizer.BuildUnits(request, deep: true);
        var labels = units.Select(u => u.Label).ToList();

        Assert.Equal(["header X-Api-Key", "header Cookie", "query trace", "query page", "auth (Bearer)", "body"], labels);
        Assert.Equal(["cookie a", "cookie session"], units[1].Children.Select(c => c.Label));
        var body = units[^1];
        Assert.Equal(["body $.customer", "body $.items"], body.Children.Select(c => c.Label));
        Assert.Equal("body $.items[0].sku", body.Children[1].Children[0].Children[0].Label);
    }

    [Fact]
    public void Apply_removes_selected_parts_and_keeps_the_rest()
    {
        var request = new ApiRequest
        {
            Method = HttpVerb.Post, Url = "https://x/orders",
            Headers = [new("X-Api-Key", "k"), new("Cookie", "a=1; session=abc; t=2")],
            Body = new RequestBody { Mode = BodyMode.Json, Content = """{"customer":{"email":"a@b.c","name":"Ann"},"items":[1,2,3]}""" }
        };
        var units = RequestMinimizer.BuildUnits(request, deep: true);
        var all = Flatten(units).ToList();
        var removed = all.Where(u => u.Label is "header X-Api-Key" or "cookie a" or "cookie t" or "body $.customer.name" or "body $.items[1]").ToHashSet();

        var result = RequestMinimizer.Apply(request, removed);

        Assert.False(result.Headers[0].Enabled);
        Assert.Equal("session=abc", result.Headers[1].Value);
        Assert.Equal("""{"customer":{"email":"a@b.c"},"items":[1,3]}""", result.Body.Content);
        Assert.True(request.Headers[0].Enabled); // the original is untouched
    }

    [Fact]
    public void Signature_follows_the_match_rule()
    {
        var response = new ApiResponse { StatusCode = 403, Body = "denied: missing scope", TestResults = [new TestResult("b", false), new TestResult("a", true)] };
        Assert.Equal("403", RequestMinimizer.Signature(response, new MinimizeOptions()));
        Assert.Equal("4xx", RequestMinimizer.Signature(response, new MinimizeOptions { Match = OutcomeMatch.StatusClass }));
        Assert.Equal("403|b", RequestMinimizer.Signature(response, new MinimizeOptions { Match = OutcomeMatch.StatusAndTests }));
        Assert.Equal("contains", RequestMinimizer.Signature(response, new MinimizeOptions { Match = OutcomeMatch.BodyContains, BodyContains = "scope" }));
        Assert.Null(RequestMinimizer.Signature(response, new MinimizeOptions { Match = OutcomeMatch.BodyContains, BodyContains = "nope" }));
    }

    private static IEnumerable<MinimizeUnit> Flatten(IEnumerable<MinimizeUnit> units) =>
        units.SelectMany(u => new[] { u }.Concat(Flatten(u.Children)));
}

public class RequestMinimizerTests
{
    private static ServiceProvider Services() => new ServiceCollection().AddDispatchEngine()
        .AddSingleton<Application.Abstractions.IHistoryRepository, NullHistory>()
        .BuildServiceProvider();

    /// <summary>Creates an order only with an API key, a session cookie, a customer email and at least one item.</summary>
    private static Task<TestServer> OrdersServerAsync() => TestServer.StartAsync(app =>
        app.MapPost("/orders", async (HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-Api-Key"] != "k")
                return Results.StatusCode(401);
            if (ctx.Request.Cookies["session"] != "abc")
                return Results.StatusCode(401);
            var body = await System.Text.Json.Nodes.JsonNode.ParseAsync(ctx.Request.Body);
            if (body?["customer"]?["email"] is null || body["items"] is not System.Text.Json.Nodes.JsonArray { Count: > 0 })
                return Results.BadRequest();
            return Results.Created("/orders/1", new { id = 1 });
        }));

    private static ApiRequest BloatedOrder(string baseUrl) => new()
    {
        Name = "Create order", Method = HttpVerb.Post, Url = $"{baseUrl}/orders?utm_source=app&debug=0&locale=en",
        Headers =
        [
            new("X-Api-Key", "k"), new("X-Request-Id", "123"), new("X-Client-Version", "4.2.1"), new("Accept-Language", "en-GB"),
            new("Cache-Control", "no-cache"), new("Cookie", "theme=dark; session=abc; _ga=GA1.2.3; consent=yes")
        ],
        Body = new RequestBody
        {
            Mode = BodyMode.Json,
            Content = """
                      {"customer":{"email":"ann@example.com","name":"Ann","phone":"555"},
                       "items":[{"sku":"A1","qty":1},{"sku":"B2","qty":3}],
                       "coupon":"SPRING","notes":"leave at door","giftWrap":false}
                      """
        },
        Settings = new RequestSettings { UseCookieJar = false }
    };

    [Fact]
    public async Task Finds_what_a_successful_request_really_needs()
    {
        await using var server = await OrdersServerAsync();
        await using var services = Services();
        var minimizer = new RequestMinimizer(services.GetRequiredService<Application.Requests.IRequestSender>());

        var report = await minimizer.MinimizeAsync(BloatedOrder(server.BaseUrl), new MinimizeOptions());

        Assert.Null(report.Error);
        Assert.Equal("HTTP 201 Created", report.Outcome);
        Assert.True(report.Confirmed);
        var required = MinimizeReportWriter.RequiredLeaves(report).Select(u => u.Label).ToList();
        Assert.Contains("header X-Api-Key", required);
        Assert.Contains("cookie session", required);
        Assert.Contains("body $.customer.email", required);
        Assert.Single(required, l => l.StartsWith("body $.items[", StringComparison.Ordinal));
        Assert.DoesNotContain(required, l => l.Contains("coupon") || l.Contains("X-Request-Id") || l.StartsWith("query"));
        Assert.Equal(4, required.Count);

        Assert.Equal("session=abc", report.Minimal.Headers.Single(h => h.Key == "Cookie").Value);
        Assert.All(report.Minimal.QueryParams, p => Assert.False(p.Enabled));
        Assert.True(report.RequestsSent < 80, $"sent {report.RequestsSent}");
        Assert.Contains("cookie session", MinimizeReportWriter.Text(report));
    }

    [Fact]
    public async Task Isolates_the_combination_that_triggers_a_server_error()
    {
        await using var server = await TestServer.StartAsync(app =>
            app.MapGet("/search", (HttpContext ctx) =>
                ctx.Request.Query["sort"] == "desc" && ctx.Request.Headers.AcceptLanguage.ToString().StartsWith("fr")
                    ? Results.StatusCode(500)
                    : Results.Json(new { results = Array.Empty<int>() })));
        await using var services = Services();
        var minimizer = new RequestMinimizer(services.GetRequiredService<Application.Requests.IRequestSender>());
        var request = new ApiRequest
        {
            Name = "Search", Url = $"{server.BaseUrl}/search?q=shoes&sort=desc&page=2&size=50",
            Headers = [new("Accept-Language", "fr-FR"), new("X-Trace", "1"), new("Accept", "application/json"), new("X-Feature", "beta")]
        };

        var report = await minimizer.MinimizeAsync(request, new MinimizeOptions());

        Assert.Equal("HTTP 500 Internal Server Error", report.Outcome);
        Assert.Equal(["header Accept-Language", "query sort"], MinimizeReportWriter.RequiredLeaves(report).Select(u => u.Label).Order());
        Assert.True(report.Confirmed);
    }

    [Fact]
    public async Task Body_contains_requires_the_text_in_the_original_response()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/x", () => Results.Text("hello")));
        await using var services = Services();
        var minimizer = new RequestMinimizer(services.GetRequiredService<Application.Requests.IRequestSender>());
        var report = await minimizer.MinimizeAsync(new ApiRequest { Url = $"{server.BaseUrl}/x" },
            new MinimizeOptions { Match = OutcomeMatch.BodyContains, BodyContains = "absent" });
        Assert.NotNull(report.Error);
        Assert.Equal(1, report.RequestsSent);
    }
}

public class RateLimitHeaderTests
{
    private static List<ResponseHeader> H(params (string, string)[] headers) => headers.Select(h => new ResponseHeader(h.Item1, h.Item2)).ToList();

    [Fact]
    public void Reads_common_header_styles()
    {
        var x = RateLimitHeaders.Read(H(("X-RateLimit-Limit", "60"), ("X-RateLimit-Remaining", "59"), ("X-RateLimit-Reset", "30")))!;
        Assert.Equal((60, 59, 30.0, "X-RateLimit-*"), (x.Limit, x.Remaining, x.ResetSeconds, x.HeaderStyle));

        var ietf = RateLimitHeaders.Read(H(("RateLimit-Remaining", "4"), ("RateLimit-Reset", "12"), ("RateLimit-Policy", "100;w=60")))!;
        Assert.Equal((100, 4, 12.0), (ietf.Limit, ietf.Remaining, ietf.ResetSeconds));

        var structured = RateLimitHeaders.Read(H(("RateLimit", "\"default\";r=7;t=3"), ("RateLimit-Policy", "\"default\";q=10;w=60")))!;
        Assert.Equal((10, 7, 3.0), (structured.Limit, structured.Remaining, structured.ResetSeconds));

        var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 20;
        var reset = RateLimitHeaders.Read(H(("X-RateLimit-Reset", epoch.ToString())))!;
        Assert.InRange(reset.ResetSeconds!.Value, 15, 21);

        Assert.Null(RateLimitHeaders.Read(H(("Content-Type", "text/plain"))));
    }

    [Fact]
    public void Reads_retry_after_seconds_and_dates()
    {
        Assert.Equal(5, RateLimitHeaders.RetryAfterSeconds(H(("Retry-After", "5"))));
        var date = DateTimeOffset.UtcNow.AddSeconds(30).ToString("R");
        Assert.InRange(RateLimitHeaders.RetryAfterSeconds(H(("Retry-After", date)))!.Value, 25, 31);
        Assert.Null(RateLimitHeaders.RetryAfterSeconds(H()));
    }
}

public class RateLimitProberTests
{
    private static RateLimitProber Prober(ServiceProvider services) => new(services.GetRequiredService<Application.Requests.IRequestSender>());

    private static ServiceProvider Services() => new ServiceCollection().AddDispatchEngine()
        .AddSingleton<Application.Abstractions.IHistoryRepository, NullHistory>()
        .BuildServiceProvider();

    private static RateLimitOptions Fast(int maxRequests = 200) => new()
    {
        MaxRequests = maxRequests, PollInterval = TimeSpan.FromMilliseconds(100), MaxDuration = TimeSpan.FromSeconds(30), Concurrency = 2
    };

    [Fact]
    public async Task Maps_a_fixed_window_limit()
    {
        // 5 requests per 1.5 s window starting at the first request; honest headers.
        var gate = new object();
        DateTimeOffset? windowStart = null;
        var used = 0;
        var window = TimeSpan.FromSeconds(1.5);
        await using var server = await TestServer.StartAsync(app => app.MapGet("/limited", (HttpContext ctx) =>
        {
            lock (gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (windowStart is null || now - windowStart >= window)
                {
                    windowStart = now;
                    used = 0;
                }
                var left = window - (now - windowStart.Value);
                ctx.Response.Headers["X-RateLimit-Limit"] = "5";
                if (used >= 5)
                {
                    ctx.Response.Headers["X-RateLimit-Remaining"] = "0";
                    ctx.Response.Headers.RetryAfter = Math.Ceiling(left.TotalSeconds).ToString();
                    return Results.StatusCode(429);
                }
                used++;
                ctx.Response.Headers["X-RateLimit-Remaining"] = (5 - used).ToString();
                return Results.Json(new { ok = true });
            }
        }));
        await using var services = Services();

        var report = await Prober(services).ProbeAsync(new ApiRequest { Name = "Limited", Url = $"{server.BaseUrl}/limited" }, Fast());

        Assert.Null(report.Error);
        Assert.True(report.Throttled);
        Assert.Equal(429, report.ThrottleStatus);
        Assert.Equal(5, report.BurstCapacity);
        Assert.Equal(RefillKind.FixedWindow, report.Refill);
        Assert.Equal(5, report.Advertised?.Limit);
        Assert.InRange(report.WindowEstimate!.Value.TotalSeconds, 1.0, 2.5);
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Good && i.Text.Contains("matches"));
        Assert.Contains("fixed window", report.Summary);
        Assert.Contains("Limited", RateLimitReportWriter.Html(report));
        Assert.Contains("\"refill\": \"FixedWindow\"", RateLimitReportWriter.Json(report));
    }

    [Fact]
    public async Task Maps_a_token_bucket_and_flags_a_missing_retry_after()
    {
        // Bucket of 5 tokens, refilled at 5 per second; no headers at all.
        var gate = new object();
        double tokens = 5;
        var last = DateTimeOffset.UtcNow;
        await using var server = await TestServer.StartAsync(app => app.MapGet("/bucket", () =>
        {
            lock (gate)
            {
                var now = DateTimeOffset.UtcNow;
                tokens = Math.Min(5, tokens + (now - last).TotalSeconds * 5);
                last = now;
                if (tokens < 1)
                    return Results.StatusCode(429);
                tokens -= 1;
                return Results.Json(new { ok = true });
            }
        }));
        await using var services = Services();

        var report = await Prober(services).ProbeAsync(new ApiRequest { Name = "Bucket", Url = $"{server.BaseUrl}/bucket" }, Fast());

        Assert.True(report.Throttled);
        Assert.InRange(report.BurstCapacity, 5, 7);
        Assert.Equal(RefillKind.Gradual, report.Refill);
        Assert.InRange(report.RefillPerSecond!.Value, 3, 8);
        Assert.Contains(report.Insights, i => i.Text.Contains("no Retry-After"));
        Assert.Contains(report.Insights, i => i.Text.Contains("No rate-limit headers"));
    }

    [Fact]
    public async Task Stops_with_an_error_when_the_request_gets_no_response()
    {
        await using var services = Services();
        var report = await Prober(services).ProbeAsync(new ApiRequest { Name = "Down", Url = "http://127.0.0.1:1/nothing" }, Fast());

        Assert.NotNull(report.Error);
        Assert.Equal(1, report.RequestsSent);
        Assert.Empty(report.Insights);
    }

    [Fact]
    public async Task Reports_no_limit_within_the_budget()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/open", () => Results.Json(new { ok = true })));
        await using var services = Services();

        var report = await Prober(services).ProbeAsync(new ApiRequest { Name = "Open", Url = $"{server.BaseUrl}/open" }, Fast(maxRequests: 30));

        Assert.False(report.Throttled);
        Assert.Equal(RefillKind.NoLimitObserved, report.Refill);
        Assert.Equal(30, report.RequestsSent);
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Warning && i.Text.Contains("No throttling"));
    }
}

[Collection("Console")]
public class CliProbeCommandTests
{
    private static async Task<string> CollectionFileAsync(string dir, params ApiRequest[] requests)
    {
        var path = Path.Combine(dir, "api.dispatch.json");
        await File.WriteAllTextAsync(path, Application.Interop.DispatchFormat.ExportCollection(new RequestCollection { Name = "Api", Requests = [.. requests] }));
        return path;
    }

    [Fact]
    public async Task Minimize_command_prints_required_parts_and_writes_a_json_report()
    {
        await using var server = await TestServer.StartAsync(app =>
            app.MapGet("/secure", (HttpContext ctx) => ctx.Request.Headers["X-Token"] == "t" ? Results.Ok() : Results.StatusCode(403)));
        var dir = Cli.TempDir();
        var source = await CollectionFileAsync(dir,
            new ApiRequest { Name = "Secure", Url = $"{server.BaseUrl}/secure?x=1", Headers = [new("X-Token", "t"), new("X-Noise", "1")] },
            new ApiRequest { Name = "Other", Url = $"{server.BaseUrl}/other" });

        var (ambiguous, message) = await Cli.RunAsync("minimize", source, "--db", Path.Combine(dir, "db"));
        Assert.Equal(2, ambiguous);
        Assert.Contains("--request", message);

        var (exit, output) = await Cli.RunAsync("minimize", source, "--request", "Secure", "--db", Path.Combine(dir, "db"),
            "--no-color", "-r", "cli,json", "-o", dir);
        Assert.Equal(0, exit);
        Assert.Contains("● header X-Token", output);
        Assert.Contains("○ header X-Noise", output);
        var json = Directory.GetFiles(dir, "secure-minimize-*.json").Single();
        Assert.Contains("\"confirmed\": true", await File.ReadAllTextAsync(json));
    }

    [Fact]
    public async Task Ratelimit_command_fails_when_a_limit_is_expected_but_missing()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/open", () => Results.Ok()));
        var dir = Cli.TempDir();
        var source = await CollectionFileAsync(dir, new ApiRequest { Name = "Open", Url = $"{server.BaseUrl}/open" });

        var (exit, output) = await Cli.RunAsync("ratelimit", source, "--db", Path.Combine(dir, "db"), "--max-requests", "20", "--expect-limit");

        Assert.Equal(1, exit);
        Assert.Contains("No limit hit after", output);
    }
}
