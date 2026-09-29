using System.Net;
using System.Text;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Persistence;
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

    private sealed class StubFactory(HttpMessageHandler handler, TimeSpan? timeout = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(100) };
    }

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

        var result = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Post, "https://t.test/x"), CancellationToken.None);

        Assert.True(result.HasResponse);
        Assert.Equal(201, result.StatusCode);
        Assert.Equal("""{"id":7}""", result.Body);
        Assert.Equal(8, result.SizeBytes);
        Assert.Equal("application/json", result.ContentType);
        Assert.Contains(result.Headers, h => h.Name == "X-Request-Id" && h.Value == "r-1");
        Assert.Equal("https://t.test/x", result.EffectiveUrl);
    }

    [Fact]
    public async Task Connection_failure_becomes_error_response()
    {
        var executor = Executor((_, _) => throw new HttpRequestException("No such host is known."));

        var result = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Get, "https://nope.test"), CancellationToken.None);

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

        var result = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Get, "https://t.test"), cts.Token);

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

        var result = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Get, "https://t.test"), CancellationToken.None);

        Assert.StartsWith("Request timed out", result.Error);
    }

    [Fact]
    public async Task Binary_bodies_are_summarised()
    {
        var executor = Executor((_, _) => Task.FromResult(new HttpResponseMessage
        {
            Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]) { Headers = { { "Content-Type", "image/png" } } }
        }));

        var result = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Get, "https://t.test"), CancellationToken.None);

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
        var sender = new RequestSender(new RequestMessageBuilder(), executor, history);

        var ok = await sender.SendAsync(new ApiRequest { Url = "https://t.test" }, null, CancellationToken.None);
        var bad = await sender.SendAsync(new ApiRequest { Url = "{{missing}}/x" }, null, CancellationToken.None);

        Assert.Equal(200, ok.StatusCode);
        Assert.Contains("Unresolved variable", bad.Error);
        Assert.Single(await history.GetRecentAsync(10));
    }

    private sealed class FixedExecutor(ApiResponse response) : IRequestExecutor
    {
        public Task<ApiResponse> ExecuteAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response);
    }
}
