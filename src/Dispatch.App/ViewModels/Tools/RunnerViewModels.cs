using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Load;
using Dispatch.Application.Running;
using Dispatch.Domain;
using Dispatch.Infrastructure.Mock;
using Dispatch.Infrastructure.Protocols.Grpc;

namespace Dispatch.App.ViewModels.Tools;

public sealed partial class SelectableRequest(ApiRequest request) : ObservableObject
{
    [ObservableProperty] private bool _isSelected = true;
    public ApiRequest Request { get; } = request;
    public string Name => Request.Folder.Length > 0 ? $"{Request.Folder}/{Request.Name}" : Request.Name;
    public object Badge => Request.Kind == RequestKind.Http ? Request.Method : Request.Kind;
}

public sealed record RunResultItem(RequestRunResult Result)
{
    public string Name => Result.Name;
    public string Iteration => $"#{Result.Iteration + 1}";
    public bool Passed => Result.Passed;
    public string Status => Result.Response.HasResponse
        ? $"{Result.Response.StatusCode} {Result.Response.ReasonPhrase}".Trim()
        : $"Error: {Result.Response.Error}";
    public string Time => $"{Result.Response.Elapsed.TotalMilliseconds:0} ms";
    public IReadOnlyList<TestResult> Tests => Result.Response.TestResults;
    public string TestsText => Tests.Count == 0 ? "" : $"{Tests.Count(t => t.Passed)}/{Tests.Count} tests";
}

/// <summary>A per-request row of the runner report.</summary>
public sealed record RunRequestRow(RequestSummary Summary)
{
    public string Name => Summary.Name;
    public string Runs => Summary.Runs.ToString("N0");
    public string Passed => Summary.Passed.ToString("N0");
    public string Failed => Summary.Failed.ToString("N0");
    public bool HasFailures => Summary.Failed > 0;
    public string Tests => Summary.Tests == 0 ? "–" : $"{Summary.Tests - Summary.FailedTests}/{Summary.Tests}";
    public string Mean => $"{Summary.Latency.Mean:0}";
    public string Min => $"{Summary.Latency.Min:0}";
    public string Max => $"{Summary.Latency.Max:0}";
    public string Statuses => Summary.StatusesText;
    public double PassRate => Summary.PassRate * 100;
    public Avalonia.Media.IBrush PassBrush => Summary.Failed == 0 ? ReportBrushes.Good : Summary.Passed == 0 ? ReportBrushes.Bad : ReportBrushes.Warning;
}

/// <summary>Collection runner: iterations or a data file, results as they come in, reports to file.</summary>
public sealed partial class RunnerViewModel : ObservableObject, ITool
{
    private readonly CollectionRunner _runner;
    private readonly IDialogService _dialogs;
    private readonly RequestCollection _collection;
    private readonly Func<ApiEnvironment?> _environment;
    private readonly Func<IReadOnlyDictionary<string, string>, Task> _saveEnvironment;
    private CancellationTokenSource? _cts;
    private RunReport? _report;

    public RunnerViewModel(RequestCollection collection, CollectionRunner runner, IDialogService dialogs,
        Func<ApiEnvironment?> environment, Func<IReadOnlyDictionary<string, string>, Task> saveEnvironment)
    {
        _runner = runner;
        _dialogs = dialogs;
        _collection = collection;
        _environment = environment;
        _saveEnvironment = saveEnvironment;
        CollectionName = collection.Name;
        foreach (var request in collection.Requests.OrderBy(r => r.Folder).ThenBy(r => r.SortOrder))
            Requests.Add(new SelectableRequest(request));
    }

    public string Title => $"Run · {CollectionName}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.Runner;
    public double Width => 1100;
    public double Height => 760;
    public string CollectionName { get; }

    public ObservableCollection<SelectableRequest> Requests { get; } = [];
    public ObservableCollection<RunResultItem> Results { get; } = [];

    // Report (filled when a run finishes).
    public ObservableCollection<StatTile> ReportStats { get; } = [];
    public ObservableCollection<Dispatch.Application.Reporting.ReportInsight> Insights { get; } = [];
    public ObservableCollection<RunRequestRow> ReportRequests { get; } = [];
    public ObservableCollection<FailureDetail> Failures { get; } = [];
    public ObservableCollection<RunResultItem> Slowest { get; } = [];
    public ObservableCollection<ReportBar> StatusBars { get; } = [];

    [ObservableProperty] private decimal _iterations = 1;
    [ObservableProperty] private decimal _delayMs;
    [ObservableProperty] private bool _stopOnFailure;
    [ObservableProperty] private bool _persistVariables = true;
    [ObservableProperty] private string _dataFile = "";
    [ObservableProperty] private string _dataSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand), nameof(ExportCommand), nameof(OpenReportCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _summary = "Choose requests and click Run.";
    [ObservableProperty] private int _passedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private RunResultItem? _selectedResult;

    /// <summary>0 = results, 1 = report.</summary>
    [ObservableProperty] private int _selectedTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private RunSummary? _reportSummary;

    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private Avalonia.Media.IBrush _verdictBrush = ReportBrushes.Neutral;
    [ObservableProperty] private string _reportSubtitle = "";
    [ObservableProperty] private bool _hasFailures;

    public bool HasReport => ReportSummary is not null;

    [RelayCommand]
    private async Task ChooseDataFileAsync()
    {
        var path = await _dialogs.OpenFileAsync("Data file (one iteration per row)", new FileFilter("CSV or JSON", "*.csv", "*.json"));
        if (path is null)
            return;
        try
        {
            var rows = Application.Running.DataFile.Load(path);
            DataFile = path;
            DataSummary = $"{rows.Count} row(s): {string.Join(", ", rows.FirstOrDefault()?.Keys ?? [])}";
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            DataSummary = ex.Message;
        }
    }

    [RelayCommand]
    private void ClearDataFile()
    {
        DataFile = "";
        DataSummary = "";
    }

    [RelayCommand]
    private void SelectAll(bool? value)
    {
        foreach (var r in Requests)
            r.IsSelected = value ?? true;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var selected = Requests.Where(r => r.IsSelected).Select(r => r.Request).ToList();
        if (selected.Count == 0)
        {
            Summary = "Select at least one request.";
            return;
        }

        IReadOnlyList<IReadOnlyDictionary<string, string>> data = [];
        if (DataFile.Length > 0)
        {
            try
            {
                data = Application.Running.DataFile.Load(DataFile);
            }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                Summary = ex.Message;
                return;
            }
        }

        Results.Clear();
        PassedCount = FailedCount = 0;
        Progress = 0;
        SelectedTab = 0;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var total = selected.Count * (data.Count > 0 ? data.Count : (int)Math.Max(1, Iterations));
        Summary = $"Running {total} request(s)…";
        var progress = new Progress<RequestRunResult>(r =>
        {
            Results.Add(new RunResultItem(r));
            if (r.Passed) PassedCount++;
            else FailedCount++;
            Progress = 100.0 * Results.Count / total;
        });

        try
        {
            _report = await _runner.RunAsync(new RunOptions
            {
                Name = CollectionName,
                Requests = selected,
                CollectionRequests = _collection.Requests,
                Environment = _environment(),
                CollectionVariables = _collection.Variables,
                CollectionSpec = _collection.SpecLocation,
                Iterations = (int)Math.Max(1, Iterations),
                Data = data,
                DelayMs = (int)DelayMs,
                StopOnFailure = StopOnFailure,
                RecordHistory = false
            }, progress, _cts.Token);

            ShowReport(_report);
            if (PersistVariables && _report.EnvironmentUpdates.Count > 0)
                await _saveEnvironment(_report.EnvironmentUpdates);
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Builds the report tab from a finished run and switches to it.</summary>
    public void ShowReport(RunReport report)
    {
        _report = report;
        if (Results.Count != report.Results.Count)
        {
            Results.Clear();
            foreach (var r in report.Results)
                Results.Add(new RunResultItem(r));
            PassedCount = report.Results.Count(r => r.Passed);
            FailedCount = report.Results.Count - PassedCount;
        }
        Progress = 100;
        Summary = $"{report.TotalRequests} request(s) in {report.Duration.TotalSeconds:0.00} s · {report.FailedRequests} failed · " +
                  $"{report.TotalTests - report.FailedTests}/{report.TotalTests} tests passed · avg {report.AverageResponseTime.TotalMilliseconds:0} ms" +
                  (report.Stopped ? " · stopped" : "");

        var summary = RunSummary.From(report);
        Verdict = report.TotalRequests == 0 ? "NO DATA" : report.Passed ? "PASSED" : "FAILED";
        VerdictBrush = ReportBrushes.ForVerdict(Verdict);
        ReportSubtitle = $"{report.StartedAt:yyyy-MM-dd HH:mm:ss} · {report.Duration.TotalSeconds:0.00} s · {report.Iterations} iteration(s)" +
                         (DataFile.Length > 0 ? $" · data {Path.GetFileName(DataFile)}" : "") + (report.Stopped ? " · stopped early" : "");

        ReportStats.Clear();
        ReportStats.Add(new StatTile("Pass rate", $"{summary.PassRate * 100:0.#}%", $"{report.TotalRequests - report.FailedRequests}/{report.TotalRequests} requests",
            report.Passed ? ReportBrushes.Good : ReportBrushes.Bad));
        ReportStats.Add(new StatTile("Tests", report.TotalTests == 0 ? "–" : $"{report.TotalTests - report.FailedTests}/{report.TotalTests}",
            report.FailedTests > 0 ? $"{report.FailedTests} failed" : "passed", report.FailedTests > 0 ? ReportBrushes.Bad : null));
        ReportStats.Add(new StatTile("Duration", $"{report.Duration.TotalSeconds:0.00} s"));
        ReportStats.Add(new StatTile("Average", $"{summary.Latency.Mean:0} ms", $"median {summary.Latency.P50:0} ms"));
        ReportStats.Add(new StatTile("p95", $"{summary.Latency.P95:0} ms", $"max {summary.Latency.Max:0} ms"));
        ReportStats.Add(new StatTile("Received", ReportBrushes.Bytes(report.Results.Sum(r => r.Response.SizeBytes))));

        Insights.Clear();
        foreach (var i in summary.Insights)
            Insights.Add(i);
        ReportRequests.Clear();
        foreach (var r in summary.Requests)
            ReportRequests.Add(new RunRequestRow(r));
        Failures.Clear();
        foreach (var f in summary.Failures)
            Failures.Add(f);
        HasFailures = Failures.Count > 0;
        Slowest.Clear();
        foreach (var r in summary.Slowest)
            Slowest.Add(new RunResultItem(r));
        StatusBars.Clear();
        var max = summary.Statuses.Count == 0 ? 1 : Math.Max(1, summary.Statuses.Values.Max());
        foreach (var s in summary.Statuses.OrderBy(s => s.Key))
            StatusBars.Add(new ReportBar(s.Key == "error" ? "No response" : s.Key, s.Value, max, ReportBrushes.ForStatus(s.Key)));

        ReportSummary = summary;
        SelectedTab = 1;
        ExportCommand.NotifyCanExecuteChanged();
        OpenReportCommand.NotifyCanExecuteChanged();
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void OpenReport()
    {
        if (_report is null)
            return;
        try
        {
            ShellOpener.OpenTemp(CollectionName + "-run", ".html", System.Text.Encoding.UTF8.GetBytes(ReportWriters.Html(_report)));
        }
        catch (Exception ex)
        {
            Summary = $"Could not open the report: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        if (_report is null)
            return;
        var (extension, content, filter) = format switch
        {
            "junit" => (".xml", ReportWriters.JUnit(_report), new FileFilter("JUnit XML", "*.xml")),
            "json" => (".json", ReportWriters.Json(_report), new FileFilter("JSON", "*.json")),
            _ => (".html", ReportWriters.Html(_report), new FileFilter("HTML", "*.html"))
        };
        var path = await _dialogs.SaveFileAsync("Save report", Application.Interop.DispatchFormat.Slug(CollectionName) + "-report" + extension, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Summary = $"Report saved to {path}";
        }
    }

    private bool CanExport() => !IsRunning && _report is not null;

    public void OnClosed() => _cts?.Cancel();
}

/// <summary>A route the mock server answers, with a live hit counter.</summary>
public sealed partial class MockRouteItem : ObservableObject
{
    private readonly System.Text.RegularExpressions.Regex? _pattern;
    private readonly int _specificity;

    public MockRouteItem(Dispatch.Application.Mock.MockRoute route)
    {
        Method = route.Method;
        Template = route.Template;
        RequestName = route.Request.Folder.Length > 0 ? $"{route.Request.Folder}/{route.Request.Name}" : route.Request.Name;
        Examples = route.Request.Examples.Count;
        _pattern = route.Pattern;
        _specificity = route.Specificity;
    }

    public MockRouteItem(string grpcMethod)
    {
        Method = "gRPC";
        Template = grpcMethod;
        RequestName = grpcMethod.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? grpcMethod;
        Examples = 1;
        IsGrpc = true;
    }

    public string Method { get; }
    public string Template { get; }
    public string RequestName { get; }
    public int Examples { get; }
    public bool IsGrpc { get; }
    public bool HasExamples => Examples > 0;
    public string ExamplesText => Examples == 0 ? "no example" : Examples == 1 ? "1 example" : $"{Examples} examples";
    public int Specificity => _specificity;

    [ObservableProperty] private int _hits;

    public bool Matches(string method, string path) => IsGrpc
        ? method == "gRPC" && string.Equals(path, Template, StringComparison.Ordinal)
        : (Method.Equals(method, StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) && Method == "GET")
          && _pattern is not null &&
          _pattern.IsMatch(path.TrimEnd('/') is { Length: > 0 } p ? p : "/");
}

public sealed partial class MockServerViewModel : ObservableObject, ITool
{
    private readonly MockServer _server;
    private readonly RequestCollection _collection;
    private readonly IClipboardService _clipboard;
    private int _served;
    private int _matched;
    private int _unmatched;
    private int _faults;
    private double _totalMs;

    public MockServerViewModel(RequestCollection collection, GrpcSchemaProvider grpcSchemas, IClipboardService clipboard)
    {
        _collection = collection;
        _clipboard = clipboard;
        _server = new MockServer(grpcSchemas);
        _server.RequestHandled += entry => Avalonia.Threading.Dispatcher.UIThread.Post(() => Record(entry));
        var withExamples = collection.Requests.Count(r => r.Examples.Count > 0);
        HasNoExamples = withExamples == 0;
        Hint = withExamples == 0
            ? "No request in this collection has a saved example yet. Send a request and click \"Save as example\" in the response, or import an OpenAPI spec. Without examples, routes answer 501."
            : $"{withExamples} of {collection.Requests.Count} request(s) have examples to serve.";
        // Show what will be served before the server starts.
        foreach (var route in Dispatch.Application.Mock.MockRouteTable.Build(collection.Requests).Routes)
            Routes.Add(new MockRouteItem(route));
        UpdateStats();
    }

    public string Title => $"Mock server · {_collection.Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.Mock;
    public double Width => 1140;
    public double Height => 780;
    public string Hint { get; }
    public bool HasNoExamples { get; }
    public string CollectionName => _collection.Name;

    // ---- Settings ----
    [ObservableProperty] private decimal _port = 3000;
    [ObservableProperty] private decimal? _grpcPort;
    [ObservableProperty] private decimal _latencyMs;
    [ObservableProperty] private decimal _jitterMs;
    [ObservableProperty] private decimal _errorRatePercent;
    [ObservableProperty] private decimal _errorStatus = 500;
    [ObservableProperty] private decimal _dropRatePercent;
    [ObservableProperty] private bool _cors = true;
    [ObservableProperty] private bool _public;
    [ObservableProperty] private bool _dynamicData;
    [ObservableProperty] private bool _stateful;
    [ObservableProperty] private decimal? _seed;

    /// <summary>Replay speed for recorded WebSocket / SSE sessions (1 = as recorded, 0 = no delays).</summary>
    [ObservableProperty] private decimal _sessionSpeed = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(CopyUrlCommand), nameof(OpenInBrowserCommand),
        nameof(CopyRouteUrlCommand), nameof(ResetStateCommand))]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(DisplayUrl))]
    private bool _isRunning;

    [ObservableProperty] private string _status = "Review the settings on the left, then click Start.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayUrl))]
    private string? _baseUrl;

    [ObservableProperty] private string? _grpcUrl;

    // ---- Log ----
    [ObservableProperty] private string _logSearch = "";
    [ObservableProperty] private bool _problemsOnly;

    /// <summary>0 = routes, 1 = request log.</summary>
    [ObservableProperty] private int _selectedTab;

    public string StateText => IsRunning ? "Running" : "Stopped";
    public string DisplayUrl => IsRunning && BaseUrl is not null ? BaseUrl : $"http://localhost:{(int)Port}";
    public bool HasNoRoutes => Routes.Count == 0;
    public bool HasNoLog => VisibleLog.Count == 0;
    public bool HasFaultSimulation => LatencyMs > 0 || JitterMs > 0 || ErrorRatePercent > 0 || DropRatePercent > 0;

    public ObservableCollection<MockRouteItem> Routes { get; } = [];
    public ObservableCollection<MockLogEntry> Log { get; } = [];
    public ObservableCollection<MockLogEntry> VisibleLog { get; } = [];
    public ObservableCollection<StatTile> Stats { get; } = [];

    partial void OnLogSearchChanged(string value) => RefreshVisibleLog();
    partial void OnProblemsOnlyChanged(bool value) => RefreshVisibleLog();
    partial void OnLatencyMsChanged(decimal value) => OnPropertyChanged(nameof(HasFaultSimulation));
    partial void OnJitterMsChanged(decimal value) => OnPropertyChanged(nameof(HasFaultSimulation));
    partial void OnErrorRatePercentChanged(decimal value) => OnPropertyChanged(nameof(HasFaultSimulation));
    partial void OnDropRatePercentChanged(decimal value) => OnPropertyChanged(nameof(HasFaultSimulation));
    partial void OnPortChanged(decimal value) => OnPropertyChanged(nameof(DisplayUrl));

    /// <summary>Adds a handled request to the log, the hit counters and the stats.</summary>
    public void Record(MockLogEntry entry)
    {
        Log.Insert(0, entry);
        if (Log.Count > 500)
        {
            VisibleLog.Remove(Log[^1]);
            Log.RemoveAt(Log.Count - 1);
        }
        if (LogMatches(entry))
            VisibleLog.Insert(0, entry);

        OnPropertyChanged(nameof(HasNoLog));
        _served++;
        _totalMs += entry.Milliseconds;
        if (IsFault(entry))
            _faults++;
        else if (entry.Matched is null || entry.Note == "no example")
            _unmatched++;
        else
            _matched++;
        if (entry.Matched is not null && Routes.Where(r => r.Matches(entry.Method, entry.Path)).MaxBy(r => r.Specificity) is { } route)
            route.Hits++;
        UpdateStats();
    }

    private static bool IsFault(MockLogEntry e) => e.Note is { } n && (n.Contains("injected", StringComparison.OrdinalIgnoreCase) ||
                                                                         n.Contains("dropped", StringComparison.OrdinalIgnoreCase));

    private static bool IsProblem(MockLogEntry e) => e.Matched is null || e.Status >= 400 || e.Status == 0 && e.Method != "gRPC";

    private bool LogMatches(MockLogEntry e)
    {
        if (ProblemsOnly && !IsProblem(e))
            return false;
        var q = LogSearch.Trim();
        return q.Length == 0 || e.Path.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Method.Equals(q, StringComparison.OrdinalIgnoreCase)
               || e.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) == q
               || (e.Matched?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void RefreshVisibleLog()
    {
        VisibleLog.Clear();
        foreach (var e in Log.Where(LogMatches))
            VisibleLog.Add(e);
        OnPropertyChanged(nameof(HasNoLog));
    }

    private void UpdateStats()
    {
        Stats.Clear();
        Stats.Add(new StatTile("Requests served", _served.ToString("N0")));
        Stats.Add(new StatTile("Matched an example", _matched.ToString("N0"), null, _matched > 0 ? ReportBrushes.Good : null));
        Stats.Add(new StatTile("No route / example", _unmatched.ToString("N0"), "404 / 501", _unmatched > 0 ? ReportBrushes.Warning : null));
        Stats.Add(new StatTile("Simulated faults", _faults.ToString("N0"), "errors + drops", _faults > 0 ? ReportBrushes.Bad : null));
        Stats.Add(new StatTile("Average time", _served == 0 ? "–" : $"{_totalMs / _served:0} ms"));
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        try
        {
            await _server.StartAsync(_collection.Requests, new MockServerOptions
            {
                Port = (int)Port,
                GrpcPort = GrpcPort is { } g and > 0 ? (int)g : null,
                LatencyMs = (int)LatencyMs,
                LatencyJitterMs = (int)JitterMs,
                ErrorRate = (double)ErrorRatePercent / 100,
                ErrorStatus = (int)ErrorStatus,
                DropRate = (double)DropRatePercent / 100,
                Cors = Cors,
                Public = Public,
                DynamicData = DynamicData,
                Stateful = Stateful,
                Seed = Seed is { } seed ? (int)seed : null,
                SessionSpeed = (double)SessionSpeed
            });
            BaseUrl = _server.BaseUrl?.ToString().TrimEnd('/');
            GrpcUrl = _server.GrpcUrl?.ToString().TrimEnd('/');
            IsRunning = true;
            Status = $"Serving {Routes.Count(r => r.HasExamples)} route(s) at {BaseUrl}" + (GrpcUrl is null ? "" : $" · gRPC at {GrpcUrl}") +
                     (HasFaultSimulation ? " · fault simulation is ON" : "") +
                     (_server.Warnings.Count > 0 ? $" · {string.Join("; ", _server.Warnings)}" : "");
            Routes.Clear();
            foreach (var r in _server.Routes)
                Routes.Add(new MockRouteItem(r));
            foreach (var route in _server.GrpcRoutes)
                Routes.Add(new MockRouteItem(route));
            OnPropertyChanged(nameof(HasNoRoutes));
        }
        catch (Exception ex)
        {
            Status = $"Could not start: {ex.Message}";
            await _server.StopAsync();
        }
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        await _server.StopAsync();
        IsRunning = false;
        Status = $"Stopped · served {_served:N0} request(s).";
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task CopyUrlAsync()
    {
        await _clipboard.SetTextAsync(BaseUrl ?? "");
        Status = $"Copied {BaseUrl}. Use it as your client's base URL.";
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void OpenInBrowser()
    {
        if (BaseUrl is null)
            return;
        try
        {
            ShellOpener.Open(BaseUrl);
        }
        catch (Exception ex)
        {
            Status = $"Could not open a browser: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task CopyRouteUrlAsync(MockRouteItem? route)
    {
        if (route is null)
            return;
        var url = route.IsGrpc ? $"{GrpcUrl ?? BaseUrl}{route.Template}" : BaseUrl + (route.Template == "/" ? "" : route.Template);
        // Recorded WebSocket sessions are reached over ws://.
        if (route.Method == Dispatch.Application.Mock.SessionMethods.WebSocket && url.StartsWith("http", StringComparison.Ordinal))
            url = "ws" + url[4..];
        await _clipboard.SetTextAsync(url);
        Status = $"Copied {url}";
    }

    [RelayCommand]
    private void ClearLog()
    {
        Log.Clear();
        VisibleLog.Clear();
        OnPropertyChanged(nameof(HasNoLog));
        _served = _matched = _unmatched = _faults = 0;
        _totalMs = 0;
        foreach (var r in Routes)
            r.Hits = 0;
        UpdateStats();
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void ResetState()
    {
        _server.ResetState();
        Status = "Stored items cleared; lists start again from their examples.";
    }

    public void OnClosed() => _ = _server.StopAsync();
}

public sealed record TimelineBar(double Height, string Tooltip, bool HasErrors);

/// <summary>A per-request row of the load-test report, pre-formatted for the table.</summary>
public sealed record LoadRequestRow(LoadRequestStats Stats)
{
    public string Name => Stats.Name;
    public string Count => Stats.Count.ToString("N0");
    public string Errors => Stats.Errors.ToString("N0");
    public bool HasErrors => Stats.Errors > 0;
    public string ErrorRate => Stats.Count == 0 ? "0%" : $"{100.0 * Stats.Errors / Stats.Count:0.0}%";
    public string Mean => $"{Stats.Latency.Mean:0}";
    public string P50 => $"{Stats.Latency.P50:0}";
    public string P90 => $"{Stats.Latency.P90:0}";
    public string P95 => $"{Stats.Latency.P95:0}";
    public string P99 => $"{Stats.Latency.P99:0}";
    public string Max => $"{Stats.Latency.Max:0}";
    public string Statuses => string.Join("  ", Stats.Statuses.OrderBy(s => s.Key).Select(s => $"{s.Key}×{s.Value}"));
}

public sealed partial class LoadTestViewModel : ObservableObject, ITool
{
    private readonly LoadTester _tester;
    private readonly RequestCollection _collection;
    private readonly IDialogService _dialogs;
    private readonly Func<ApiEnvironment?> _environment;
    private CancellationTokenSource? _cts;

    public LoadTestViewModel(RequestCollection collection, LoadTester tester, IDialogService dialogs, Func<ApiEnvironment?> environment)
    {
        _collection = collection;
        _tester = tester;
        _dialogs = dialogs;
        _environment = environment;
        foreach (var request in collection.Requests.OrderBy(r => r.Folder).ThenBy(r => r.SortOrder))
            Requests.Add(new SelectableRequest(request) { IsSelected = false });
        if (Requests.Count > 0)
            Requests[0].IsSelected = true;
    }

    public string Title => $"Load test · {_collection.Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.LoadTest;
    public double Width => 1120;
    public double Height => 780;

    public ObservableCollection<SelectableRequest> Requests { get; } = [];
    public ObservableCollection<TimelineBar> Timeline { get; } = [];
    public ObservableCollection<LoadRequestStats> PerRequest { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];

    // Report (filled when a run finishes).
    public ObservableCollection<StatTile> ReportStats { get; } = [];
    public ObservableCollection<StatTile> LatencyTiles { get; } = [];
    public ObservableCollection<Dispatch.Application.Reporting.ReportInsight> Insights { get; } = [];
    public ObservableCollection<ReportBar> Histogram { get; } = [];
    public ObservableCollection<ReportBar> StatusBars { get; } = [];
    public ObservableCollection<LoadRequestRow> ReportRequests { get; } = [];

    [ObservableProperty] private decimal _users = 10;
    [ObservableProperty] private decimal _durationSeconds = 30;
    [ObservableProperty] private decimal _rampUpSeconds;
    [ObservableProperty] private decimal _thinkTimeMs;
    [ObservableProperty] private bool _runChecks = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ExportCommand), nameof(OpenReportCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _status = "Pick the requests each virtual user runs (in order), then Start.";
    [ObservableProperty] private string _requestsPerSecond = "–";
    [ObservableProperty] private string _totalRequests = "–";
    [ObservableProperty] private string _errorRate = "–";
    [ObservableProperty] private string _p50 = "–";
    [ObservableProperty] private string _p95 = "–";
    [ObservableProperty] private string _p99 = "–";
    [ObservableProperty] private string _activeUsers = "–";
    [ObservableProperty] private double _progress;

    /// <summary>0 = live view, 1 = report.</summary>
    [ObservableProperty] private int _selectedTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(OpenReportCommand))]
    private LoadReport? _report;

    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private Avalonia.Media.IBrush _verdictBrush = ReportBrushes.Neutral;
    [ObservableProperty] private string _reportSubtitle = "";

    public bool HasReport => Report is not null;
    public bool HasSampleErrors => Errors.Count > 0;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var journey = Requests.Where(r => r.IsSelected).Select(r => r.Request).ToList();
        if (journey.Count == 0)
        {
            Status = "Select at least one request.";
            return;
        }
        IsRunning = true;
        SelectedTab = 0;
        Timeline.Clear();
        PerRequest.Clear();
        Errors.Clear();
        OnPropertyChanged(nameof(HasSampleErrors));
        _cts = new CancellationTokenSource();
        var duration = TimeSpan.FromSeconds((double)Math.Max(1, DurationSeconds));
        var rampUp = TimeSpan.FromSeconds((double)RampUpSeconds);
        Status = "Running…";

        var progress = new Progress<LoadSnapshot>(s =>
        {
            Show(s.TotalRequests, s.RequestsPerSecond, s.Errors, s.Latency, s.ActiveUsers);
            Progress = Math.Min(100, 100 * s.Elapsed.TotalSeconds / (duration + rampUp).TotalSeconds);
            UpdateTimeline(s.Timeline);
        });

        try
        {
            var report = await _tester.RunAsync(new LoadOptions
            {
                Requests = journey,
                VirtualUsers = (int)Math.Max(1, Users),
                Duration = duration,
                RampUp = rampUp,
                ThinkTimeMs = (int)ThinkTimeMs,
                Environment = _environment(),
                CollectionVariables = _collection.Variables,
                RunChecks = RunChecks
            }, progress, _cts.Token);

            ShowReport(report);
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Fills the live panel and the report tab from a finished run, and switches to the report.</summary>
    public void ShowReport(LoadReport report)
    {
        Show(report.TotalRequests, report.RequestsPerSecond, report.Errors, report.Latency, 0);
        UpdateTimeline(report.Timeline);
        PerRequest.Clear();
        foreach (var r in report.PerRequest)
            PerRequest.Add(r);
        Errors.Clear();
        foreach (var e in report.SampleErrors)
            Errors.Add(e);
        OnPropertyChanged(nameof(HasSampleErrors));
        Progress = 100;
        Status = $"Done: {report.TotalRequests:N0} requests in {report.Duration.TotalSeconds:0.0} s" + (report.Stopped ? " (stopped)" : "") +
                 " · see the Report tab for the full analysis.";

        Verdict = LoadReportWriter.Verdict(report);
        VerdictBrush = ReportBrushes.ForVerdict(Verdict);
        ReportSubtitle = $"{report.StartedAt:yyyy-MM-dd HH:mm:ss} · {report.Duration.TotalSeconds:0.0} s · {report.VirtualUsers} virtual user(s) · " +
                         $"ramp-up {report.RampUp.TotalSeconds:0} s · think time {report.ThinkTimeMs} ms" + (report.Stopped ? " · stopped early" : "");

        var l = report.Latency;
        ReportStats.Clear();
        ReportStats.Add(new StatTile("Requests", report.TotalRequests.ToString("N0")));
        ReportStats.Add(new StatTile("Throughput (req/s)", $"{report.RequestsPerSecond:0.0}", $"peak {report.PeakRequestsPerSecond:0}/s"));
        ReportStats.Add(new StatTile("Error rate", $"{report.ErrorRate * 100:0.0}%", $"{report.Errors:N0} failed",
            report.Errors > 0 ? ReportBrushes.Bad : ReportBrushes.Good));
        ReportStats.Add(new StatTile("Average", $"{l.Mean:0} ms", $"median {l.P50:0} ms"));
        ReportStats.Add(new StatTile("p95", $"{l.P95:0} ms"));
        ReportStats.Add(new StatTile("Received", ReportBrushes.Bytes(report.TotalBytes)));

        LatencyTiles.Clear();
        foreach (var (label, value) in new[] { ("min", l.Min), ("p50", l.P50), ("p90", l.P90), ("p95", l.P95), ("p99", l.P99), ("max", l.Max) })
            LatencyTiles.Add(new StatTile(label, $"{value:0} ms"));

        Insights.Clear();
        foreach (var i in LoadReportWriter.Insights(report))
            Insights.Add(i);

        Histogram.Clear();
        var buckets = LoadReportWriter.Trim(report.Histogram);
        var bucketMax = buckets.Count == 0 ? 1 : Math.Max(1, buckets.Max(b => b.Count));
        foreach (var b in buckets)
            Histogram.Add(new ReportBar(b.Label, b.Count, bucketMax, ReportBrushes.Accent));

        StatusBars.Clear();
        var statusMax = report.Statuses.Count == 0 ? 1 : Math.Max(1, report.Statuses.Values.Max());
        foreach (var s in report.Statuses.OrderBy(s => s.Key))
            StatusBars.Add(new ReportBar(s.Key == "error" ? "No response" : s.Key, s.Value, statusMax, ReportBrushes.ForStatus(s.Key)));

        ReportRequests.Clear();
        foreach (var r in report.PerRequest.OrderByDescending(r => r.Latency.P95))
            ReportRequests.Add(new LoadRequestRow(r));

        Report = report;
        SelectedTab = 1;
    }

    private void Show(int total, double rps, int errors, LatencyStats latency, int users)
    {
        TotalRequests = total.ToString("N0");
        RequestsPerSecond = rps.ToString("0.0");
        ErrorRate = total == 0 ? "0%" : $"{100.0 * errors / total:0.0}%";
        P50 = $"{latency.P50:0} ms";
        P95 = $"{latency.P95:0} ms";
        P99 = $"{latency.P99:0} ms";
        ActiveUsers = users.ToString();
    }

    private void UpdateTimeline(IReadOnlyList<LoadSecond> seconds)
    {
        Timeline.Clear();
        var max = Math.Max(1, seconds.Count == 0 ? 1 : seconds.Max(s => s.Requests));
        foreach (var s in seconds.TakeLast(120))
            Timeline.Add(new TimelineBar(4 + 116.0 * s.Requests / max, $"{s.Second}s: {s.Requests} req, {s.Errors} errors, p95 {s.P95:0} ms", s.Errors > 0));
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    private bool CanExport() => !IsRunning && Report is not null;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        if (Report is null)
            return;
        var (extension, content, filter) = format switch
        {
            "json" => (".json", LoadReportWriter.Json(Report, _collection.Name), new FileFilter("JSON", "*.json")),
            "csv" => (".csv", LoadReportWriter.Csv(Report), new FileFilter("CSV", "*.csv")),
            _ => (".html", LoadReportWriter.Html(Report, _collection.Name), new FileFilter("HTML", "*.html"))
        };
        var path = await _dialogs.SaveFileAsync("Save load test report",
            Application.Interop.DispatchFormat.Slug(_collection.Name) + "-load" + extension, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Report saved to {path}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void OpenReport()
    {
        if (Report is null)
            return;
        try
        {
            ShellOpener.OpenTemp(_collection.Name + "-load", ".html", System.Text.Encoding.UTF8.GetBytes(LoadReportWriter.Html(Report, _collection.Name)));
        }
        catch (Exception ex)
        {
            Status = $"Could not open the report: {ex.Message}";
        }
    }

    public void OnClosed() => _cts?.Cancel();
}
