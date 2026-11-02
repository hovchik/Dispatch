using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dispatch.Application.Consequences;
using Dispatch.Application.Requests;
using Dispatch.Application.Running;
using Dispatch.Application.Webhooks;
using Dispatch.Domain;
using Dispatch.Infrastructure;
using Dispatch.Infrastructure.Webhooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Tests;

public class WebhookUnitTests
{
    [Theory]
    [InlineData("POST", "/hooks/orders", "", true)]
    [InlineData("POST", "/hooks/orders", "/hooks/orders", true)]
    [InlineData("POST", "/hooks/orders/", "/HOOKS/orders", true)]
    [InlineData("POST", "/hooks/orders", "/hooks/other", false)]
    [InlineData("POST", "/hooks/orders", "/hooks/*", true)]
    [InlineData("POST", "/hooks/orders/42", "/hooks/*/42", true)]
    [InlineData("POST", "/hooks/orders", "orders", true)]
    [InlineData("POST", "/hooks/orders", "POST /hooks/orders", true)]
    [InlineData("GET", "/hooks/orders", "POST /hooks/orders", false)]
    [InlineData("GET", "/hooks/orders", "GET", true)]
    public void Matcher_understands_paths_wildcards_and_methods(string method, string path, string filter, bool expected) =>
        Assert.Equal(expected, WebhookMatcher.Matches(method, path, filter));

    [Fact]
    public void Matcher_reads_stream_message_labels()
    {
        Assert.True(WebhookMatcher.MatchesLabel("POST /hooks/orders", "/hooks/orders"));
        Assert.False(WebhookMatcher.MatchesLabel("POST /hooks/orders", "/hooks/payments"));
        Assert.True(WebhookMatcher.MatchesLabel(null, ""));
    }

    [Fact]
    public async Task Log_is_bounded_and_wakes_waiters()
    {
        var log = new WebhookLog(capacity: 2);
        log.Add(new ReceivedWebhook { Path = "/a" });
        log.Add(new ReceivedWebhook { Path = "/b" });
        log.Add(new ReceivedWebhook { Path = "/c" });
        Assert.Equal(["/b", "/c"], log.Snapshot().Select(h => h.Path));
        Assert.Equal(3, log.Total);

        var since = DateTimeOffset.Now;
        var waiting = log.WaitForAsync("/hooks/orders", since, since.AddSeconds(5));
        log.Add(new ReceivedWebhook { Path = "/hooks/other", ReceivedAt = since.AddMilliseconds(1) });
        Assert.False(waiting.IsCompleted);
        log.Add(new ReceivedWebhook { Path = "/hooks/orders", ReceivedAt = since.AddMilliseconds(2) });
        Assert.Equal("/hooks/orders", (await waiting)?.Path);
        Assert.Null(await log.WaitForAsync("/nothing", since, DateTimeOffset.Now.AddMilliseconds(50)));
    }

    [Fact]
    public void Replay_builds_a_request_and_an_example_from_a_hook()
    {
        var hook = new ReceivedWebhook
        {
            Method = "POST", Path = "/hooks/orders", Query = "v=2", ContentType = "application/json", Body = """{"status":"paid"}""",
            Headers = [new KeyValueItem("Host", "localhost:9000"), new KeyValueItem("Content-Length", "17"), new KeyValueItem("X-Signature", "abc"),
                       new KeyValueItem("Content-Type", "application/json")]
        };
        var request = WebhookReplay.ToRequest(hook, "localhost:8080/");
        Assert.Equal("http://localhost:8080/hooks/orders?v=2", request.Url);
        Assert.Equal(HttpVerb.Post, request.Method);
        Assert.Equal(BodyMode.Json, request.Body.Mode);
        Assert.Equal(["X-Signature", "Content-Type"], request.Headers.Select(h => h.Key));

        var example = WebhookReplay.ToExample(hook);
        Assert.Equal(200, example.StatusCode);
        Assert.Contains("\"status\": \"paid\"", example.Body);
        Assert.DoesNotContain(example.Headers, h => h.Key == "Content-Type");

        var collection = new RequestCollection { Name = "Shop" };
        var created = WebhookReplay.FindOrCreateRequest(collection, hook);
        Assert.Equal("{{webhookTarget}}/hooks/orders", created.Url);
        Assert.Equal("Webhooks", created.Folder);
        Assert.Equal(collection.Id, created.CollectionId);
        collection.Requests.Add(created);
        Assert.Same(created, WebhookReplay.FindOrCreateRequest(collection, hook));
        Assert.NotSame(created, WebhookReplay.FindOrCreateRequest(collection, new ReceivedWebhook { Method = "POST", Path = "/hooks/payments" }));
    }

    [Fact]
    public void Reports_round_trip_json_and_render_html()
    {
        var hooks = new List<ReceivedWebhook>
        {
            new() { Method = "POST", Path = "/hooks/orders", Body = """{"id":1}""", ContentType = "application/json", BodyBytes = 8,
                    Headers = [new KeyValueItem("X-Signature", "abc")], RemoteAddress = "127.0.0.1:5" },
            new() { Method = "GET", Path = "/ping", Query = "a=1" }
        };
        var json = WebhookReportWriter.Json(hooks, new Uri("http://localhost:9000/"));
        var back = WebhookReportWriter.FromJson(json);
        Assert.Equal(2, back.Count);
        Assert.Equal((hooks[0].Id, "POST", "/hooks/orders", """{"id":1}""", "abc"), (back[0].Id, back[0].Method, back[0].Path, back[0].Body, back[0].Header("X-Signature")));
        Assert.Equal("a=1", back[1].Query);
        Assert.Contains("\"json\": {", json);

        var html = WebhookReportWriter.Html(hooks, new Uri("http://localhost:9000/"));
        Assert.Contains("/hooks/orders", html);
        Assert.Contains("X-Signature", html);
        Assert.Contains("2 HOOK(S)", html);
        Assert.Contains("POST   /hooks/orders", WebhookReportWriter.Text(hooks));
    }

    [Fact]
    public void Webhook_checks_are_described_with_the_receiver_name()
    {
        var e = new MessageExpectation { ListenerId = WebhookListener.Id, Channel = "/hooks/orders", Path = "$.status", Expected = "paid", TimeoutMs = 5000 };
        Assert.True(WebhookListener.Is(e));
        var text = Application.Docs.DocsGenerator.MarkdownText(new RequestCollection { Name = "Shop", Requests = [new ApiRequest { Name = "Create", Expectations = [e] }] });
        Assert.Contains("Message on Webhook receiver [/hooks/orders] where $.status == paid within 5000 ms", text);
    }
}

/// <summary>The receiver as a real Kestrel endpoint, and webhook message checks end to end.</summary>
[Collection("Console")]
public sealed class WebhookIntegrationTests : IAsyncLifetime
{
    private TestServer _api = null!;
    private ServiceProvider _services = null!;
    private readonly HttpClient _client = new();
    private int _nextId = 500;

    public async Task InitializeAsync()
    {
        _api = await TestServer.StartAsync(app =>
        {
            // Answers first, then calls back a little later: the check must wait for the callback.
            app.MapPost("/orders", (HttpContext ctx) =>
            {
                var id = Interlocked.Increment(ref _nextId);
                var callback = ctx.Request.Query["callback"].ToString();
                var status = ctx.Request.Query["status"].ToString() is { Length: > 0 } s ? s : "paid";
                if (callback.Length > 0)
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(150);
                        using var content = new StringContent($$"""{"orderId":{{id}},"status":"{{status}}"}""", Encoding.UTF8, "application/json");
                        content.Headers.Add("X-Signature", "sig-" + id);
                        await _client.PostAsync(callback, content);
                    });
                return Results.Json(new { id });
            });
        });
        _services = new ServiceCollection().AddDispatchEngine()
            .AddSingleton<Application.Abstractions.IHistoryRepository, NullHistory>()
            .BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.GetRequiredService<IWebhookReceiver>().StopAsync();
        await _api.DisposeAsync();
        await _services.DisposeAsync();
        _client.Dispose();
    }

    [Fact]
    public async Task Receiver_records_any_callback_and_answers_as_configured()
    {
        await using var receiver = new WebhookReceiver();
        await receiver.StartAsync(new WebhookReceiverOptions { Port = 0, ResponseStatus = 202, ResponseBody = """{"queued":true}""" });
        Assert.True(receiver.IsRunning);
        Assert.NotNull(receiver.BaseUrl);

        using var content = new StringContent("""{"event":"order.paid","amount":12.5}""", Encoding.UTF8, "application/json");
        content.Headers.Add("X-Signature", "abc");
        var response = await _client.PostAsync(receiver.UrlFor("/hooks/orders?source=test"), content);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("""{"queued":true}""", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.Contains("Dispatch-Webhook-Id"));

        var put = await _client.PutAsync(receiver.UrlFor("/anything/else"), new StringContent("plain text"));
        Assert.Equal(HttpStatusCode.Accepted, put.StatusCode);

        // A browser visit to the root is a status page, not a recorded hook.
        using var browser = new HttpRequestMessage(HttpMethod.Get, receiver.BaseUrl);
        browser.Headers.Accept.ParseAdd("text/html");
        var page = await _client.SendAsync(browser);
        Assert.Contains("Dispatch webhook receiver", await page.Content.ReadAsStringAsync());

        var hooks = receiver.Log.Snapshot();
        Assert.Equal(2, hooks.Count);
        var hook = hooks[0];
        Assert.Equal(("POST", "/hooks/orders", "source=test", "abc"), (hook.Method, hook.Path, hook.Query, hook.Header("X-Signature")));
        Assert.Contains("order.paid", hook.Body);
        Assert.True(hook.IsJson);
        Assert.Equal("POST /hooks/orders", hook.Label);
        Assert.Equal(("PUT", "plain text"), (hooks[1].Method, hooks[1].Body));

        await receiver.StopAsync();
        Assert.False(receiver.IsRunning);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _client.PostAsync(hooks[0].Header("Host") is { } host ? $"http://{host}/x" : "http://localhost:1/x", content));
    }

    [Fact]
    public async Task Receiver_cuts_bodies_over_the_limit_and_refuses_double_starts()
    {
        await using var receiver = new WebhookReceiver();
        await receiver.StartAsync(new WebhookReceiverOptions { Port = 0, MaxBodyBytes = 2048 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.StartAsync(new WebhookReceiverOptions { Port = 0 }));
        var big = new string('x', 5000);
        var response = await _client.PostAsync(receiver.UrlFor("/big"), new StringContent(big));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var hook = Assert.Single(receiver.Log.Snapshot());
        Assert.True(hook.Truncated);
        Assert.Equal(5000, hook.BodyBytes);
        Assert.Equal(2048, hook.Body.Length);
    }

    private static ApiRequest CreateOrder(string callback, string status, params MessageExpectation[] expectations) => new()
    {
        Name = "Create order", Method = HttpVerb.Post,
        Url = $"{{{{api}}}}/orders?callback={Uri.EscapeDataString(callback)}&status={status}",
        Extractions = [new ExtractionRule { Variable = "orderId", Source = ValueSource.JsonPath, Path = "$.id", Scope = VariableScope.Runtime }],
        Expectations = [.. expectations]
    };

    private static MessageExpectation Hook(string path, string jsonPath, string expected, int timeoutMs = 3000, bool none = false) => new()
    {
        ListenerId = WebhookListener.Id, Channel = path, Path = jsonPath, Expected = expected, TimeoutMs = timeoutMs, ExpectNone = none
    };

    [Fact]
    public async Task Webhook_check_waits_for_the_callback_and_reports_what_arrived()
    {
        var receiver = _services.GetRequiredService<IWebhookReceiver>();
        await receiver.StartAsync(new WebhookReceiverOptions { Port = 0 });
        var callback = receiver.UrlFor("/hooks/orders").ToString();
        var request = CreateOrder(callback, "paid",
            Hook("/hooks/orders", "$.status", "paid"),
            Hook("/hooks/orders", "$.orderId", "{{orderId}}"),
            Hook("POST /hooks/*", "$.status", "failed", 400, none: true),
            Hook("/hooks/payments", "$.status", "paid", 300));
        var sender = _services.GetRequiredService<IRequestSender>();
        var environment = new ApiEnvironment { Name = "test", Variables = [new KeyValueItem("api", _api.BaseUrl)] };

        var response = await sender.SendAsync(request, new SendOptions { Environment = environment, RecordHistory = false }, CancellationToken.None);

        Assert.True(response.HasResponse, response.Error);
        var tests = response.TestResults;
        Assert.Equal(4, tests.Count);
        Assert.True(tests[0].Passed, tests[0].Message);
        Assert.StartsWith("Message on Webhook receiver [/hooks/orders] where $.status == paid", tests[0].Name);
        Assert.Contains("\"status\":\"paid\"", tests[0].Actual);
        Assert.True(tests[1].Passed, tests[1].Message);
        Assert.True(tests[2].Passed, tests[2].Message);
        Assert.StartsWith("No message on Webhook receiver [POST /hooks/*]", tests[2].Name);
        Assert.False(tests[3].Passed);
        Assert.Contains("No matching message arrived within 300 ms", tests[3].Message);
        Assert.Contains("other message(s) received", tests[3].Message);
    }

    [Fact]
    public async Task Webhook_check_starts_the_receiver_on_demand_and_ignores_earlier_callbacks()
    {
        var previous = Environment.GetEnvironmentVariable("DISPATCH_WEBHOOK_PORT");
        var port = HttpProtocolExecutorTests.FreePort();
        Environment.SetEnvironmentVariable("DISPATCH_WEBHOOK_PORT", port.ToString());
        try
        {
            var receiver = _services.GetRequiredService<IWebhookReceiver>();
            await receiver.StopAsync();
            Assert.False(receiver.IsRunning);

            var callback = $"http://localhost:{port}/hooks/orders";
            var sender = _services.GetRequiredService<IRequestSender>();
            var environment = new ApiEnvironment { Name = "test", Variables = [new KeyValueItem("api", _api.BaseUrl)] };

            // First send: the receiver is started by the check itself.
            var first = await sender.SendAsync(CreateOrder(callback, "paid", Hook("/hooks/orders", "$.status", "paid")),
                new SendOptions { Environment = environment, RecordHistory = false }, CancellationToken.None);
            Assert.True(receiver.IsRunning);
            Assert.Equal(port, receiver.Port);
            Assert.True(Assert.Single(first.TestResults).Passed, first.TestResults[0].Message);

            // Second send: the earlier "paid" hook is still in the log but must not satisfy a check that expects it now.
            var second = await sender.SendAsync(CreateOrder(callback, "pending", Hook("/hooks/orders", "$.status", "paid", 500)),
                new SendOptions { Environment = environment, RecordHistory = false }, CancellationToken.None);
            var result = Assert.Single(second.TestResults);
            Assert.False(result.Passed);
            Assert.Contains("\"status\":\"pending\"", result.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPATCH_WEBHOOK_PORT", previous);
        }
    }

    [Fact]
    public async Task Collection_runner_and_cli_run_webhook_checks()
    {
        var previous = Environment.GetEnvironmentVariable("DISPATCH_WEBHOOK_PORT");
        var port = HttpProtocolExecutorTests.FreePort();
        Environment.SetEnvironmentVariable("DISPATCH_WEBHOOK_PORT", port.ToString());
        try
        {
            await _services.GetRequiredService<IWebhookReceiver>().StopAsync();
            var callback = $"http://localhost:{port}/hooks/orders";
            var ok = CreateOrder(callback, "paid", Hook("/hooks/orders", "$.status", "paid"));
            var missing = CreateOrder(callback, "paid", Hook("/hooks/refunds", "$.status", "paid", 300));
            missing.Name = "Refund order";
            var environment = new ApiEnvironment { Name = "test", Variables = [new KeyValueItem("api", _api.BaseUrl)] };

            var runner = _services.GetRequiredService<CollectionRunner>();
            var report = await runner.RunAsync(new RunOptions { Requests = [ok], CollectionRequests = [ok], Environment = environment });
            Assert.True(report.Passed, string.Join("; ", report.Results.SelectMany(r => r.Response.TestResults).Select(t => t.Message)));
            await _services.GetRequiredService<IWebhookReceiver>().StopAsync();

            var dir = Cli.TempDir();
            var file = Path.Combine(dir, "shop.dispatch.json");
            await File.WriteAllTextAsync(file, Application.Interop.DispatchFormat.ExportCollection(
                new RequestCollection { Name = "Shop", Requests = [ok, missing], Variables = [new KeyValueItem("api", _api.BaseUrl)] }));

            var (exit, output) = await Cli.RunAsync("run", file, "--db", Path.Combine(dir, "db"), "--no-color");

            Assert.Equal(1, exit);
            Assert.Contains("Message on Webhook receiver [/hooks/orders] where $.status == paid", output);
            Assert.Contains("No matching message arrived within 300 ms", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPATCH_WEBHOOK_PORT", previous);
        }
    }

    [Fact]
    public async Task Cli_webhooks_records_callbacks_to_a_file_and_stops_after_the_expected_count()
    {
        var port = HttpProtocolExecutorTests.FreePort();
        var dir = Cli.TempDir();
        var outFile = Path.Combine(dir, "hooks.json");
        var sending = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    await _client.PostAsync($"http://localhost:{port}/hooks/orders", new StringContent("""{"id":7}""", Encoding.UTF8, "application/json"));
                    return;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(100);
                }
            }
        });

        var (exit, output) = await Cli.RunAsync("webhooks", "--port", port.ToString(), "--expect", "1", "--duration", "20s", "--out", outFile,
            "-r", "cli,json", "--report-dir", Path.Combine(dir, "reports"), "--no-color");
        await sending;

        Assert.Equal(0, exit);
        Assert.Contains("Received 1 callback(s)", output);
        Assert.Contains("POST   /hooks/orders", output);
        var saved = WebhookReportWriter.FromJson(await File.ReadAllTextAsync(outFile));
        Assert.Equal("""{"id":7}""", Assert.Single(saved).Body);
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "reports"), "webhooks-*.json"));

        var (missed, text) = await Cli.RunAsync("webhooks", "--port", port.ToString(), "--path", "/hooks/orders", "--expect", "1", "--duration", "300ms", "--no-color");
        Assert.Equal(1, missed);
        Assert.Contains("Received 0 callback(s), 0 to /hooks/orders.", text);
    }
}
