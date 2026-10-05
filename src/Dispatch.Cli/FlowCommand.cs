using Dispatch.Application.Abstractions;
using Dispatch.Application.Flows;
using Dispatch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

public static class FlowCommand
{
    private static readonly HashSet<string> Flags = ["no-color", "insecure", "save-env", "offline", "no-response"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["e"] = "env" });
        if (args.Positionals.Count != 1)
            throw new UsageException("Usage: dispatch flow <collection> [--name <flow>] [--env e] [--insecure] [--save-env] [--record <file>]\n" +
                                     "       dispatch flow <collection> --replay <file> --fork <n> [--status <code>] [--body <text|@file>] " +
                                     "[--no-response] [--offline]");

        await using var services = Program.BuildServices(args.Option("db"));
        var workspace = new Workspace(services);
        var (collection, bundled) = await workspace.LoadCollectionAsync(args.Positionals[0], args.Option("collection"));
        var environment = await workspace.LoadEnvironmentAsync(args.Option("env"), bundled);

        var fork = await ForkAsync(args);
        var flows = (await services.GetRequiredService<IFlowRepository>().GetAllAsync(cancellationToken))
            .Where(f => f.CollectionId == collection.Id).ToList();
        // A replay runs the recorded flow.
        if (fork is not null && args.Option("name") is null)
            flows = flows.Where(f => f.Id == fork.Recording.FlowId || f.Name == fork.Recording.FlowName).Take(1).ToList();
        if (args.Option("name") is { } wanted)
            flows = flows.Where(f => f.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (flows.Count == 0)
            throw new UsageException(args.Option("name") is { } n
                ? $"No flow named '{n}' in collection '{collection.Name}'."
                : $"Collection '{collection.Name}' has no saved flows. Build one in the app (Flows → New).");

        var requests = collection.Requests;
        if (args.Flag("insecure"))
            requests = requests.Select(r => { var c = r.Clone(); c.Settings.VerifySsl = false; return c; }).ToList();

        var color = !args.Flag("no-color") && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        var runner = services.GetRequiredService<FlowRunner>();
        var allPassed = true;

        foreach (var flow in flows)
        {
            Console.WriteLine(fork is null
                ? $"Flow: {flow.Name}"
                : $"Flow: {flow.Name} · replaying the run of {fork.Recording.RecordedAt:g} with request #{fork.ForkOrdinal} " +
                  (fork.Replacement is null ? "sent live" : "edited") + (fork.After == AfterFork.Recorded ? ", offline" : ", live after it"));
            var progress = new Progress<FlowEvent>(e => Console.WriteLine(Render(e, color)));
            var result = await runner.RunAsync(flow, new FlowRunOptions
            {
                Requests = requests,
                Environment = environment,
                CollectionVariables = collection.Variables,
                CollectionSpec = collection.SpecLocation,
                Fork = fork
            }, progress, cancellationToken);

            Console.WriteLine();
            var verdict = result.Passed ? Paint("PASSED", "32", color) : Paint("FAILED", "31", color);
            Console.WriteLine($"{verdict} · {result.StepsRun} step(s), {result.RequestsSent} request(s), " +
                              $"{result.TestsPassed}/{result.TestsPassed + result.TestsFailed} tests · {result.Duration.TotalSeconds:0.00} s" +
                              (result.StopReason is { } r ? $" · {r}" : ""));
            if (fork is not null)
            {
                var changes = FlowRunDiff.Compare(fork.Recording, result);
                Console.WriteLine(changes.Count == 0 ? "Same outcome as the original run." : "Compared with the original run:");
                foreach (var change in changes)
                    Console.WriteLine($"  {change}");
            }
            if (args.Option("record") is { } recordPath)
            {
                var path = flows.Count > 1 ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recordPath))!,
                    $"{Path.GetFileNameWithoutExtension(recordPath)}-{Dispatch.Application.Interop.DispatchFormat.Slug(flow.Name)}{Path.GetExtension(recordPath)}") : recordPath;
                await File.WriteAllTextAsync(path, result.Recording.ToJson(), cancellationToken);
                Console.WriteLine($"Recorded {result.Recording.Exchanges.Count} request(s) to {Path.GetFullPath(path)}");
            }
            Console.WriteLine();

            if (args.Flag("save-env") && environment is not null && result.EnvironmentUpdates.Count > 0)
            {
                foreach (var (k, v) in result.EnvironmentUpdates)
                    environment.SetVariable(k, v);
                await workspace.SaveEnvironmentAsync(environment);
            }
            allPassed &= result.Passed;
        }
        return allPassed ? Program.ExitOk : Program.ExitTestsFailed;
    }

    /// <summary>--replay / --fork / --status / --body / --no-response / --offline → a fork, or null for a normal run.</summary>
    private static async Task<FlowFork?> ForkAsync(Arguments args)
    {
        if (args.Option("replay") is not { } file)
            return args.Option("fork") is null ? null : throw new UsageException("--fork needs --replay <recording file>.");
        var recording = FlowRecording.FromJson(await File.ReadAllTextAsync(file));
        var ordinal = args.Int("fork", 0);
        if (recording.Find(ordinal) is not { } exchange)
            throw new UsageException($"--fork: the recording has requests #1 to #{recording.Exchanges.Count}: " +
                                     string.Join(", ", recording.Exchanges.Select(e => $"#{e.Ordinal} {e.StepName}")));
        RecordedResponse? replacement = exchange.Response.Clone();
        if (args.Flag("no-response"))
            replacement = ForkPresets.NoResponse(exchange.Response);
        if (args.Option("status") is { } status)
            replacement = ForkPresets.Status(replacement, int.TryParse(status, out var code) && code is >= 100 and < 600
                ? code
                : throw new UsageException($"--status: '{status}' is not an HTTP status."));
        if (args.Option("body") is { } body)
            replacement.Body = body.StartsWith('@') ? await File.ReadAllTextAsync(body[1..]) : body;
        // Nothing to change means: send the fork request live and see what happens now.
        var unchanged = args.Option("status") is null && args.Option("body") is null && !args.Flag("no-response");
        return new FlowFork
        {
            Recording = recording, ForkOrdinal = ordinal, Replacement = unchanged ? null : replacement,
            After = args.Flag("offline") ? AfterFork.Recorded : AfterFork.Live
        };
    }

    private static string Render(FlowEvent e, bool color)
    {
        var indent = new string(' ', 2 + e.Depth * 2);
        var mark = e.Kind switch
        {
            FlowEventKind.StepFinished => e.Ok ? Paint("✓", "32", color) : Paint("✗", "31", color),
            FlowEventKind.Failed => Paint("✗", "31", color),
            FlowEventKind.Skipped => Paint("–", "90", color),
            _ => Paint("›", "90", color)
        };
        var detail = e.Detail.Length > 0 ? Paint($"  {e.Detail}", e.Ok ? "90" : "33", color) : "";
        var number = e.Exchange is { } n ? Paint($"#{n} ", "90", color) : "";
        return $"{indent}{mark} {number}{e.StepName}{detail}";
    }

    private static string Paint(string text, string code, bool color) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
}
