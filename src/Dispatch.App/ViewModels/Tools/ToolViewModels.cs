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

    /// <summary>Help topic (<see cref="Dispatch.Application.Help.HelpCatalog"/>) linked from the window's Help button.</summary>
    string? HelpTopic => null;

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
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.ImportExport;
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
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.Requests;
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
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.Variables;
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

/// <summary>Runs a security scan against a collection and shows findings as they arrive.</summary>
public sealed partial class SecurityScanViewModel : ObservableObject, ITool
{
    private readonly RequestCollection _collection;
    private readonly Dispatch.Application.Security.SecurityScanner _scanner;
    private readonly IDialogService _dialogs;
    private readonly Func<ApiEnvironment?> _environment;
    private CancellationTokenSource? _cts;
    private Dispatch.Application.Security.ScanReport? _report;

    public SecurityScanViewModel(RequestCollection collection, Dispatch.Application.Security.SecurityScanner scanner,
        IDialogService dialogs, Func<ApiEnvironment?> environment)
    {
        _collection = collection;
        _scanner = scanner;
        _dialogs = dialogs;
        _environment = environment;
    }

    public string Title => $"Security scan · {_collection.Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.SecurityScan;
    public double Width => 1000;
    public double Height => 720;

    public ObservableCollection<Dispatch.Application.Security.ScanFinding> Findings { get; } = [];

    [ObservableProperty] private bool _passive = true;
    [ObservableProperty] private bool _active = true;
    [ObservableProperty] private bool _checkInjection = true;
    [ObservableProperty] private bool _checkAuth = true;
    [ObservableProperty] private bool _checkBoundaries = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(StopCommand), nameof(ExportCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _status = "Scan APIs you are authorised to test. Passive checks read normal responses; active probes send extra crafted requests.";
    [ObservableProperty] private int _highCount;
    [ObservableProperty] private int _mediumCount;
    [ObservableProperty] private int _lowCount;
    [ObservableProperty] private int _infoCount;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        Findings.Clear();
        HighCount = MediumCount = LowCount = InfoCount = 0;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        Status = "Scanning…";
        var options = new Dispatch.Application.Security.ScanOptions
        {
            Passive = Passive,
            Active = Active,
            CheckInjection = CheckInjection,
            CheckAuth = CheckAuth,
            CheckBoundaries = CheckBoundaries
        };
        var progress = new Progress<Dispatch.Application.Security.ScanFinding>(f =>
        {
            Findings.Add(f);
            switch (f.Severity)
            {
                case Dispatch.Application.Security.ScanSeverity.High: HighCount++; break;
                case Dispatch.Application.Security.ScanSeverity.Medium: MediumCount++; break;
                case Dispatch.Application.Security.ScanSeverity.Low: LowCount++; break;
                default: InfoCount++; break;
            }
        });
        try
        {
            _report = await _scanner.ScanAsync(_collection.Requests.ToList(), options, _environment(), _collection.Variables, progress, _cts.Token);
            Status = $"{_report.RequestsScanned} request(s) scanned, {_report.ProbesSent} probe(s) sent in {_report.Duration.TotalSeconds:0.0} s · " +
                     $"{Findings.Count} finding(s)" + (Findings.Count == 0 ? ". Automated scanning is not exhaustive." : "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanScan() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        if (_report is null)
            return;
        var (ext, content, filter) = format == "json"
            ? (".json", Dispatch.Application.Security.ScanReportWriter.Json(_report), new FileFilter("JSON", "*.json"))
            : (".html", Dispatch.Application.Security.ScanReportWriter.Html(_report, _collection.Name), new FileFilter("HTML", "*.html"));
        var path = await _dialogs.SaveFileAsync("Save scan report", Application.Interop.DispatchFormat.Slug(_collection.Name) + "-scan" + ext, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Report saved to {path}";
        }
    }

    private bool CanExport() => !IsRunning && _report is not null;

    public void OnClosed() => _cts?.Cancel();
}

/// <summary>Severity → badge colour for the scan view.</summary>
public sealed class ScanSeverityBrush : Avalonia.Data.Converters.IValueConverter
{
    public static readonly ScanSeverityBrush Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        new Avalonia.Media.SolidColorBrush(value switch
        {
            Dispatch.Application.Security.ScanSeverity.High => Avalonia.Media.Color.Parse("#CF222E"),
            Dispatch.Application.Security.ScanSeverity.Medium => Avalonia.Media.Color.Parse("#9A6700"),
            Dispatch.Application.Security.ScanSeverity.Low => Avalonia.Media.Color.Parse("#0969DA"),
            _ => Avalonia.Media.Color.Parse("#6E7781")
        });

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => null;
}


/// <summary>Flow-log depth → left margin thickness.</summary>
public sealed class DepthIndentConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly DepthIndentConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        new Avalonia.Thickness(value is int d ? Math.Clamp(d, 0, 10) * 14 : 0, 0, 0, 0);
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => null;
}


/// <summary>Monitor enabled → green/grey status dot.</summary>
public sealed class EnabledBrush : Avalonia.Data.Converters.IValueConverter
{
    public static readonly EnabledBrush Instance = new();
    public object Convert(object? value, Type t, object? p, System.Globalization.CultureInfo c) =>
        new Avalonia.Media.SolidColorBrush(value is true ? Avalonia.Media.Color.Parse("#22A06B") : Avalonia.Media.Color.Parse("#8B949E"));
    public object? ConvertBack(object? value, Type t, object? p, System.Globalization.CultureInfo c) => null;
}

/// <summary>The traffic capture proxy: start it, watch requests arrive, save them to a collection or HAR.</summary>
public sealed partial class CaptureViewModel : ObservableObject, ITool
{
    private readonly Dispatch.Infrastructure.Capture.CaptureProxy _proxy;
    private readonly Dispatch.Infrastructure.Capture.CertificateAuthority _authority;
    private readonly IDialogService _dialogs;
    private readonly ICollectionRepository _collections;
    private readonly Func<Task> _onSaved;

    public CaptureViewModel(Dispatch.Infrastructure.Capture.CaptureProxy proxy, Dispatch.Infrastructure.Capture.CertificateAuthority authority,
        IDialogService dialogs, ICollectionRepository collections, Func<Task> onSaved)
    {
        _proxy = proxy;
        _authority = authority;
        _dialogs = dialogs;
        _collections = collections;
        _onSaved = onSaved;
        _proxy.Captured += e => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Exchanges.Insert(0, e);
            if (Exchanges.Count > 1000)
                Exchanges.RemoveAt(Exchanges.Count - 1);
        });
    }

    public string Title => "Capture proxy";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.Capture;
    public double Width => 1040;
    public double Height => 720;

    public ObservableCollection<Dispatch.Application.Capture.CapturedExchange> Exchanges { get; } = [];

    [ObservableProperty] private decimal _port = 8899;
    [ObservableProperty] private string _hostFilter = "";
    [ObservableProperty] private bool _decryptHttps = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _status = "Start the proxy, then point your browser, app, or HTTP(S)_PROXY at it.";
    [ObservableProperty] private Dispatch.Application.Capture.CapturedExchange? _selected;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        try
        {
            await _proxy.StartAsync(new Dispatch.Infrastructure.Capture.CaptureProxyOptions
            {
                Port = (int)Port,
                HostFilter = HostFilter.Trim(),
                DecryptHttps = DecryptHttps
            });
            IsRunning = true;
            Status = $"Listening on http://127.0.0.1:{_proxy.Port}" + (DecryptHttps ? " · HTTPS decrypted (trust the CA, button below)" : " · HTTPS tunnelled");
        }
        catch (Exception ex)
        {
            Status = $"Could not start: {ex.Message}";
        }
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        await _proxy.StopAsync();
        IsRunning = false;
        Status = $"Stopped · {Exchanges.Count} exchange(s) captured.";
    }

    [RelayCommand]
    private void Clear() => Exchanges.Clear();

    [RelayCommand]
    private async Task ExportCaAsync()
    {
        var path = await _dialogs.SaveFileAsync("Export CA certificate", "dispatch-ca.crt", new FileFilter("Certificate", "*.crt", "*.pem"));
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, _authority.CaCertificatePem);
            Status = $"CA written to {path}. Trust it so HTTPS clients don't warn.";
        }
    }

    [RelayCommand]
    private async Task ExportHarAsync()
    {
        if (Exchanges.Count == 0)
            return;
        var path = await _dialogs.SaveFileAsync("Export HAR", "capture.har", new FileFilter("HAR", "*.har"));
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, Dispatch.Application.Capture.CaptureConverter.ToHar(Exchanges.Reverse()));
            Status = $"Exported {Exchanges.Count} exchange(s) to {path}";
        }
    }

    [RelayCommand]
    private async Task SaveToCollectionAsync()
    {
        if (Exchanges.Count == 0)
            return;
        var collection = new RequestCollection { Name = $"Captured {DateTime.Now:yyyy-MM-dd HH:mm}" };
        await _collections.AddAsync(collection);
        foreach (var exchange in Exchanges.Reverse())
        {
            var request = Dispatch.Application.Capture.CaptureConverter.ToRequest(exchange);
            request.CollectionId = collection.Id;
            await _collections.SaveRequestAsync(request);
        }
        await _onSaved();
        Status = $"Saved {Exchanges.Count} request(s) to collection \"{collection.Name}\".";
    }

    [RelayCommand]
    private async Task SendToTabAsync(Dispatch.Application.Capture.CapturedExchange? exchange)
    {
        if (exchange is not null)
            await Task.CompletedTask; // reserved for "open in a tab"; selection drives the detail pane
    }

    public void OnClosed() => _ = _proxy.StopAsync();
}
