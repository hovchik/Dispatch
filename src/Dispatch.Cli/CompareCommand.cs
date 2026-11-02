using Dispatch.Application.Compare;
using Dispatch.Application.Interop;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

/// <summary><c>dispatch compare</c>: run a collection against two environments (or base URLs) and diff every response.</summary>
public static class CompareCommand
{
    private const string Usage = "Usage: dispatch compare <collection> -e <env|url> -e <env|url> [--ignore $.timestamp] [--fail-on-diff] " +
                                 "[--request name] [--folder name] [--checks] [-r cli,html,json] [-o dir] [--insecure]";

    private static readonly HashSet<string> Flags = ["fail-on-diff", "insecure", "no-color", "checks"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["e"] = "env", ["o"] = "out", ["r"] = "reporter", ["i"] = "ignore" });
        SingleRequest.ValidateReporters(args);
        if (args.Positionals.Count != 1)
            throw new UsageException(Usage);
        var sides = args.RawOptions("env");
        if (sides.Count != 2)
            throw new UsageException("Pass exactly two -e values: environments (file or saved name) or base URLs. " + Usage);

        await using var services = Program.BuildServices(args.Option("db"));
        var workspace = new Workspace(services);
        var (collection, bundled) = await workspace.LoadCollectionAsync(args.Positionals[0], args.Option("collection"));
        var requests = Program.Filter(collection, args);
        if (args.Flag("insecure"))
            requests = requests.Select(r =>
            {
                var copy = r.Clone();
                copy.Settings.VerifySsl = false;
                return copy;
            }).ToList();

        var left = await TargetAsync(workspace, sides[0], bundled);
        var right = await TargetAsync(workspace, sides[1], bundled);
        var options = new CompareOptions
        {
            Requests = requests,
            Left = left,
            Right = right,
            IgnorePaths = args.Options("ignore"),
            CollectionVariables = collection.Variables,
            RunChecks = args.Flag("checks")
        };

        Console.WriteLine($"Comparing {collection.Name}: {requests.Count} request(s) on {left.Name} vs {right.Name}");
        var progress = new Progress<CompareRequestResult>(r =>
            Console.WriteLine($"  {(r.Outcome == CompareOutcome.Same ? "=" : r.Outcome == CompareOutcome.Different ? "≠" : "✗")} {r.Name}: {r.Summary}"));
        var report = await new EnvironmentComparer(services.GetRequiredService<IRequestSender>()).RunAsync(options, progress, cancellationToken);

        if (args.Options("reporter").DefaultIfEmpty("cli").Contains("cli", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine(CompareReportWriter.Text(report, SingleRequest.Color(args)));
        }
        await SingleRequest.WriteReportsAsync(args, $"{DispatchFormat.Slug(collection.Name)}-compare-{report.StartedAt:yyyyMMdd-HHmmss}",
            reporter => reporter switch
            {
                "html" => (".html", CompareReportWriter.Html(report)),
                "json" => (".json", CompareReportWriter.Json(report)),
                _ => null
            });

        return args.Flag("fail-on-diff") && report.HasDifferences ? Program.ExitTestsFailed : Program.ExitOk;
    }

    /// <summary>An http(s) URL is a base URL; anything else is an environment file, a bundled environment or a saved one.</summary>
    private static async Task<CompareTarget> TargetAsync(Workspace workspace, string spec, IReadOnlyList<ApiEnvironment> bundled)
    {
        if (CompareTarget.LooksLikeUrl(spec))
            return CompareTarget.FromBaseUrl(spec);
        var environment = await workspace.LoadEnvironmentAsync(spec, bundled) ?? throw new UsageException($"Environment '{spec}' not found.");
        return CompareTarget.FromEnvironment(environment);
    }
}
