using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Impact;
using Dispatch.Application.Interop;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>Shows what in the collection depends on fields that changed between a baseline response and the latest one.</summary>
public sealed partial class ImpactViewModel : ObservableObject, ITool
{
    private readonly ApiRequest _request;
    private readonly string? _current;
    private readonly IReadOnlyList<ApiRequest> _collectionRequests;
    private readonly Func<Task<IReadOnlyList<TestFlow>>> _loadFlows;
    private readonly IDialogService _dialogs;
    private IReadOnlyList<TestFlow>? _flows;
    private ImpactReport? _report;

    public ImpactViewModel(ApiRequest request, string? current, string? previous, IReadOnlyList<ApiRequest>? collectionRequests,
        Func<Task<IReadOnlyList<TestFlow>>> loadFlows, IDialogService dialogs)
    {
        _request = request;
        _current = current;
        _collectionRequests = collectionRequests ?? [request];
        _loadFlows = loadFlows;
        _dialogs = dialogs;
        if (previous is not null)
            Baselines.Add(new ImpactBaseline("previous response", previous));
        foreach (var baseline in ImpactBaselines.For(request))
            Baselines.Add(baseline);
        _selectedBaseline = Baselines.FirstOrDefault();
    }

    public string Title => $"Change impact · {_request.Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.ChangeImpact;
    public double Width => 1040;
    public double Height => 720;

    public ObservableCollection<ImpactBaseline> Baselines { get; } = [];
    public ObservableCollection<ShapeChange> Changes { get; } = [];
    public ObservableCollection<ImpactItem> Items { get; } = [];

    [ObservableProperty] private ImpactBaseline? _selectedBaseline;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private int _breakCount;
    [ObservableProperty] private int _possibleCount;
    [ObservableProperty] private int _variableCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyPropertyChangedFor(nameof(NoChanges), nameof(NoImpact))]
    private bool _hasReport;

    public bool NoChanges => HasReport && Changes.Count == 0;
    public bool NoImpact => HasReport && Items.Count == 0;

    public bool CanAnalyze => _current is not null && Baselines.Count > 0;

    partial void OnSelectedBaselineChanged(ImpactBaseline? value) => _ = AnalyzeAsync();

    /// <summary>Runs the analysis for the selected baseline. Called when the window opens and when the baseline changes.</summary>
    public async Task AnalyzeAsync()
    {
        Changes.Clear();
        Items.Clear();
        HasReport = false;
        if (_current is null)
        {
            Status = "Send the request first: the latest response is compared with a baseline.";
            return;
        }
        if (SelectedBaseline is null)
        {
            Status = "Nothing to compare with yet. Send the request again (the previous response becomes the baseline), record a snapshot assertion, or save an example.";
            return;
        }
        try
        {
            _flows ??= await _loadFlows();
        }
        catch (Exception ex)
        {
            _flows = [];
            Status = $"Flows could not be loaded ({ex.Message}); they are left out.";
        }

        _report = ImpactAnalyzer.Analyze(_request, SelectedBaseline.Body, _current, _collectionRequests, _flows, SelectedBaseline.Name);
        foreach (var change in _report.Changes)
            Changes.Add(change);
        foreach (var item in _report.Items.OrderBy(i => i.Severity))
            Items.Add(item);
        Summary = _report.Summary;
        BreakCount = _report.Items.Count(i => i.Severity == ImpactSeverity.Breaks);
        PossibleCount = _report.Items.Count(i => i.Severity == ImpactSeverity.Possible);
        VariableCount = _report.BrokenVariables.Count;
        HasReport = true;
        if (_flows.Count > 0 || Status.Length == 0)
            Status = $"Checked {_collectionRequests.Count} request(s) and {_flows.Count} flow(s) of the collection against the latest response.";
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private async Task ExportAsync(string? format)
    {
        if (_report is null)
            return;
        var (ext, content, filter) = format == "json"
            ? (".json", ImpactReportWriter.Json(_report), new FileFilter("JSON", "*.json"))
            : (".html", ImpactReportWriter.Html(_report), new FileFilter("HTML", "*.html"));
        var path = await _dialogs.SaveFileAsync("Save impact report", DispatchFormat.Slug(_request.Name) + "-impact" + ext, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Report saved to {path}";
        }
    }
}

/// <summary>Impact severity → badge colour.</summary>
public sealed class ImpactSeverityBrush : Avalonia.Data.Converters.IValueConverter
{
    public static readonly ImpactSeverityBrush Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        new Avalonia.Media.SolidColorBrush(value is ImpactSeverity.Breaks ? Avalonia.Media.Color.Parse("#CF222E") : Avalonia.Media.Color.Parse("#9A6700"));

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => null;
}

/// <summary>Shape change kind → colour (additions green, everything else red).</summary>
public sealed class ShapeChangeBrush : Avalonia.Data.Converters.IValueConverter
{
    public static readonly ShapeChangeBrush Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        new Avalonia.Media.SolidColorBrush(value is ShapeChangeKind.Added ? Avalonia.Media.Color.Parse("#22A06B") : Avalonia.Media.Color.Parse("#CF222E"));

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => null;
}
