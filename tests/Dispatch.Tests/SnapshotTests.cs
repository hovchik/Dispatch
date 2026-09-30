using Dispatch.Application.Requests;
using Dispatch.Application.Running;
using Dispatch.Application.Testing;
using Dispatch.Domain;

namespace Dispatch.Tests;

public class SnapshotTests
{
    private static ApiResponse Json(string body) => new() { StatusCode = 200, Body = body, ContentType = "application/json" };

    private static ApiRequest SnapshotRequest(string expected = "", string ignore = "") => new()
    {
        Url = "http://x/users/1",
        Assertions = [new Assertion { Source = ValueSource.Snapshot, Path = ignore, Expected = expected }]
    };

    [Fact]
    public void Json_is_compared_structurally_with_ignore_paths()
    {
        var snapshot = Snapshots.Capture(Json("""{"id":1,"name":"Ann","createdAt":"2020","tags":["a"]}"""));
        Assert.Empty(Snapshots.Compare(snapshot, Json("""{"tags":["a"],"createdAt":"2024","name":"Ann","id":1}"""), "$.createdAt"));

        var diff = Snapshots.Compare(snapshot, Json("""{"id":1,"name":"Bob","createdAt":"2020","tags":["a","b"]}"""), "");
        Assert.Contains(diff, d => d.Contains("$.name") && d.Contains("Bob"));
        Assert.Contains(diff, d => d.StartsWith("+ $.tags[1]"));
    }

    [Fact]
    public void Text_is_compared_line_by_line()
    {
        var response = new ApiResponse { Body = "line 1\nline 2" };
        Assert.Empty(Snapshots.Compare("line 1\r\nline 2", response, null));
        Assert.Equal(["- line 2", "+ line two"], Snapshots.Compare("line 1\nline 2", new ApiResponse { Body = "line 1\nline two" }, null));
    }

    [Fact]
    public async Task Missing_snapshot_is_recorded_then_later_responses_are_compared()
    {
        var body = """{"id":1,"at":"t1"}""";
        var sender = new RequestSender([new FakeExecutor(_ => Json(body))], new NullHistory());
        var request = SnapshotRequest(ignore: "$.at");

        var first = await sender.SendAsync(request, new SendOptions(), CancellationToken.None);
        Assert.True(first.AllTestsPassed);
        var recorded = Assert.Single(first.SnapshotUpdates);
        Assert.Equal(0, recorded.Key);
        Assert.Contains("\"id\": 1", recorded.Value);

        request.Assertions[0].Expected = recorded.Value;
        body = """{"id":1,"at":"t2"}""";
        var second = await sender.SendAsync(request, new SendOptions(), CancellationToken.None);
        Assert.True(second.AllTestsPassed);
        Assert.Empty(second.SnapshotUpdates);

        body = """{"id":2,"at":"t3"}""";
        var third = await sender.SendAsync(request, new SendOptions(), CancellationToken.None);
        var failed = Assert.Single(third.TestResults);
        Assert.False(failed.Passed);
        Assert.Contains("$.id", failed.Message);
    }

    [Fact]
    public async Task Verify_mode_fails_on_missing_snapshot_and_update_mode_overwrites()
    {
        var sender = new RequestSender([new FakeExecutor(_ => Json("""{"v":2}"""))], new NullHistory());

        var verify = await sender.SendAsync(SnapshotRequest(), new SendOptions { Snapshots = SnapshotMode.Verify }, CancellationToken.None);
        Assert.False(verify.AllTestsPassed);
        Assert.Contains("--update-snapshots", verify.TestResults[0].Message);

        var update = await sender.SendAsync(SnapshotRequest(expected: """{"v":1}"""), new SendOptions { Snapshots = SnapshotMode.Update },
            CancellationToken.None);
        Assert.True(update.AllTestsPassed);
        Assert.Contains("\"v\": 2", update.SnapshotUpdates[0]);
    }

    [Fact]
    public async Task Snapshots_with_braces_are_not_treated_as_variables()
    {
        var sender = new RequestSender([new FakeExecutor(_ => new ApiResponse { StatusCode = 200, Body = "Hello {{name}}" })], new NullHistory());
        var response = await sender.SendAsync(SnapshotRequest(expected: "Hello {{name}}"),
            new SendOptions { Environment = new ApiEnvironment { Variables = [new("name", "Bob")] } }, CancellationToken.None);
        Assert.True(response.AllTestsPassed, response.TestResults[0].Message);
    }

    [Fact]
    public async Task Runner_collects_updates_and_later_iterations_use_them_without_touching_the_input()
    {
        var n = 0;
        var sender = new RequestSender([new FakeExecutor(_ => Json($$"""{"n":{{(n++ == 0 ? 1 : 1)}}}"""))], new NullHistory());
        var request = SnapshotRequest();
        var report = await new CollectionRunner(sender).RunAsync(new RunOptions { Requests = [request], Iterations = 3 });

        Assert.True(report.Passed);
        Assert.Single(report.SnapshotUpdates[request.Id]);
        Assert.Equal("", request.Assertions[0].Expected);
    }
}
