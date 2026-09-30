using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Diff;
using Dispatch.Application.Interop;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Interop;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>A view model shown in its own tool window.</summary>
public interface ITool
{
    string Title { get; }
    double Width => 900;
    double Height => 640;

    /// <summary>Called when the window closes (stop servers, cancel runs).</summary>
    void OnClosed() { }
}

// ---- Import ---------------------------------------------------------------------------------------------

public sealed partial class ImportViewModel(
    Importer importer,
    ICollectionRepository collections,
    IEnvironmentRepository environments,
    IDialogService dialogs,
    Func<Task> onImported) : ObservableObject, ITool
{
    public string Title => "Import";
    public double Width => 760;
    public double Height => 600;

    [ObservableProperty] private string _pastedText = string.Empty;
    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _imported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private ImportResult? _preview;

    public bool HasPreview => Preview is not null;
    public ObservableCollection<string> PreviewLines { get; } = [];

    public static string SupportedFormats =>
        "Postman collections & environments · OpenAPI / Swagger (JSON or YAML) · Insomnia · HAR · WSDL · .proto · .http files · " +
        "cURL commands · Dispatch files and folders";

    [RelayCommand]
    private Task ChooseFilesAsync() => BusyAsync(async () =>
    {
        var paths = await dialogs.OpenFilesAsync("Import files");
        if (paths.Count == 0)
            return;
        var combined = new ImportResult { Format = "" };
        var formats = new List<string>();
        var protos = paths.Where(p => p.EndsWith(".proto", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var path in paths.Except(protos))
            Merge(combined, await importer.ImportPathAsync(path), formats);
        if (protos.Count > 0)
            Merge(combined, Importer.ImportProto(protos), formats);
        SetPreview(combined, string.Join(", ", formats.Distinct()));
    });

    [RelayCommand]
    private Task ChooseFolderAsync() => BusyAsync(async () =>
    {
        if (await dialogs.OpenFolderAsync("Import a Dispatch collection folder") is { } folder)
            SetPreview(await importer.ImportPathAsync(folder), null);
    });

    [RelayCommand]
    private Task ImportUrlAsync() => BusyAsync(async () =>
    {
        if (Url.Trim().Length > 0)
            SetPreview(await importer.ImportUrlAsync(Url.Trim()), null);
    });

    [RelayCommand]
    private Task ParseTextAsync() => BusyAsync(() =>
    {
        SetPreview(Importer.ImportText(PastedText), null);
        return Task.CompletedTask;
    });

    private static void Merge(ImportResult into, ImportResult from, List<string> formats)
    {
        into.Collections.AddRange(from.Collections);
        into.Environments.AddRange(from.Environments);
        into.Warnings.AddRange(from.Warnings);
        formats.Add(from.Format);
    }

    private void SetPreview(ImportResult result, string? format)
    {
        Preview = result;
        Imported = false;
        PreviewLines.Clear();
        PreviewLines.Add($"Format: {format ?? result.Format}");
        foreach (var c in result.Collections)
            PreviewLines.Add($"Collection \"{c.Name}\": {c.Requests.Count} request(s)" +
                             (c.Requests.Count == 0 ? "" : $" ({string.Join(", ", c.Requests.GroupBy(r => r.Kind).Select(g => $"{g.Count()} {g.Key}"))})"));
        foreach (var e in result.Environments)
            PreviewLines.Add($"Environment \"{e.Name}\": {e.Variables.Count} variable(s)");
        foreach (var w in result.Warnings)
            PreviewLines.Add($"Warning: {w}");
        Status = null;
    }

    [RelayCommand]
    private Task ImportAsync() => BusyAsync(async () =>
    {
        if (Preview is null)
            return;
        foreach (var collection in Preview.Collections)
        {
            var requests = collection.Requests.ToList();
            collection.Id = Guid.NewGuid();
            collection.Requests = [];
            await collections.AddAsync(collection);
            foreach (var request in requests)
            {
                request.Id = Guid.NewGuid();
                request.CollectionId = collection.Id;
                await collections.SaveRequestAsync(request);
            }
        }
        foreach (var environment in Preview.Environments)
        {
            environment.Id = Guid.NewGuid();
            await environments.SaveAsync(environment);
        }
        Status = $"Imported {Preview.Collections.Count} collection(s), {Preview.RequestCount} request(s), {Preview.Environments.Count} environment(s).";
        Preview = null;
        PreviewLines.Clear();
        Imported = true;
        await onImported();
    });

    private async Task BusyAsync(Func<Task> action)
    {
        IsBusy = true;
        Status = null;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

// ---- Cookies --------------------------------------------------------------------------------------------

public sealed partial class CookiesViewModel : ObservableObject, ITool
{
    private readonly ICookieJar _jar;

    public CookiesViewModel(ICookieJar jar)
    {
        _jar = jar;
        Refresh();
    }

    public string Title => "Cookies";
    public double Width => 820;
    public double Height => 480;

    public ObservableCollection<CookieInfo> Cookies { get; } = [];
    [ObservableProperty] private bool _isEmpty;

    [RelayCommand]
    private void Refresh()
    {
        Cookies.Clear();
        foreach (var c in _jar.GetAll())
            Cookies.Add(c);
        IsEmpty = Cookies.Count == 0;
    }

    [RelayCommand]
    private void Delete(CookieInfo? cookie)
    {
        if (cookie is null)
            return;
        _jar.Delete(cookie.Domain, cookie.Path, cookie.Name);
        Refresh();
    }

    [RelayCommand]
    private void Clear()
    {
        _jar.Clear();
        Refresh();
    }
}

// ---- Diff -----------------------------------------------------------------------------------------------

public sealed record DiffLineViewModel(DiffKind Kind, string Text, string LeftNumber, string RightNumber)
{
    public string Marker => Kind switch { DiffKind.Added => "+", DiffKind.Removed => "−", _ => " " };
}

public sealed partial class DiffViewModel : ObservableObject, ITool
{
    private readonly string _left;
    private readonly string _right;

    public DiffViewModel(string title, string leftTitle, string rightTitle, string left, string right, string leftStatus, string rightStatus)
    {
        Title = title;
        LeftTitle = leftTitle;
        RightTitle = rightTitle;
        LeftStatus = leftStatus;
        RightStatus = rightStatus;
        _left = left;
        _right = right;
        Recompute();
    }

    public string Title { get; }
    public string LeftTitle { get; }
    public string RightTitle { get; }
    public string LeftStatus { get; }
    public string RightStatus { get; }
    public double Width => 1100;
    public double Height => 720;

    public ObservableCollection<DiffLineViewModel> Lines { get; } = [];
    public ObservableCollection<JsonChange> JsonChanges { get; } = [];

    [ObservableProperty] private string _ignorePaths = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isJson;
    [ObservableProperty] private bool _identical;

    partial void OnIgnorePathsChanged(string value) => Recompute();

    [RelayCommand]
    private void Recompute()
    {
        Lines.Clear();
        foreach (var line in ResponseDiff.Lines(_left, _right))
            Lines.Add(new DiffLineViewModel(line.Kind, line.Text, line.LeftLine?.ToString() ?? "", line.RightLine?.ToString() ?? ""));

        JsonChanges.Clear();
        try
        {
            var ignore = IgnorePaths.Split([',', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var change in ResponseDiff.Json(_left, _right, ignore))
                JsonChanges.Add(change);
            IsJson = true;
        }
        catch (FormatException)
        {
            IsJson = false;
        }

        var added = Lines.Count(l => l.Kind == DiffKind.Added);
        var removed = Lines.Count(l => l.Kind == DiffKind.Removed);
        Identical = IsJson ? JsonChanges.Count == 0 : added + removed == 0;
        Summary = IsJson
            ? Identical ? "No structural differences (after ignore rules)." : $"{JsonChanges.Count} structural difference(s) · {added} line(s) added, {removed} removed"
            : Identical ? "Identical." : $"{added} line(s) added, {removed} removed";
    }
}

// ---- Code snippets --------------------------------------------------------------------------------------

public sealed partial class CodeSnippetViewModel : ObservableObject, ITool
{
    private readonly ApiRequest _request;
    private readonly ApiRequest _resolved;
    private readonly IClipboardService _clipboard;

    public CodeSnippetViewModel(ApiRequest request, ApiRequest resolved, IClipboardService clipboard)
    {
        _request = request;
        _resolved = resolved;
        _clipboard = clipboard;
        Targets = CodeGenerator.TargetsFor(request.Kind);
        _selectedTarget = Targets.Count > 0 ? Targets[0] : CodeTarget.Curl;
        Regenerate();
    }

    public string Title => $"Code · {_request.Name}";
    public double Width => 820;
    public double Height => 560;
    public IReadOnlyList<CodeTarget> Targets { get; }
    public bool HasTargets => Targets.Count > 0;

    [ObservableProperty] private CodeTarget _selectedTarget;
    [ObservableProperty] private bool _resolveVariables = true;
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string? _copyFeedback;

    partial void OnSelectedTargetChanged(CodeTarget value) => Regenerate();
    partial void OnResolveVariablesChanged(bool value) => Regenerate();

    private void Regenerate() => Code = HasTargets
        ? CodeGenerator.Generate(ResolveVariables ? _resolved : _request, SelectedTarget)
        : $"Code generation isn't available for {_request.Kind} requests yet; use the Dispatch CLI: dispatch run <collection> --request \"{_request.Name}\"";

    [RelayCommand]
    private async Task CopyAsync()
    {
        await _clipboard.SetTextAsync(Code);
        CopyFeedback = "Copied";
        await Task.Delay(1500);
        CopyFeedback = null;
    }
}

// ---- Collection settings ---------------------------------------------------------------------------------

public sealed partial class CollectionSettingsViewModel : ObservableObject, ITool
{
    private readonly RequestCollection _model;
    private readonly ICollectionRepository _repository;
    private readonly IDialogService _dialogs;
    private readonly Func<Task> _onSaved;

    public CollectionSettingsViewModel(RequestCollection model, ICollectionRepository repository, IDialogService dialogs, Func<Task> onSaved)
    {
        _model = model;
        _repository = repository;
        _dialogs = dialogs;
        _onSaved = onSaved;
        _name = model.Name;
        _description = model.Description;
        _specLocation = model.SpecLocation;
        Variables = new KeyValueListViewModel("Variable", "Value", supportsBulkEdit: true, supportsSecret: true);
        Variables.Load(model.Variables);
    }

    public string Title => $"Collection · {_model.Name}";
    public double Width => 760;
    public double Height => 600;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _specLocation;
    [ObservableProperty] private string? _status;
    public KeyValueListViewModel Variables { get; }

    [RelayCommand]
    private async Task BrowseSpecAsync()
    {
        if (await _dialogs.OpenFileAsync("OpenAPI document", new FileFilter("OpenAPI", "*.json", "*.yaml", "*.yml")) is { } path)
            SpecLocation = path;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            _model.Name = Name.Trim().Length == 0 ? _model.Name : Name.Trim();
            _model.Description = Description;
            _model.SpecLocation = SpecLocation.Trim();
            _model.Variables = Variables.ToItems();
            await _repository.UpdateAsync(_model);
            await _onSaved();
            Status = "Saved.";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }
}

// ---- Command palette ------------------------------------------------------------------------------------

public sealed record PaletteItem(string Title, string Hint, Action Execute);

public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly Func<IEnumerable<PaletteItem>> _source;

    public CommandPaletteViewModel(Func<IEnumerable<PaletteItem>> source) => _source = source;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteItem? _selected;
    public ObservableCollection<PaletteItem> Items { get; } = [];

    public void Open()
    {
        Query = "";
        Refresh();
        IsOpen = true;
    }

    partial void OnQueryChanged(string value) => Refresh();

    private void Refresh()
    {
        Items.Clear();
        var terms = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = _source()
            .Where(i => terms.All(t => i.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || i.Hint.Contains(t, StringComparison.OrdinalIgnoreCase)))
            // Title prefix matches first, then other title matches, then hint-only matches.
            .OrderBy(i => terms.Length == 0 ? 0
                : i.Title.StartsWith(terms[0], StringComparison.OrdinalIgnoreCase) ? 0
                : terms.All(t => i.Title.Contains(t, StringComparison.OrdinalIgnoreCase)) ? 1 : 2)
            .Take(50);
        foreach (var item in matches)
            Items.Add(item);
        Selected = Items.FirstOrDefault();
    }

    [RelayCommand]
    private void Run(PaletteItem? item)
    {
        item ??= Selected;
        IsOpen = false;
        item?.Execute();
    }

    [RelayCommand]
    private void Move(string? direction)
    {
        if (Items.Count == 0)
            return;
        var index = Selected is null ? -1 : Items.IndexOf(Selected);
        index = direction == "up" ? Math.Max(0, index - 1) : Math.Min(Items.Count - 1, index + 1);
        Selected = Items[index];
    }

    [RelayCommand]
    private void Close() => IsOpen = false;
}
