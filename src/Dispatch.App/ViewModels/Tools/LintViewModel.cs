using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Interop;
using Dispatch.Application.Lint;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>Static checks over a collection with a clickable problem list; rule settings are saved per collection.</summary>
public sealed partial class LintViewModel : ObservableObject, ITool
{
    private readonly RequestCollection _collection;
    private readonly ISettingsRepository _settings;
    private readonly Func<IReadOnlyList<ApiEnvironment>> _environments;
    private readonly Action<ApiRequest> _openRequest;
    private readonly IDialogService _dialogs;
    private readonly LintConfig _config = new();
    private LintReport? _report;
    private bool _loadingConfig;

    public LintViewModel(RequestCollection collection, ISettingsRepository settings, Func<IReadOnlyList<ApiEnvironment>> environments,
        Action<ApiRequest> openRequest, IDialogService dialogs)
    {
        _collection = collection;
        _settings = settings;
        _environments = environments;
        _openRequest = openRequest;
        _dialogs = dialogs;
        foreach (var rule in LintRules.All)
            Rules.Add(new LintRuleToggle(rule, this));
    }

    public string Title => $"Lint · {_collection.Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.CollectionLint;
    public double Width => 1040;
    public double Height => 700;

    /// <summary>Settings key under which this collection's rule configuration is kept.</summary>
    public string SettingKey => $"lint:{_collection.Id}";

    public ObservableCollection<LintRuleToggle> Rules { get; } = [];
    public ObservableCollection<LintProblem> Problems { get; } = [];

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _status = "Checking…";
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _infoCount;
    [ObservableProperty] private bool _showErrors = true;
    [ObservableProperty] private bool _showWarnings = true;
    [ObservableProperty] private bool _showInfo = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyPropertyChangedFor(nameof(IsClean))]
    private bool _hasReport;

    public bool IsClean => HasReport && Problems.Count == 0;

    partial void OnShowErrorsChanged(bool value) => Refresh();
    partial void OnShowWarningsChanged(bool value) => Refresh();
    partial void OnShowInfoChanged(bool value) => Refresh();

    /// <summary>Loads the saved rule configuration and runs the checks. Called when the window opens.</summary>
    public async Task LoadAsync()
    {
        _loadingConfig = true;
        try
        {
            var saved = await _settings.GetAsync(SettingKey);
            if (saved is not null)
            {
                var config = LintConfig.Parse(saved);
                foreach (var (id, severity) in config.Rules)
                    _config.Rules[id] = severity;
            }
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            Status = $"Saved rule settings could not be read ({ex.Message}); defaults are used.";
        }
        finally
        {
            foreach (var toggle in Rules)
                toggle.Load(_config);
            _loadingConfig = false;
        }
        Lint();
    }

    [RelayCommand]
    private void Lint()
    {
        _report = CollectionLinter.Lint(_collection, new LintOptions { Config = _config, Environments = _environments() });
        ErrorCount = _report.Errors;
        WarningCount = _report.Warnings;
        InfoCount = _report.Infos;
        Summary = _report.Summary;
        foreach (var toggle in Rules)
            toggle.Count = _report.Problems.Count(p => p.RuleId == toggle.Id);
        Refresh();
        HasReport = true;
        var envs = _environments();
        Status = $"Checked {_collection.Requests.Count} request(s) with {_report.RulesChecked.Count} rule(s)" +
                 (envs.Count > 0 ? $"; variables from {string.Join(", ", envs.Select(e => e.Name))} count as defined." : "; no environment is active, so only collection variables and extractions count as defined.");
    }

    private void Refresh()
    {
        Problems.Clear();
        if (_report is null)
            return;
        foreach (var problem in _report.Problems)
        {
            var visible = problem.Severity switch
            {
                LintSeverity.Error => ShowErrors,
                LintSeverity.Warning => ShowWarnings,
                _ => ShowInfo
            };
            if (visible)
                Problems.Add(problem);
        }
        OnPropertyChanged(nameof(IsClean));
    }

    /// <summary>Opens the request a problem points at (nothing for collection-level problems).</summary>
    [RelayCommand]
    private void Open(LintProblem? problem)
    {
        if (problem?.RequestId is { } id && _collection.Requests.FirstOrDefault(r => r.Id == id) is { } request)
            _openRequest(request.Clone());
    }

    /// <summary>A rule toggle changed: persist the configuration and lint again.</summary>
    internal void RuleChanged(LintRuleToggle toggle)
    {
        if (_loadingConfig)
            return;
        if (!toggle.IsEnabled)
            _config.Disable(toggle.Id);
        else
            _config.Set(toggle.Id, toggle.Severity);
        _ = SaveConfigAsync();
        Lint();
    }

    private async Task SaveConfigAsync()
    {
        try
        {
            await _settings.SetAsync(SettingKey, _config.Rules.Count == 0 ? null : _config.ToJson());
        }
        catch (Exception ex)
        {
            Status = $"Rule settings could not be saved: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        var path = await _dialogs.SaveFileAsync("Save lint configuration (for dispatch lint in CI)", LintConfig.FileName, new FileFilter("JSON", "*.json"));
        if (path is null)
            return;
        await File.WriteAllTextAsync(path, _config.ToJson());
        Status = $"Configuration saved to {path}. Put it next to the collection file or pass --config to dispatch lint.";
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private async Task ExportAsync(string? format)
    {
        if (_report is null)
            return;
        var (ext, content, filter) = format == "json"
            ? (".json", LintReportWriter.Json(_report), new FileFilter("JSON", "*.json"))
            : (".html", LintReportWriter.Html(_report), new FileFilter("HTML", "*.html"));
        var path = await _dialogs.SaveFileAsync("Save lint report", DispatchFormat.Slug(_collection.Name) + "-lint" + ext, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Report saved to {path}";
        }
    }
}

/// <summary>One rule in the settings list: on/off and its severity.</summary>
public sealed partial class LintRuleToggle : ObservableObject
{
    private readonly LintRule _rule;
    private readonly LintViewModel _owner;
    private bool _loading;

    public LintRuleToggle(LintRule rule, LintViewModel owner)
    {
        _rule = rule;
        _owner = owner;
        _isEnabled = rule.EnabledByDefault;
        _severity = rule.DefaultSeverity;
    }

    public string Id => _rule.Id;
    public string Title => _rule.Title;
    public string Description => _rule.Description;
    public static IReadOnlyList<LintSeverity> Severities { get; } = [LintSeverity.Error, LintSeverity.Warning, LintSeverity.Info];

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private LintSeverity _severity;
    [ObservableProperty] private int _count;

    internal void Load(LintConfig config)
    {
        _loading = true;
        IsEnabled = config.IsEnabled(_rule);
        Severity = config.SeverityOf(_rule);
        _loading = false;
    }

    partial void OnIsEnabledChanged(bool value) => Notify();
    partial void OnSeverityChanged(LintSeverity value) => Notify();

    private void Notify()
    {
        if (!_loading)
            _owner.RuleChanged(this);
    }
}

/// <summary>Lint severity → badge colour.</summary>
public sealed class LintSeverityBrush : Avalonia.Data.Converters.IValueConverter
{
    public static readonly LintSeverityBrush Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        new Avalonia.Media.SolidColorBrush(value switch
        {
            LintSeverity.Error => Avalonia.Media.Color.Parse("#CF222E"),
            LintSeverity.Warning => Avalonia.Media.Color.Parse("#9A6700"),
            _ => Avalonia.Media.Color.Parse("#3B82F6")
        });

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => null;
}
