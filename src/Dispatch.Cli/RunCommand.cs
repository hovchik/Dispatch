using Dispatch.Application.Running;
using Dispatch.Application.Testing;
using Dispatch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

public static class RunCommand
{
    private static readonly HashSet<string> Flags = ["bail", "insecure", "save-env", "no-color", "silent", "update-snapshots"];

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["e"] = "env", ["d"] = "data", ["n"] = "iterations", ["r"] = "reporter", ["o"] = "out", ["environment"] = "env"
    };

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, Aliases);
        if (args.Positionals.Count != 1)
            throw new UsageException("Usage: dispatch run <collection> [options]. Run 'dispatch --help' for options.");

        await using var services = Program.BuildServices(args.Option("db"));
        var workspace = new Workspace(services);
        var (collection, bundled) = await workspace.LoadCollectionAsync(args.Positionals[0], args.Option("collection"));
        var environment = await workspace.LoadEnvironmentAsync(args.Option("env"), bundled);
        var requests = Program.Filter(collection, args);

        // --var overrides go into (a copy of) the environment so they win over collection variables.
        var runEnvironment = environment is null ? new ApiEnvironment { Name = "(cli)" } : Copy(environment);
        foreach (var assignment in args.RawOptions("var"))
        {
            var eq = assignment.IndexOf('=');
            if (eq <= 0)
                throw new UsageException($"--var expects key=value, got '{assignment}'.");
            runEnvironment.SetVariable(assignment[..eq], assignment[(eq + 1)..]);
        }

        var timeout = args.Int("timeout", 0);
        var insecure = args.Flag("insecure");
        if (timeout > 0 || insecure)
        {
            requests = requests.Select(r =>
            {
                var copy = r.Clone();
                if (timeout > 0 && copy.Settings.TimeoutMs == 0)
                    copy.Settings.TimeoutMs = timeout;
                if (insecure)
                    copy.Settings.VerifySsl = false;
                return copy;
            }).ToList();
        }

        var data = args.Option("data") is { } dataPath ? DataFile.Load(dataPath) : [];
        var reporters = args.Options("reporter").Select(r => r.ToLowerInvariant()).DefaultIfEmpty("cli").ToHashSet();
        var unknown = reporters.Except(["cli", "junit", "html", "json"]).ToList();
        if (unknown.Count > 0)
            throw new UsageException($"Unknown reporter(s): {string.Join(", ", unknown)}. Use cli, junit, html, json.");

        var color = !args.Flag("no-color") && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        var silent = args.Flag("silent");
        if (!silent)
            Console.WriteLine($"{collection.Name}: {requests.Count} request(s)" +
                              (data.Count > 0 ? $" × {data.Count} data row(s)" : args.Int("iterations", 1) > 1 ? $" × {args.Int("iterations", 1)} iterations" : "") +
                              (environment is null ? "" : $" · environment {environment.Name}"));

        var progress = new ConsoleProgress(data.Count > 0 || args.Int("iterations", 1) > 1, color, silent || !reporters.Contains("cli"));
        var runner = services.GetRequiredService<CollectionRunner>();
        var report = await runner.RunAsync(new RunOptions
        {
            Name = collection.Name,
            Requests = requests,
            Environment = runEnvironment,
            CollectionVariables = collection.Variables,
            CollectionSpec = collection.SpecLocation,
            Iterations = args.Int("iterations", 1),
            Data = data,
            DelayMs = args.Int("delay", 0),
            StopOnFailure = args.Flag("bail"),
            Snapshots = args.Flag("update-snapshots") ? SnapshotMode.Update : SnapshotMode.Verify
        }, progress, cancellationToken);

        if (reporters.Contains("cli") && !silent)
        {
            Console.WriteLine();
            Console.WriteLine(Summary(report, color));
        }

        var outDir = args.Option("out") ?? "dispatch-reports";
        var stamp = report.StartedAt.ToString("yyyyMMdd-HHmmss");
        var baseName = $"{Dispatch.Application.Interop.DispatchFormat.Slug(collection.Name)}-{stamp}";
        foreach (var reporter in reporters.Where(r => r != "cli"))
        {
            Directory.CreateDirectory(outDir);
            var (extension, content) = reporter switch
            {
                "junit" => (".xml", ReportWriters.JUnit(report)),
                "html" => (".html", ReportWriters.Html(report)),
                _ => (".json", ReportWriters.Json(report))
            };
            var path = Path.Combine(outDir, baseName + extension);
            await File.WriteAllTextAsync(path, content, CancellationToken.None);
            if (!silent)
                Console.WriteLine($"{reporter} report: {Path.GetFullPath(path)}");
        }

        if (args.Flag("save-env") && environment is not null && report.EnvironmentUpdates.Count > 0)
        {
            foreach (var (k, v) in report.EnvironmentUpdates)
                environment.SetVariable(k, v);
            await workspace.SaveEnvironmentAsync(environment);
            if (!silent)
                Console.WriteLine($"Saved {report.EnvironmentUpdates.Count} variable(s) to environment {environment.Name}.");
        }

        if (report.SnapshotUpdates.Count > 0)
        {
            foreach (var request in collection.Requests)
                if (report.SnapshotUpdates.TryGetValue(request.Id, out var snapshots))
                    foreach (var (index, snapshot) in snapshots)
                        request.Assertions[index].Expected = snapshot;
            var where = await workspace.SaveCollectionAsync(collection, args.Positionals[0], bundled, report.SnapshotUpdates.Keys);
            if (!silent)
                Console.WriteLine($"Updated snapshots of {report.SnapshotUpdates.Count} request(s) in {where}.");
        }

        return report.Passed && !report.Stopped ? Program.ExitOk : Program.ExitTestsFailed;
    }

    private static ApiEnvironment Copy(ApiEnvironment e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Variables = e.Variables.Select(v => v.Clone()).ToList()
    };

    private static string Summary(RunReport report, bool color)
    {
        string Paint(string text, string code) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
        var requests = report.FailedRequests == 0
            ? Paint($"{report.TotalRequests} passed", "32")
            : Paint($"{report.FailedRequests} of {report.TotalRequests} failed", "31");
        var tests = report.TotalTests == 0 ? "no tests"
            : report.FailedTests == 0 ? Paint($"{report.TotalTests} tests passed", "32")
            : Paint($"{report.FailedTests} of {report.TotalTests} tests failed", "31");
        return $"Requests: {requests} · {tests} · avg {report.AverageResponseTime.TotalMilliseconds:0} ms · total {report.Duration.TotalSeconds:0.00} s" +
               (report.Stopped ? Paint(" · stopped early", "33") : "");
    }

    /// <summary>Prints each request as it completes.</summary>
    private sealed class ConsoleProgress(bool showIteration, bool color, bool quiet) : IProgress<RequestRunResult>
    {
        private readonly Lock _gate = new();

        public void Report(RequestRunResult r)
        {
            if (quiet)
                return;
            string Paint(string text, string code) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
            lock (_gate)
            {
                var response = r.Response;
                var mark = r.Passed ? Paint("✓", "32") : Paint("✗", "31");
                var iteration = showIteration ? Paint($"[{r.Iteration + 1}] ", "90") : "";
                var status = response.HasResponse ? $"{response.StatusCode} {response.ReasonPhrase}".Trim() : $"error: {response.Error}";
                Console.WriteLine($"{mark} {iteration}{r.Name}  {Paint(r.Request.Kind.ToString().ToUpperInvariant(), "36")} " +
                                  $"{Paint(status, response.IsSuccess ? "90" : "33")}  {response.Elapsed.TotalMilliseconds:0} ms");
                foreach (var test in response.TestResults)
                {
                    if (test.Passed)
                        Console.WriteLine($"    {Paint("✓", "32")} {test.Name}");
                    else
                        Console.WriteLine($"    {Paint("✗", "31")} {test.Name}{(test.Message is null ? "" : Paint($" — {test.Message}", "90"))}");
                }
                foreach (var line in response.ScriptLog)
                    Console.WriteLine($"    {Paint("›", "90")} {line}");
            }
        }
    }
}
