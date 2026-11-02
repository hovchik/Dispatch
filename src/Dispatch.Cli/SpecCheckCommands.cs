using System.Globalization;
using System.Text.Json.Nodes;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Interop;
using Dispatch.Application.Running;
using Dispatch.Application.Spec;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Interop;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

/// <summary><c>dispatch coverage</c>: how much of an OpenAPI spec a collection, a run or captured traffic exercises.</summary>
public static class CoverageCommand
{
    private const string Usage = "Usage: dispatch coverage <spec.yaml|url> <collection|run.json|traffic.har> [--run] [--env e] [--requests <collection>] " +
                                 "[--min 80] [-r cli,html,json] [-o dir] [--insecure]";

    private static readonly HashSet<string> Flags = ["run", "insecure", "no-color"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["e"] = "env", ["o"] = "out", ["r"] = "reporter" });
        if (args.Positionals.Count != 2)
            throw new UsageException(Usage);
        SingleRequest.ValidateReporters(args);
        double? minimum = null;
        if (args.Option("min") is { } minText)
        {
            if (!double.TryParse(minText.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var min) || min is < 0 or > 100)
                throw new UsageException($"--min expects a percentage between 0 and 100, got '{minText}'.");
            minimum = min;
        }

        await using var services = Program.BuildServices(args.Option("db"));
        var workspace = new Workspace(services);
        var spec = await SpecLoader.LoadAsync(args.Positionals[0], services.GetRequiredService<IHttpClientSource>(), cancellationToken);
        var source = args.Positionals[1];

        IReadOnlyList<CoverageSample> samples;
        string sourceName;
        var json = File.Exists(source) ? TryJson(source) : null;
        if (json is not null && Har.IsHar(json))
        {
            samples = CoverageSamples.FromHar(json);
            sourceName = Path.GetFileName(source);
            Console.WriteLine($"Read {samples.Count} exchange(s) from {sourceName}.");
        }
        else if (json is not null && CoverageSamples.IsRunJson(json))
        {
            RequestCollection? origin = null;
            if (args.Option("requests") is { } requestsSource)
                (origin, _) = await workspace.LoadCollectionAsync(requestsSource, args.Option("collection"));
            samples = CoverageSamples.FromRunJson(json, origin);
            sourceName = Path.GetFileName(source);
            Console.WriteLine($"Read {samples.Count} result(s) from {sourceName}" + (origin is null ? " (pass --requests <collection> to add methods, headers and bodies)." : "."));
        }
        else
        {
            var (collection, bundled) = await workspace.LoadCollectionAsync(source, args.Option("collection"));
            var environment = await workspace.LoadEnvironmentAsync(args.Option("env"), bundled);
            sourceName = collection.Name;
            if (args.Flag("run"))
            {
                var requests = collection.Requests.Where(r => r.Kind == RequestKind.Http).OrderBy(r => r.SortOrder).ToList();
                if (args.Flag("insecure"))
                    requests = requests.Select(r => { var c = r.Clone(); c.Settings.VerifySsl = false; return c; }).ToList();
                Console.WriteLine($"Running {collection.Name} ({requests.Count} request(s))…");
                var report = await services.GetRequiredService<CollectionRunner>().RunAsync(new RunOptions
                {
                    Name = collection.Name, Requests = requests, CollectionRequests = collection.Requests, Environment = environment,
                    CollectionVariables = collection.Variables, CollectionSpec = collection.SpecLocation, RecordHistory = false,
                    Snapshots = Dispatch.Application.Testing.SnapshotMode.Verify
                }, cancellationToken: cancellationToken);
                samples = CoverageSamples.FromRun(report);
                sourceName += " (run)";
            }
            else
            {
                samples = CoverageSamples.FromCollection(collection, environment);
            }
        }

        var coverage = SpecCoverage.Analyze(spec, samples, sourceName);
        if (args.Options("reporter").DefaultIfEmpty("cli").Contains("cli", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine(CoverageReportWriter.Text(coverage, SingleRequest.Color(args), minimum));
        }
        await SingleRequest.WriteReportsAsync(args, $"{DispatchFormat.Slug(spec.Title)}-coverage-{coverage.CreatedAt:yyyyMMdd-HHmmss}",
            reporter => reporter switch
            {
                "html" => (".html", CoverageReportWriter.Html(coverage, minimum)),
                "json" => (".json", CoverageReportWriter.Json(coverage, minimum)),
                _ => null
            });
        return minimum is { } m && !coverage.Passes(m) ? Program.ExitTestsFailed : Program.ExitOk;
    }

    internal static JsonNode? TryJson(string path)
    {
        try
        {
            var text = File.ReadAllText(path).TrimStart();
            return text.StartsWith('{') ? JsonNode.Parse(text) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary><c>dispatch breaking</c>: classify the differences between two spec versions and what they break in a collection.</summary>
public static class BreakingCommand
{
    private const string Usage = "Usage: dispatch breaking <old.yaml|url> <new.yaml|url> [--collection c] [--fail-on breaking|any|never] [-r cli,html,json] [-o dir]";

    private static readonly HashSet<string> Flags = ["no-color"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["o"] = "out", ["r"] = "reporter", ["c"] = "collection" });
        if (args.Positionals.Count != 2)
            throw new UsageException(Usage);
        SingleRequest.ValidateReporters(args);
        var failOn = (args.Option("fail-on") ?? "breaking").ToLowerInvariant();
        if (failOn is not ("breaking" or "any" or "never"))
            throw new UsageException($"--fail-on expects breaking, any or never, got '{failOn}'.");

        await using var services = Program.BuildServices(args.Option("db"));
        var clients = services.GetRequiredService<IHttpClientSource>();
        var before = await SpecLoader.LoadAsync(args.Positionals[0], clients, cancellationToken);
        var after = await SpecLoader.LoadAsync(args.Positionals[1], clients, cancellationToken);
        var report = SpecDiff.Compare(before, after, Name(args.Positionals[0], before), Name(args.Positionals[1], after));

        if (args.Option("collection") is { } source)
        {
            var workspace = new Workspace(services);
            var (collection, _) = await workspace.LoadCollectionAsync(source, null);
            SpecDiff.AddImpact(report, before, collection, await FlowsAsync(services, collection));
        }

        if (args.Options("reporter").DefaultIfEmpty("cli").Contains("cli", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(SpecDiffReportWriter.Text(report, SingleRequest.Color(args)));
        }
        await SingleRequest.WriteReportsAsync(args, $"{DispatchFormat.Slug(after.Title)}-breaking-{report.CreatedAt:yyyyMMdd-HHmmss}",
            reporter => reporter switch
            {
                "html" => (".html", SpecDiffReportWriter.Html(report)),
                "json" => (".json", SpecDiffReportWriter.Json(report)),
                _ => null
            });

        var failed = failOn switch
        {
            "breaking" => report.HasBreaking,
            "any" => report.Changes.Any(c => c.Severity != ChangeSeverity.Info),
            _ => false
        };
        return failed ? Program.ExitTestsFailed : Program.ExitOk;
    }

    private static string Name(string location, SpecDocument spec)
    {
        var file = File.Exists(location) ? Path.GetFileName(location) : location;
        return spec.Version.Length > 0 ? $"{file} ({spec.Version})" : file;
    }

    /// <summary>Flows saved in the app for this collection (none for file-only collections or a fresh database).</summary>
    private static async Task<IReadOnlyList<TestFlow>> FlowsAsync(IServiceProvider services, RequestCollection collection)
    {
        try
        {
            await services.GetRequiredService<Dispatch.Infrastructure.Persistence.DatabaseInitializer>().InitializeAsync();
            return (await services.GetRequiredService<IFlowRepository>().GetAllAsync()).Where(f => f.CollectionId == collection.Id).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }
    }
}
