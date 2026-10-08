using Dispatch.Application.Interop;
using Dispatch.Application.Minimize;
using Dispatch.Application.RateLimits;
using Dispatch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

/// <summary>Shared plumbing for commands that work on a single request of a collection.</summary>
internal static class SingleRequest
{
    public static async Task<(RequestCollection Collection, ApiRequest Request, ApiEnvironment? Environment)> LoadAsync(
        Workspace workspace, Arguments args, string usage)
    {
        if (args.Positionals.Count != 1)
            throw new UsageException(usage);
        var (collection, bundled) = await workspace.LoadCollectionAsync(args.Positionals[0], args.Option("collection"));
        var environment = await workspace.LoadEnvironmentAsync(args.Option("env"), bundled);
        var requests = Program.Filter(collection, args);
        if (requests.Count > 1)
            throw new UsageException($"Pick one request with --request <name>. {collection.Name} has: " +
                                     string.Join(", ", requests.Take(15).Select(r => r.Name)) + (requests.Count > 15 ? ", …" : ""));
        var request = requests[0];
        if (args.Flag("insecure"))
        {
            request = request.Clone();
            request.Settings.VerifySsl = false;
        }
        return (collection, request, environment);
    }

    /// <summary>Rejects unknown <c>--reporter</c> names up front, before the (slow) work runs.</summary>
    public static void ValidateReporters(Arguments args)
    {
        var unknown = args.Options("reporter").Select(r => r.ToLowerInvariant()).Except(["cli", "html", "json"]).ToList();
        if (unknown.Count > 0)
            throw new UsageException($"Unknown reporter(s): {string.Join(", ", unknown)}. Use cli, html or json.");
    }

    public static async Task WriteReportsAsync(Arguments args, string baseName, Func<string, (string Ext, string Content)?> render)
    {
        var outDir = args.Option("out") ?? "dispatch-reports";
        foreach (var reporter in args.Options("reporter").Select(r => r.ToLowerInvariant()).Where(r => r != "cli").Distinct())
        {
            var (ext, content) = render(reporter) ?? throw new UsageException($"Unknown reporter '{reporter}'. Use cli, html or json.");
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, baseName + ext);
            await File.WriteAllTextAsync(path, content, CancellationToken.None);
            Console.WriteLine($"{reporter} report: {Path.GetFullPath(path)}");
        }
    }

    public static bool Color(Arguments args) =>
        !args.Flag("no-color") && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
}

/// <summary><c>dispatch minimize</c>: shrink a request to the parts its outcome depends on.</summary>
public static class MinimizeCommand
{
    private const string Usage = "Usage: dispatch minimize <collection> [--request name] [--env e] [--match status|class|tests|body] " +
                                 "[--contains text] [--max-requests n] [--shallow] [--no-scripts] [-r cli,html,json] [-o dir] [--insecure]";

    private static readonly HashSet<string> Flags = ["shallow", "no-scripts", "insecure", "no-color"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["e"] = "env", ["o"] = "out", ["r"] = "reporter" });
        SingleRequest.ValidateReporters(args);
        await using var services = Program.BuildServices(args.Option("db"));
        var (collection, request, environment) = await SingleRequest.LoadAsync(new Workspace(services), args, Usage);

        var contains = args.Option("contains");
        var match = (args.Option("match") ?? (contains is null ? "status" : "body")).ToLowerInvariant() switch
        {
            "status" => OutcomeMatch.Status,
            "class" or "status-class" => OutcomeMatch.StatusClass,
            "tests" => OutcomeMatch.StatusAndTests,
            "body" or "contains" => OutcomeMatch.BodyContains,
            var other => throw new UsageException($"--match expects status, class, tests or body, got '{other}'.")
        };
        if (match == OutcomeMatch.BodyContains && string.IsNullOrEmpty(contains))
            throw new UsageException("--match body needs --contains <text>.");

        var options = new MinimizeOptions
        {
            Match = match,
            BodyContains = contains,
            MaxRequests = args.Int("max-requests", 300),
            Deep = !args.Flag("shallow"),
            RunScripts = !args.Flag("no-scripts"),
            Environment = environment,
            CollectionVariables = collection.Variables
        };

        Console.WriteLine($"Minimizing {request.Method.ToString().ToUpperInvariant()} {request.Name}: every probe is a real request " +
                          $"(up to {options.MaxRequests}).");
        if (request.Method is HttpVerb.Post or HttpVerb.Put or HttpVerb.Patch or HttpVerb.Delete)
            Console.Error.WriteLine($"warning: {request.Method.ToString().ToUpperInvariant()} may change data on every probe; prefer a test environment.");

        var minimizer = services.GetRequiredService<RequestMinimizer>();
        var report = await minimizer.MinimizeAsync(request, options, cancellationToken: cancellationToken);

        if (args.Options("reporter").DefaultIfEmpty("cli").Contains("cli", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine(MinimizeReportWriter.Text(report, SingleRequest.Color(args)));
        }
        await SingleRequest.WriteReportsAsync(args, $"{DispatchFormat.Slug(request.Name)}-minimize-{report.StartedAt:yyyyMMdd-HHmmss}",
            reporter => reporter switch
            {
                "html" => (".html", MinimizeReportWriter.Html(report)),
                "json" => (".json", MinimizeReportWriter.Json(report)),
                _ => null
            });

        if (report.Error is not null && report.RequestsSent <= 1)
            return Program.ExitError;
        return report.Confirmed ? Program.ExitOk : Program.ExitTestsFailed;
    }
}

/// <summary><c>dispatch ratelimit</c>: discover an endpoint's real rate-limit policy.</summary>
public static class RateLimitCommand
{
    private const string Usage = "Usage: dispatch ratelimit <collection> [--request name] [--env e] [--max-requests n] [--max-duration 3m] " +
                                 "[--concurrency n] [--poll 1s] [--throttle-status 429,503] [--expect-limit] [-r cli,html,json] [-o dir] [--insecure]";

    private static readonly HashSet<string> Flags = ["expect-limit", "insecure", "no-color"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["e"] = "env", ["o"] = "out", ["r"] = "reporter" });
        SingleRequest.ValidateReporters(args);
        await using var services = Program.BuildServices(args.Option("db"));
        var (collection, request, environment) = await SingleRequest.LoadAsync(new Workspace(services), args, Usage);

        var statuses = args.Options("throttle-status").Select(s => int.TryParse(s, out var code) && code is >= 100 and < 600
            ? code
            : throw new UsageException($"--throttle-status: '{s}' is not an HTTP status.")).ToHashSet();
        var options = new RateLimitOptions
        {
            MaxRequests = Math.Max(2, args.Int("max-requests", 400)),
            MaxDuration = args.Duration("max-duration", TimeSpan.FromMinutes(3)),
            Concurrency = Math.Max(1, args.Int("concurrency", 4)),
            PollInterval = args.Duration("poll", TimeSpan.FromSeconds(1)),
            ThrottleStatuses = statuses.Count > 0 ? statuses : new HashSet<int> { 429 },
            Environment = environment,
            CollectionVariables = collection.Variables
        };

        Console.WriteLine($"Probing the rate limit of {request.Name}: up to {options.MaxRequests} requests over at most " +
                          $"{options.MaxDuration.TotalSeconds:0} s. Only probe APIs you are authorised to test.");
        var prober = services.GetRequiredService<RateLimitProber>();
        var report = await prober.ProbeAsync(request, options, cancellationToken: cancellationToken);

        if (args.Options("reporter").DefaultIfEmpty("cli").Contains("cli", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine(RateLimitReportWriter.Text(report));
        }
        await SingleRequest.WriteReportsAsync(args, $"{DispatchFormat.Slug(request.Name)}-ratelimit-{report.StartedAt:yyyyMMdd-HHmmss}",
            reporter => reporter switch
            {
                "html" => (".html", RateLimitReportWriter.Html(report)),
                "json" => (".json", RateLimitReportWriter.Json(report)),
                _ => null
            });

        if (report.Error is not null)
            return Program.ExitError;
        return args.Flag("expect-limit") && !report.Throttled ? Program.ExitTestsFailed : Program.ExitOk;
    }
}
