using System.Text;
using Dispatch.Application.Interop;
using Dispatch.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dispatch.Tests;

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;

/// <summary>Runs the CLI in-process, capturing its output.</summary>
internal static class Cli
{
    public static async Task<(int Exit, string Output)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exit = await Dispatch.Cli.Program.Main(args);
            return (exit, output.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dispatch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

[Collection("Console")]
public class CliSnapshotTests
{
    [Fact]
    public async Task Update_snapshots_writes_the_file_and_later_runs_verify_against_it()
    {
        var name = "Ann";
        await using var server = await TestServer.StartAsync(app =>
            app.MapGet("/user", () => Results.Json(new { id = 1, name, at = DateTime.UtcNow.Ticks })));
        var dir = Cli.TempDir();
        var file = Path.Combine(dir, "api.dispatch.json");
        var collection = new RequestCollection
        {
            Name = "Snap",
            Requests = [new ApiRequest
            {
                Name = "user", Url = server.BaseUrl + "/user",
                Assertions = [new Assertion { Source = ValueSource.Snapshot, Path = "$.at" }]
            }]
        };
        await File.WriteAllTextAsync(file, DispatchFormat.ExportCollection(collection));
        var db = Path.Combine(dir, "db.sqlite");

        var (missing, missingOutput) = await Cli.RunAsync("run", file, "--db", db, "--no-color");
        Assert.Equal(1, missing);
        Assert.Contains("No snapshot recorded", missingOutput);

        var (updated, updateOutput) = await Cli.RunAsync("run", file, "--db", db, "--no-color", "--update-snapshots");
        Assert.Equal(0, updated);
        Assert.Contains("Updated snapshots of 1 request(s)", updateOutput);
        Assert.Contains("Ann", await File.ReadAllTextAsync(file));

        Assert.Equal(0, (await Cli.RunAsync("run", file, "--db", db)).Exit);

        name = "Bob";
        var (changed, changedOutput) = await Cli.RunAsync("run", file, "--db", db, "--no-color");
        Assert.Equal(1, changed);
        Assert.Contains("$.name", changedOutput);
    }
}

/// <summary>Option validation that must happen before any (slow or side-effecting) work, and error reporting of setup failures.</summary>
[Collection("Console")]
public class CliOptionValidationTests
{
    [Fact]
    public async Task Scan_rejects_an_unknown_fail_on_before_loading_the_collection()
    {
        // The collection does not exist: the --fail-on message proves validation ran first.
        var (exit, output) = await Cli.RunAsync("scan", Path.Combine(Cli.TempDir(), "missing.dispatch.json"), "--fail-on", "critical");
        Assert.Equal(Dispatch.Cli.Program.ExitError, exit);
        Assert.Contains("--fail-on expects high, medium or low", output);
    }

    [Fact]
    public async Task Impact_and_probes_reject_an_unknown_reporter_before_loading_the_collection()
    {
        var missing = Path.Combine(Cli.TempDir(), "missing.dispatch.json");
        foreach (var command in new[] { "impact", "minimize", "ratelimit", "laws" })
        {
            var (exit, output) = await Cli.RunAsync(command, missing, "-r", "cli,pdf");
            Assert.Equal(Dispatch.Cli.Program.ExitError, exit);
            Assert.Contains("Unknown reporter(s): pdf", output);
        }
        var impact = await Cli.RunAsync("impact", missing, "--fail-on", "always");
        Assert.Equal(Dispatch.Cli.Program.ExitError, impact.Exit);
        Assert.Contains("--fail-on expects breaks, possible or never", impact.Output);
    }

    [Fact]
    public async Task Flow_replay_with_invalid_json_reports_an_error_instead_of_crashing()
    {
        var dir = Cli.TempDir();
        var file = Path.Combine(dir, "api.dispatch.json");
        await File.WriteAllTextAsync(file, DispatchFormat.ExportCollection(new RequestCollection
        {
            Name = "Replay", Requests = [new ApiRequest { Name = "ping", Url = "http://localhost/ping" }]
        }));
        var recording = Path.Combine(dir, "recording.json");
        await File.WriteAllTextAsync(recording, "{ not json");

        var (exit, output) = await Cli.RunAsync("flow", file, "--replay", recording, "--db", Path.Combine(dir, "db"));
        Assert.Equal(Dispatch.Cli.Program.ExitError, exit);
        Assert.StartsWith("error:", output.Trim());
        Assert.DoesNotContain("   at ", output);
    }

    [Fact]
    public async Task Unwritable_database_path_reports_an_error_instead_of_crashing()
    {
        var dir = Cli.TempDir();
        var directoryAsDb = Path.Combine(dir, "not-a-file");
        Directory.CreateDirectory(directoryAsDb);
        var (exit, output) = await Cli.RunAsync("list", "--db", directoryAsDb);
        Assert.Equal(Dispatch.Cli.Program.ExitError, exit);
        Assert.Contains("error: SQLite Error", output);
        Assert.DoesNotContain("   at ", output);
    }
}
