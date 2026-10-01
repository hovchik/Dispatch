using Dispatch.Application.Abstractions;
using Dispatch.Application.Monitoring;
using Dispatch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

/// <summary>Runs monitors on their schedules (or once) and sends their alerts; the engine behind a cron job or sidecar.</summary>
public static class MonitorCommand
{
    private static readonly HashSet<string> Flags = ["once", "watch", "no-color", "list"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string>());
        await using var services = Program.BuildServices(args.Option("db"));
        await services.GetRequiredService<Dispatch.Infrastructure.Persistence.DatabaseInitializer>().InitializeAsync(cancellationToken);

        var monitorRepo = services.GetRequiredService<IMonitorRepository>();
        var collections = await services.GetRequiredService<ICollectionRepository>().GetAllAsync(cancellationToken);
        var environments = await services.GetRequiredService<IEnvironmentRepository>().GetAllAsync(cancellationToken);
        var service = services.GetRequiredService<MonitorService>();

        var monitors = (await monitorRepo.GetAllAsync(cancellationToken)).Where(m => m.Enabled).ToList();
        if (args.Option("name") is { } name)
            monitors = monitors.Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();

        if (args.Flag("list"))
        {
            Console.WriteLine($"{monitors.Count} enabled monitor(s):");
            foreach (var m in monitors)
                Console.WriteLine($"  {m.Name}  ({(m.ScheduleKind == ScheduleKind.Cron ? m.Cron : $"every {m.IntervalMinutes} min")}, {m.Alerts.Count} alert target(s))");
            return Program.ExitOk;
        }
        if (monitors.Count == 0)
            throw new UsageException("No enabled monitors. Create one in the app (collection → Monitors).");

        MonitorContext? Context(MonitorDefinition m)
        {
            var collection = collections.FirstOrDefault(c => c.Id == m.CollectionId);
            if (collection is null)
                return null;
            var env = environments.FirstOrDefault(e => e.Id == m.EnvironmentId);
            return new MonitorContext(collection, env);
        }

        var color = !args.Flag("no-color") && !Console.IsOutputRedirected;

        if (!args.Flag("watch"))
        {
            // Run each selected monitor once (the default; also what --once means).
            var allPassed = true;
            foreach (var monitor in monitors)
            {
                if (Context(monitor) is not { } ctx)
                {
                    Console.Error.WriteLine($"warning: monitor '{monitor.Name}' has no collection; skipped.");
                    continue;
                }
                var previous = (await monitorRepo.GetRunsAsync(monitor.Id, 1, cancellationToken)).FirstOrDefault()?.Passed;
                var run = await service.RunOnceAsync(monitor, ctx, previous, cancellationToken);
                await monitorRepo.AddRunAsync(run, cancellationToken);
                Console.WriteLine(Line(monitor, run, color));
                allPassed &= run.Passed;
            }
            return allPassed ? Program.ExitOk : Program.ExitTestsFailed;
        }

        // Watch: a scheduler loop that runs each monitor when due until cancelled.
        var scheduler = new MonitorScheduler();
        var now = DateTimeOffset.Now;
        foreach (var monitor in monitors)
        {
            scheduler.Prime(monitor, now);
            if ((await monitorRepo.GetRunsAsync(monitor.Id, 1, cancellationToken)).FirstOrDefault()?.Passed is { } p)
                scheduler.Record(monitor.Id, p);
        }
        Console.WriteLine($"Watching {monitors.Count} monitor(s). Press Ctrl+C to stop.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var monitor in monitors)
                {
                    if (!scheduler.IsDue(monitor, DateTimeOffset.Now) || Context(monitor) is not { } ctx)
                        continue;
                    var run = await service.RunOnceAsync(monitor, ctx, scheduler.LastResult(monitor.Id), cancellationToken);
                    await monitorRepo.AddRunAsync(run, cancellationToken);
                    scheduler.Record(monitor.Id, run.Passed);
                    Console.WriteLine(Line(monitor, run, color));
                }
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        return Program.ExitOk;
    }

    private static string Line(MonitorDefinition monitor, MonitorRun run, bool color)
    {
        var mark = run.Passed ? Paint("✓", "32", color) : Paint("✗", "31", color);
        return $"{DateTimeOffset.Now:HH:mm:ss} {mark} {monitor.Name}  {run.Summary}  ({run.AverageMs:0} ms avg)";
    }

    private static string Paint(string text, string code, bool color) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
}
