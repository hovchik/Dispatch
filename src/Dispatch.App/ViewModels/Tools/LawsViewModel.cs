using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Interop;
using Dispatch.Application.Laws;
using Dispatch.Application.Running;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>One inferred law, selectable for adding as a test to its saved request.</summary>
public sealed partial class LawItemViewModel : ObservableObject
{
    public LawItemViewModel(ApiLaw law, ApiRequest? target)
    {
        Law = law;
        Target = target;
        _isSelected = CanAdd && law.Confidence != LawConfidence.Low;
    }

    public ApiLaw Law { get; }
    public ApiRequest? Target { get; }
    public string Endpoint => Law.Endpoint;
    public string Description => Law.Description;
    public bool IsAnomaly => Law.IsAnomaly;
    public string Counterexamples => string.Join("\n", Law.Counterexamples.Take(5));
    public string? Advice => Law.Advice;
    public string Meta => $"seen {Law.Support}× · {Law.Confidence.ToString().ToLowerInvariant()} confidence" +
                          (Target is null ? "" : $" · test goes to {Target.Name}");
    public bool CanAdd => !Law.IsAnomaly && Law.CanBecomeTest && Target is not null && !LawTests.IsApplied(Law, Target);

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isAdded;
}

/// <summary>Infers API laws for a collection from its traffic, shows anomalies, and adds laws as tests.</summary>
public sealed partial class LawsViewModel : ObservableObject, ITool
{
    private readonly RequestCollection _collection;
    private readonly IHistoryRepository _history;
    private readonly CollectionRunner _runner;
    private readonly ICollectionRepository _collections;
    private readonly IDialogService _dialogs;
    private readonly Func<ApiEnvironment?> _environment;
    private readonly Func<Task> _onSaved;
    private readonly List<Observation> _fromHistory = [];
    private readonly List<Observation> _extra = [];
    private LawReport? _report;

    public LawsViewModel(RequestCollection collection, IHistoryRepository history, CollectionRunner runner, ICollectionRepository collections,
        IDialogService dialogs, Func<ApiEnvironment?> environment, Func<Task> onSaved)
    {
        _collection = collection;
        _history = history;
        _runner = runner;
        _collections = collections;
        _dialogs = dialogs;
        _environment = environment;
        _onSaved = onSaved;
    }

    public string Title => $"API laws · {_collection.Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.ApiLaws;
    public double Width => 1080;
    public double Height => 760;

    public ObservableCollection<LawItemViewModel> Anomalies { get; } = [];
    public ObservableCollection<LawItemViewModel> Laws { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCollectionCommand), nameof(ImportHarCommand), nameof(AddSelectedCommand), nameof(ExportCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _sources = string.Empty;
    [ObservableProperty] private decimal _runs = 3;
    [ObservableProperty] private bool _hasAnomalies;

    /// <summary>Reads recent history that belongs to this collection's endpoints, then analyses it.</summary>
    public async Task LoadHistoryAsync()
    {
        try
        {
            var entries = await _history.GetRecentAsync(5000);
            _fromHistory.Clear();
            _fromHistory.AddRange(entries.Select(Observations.FromHistory).OfType<Observation>()
                .Where(o => LawTests.FindRequest(o.Endpoint, _collection.Requests) is not null));
        }
        catch (Exception ex)
        {
            Status = $"History could not be read: {ex.Message}";
        }
        Analyze();
    }

    private void Analyze()
    {
        var all = _fromHistory.Concat(_extra).ToList();
        Sources = $"{_fromHistory.Count} from history" + (_extra.Count > 0 ? $" · {_extra.Count} from runs and imports" : "");
        _report = LawMiner.Mine(all, source: _collection.Name);
        Anomalies.Clear();
        Laws.Clear();
        foreach (var law in _report.Laws)
        {
            var item = new LawItemViewModel(law, LawTests.FindRequest(law.Endpoint, _collection.Requests));
            (law.IsAnomaly ? Anomalies : Laws).Add(item);
        }
        HasAnomalies = Anomalies.Count > 0;
        Summary = _report.Summary;
        if (all.Count == 0)
            Status = "No traffic yet for this collection. Send its requests a few times, run it here, or import a HAR file from the capture proxy or your browser.";
        AddSelectedCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    private bool CanAct() => !IsBusy;

    /// <summary>Runs the whole collection a few times and adds what it observed.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RunCollectionAsync()
    {
        IsBusy = true;
        Status = $"Running {_collection.Name} {Runs}×…";
        try
        {
            var report = await _runner.RunAsync(new RunOptions
            {
                Name = _collection.Name, Requests = _collection.Requests.OrderBy(r => r.SortOrder).ToList(), CollectionRequests = _collection.Requests,
                Environment = _environment(), CollectionVariables = _collection.Variables, CollectionSpec = _collection.SpecLocation,
                Iterations = (int)Math.Max(1, Runs), RecordHistory = false, Snapshots = Dispatch.Application.Testing.SnapshotMode.Verify
            });
            var at = DateTimeOffset.Now;
            _extra.AddRange(report.Results.Select((r, i) => Observations.FromRun(r, at.AddMilliseconds(i))).OfType<Observation>());
            Status = $"Observed {report.Results.Count} response(s).";
            Analyze();
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

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ImportHarAsync()
    {
        var path = await _dialogs.OpenFileAsync("Import traffic (HAR)", new FileFilter("HAR", "*.har", "*.json"));
        if (path is null)
            return;
        try
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path));
            if (root is null || !Har.IsHar(root))
            {
                Status = "That file is not a HAR archive.";
                return;
            }
            var observations = Observations.FromHar(root);
            _extra.AddRange(observations);
            Status = $"Imported {observations.Count} exchange(s) from {Path.GetFileName(path)}.";
            Analyze();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            Status = $"Could not read {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    private bool CanAdd() => !IsBusy && Laws.Any(l => l.CanAdd);

    /// <summary>Adds the selected laws as assertions or test-script checks to their saved requests.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddSelectedAsync()
    {
        IsBusy = true;
        try
        {
            var changed = new HashSet<ApiRequest>();
            foreach (var item in Laws.Where(l => l.IsSelected && l.CanAdd))
                if (LawTests.Apply(item.Law, item.Target!))
                {
                    changed.Add(item.Target!);
                    item.IsAdded = true;
                    item.IsSelected = false;
                }
            foreach (var request in changed)
                await _collections.SaveRequestAsync(request);
            await _onSaved();
            Status = changed.Count == 0 ? "Nothing new to add." : $"Added tests to {changed.Count} request(s): {string.Join(", ", changed.Select(r => r.Name))}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not save: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExport() => !IsBusy && _report is not null;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        if (_report is null)
            return;
        var (ext, content, filter) = format == "json"
            ? (".json", LawReportWriter.Json(_report), new FileFilter("JSON", "*.json"))
            : (".html", LawReportWriter.Html(_report), new FileFilter("HTML", "*.html"));
        var path = await _dialogs.SaveFileAsync("Save API laws report", DispatchFormat.Slug(_collection.Name) + "-laws" + ext, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Report saved to {path}";
        }
    }
}
