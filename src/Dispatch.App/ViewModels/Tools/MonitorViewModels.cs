using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Monitoring;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Tools;

public sealed partial class AlertTargetViewModel(AlertTarget model) : ObservableObject
{
    public AlertTarget Model { get; } = model;
    public static IReadOnlyList<AlertChannel> Channels { get; } = Enum.GetValues<AlertChannel>();
    public static IReadOnlyList<AlertTrigger> Triggers { get; } = Enum.GetValues<AlertTrigger>();

    public bool Enabled { get => Model.Enabled; set { Model.Enabled = value; OnPropertyChanged(); } }
    public AlertChannel Channel { get => Model.Channel; set { Model.Channel = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsEmail)); OnPropertyChanged(nameof(TargetWatermark)); } }
    public AlertTrigger Trigger { get => Model.Trigger; set { Model.Trigger = value; OnPropertyChanged(); } }
    public string Target { get => Model.Target; set { Model.Target = value; OnPropertyChanged(); } }
    public string SmtpHost { get => Model.SmtpHost; set { Model.SmtpHost = value; OnPropertyChanged(); } }
    public decimal SmtpPort { get => Model.SmtpPort; set { Model.SmtpPort = (int)value; OnPropertyChanged(); } }
    public string SmtpUser { get => Model.SmtpUser; set { Model.SmtpUser = value; OnPropertyChanged(); } }
    public string SmtpPassword { get => Model.SmtpPassword; set { Model.SmtpPassword = value; OnPropertyChanged(); } }
    public string FromAddress { get => Model.FromAddress; set { Model.FromAddress = value; OnPropertyChanged(); } }

    public bool IsEmail => Channel == AlertChannel.Email;
    public string TargetWatermark => Channel switch
    {
        AlertChannel.Slack => "Slack incoming webhook URL",
        AlertChannel.Email => "recipient@example.com, other@example.com",
        _ => "https://your-service/webhook"
    };
}

/// <summary>Create/edit one monitor: schedule, request subset and alert targets, with a run-now button and history.</summary>
public sealed partial class MonitorEditorViewModel : ObservableObject, ITool
{
    private readonly RequestCollection _collection;
    private readonly MonitorDefinition _monitor;
    private readonly IMonitorRepository _repository;
    private readonly MonitorService _service;
    private readonly Func<ApiEnvironment?> _environment;

    public MonitorEditorViewModel(RequestCollection collection, MonitorDefinition monitor, IMonitorRepository repository,
        MonitorService service, Func<ApiEnvironment?> environment)
    {
        _collection = collection;
        _monitor = monitor;
        _repository = repository;
        _service = service;
        _environment = environment;
        Name = monitor.Name;
        foreach (var request in collection.Requests.OrderBy(r => r.SortOrder))
            Requests.Add(new SelectableRequest(request) { IsSelected = monitor.RequestNames.Count == 0 || monitor.RequestNames.Contains(request.Name) });
        foreach (var alert in monitor.Alerts)
            Alerts.Add(new AlertTargetViewModel(alert));
        _ = LoadHistoryAsync();
    }

    public string Title => $"Monitor · {Name}";
    public double Width => 820;
    public double Height => 720;

    public ObservableCollection<SelectableRequest> Requests { get; } = [];
    public ObservableCollection<AlertTargetViewModel> Alerts { get; } = [];
    public ObservableCollection<MonitorRun> History { get; } = [];
    public static IReadOnlyList<ScheduleKind> ScheduleKinds { get; } = Enum.GetValues<ScheduleKind>();

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private ScheduleKind _scheduleKind;
    [ObservableProperty] private decimal _intervalMinutes = 5;
    [ObservableProperty] private string _cron = "*/5 * * * *";
    [ObservableProperty] private decimal _maxAverageMs;
    [ObservableProperty] private string _status = "Configure the schedule and alerts, then Save. Run now to test it.";
    [ObservableProperty] private bool _isRunning;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Title));

    public bool IsInterval => ScheduleKind == ScheduleKind.Interval;
    public bool IsCron => ScheduleKind == ScheduleKind.Cron;
    partial void OnScheduleKindChanged(ScheduleKind value)
    {
        OnPropertyChanged(nameof(IsInterval));
        OnPropertyChanged(nameof(IsCron));
    }

    public MonitorEditorViewModel Initialize()
    {
        Enabled = _monitor.Enabled;
        ScheduleKind = _monitor.ScheduleKind;
        IntervalMinutes = _monitor.IntervalMinutes;
        Cron = _monitor.Cron;
        MaxAverageMs = _monitor.MaxAverageMs;
        return this;
    }

    [RelayCommand]
    private void AddAlert() => Alerts.Add(new AlertTargetViewModel(new AlertTarget()));

    [RelayCommand]
    private void RemoveAlert(AlertTargetViewModel? alert)
    {
        if (alert is not null)
            Alerts.Remove(alert);
    }

    private void Apply()
    {
        _monitor.Name = Name;
        _monitor.Enabled = Enabled;
        _monitor.CollectionId = _collection.Id;
        _monitor.ScheduleKind = ScheduleKind;
        _monitor.IntervalMinutes = (int)IntervalMinutes;
        _monitor.Cron = Cron;
        _monitor.MaxAverageMs = (int)MaxAverageMs;
        _monitor.RequestNames = Requests.All(r => r.IsSelected) ? [] : Requests.Where(r => r.IsSelected).Select(r => r.Request.Name).ToList();
        _monitor.Alerts = Alerts.Select(a => a.Model).ToList();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (ScheduleKind == ScheduleKind.Cron && !CronSchedule.TryParse(Cron, out _))
        {
            Status = "That cron expression isn't valid (need: minute hour day month day-of-week).";
            return;
        }
        Apply();
        await _repository.SaveAsync(_monitor);
        Status = $"Saved \"{_monitor.Name}\".";
    }

    [RelayCommand]
    private async Task RunNowAsync()
    {
        Apply();
        IsRunning = true;
        Status = "Running…";
        try
        {
            var previous = (await _repository.GetRunsAsync(_monitor.Id, 1)).FirstOrDefault()?.Passed;
            var run = await _service.RunOnceAsync(_monitor, new MonitorContext(_collection, _environment()), previous);
            await _repository.SaveAsync(_monitor);
            await _repository.AddRunAsync(run);
            await LoadHistoryAsync();
            Status = (run.Passed ? "✓ Passed" : "✗ Failed") + $" · {run.Summary} · {run.AverageMs:0} ms avg" +
                     (Alerts.Count > 0 ? " · alerts sent per their triggers" : "");
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task LoadHistoryAsync()
    {
        History.Clear();
        foreach (var run in await _repository.GetRunsAsync(_monitor.Id, 30))
            History.Add(run);
    }
}

/// <summary>Lists the monitors of a collection; opens, creates and deletes them.</summary>
public sealed partial class MonitorManagerViewModel : ObservableObject, ITool
{
    private readonly RequestCollection _collection;
    private readonly IMonitorRepository _repository;
    private readonly MonitorService _service;
    private readonly IDialogService _dialogs;
    private readonly Func<ApiEnvironment?> _environment;

    public MonitorManagerViewModel(RequestCollection collection, IMonitorRepository repository, MonitorService service,
        IDialogService dialogs, Func<ApiEnvironment?> environment)
    {
        _collection = collection;
        _repository = repository;
        _service = service;
        _dialogs = dialogs;
        _environment = environment;
        _ = LoadAsync();
    }

    public string Title => $"Monitors · {_collection.Name}";
    public double Width => 560;
    public double Height => 520;

    public ObservableCollection<MonitorDefinition> Monitors { get; } = [];
    [ObservableProperty] private string _hint = "Loading…";

    private async Task LoadAsync()
    {
        Monitors.Clear();
        foreach (var m in (await _repository.GetAllAsync()).Where(m => m.CollectionId == _collection.Id))
            Monitors.Add(m);
        Hint = Monitors.Count == 0
            ? "No monitors yet. Create one to run this collection on a schedule and alert on failures (run 'dispatch monitor --watch' to execute them)."
            : $"{Monitors.Count} monitor(s). Run them with 'dispatch monitor --watch'.";
    }

    [RelayCommand]
    private void New()
    {
        Open(new MonitorDefinition { Name = "New Monitor", CollectionId = _collection.Id });
        _ = LoadAsync();
    }

    [RelayCommand]
    private void Open(MonitorDefinition? monitor)
    {
        if (monitor is null)
            return;
        _dialogs.ShowTool(new MonitorEditorViewModel(_collection, monitor.Clone(), _repository, _service, _environment).Initialize());
    }

    [RelayCommand]
    private async Task DeleteAsync(MonitorDefinition? monitor)
    {
        if (monitor is null)
            return;
        await _repository.DeleteAsync(monitor.Id);
        await LoadAsync();
    }
}
