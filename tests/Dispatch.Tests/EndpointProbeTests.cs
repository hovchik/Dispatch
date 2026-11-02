using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Dispatch.Application.Probes;
using Dispatch.Application.Reporting;
using Dispatch.Domain;
using Dispatch.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Tests;

public class EndpointProbeUnitTests
{
    [Fact]
    public void Reads_the_next_link_from_a_Link_header()
    {
        var response = new ApiResponse
        {
            Headers = [new ResponseHeader("Link", "<https://api.x/items?page=1>; rel=\"prev\", <https://api.x/items?page=3>; rel=\"next\"")]
        };
        Assert.Equal("https://api.x/items?page=3", PaginationWalker.NextLink(response));
        Assert.Null(PaginationWalker.NextLink(new ApiResponse()));
    }

    [Fact]
    public void Finds_the_items_of_a_page()
    {
        Assert.Equal("$", PaginationWalker.FindItems(JsonNode.Parse("[1,2]"), null)!.Value.Path);
        Assert.Equal("$.data", PaginationWalker.FindItems(JsonNode.Parse("""{"data":[1],"total":1}"""), null)!.Value.Path);
        Assert.Equal("$._embedded.orders", PaginationWalker.FindItems(JsonNode.Parse("""{"_embedded":{"orders":[{"id":1}]}}"""), null)!.Value.Path);
        Assert.Equal("$.things", PaginationWalker.FindItems(JsonNode.Parse("""{"meta":{},"things":[1,2,3]}"""), null)!.Value.Path);
        Assert.Null(PaginationWalker.FindItems(JsonNode.Parse("""{"a":1}"""), null));
    }

    [Fact]
    public void Parses_cache_control_and_validates_etags()
    {
        var cc = CacheControl.Parse("public, max-age=60, s-maxage=\"120\"");
        Assert.True(cc.Has("public"));
        Assert.Equal(60, cc.Seconds("max-age"));
        Assert.Equal(120, cc.Seconds("s-maxage"));
        Assert.True(cc.SharedCacheable);
        Assert.False(CacheControl.Parse("private, max-age=60").SharedCacheable);
        Assert.True(CacheChecker.IsValidETag("\"abc\""));
        Assert.True(CacheChecker.IsValidETag("W/\"abc\""));
        Assert.False(CacheChecker.IsValidETag("abc"));
    }

    [Fact]
    public void Changes_the_body_for_key_reuse()
    {
        var request = new ApiRequest { Method = HttpVerb.Post, Body = new RequestBody { Mode = BodyMode.Json, Content = """{"sku":"a","qty":1}""" } };
        var changed = IdempotencyProber.WithDifferentBody(request);
        Assert.Equal("""{"sku":"a","qty":2}""", changed.Body.Content);
        Assert.Equal("""{"sku":"a","qty":1}""", request.Body.Content);
    }

    [Fact]
    public void Canonical_bodies_ignore_volatile_members()
    {
        Assert.Equal(ProbeHelpers.Canonical("""{"id":1,"createdAt":"x","name":"a"}"""), ProbeHelpers.Canonical("""{"name":"a","id":1,"createdAt":"y"}"""));
        Assert.NotEqual(ProbeHelpers.Canonical("""{"id":1}"""), ProbeHelpers.Canonical("""{"id":2}"""));
    }

    [Fact]
    public void Volatile_names_match_whole_words()
    {
        Assert.True(ProbeHelpers.IsVolatile("createdAt"));
        Assert.True(ProbeHelpers.IsVolatile("updated_at"));
        Assert.True(ProbeHelpers.IsVolatile("timestamp"));
        Assert.True(ProbeHelpers.IsVolatile("requestId"));
        Assert.True(ProbeHelpers.IsVolatile("X-Trace-Id"));
        Assert.False(ProbeHelpers.IsVolatile("candidateId"));
        Assert.False(ProbeHelpers.IsVolatile("format"));
        Assert.False(ProbeHelpers.IsVolatile("id"));
        Assert.NotEqual(ProbeHelpers.Canonical("""{"candidateId":1}"""), ProbeHelpers.Canonical("""{"candidateId":2}"""));
    }

    [Fact]
    public void Report_fails_by_threshold_and_renders()
    {
        var report = new ProbeReport { Kind = ProbeKind.Caching, RequestName = "Get", Method = "GET", Url = "http://x", Summary = "s" };
        report.Insights.Add(new ReportInsight(InsightLevel.Warning, "w"));
        Assert.False(report.Fails(ProbeFailOn.Bad));
        Assert.True(report.Fails(ProbeFailOn.Warning));
        report.Steps.Add(new ProbeStep(1, "GET", "GET", "http://x", 200, false, 3));
        Assert.Contains("<h1>Caching", ProbeReportWriter.Html(report));
        Assert.Equal("caching", JsonNode.Parse(ProbeReportWriter.Json(report))!["tool"]!.GetValue<string>());
        Assert.Contains("! w", ProbeReportWriter.Text(report));
    }
}

public class EndpointProbeTests
{
    private static ServiceProvider Services() => new ServiceCollection().AddDispatchEngine()
        .AddSingleton<Application.Abstractions.IHistoryRepository, NullHistory>()
        .BuildServiceProvider();

    private static ApiRequest Get(string url) => new() { Name = "List", Method = HttpVerb.Get, Url = url };

    private static IEnumerable<object> Items(int count) => Enumerable.Range(1, count).Select(i => new { id = i, name = $"item {i}" });

    private static IResult PageOf(HttpContext ctx, IReadOnlyList<object> all, bool caps = true)
    {
        var q = ctx.Request.Query;
        if (!int.TryParse(q["page"].FirstOrDefault() ?? "1", out var page) || page < 1)
            return Results.BadRequest(new { error = "bad page" });
        if (!int.TryParse(q["limit"].FirstOrDefault() ?? "10", out var limit) || limit < 1)
            return Results.BadRequest(new { error = "bad limit" });
        if (caps)
            limit = Math.Min(limit, 100);
        return Results.Json(new { data = all.Skip((page - 1) * limit).Take(limit), total = all.Count });
    }

    [Fact]
    public async Task Walks_page_numbers_and_finds_nothing_wrong_with_a_correct_endpoint()
    {
        var all = Items(23).ToList();
        await using var server = await TestServer.StartAsync(app => app.MapGet("/items", (HttpContext ctx) => PageOf(ctx, all)));
        await using var services = Services();
        var report = await services.GetRequiredService<PaginationWalker>()
            .WalkAsync(Get(server.BaseUrl + "/items?page=1&limit=5"), new PaginationOptions());

        Assert.Null(report.Error);
        Assert.Equal(0, report.Problems);
        Assert.Equal(23, report.Details["items"]!.GetValue<int>());
        Assert.Equal(5, report.Details["pages"]!.GetValue<int>());
        Assert.Equal("Page", report.Details["strategy"]!.GetValue<string>());
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Good && i.Text.Contains("advertised total (23) matches"));
        Assert.Contains(report.Insights, i => i.Text.Contains("page=11 (past the end) returns an empty page"));
        Assert.Contains(report.Insights, i => i.Text.Contains("limit=100000 is capped"));
    }

    [Fact]
    public async Task Flags_duplicates_and_a_total_that_does_not_add_up()
    {
        // An unstable sort makes page 2 start one item early (item 5 repeats), and the total counts 2 soft-deleted rows that no page returns.
        var all = Items(20).ToList();
        await using var server = await TestServer.StartAsync(app => app.MapGet("/items", (HttpContext ctx) =>
        {
            var page = int.Parse(ctx.Request.Query["page"].FirstOrDefault() ?? "1");
            var limit = int.Parse(ctx.Request.Query["limit"].FirstOrDefault() ?? "5");
            var skip = Math.Max(0, (page - 1) * limit - (page > 1 ? 1 : 0));
            return Results.Json(new { items = all.Skip(skip).Take(limit), total_count = all.Count + 2 });
        }));
        await using var services = Services();
        var report = await services.GetRequiredService<PaginationWalker>()
            .WalkAsync(Get(server.BaseUrl + "/items?page=1&limit=5"), new PaginationOptions { EdgeCases = false });

        Assert.True(report.Problems >= 2, ProbeReportWriter.Text(report));
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("appear on more than one page"));
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("advertised total (22") && i.Text.Contains("2 item(s) can't be reached"));
        Assert.True(report.Fails(ProbeFailOn.Bad));
    }

    [Fact]
    public async Task Follows_Link_headers_and_body_cursors()
    {
        var all = Items(7).ToList();
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/feed", (HttpContext ctx) =>
            {
                var after = int.Parse(ctx.Request.Query["after"].FirstOrDefault() ?? "0");
                var page = all.Skip(after).Take(3).ToList();
                if (after + 3 < all.Count)
                    ctx.Response.Headers.Link = $"<{ctx.Request.Scheme}://{ctx.Request.Host}/feed?after={after + 3}>; rel=\"next\"";
                return Results.Json(page);
            });
            app.MapGet("/events", (HttpContext ctx) =>
            {
                var cursor = ctx.Request.Query["cursor"].FirstOrDefault();
                var start = cursor is null ? 0 : int.Parse(cursor.Replace("c", ""));
                var next = start + 3 < all.Count ? $"c{start + 3}" : null;
                return Results.Json(new { results = all.Skip(start).Take(3), next_cursor = next });
            });
        });
        await using var services = Services();
        var walker = services.GetRequiredService<PaginationWalker>();

        var link = await walker.WalkAsync(Get(server.BaseUrl + "/feed"), new PaginationOptions());
        Assert.Equal("LinkHeader", link.Details["strategy"]!.GetValue<string>());
        Assert.Equal(7, link.Details["items"]!.GetValue<int>());
        Assert.Equal(0, link.Problems);

        var cursor = await walker.WalkAsync(Get(server.BaseUrl + "/events"), new PaginationOptions());
        Assert.Equal("Cursor", cursor.Details["strategy"]!.GetValue<string>());
        Assert.Equal("cursor", cursor.Details["cursorParam"]!.GetValue<string>());
        Assert.Equal(7, cursor.Details["items"]!.GetValue<int>());
        Assert.Equal(0, cursor.Problems);
    }

    [Fact]
    public async Task Detects_a_loop_and_reports_an_unpaginated_endpoint_without_problems()
    {
        var all = Items(4).ToList();
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/loop", (HttpContext ctx) =>
            {
                ctx.Response.Headers.Link = $"<{ctx.Request.Scheme}://{ctx.Request.Host}/loop?page=2>; rel=\"next\"";
                var page = ctx.Request.Query["page"].FirstOrDefault() == "2" ? all.Skip(2) : all.Take(2);
                return Results.Json(page);
            });
            app.MapGet("/all", () => Results.Json(all));
        });
        await using var services = Services();
        var walker = services.GetRequiredService<PaginationWalker>();

        var loop = await walker.WalkAsync(Get(server.BaseUrl + "/loop"), new PaginationOptions());
        Assert.Contains(loop.Insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("loops"));

        var flat = await walker.WalkAsync(Get(server.BaseUrl + "/all"), new PaginationOptions());
        Assert.Equal(0, flat.Problems);
        Assert.Contains(flat.Insights, i => i.Text.Contains("No pagination found"));
    }

    [Fact]
    public async Task Walks_past_a_server_capped_page_size_and_counts_from_a_later_start_as_partial()
    {
        var all = Items(23).ToList();
        // The server caps limit at 10, whatever is asked for.
        await using var server = await TestServer.StartAsync(app => app.MapGet("/items", (HttpContext ctx) =>
        {
            var page = int.Parse(ctx.Request.Query["page"].FirstOrDefault() ?? "1");
            return Results.Json(new { data = all.Skip((page - 1) * 10).Take(10), total = all.Count });
        }));
        await using var services = Services();
        var walker = services.GetRequiredService<PaginationWalker>();

        var capped = await walker.WalkAsync(Get(server.BaseUrl + "/items?page=1&limit=100"), new PaginationOptions { EdgeCases = false });
        Assert.Equal(23, capped.Details["items"]!.GetValue<int>());
        Assert.Equal(0, capped.Problems);

        var later = await walker.WalkAsync(Get(server.BaseUrl + "/items?page=2"), new PaginationOptions { EdgeCases = false });
        Assert.Equal(0, later.Problems);
        Assert.Contains(later.Insights, i => i.Text.Contains("not the first page"));
    }

    [Fact]
    public async Task Does_not_follow_next_links_to_another_origin()
    {
        await using var other = await TestServer.StartAsync(app => app.MapGet("/steal", (HttpContext ctx) =>
            Results.Json(new { got = ctx.Request.Headers.Authorization.ToString() })));
        var hits = 0;
        await using var server = await TestServer.StartAsync(app => app.MapGet("/items", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref hits);
            ctx.Response.Headers.Link = $"<{other.BaseUrl}/steal>; rel=\"next\"";
            return Results.Json(new[] { new { id = 1 }, new { id = 2 } });
        }));
        await using var services = Services();
        var request = Get(server.BaseUrl + "/items");
        request.Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "secret" };
        var report = await services.GetRequiredService<PaginationWalker>().WalkAsync(request, new PaginationOptions { CheckStability = false });

        Assert.Equal(1, report.RequestsSent);
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Warning && i.Text.Contains("another origin"));
    }

    private static ApiRequest Post(string url) => new()
    {
        Name = "Create order", Method = HttpVerb.Post, Url = url,
        Body = new RequestBody { Mode = BodyMode.Json, Content = """{"sku":"abc","qty":1}""" }
    };

    [Fact]
    public async Task Idempotency_passes_an_endpoint_that_honours_keys()
    {
        var next = 0;
        var byKey = new ConcurrentDictionary<string, Lazy<(string Body, int Id)>>();
        await using var server = await TestServer.StartAsync(app => app.MapPost("/orders", async (HttpContext ctx) =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            var key = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (key is null)
                return Results.Json(new { id = Interlocked.Increment(ref next) }, statusCode: 201);
            var entry = byKey.GetOrAdd(key, _ => new Lazy<(string, int)>(() => (body, Interlocked.Increment(ref next))));
            var (firstBody, id) = entry.Value;
            if (firstBody != body)
                return Results.Json(new { error = "key reused with a different body" }, statusCode: 422);
            return Results.Json(new { id }, statusCode: 201);
        }));
        await using var services = Services();
        var report = await services.GetRequiredService<IdempotencyProber>().ProbeAsync(Post(server.BaseUrl + "/orders"), new IdempotencyOptions());

        Assert.Null(report.Error);
        Assert.Equal(0, report.Problems);
        Assert.Equal("same response", report.Details["replay"]!.GetValue<string>());
        Assert.Equal("rejected (422)", report.Details["keyReuse"]!.GetValue<string>());
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Good && i.Text.Contains("simultaneous requests with one key were handled safely"));
    }

    [Fact]
    public async Task Idempotency_flags_an_endpoint_that_ignores_keys()
    {
        var next = 0;
        await using var server = await TestServer.StartAsync(app => app.MapPost("/orders",
            () => Results.Json(new { id = Interlocked.Increment(ref next) }, statusCode: 201)));
        await using var services = Services();
        var report = await services.GetRequiredService<IdempotencyProber>()
            .ProbeAsync(Post(server.BaseUrl + "/orders"), new IdempotencyOptions { Parallel = 4, RaceWithoutKey = false });

        Assert.Equal("duplicate", report.Details["replay"]!.GetValue<string>());
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("created a second resource"));
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("created 4 different resources"));
        Assert.True(report.Fails(ProbeFailOn.Bad));
    }

    [Fact]
    public async Task Idempotency_marks_a_run_cut_short_by_the_request_cap_as_incomplete()
    {
        await using var server = await TestServer.StartAsync(app => app.MapPost("/orders", () => Results.Json(new { id = 7 }, statusCode: 201)));
        await using var services = Services();
        var report = await services.GetRequiredService<IdempotencyProber>()
            .ProbeAsync(Post(server.BaseUrl + "/orders"), new IdempotencyOptions { MaxRequests = 2 });

        Assert.True(report.Stopped);
        Assert.StartsWith("Incomplete", report.Summary);
        Assert.DoesNotContain(report.Insights, i => i.Text.Contains("handled safely"));
    }

    [Fact]
    public async Task Caching_flags_a_stable_etag_that_never_gets_a_304()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/doc", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.ETag = "\"v1\"";
            return Results.Json(new { title = "Doc" });
        }));
        await using var services = Services();
        var report = await services.GetRequiredService<CacheChecker>().CheckAsync(Get(server.BaseUrl + "/doc"), new CacheOptions { CheckHead = false });
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("conditional requests are not supported"));
    }

    [Fact]
    public async Task Caching_refuses_unsafe_methods_and_allows_max_age_for_Authorization()
    {
        var posts = 0;
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapPost("/orders", () => { Interlocked.Increment(ref posts); return Results.Ok(); });
            app.MapGet("/me", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "max-age=60";
                return Results.Json(new { user = "ann" });
            });
        });
        await using var services = Services();
        var checker = services.GetRequiredService<CacheChecker>();

        var post = await checker.CheckAsync(Post(server.BaseUrl + "/orders"), new CacheOptions());
        Assert.NotNull(post.Error);
        Assert.Equal(0, posts);

        var request = Get(server.BaseUrl + "/me");
        request.Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "secret" };
        var me = await checker.CheckAsync(request, new CacheOptions { CheckHead = false });
        Assert.DoesNotContain(me.Insights, i => i.Level == InsightLevel.Bad);

        var cookie = Get(server.BaseUrl + "/me");
        cookie.Headers.Add(new KeyValueItem("Cookie", "session=abc"));
        var withCookie = await checker.CheckAsync(cookie, new CacheOptions { CheckHead = false });
        Assert.Contains(withCookie.Insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("cookies or an API key"));
    }

    [Fact]
    public async Task Caching_passes_correct_revalidation()
    {
        await using var server = await TestServer.StartAsync(app => app.MapMethods("/product", ["GET", "HEAD"], (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            ctx.Response.Headers.ETag = "\"v1\"";
            if (ctx.Request.Headers.IfNoneMatch.ToString() == "\"v1\"")
                return Results.StatusCode(304);
            return Results.Json(new { id = 1, name = "Lamp" });
        }));
        await using var services = Services();
        var report = await services.GetRequiredService<CacheChecker>().CheckAsync(Get(server.BaseUrl + "/product"), new CacheOptions());

        Assert.Null(report.Error);
        Assert.Equal(0, report.Problems);
        Assert.Equal("304", report.Details["ifNoneMatch"]!.GetValue<string>());
        Assert.Equal("stable", report.Details["etagStability"]!.GetValue<string>());
        Assert.StartsWith("Fresh for 60 s", report.Summary);
    }

    [Fact]
    public async Task Caching_flags_unstable_etags_ignored_revalidation_and_shared_caching_of_private_data()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/me", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            ctx.Response.Headers.ETag = $"\"{Guid.NewGuid():N}\"";
            ctx.Response.Headers.AccessControlAllowOrigin = "https://app.example.com";
            return Results.Json(new { user = "ann" });
        }));
        await using var services = Services();
        var request = Get(server.BaseUrl + "/me");
        request.Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "secret" };
        var report = await services.GetRequiredService<CacheChecker>().CheckAsync(request, new CacheOptions { CheckHead = false });

        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("carries an Authorization header"));
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("ETag did"));
        // The ETag changes on every request, so the conditional request's 200 is inconclusive rather than a failure.
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Info && i.Text.Contains("changed during the probe"));
        Assert.Contains(report.Insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("Vary doesn't list Origin"));
    }
}
