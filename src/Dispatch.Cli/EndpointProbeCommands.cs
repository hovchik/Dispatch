using Dispatch.Application.Interop;
using Dispatch.Application.Probes;
using Dispatch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

/// <summary>Shared plumbing for <c>paginate</c>, <c>idempotency</c> and <c>cache</c>.</summary>
internal static class EndpointProbeCommand
{
    public const string CommonUsage = "[--request name] [--env e] [--max-requests n] [--max-duration 2m] [--no-scripts] " +
                                      "[--fail-on bad|warning|never] [-r cli,html,json] [-o dir] [--insecure]";

    public static readonly Dictionary<string, string> Aliases = new() { ["e"] = "env", ["o"] = "out", ["r"] = "reporter" };

    public static ProbeFailOn FailOn(Arguments args) => (args.Option("fail-on") ?? "bad").ToLowerInvariant() switch
    {
        "bad" or "problem" or "problems" or "high" => ProbeFailOn.Bad,
        "warning" or "warnings" or "warn" => ProbeFailOn.Warning,
        "never" or "none" => ProbeFailOn.Never,
        var other => throw new UsageException($"--fail-on expects bad, warning or never, got '{other}'.")
    };

    public static async Task<int> RunAsync(string[] rawArgs, string usage, HashSet<string> flags, string slug, string intro,
        Func<IServiceProvider, ApiRequest, Arguments, ApiEnvironment?, IReadOnlyList<KeyValueItem>?, CancellationToken, Task<ProbeReport>> probe,
        CancellationToken cancellationToken)
    {
        flags.UnionWith(["insecure", "no-color", "no-scripts"]);
        var args = Arguments.Parse(rawArgs, flags, Aliases);
        SingleRequest.ValidateReporters(args);
        var failOn = FailOn(args);
        await using var services = Program.BuildServices(args.Option("db"));
        var (collection, request, environment) = await SingleRequest.LoadAsync(new Workspace(services), args, usage);

        Console.WriteLine(intro.Replace("{name}", request.Name, StringComparison.Ordinal));
        var report = await probe(services, request, args, environment, collection.Variables, cancellationToken);

        if (args.Options("reporter").DefaultIfEmpty("cli").Contains("cli", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine(ProbeReportWriter.Text(report));
        }
        await SingleRequest.WriteReportsAsync(args, $"{DispatchFormat.Slug(request.Name)}-{slug}-{report.StartedAt:yyyyMMdd-HHmmss}",
            reporter => reporter switch
            {
                "html" => (".html", ProbeReportWriter.Html(report)),
                "json" => (".json", ProbeReportWriter.Json(report)),
                _ => null
            });

        if (report.Error is not null)
            return Program.ExitError;
        return report.Fails(failOn) ? Program.ExitTestsFailed : Program.ExitOk;
    }
}

/// <summary><c>dispatch paginate</c>: walk a list endpoint's pages and check them.</summary>
public static class PaginateCommand
{
    private const string Usage = "Usage: dispatch paginate <collection> [--strategy auto|link|next|cursor|page|offset] [--items $.data] [--id id] " +
                                 "[--page-param page] [--size-param limit] [--page-size n] [--cursor-param cursor] [--cursor-path $.next_cursor] " +
                                 "[--max-pages 50] [--no-edge] [--no-stability] " + EndpointProbeCommand.CommonUsage;

    public static Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken) =>
        EndpointProbeCommand.RunAsync(rawArgs, Usage, ["no-edge", "no-stability"], "pagination",
            "Walking the pages of {name}. Only probe APIs you are authorised to test.",
            (services, request, args, environment, variables, ct) =>
            {
                var strategy = (args.Option("strategy") ?? "auto").ToLowerInvariant() switch
                {
                    "auto" => PaginationStrategy.Auto,
                    "link" or "link-header" => PaginationStrategy.LinkHeader,
                    "next" or "next-url" or "url" => PaginationStrategy.NextUrl,
                    "cursor" or "token" => PaginationStrategy.Cursor,
                    "page" => PaginationStrategy.Page,
                    "offset" or "skip" => PaginationStrategy.Offset,
                    var other => throw new UsageException($"--strategy expects auto, link, next, cursor, page or offset, got '{other}'.")
                };
                var pageSize = args.Option("page-size") is null ? (int?)null : Math.Max(1, args.Int("page-size", 10));
                var options = new PaginationOptions
                {
                    Strategy = strategy,
                    ItemsPath = args.Option("items"),
                    IdField = args.Option("id"),
                    PageParam = args.Option("page-param"),
                    SizeParam = args.Option("size-param"),
                    PageSize = pageSize,
                    CursorParam = args.Option("cursor-param"),
                    CursorPath = args.Option("cursor-path"),
                    MaxPages = Math.Max(1, args.Int("max-pages", 50)),
                    EdgeCases = !args.Flag("no-edge"),
                    CheckStability = !args.Flag("no-stability"),
                    MaxRequests = Math.Max(2, args.Int("max-requests", 200)),
                    MaxDuration = args.Duration("max-duration", TimeSpan.FromMinutes(2)),
                    RunScripts = !args.Flag("no-scripts"),
                    Environment = environment,
                    CollectionVariables = variables
                };
                return services.GetRequiredService<PaginationWalker>().WalkAsync(request, options, cancellationToken: ct);
            }, cancellationToken);
}

/// <summary><c>dispatch idempotency</c>: check that a mutating request is safe to retry.</summary>
public static class IdempotencyCommand
{
    private const string Usage = "Usage: dispatch idempotency <collection> [--key-header Idempotency-Key] [--parallel 5] [--no-replay] [--no-reuse] " +
                                 "[--no-race-without-key] " + EndpointProbeCommand.CommonUsage;

    public static Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken) =>
        EndpointProbeCommand.RunAsync(rawArgs, Usage, ["no-replay", "no-reuse", "no-race-without-key"], "idempotency",
            "Checking retries of {name}: this sends the request several times and creates data. Use a test environment.",
            (services, request, args, environment, variables, ct) =>
            {
                var options = new IdempotencyOptions
                {
                    KeyHeader = args.Option("key-header") ?? "Idempotency-Key",
                    Parallel = Math.Clamp(args.Int("parallel", 5), 0, 50),
                    Replay = !args.Flag("no-replay"),
                    KeyReuse = !args.Flag("no-reuse"),
                    RaceWithoutKey = !args.Flag("no-race-without-key"),
                    MaxRequests = Math.Max(2, args.Int("max-requests", 200)),
                    MaxDuration = args.Duration("max-duration", TimeSpan.FromMinutes(2)),
                    RunScripts = !args.Flag("no-scripts"),
                    Environment = environment,
                    CollectionVariables = variables
                };
                return services.GetRequiredService<IdempotencyProber>().ProbeAsync(request, options, cancellationToken: ct);
            }, cancellationToken);
}

/// <summary><c>dispatch cache</c>: check an endpoint's HTTP caching headers and conditional requests.</summary>
public static class CacheCommand
{
    private const string Usage = "Usage: dispatch cache <collection> [--no-head] " + EndpointProbeCommand.CommonUsage;

    public static Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken) =>
        EndpointProbeCommand.RunAsync(rawArgs, Usage, ["no-head"], "caching", "Checking the HTTP caching of {name}.",
            (services, request, args, environment, variables, ct) =>
            {
                var options = new CacheOptions
                {
                    CheckHead = !args.Flag("no-head"),
                    MaxRequests = Math.Max(2, args.Int("max-requests", 50)),
                    MaxDuration = args.Duration("max-duration", TimeSpan.FromMinutes(2)),
                    RunScripts = !args.Flag("no-scripts"),
                    Environment = environment,
                    CollectionVariables = variables
                };
                return services.GetRequiredService<CacheChecker>().CheckAsync(request, options, cancellationToken: ct);
            }, cancellationToken);
}
