using Dispatch.Application.Abstractions;
using Dispatch.Application.Sync;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

/// <summary><c>dispatch sync</c>: two-way sync between a saved collection and a git-friendly folder.</summary>
public static class SyncCommand
{
    private const string Usage = "Usage: dispatch sync <saved collection> <folder> [--watch] [--poll 5s] [--resolve app|folder] [--db <path>]";

    private static readonly HashSet<string> Flags = ["watch", "no-color"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags);
        if (args.Positionals.Count != 2)
            throw new UsageException(Usage);
        var resolve = args.Option("resolve")?.ToLowerInvariant() switch
        {
            null => (SyncSide?)null,
            "app" => SyncSide.App,
            "folder" => SyncSide.Folder,
            var other => throw new UsageException($"--resolve expects app or folder, got '{other}'.")
        };

        await using var services = Program.BuildServices(args.Option("db"));
        await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        var saved = await services.GetRequiredService<ICollectionRepository>().GetAllAsync(cancellationToken);
        var collection = saved.FirstOrDefault(c => c.Name.Equals(args.Positionals[0], StringComparison.OrdinalIgnoreCase))
                         ?? throw new UsageException($"'{args.Positionals[0]}' is not a collection saved in the app. Saved collections: " +
                                                     (saved.Count == 0 ? "(none)" : string.Join(", ", saved.Select(c => c.Name))) +
                                                     ". Import a file first with 'dispatch import'.");
        var folder = Path.GetFullPath(args.Positionals[1]);
        var sync = services.GetRequiredService<FolderSyncService>();

        var result = await sync.SyncAsync(collection.Id, folder, resolve, cancellationToken);
        Print(result);
        if (!args.Flag("watch"))
            return result.Conflicts.Count > 0 ? Program.ExitTestsFailed : Program.ExitOk;

        var poll = args.Duration("poll", TimeSpan.FromSeconds(5));
        Console.WriteLine($"Watching {folder} and the app's copy of '{collection.Name}' (poll every {poll.TotalSeconds:0.#} s). Ctrl+C to stop.");
        using var watcher = sync.Watch(collection.Id, folder, poll,
            r =>
            {
                if (!r.InSync)
                    Print(r);
            },
            ex => Console.Error.WriteLine($"sync error: {ex.Message}"));
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        return Program.ExitOk;
    }

    private static void Print(SyncResult result)
    {
        Console.WriteLine($"[{result.SyncedAt.ToLocalTime():HH:mm:ss}] {result.Summary}");
        foreach (var change in result.Changes)
            Console.WriteLine($"  {Arrow(change.Action)} {change.Text}");
        foreach (var conflict in result.Conflicts)
            Console.WriteLine($"  !! {conflict.Name}: {conflict.Description}. Resolve with --resolve app|folder, or in the app.");
        foreach (var warning in result.Warnings)
            Console.Error.WriteLine($"warning: {warning}");
    }

    private static string Arrow(SyncAction action) => action switch
    {
        SyncAction.ToFolder or SyncAction.DeleteInFolder => "->",
        SyncAction.ToApp or SyncAction.DeleteInApp => "<-",
        _ => "  "
    };
}
