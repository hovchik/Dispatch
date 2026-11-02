using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Sync;
using Dispatch.Domain;
using Dispatch.Infrastructure.Sync;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>Two-way sync between a saved collection and a git-friendly folder, with conflicts to resolve by hand.</summary>
public sealed partial class FolderSyncViewModel : ObservableObject, ITool
{
    private readonly RequestCollection _collection;
    private readonly FolderSyncService _sync;
    private readonly ISettingsRepository _settings;
    private readonly IDialogService _dialogs;
    private readonly Func<Task> _reloadCollections;
    private FolderSyncWatcher? _watcher;

    public FolderSyncViewModel(RequestCollection collection, FolderSyncService sync, ISettingsRepository settings, IDialogService dialogs,
        Func<Task> reloadCollections)
    {
        _collection = collection;
        _sync = sync;
        _settings = settings;
        _dialogs = dialogs;
        _reloadCollections = reloadCollections;
        _folder = sync.LastFolder(collection.Id) ?? string.Empty;
    }

    public string Title => $"Sync with folder · {_collection.Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.FolderSync;
    public double Width => 960;
    public double Height => 640;

    public string SettingKey => $"sync:{_collection.Id}";

    public ObservableCollection<string> Log { get; } = [];
    public ObservableCollection<SyncConflictItem> Conflicts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    [NotifyPropertyChangedFor(nameof(CanSync))]
    private string _folder;

    [ObservableProperty] private string _status = "Pick the folder the collection should live in (an empty folder, or one exported earlier).";
    [ObservableProperty] private string _lastResult = string.Empty;
    [ObservableProperty] private bool _isWatching;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _toFolderCount;
    [ObservableProperty] private int _toAppCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflicts))]
    private int _conflictCount;

    public bool CanSync => !string.IsNullOrWhiteSpace(Folder);
    public bool HasConflicts => ConflictCount > 0;

    /// <summary>Restores the folder used last time. Called when the window opens.</summary>
    public async Task LoadAsync()
    {
        if (Folder.Length == 0)
            Folder = await _settings.GetAsync(SettingKey) ?? string.Empty;
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (await _dialogs.OpenFolderAsync("Folder to keep in sync with the collection") is { } folder)
            Folder = folder;
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncNowAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await _settings.SetAsync(SettingKey, Folder);
            Apply(await Task.Run(() => _sync.SyncAsync(_collection.Id, Folder)));
        }
        catch (Exception ex)
        {
            Status = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnIsWatchingChanged(bool value)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!value)
        {
            Status = "Watching stopped.";
            return;
        }
        if (!CanSync)
        {
            IsWatching = false;
            return;
        }
        try
        {
            _ = _settings.SetAsync(SettingKey, Folder);
            _watcher = _sync.Watch(_collection.Id, Folder, TimeSpan.FromSeconds(5),
                result => Dispatcher.UIThread.Post(() =>
                {
                    if (!result.InSync || Conflicts.Count > 0)
                        Apply(result);
                }),
                ex => Dispatcher.UIThread.Post(() => Status = $"Sync error: {ex.Message}"));
            _watcher.Trigger();
            Status = $"Watching {Folder}: file edits are applied within a second; app edits are written back every 5 s.";
        }
        catch (Exception ex)
        {
            IsWatching = false;
            Status = $"Could not watch the folder: {ex.Message}";
        }
    }

    private void Apply(SyncResult result)
    {
        ToFolderCount = result.ToFolder;
        ToAppCount = result.ToApp;
        ConflictCount = result.Conflicts.Count;
        LastResult = result.Summary;
        Status = $"Last sync {result.SyncedAt.ToLocalTime():HH:mm:ss} · {result.Folder}";
        foreach (var warning in result.Warnings)
            Log.Insert(0, $"{result.SyncedAt.ToLocalTime():HH:mm:ss}  warning: {warning}");
        foreach (var change in result.Changes)
            Log.Insert(0, $"{result.SyncedAt.ToLocalTime():HH:mm:ss}  {change.Text}");
        while (Log.Count > 200)
            Log.RemoveAt(Log.Count - 1);
        Conflicts.Clear();
        foreach (var conflict in result.Conflicts)
            Conflicts.Add(new SyncConflictItem(conflict, this));
        if (result.ToApp > 0)
            _ = _reloadCollections();
    }

    internal async Task ResolveAsync(SyncConflict conflict, SyncSide keep)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            Apply(await Task.Run(() => _sync.ResolveAsync(_collection.Id, Folder, conflict.Key, keep)));
            Log.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss}  {conflict.Name}: kept the {(keep == SyncSide.App ? "app" : "folder")} version");
        }
        catch (Exception ex)
        {
            Status = $"Could not resolve: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task KeepAllAppAsync() => ResolveAllAsync(SyncSide.App);

    [RelayCommand]
    private Task KeepAllFolderAsync() => ResolveAllAsync(SyncSide.Folder);

    private async Task ResolveAllAsync(SyncSide keep)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            Apply(await Task.Run(() => _sync.SyncAsync(_collection.Id, Folder, keep)));
        }
        catch (Exception ex)
        {
            Status = $"Could not resolve: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (CanSync && Directory.Exists(Folder))
            ShellOpener.Open(Folder);
    }

    public void OnClosed()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}

/// <summary>A conflict row with its two resolutions.</summary>
public sealed partial class SyncConflictItem(SyncConflict conflict, FolderSyncViewModel owner) : ObservableObject
{
    public string Name => conflict.Name;
    public string Description => conflict.Description;
    public string AppText => conflict.AppJson ?? "(deleted in the app)";
    public string FolderText => conflict.FolderJson ?? "(deleted in the folder)";

    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private Task KeepAppAsync() => owner.ResolveAsync(conflict, SyncSide.App);

    [RelayCommand]
    private Task KeepFolderAsync() => owner.ResolveAsync(conflict, SyncSide.Folder);
}
