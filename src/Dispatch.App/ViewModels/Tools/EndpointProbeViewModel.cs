using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Interop;
using Dispatch.Application.Probes;
using Dispatch.Application.Reporting;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>One endpoint probe (pagination walker, idempotency / race probe or caching checker) on one request.</summary>
public sealed partial class EndpointProbeViewModel : ObservableObject, ITool
{
    private readonly ApiRequest _request;
    private readonly EndpointProbes _probes;
    private readonly IDialogService _dialogs;
    private readonly Func<ApiEnvironment?> _environment;
    private readonly IReadOnlyList<KeyValueItem>? _collectionVariables;
    private CancellationTokenSource? _cts;
    private ProbeReport? _report;

    public EndpointProbeViewModel(ProbeKind kind, ApiRequest request, EndpointProbes probes, IDialogService dialogs, Func<ApiEnvironment?> environment,
        IReadOnlyList<KeyValueItem>? collectionVariables)
    {
        Kind = kind;
        _request = request;
        _probes = probes;
        _dialogs = dialogs;
        _environment = environment;
        _collectionVariables = collectionVariables;
        _status = Intro;
    }

    public ProbeKind Kind { get; }
    public bool IsPagination => Kind == ProbeKind.Pagination;
    public bool IsIdempotency => Kind == ProbeKind.Idempotency;
    public bool IsCaching => Kind == ProbeKind.Caching;

    public string ToolName => Kind switch
    {
        ProbeKind.Pagination => "Pagination walker",
        ProbeKind.Idempotency => "Idempotency & race probe",
        _ => "Caching checker"
    };

    public string Title => $"{ToolName} · {_request.Name}";

    public string? HelpTopic => Kind switch
    {
        ProbeKind.Pagination => Dispatch.Application.Help.HelpCatalog.Pagination,
        ProbeKind.Idempotency => Dispatch.Application.Help.HelpCatalog.Idempotency,
        _ => Dispatch.Application.Help.HelpCatalog.Caching
    };

    public double Width => 1000;
    public double Height => 700;
    public string RequestLine => $"{_request.Method.ToString().ToUpperInvariant()} {_request.Url}";

    public string Intro => Kind switch
    {
        ProbeKind.Pagination => "Follows the pages to the end and checks for duplicates, gaps, totals that don't add up, loops and unstable order.",
        ProbeKind.Idempotency => "Replays the request with the same key, reuses the key with another body, and sends copies at the same moment. It creates data: use a test environment.",
        _ => "Reads the caching headers, then checks that ETag / Last-Modified revalidation really answers 304."
    };

    public string Warning => Kind switch
    {
        ProbeKind.Idempotency => "Sends the request several times and creates data. Only probe APIs you are authorised to test.",
        _ => "Only probe APIs you own or are authorised to test."
    };

    public IReadOnlyList<string> Strategies { get; } = ["Auto", "Link header", "Next URL", "Cursor", "Page number", "Offset"];

    // ---- Pagination settings ----
    [ObservableProperty] private int _strategyIndex;
    [ObservableProperty] private string _itemsPath = string.Empty;
    [ObservableProperty] private string _idField = string.Empty;
    [ObservableProperty] private string _pageParam = string.Empty;
    [ObservableProperty] private string _sizeParam = string.Empty;
    [ObservableProperty] private decimal? _pageSize;
    [ObservableProperty] private decimal _maxPages = 50;
    [ObservableProperty] private bool _edgeCases = true;
    [ObservableProperty] private bool _checkStability = true;

    // ---- Idempotency settings ----
    [ObservableProperty] private string _keyHeader = "Idempotency-Key";
    [ObservableProperty] private decimal _parallel = 5;
    [ObservableProperty] private bool _raceWithoutKey = true;

    // ---- Caching settings ----
    [ObservableProperty] private bool _checkHead = true;

    [ObservableProperty] private decimal _maxRequests = 200;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand), nameof(ExportCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _status;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _verdict = string.Empty;
    [ObservableProperty] private int _sent;
    [ObservableProperty] private int _problems;
    [ObservableProperty] private int _warnings;

    public ObservableCollection<ProbeStat> Stats { get; } = [];
    public ObservableCollection<ReportInsight> Insights { get; } = [];

    /// <summary>Requests sent, newest first.</summary>
    public ObservableCollection<string> Log { get; } = [];

    private bool CanRun() => !IsRunning;
    private bool CanExport() => !IsRunning && _report is not null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        Stats.Clear();
        Insights.Clear();
        Log.Clear();
        Sent = Problems = Warnings = 0;
        Summary = Verdict = string.Empty;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        Status = "Running… " + Warning;
        var progress = new Progress<ProbeStep>(s =>
        {
            Sent++;
            Log.Insert(0, $"{s.AtMs / 1000,7:0.000} s  {s.StatusText,-5} {s.Label}" + (s.Note is null ? "" : $"  · {s.Note}"));
            if (Log.Count > 300)
                Log.RemoveAt(Log.Count - 1);
        });
        try
        {
            var environment = _environment();
            _report = Kind switch
            {
                ProbeKind.Pagination => await _probes.Pagination.WalkAsync(_request, new PaginationOptions
                {
                    Strategy = (PaginationStrategy)Math.Clamp(StrategyIndex, 0, 5),
                    ItemsPath = Blank(ItemsPath),
                    IdField = Blank(IdField),
                    PageParam = Blank(PageParam),
                    SizeParam = Blank(SizeParam),
                    PageSize = PageSize is { } size && size > 0 ? (int)size : null,
                    MaxPages = (int)Math.Max(1, MaxPages),
                    EdgeCases = EdgeCases,
                    CheckStability = CheckStability,
                    MaxRequests = (int)Math.Max(2, MaxRequests),
                    Environment = environment,
                    CollectionVariables = _collectionVariables
                }, progress, _cts.Token),
                ProbeKind.Idempotency => await _probes.Idempotency.ProbeAsync(_request, new IdempotencyOptions
                {
                    KeyHeader = string.IsNullOrWhiteSpace(KeyHeader) ? "Idempotency-Key" : KeyHeader.Trim(),
                    Parallel = (int)Math.Clamp(Parallel, 0, 50),
                    RaceWithoutKey = RaceWithoutKey,
                    MaxRequests = (int)Math.Max(2, MaxRequests),
                    Environment = environment,
                    CollectionVariables = _collectionVariables
                }, progress, _cts.Token),
                _ => await _probes.Caching.CheckAsync(_request, new CacheOptions
                {
                    CheckHead = CheckHead,
                    MaxRequests = (int)Math.Max(2, MaxRequests),
                    Environment = environment,
                    CollectionVariables = _collectionVariables
                }, progress, _cts.Token)
            };
            Show(_report);
        }
        catch (Exception ex)
        {
            Status = $"Probe error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void Show(ProbeReport report)
    {
        Summary = report.Error ?? report.Summary;
        Verdict = report.Verdict;
        Problems = report.Problems;
        Warnings = report.Warnings;
        foreach (var stat in report.Stats)
            Stats.Add(stat);
        foreach (var insight in report.Insights.OrderByDescending(i => i.Level == InsightLevel.Bad).ThenByDescending(i => i.Level == InsightLevel.Warning))
            Insights.Add(insight);
        Status = $"{report.RequestsSent} request(s) in {report.Duration.TotalSeconds:0.0} s" + (report.Stopped ? " · stopped early" : "");
        ExportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        if (_report is null)
            return;
        var (ext, content, filter) = format == "json"
            ? (".json", ProbeReportWriter.Json(_report), new FileFilter("JSON", "*.json"))
            : (".html", ProbeReportWriter.Html(_report), new FileFilter("HTML", "*.html"));
        var path = await _dialogs.SaveFileAsync($"Save {ToolName.ToLowerInvariant()} report",
            DispatchFormat.Slug(_request.Name) + "-" + Kind.ToString().ToLowerInvariant() + ext, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Report saved to {path}";
        }
    }

    public void OnClosed() => _cts?.Cancel();
}
