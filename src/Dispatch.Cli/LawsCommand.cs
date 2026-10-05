using System.Text.Json.Nodes;
using Dispatch.Application.Interop;
using Dispatch.Application.Laws;
using Dispatch.Application.Running;
using Dispatch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

/// <summary><c>dispatch laws</c>: infer API laws from captured traffic (HAR) or from running a collection a few times.</summary>
public static class LawsCommand
{
    private const string Usage = "Usage: dispatch laws <traffic.har | collection> [--runs n] [--env e] [--min-samples n] " +
                                 "[--fail-on-anomaly] [--write] [-r cli,html,json] [-o dir] [--insecure]";

    private static readonly HashSet<string> Flags = ["fail-on-anomaly", "write", "insecure", "no-color"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["e"] = "env", ["o"] = "out", ["r"] = "reporter" });
        if (args.Positionals.Count != 1)
            throw new UsageException(Usage);
        var source = args.Positionals[0];
        var options = new LawOptions { MinSamples = Math.Max(2, args.Int("min-samples", 3)) };
        await using var services = Program.BuildServices(args.Option("db"));
        var workspace = new Workspace(services);

        IReadOnlyList<Observation> observations;
        RequestCollection? collection = null;
        string? collectionSource = null;
        IReadOnlyList<ApiEnvironment> bundled = [];
        if (File.Exists(source) && TryHar(source) is { } har)
        {
            observations = Observations.FromHar(har);
            Console.WriteLine($"Read {observations.Count} exchange(s) from {Path.GetFileName(source)}.");
        }
        else
        {
            (collection, bundled) = await workspace.LoadCollectionAsync(source, args.Option("collection"));
            collectionSource = source;
            var environment = await workspace.LoadEnvironmentAsync(args.Option("env"), bundled);
            var runs = Math.Max(1, args.Int("runs", 3));
            var requests = Program.Filter(collection, args);
            if (args.Flag("insecure"))
                requests = requests.Select(r => { var c = r.Clone(); c.Settings.VerifySsl = false; return c; }).ToList();
            Console.WriteLine($"Running {collection.Name} {runs}× ({requests.Count} request(s) each) to observe its API…");
            var report = await services.GetRequiredService<CollectionRunner>().RunAsync(new RunOptions
            {
                Name = collection.Name, Requests = requests, CollectionRequests = collection.Requests, Environment = environment,
                CollectionVariables = collection.Variables, Iterations = runs, RecordHistory = false, Snapshots = Dispatch.Application.Testing.SnapshotMode.Verify
            }, cancellationToken: cancellationToken);
            var at = DateTimeOffset.Now;
            observations = report.Results.Select((r, i) => Observations.FromRun(r, at.AddMilliseconds(i))).OfType<Observation>().ToList();
        }

        var laws = LawMiner.Mine(observations, options, collection?.Name ?? Path.GetFileName(source));
        if (args.Options("reporter").DefaultIfEmpty("cli").Contains("cli", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine(LawReportWriter.Text(laws, SingleRequest.Color(args)));
        }
        await SingleRequest.WriteReportsAsync(args, $"{DispatchFormat.Slug(laws.Source)}-laws-{laws.CreatedAt:yyyyMMdd-HHmmss}",
            reporter => reporter switch
            {
                "html" => (".html", LawReportWriter.Html(laws)),
                "json" => (".json", LawReportWriter.Json(laws)),
                _ => null
            });

        if (args.Flag("write"))
        {
            if (collection is null || collectionSource is null)
                throw new UsageException("--write needs a collection (laws from a HAR file can't be matched to saved requests).");
            var changed = new HashSet<Guid>();
            foreach (var law in laws.Laws.Where(l => !l.IsAnomaly && l.CanBecomeTest))
                if (LawTests.FindRequest(law.Endpoint, collection.Requests) is { } request && LawTests.Apply(law, request))
                    changed.Add(request.Id);
            if (changed.Count > 0)
                Console.WriteLine($"Added tests to {changed.Count} request(s): {await workspace.SaveCollectionAsync(collection, collectionSource, bundled, changed)}");
            else
                Console.WriteLine("No new tests to add.");
        }

        return args.Flag("fail-on-anomaly") && laws.Anomalies.Any() ? Program.ExitTestsFailed : Program.ExitOk;
    }

    private static JsonNode? TryHar(string path)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path));
            return root is not null && Har.IsHar(root) ? root : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
