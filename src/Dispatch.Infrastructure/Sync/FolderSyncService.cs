using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Interop;
using Dispatch.Application.Sync;
using Dispatch.Domain;

namespace Dispatch.Infrastructure.Sync;

/// <summary>One change a sync applied (or found it could not apply).</summary>
public sealed record SyncChange(string Key, string Name, SyncAction Action)
{
    public string Text => Action switch
    {
        SyncAction.ToFolder => $"{Name}: written to the folder",
        SyncAction.ToApp => $"{Name}: applied to the app",
        SyncAction.DeleteInApp => $"{Name}: deleted in the app (its file is gone)",
        SyncAction.DeleteInFolder => $"{Name}: file removed (deleted in the app)",
        SyncAction.Conflict => $"{Name}: conflict",
        _ => $"{Name}: unchanged"
    };
}

/// <summary>An item changed on both sides since the last sync. Either text may be null when that side deleted it.</summary>
public sealed record SyncConflict(string Key, string Name, string? AppJson, string? FolderJson)
{
    public string Description => AppJson is null ? "deleted in the app, changed in the folder"
        : FolderJson is null ? "changed in the app, deleted in the folder"
        : "changed in both the app and the folder";
}

public sealed class SyncResult
{
    public required Guid CollectionId { get; init; }
    public required string Folder { get; init; }
    public DateTimeOffset SyncedAt { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyList<SyncChange> Changes { get; init; }
    public required IReadOnlyList<SyncConflict> Conflicts { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public int ToFolder => Changes.Count(c => c.Action is SyncAction.ToFolder or SyncAction.DeleteInFolder);
    public int ToApp => Changes.Count(c => c.Action is SyncAction.ToApp or SyncAction.DeleteInApp);
    public bool InSync => Changes.Count == 0 && Conflicts.Count == 0;

    public string Summary => InSync
        ? "In sync: nothing changed on either side."
        : $"{ToFolder} change(s) written to the folder, {ToApp} applied to the app" + (Conflicts.Count > 0 ? $", {Conflicts.Count} conflict(s) need a decision." : ".");
}

/// <summary>
/// Two-way sync between a collection saved in the app and a git-friendly folder (the folder export format: one
/// <c>*.request.json</c> per request plus <c>collection.dispatch.json</c>). The state recorded at the last sync lets
/// a change be attributed to one side; an item changed on both sides is reported as a conflict and left untouched
/// on both sides until it is resolved with <see cref="ResolveAsync"/>.
/// </summary>
public sealed class FolderSyncService(ICollectionRepository collections, string stateDirectory)
{
    public const string CollectionKey = "collection";

    /// <summary>Folders synced with this collection before (most recent first), from the recorded state.</summary>
    public string? LastFolder(Guid collectionId) => LoadState(collectionId)?["folder"]?.ToString();

    public async Task<SyncResult> SyncAsync(Guid collectionId, string folder, SyncSide? resolveAll = null, CancellationToken ct = default)
    {
        folder = Path.GetFullPath(folder);
        var (app, folderCollection, warnings) = await LoadSidesAsync(collectionId, folder);
        var last = LoadLast(collectionId);
        var appMap = Canonical(app);
        var folderMap = Canonical(folderCollection);
        var plan = SyncEngine.Plan(appMap, folderMap, last);
        if (resolveAll is { } side && plan.Any(d => d.Action == SyncAction.Conflict))
        {
            foreach (var conflict in plan.Where(d => d.Action == SyncAction.Conflict))
                SyncEngine.Resolve(last, conflict, side);
            plan = SyncEngine.Plan(appMap, folderMap, last);
        }

        var changes = new List<SyncChange>();
        var conflicts = new List<SyncConflict>();
        var folderRequests = folderCollection.Requests.ToDictionary(r => r.Id.ToString(), r => r);
        var appRequests = app.Requests.ToDictionary(r => r.Id.ToString(), r => r);

        // 1. Folder → app.
        foreach (var d in plan)
        {
            ct.ThrowIfCancellationRequested();
            var name = NameOf(d, appRequests, folderRequests, app, folderCollection);
            switch (d.Action)
            {
                case SyncAction.ToApp when d.Key == CollectionKey:
                    ApplyMetadata(app, folderCollection);
                    await collections.UpdateAsync(app, ct);
                    changes.Add(new SyncChange(d.Key, name, d.Action));
                    break;
                case SyncAction.ToApp:
                {
                    var incoming = folderRequests[d.Key].Clone();
                    incoming.CollectionId = collectionId;
                    if (appRequests.TryGetValue(d.Key, out var existing))
                        incoming.SortOrder = existing.SortOrder;
                    await collections.SaveRequestAsync(incoming, ct);
                    changes.Add(new SyncChange(d.Key, name, d.Action));
                    break;
                }
                case SyncAction.DeleteInApp when d.Key != CollectionKey:
                    await collections.DeleteRequestAsync(Guid.Parse(d.Key), ct);
                    changes.Add(new SyncChange(d.Key, name, d.Action));
                    break;
                case SyncAction.ToFolder or SyncAction.DeleteInFolder:
                    changes.Add(new SyncChange(d.Key, name, d.Action));
                    break;
                case SyncAction.Conflict:
                    conflicts.Add(new SyncConflict(d.Key, name, d.App, d.Folder));
                    break;
            }
        }

        // 2. App → folder: export the merged collection, keeping the folder's own text for conflicting items.
        var merged = await LoadAppAsync(collectionId);
        var view = new RequestCollection
        {
            Id = merged.Id, Name = merged.Name, Description = merged.Description, SpecLocation = merged.SpecLocation,
            Variables = merged.Variables.Select(v => v.Clone()).ToList(), SortOrder = merged.SortOrder, CreatedAt = merged.CreatedAt
        };
        var conflictKeys = conflicts.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        if (conflictKeys.Contains(CollectionKey))
            ApplyMetadata(view, folderCollection);
        foreach (var request in merged.Requests)
            if (!conflictKeys.Contains(request.Id.ToString()))
                view.Requests.Add(request);
        foreach (var key in conflictKeys.Where(k => k != CollectionKey))
            if (folderRequests.TryGetValue(key, out var theirs))
                view.Requests.Add(theirs);
        DispatchFormat.ExportFolder(view, folder);

        // 3. Record what both sides now agree on.
        var next = SyncEngine.NextState(plan);
        SaveState(collectionId, folder, next);

        return new SyncResult { CollectionId = collectionId, Folder = folder, Changes = changes, Conflicts = conflicts, Warnings = warnings };
    }

    /// <summary>Keeps one side of a conflict and syncs again, so the winner is copied over the loser.</summary>
    public async Task<SyncResult> ResolveAsync(Guid collectionId, string folder, string key, SyncSide keep, CancellationToken ct = default)
    {
        folder = Path.GetFullPath(folder);
        var (app, folderCollection, _) = await LoadSidesAsync(collectionId, folder);
        var last = LoadLast(collectionId);
        var plan = SyncEngine.Plan(Canonical(app), Canonical(folderCollection), last);
        if (plan.FirstOrDefault(d => d.Key == key && d.Action == SyncAction.Conflict) is { } conflict)
        {
            SyncEngine.Resolve(last, conflict, keep);
            SaveState(collectionId, folder, last);
        }
        return await SyncAsync(collectionId, folder, null, ct);
    }

    /// <summary>
    /// Keeps syncing: the folder is watched for file changes (debounced), and the app side is polled every
    /// <paramref name="poll"/> since the database has no change events. Dispose to stop.
    /// </summary>
    public FolderSyncWatcher Watch(Guid collectionId, string folder, TimeSpan poll, Action<SyncResult> onSynced, Action<Exception> onError) =>
        new(this, collectionId, Path.GetFullPath(folder), poll, onSynced, onError);

    // ---- Loading ------------------------------------------------------------------------------------------

    private async Task<RequestCollection> LoadAppAsync(Guid collectionId) =>
        (await collections.GetAllAsync()).FirstOrDefault(c => c.Id == collectionId)
        ?? throw new InvalidOperationException("The collection no longer exists in the app.");

    private async Task<(RequestCollection App, RequestCollection Folder, IReadOnlyList<string> Warnings)> LoadSidesAsync(Guid collectionId, string folder)
    {
        var app = await LoadAppAsync(collectionId);
        Directory.CreateDirectory(folder);
        var imported = DispatchFormat.ImportFolder(folder);
        var folderCollection = imported.Collections[0];
        if (!File.Exists(Path.Combine(folder, DispatchFormat.CollectionFileName)))
        {
            // A folder without a collection file (empty, or hand-made) takes the app's metadata.
            ApplyMetadata(folderCollection, app);
        }
        // A hand-written file gets its folder from the directory it sits in ("admin"); use the app's spelling ("Admin").
        var spelled = app.Requests.Select(r => r.Folder).Where(f => f.Length > 0).Distinct()
            .ToDictionary(SlugPath, f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var request in folderCollection.Requests)
            if (request.Folder.Length > 0 && spelled.TryGetValue(SlugPath(request.Folder), out var spelling))
                request.Folder = spelling;
        return (app, folderCollection, imported.Warnings);
    }

    private static string SlugPath(string folder) =>
        string.Join("/", folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(DispatchFormat.Slug));

    private static Dictionary<string, string> Canonical(RequestCollection collection)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { [CollectionKey] = DispatchFormat.CollectionJson(collection) };
        foreach (var request in collection.Requests)
            map[request.Id.ToString()] = DispatchFormat.RequestJson(request);
        return map;
    }

    /// <summary>Copies name, description, spec and variables; secret values the target already has are kept (folders never hold them).</summary>
    private static void ApplyMetadata(RequestCollection target, RequestCollection source)
    {
        var secrets = target.Variables.Where(v => v.IsSecret).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        target.Name = source.Name;
        target.Description = source.Description;
        target.SpecLocation = source.SpecLocation;
        target.Variables = source.Variables.Select(v =>
        {
            var copy = v.Clone();
            if (copy.IsSecret && copy.Value.Length == 0 && secrets.TryGetValue(copy.Key, out var kept))
                copy.Value = kept;
            return copy;
        }).ToList();
    }

    private static string NameOf(SyncDecision d, Dictionary<string, ApiRequest> app, Dictionary<string, ApiRequest> folder,
        RequestCollection appCollection, RequestCollection folderCollection)
    {
        if (d.Key == CollectionKey)
            return $"Collection '{appCollection.Name}'" + (folderCollection.Name != appCollection.Name ? $" / '{folderCollection.Name}'" : "");
        if (app.TryGetValue(d.Key, out var a))
            return a.Folder.Length > 0 ? $"{a.Folder}/{a.Name}" : a.Name;
        if (folder.TryGetValue(d.Key, out var f))
            return f.Folder.Length > 0 ? $"{f.Folder}/{f.Name}" : f.Name;
        try
        {
            return JsonNode.Parse(d.Base ?? "{}")?["name"]?.ToString() ?? d.Key;
        }
        catch (JsonException)
        {
            return d.Key;
        }
    }

    // ---- State --------------------------------------------------------------------------------------------

    private string StatePath(Guid collectionId) => Path.Combine(stateDirectory, collectionId.ToString("N") + ".sync.json");

    private JsonObject? LoadState(Guid collectionId)
    {
        var path = StatePath(collectionId);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Dictionary<string, string> LoadLast(Guid collectionId)
    {
        var last = new Dictionary<string, string>(StringComparer.Ordinal);
        if (LoadState(collectionId)?["items"] is JsonObject items)
            foreach (var (key, value) in items)
                if (value is not null)
                    last[key] = value.ToString();
        return last;
    }

    private void SaveState(Guid collectionId, string folder, Dictionary<string, string> items)
    {
        Directory.CreateDirectory(stateDirectory);
        var node = new JsonObject
        {
            ["folder"] = folder,
            ["syncedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["items"] = new JsonObject(items.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => KeyValuePair.Create(i.Key, (JsonNode?)i.Value)))
        };
        File.WriteAllText(StatePath(collectionId), node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }
}

/// <summary>Keeps a collection and a folder in sync until disposed. Callbacks run on a thread-pool thread.</summary>
public sealed class FolderSyncWatcher : IDisposable
{
    private readonly FolderSyncService _service;
    private readonly Guid _collectionId;
    private readonly string _folder;
    private readonly Action<SyncResult> _onSynced;
    private readonly Action<Exception> _onError;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly Timer _poll;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _pending;
    private bool _disposed;

    internal FolderSyncWatcher(FolderSyncService service, Guid collectionId, string folder, TimeSpan poll, Action<SyncResult> onSynced,
        Action<Exception> onError)
    {
        _service = service;
        _collectionId = collectionId;
        _folder = folder;
        _onSynced = onSynced;
        _onError = onError;
        Directory.CreateDirectory(folder);
        _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Error += (_, e) => _onError(e.GetException());
        _debounce = new Timer(_ => Trigger(), null, Timeout.Infinite, Timeout.Infinite);
        _poll = new Timer(_ => Trigger(), null, poll, poll);
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (e.Name is null || e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || e is RenamedEventArgs || Directory.Exists(e.FullPath))
            _debounce.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Runs a sync now (coalescing with one already running).</summary>
    public void Trigger()
    {
        if (_disposed)
            return;
        Interlocked.Exchange(ref _pending, 1);
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        if (!await _gate.WaitAsync(0))
            return;
        try
        {
            while (Interlocked.Exchange(ref _pending, 0) == 1 && !_disposed)
            {
                try
                {
                    var result = await _service.SyncAsync(_collectionId, _folder);
                    _onSynced(result);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or FormatException)
                {
                    _onError(ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounce.Dispose();
        _poll.Dispose();
    }
}
