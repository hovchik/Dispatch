using Dispatch.Application.Abstractions;
using Dispatch.Application.Impact;
using Dispatch.Application.Interop;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

/// <summary><c>dispatch impact</c>: what in the collection breaks because a response changed shape.</summary>
public static class ImpactCommand
{
    private const string Usage = "Usage: dispatch impact <collection> --request <name> [--baseline snapshot|example[:name]|<file>] " +
                                 "[--current <file>] [--env e] [--fail-on breaks|possible|never] [-r cli,html,json] [-o dir] [--insecure]";

    private static readonly HashSet<string> Flags = ["insecure", "no-color"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["e"] = "env", ["o"] = "out", ["r"] = "reporter" });
        SingleRequest.ValidateReporters(args);
        var failOn = (args.Option("fail-on") ?? "breaks").ToLowerInvariant();
        if (failOn is not ("breaks" or "possible" or "never"))
            throw new UsageException($"--fail-on expects breaks, possible or never, got '{failOn}'.");
        await using var services = Program.BuildServices(args.Option("db"));
        var workspace = new Workspace(services);
        var (collection, request, environment) = await SingleRequest.LoadAsync(workspace, args, Usage);

        var baseline = PickBaseline(request, args.Option("baseline"));
        string current;
        if (args.Option("current") is { } currentFile)
        {
            current = await File.ReadAllTextAsync(currentFile, cancellationToken);
        }
        else
        {
            Console.WriteLine($"Sending {request.Name} to get the current response…");
            var response = await services.GetRequiredService<IRequestSender>().SendAsync(request, new SendOptions
            {
                Environment = environment,
                CollectionVariables = collection.Variables,
                RecordHistory = false,
                RunScripts = false,
                CheckExpectations = false
            }, cancellationToken);
            if (!response.HasResponse)
                throw new UsageException($"{request.Name} got no response: {response.Error}");
            current = response.Body;
        }

        var report = ImpactAnalyzer.Analyze(request, baseline.Body, current, collection.Requests, await FlowsAsync(services, collection), baseline.Name);

        if (args.Options("reporter").DefaultIfEmpty("cli").Contains("cli", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine(ImpactReportWriter.Text(report, SingleRequest.Color(args)));
        }
        await SingleRequest.WriteReportsAsync(args, $"{DispatchFormat.Slug(request.Name)}-impact-{report.CreatedAt:yyyyMMdd-HHmmss}",
            reporter => reporter switch
            {
                "html" => (".html", ImpactReportWriter.Html(report)),
                "json" => (".json", ImpactReportWriter.Json(report)),
                _ => null
            });

        var failed = failOn switch
        {
            "breaks" => report.HasBreakingImpact,
            "possible" => report.Items.Count > 0,
            "never" => false,
            _ => throw new UsageException($"--fail-on expects breaks, possible or never, got '{failOn}'.")
        };
        return failed ? Program.ExitTestsFailed : Program.ExitOk;
    }

    /// <summary>snapshot (default when recorded), example / example:name, or a path to a saved response body.</summary>
    private static ImpactBaseline PickBaseline(ApiRequest request, string? choice)
    {
        var available = ImpactBaselines.For(request);
        if (choice is not null && File.Exists(choice))
            return new ImpactBaseline(Path.GetFileName(choice), File.ReadAllText(choice));
        if (choice is null)
            return available.FirstOrDefault()
                   ?? throw new UsageException($"{request.Name} has no recorded snapshot or saved example to compare with. " +
                                               "Pass --baseline <file> with a previous response body.");
        if (choice.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
            return available.FirstOrDefault(b => b.Name.StartsWith("snapshot", StringComparison.Ordinal))
                   ?? throw new UsageException($"{request.Name} has no recorded snapshot.");
        if (choice.StartsWith("example", StringComparison.OrdinalIgnoreCase))
        {
            var name = choice.Length > "example".Length ? choice["example".Length..].TrimStart(':').Trim() : "";
            return available.FirstOrDefault(b => b.Name.StartsWith("example: ", StringComparison.Ordinal)
                                                 && (name.Length == 0 || b.Name["example: ".Length..].Equals(name, StringComparison.OrdinalIgnoreCase)))
                   ?? throw new UsageException(name.Length == 0 ? $"{request.Name} has no saved example." : $"{request.Name} has no example named '{name}'.");
        }
        throw new UsageException($"--baseline: '{choice}' is not snapshot, example[:name] or an existing file.");
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
