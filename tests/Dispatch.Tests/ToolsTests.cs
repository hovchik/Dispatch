using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dispatch.Application.Diff;
using Dispatch.Application.Interop;
using Dispatch.Application.Load;
using Dispatch.Application.Mock;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Mock;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Protocols.Grpc;
using Dispatch.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Tests;

public class MockRouteTableTests
{
    private static readonly ApiRequest GetUser = new()
    {
        Name = "Get user", Url = "{{baseUrl}}/users/{{id}}?expand=1",
        Examples =
        [
            new ResponseExample { Name = "Admin", Body = """{"id":"{{id}}","role":"admin"}""", MatchQuery = [new("role", "admin")] },
            new ResponseExample { Name = "Default", Body = """{"id":"{{id}}"}""" }
        ]
    };

    private static readonly ApiRequest GetMe = new() { Name = "Me", Url = "https://api.test/users/me", Examples = [new ResponseExample { Body = "me" }] };

    [Theory]
    [InlineData("{{baseUrl}}/users/{{id}}?x=1", "/users/{{id}}")]
    [InlineData("https://api.test:8443/v1/items/", "/v1/items")]
    [InlineData("{{baseUrl}}", "/")]
    [InlineData("/health", "/health")]
    public void Extracts_path_templates(string url, string expected) => Assert.Equal(expected, MockRouteTable.PathTemplate(url));

    [Fact]
    public void Prefers_literal_routes_and_rule_matched_examples()
    {
        var table = MockRouteTable.Build([GetUser, GetMe]);
        var empty = new Dictionary<string, string>();

        Assert.Equal("Me", table.Match("GET", "/users/me", empty, empty, "")!.Request.Name);

        var plain = table.Match("GET", "/users/42", empty, empty, "")!;
        Assert.Equal("Default", plain.Example!.Name);
        Assert.Equal("42", plain.Variables["id"]);

        var admin = table.Match("GET", "/users/42", new Dictionary<string, string> { ["role"] = "admin" }, empty, "")!;
        Assert.Equal("Admin", admin.Example!.Name);

        Assert.Null(table.Match("POST", "/users/42", empty, empty, ""));
    }
}

public sealed class MockServerTests
{
    [Fact]
    public async Task Serves_examples_with_variables_cors_and_404_listing()
    {
        await using var server = new MockServer();
        var port = HttpProtocolExecutorTests.FreePort();
        await server.StartAsync([
            new ApiRequest
            {
                Name = "Get user", Method = HttpVerb.Get, Url = "{{baseUrl}}/users/{{id}}",
                Examples = [new ResponseExample { StatusCode = 200, Headers = [new("X-Id", "{{id}}")], Body = """{"id":"{{id}}","trace":"{{$guid}}"}""" }]
            },
            new ApiRequest { Name = "Create", Method = HttpVerb.Post, Url = "{{baseUrl}}/users", Examples = [new ResponseExample { StatusCode = 201, Body = "{}" }] }
        ], new MockServerOptions { Port = port });
        var log = new List<MockLogEntry>();
        server.RequestHandled += log.Add;
        using var client = new HttpClient { BaseAddress = server.BaseUrl };

        var get = await client.GetAsync("/users/7");
        var body = JsonNode.Parse(await get.Content.ReadAsStringAsync())!;
        Assert.Equal(200, (int)get.StatusCode);
        Assert.Equal("7", body["id"]!.GetValue<string>());
        Assert.True(Guid.TryParse(body["trace"]!.GetValue<string>(), out _));
        Assert.Equal("7", get.Headers.GetValues("X-Id").Single());
        Assert.Equal("*", get.Headers.GetValues("Access-Control-Allow-Origin").Single());

        Assert.Equal(201, (int)(await client.PostAsync("/users", new StringContent("{}"))).StatusCode);

        var missing = await client.GetAsync("/nope");
        Assert.Equal(404, (int)missing.StatusCode);
        Assert.Contains("GET /users/{{id}}", await missing.Content.ReadAsStringAsync());
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public async Task Injects_errors_and_latency()
    {
        await using var server = new MockServer();
        await server.StartAsync([new ApiRequest { Url = "/ping", Examples = [new ResponseExample { Body = "pong" }] }],
            new MockServerOptions { Port = HttpProtocolExecutorTests.FreePort(), ErrorRate = 1, ErrorStatus = 503, LatencyMs = 150 });
        using var client = new HttpClient { BaseAddress = server.BaseUrl };

        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync("/ping");

        Assert.Equal(503, (int)response.StatusCode);
        Assert.True(sw.ElapsedMilliseconds >= 140);
    }

    [Fact]
    public async Task Mocks_grpc_methods_from_examples()
    {
        using var pool = new HttpClientPool();
        var protoPath = Path.Combine(AppContext.BaseDirectory, "Protos", "interop.proto");
        var grpcRequest = new ApiRequest
        {
            Kind = RequestKind.Grpc,
            Name = "Count",
            Protocol = new ProtocolSettings
            {
                Grpc = new GrpcSettings
                {
                    SchemaSource = GrpcSchemaSource.ProtoFiles, ProtoFiles = [protoPath],
                    Service = "dispatch.interop.Showcase", Method = "Count", Message = """{"to":2}"""
                },
                Stream = new StreamSettings { ListenSeconds = 5 }
            },
            Examples = [new ResponseExample { StatusCode = 0, Body = """[{"n":10},{"n":20},{"n":30}]""" }]
        };

        await using var server = new MockServer(new GrpcSchemaProvider(pool));
        var grpcPort = HttpProtocolExecutorTests.FreePort();
        await server.StartAsync([grpcRequest], new MockServerOptions { Port = HttpProtocolExecutorTests.FreePort(), GrpcPort = grpcPort });
        Assert.Empty(server.Warnings);

        grpcRequest.Url = $"localhost:{grpcPort}";
        var response = await new GrpcExecutor(pool, new GrpcSchemaProvider(pool)).ExecuteAsync(grpcRequest, new ExecutionContext(), CancellationToken.None);

        Assert.True(response.IsSuccess, response.ReasonPhrase);
        Assert.Equal([10, 20, 30], JsonNode.Parse(response.Body)!.AsArray().Select(n => n!["n"]!.GetValue<int>()));
    }
}

public sealed class LoadTesterTests
{
    [Fact]
    public async Task Runs_users_for_iterations_and_reports_percentiles_and_errors()
    {
        var calls = 0;
        await using var server = await TestServer.StartAsync(app => app.MapGet("/work", async (HttpContext ctx) =>
        {
            var n = Interlocked.Increment(ref calls);
            await Task.Delay(5);
            ctx.Response.StatusCode = n % 10 == 0 ? 500 : 200;
            await ctx.Response.WriteAsync("ok");
        }));
        using var pool = new HttpClientPool();
        var sender = new RequestSender([new HttpProtocolExecutor(new RequestMessageBuilder(), new HttpRequestExecutor(pool))], new NullHistory());
        var snapshots = new List<LoadSnapshot>();

        var report = await new LoadTester(sender).RunAsync(new Dispatch.Application.Load.LoadOptions
        {
            Requests = [new ApiRequest { Name = "work", Url = server.BaseUrl + "/work" }],
            VirtualUsers = 5,
            IterationsPerUser = 20
        }, new SyncProgress<LoadSnapshot>(snapshots.Add));

        Assert.Equal(100, report.TotalRequests);
        Assert.Equal(10, report.Errors);
        Assert.Equal(90, report.Statuses["200"]);
        Assert.True(report.Latency.P50 >= 5);
        Assert.True(report.Latency.P99 >= report.Latency.P50);
        Assert.Equal("work", report.PerRequest.Single().Name);
        Assert.Contains("req/s", report.ToText());
    }
}

public class DiffTests
{
    [Fact]
    public void Line_diff_marks_added_and_removed_lines()
    {
        var diff = ResponseDiff.Lines("a\nb\nc\nd", "a\nc\nd\ne");

        Assert.Equal([DiffKind.Same, DiffKind.Removed, DiffKind.Same, DiffKind.Same, DiffKind.Added], diff.Select(d => d.Kind));
        Assert.Equal("b", diff[1].Text);
        Assert.Equal("e", diff[4].Text);
        Assert.All(ResponseDiff.Lines("x\ny", "x\ny"), l => Assert.Equal(DiffKind.Same, l.Kind));
    }

    [Fact]
    public void Line_diff_handles_empty_and_completely_different_inputs()
    {
        Assert.Equal(2, ResponseDiff.Lines("", "a").Count);
        var diff = ResponseDiff.Lines("1\n2\n3", "4\n5");
        Assert.Equal(3, diff.Count(d => d.Kind == DiffKind.Removed));
        Assert.Equal(2, diff.Count(d => d.Kind == DiffKind.Added));
    }

    [Fact]
    public void Json_diff_reports_paths_and_honours_ignore_rules()
    {
        var changes = ResponseDiff.Json(
            """{"id":1,"name":"Ann","tags":["a"],"meta":{"at":"t1","v":1},"items":[{"id":1,"updatedAt":"x"}]}""",
            """{"id":2,"name":"Ann","tags":["a","b"],"meta":{"at":"t2","v":"1"},"items":[{"id":1,"updatedAt":"y"}],"extra":true}""",
            ["$.id", "$..at", "$.items[*].updatedAt"]);

        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c is { Kind: JsonChangeKind.Added, Path: "$.tags[1]" });
        Assert.Contains(changes, c => c is { Kind: JsonChangeKind.TypeChanged, Path: "$.meta.v" });
        Assert.Contains(changes, c => c is { Kind: JsonChangeKind.Added, Path: "$.extra" });
    }
}

public sealed class SecretTests
{
    [Fact]
    public void Protector_round_trips_and_detects_tampering()
    {
        var protector = new SecretProtector(() => new byte[32]);
        var encrypted = protector.Protect("p@ss");

        Assert.StartsWith(SecretProtector.Prefix, encrypted);
        Assert.NotEqual(encrypted, protector.Protect("p@ss")); // random nonce
        Assert.Equal("p@ss", protector.Unprotect(encrypted));

        var tampered = encrypted[..^4] + (encrypted[^4] == 'A' ? "B" : "A") + encrypted[^3..];
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(tampered));
    }

    [Fact]
    public async Task Environment_secrets_are_encrypted_in_the_database_only()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dispatch-secrets-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new DbFactory(path);
            await new DatabaseInitializer(factory).InitializeAsync();
            var repo = new EnvironmentRepository(factory, new SecretProtector(() => Enumerable.Repeat((byte)7, 32).ToArray()));
            var env = new ApiEnvironment { Name = "Prod", Variables = [new("host", "h"), new("password", "hunter2") { IsSecret = true }] };

            await repo.SaveAsync(env);

            Assert.Equal("hunter2", env.Variables[1].Value); // the caller's object is untouched
            await using (var db = factory.CreateDbContext())
            {
                var raw = await db.Environments.AsNoTracking().SingleAsync(e => e.Id == env.Id);
                Assert.StartsWith(SecretProtector.Prefix, raw.Variables[1].Value);
                Assert.Equal("h", raw.Variables[0].Value);
            }
            var loaded = (await repo.GetAllAsync()).Single(e => e.Id == env.Id);
            Assert.Equal("hunter2", loaded.Variables[1].Value);

            // Another key (e.g. another machine) can't read it; the value comes back empty instead of failing.
            var other = new EnvironmentRepository(factory, new SecretProtector(() => new byte[32]));
            Assert.Equal("", (await other.GetAllAsync()).Single(e => e.Id == env.Id).Variables[1].Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class DbFactory(string path) : IDbContextFactory<DispatchDbContext>
    {
        public DispatchDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<DispatchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}

public sealed class CliTests
{
    private static async Task<(int Exit, string Output)> Cli(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Dispatch.Cli", "bin", "Debug", "net10.0", "dispatch.dll");
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(Path.GetFullPath(dll));
        foreach (var a in args)
            info.ArgumentList.Add(a);
        info.Environment["NO_COLOR"] = "1";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    [Fact]
    public async Task Run_reports_results_writes_junit_and_sets_exit_code()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapPost("/login", () => Results.Json(new { token = "t-1" }));
            app.MapGet("/me", (HttpContext ctx) => ctx.Request.Headers.Authorization == "Bearer t-1" ? Results.Json(new { name = "Ann" }) : Results.StatusCode(401));
        });

        var dir = Path.Combine(Path.GetTempPath(), "dispatch-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var collection = new RequestCollection { Name = "Smoke", Variables = [new("base", server.BaseUrl)] };
            collection.Requests.Add(new ApiRequest
            {
                Name = "Login", Method = HttpVerb.Post, Url = "{{base}}/login", SortOrder = 0,
                Extractions = [new ExtractionRule { Variable = "token", Path = "$.token" }]
            });
            collection.Requests.Add(new ApiRequest
            {
                Name = "Me", Url = "{{base}}/me", SortOrder = 1,
                Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "{{token}}" },
                Assertions = [new Assertion { Source = ValueSource.JsonPath, Path = "$.name", Expected = "{{expectedName}}" }]
            });
            var file = Path.Combine(dir, "smoke.dispatch.json");
            await File.WriteAllTextAsync(file, DispatchFormat.ExportCollection(collection));
            var db = Path.Combine(dir, "cli.db");

            var (passExit, passOutput) = await Cli("run", file, "--var", "expectedName=Ann", "-r", "cli,junit", "-o", dir, "--db", db);
            var (failExit, failOutput) = await Cli("run", file, "--var", "expectedName=Bob", "--db", db);

            Assert.True(passExit == 0, passOutput);
            Assert.Contains("✓ Me", passOutput);
            var junit = XDocument.Load(Directory.GetFiles(dir, "*.xml").Single());
            Assert.Equal("0", junit.Root!.Attribute("failures")!.Value);

            Assert.Equal(1, failExit);
            Assert.Contains("✗ Me", failOutput);
            Assert.Contains("Actual: Ann", failOutput);

            var (usageExit, _) = await Cli("run");
            Assert.Equal(2, usageExit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
