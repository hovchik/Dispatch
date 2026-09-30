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
    public double Width => 1000;
    public double Height => 700;
    public string CollectionName { get; }

    public ObservableCollection<SelectableRequest> Requests { get; } = [];
    public ObservableCollection<RunResultItem> Results { get; } = [];

    [ObservableProperty] private decimal _iterations = 1;
    [ObservableProperty] private decimal _delayMs;
    [ObservableProperty] private bool _stopOnFailure;
    [ObservableProperty] private bool _persistVariables = true;
    [ObservableProperty] private string _dataFile = "";
    [ObservableProperty] private string _dataSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand), nameof(ExportCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _summary = "Choose requests and click Run.";
    [ObservableProperty] private int _passedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private RunResultItem? _selectedResult;

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
                Environment = _environment(),
                CollectionVariables = _collection.Variables,
                CollectionSpec = _collection.SpecLocation,
                Iterations = (int)Math.Max(1, Iterations),
                Data = data,
                DelayMs = (int)DelayMs,
                StopOnFailure = StopOnFailure,
                RecordHistory = false
            }, progress, _cts.Token);

            Summary = $"{_report.TotalRequests} request(s) in {_report.Duration.TotalSeconds:0.00} s · {_report.FailedRequests} failed · " +
                      $"{_report.TotalTests - _report.FailedTests}/{_report.TotalTests} tests passed · avg {_report.AverageResponseTime.TotalMilliseconds:0} ms" +
                      (_report.Stopped ? " · stopped" : "");
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

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

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

public sealed partial class MockServerViewModel : ObservableObject, ITool
{
    private readonly MockServer _server;
    private readonly RequestCollection _collection;
    private readonly IClipboardService _clipboard;

    public MockServerViewModel(RequestCollection collection, GrpcSchemaProvider grpcSchemas, IClipboardService clipboard)
    {
        _collection = collection;
        _clipboard = clipboard;
        _server = new MockServer(grpcSchemas);
        _server.RequestHandled += entry => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Log.Insert(0, entry);
            if (Log.Count > 500)
                Log.RemoveAt(Log.Count - 1);
        });
        var withExamples = collection.Requests.Count(r => r.Examples.Count > 0);
        Hint = withExamples == 0
            ? "No request in this collection has a saved example yet. Send a request and click \"Save as example\" in the response, or import an OpenAPI spec."
            : $"{withExamples} of {collection.Requests.Count} request(s) have examples to serve.";
    }

    public string Title => $"Mock server · {_collection.Name}";
    public double Width => 960;
    public double Height => 660;
    public string Hint { get; }

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _status = "Stopped";
    [ObservableProperty] private string? _baseUrl;

    public ObservableCollection<string> Routes { get; } = [];
    public ObservableCollection<MockLogEntry> Log { get; } = [];

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
                Stateful = Stateful
            });
            IsRunning = true;
            BaseUrl = _server.BaseUrl?.ToString().TrimEnd('/');
            Status = $"Running at {BaseUrl}" + (_server.GrpcUrl is null ? "" : $" · gRPC at {_server.GrpcUrl}") +
                     (_server.Warnings.Count > 0 ? $" · {string.Join("; ", _server.Warnings)}" : "");
            Routes.Clear();
            foreach (var r in _server.Routes)
                Routes.Add($"{r.Method,-7} {r.Template}   → {r.Request.Name} ({r.Request.Examples.Count} example(s))");
            foreach (var route in _server.GrpcRoutes)
                Routes.Add($"gRPC    {route}");
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
        Status = "Stopped";
    }

    [RelayCommand]
    private Task CopyUrlAsync() => _clipboard.SetTextAsync(BaseUrl ?? "");

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    [RelayCommand]
    private void ResetState()
    {
        _server.ResetState();
        Status = $"Running at {BaseUrl} · stored items cleared";
    }

    public void OnClosed() => _ = _server.StopAsync();
}

public sealed record TimelineBar(double Height, string Tooltip, bool HasErrors);

public sealed partial class LoadTestViewModel : ObservableObject, ITool
{
    private readonly LoadTester _tester;
    private readonly RequestCollection _collection;
    private readonly Func<ApiEnvironment?> _environment;
    private CancellationTokenSource? _cts;

    public LoadTestViewModel(RequestCollection collection, LoadTester tester, Func<ApiEnvironment?> environment)
    {
        _collection = collection;
        _tester = tester;
        _environment = environment;
        foreach (var request in collection.Requests.OrderBy(r => r.Folder).ThenBy(r => r.SortOrder))
            Requests.Add(new SelectableRequest(request) { IsSelected = false });
        if (Requests.Count > 0)
            Requests[0].IsSelected = true;
    }

    public string Title => $"Load test · {_collection.Name}";
    public double Width => 1000;
    public double Height => 720;

    public ObservableCollection<SelectableRequest> Requests { get; } = [];
    public ObservableCollection<TimelineBar> Timeline { get; } = [];
    public ObservableCollection<LoadRequestStats> PerRequest { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];

    [ObservableProperty] private decimal _users = 10;
    [ObservableProperty] private decimal _durationSeconds = 30;
    [ObservableProperty] private decimal _rampUpSeconds;
    [ObservableProperty] private decimal _thinkTimeMs;
    [ObservableProperty] private bool _runChecks = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
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
        Timeline.Clear();
        PerRequest.Clear();
        Errors.Clear();
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

            Show(report.TotalRequests, report.RequestsPerSecond, report.Errors, report.Latency, 0);
            UpdateTimeline(report.Timeline);
            foreach (var r in report.PerRequest)
                PerRequest.Add(r);
            foreach (var e in report.SampleErrors)
                Errors.Add(e);
            Progress = 100;
            Status = $"Done: {report.TotalRequests} requests in {report.Duration.TotalSeconds:0.0} s · " +
                     string.Join("  ", report.Statuses.OrderBy(s => s.Key).Select(s => $"{s.Key}×{s.Value}"));
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

    public void OnClosed() => _cts?.Cancel();
}
