using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.ClientFuzz;
using Dispatch.Infrastructure.Capture;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>A mutation kind with an on/off switch.</summary>
public sealed partial class MutationKindOption(MutationKind kind, string label, string group) : ObservableObject
{
    public MutationKind Kind { get; } = kind;
    public string Label { get; } = label;
    public string Group { get; } = group;
    [ObservableProperty] private bool _isEnabled = kind != MutationKind.HugeString;
}

/// <summary>One experiment in the live list.</summary>
public sealed partial class FuzzExperimentItem(FuzzExperiment experiment) : ObservableObject
{
    public FuzzExperiment Model { get; } = experiment;
    public string Endpoint => Model.Endpoint;
    public string Variation => Model.Mutation.Description;
    public string Time => Model.StartedAt.ToLocalTime().ToString("HH:mm:ss");
    public bool IsRunning => Model.Verdict == FuzzVerdict.Running;
    public bool Breaks => Model.Verdict == FuzzVerdict.Breaks;
    public string Badge => Model.Verdict switch { FuzzVerdict.Breaks => "BREAKS", FuzzVerdict.Copes => "OK", _ => "…" };
    private static readonly Avalonia.Media.IBrush BreaksBrush = Avalonia.Media.SolidColorBrush.Parse("#CF222E");
    private static readonly Avalonia.Media.IBrush CopesBrush = Avalonia.Media.SolidColorBrush.Parse("#22A06B");
    private static readonly Avalonia.Media.IBrush PendingBrush = Avalonia.Media.SolidColorBrush.Parse("#6E7781");
    public Avalonia.Media.IBrush BadgeBrush => Model.Verdict switch
    {
        FuzzVerdict.Breaks => BreaksBrush,
        FuzzVerdict.Copes => CopesBrush,
        _ => PendingBrush
    };
    public string Result => Model.Verdict switch
    {
        FuzzVerdict.Breaks => string.Join("\n", Model.Signals),
        FuzzVerdict.Copes => $"copes · {Model.RequestsAfter} request(s) afterwards",
        _ => "watching how the app reacts…"
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Breaks));
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(BadgeBrush));
    }
}

/// <summary>A titled group of mutation kinds.</summary>
public sealed class MutationKindGroup(string name, IReadOnlyList<MutationKindOption> kinds)
{
    public string Name { get; } = name;
    public IReadOnlyList<MutationKindOption> Kinds { get; } = kinds;
}

/// <summary>Client fuzzing: a proxy that varies responses to the app under test and watches how the app reacts.</summary>
public sealed partial class ClientFuzzViewModel : ObservableObject, ITool
{
    private readonly Func<CaptureProxy> _proxyFactory;
    private readonly CertificateAuthority _authority;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private CaptureProxy? _proxy;
    private ClientFuzzer? _fuzzer;
    private DispatcherTimer? _timer;

    public ClientFuzzViewModel(Func<CaptureProxy> proxyFactory, CertificateAuthority authority, IDialogService dialogs, IClipboardService clipboard)
    {
        _proxyFactory = proxyFactory;
        _authority = authority;
        _dialogs = dialogs;
        _clipboard = clipboard;
        KindGroups = Kinds.GroupBy(k => k.Group).Select(g => new MutationKindGroup(g.Key, g.ToList())).ToList();
        Experiments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasExperiments));
    }

    public IReadOnlyList<MutationKindGroup> KindGroups { get; }

    public string Title => "Client fuzzing";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.ClientFuzz;
    public double Width => 1100;
    public double Height => 760;

    public IReadOnlyList<MutationKindOption> Kinds { get; } =
    [
        new(MutationKind.NullField, "null values", "Data"),
        new(MutationKind.DropField, "missing fields", "Data"),
        new(MutationKind.EmptyString, "empty strings", "Data"),
        new(MutationKind.WrongType, "wrong types", "Data"),
        new(MutationKind.UnexpectedEnum, "unexpected enum values", "Data"),
        new(MutationKind.ExtraField, "unknown extra fields", "Data"),
        new(MutationKind.HugeString, "very long text", "Data"),
        new(MutationKind.EmptyArray, "empty lists", "Lists"),
        new(MutationKind.SingleItem, "single-item lists", "Lists"),
        new(MutationKind.ServerError, "500 errors", "Failures"),
        new(MutationKind.Unavailable, "503 unavailable", "Failures"),
        new(MutationKind.RateLimited, "429 rate limited", "Failures"),
        new(MutationKind.Unauthorized, "401 expired session", "Failures"),
        new(MutationKind.MalformedJson, "malformed JSON", "Failures"),
        new(MutationKind.EmptyBody, "empty bodies", "Failures"),
        new(MutationKind.Slow, "slow responses", "Failures")
    ];

    public ObservableCollection<FuzzExperimentItem> Experiments { get; } = [];
    public ObservableCollection<FuzzExperimentItem> Visible { get; } = [];
    public bool HasExperiments => Experiments.Count > 0;
    public bool HasVisible => Visible.Count > 0;
    public string StateText => IsRunning ? "Running" : "Stopped";
    public string ProxyAddress => IsRunning ? Address : $"127.0.0.1:{(int)Port}";
    public string CountText => BreaksOnly ? $"{Visible.Count} of {Experiments.Count}" : $"{Experiments.Count} variation(s)";

    [ObservableProperty] private bool _breaksOnly;
    partial void OnBreaksOnlyChanged(bool value) => RebuildVisible();
    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ProxyAddress));
    }
    partial void OnPortChanged(decimal value) => OnPropertyChanged(nameof(ProxyAddress));
    partial void OnAddressChanged(string value) => OnPropertyChanged(nameof(ProxyAddress));

    private void RebuildVisible()
    {
        Visible.Clear();
        foreach (var e in Experiments.Where(Shows))
            Visible.Add(e);
        OnVisibleChanged();
    }

    private bool Shows(FuzzExperimentItem e) => !BreaksOnly || e.Breaks;

    private void OnVisibleChanged()
    {
        OnPropertyChanged(nameof(HasVisible));
        OnPropertyChanged(nameof(CountText));
    }

    private void Added(FuzzExperimentItem item)
    {
        Experiments.Insert(0, item);
        if (Shows(item))
            Visible.Insert(0, item);
        OnVisibleChanged();
    }

    [RelayCommand]
    private void SelectAllKinds(string? group)
    {
        foreach (var k in Kinds.Where(k => group is null || k.Group == group))
            k.IsEnabled = true;
    }

    [RelayCommand]
    private void SelectNoKinds(string? group)
    {
        foreach (var k in Kinds.Where(k => group is null || k.Group == group))
            k.IsEnabled = false;
    }

    [ObservableProperty] private decimal _port = 8899;
    [ObservableProperty] private string _hostFilter = string.Empty;
    [ObservableProperty] private decimal _windowSeconds = 5;
    [ObservableProperty] private decimal _baseline = 2;
    [ObservableProperty] private decimal _maxPerEndpoint = 25;
    [ObservableProperty] private bool _decryptHttps = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ExportCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _status = "Point the app you are testing at this proxy (like the capture proxy), start, and use the app as usual.";
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private int _tried;
    [ObservableProperty] private int _breaks;
    [ObservableProperty] private int _copes;
    [ObservableProperty] private string _summary = string.Empty;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var kinds = Kinds.Where(k => k.IsEnabled).Select(k => k.Kind).ToHashSet();
        if (kinds.Count == 0)
        {
            Status = "Pick at least one kind of variation.";
            return;
        }
        Experiments.Clear();
        Visible.Clear();
        OnVisibleChanged();
        Tried = Breaks = Copes = 0;
        Summary = string.Empty;
        _fuzzer = new ClientFuzzer(new ClientFuzzOptions
        {
            HostFilter = HostFilter.Trim(),
            Observation = TimeSpan.FromSeconds((double)Math.Max(1, WindowSeconds)),
            BaselineResponses = (int)Math.Max(1, Baseline),
            MaxMutationsPerEndpoint = (int)Math.Max(1, MaxPerEndpoint),
            Kinds = kinds
        });
        _fuzzer.ExperimentStarted += e => Dispatcher.UIThread.Post(() => Added(new FuzzExperimentItem(e)));
        _fuzzer.ExperimentFinished += e => Dispatcher.UIThread.Post(() => OnFinished(e));
        try
        {
            _proxy = _proxyFactory();
            await _proxy.StartAsync(new CaptureProxyOptions
            {
                Port = (int)Port, HostFilter = HostFilter.Trim(), DecryptHttps = DecryptHttps, Interceptor = _fuzzer
            });
        }
        catch (Exception ex)
        {
            Status = $"Could not start: {ex.Message}";
            _proxy = null;
            return;
        }
        Address = $"127.0.0.1:{_proxy.Port}";
        IsRunning = true;
        Status = $"Fuzzing through {Address}. Use the app: after {Baseline} normal response(s) per endpoint, one response at a time is varied.";
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => _fuzzer?.Tick(DateTimeOffset.Now));
        _timer.Start();
    }

    private void OnFinished(FuzzExperiment experiment)
    {
        Experiments.FirstOrDefault(e => e.Model == experiment)?.Refresh();
        if (BreaksOnly)
            RebuildVisible();
        Tried++;
        if (experiment.Verdict == FuzzVerdict.Breaks)
            Breaks++;
        else if (experiment.Verdict == FuzzVerdict.Copes)
            Copes++;
        Summary = ClientFuzzReport.Summary(_fuzzer?.Experiments ?? []);
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        _timer?.Stop();
        if (_proxy is not null)
            await _proxy.StopAsync();
        _proxy = null;
        _fuzzer?.Tick(DateTimeOffset.MaxValue);
        IsRunning = false;
        Status = "Stopped. " + ClientFuzzReport.Summary(_fuzzer?.Experiments ?? []);
    }

    [RelayCommand]
    private async Task CopyAddressAsync()
    {
        if (Address.Length > 0)
            await _clipboard.SetTextAsync($"http://{Address}");
    }

    [RelayCommand]
    private async Task ExportCaAsync()
    {
        var path = await _dialogs.SaveFileAsync("Save CA certificate", "dispatch-ca.crt", new FileFilter("Certificate", "*.crt", "*.pem"));
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, _authority.CaCertificatePem);
            Status = $"CA certificate saved to {path}. Trust it on the device to fuzz HTTPS traffic.";
        }
    }

    private bool CanExport() => !IsRunning && _fuzzer is not null;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        if (_fuzzer is null)
            return;
        var (ext, content, filter) = format == "json"
            ? (".json", ClientFuzzReport.Json(_fuzzer.Experiments), new FileFilter("JSON", "*.json"))
            : (".html", ClientFuzzReport.Html(_fuzzer.Experiments), new FileFilter("HTML", "*.html"));
        var path = await _dialogs.SaveFileAsync("Save fuzzing report", "client-fuzz" + ext, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Report saved to {path}";
        }
    }

    public void OnClosed()
    {
        _timer?.Stop();
        _ = _proxy?.StopAsync();
    }
}
