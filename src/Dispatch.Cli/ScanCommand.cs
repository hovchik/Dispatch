using Dispatch.Application.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

public static class ScanCommand
{
    private static readonly HashSet<string> Flags = ["passive-only", "active-only", "no-injection", "no-auth", "no-boundaries", "insecure", "no-color"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["e"] = "env", ["o"] = "out", ["r"] = "reporter" });
        if (args.Positionals.Count != 1)
            throw new UsageException("Usage: dispatch scan <collection> [--env e] [--passive-only|--active-only] [--no-injection] " +
                                     "[--no-auth] [--no-boundaries] [--max-probes n] [--fail-on high|medium|low] [-r cli,html,json] [-o dir] [--insecure]");

        // Validate the options that decide the outcome before doing any work.
        var reporters = args.Options("reporter").Select(r => r.ToLowerInvariant()).DefaultIfEmpty("cli").ToHashSet();
        if (reporters.Except(["cli", "html", "json"]).ToList() is { Count: > 0 } unknown)
            throw new UsageException($"Unknown reporter(s): {string.Join(", ", unknown)}. Use cli, html or json.");
        var failOn = args.Option("fail-on")?.ToLowerInvariant() switch
        {
            "high" => ScanSeverity.High,
            "medium" => ScanSeverity.Medium,
            "low" => ScanSeverity.Low,
            null => (ScanSeverity?)null,
            var other => throw new UsageException($"--fail-on expects high, medium or low, got '{other}'.")
        };

        await using var services = Program.BuildServices(args.Option("db"));
        var workspace = new Workspace(services);
        var (collection, bundled) = await workspace.LoadCollectionAsync(args.Positionals[0], args.Option("collection"));
        var environment = await workspace.LoadEnvironmentAsync(args.Option("env"), bundled);
        var requests = Program.Filter(collection, args);
        if (args.Flag("insecure"))
            requests = requests.Select(r => { var c = r.Clone(); c.Settings.VerifySsl = false; return c; }).ToList();

        var options = new ScanOptions
        {
            Passive = !args.Flag("active-only"),
            Active = !args.Flag("passive-only"),
            CheckInjection = !args.Flag("no-injection"),
            CheckAuth = !args.Flag("no-auth"),
            CheckBoundaries = !args.Flag("no-boundaries"),
            MaxProbesPerRequest = args.Int("max-probes", 40)
        };

        var color = !args.Flag("no-color") && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        Console.WriteLine($"Scanning {collection.Name}: {requests.Count} request(s)" +
                          (options.Active ? " · active probes" : " · passive only"));
        Console.WriteLine("Only scan APIs you are authorised to test.");

        var scanner = services.GetRequiredService<SecurityScanner>();
        var report = await scanner.ScanAsync(requests, options, environment, collection.Variables, cancellationToken: cancellationToken);

        if (reporters.Contains("cli"))
        {
            Console.WriteLine();
            Console.WriteLine(ScanReportWriter.Text(report, color));
        }

        var outDir = args.Option("out") ?? "dispatch-reports";
        var baseName = $"{Dispatch.Application.Interop.DispatchFormat.Slug(collection.Name)}-scan-{report.StartedAt:yyyyMMdd-HHmmss}";
        foreach (var reporter in reporters.Where(r => r != "cli"))
        {
            Directory.CreateDirectory(outDir);
            var (ext, content) = reporter switch
            {
                "html" => (".html", ScanReportWriter.Html(report, collection.Name)),
                "json" => (".json", ScanReportWriter.Json(report)),
                _ => throw new UsageException($"Unknown reporter '{reporter}'. Use cli, html or json.")
            };
            var path = Path.Combine(outDir, baseName + ext);
            await File.WriteAllTextAsync(path, content, CancellationToken.None);
            Console.WriteLine($"{reporter} report: {Path.GetFullPath(path)}");
        }

        return failOn is { } threshold && report.HasFindingsAtOrAbove(threshold) ? Program.ExitTestsFailed : Program.ExitOk;
    }
}
