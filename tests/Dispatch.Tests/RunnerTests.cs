using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Application.Running;
using Dispatch.Application.Variables;
using Dispatch.Domain;
using Dispatch.Infrastructure.Scripting;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Tests;

/// <summary>Answers every request with a canned response built from the (resolved) request.</summary>
internal sealed class FakeExecutor(Func<ApiRequest, ApiResponse> respond) : IProtocolExecutor
{
    public List<ApiRequest> Sent { get; } = [];
    public IReadOnlyCollection<RequestKind> Kinds { get; } = Enum.GetValues<RequestKind>();

    public Task<ApiResponse> ExecuteAsync(ApiRequest request, ExecutionContext context, CancellationToken cancellationToken)
    {
        lock (Sent)
            Sent.Add(request);
        return Task.FromResult(respond(request));
    }
}

internal sealed class NullHistory : IHistoryRepository
{
    public Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(int take, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HistoryEntry>>([]);
    public Task AddAsync(HistoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(Guid entryId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public class ScriptTests
{
    private static readonly ApiResponse Response = new()
    {
        StatusCode = 200,
        ReasonPhrase = "OK",
        Elapsed = TimeSpan.FromMilliseconds(42),
        Body = """{"user":{"id":7,"roles":["admin"]},"token":"abc"}""",
        Headers = [new ResponseHeader("Content-Type", "application/json")]
    };

    [Fact]
    public async Task Postman_style_tests_pass_and_fail_with_messages()
    {
        const string script = """
            pm.test("status is 200", function () { pm.response.to.have.status(200); });
            pm.test("is ok", () => pm.response.to.be.ok);
            pm.test("json", () => {
              const json = pm.response.json();
              pm.expect(json.user.id).to.equal(7);
              pm.expect(json.user.roles).to.include("admin");
              pm.expect(json).to.have.nested.property("user.id", 7);
              pm.expect(json.token).to.be.a("string").and.not.empty;
              pm.expect(pm.response.responseTime).to.be.below(1000);
              pm.expect(pm.response.headers.get("content-type")).to.match(/json/);
            });
            pm.test("fails", () => pm.expect(1).to.eql(2));
            pm.environment.set("token", pm.response.json().token);
            pm.variables.set("obj", { a: 1 });
            console.log("done", { n: 1 });
            """;
        var variables = new VariableContext();

        var result = await new JintScriptRunner().RunTestsAsync(script, new ApiRequest(), Response, variables, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(4, result.Tests.Count);
        Assert.All(result.Tests.Take(3), t => Assert.True(t.Passed, $"{t.Name}: {t.Message}"));
        Assert.False(result.Tests[3].Passed);
        Assert.Contains("expected 1 to deeply equal 2", result.Tests[3].Message);
        Assert.Equal("abc", variables.EnvironmentUpdates["token"]);
        Assert.Equal("""{"a":1}""", variables.Runtime["obj"]);
        Assert.Equal(["done {\"n\":1}"], result.Log);
    }

    [Fact]
    public async Task Pre_request_script_changes_request_and_variables()
    {
        var request = new ApiRequest
        {
            Url = "https://api/{{path}}",
            Headers = [new("X-Old", "1"), new("X-Off", "0", enabled: false)],
            Body = new RequestBody { Mode = BodyMode.Json, Content = "{}" }
        };
        const string script = """
            pm.variables.set("path", "users");
            pm.request.headers.upsert({ key: "X-Sig", value: btoa("secret") });
            pm.request.headers.remove("X-Old");
            pm.request.body.raw = JSON.stringify({ at: pm.variables.replaceIn("{{path}}") });
            """;
        var variables = new VariableContext();

        var result = await new JintScriptRunner().RunPreRequestAsync(script, request, variables, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("users", variables.Get("path"));
        Assert.Contains(request.Headers, h => h.Key == "X-Sig" && h.Value == "c2VjcmV0");
        Assert.DoesNotContain(request.Headers, h => h.Key == "X-Old");
        Assert.Contains(request.Headers, h => h.Key == "X-Off" && !h.Enabled);
        Assert.Equal("""{"at":"users"}""", request.Body.Content);
    }

    [Fact]
    public async Task Errors_and_infinite_loops_are_contained()
    {
        var runner = new JintScriptRunner();
        var error = await runner.RunTestsAsync("throw new Error('boom')", new ApiRequest(), Response, new VariableContext(), CancellationToken.None);
        var loop = await runner.RunTestsAsync("while (true) {}", new ApiRequest(), Response, new VariableContext(), CancellationToken.None);
        var syntax = await runner.RunTestsAsync("pm.test(", new ApiRequest(), Response, new VariableContext(), CancellationToken.None);

        Assert.Contains("boom", error.Error);
        Assert.NotNull(loop.Error);
        Assert.NotNull(syntax.Error);
    }
}

public class RunnerTests
{
    [Fact]
    public async Task Runs_iterations_from_data_rows_and_chains_extracted_values()
    {
        var executor = new FakeExecutor(r => r.Url.EndsWith("/login")
            ? new ApiResponse { StatusCode = 200, Body = """{"token":"t-123"}""" }
            : new ApiResponse { StatusCode = r.Auth.Token == "t-123" ? 200 : 401, Body = $$"""{"user":"{{r.Url[(r.Url.LastIndexOf('/') + 1)..]}}"}""" });
        var sender = new RequestSender([executor], new NullHistory(), scripts: new JintScriptRunner());
        var login = new ApiRequest
        {
            Name = "Login",
            Url = "{{base}}/login",
            Extractions = [new ExtractionRule { Variable = "token", Source = ValueSource.JsonPath, Path = "$.token" }]
        };
        var getUser = new ApiRequest
        {
            Name = "Get user",
            Url = "{{base}}/users/{{userId}}",
            Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "{{token}}" },
            Assertions = [new Assertion { Source = ValueSource.Status, Expected = "200" }],
            TestScript = "pm.test('user matches data', () => pm.expect(pm.response.json().user).to.equal(pm.iterationData.get('userId')));"
        };
        var progress = new List<RequestRunResult>();

        var report = await new CollectionRunner(sender).RunAsync(new RunOptions
        {
            Requests = [login, getUser],
            Environment = new ApiEnvironment { Variables = [new("base", "https://api.test")] },
            Data = DataFile.ParseCsv("userId,name\n7,Ann\n\"8\",\"Bob, Jr\"\n")
        }, new SyncProgress<RequestRunResult>(progress.Add));

        Assert.Equal(2, report.Iterations);
        Assert.Equal(4, report.TotalRequests);
        Assert.True(report.Passed, ReportWriters.Text(report));
        Assert.Equal(4, progress.Count);
        Assert.Equal("t-123", report.EnvironmentUpdates["token"]);
        Assert.Equal("https://api.test/users/8", executor.Sent[3].Url);
    }

    [Fact]
    public async Task Stop_on_failure_ends_the_run()
    {
        var sender = new RequestSender([new FakeExecutor(_ => new ApiResponse { StatusCode = 500 })], new NullHistory());

        var report = await new CollectionRunner(sender).RunAsync(new RunOptions
        {
            Requests = [new ApiRequest { Url = "https://a" }, new ApiRequest { Url = "https://b" }],
            Iterations = 3,
            StopOnFailure = true
        });

        Assert.True(report.Stopped);
        Assert.Single(report.Results);
        Assert.False(report.Passed);
    }

    [Fact]
    public void Reports_render_junit_json_and_html()
    {
        var report = new RunReport { Name = "Smoke", StartedAt = DateTimeOffset.Now, Iterations = 1, Duration = TimeSpan.FromSeconds(1) };
        report.Results.Add(new RequestRunResult(0, new ApiRequest { Name = "A <b>" }, new ApiResponse
        {
            StatusCode = 200,
            TestResults = [new TestResult("ok", true), new TestResult("bad", false, "expected 1 to equal 2")]
        }));
        report.Results.Add(new RequestRunResult(0, new ApiRequest { Name = "B" }, ApiResponse.Failed("refused", TimeSpan.Zero)));

        var junit = XDocument.Parse(ReportWriters.JUnit(report));
        Assert.Equal("3", junit.Root!.Attribute("tests")!.Value);
        Assert.Equal("2", junit.Root.Attribute("failures")!.Value);
        Assert.Contains(junit.Descendants("failure"), f => f.Attribute("message")!.Value == "expected 1 to equal 2");

        var json = JsonNode.Parse(ReportWriters.Json(report))!;
        Assert.Equal(2, json["totals"]!["failedRequests"]!.GetValue<int>());

        var html = ReportWriters.Html(report);
        Assert.Contains("A &lt;b&gt;", html);
        Assert.Contains("FAILED", html);
    }

    [Fact]
    public void Data_files_parse_csv_quoting_semicolons_and_json()
    {
        var csv = DataFile.ParseCsv("a;b\r\n1;\"x;\"\"y\"\"\"\r\n");
        Assert.Equal("x;\"y\"", csv[0]["b"]);

        var json = DataFile.ParseJson("""[{"id":1,"tags":["a"],"name":"n"}]""");
        Assert.Equal("1", json[0]["id"]);
        Assert.Equal("""["a"]""", json[0]["tags"]);
    }
}
