using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Impact;
using Dispatch.Application.Interop;
using Dispatch.Application.Running;
using Dispatch.Application.Spec;
using Dispatch.Domain;
using Dispatch.Infrastructure.Interop;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>Scores a collection (as defined, or by running it) or imported traffic against an OpenAPI document.</summary>
public sealed partial class SpecCoverageViewModel : ObservableObject, ITool
{
    private readonly RequestCollection _collection;
    private readonly CollectionRunner _runner;
    private readonly IDialogService _dialogs;
    private readonly Func<ApiEnvironment?> _environment;
    private readonly List<CoverageSample> _imported = [];
    private CoverageReport? _report;
    private SpecDocument? _spec;
    private string _specLoadedFrom = "";

    public SpecCoverageViewModel(RequestCollection collection, CollectionRunner runner, IDialogService dialogs, Func<ApiEnvironment?> environment)
    {
        _collection = collection;
        _runner = runner;
        _dialogs = dialogs;
        _environment = environment;
        _specPath = collection.SpecLocation;
    }

    public string Title => $"Spec coverage · {_collection.Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.SpecCoverage;
    public double Width => 1100;
    public double Height => 760;

    public ObservableCollection<CoverageMetric> Metrics { get; } = [];
    public ObservableCollection<CoverageGap> Gaps { get; } = [];
    public ObservableCollection<OperationCoverage> Operations { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(RunAndAnalyzeCommand), nameof(ImportHarCommand))]
    private string _specPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(RunAndAnalyzeCommand), nameof(ImportHarCommand), nameof(ExportCommand))]
    private bool _isBusy;

    [ObservableProperty] private CoverageMetric? _selectedMetric;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _overallText = "—";
    [ObservableProperty] private string _sourceText = string.Empty;
    [ObservableProperty] private decimal _minimum = 80;
    [ObservableProperty] private bool _passes;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _hasReport;

    public bool NoGaps => HasReport && SelectedMetric is { Gaps.Count: 0 };

    partial void OnSelectedMetricChanged(CoverageMetric? value)
    {
        Gaps.Clear();
        foreach (var gap in value?.Gaps ?? [])
            Gaps.Add(gap);
        OnPropertyChanged(nameof(NoGaps));
    }

    partial void OnMinimumChanged(decimal value)
    {
        if (_report is not null)
            Passes = _report.Passes((double)value);
    }

    private bool CanAnalyze() => !IsBusy && !string.IsNullOrWhiteSpace(SpecPath);

    [RelayCommand]
    private async Task BrowseSpecAsync()
    {
        var path = await _dialogs.OpenFileAsync("OpenAPI document", new FileFilter("OpenAPI / Swagger", "*.yaml", "*.yml", "*.json"));
        if (path is not null)
            SpecPath = path;
    }

    /// <summary>Scores the collection as saved: its URLs, parameters, headers, bodies and asserted statuses.</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        IsBusy = true;
        try
        {
            if (await LoadSpecAsync() is not { } spec)
                return;
            var samples = CoverageSamples.FromCollection(_collection, _environment()).Concat(_imported).ToList();
            Show(SpecCoverage.Analyze(spec, samples, _collection.Name + (_imported.Count > 0 ? " + imported traffic" : "")));
            Status = $"Scored {samples.Count} saved request(s){(_imported.Count > 0 ? " and imported exchanges" : "")} as defined. Run the collection to score real responses.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Runs the collection and scores the real requests and responses.</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task RunAndAnalyzeAsync()
    {
        IsBusy = true;
        Status = $"Running {_collection.Name}…";
        try
        {
            if (await LoadSpecAsync() is not { } spec)
                return;
            var report = await _runner.RunAsync(new RunOptions
            {
                Name = _collection.Name, Requests = _collection.Requests.Where(r => r.Kind == RequestKind.Http).OrderBy(r => r.SortOrder).ToList(),
                CollectionRequests = _collection.Requests, Environment = _environment(), CollectionVariables = _collection.Variables,
                CollectionSpec = _collection.SpecLocation, RecordHistory = false, Snapshots = Dispatch.Application.Testing.SnapshotMode.Verify
            });
            var samples = CoverageSamples.FromRun(report).Concat(_imported).ToList();
            Show(SpecCoverage.Analyze(spec, samples, _collection.Name + " (run)" + (_imported.Count > 0 ? " + imported traffic" : "")));
            Status = $"Scored {report.Results.Count} response(s) from the run{(_imported.Count > 0 ? " plus imported exchanges" : "")}.";
        }
        catch (Exception ex)
        {
            Status = $"Run failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task ImportHarAsync()
    {
        var path = await _dialogs.OpenFileAsync("Import traffic (HAR or run report)", new FileFilter("HAR or JSON report", "*.har", "*.json"));
        if (path is null)
            return;
        try
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path));
            IReadOnlyList<CoverageSample> samples;
            if (root is not null && Har.IsHar(root))
                samples = CoverageSamples.FromHar(root);
            else if (root is not null && CoverageSamples.IsRunJson(root))
                samples = CoverageSamples.FromRunJson(root, _collection);
            else
            {
                Status = "That file is neither a HAR archive nor a dispatch run report.";
                return;
            }
            _imported.AddRange(samples);
            Status = $"Imported {samples.Count} exchange(s) from {Path.GetFileName(path)}.";
            await AnalyzeAsync();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            Status = $"Could not read {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    private async Task<SpecDocument?> LoadSpecAsync()
    {
        try
        {
            if (_spec is null || _specLoadedFrom != SpecPath)
            {
                _spec = await SpecLoader.LoadAsync(SpecPath.Trim());
                _specLoadedFrom = SpecPath;
            }
            return _spec;
        }
        catch (Exception ex) when (ex is IOException or FormatException or HttpRequestException or UnauthorizedAccessException)
        {
            Status = $"Could not load the spec: {ex.Message}";
            return null;
        }
    }

    private void Show(CoverageReport report)
    {
        _report = report;
        Metrics.Clear();
        Operations.Clear();
        foreach (var metric in report.Metrics)
            Metrics.Add(metric);
        foreach (var row in report.Operations)
            Operations.Add(row);
        SelectedMetric = Metrics.FirstOrDefault(m => m.Gaps.Count > 0) ?? Metrics.FirstOrDefault();
        OverallText = $"{report.Overall:0.#}%";
        Summary = report.Summary;
        SourceText = $"{report.SpecTitle} ← {report.Source} · {report.SampleCount} sample(s)";
        Passes = report.Passes((double)Minimum);
        HasReport = true;
        OnPropertyChanged(nameof(NoGaps));
    }

    private bool CanExport() => !IsBusy && HasReport;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        if (_report is null)
            return;
        var minimum = (double)Minimum;
        var (ext, content, filter) = format == "json"
            ? (".json", CoverageReportWriter.Json(_report, minimum), new FileFilter("JSON", "*.json"))
            : (".html", CoverageReportWriter.Html(_report, minimum), new FileFilter("HTML", "*.html"));
        var path = await _dialogs.SaveFileAsync("Save coverage report", DispatchFormat.Slug(_collection.Name) + "-coverage" + ext, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Report saved to {path}";
        }
    }
}

/// <summary>A collection the spec diff can be checked against; <see cref="Collection"/> is null for "changes only".</summary>
public sealed record CollectionChoice(string Name, RequestCollection? Collection)
{
    public override string ToString() => Name;
}

/// <summary>Compares two versions of an OpenAPI document and lists what each breaking change affects in a collection.</summary>
public sealed partial class SpecDiffViewModel : ObservableObject, ITool
{
    private readonly IDialogService _dialogs;
    private readonly Func<RequestCollection, Task<IReadOnlyList<TestFlow>>> _loadFlows;
    private SpecDiffReport? _report;

    public SpecDiffViewModel(IReadOnlyList<RequestCollection> collections, RequestCollection? current, IDialogService dialogs,
        Func<RequestCollection, Task<IReadOnlyList<TestFlow>>> loadFlows)
    {
        _dialogs = dialogs;
        _loadFlows = loadFlows;
        Collections.Add(new CollectionChoice("No collection (changes only)", null));
        foreach (var collection in collections)
            Collections.Add(new CollectionChoice(collection.Name, collection));
        _selectedCollection = Collections.FirstOrDefault(c => c.Collection is not null && current is not null && c.Collection.Id == current.Id) ?? Collections[0];
        _oldPath = current?.SpecLocation ?? "";
    }

    public string Title => "Compare spec versions";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.BreakingChanges;
    public double Width => 1100;
    public double Height => 760;

    public ObservableCollection<CollectionChoice> Collections { get; } = [];
    public ObservableCollection<SpecChange> Changes { get; } = [];
    public ObservableCollection<SpecImpactItem> Impact { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    private string _oldPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    private string _newPath = string.Empty;

    [ObservableProperty] private CollectionChoice? _selectedCollection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand), nameof(ExportCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private int _breakingCount;
    [ObservableProperty] private int _nonBreakingCount;
    [ObservableProperty] private int _breaksCount;
    [ObservableProperty] private bool _showImpact;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyPropertyChangedFor(nameof(NoChanges), nameof(NoImpact))]
    private bool _hasReport;

    public bool NoChanges => HasReport && Changes.Count == 0;
    public bool NoImpact => HasReport && ShowImpact && Impact.Count == 0;

    [RelayCommand]
    private async Task BrowseOldAsync()
    {
        var path = await _dialogs.OpenFileAsync("Previous spec version", new FileFilter("OpenAPI / Swagger", "*.yaml", "*.yml", "*.json"));
        if (path is not null)
            OldPath = path;
    }

    [RelayCommand]
    private async Task BrowseNewAsync()
    {
        var path = await _dialogs.OpenFileAsync("New spec version", new FileFilter("OpenAPI / Swagger", "*.yaml", "*.yml", "*.json"));
        if (path is not null)
            NewPath = path;
    }

    private bool CanCompare() => !IsBusy && !string.IsNullOrWhiteSpace(OldPath) && !string.IsNullOrWhiteSpace(NewPath);

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareAsync()
    {
        IsBusy = true;
        try
        {
            var before = await SpecLoader.LoadAsync(OldPath.Trim());
            var after = await SpecLoader.LoadAsync(NewPath.Trim());
            var report = SpecDiff.Compare(before, after, Name(OldPath, before), Name(NewPath, after));
            if (SelectedCollection?.Collection is { } collection)
            {
                IReadOnlyList<TestFlow> flows;
                try
                {
                    flows = await _loadFlows(collection);
                }
                catch (Exception ex)
                {
                    flows = [];
                    Status = $"Flows could not be loaded ({ex.Message}); they are left out.";
                }
                SpecDiff.AddImpact(report, before, collection, flows);
            }
            _report = report;
            Changes.Clear();
            Impact.Clear();
            foreach (var change in report.Changes)
                Changes.Add(change);
            foreach (var item in report.Impact)
                Impact.Add(item);
            BreakingCount = report.Count(ChangeSeverity.Breaking);
            NonBreakingCount = report.Count(ChangeSeverity.NonBreaking);
            BreaksCount = report.Impact.Count(i => i.Severity == ImpactSeverity.Breaks);
            ShowImpact = report.CollectionName is not null;
            Summary = report.Summary;
            HasReport = true;
            OnPropertyChanged(nameof(NoChanges));
            OnPropertyChanged(nameof(NoImpact));
            Status = $"Compared {before.Operations.Count} → {after.Operations.Count} operation(s).";
        }
        catch (Exception ex) when (ex is IOException or FormatException or HttpRequestException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Name(string location, SpecDocument spec)
    {
        var file = File.Exists(location) ? Path.GetFileName(location) : location;
        return spec.Version.Length > 0 ? $"{file} ({spec.Version})" : file;
    }

    private bool CanExport() => !IsBusy && HasReport;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        if (_report is null)
            return;
        var (ext, content, filter) = format == "json"
            ? (".json", SpecDiffReportWriter.Json(_report), new FileFilter("JSON", "*.json"))
            : (".html", SpecDiffReportWriter.Html(_report), new FileFilter("HTML", "*.html"));
        var path = await _dialogs.SaveFileAsync("Save spec changes report", "spec-changes" + ext, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Report saved to {path}";
        }
    }
}

/// <summary>Change severity → badge colour.</summary>
public sealed class ChangeSeverityBrush : Avalonia.Data.Converters.IValueConverter
{
    public static readonly ChangeSeverityBrush Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        new Avalonia.Media.SolidColorBrush(value switch
        {
            ChangeSeverity.Breaking => Avalonia.Media.Color.Parse("#CF222E"),
            ChangeSeverity.NonBreaking => Avalonia.Media.Color.Parse("#22A06B"),
            _ => Avalonia.Media.Color.Parse("#6B7280")
        });

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => null;
}

/// <summary>Coverage percentage → colour (green ≥ 80, amber ≥ 50, red below).</summary>
public sealed class CoverageBrush : Avalonia.Data.Converters.IValueConverter
{
    public static readonly CoverageBrush Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var percent = value is double d ? d : value is CoverageMetric m ? m.Percent : 0;
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(percent >= 80 ? "#22A06B" : percent >= 50 ? "#9A6700" : "#CF222E"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => null;
}
