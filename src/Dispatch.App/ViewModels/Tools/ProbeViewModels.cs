using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Interop;
using Dispatch.Application.Minimize;
using Dispatch.Application.RateLimits;
using Dispatch.Application.Reporting;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>Shrinks one request to the parts its outcome depends on (delta debugging).</summary>
public sealed partial class MinimizeViewModel : ObservableObject, ITool
{
    private readonly ApiRequest _request;
    private readonly RequestMinimizer _minimizer;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly Func<ApiEnvironment?> _environment;
    private readonly IReadOnlyList<KeyValueItem>? _collectionVariables;
    private readonly Action<ApiRequest> _open;
    private CancellationTokenSource? _cts;
    private MinimizeReport? _report;

    public MinimizeViewModel(ApiRequest request, RequestMinimizer minimizer, IDialogService dialogs, IClipboardService clipboard,
        Func<ApiEnvironment?> environment, IReadOnlyList<KeyValueItem>? collectionVariables, Action<ApiRequest> open)
    {
        _request = request;
        _minimizer = minimizer;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _environment = environment;
        _collectionVariables = collectionVariables;
        _open = open;
        PartCount = RequestMinimizer.CountParts(request);
    }

    public string Title => $"Minimize · {_request.Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.Minimize;
    public double Width => 980;
    public double Height => 700;

    public string RequestLine => $"{_request.Method.ToString().ToUpperInvariant()} {_request.Url}";
    public int PartCount { get; }

    public bool HasSideEffects => _request.Kind == RequestKind.Http && _request.Method is HttpVerb.Post or HttpVerb.Put or HttpVerb.Patch or HttpVerb.Delete;

    public static IReadOnlyList<string> MatchOptions { get; } =
    [
        "Same status code",
        "Same status class (2xx, 4xx, …)",
        "Same status and failing tests",
        "Body still contains text"
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsText))]
    private int _matchIndex;

    public bool NeedsText => MatchIndex == 3;

    [ObservableProperty] private string _bodyContains = string.Empty;
    [ObservableProperty] private decimal _maxRequests = 300;
    [ObservableProperty] private bool _deep = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MinimizeCommand), nameof(StopCommand), nameof(ExportCommand), nameof(OpenMinimalCommand), nameof(CopyCurlCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _status = "Each probe is a real request. The original response's outcome is kept while parts are removed.";
    [ObservableProperty] private int _requestsSent;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _minimalCurl = string.Empty;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _confirmed;

    public ObservableCollection<string> Required { get; } = [];
    public ObservableCollection<string> Removed { get; } = [];

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task MinimizeAsync()
    {
        Required.Clear();
        Removed.Clear();
        HasResult = false;
        IsRunning = true;
        RequestsSent = 0;
        _cts = new CancellationTokenSource();
        Status = "Sending the original request…";
        var options = new MinimizeOptions
        {
            Match = (OutcomeMatch)Math.Clamp(MatchIndex, 0, 3),
            BodyContains = BodyContains,
            MaxRequests = (int)Math.Max(2, MaxRequests),
            Deep = Deep,
            Environment = _environment(),
            CollectionVariables = _collectionVariables
        };
        var progress = new Progress<MinimizeProgress>(p =>
        {
            RequestsSent = p.RequestsSent;
            Status = $"{p.RequestsSent} request(s) sent · {p.Message}";
        });
        try
        {
            _report = await _minimizer.MinimizeAsync(_request, options, progress, _cts.Token);
            Show(_report);
        }
        catch (Exception ex)
        {
            Status = $"Minimize error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Show(MinimizeReport report)
    {
        RequestsSent = report.RequestsSent;
        Summary = MinimizeReportWriter.Summary(report);
        if (report.Error is not null && report.RequestsSent <= 1)
        {
            Status = report.Error;
            return;
        }
        foreach (var unit in MinimizeReportWriter.RequiredLeaves(report))
            Required.Add(unit.Label);
        foreach (var unit in MinimizeReportWriter.TopRemoved(report))
            Removed.Add(unit.Label);
        MinimalCurl = MinimizeReportWriter.Curl(report.Minimal);
        Confirmed = report.Confirmed;
        HasResult = true;
        Status = $"{report.RequestsSent} request(s) in {report.Duration.TotalSeconds:0.0} s" +
                 (report.Error is not null ? $" · {report.Error}"
                     : report.Confirmed ? " · confirmed: the minimal request reproduces the outcome"
                     : " · not confirmed: re-sending the minimal request gave a different outcome (flaky endpoint?)") +
                 (report.BudgetExhausted ? " · request budget ran out, result may not be fully minimal" : "");
    }

    private bool CanStart() => !IsRunning;
    private bool CanUseResult() => !IsRunning && _report is not null && HasResult;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanUseResult))]
    private void OpenMinimal()
    {
        if (_report is null)
            return;
        var copy = _report.Minimal.Clone(newIdentity: true);
        _open(copy);
    }

    [RelayCommand(CanExecute = nameof(CanUseResult))]
    private async Task CopyCurlAsync()
    {
        await _clipboard.SetTextAsync(MinimalCurl);
        Status = "Copied the minimal request as cURL.";
    }

    [RelayCommand(CanExecute = nameof(CanUseResult))]
    private async Task ExportAsync(string? format)
    {
        if (_report is null)
            return;
        var (ext, content, filter) = format == "json"
            ? (".json", MinimizeReportWriter.Json(_report), new FileFilter("JSON", "*.json"))
            : (".html", MinimizeReportWriter.Html(_report), new FileFilter("HTML", "*.html"));
        var path = await _dialogs.SaveFileAsync("Save minimize report", DispatchFormat.Slug(_request.Name) + "-minimize" + ext, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Report saved to {path}";
        }
    }

    public void OnClosed() => _cts?.Cancel();
}

/// <summary>Probes one request to discover its real rate-limit policy.</summary>
public sealed partial class RateLimitViewModel : ObservableObject, ITool
{
    private readonly ApiRequest _request;
    private readonly RateLimitProber _prober;
    private readonly IDialogService _dialogs;
    private readonly Func<ApiEnvironment?> _environment;
    private readonly IReadOnlyList<KeyValueItem>? _collectionVariables;
    private CancellationTokenSource? _cts;
    private RateLimitReport? _report;

    public RateLimitViewModel(ApiRequest request, RateLimitProber prober, IDialogService dialogs, Func<ApiEnvironment?> environment,
        IReadOnlyList<KeyValueItem>? collectionVariables)
    {
        _request = request;
        _prober = prober;
        _dialogs = dialogs;
        _environment = environment;
        _collectionVariables = collectionVariables;
    }

    public string Title => $"Rate limit · {_request.Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.RateLimit;
    public double Width => 1000;
    public double Height => 700;
    public string RequestLine => $"{_request.Method.ToString().ToUpperInvariant()} {_request.Url}";

    [ObservableProperty] private decimal _maxRequests = 400;
    [ObservableProperty] private decimal _maxSeconds = 180;
    [ObservableProperty] private decimal _concurrency = 4;
    [ObservableProperty] private decimal _pollMs = 1000;
    [ObservableProperty] private bool _treat503AsThrottle;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProbeCommand), nameof(StopCommand), nameof(ExportCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _status = "Sends a burst until throttled, waits for recovery, then measures how capacity refills.";
    [ObservableProperty] private int _sent;
    [ObservableProperty] private int _accepted;
    [ObservableProperty] private int _throttled;
    [ObservableProperty] private string _phase = "—";
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _capacityText = "—";
    [ObservableProperty] private string _recoveryText = "—";
    [ObservableProperty] private string _refillText = "—";
    [ObservableProperty] private string _advertisedText = "—";

    public ObservableCollection<ReportInsight> Insights { get; } = [];

    /// <summary>Last samples, newest first, for the live log.</summary>
    public ObservableCollection<string> Log { get; } = [];

    [RelayCommand(CanExecute = nameof(CanProbe))]
    private async Task ProbeAsync()
    {
        Insights.Clear();
        Log.Clear();
        Sent = Accepted = Throttled = 0;
        Summary = string.Empty;
        CapacityText = RecoveryText = RefillText = AdvertisedText = "—";
        IsRunning = true;
        _cts = new CancellationTokenSource();
        Status = "Probing… only probe APIs you are authorised to test.";
        var statuses = new HashSet<int> { 429 };
        if (Treat503AsThrottle)
            statuses.Add(503);
        var options = new RateLimitOptions
        {
            MaxRequests = (int)Math.Max(2, MaxRequests),
            MaxDuration = TimeSpan.FromSeconds((double)Math.Max(5, MaxSeconds)),
            Concurrency = (int)Math.Max(1, Concurrency),
            PollInterval = TimeSpan.FromMilliseconds((double)Math.Max(50, PollMs)),
            ThrottleStatuses = statuses,
            Environment = _environment(),
            CollectionVariables = _collectionVariables
        };
        var progress = new Progress<RateLimitSample>(s =>
        {
            Sent++;
            if (s.Throttled)
                Throttled++;
            else if (!s.Error)
                Accepted++;
            Phase = s.Phase.ToString();
            Log.Insert(0, $"{s.AtMs / 1000,7:0.000} s  {s.Phase,-9} {(s.Error ? "error" : s.Status.ToString())}" +
                          (s.Remaining is { } r ? $"  remaining {r}" : "") + (s.RetryAfterSeconds is { } ra ? $"  retry-after {ra:0.#}s" : ""));
            if (Log.Count > 300)
                Log.RemoveAt(Log.Count - 1);
        });
        try
        {
            _report = await _prober.ProbeAsync(_request, options, progress, _cts.Token);
            Show(_report);
        }
        catch (Exception ex)
        {
            Status = $"Probe error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            Phase = "—";
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Show(RateLimitReport report)
    {
        Summary = report.Summary;
        CapacityText = report.Throttled ? $"{report.BurstCapacity} at {report.BurstRate:0.#} req/s" : $"≥ {report.BurstCapacity} (not throttled)";
        RecoveryText = report.Recovery is null ? "—"
            : RateLimitReport.Seconds(report.Recovery) + (report.RetryAfterSeconds is { } ra ? $" (Retry-After {ra:0.#} s)" : " (no Retry-After)");
        RefillText = report.Refill switch
        {
            RefillKind.FixedWindow => $"Fixed window ≈ {RateLimitReport.Seconds(report.WindowEstimate)}",
            RefillKind.Gradual => $"Gradual {(report.RefillIsLowerBound ? "≥" : "≈")} {report.RefillPerSecond:0.##} req/s",
            RefillKind.NoLimitObserved => "No limit observed",
            _ => "Unknown"
        };
        AdvertisedText = report.Advertised is { } a ? $"{a.Limit?.ToString() ?? "?"} ({a.HeaderStyle})" : "none";
        foreach (var insight in report.Insights)
            Insights.Add(insight);
        Status = $"{report.RequestsSent} request(s) in {report.Duration.TotalSeconds:0.0} s" + (report.Stopped ? " · stopped early" : "");
    }

    private bool CanProbe() => !IsRunning;
    private bool CanExport() => !IsRunning && _report is not null;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        if (_report is null)
            return;
        var (ext, content, filter) = format == "json"
            ? (".json", RateLimitReportWriter.Json(_report), new FileFilter("JSON", "*.json"))
            : (".html", RateLimitReportWriter.Html(_report), new FileFilter("HTML", "*.html"));
        var path = await _dialogs.SaveFileAsync("Save rate-limit report", DispatchFormat.Slug(_request.Name) + "-ratelimit" + ext, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Report saved to {path}";
        }
    }

    public void OnClosed() => _cts?.Cancel();
}
