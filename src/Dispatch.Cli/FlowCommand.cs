using Dispatch.Application.Abstractions;
using Dispatch.Application.Flows;
using Dispatch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

public static class FlowCommand
{
    private static readonly HashSet<string> Flags = ["no-color", "insecure", "save-env"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["e"] = "env" });
        if (args.Positionals.Count != 1)
            throw new UsageException("Usage: dispatch flow <collection> [--name <flow>] [--env e] [--insecure] [--save-env]");

        await using var services = Program.BuildServices(args.Option("db"));
        var workspace = new Workspace(services);
        var (collection, bundled) = await workspace.LoadCollectionAsync(args.Positionals[0], args.Option("collection"));
        var environment = await workspace.LoadEnvironmentAsync(args.Option("env"), bundled);

        var flows = (await services.GetRequiredService<IFlowRepository>().GetAllAsync(cancellationToken))
            .Where(f => f.CollectionId == collection.Id).ToList();
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
            Console.WriteLine($"Flow: {flow.Name}");
            var progress = new Progress<FlowEvent>(e => Console.WriteLine(Render(e, color)));
            var result = await runner.RunAsync(flow, new FlowRunOptions
            {
                Requests = requests,
                Environment = environment,
                CollectionVariables = collection.Variables,
                CollectionSpec = collection.SpecLocation
            }, progress, cancellationToken);

            Console.WriteLine();
            var verdict = result.Passed ? Paint("PASSED", "32", color) : Paint("FAILED", "31", color);
            Console.WriteLine($"{verdict} · {result.StepsRun} step(s), {result.RequestsSent} request(s), " +
                              $"{result.TestsPassed}/{result.TestsPassed + result.TestsFailed} tests · {result.Duration.TotalSeconds:0.00} s" +
                              (result.StopReason is { } r ? $" · {r}" : ""));
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
        return $"{indent}{mark} {e.StepName}{detail}";
    }

    private static string Paint(string text, string code, bool color) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
}
