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
