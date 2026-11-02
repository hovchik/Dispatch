using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Compare;
using Dispatch.Application.Diff;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>An environment the user can pick for one side of a comparison ("No environment" is represented by null).</summary>
public sealed record CompareEnvironmentChoice(string Name, ApiEnvironment? Environment)
{
    public override string ToString() => Name;
}

/// <summary>One compared request, pre-formatted for the results table.</summary>
public sealed record CompareRow(CompareRequestResult Result, IDialogService Dialogs)
{
    public string Name => Result.Name;
    public string Label => Result.Label;
    public string LeftStatus => Result.Left.StatusText;
    public string RightStatus => Result.Right.StatusText;
    public string LeftMs => Result.Left.HasResponse ? $"{Result.Left.Milliseconds:0} ms" : "–";
    public string RightMs => Result.Right.HasResponse ? $"{Result.Right.Milliseconds:0} ms" : "–";
    public string Delta => Result.Left.HasResponse && Result.Right.HasResponse ? $"{Result.LatencyDeltaPercent:+0;-0;0}%" : "";
    public string Summary => Result.Summary;
    public string Mark => Result.Outcome switch { CompareOutcome.Same => "=", CompareOutcome.Different => "≠", _ => "✗" };
    public IBrush MarkBrush => Result.Outcome switch
    {
        CompareOutcome.Same => ReportBrushes.Good,
        CompareOutcome.Different => ReportBrushes.Warning,
        _ => ReportBrushes.Bad
    };
    public bool IsDifferent => Result.Outcome != CompareOutcome.Same;
    public IReadOnlyList<string> Details =>
        new[] { Result.Left.Error is null ? null : $"left: {Result.Left.Error}", Result.Right.Error is null ? null : $"right: {Result.Right.Error}" }
            .Where(d => d is not null).Select(d => d!)
            .Concat(Result.BodyChanges.Take(20).Select(c => c.ToString()))
            .Concat(Result.BodyChanges.Count > 20 ? [$"… {Result.BodyChanges.Count - 20} more"] : [])
            .Concat(Result.HeaderChanges.Select(h => $"header {h}"))
            .Concat(Result.Left.FailedChecks.Select(c => $"check failed on left: {c.Name}{(c.Message is null ? "" : $" — {c.Message}")}"))
            .Concat(Result.Right.FailedChecks.Select(c => $"check failed on right: {c.Name}{(c.Message is null ? "" : $" — {c.Message}")}"))
            .ToList();
    public bool HasDetails => Details.Count > 0;
    public bool CanDiff => Result.Left.HasResponse && Result.Right.HasResponse && Result.BodyDiffers;

    public RelayCommand OpenDiffCommand => new(() => Dialogs.ShowTool(new DiffViewModel($"Diff · {Result.Name}", "Left", "Right",
        Pretty(Result.Left.Body), Pretty(Result.Right.Body), Result.Left.StatusText, Result.Right.StatusText)));

    private static string Pretty(string body)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(body)?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? body;
        }
        catch (System.Text.Json.JsonException)
        {
            return body;
        }
    }
}

/// <summary>Runs a collection against two environments or base URLs and shows what differs, request by request.</summary>
public sealed partial class CompareViewModel : ObservableObject, ITool
{
    private readonly RequestCollection _collection;
    private readonly EnvironmentComparer _comparer;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public CompareViewModel(RequestCollection collection, EnvironmentComparer comparer, IDialogService dialogs,
        IReadOnlyList<ApiEnvironment> environments, ApiEnvironment? active)
    {
        _collection = collection;
        _comparer = comparer;
        _dialogs = dialogs;
        foreach (var request in collection.Requests.OrderBy(r => r.Folder).ThenBy(r => r.SortOrder))
            Requests.Add(new SelectableRequest(request));
        Environments.Add(new CompareEnvironmentChoice("No environment", null));
        foreach (var environment in environments)
            Environments.Add(new CompareEnvironmentChoice(environment.Name, environment));
        _leftEnvironment = Environments.FirstOrDefault(e => e.Environment?.Id == active?.Id) ?? Environments[0];
        _rightEnvironment = Environments.Skip(1).FirstOrDefault(e => e != _leftEnvironment) ?? Environments[0];
    }

    public string Title => $"Compare environments · {_collection.Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.CompareEnvironments;
    public double Width => 1120;
    public double Height => 760;

    public ObservableCollection<SelectableRequest> Requests { get; } = [];
    public ObservableCollection<CompareEnvironmentChoice> Environments { get; } = [];
    public ObservableCollection<CompareRow> Rows { get; } = [];
    public ObservableCollection<Dispatch.Application.Reporting.ReportInsight> Insights { get; } = [];

    [ObservableProperty] private CompareEnvironmentChoice? _leftEnvironment;
    [ObservableProperty] private CompareEnvironmentChoice? _rightEnvironment;
    [ObservableProperty] private string _leftBaseUrl = string.Empty;
    [ObservableProperty] private string _rightBaseUrl = string.Empty;
    [ObservableProperty] private string _ignorePaths = string.Empty;
    [ObservableProperty] private bool _runChecks;
    [ObservableProperty] private bool _onlyDifferences;
    [ObservableProperty] private string _status = "Pick two environments (or type base URLs), choose the requests, then Compare.";
    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private IBrush _verdictBrush = ReportBrushes.Neutral;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _leftTitle = "Left";
    [ObservableProperty] private string _rightTitle = "Right";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ExportCommand), nameof(OpenReportCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(OpenReportCommand))]
    private CompareReport? _report;

    public bool HasReport => Report is not null;

    partial void OnOnlyDifferencesChanged(bool value) => Refill();

    private CompareTarget Target(CompareEnvironmentChoice? environment, string baseUrl, string fallback)
    {
        var url = baseUrl.Trim();
        if (url.Length > 0)
        {
            var target = CompareTarget.FromBaseUrl(url);
            return new CompareTarget { Name = target.Name, BaseUrl = target.BaseUrl, Environment = environment?.Environment };
        }
        return environment?.Environment is { } env ? CompareTarget.FromEnvironment(env) : new CompareTarget { Name = fallback };
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var requests = Requests.Where(r => r.IsSelected).Select(r => r.Request).ToList();
        if (requests.Count == 0)
        {
            Status = "Select at least one request.";
            return;
        }
        CompareTarget left, right;
        try
        {
            left = Target(LeftEnvironment, LeftBaseUrl, "left (no environment)");
            right = Target(RightEnvironment, RightBaseUrl, "right (no environment)");
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
            return;
        }
        if (left.Environment is null && left.BaseUrl is null && right.Environment is null && right.BaseUrl is null)
        {
            Status = "Both sides are identical: pick an environment or type a base URL on at least one side.";
            return;
        }

        IsRunning = true;
        Rows.Clear();
        Insights.Clear();
        Report = null;
        Verdict = "";
        VerdictBrush = ReportBrushes.Neutral;
        LeftTitle = left.Name;
        RightTitle = right.Name;
        _cts = new CancellationTokenSource();
        Status = $"Comparing {requests.Count} request(s) on {left.Name} and {right.Name}…";
        var results = new List<CompareRequestResult>();
        var progress = new Progress<CompareRequestResult>(r =>
        {
            results.Add(r);
            if (!OnlyDifferences || r.Outcome != CompareOutcome.Same)
                Rows.Add(new CompareRow(r, _dialogs));
            Status = $"{results.Count} of {requests.Count}: {r.Name} — {r.Summary}";
        });

        try
        {
            var report = await _comparer.RunAsync(new CompareOptions
            {
                Requests = requests,
                Left = left,
                Right = right,
                IgnorePaths = IgnorePaths.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                CollectionVariables = _collection.Variables,
                RunChecks = RunChecks
            }, progress, _cts.Token);
            Show(report);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
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

    private void Show(CompareReport report)
    {
        Report = report;
        Verdict = report.Verdict;
        VerdictBrush = report.Verdict switch { "IDENTICAL" => ReportBrushes.Good, "DIFFERENT" => ReportBrushes.Warning, "NO DATA" => ReportBrushes.Neutral, _ => ReportBrushes.Bad };
        Summary = $"{report.Same} identical · {report.Different} different · {report.Failed} failed · " +
                  $"median latency {report.MedianLatencyDeltaPercent:+0;-0;0}% on {report.RightName} · {report.Duration.TotalSeconds:0.0} s";
        Status = report.HasDifferences
            ? $"Done: {report.Different + report.Failed} of {report.Results.Count} request(s) differ. Click a row's Diff to see both bodies."
            : $"Done: all {report.Results.Count} request(s) answered identically.";
        foreach (var insight in CompareReportWriter.Insights(report))
            Insights.Add(insight);
        Refill();
    }

    private void Refill()
    {
        if (Report is null)
            return;
        Rows.Clear();
        foreach (var r in Report.Results.Where(r => !OnlyDifferences || r.Outcome != CompareOutcome.Same))
            Rows.Add(new CompareRow(r, _dialogs));
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
        var (extension, content, filter) = format == "json"
            ? (".json", CompareReportWriter.Json(Report), new FileFilter("JSON", "*.json"))
            : (".html", CompareReportWriter.Html(Report), new FileFilter("HTML", "*.html"));
        var path = await _dialogs.SaveFileAsync("Save comparison report",
            Application.Interop.DispatchFormat.Slug(_collection.Name) + "-compare" + extension, filter);
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
            ShellOpener.OpenTemp(_collection.Name + "-compare", ".html", System.Text.Encoding.UTF8.GetBytes(CompareReportWriter.Html(Report)));
        }
        catch (Exception ex)
        {
            Status = $"Could not open the report: {ex.Message}";
        }
    }

    public void OnClosed() => _cts?.Cancel();
}
