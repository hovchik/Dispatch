using System.Net;
using System.Text;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Tests;

public class HttpRequestExecutorTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            send(request, ct);
    }

    private sealed class StubFactory(HttpMessageHandler handler, TimeSpan? timeout = null) : IHttpClientSource
    {
        public HttpClient GetClient(RequestSettings settings, NetworkCredential? credentials = null) =>
            new(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(100) };
    }

    private static readonly RequestSettings Settings = new();

    private static HttpRequestExecutor Executor(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        TimeSpan? timeout = null) => new(new StubFactory(new StubHandler(send), timeout));

    [Fact]
    public async Task Captures_status_body_headers_and_size()
    {
        var executor = Executor((req, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"id":7}""", Encoding.UTF8, "application/json"),
                RequestMessage = req
            };
            response.Headers.Add("X-Request-Id", "r-1");
            return Task.FromResult(response);
        });

        var result = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Post, "https://t.test/x"), Settings, CancellationToken.None);

        Assert.True(result.HasResponse);
        Assert.Equal(201, result.StatusCode);
        Assert.Equal("""{"id":7}""", result.Body);
        Assert.Equal(8, result.SizeBytes);
        Assert.Equal("application/json", result.ContentType);
        Assert.Contains(result.Headers, h => string.Equals(h.Name, "X-Request-Id", StringComparison.OrdinalIgnoreCase) && h.Value == "r-1");
        Assert.Equal("https://t.test/x", result.EffectiveUrl);
    }

    [Fact]
    public async Task Connection_failure_becomes_error_response()
    {
        var executor = Executor((_, _) => throw new HttpRequestException("No such host is known."));

        var result = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Get, "https://nope.test"), Settings, CancellationToken.None);

        Assert.False(result.HasResponse);
        Assert.Contains("No such host", result.Error);
    }

    [Fact]
    public async Task User_cancellation_is_reported_as_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var executor = Executor(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });

        var result = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Get, "https://t.test"), Settings, cts.Token);

        Assert.Equal("Request cancelled.", result.Error);
    }

    [Fact]
    public async Task Client_timeout_is_reported_as_timeout()
    {
        var executor = Executor(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }, timeout: TimeSpan.FromMilliseconds(50));

        var result = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Get, "https://t.test"), Settings, CancellationToken.None);

        Assert.StartsWith("Request timed out", result.Error);
    }

    [Fact]
    public async Task Binary_bodies_are_summarised()
    {
        var executor = Executor((_, _) => Task.FromResult(new HttpResponseMessage
        {
            Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]) { Headers = { { "Content-Type", "image/png" } } }
        }));

        var result = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Get, "https://t.test"), Settings, CancellationToken.None);

        Assert.StartsWith("[Binary content: image/png", result.Body);
    }
}

public sealed class PersistenceTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dispatch-test-{Guid.NewGuid():N}.db");
    private TestDbFactory _factory = null!;

    private sealed class TestDbFactory(string path) : IDbContextFactory<DispatchDbContext>
    {
        public DispatchDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<DispatchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbFactory(_dbPath);
        await new DatabaseInitializer(_factory).InitializeAsync();
    }

    public Task DisposeAsync()
    {
        File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Seeds_getting_started_data_once()
    {
        await new DatabaseInitializer(_factory).InitializeAsync(); // second run must not duplicate

        var collections = await new CollectionRepository(_factory).GetAllAsync();
        var envs = await new EnvironmentRepository(_factory).GetAllAsync();

        Assert.Single(collections);
        Assert.Equal(3, collections[0].Requests.Count);
        Assert.Single(envs);
        Assert.Equal(envs[0].Id.ToString(), await new SettingsRepository(_factory).GetAsync(SettingKeys.ActiveEnvironmentId));
    }

    [Fact]
    public async Task Request_round_trips_with_all_json_columns()
    {
        var repo = new CollectionRepository(_factory);
        var collection = new RequestCollection { Name = "Payments" };
        await repo.AddAsync(collection);

        var request = new ApiRequest
        {
            CollectionId = collection.Id,
            Name = "Create payment",
            Method = HttpVerb.Post,
            Url = "{{base}}/payments?dry=true",
            QueryParams = [new("dry", "true"), new("off", "1", enabled: false)],
            Headers = [new("Idempotency-Key", "{{$guid}}")],
            Body = new RequestBody { Mode = BodyMode.Json, Content = """{"amount":100}""" },
            Auth = new AuthSettings { Mode = AuthMode.ApiKey, ApiKeyName = "X-Key", ApiKeyValue = "k" }
        };
        await repo.SaveRequestAsync(request);

        request.Name = "Create payment v2";
        request.Headers.Add(new KeyValueItem("X-Extra", "1"));
        await repo.SaveRequestAsync(request); // update path

        var loaded = (await repo.GetAllAsync()).Single(c => c.Id == collection.Id).Requests.Single();
        Assert.Equal("Create payment v2", loaded.Name);
        Assert.Equal(HttpVerb.Post, loaded.Method);
        Assert.Equal(2, loaded.QueryParams.Count);
        Assert.False(loaded.QueryParams[1].Enabled);
        Assert.Equal(2, loaded.Headers.Count);
        Assert.Equal(BodyMode.Json, loaded.Body.Mode);
        Assert.Equal(AuthMode.ApiKey, loaded.Auth.Mode);
    }

    [Fact]
    public async Task Deleting_collection_removes_its_requests()
    {
        var repo = new CollectionRepository(_factory);
        var collection = (await repo.GetAllAsync()).Single();

        await repo.DeleteAsync(collection.Id);

        Assert.Empty(await repo.GetAllAsync());
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Requests.CountAsync());
    }

    [Fact]
    public async Task History_is_newest_first_and_pruned()
    {
        var repo = new HistoryRepository(_factory);
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        for (var i = 0; i < HistoryRepository.MaxEntries + 5; i++)
            await repo.AddAsync(new HistoryEntry { Url = $"/{i}", Timestamp = start.AddSeconds(i), Request = new ApiRequest() });

        var recent = await repo.GetRecentAsync(1000);

        Assert.Equal(HistoryRepository.MaxEntries, recent.Count);
        Assert.Equal($"/{HistoryRepository.MaxEntries + 4}", recent[0].Url);
    }

    [Fact]
    public async Task Sender_records_history_and_reports_build_errors_without_throwing()
    {
        var history = new HistoryRepository(_factory);
        var executor = new FixedExecutor(new ApiResponse { StatusCode = 200, ReasonPhrase = "OK" });
        var sender = new RequestSender([new HttpProtocolExecutor(new RequestMessageBuilder(), executor)], history);

        var ok = await sender.SendAsync(new ApiRequest { Url = "https://t.test" }, (ApiEnvironment?)null, CancellationToken.None);
        var bad = await sender.SendAsync(new ApiRequest { Url = "{{missing}}/x" }, (ApiEnvironment?)null, CancellationToken.None);

        Assert.Equal(200, ok.StatusCode);
        Assert.Contains("Unresolved variable", bad.Error);
        Assert.Single(await history.GetRecentAsync(10));
    }

    private sealed class FixedExecutor(ApiResponse response) : IRequestExecutor
    {
        public Task<ApiResponse> ExecuteAsync(HttpRequestMessage request, RequestSettings settings, CancellationToken ct) =>
            Task.FromResult(response);
    }
}

/// <summary>Redirects are followed by hand when the cookie jar is on, so cookies set on intermediate hops are kept.</summary>
public sealed class HttpRedirectCookieTests : IDisposable
{
    private readonly HttpClientPool _pool = new();

    public void Dispose() => _pool.Dispose();

    private HttpProtocolExecutor Executor(CookieJar jar) => new(new RequestMessageBuilder(), new HttpRequestExecutor(_pool), jar);

    [Fact]
    public async Task Cookies_from_every_redirect_hop_are_stored_and_sent_to_the_next_hop()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapPost("/login", (HttpContext ctx) =>
            {
                ctx.Response.Headers.Append("Set-Cookie", "sid=1; Path=/");
                return Results.Redirect("/step", permanent: false, preserveMethod: false); // 302
            });
            app.MapGet("/step", (HttpContext ctx) =>
            {
                if (ctx.Request.Cookies["sid"] != "1")
                    return Results.Unauthorized();
                ctx.Response.Headers.Append("Set-Cookie", "flag=2; Path=/");
                ctx.Response.Headers.Location = "/me";
                return Results.StatusCode(303);
            });
            app.MapGet("/me", (HttpContext ctx) => Results.Json(new
            {
                method = ctx.Request.Method,
                cookie = ctx.Request.Headers.Cookie.ToString()
            }));
        });
        var jar = new CookieJar();
        var request = new ApiRequest
        {
            Method = HttpVerb.Post,
            Url = server.BaseUrl + "/login",
            Body = new RequestBody { Mode = BodyMode.Json, Content = """{"u":"ann"}""" }
        };

        var response = await Executor(jar).SendAsync(request, CancellationToken.None);

        Assert.True(response.HasResponse, response.Error);
        Assert.Equal(200, response.StatusCode);
        Assert.EndsWith("/me", response.EffectiveUrl);
        Assert.StartsWith("POST /login", response.RawRequest);
        var body = System.Text.Json.Nodes.JsonNode.Parse(response.Body)!;
        Assert.Equal("GET", body["method"]!.GetValue<string>());
        Assert.Contains("sid=1", body["cookie"]!.GetValue<string>());
        Assert.Contains("flag=2", body["cookie"]!.GetValue<string>());
        Assert.Equal(["flag", "sid"], jar.GetAll().Select(c => c.Name).Order().ToArray());
    }

    [Fact]
    public async Task Temporary_redirect_keeps_method_and_body_but_drops_authorization_across_origins()
    {
        await using var target = await TestServer.StartAsync(app =>
            app.MapPost("/there", async (HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                return Results.Json(new
                {
                    body = await reader.ReadToEndAsync(),
                    auth = ctx.Request.Headers.Authorization.ToString(),
                    cookie = ctx.Request.Headers.Cookie.ToString(),
                    contentType = ctx.Request.ContentType
                });
            }));
        await using var origin = await TestServer.StartAsync(app =>
            app.MapPost("/go", (HttpContext ctx) =>
            {
                ctx.Response.Headers.Location = target.BaseUrl + "/there";
                return Results.StatusCode(307);
            }));
        // Cookies are scoped by host, not port: reach the origin as "localhost" so the two servers are different origins.
        var originUrl = origin.BaseUrl.Replace("127.0.0.1", "localhost");
        var jar = new CookieJar();
        jar.Store(new Uri(originUrl), ["a=origin; Path=/"]);
        jar.Store(new Uri(target.BaseUrl), ["b=target; Path=/"]);
        var request = new ApiRequest
        {
            Method = HttpVerb.Post,
            Url = originUrl + "/go",
            Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "secret" },
            Body = new RequestBody { Mode = BodyMode.Json, Content = """{"n":1}""" }
        };

        var response = await Executor(jar).SendAsync(request, CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        var body = System.Text.Json.Nodes.JsonNode.Parse(response.Body)!;
        Assert.Equal("""{"n":1}""", body["body"]!.GetValue<string>());
        Assert.StartsWith("application/json", body["contentType"]!.GetValue<string>());
        Assert.Equal("", body["auth"]!.GetValue<string>());      // not forwarded to another origin
        Assert.Equal("b=target", body["cookie"]!.GetValue<string>()); // the target's cookies, not the origin's
    }

    [Fact]
    public async Task Max_redirects_is_honoured_and_the_last_3xx_is_returned()
    {
        await using var server = await TestServer.StartAsync(app =>
            app.MapGet("/loop/{n:int}", (int n, HttpContext ctx) =>
            {
                ctx.Response.Headers.Location = $"/loop/{n + 1}";
                return Results.StatusCode(302);
            }));
        var request = new ApiRequest { Url = server.BaseUrl + "/loop/0", Settings = new RequestSettings { MaxRedirects = 2 } };

        var response = await Executor(new CookieJar()).SendAsync(request, CancellationToken.None);

        Assert.Equal(302, response.StatusCode);
        Assert.EndsWith("/loop/2", response.EffectiveUrl);
    }

    [Fact]
    public async Task Transport_setup_errors_are_error_responses()
    {
        var executor = new HttpRequestExecutor(_pool);

        var missingCert = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:1/"),
            new RequestSettings { ClientCertificatePath = Path.Combine(Path.GetTempPath(), "no-such-cert.pfx") }, CancellationToken.None);
        var badProxy = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:1/"),
            new RequestSettings { Proxy = "not a proxy url" }, CancellationToken.None);

        Assert.False(missingCert.HasResponse);
        Assert.Contains("Client certificate not found", missingCert.Error);
        Assert.False(badProxy.HasResponse);
        Assert.NotNull(badProxy.Error);
    }
}
