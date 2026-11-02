using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Application.Webhooks;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>
/// The webhook receiver window: start / stop the local endpoint, list received callbacks with headers and body,
/// replay one to your own handler, open it as a request, or save it as an example in the current collection.
/// </summary>
public sealed partial class WebhookViewModel : ObservableObject, ITool
{
    private const int MaxShown = 2000;
    private readonly IWebhookReceiver _receiver;
    private readonly IRequestSender _sender;
    private readonly ICollectionRepository _collections;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly Func<ApiEnvironment?> _environment;
    private readonly Func<RequestCollection?> _currentCollection;
    private readonly Func<Task> _onSaved;
    private readonly Action<ApiRequest> _open;
    private WebhookLog? _log;

    public WebhookViewModel(IWebhookReceiver receiver, IRequestSender sender, ICollectionRepository collections, IDialogService dialogs,
        IClipboardService clipboard, Func<ApiEnvironment?> environment, Func<RequestCollection?> currentCollection, Func<Task> onSaved,
        Action<ApiRequest> open)
    {
        _receiver = receiver;
        _sender = sender;
        _collections = collections;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _environment = environment;
        _currentCollection = currentCollection;
        _onSaved = onSaved;
        _open = open;
        _port = WebhookReceiverOptions.DefaultPort;
        _receiver.StateChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(SyncState);
        Attach(_receiver.Log);
        foreach (var hook in _receiver.Log.Snapshot())
            Add(hook, refresh: false);
        RefreshVisible();
        SyncState();
    }

    public string Title => "Webhook receiver";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.Webhooks;
    public double Width => 1200;
    public double Height => 780;

    /// <summary>Everything received, newest first.</summary>
    public ObservableCollection<ReceivedWebhook> Hooks { get; } = [];

    /// <summary>What the list shows after the filter.</summary>
    public ObservableCollection<ReceivedWebhook> Visible { get; } = [];

    // ---- Settings ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Address), nameof(ExampleUrl), nameof(TunnelCommands))]
    private decimal _port;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Address), nameof(ExampleUrl))]
    private bool _allowRemote;

    [ObservableProperty] private decimal _responseStatus = 200;
    [ObservableProperty] private string _responseBody = """{"received":true}""";
    [ObservableProperty] private decimal _responseDelayMs;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(Address), nameof(ExampleUrl), nameof(TunnelCommands))]
    private bool _isRunning;

    [ObservableProperty] private string _status = "Click Start, then point the sender's callback URL at the address above (any path works). " +
                                                 "Message checks on a request start the receiver by themselves.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedBody), nameof(SelectedHeaders), nameof(SelectedQuery), nameof(HasSelectedQuery))]
    [NotifyCanExecuteChangedFor(nameof(ReplayCommand), nameof(OpenAsRequestCommand), nameof(SaveAsExampleCommand), nameof(CopyBodyCommand),
        nameof(CopyCurlCommand), nameof(RemoveCommand))]
    private ReceivedWebhook? _selected;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _countText = "No callbacks yet";
    [ObservableProperty] private string _replayTarget = "http://localhost:8080";
    [ObservableProperty] private bool _prettyJson = true;

    public bool HasSelection => Selected is not null;
    public bool HasHooks => Hooks.Count > 0;
    public string StateText => IsRunning ? "Listening" : "Stopped";

    /// <summary>The URL senders should use. With remote access on, the machine's LAN address.</summary>
    public string Address =>
        $"http://{(AllowRemote ? LanAddress() : "localhost")}:{(IsRunning && _receiver.Port is { } p ? p : (int)Port)}/";

    public string ExampleUrl => Address + "hooks/orders";

    public string TunnelCommands =>
        $"cloudflared tunnel --url http://localhost:{(IsRunning && _receiver.Port is { } p ? p : (int)Port)}\n" +
        $"ngrok http {(IsRunning && _receiver.Port is { } q ? q : (int)Port)}";

    public string SelectedBody => Selected is null ? "" : PrettyJson && Selected.IsJson ? Application.Webhooks.WebhookReplay.Pretty(Selected.Body) : Selected.Body;
    public IReadOnlyList<KeyValueItem> SelectedHeaders => Selected?.Headers ?? [];
    public string SelectedQuery => Selected?.Query ?? "";
    public bool HasSelectedQuery => SelectedQuery.Length > 0;

    partial void OnSearchChanged(string value) => RefreshVisible();
    partial void OnPrettyJsonChanged(bool value) => OnPropertyChanged(nameof(SelectedBody));

    private void Attach(WebhookLog log)
    {
        if (ReferenceEquals(_log, log))
            return;
        if (_log is not null)
        {
            _log.Added -= OnAdded;
            _log.Cleared -= OnCleared;
        }
        _log = log;
        log.Added += OnAdded;
        log.Cleared += OnCleared;
    }

    private void OnAdded(ReceivedWebhook hook) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Add(hook, refresh: true));

    private void OnCleared() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        Hooks.Clear();
        Visible.Clear();
        Selected = null;
        UpdateCount();
    });

    private void SyncState()
    {
        Attach(_receiver.Log);
        IsRunning = _receiver.IsRunning;
        if (IsRunning && _receiver.Port is { } port && port != (int)Port)
            Port = port;
    }

    private void Add(ReceivedWebhook hook, bool refresh)
    {
        Hooks.Insert(0, hook);
        if (Hooks.Count > MaxShown)
        {
            var dropped = Hooks[^1];
            Hooks.RemoveAt(Hooks.Count - 1);
            Visible.Remove(dropped);
        }
        if (refresh)
        {
            if (Matches(hook))
                Visible.Insert(0, hook);
            UpdateCount();
        }
    }

    private bool Matches(ReceivedWebhook hook)
    {
        var q = Search.Trim();
        return q.Length == 0
               || hook.PathAndQuery.Contains(q, StringComparison.OrdinalIgnoreCase)
               || hook.Method.Equals(q, StringComparison.OrdinalIgnoreCase)
               || hook.Body.Contains(q, StringComparison.OrdinalIgnoreCase)
               || hook.Headers.Any(h => h.Value.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshVisible()
    {
        Visible.Clear();
        foreach (var hook in Hooks.Where(Matches))
            Visible.Add(hook);
        if (Selected is not null && !Visible.Contains(Selected))
            Selected = null;
        UpdateCount();
    }

    private void UpdateCount()
    {
        CountText = Hooks.Count == 0 ? "No callbacks yet"
            : Visible.Count == Hooks.Count ? $"{Hooks.Count} callback(s)" : $"{Visible.Count} of {Hooks.Count} callback(s)";
        OnPropertyChanged(nameof(HasHooks));
        ExportCommand.NotifyCanExecuteChanged();
        OpenReportCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        try
        {
            await _receiver.StartAsync(new WebhookReceiverOptions
            {
                Port = (int)Port,
                Public = AllowRemote,
                ResponseStatus = (int)Math.Clamp(ResponseStatus, 100, 599),
                ResponseBody = ResponseBody,
                ResponseDelayMs = (int)Math.Max(0, ResponseDelayMs)
            });
            SyncState();
            Status = $"Listening on {Address} · every callback is answered with {(int)ResponseStatus}. " +
                     "Use any path, e.g. " + ExampleUrl + ". For callbacks from the internet, expose the port with a tunnel (see the left panel).";
        }
        catch (Exception ex)
        {
            Status = $"Could not start: {ex.Message}";
        }
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        await _receiver.StopAsync();
        SyncState();
        Status = $"Stopped · {Hooks.Count} callback(s) kept in the list.";
    }

    [RelayCommand(CanExecute = nameof(HasHooks))]
    private void Clear() => _receiver.Log.Clear();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (Selected is not { } hook)
            return;
        _receiver.Log.Remove(hook.Id);
        Hooks.Remove(hook);
        Visible.Remove(hook);
        Selected = null;
        UpdateCount();
    }

    [RelayCommand]
    private Task CopyAddressAsync() => CopyAsync(Address, "Receiver address copied.");

    [RelayCommand]
    private Task CopyTunnelAsync() => CopyAsync(TunnelCommands.Split('\n')[0], "Tunnel command copied.");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task CopyBodyAsync() => CopyAsync(SelectedBody, "Body copied.");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task CopyCurlAsync()
    {
        if (Selected is not { } hook)
            return Task.CompletedTask;
        var request = WebhookReplay.ToRequest(hook, ReplayTarget);
        var curl = $"curl -X {request.Method.ToString().ToUpperInvariant()} '{request.Url}'" +
                   string.Concat(request.Headers.Select(h => $" \\\n  -H '{h.Key}: {h.Value.Replace("'", "'\\''")}'")) +
                   (request.Body.Mode == BodyMode.None ? "" : $" \\\n  --data-raw '{request.Body.Content.Replace("'", "'\\''")}'");
        return CopyAsync(curl, "cURL command copied.");
    }

    private async Task CopyAsync(string text, string message)
    {
        await _clipboard.SetTextAsync(text);
        Status = message;
    }

    /// <summary>Re-sends the selected callback to <see cref="ReplayTarget"/>, e.g. your handler under development.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ReplayAsync()
    {
        if (Selected is not { } hook)
            return;
        var request = WebhookReplay.ToRequest(hook, ReplayTarget);
        Status = $"Replaying {hook.Label} to {request.Url}…";
        try
        {
            var response = await _sender.SendAsync(request, new SendOptions
            {
                Environment = _environment(), RecordHistory = false, RunScripts = false, CheckExpectations = false
            }, CancellationToken.None);
            Status = response.HasResponse
                ? $"Replayed {hook.Label} to {request.Url}: {response.StatusCode} {response.ReasonPhrase} in {response.Elapsed.TotalMilliseconds:0} ms."
                : $"Replay of {hook.Label} to {request.Url} failed: {response.Error}";
        }
        catch (Exception ex)
        {
            Status = $"Replay failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenAsRequest()
    {
        if (Selected is not { } hook)
            return;
        _open(WebhookReplay.ToRequest(hook, ReplayTarget));
        Status = $"Opened {hook.Label} as a request. Change the URL to your handler and send it to replay.";
    }

    /// <summary>
    /// Stores the callback's payload as an example on the request that stands for this endpoint in the current
    /// collection (created in a Webhooks folder when missing), so it is documented and the mock server can serve it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SaveAsExampleAsync()
    {
        if (Selected is not { } hook)
            return;
        var collection = _currentCollection();
        if (collection is null)
        {
            Status = "Create or open a collection first; the example is saved to the current collection.";
            return;
        }
        try
        {
            var request = WebhookReplay.FindOrCreateRequest(collection, hook);
            var created = !collection.Requests.Contains(request);
            request.Examples.Add(WebhookReplay.ToExample(hook));
            await _collections.SaveRequestAsync(request);
            await _onSaved();
            Status = created
                ? $"Saved as example on new request \"{request.Name}\" (folder Webhooks) in \"{collection.Name}\"."
                : $"Saved as example #{request.Examples.Count} on \"{request.Name}\" in \"{collection.Name}\".";
        }
        catch (Exception ex)
        {
            Status = $"Could not save the example: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasHooks))]
    private async Task ExportAsync(string? format)
    {
        var hooks = Hooks.Reverse().ToList();
        var (ext, content, filter) = format == "html"
            ? (".html", WebhookReportWriter.Html(hooks, _receiver.BaseUrl), new FileFilter("HTML", "*.html"))
            : (".json", WebhookReportWriter.Json(hooks, _receiver.BaseUrl), new FileFilter("JSON", "*.json"));
        var path = await _dialogs.SaveFileAsync("Save received webhooks", "hooks" + ext, filter);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
            Status = $"Saved {hooks.Count} callback(s) to {path}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasHooks))]
    private void OpenReport()
    {
        try
        {
            ShellOpener.OpenTemp("webhooks", ".html", System.Text.Encoding.UTF8.GetBytes(WebhookReportWriter.Html(Hooks.Reverse().ToList(), _receiver.BaseUrl)));
        }
        catch (Exception ex)
        {
            Status = $"Could not open the report: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = await _dialogs.OpenFileAsync("Open saved webhooks", new FileFilter("JSON", "*.json"));
        if (path is null)
            return;
        try
        {
            var hooks = WebhookReportWriter.FromJson(await File.ReadAllTextAsync(path));
            foreach (var hook in hooks)
                _receiver.Log.Add(hook);
            Status = $"Loaded {hooks.Count} callback(s) from {path}";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidOperationException)
        {
            Status = $"Could not read {path}: {ex.Message}";
        }
    }

    private static string LanAddress()
    {
        try
        {
            var address = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));
            return address?.ToString() ?? "127.0.0.1";
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return "127.0.0.1";
        }
    }

    /// <summary>The receiver keeps running when the window closes, so message checks and tunnels are not interrupted.</summary>
    public void OnClosed()
    {
    }
}
