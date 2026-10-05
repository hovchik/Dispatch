using Dispatch.Application.Impact;
using Dispatch.Domain;

namespace Dispatch.Tests;

public class JsonShapeTests
{
    [Theory]
    [InlineData("$.user.id", new[] { "user", "id" })]
    [InlineData("$.items[0].sku", new[] { "items", "*", "sku" })]
    [InlineData("$['odd key'][\"x\"]", new[] { "odd key", "x" })]
    [InlineData("$..id", new[] { "**", "id" })]
    [InlineData("$.items[?(@.price > 5)].sku", new[] { "items", "*", "sku" })]
    [InlineData("$.*.id", new[] { "*", "id" })]
    public void Splits_jsonpaths_into_segments(string path, string[] expected) =>
        Assert.Equal(expected, JsonShape.Split(path));

    [Theory]
    [InlineData("$.user.id", "$.user.id", "Inside")]
    [InlineData("$.user.id.value", "$.user.id", "Inside")]
    [InlineData("$.user", "$.user.id", "Ancestor")]
    [InlineData("$.items[3].sku", "$.items[*].sku", "Inside")]
    [InlineData("$..id", "$.user.id", "Inside")]
    [InlineData("$.*.id", "$.user.id", "Inside")]
    [InlineData("$.user.name", "$.user.id", "None")]
    [InlineData("$.users", "$.user.id", "None")]
    public void Relates_reference_paths_to_changed_paths(string reference, string changed, string overlap) =>
        Assert.Equal(overlap, JsonShape.Relate(JsonShape.Split(reference), JsonShape.Split(changed)).ToString());

    [Fact]
    public void Diff_finds_renames_removals_type_changes_and_additions()
    {
        var changes = JsonShape.Diff(ImpactScenario.Before, ImpactScenario.After);

        Assert.Contains(changes, c => c is { Kind: ShapeChangeKind.Renamed, Path: "$.user.id", NewPath: "$.user.userId" });
        Assert.Contains(changes, c => c is { Kind: ShapeChangeKind.Removed, Path: "$.user.email", OldType: "string" });
        Assert.Contains(changes, c => c is { Kind: ShapeChangeKind.TypeChanged, Path: "$.items[*].price", OldType: "number", NewType: "string" });
        Assert.Contains(changes, c => c is { Kind: ShapeChangeKind.Added, Path: "$.currency" });
        Assert.Equal(4, changes.Count);
    }

    [Fact]
    public void Diff_reports_only_the_top_of_a_removed_subtree_and_detects_moves_by_value()
    {
        var changes = JsonShape.Diff("""{"meta":{"a":1,"b":2},"ref":"ORD-123"}""", """{"order":{"ref":"ORD-123"}}""");
        Assert.Contains(changes, c => c is { Kind: ShapeChangeKind.Removed, Path: "$.meta" });
        Assert.DoesNotContain(changes, c => c.Path == "$.meta.a");
        Assert.Contains(changes, c => c is { Kind: ShapeChangeKind.Renamed, Path: "$.ref", NewPath: "$.order.ref" });
    }

    [Fact]
    public void Diff_ignores_values_and_non_json()
    {
        Assert.Empty(JsonShape.Diff("""{"a":1,"b":[1,2]}""", """{"b":[5],"a":2}"""));
        Assert.Empty(JsonShape.Diff("<xml/>", """{"a":1}"""));
        // Becoming nullable is not a type change.
        Assert.Empty(JsonShape.Diff("""{"a":"x"}""", """{"a":null}"""));
    }
}

internal static class ImpactScenario
{
    public const string Before = """
        {"user":{"id":7,"name":"Ann","email":"ann@example.com"},"items":[{"sku":"A1","price":10}],"total":10}
        """;

    public const string After = """
        {"user":{"userId":7,"name":"Ann"},"items":[{"sku":"A1","price":"10.00"}],"total":10,"currency":"EUR"}
        """;

    public static (ApiRequest GetUser, List<ApiRequest> All, List<TestFlow> Flows) Build()
    {
        var getUser = new ApiRequest
        {
            Name = "Get user", Url = "{{base}}/me",
            Assertions =
            [
                new Assertion { Source = ValueSource.JsonPath, Path = "$.user.id", Operator = AssertionOperator.Equals, Expected = "7" },
                new Assertion { Source = ValueSource.JsonPath, Path = "$.items[0].price", Operator = AssertionOperator.GreaterThan, Expected = "5" },
                new Assertion { Source = ValueSource.JsonPath, Path = "$.user", Operator = AssertionOperator.Exists },
                new Assertion { Source = ValueSource.JsonPath, Path = "$.total", Operator = AssertionOperator.Equals, Expected = "10" },
                new Assertion { Source = ValueSource.JsonPath, Path = "$.user.email", Operator = AssertionOperator.NotExists },
                new Assertion { Source = ValueSource.Snapshot, Path = "$.total", Expected = Before },
                new Assertion { Source = ValueSource.Status, Expected = "200" }
            ],
            Extractions =
            [
                new ExtractionRule { Variable = "userId", Path = "$.user.id" },
                new ExtractionRule { Variable = "email", Path = "$.user.email" },
                new ExtractionRule { Variable = "name", Path = "$.user.name" }
            ],
            TestScript = "const json = pm.response.json();\npm.test('mail', () => pm.expect(json.user.email).to.include('@'));",
            Examples = [new ResponseExample { Name = "Ann", Body = Before }]
        };
        var update = new ApiRequest
        {
            Name = "Update user", Method = HttpVerb.Put, Url = "{{base}}/users/{{userId}}",
            Extractions = [new ExtractionRule { Variable = "etag", Source = ValueSource.Header, Path = "ETag" }]
        };
        var audit = new ApiRequest { Name = "Audit", Url = "{{base}}/audit", Headers = [new("If-Match", "{{etag}}")] };
        var mail = new ApiRequest { Name = "Send mail", PreRequestScript = "const to = pm.environment.get('email');" };
        var greet = new ApiRequest { Name = "Greet", Url = "{{base}}/hello/{{name}}" };
        var health = new ApiRequest { Name = "Health", Url = "{{base}}/health" };

        var onboarding = new TestFlow
        {
            Name = "Onboarding",
            Steps =
            [
                new FlowStep { Type = FlowStepType.Request, RequestId = getUser.Id },
                new FlowStep
                {
                    Type = FlowStepType.If, Name = "Has mail", Condition = new FlowCondition { Left = "{{email}}", Comparison = FlowComparison.IsTruthy },
                    Children = [new FlowStep { Type = FlowStepType.Request, RequestId = mail.Id }]
                }
            ]
        };
        var smoke = new TestFlow { Name = "Smoke", Steps = [new FlowStep { Type = FlowStepType.Request, RequestId = health.Id }] };
        return (getUser, [getUser, update, audit, mail, greet, health], [onboarding, smoke]);
    }
}

public class ImpactAnalyzerTests
{
    private static ImpactReport Analyze()
    {
        var (getUser, all, flows) = ImpactScenario.Build();
        return ImpactAnalyzer.Analyze(getUser, ImpactScenario.Before, ImpactScenario.After, all, flows, "snapshot");
    }

    [Fact]
    public void Finds_broken_assertions_with_fix_suggestions()
    {
        var report = Analyze();
        var assertions = report.Items.Where(i => i.Kind == ImpactKind.Assertion).ToList();

        var id = Assert.Single(assertions, i => i.Location.Contains("$.user.id"));
        Assert.Equal(ImpactSeverity.Breaks, id.Severity);
        Assert.Equal("Change the path to $.user.userId.", id.Suggestion);
        Assert.Contains(assertions, i => i.Location.Contains("$.items[0].price") && i.Severity == ImpactSeverity.Breaks && i.Detail.Contains("now string"));
        // Exists on the parent, an unrelated field, and NotExists on a removed field are all unaffected.
        Assert.DoesNotContain(assertions, i => i.Location.Contains("$.total") || i.Location.EndsWith("$.user exists") || i.Location.Contains("does not exist"));
        Assert.Equal(2, assertions.Count);
        Assert.Contains(report.Items, i => i.Kind == ImpactKind.Snapshot && i.Severity == ImpactSeverity.Breaks);
    }

    [Fact]
    public void Follows_broken_variables_to_requests_scripts_and_chained_extractions()
    {
        var report = Analyze();

        Assert.Equal(["email", "etag", "userId"], report.BrokenVariables.Keys.Order());
        Assert.Contains(report.Items, i => i is { Kind: ImpactKind.VariableUse, Owner: "Update user", Location: "URL", Severity: ImpactSeverity.Breaks });
        Assert.Contains(report.Items, i => i is { Kind: ImpactKind.VariableUse, Owner: "Send mail", Location: "Pre-request script", Severity: ImpactSeverity.Breaks });
        var chained = Assert.Single(report.Items, i => i.Owner == "Audit");
        Assert.Equal(ImpactSeverity.Possible, chained.Severity);
        Assert.Contains("(chained)", chained.Detail);
        Assert.DoesNotContain(report.Items, i => i.Owner is "Greet" or "Health");
    }

    [Fact]
    public void Flags_scripts_examples_and_flows()
    {
        var report = Analyze();

        var script = Assert.Single(report.Items, i => i.Kind == ImpactKind.Script);
        Assert.Equal("Test script · line 2", script.Location);
        Assert.Contains(report.Items, i => i.Kind == ImpactKind.Example && i.Location == "Examples · Ann");

        var flow = Assert.Single(report.Items, i => i.Kind == ImpactKind.Flow);
        Assert.Equal("Onboarding", flow.Owner);
        Assert.Equal(ImpactSeverity.Breaks, flow.Severity);
        Assert.Contains("runs Get user", flow.Detail);
        Assert.Contains("step \"Has mail\" uses {{email}}", flow.Detail);
        Assert.Contains("runs Send mail", flow.Detail);
    }

    [Fact]
    public void Summarises_the_blast_radius()
    {
        var report = Analyze();
        Assert.True(report.HasBreakingImpact);
        Assert.Equal("3 structural changes break 3 tests, 2 extractions, 2 requests and 1 flow (3 more to check).", report.Summary);
    }

    [Fact]
    public void Single_change_summary_names_the_change()
    {
        var (getUser, all, flows) = ImpactScenario.Build();
        var report = ImpactAnalyzer.Analyze(getUser, """{"user":{"id":7}}""", """{"user":{"userId":7}}""", all, flows);
        Assert.StartsWith("Renamed $.user.id → $.user.userId breaks 2 tests, 1 extraction, 1 request and 1 flow", report.Summary);
    }

    [Fact]
    public void Additions_alone_have_no_impact()
    {
        var (getUser, all, flows) = ImpactScenario.Build();
        var report = ImpactAnalyzer.Analyze(getUser, ImpactScenario.Before, ImpactScenario.Before.Replace("\"total\":10", "\"total\":10,\"x\":1"), all, flows);
        Assert.Empty(report.Items);
        Assert.False(report.HasBreakingImpact);
        Assert.StartsWith("Only additions", report.Summary);
    }
}

[Collection("Console")]
public class CliImpactTests
{
    [Fact]
    public async Task Impact_command_compares_the_live_response_with_the_snapshot()
    {
        await using var server = await TestServer.StartAsync(app =>
            Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(app, "/me",
                () => Microsoft.AspNetCore.Http.Results.Text(ImpactScenario.After, "application/json")));
        var (getUser, all, _) = ImpactScenario.Build();
        foreach (var r in all)
            r.Url = r.Url.Replace("{{base}}", server.BaseUrl);
        var dir = Cli.TempDir();
        var file = Path.Combine(dir, "users.dispatch.json");
        await File.WriteAllTextAsync(file, Application.Interop.DispatchFormat.ExportCollection(new RequestCollection { Name = "Users", Requests = all }));

        var (exit, output) = await Cli.RunAsync("impact", file, "--request", getUser.Name, "--db", Path.Combine(dir, "db"), "--no-color",
            "-r", "cli,json", "-o", dir);

        Assert.Equal(1, exit);
        Assert.Contains("compared with snapshot", output);
        Assert.Contains("- renamed $.user.id → $.user.userId", output);
        Assert.Contains("→ Change the path to $.user.userId.", output);
        Assert.Contains("Update user · URL", output);
        Assert.Contains("\"breaking\": true", await File.ReadAllTextAsync(Directory.GetFiles(dir, "get-user-impact-*.json").Single()));

        // Offline: compare two saved bodies, and don't fail the build.
        var current = Path.Combine(dir, "current.json");
        await File.WriteAllTextAsync(current, ImpactScenario.Before);
        var (same, sameOutput) = await Cli.RunAsync("impact", file, "--request", getUser.Name, "--db", Path.Combine(dir, "db"),
            "--current", current, "--baseline", "example:Ann", "--fail-on", "never");
        Assert.Equal(0, same);
        Assert.Contains("No structural change.", sameOutput);
    }
}
