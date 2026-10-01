using System.Collections.ObjectModel;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.App.ViewModels.Tools;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Interop;
using Dispatch.Application.Load;
using Dispatch.Application.Requests;
using Dispatch.Application.Running;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Interop;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Protocols.Grpc;

namespace Dispatch.App.ViewModels;

/// <summary>Everything the main window needs besides the sidebar view models.</summary>
public sealed record MainServices(
    DatabaseInitializer Database,
    RequestTabServices Tabs,
    ISettingsRepository Settings,
    IEnvironmentRepository Environments,
    CookieJar Cookies,
    Importer Importer,
    CollectionRunner Runner,
    LoadTester LoadTester,
    Dispatch.Application.Security.SecurityScanner Scanner,
    IFlowRepository Flows,
    Dispatch.Application.Flows.FlowRunner FlowRunner,
    GrpcSchemaProvider GrpcSchemas,
    Infrastructure.Auth.SystemBrowserInteraction OAuth);

public sealed partial class MainWindowViewModel : ObservableObject, ITabHost
{
    private readonly MainServices _services;

    public MainWindowViewModel(MainServices services, CollectionsViewModel collections, HistoryViewModel history,
        EnvironmentsViewModel environments)
    {
        _services = services;
        Collections = collections;
        History = history;
        Environments = environments;
        Palette = new CommandPaletteViewModel(PaletteItems);

        Collections.OpenRequested += OpenRequest;
        Collections.RequestDeleted += OnRequestDeleted;
        Collections.ActionRequested += (node, action) => _ = RunCollectionActionAsync(node, action);
        History.OpenRequested += OpenRequest;
        History.CompareRequested += CompareHistory;
        _services.Cookies.Changed += (_, _) => _ = SaveCookiesAsync();
        _services.OAuth.DeviceCodeRequested += (code, uri, _completeUri) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _ = _services.Tabs.Clipboard.SetTextAsync(code);
            ShowInfo($"OAuth device sign-in: enter code {code} at {uri} (copied to the clipboard; the browser has been opened).", 120);
        });
    }

    public CollectionsViewModel Collections { get; }
    public HistoryViewModel History { get; }
    public EnvironmentsViewModel Environments { get; }
    public CommandPaletteViewModel Palette { get; }
    public ObservableCollection<RequestTabViewModel> Tabs { get; } = [];
    public static IReadOnlyList<RequestKind> NewRequestKinds { get; } = Enum.GetValues<RequestKind>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTabs))]
    private RequestTabViewModel? _selectedTab;

    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _infoMessage;

    public bool HasTabs => Tabs.Count > 0;
    private IDialogService Dialogs => _services.Tabs.Dialogs;

    // ---- ITabHost ------------------------------------------------------------------------------

    public ApiEnvironment? ActiveEnvironment => Environments.ActiveModel;
    public ObservableCollection<CollectionNodeViewModel> CollectionNodes => Collections.Items;
    public RequestCollection? FindCollection(Guid? id) => Collections.Find(id);

    public Task<CollectionNodeViewModel> CreateDefaultCollectionAsync() =>
        Collections.CreateCollectionAsync("My Collection");

    public async Task OnRequestSentAsync(ApiResponse response)
    {
        await SafeAsync(History.LoadAsync);
        if (response.EnvironmentUpdates.Count > 0)
        {
            await Environments.ApplyUpdatesAsync(response.EnvironmentUpdates);
            ShowInfo($"Saved {string.Join(", ", response.EnvironmentUpdates.Keys)} to environment \"{Environments.Active.Name}\".");
        }
    }

    public Task OnRequestSavedAsync() => SafeAsync(Collections.LoadAsync);

    public void CloseTab(RequestTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;

        tab.SendCancelCommand.Execute(null);
        Tabs.Remove(tab);
        if (SelectedTab == tab || SelectedTab is null)
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        OnPropertyChanged(nameof(HasTabs));
    }

    public void ReportError(string message) => ErrorMessage = message;

    public void ShowCode(ApiRequest request, ApiRequest resolved) =>
        Dialogs.ShowTool(new CodeSnippetViewModel(request, resolved, _services.Tabs.Clipboard));

    public void ShowDiff(string title, ResponseViewModel left, ResponseViewModel right) =>
        Dialogs.ShowTool(new DiffViewModel(title, "Previous", "Latest", left.PrettyBody, right.PrettyBody, left.StatusText, right.StatusText));

    private void CompareHistory(HistoryEntry first, HistoryEntry second)
    {
        static string Body(HistoryEntry e) => e.Response is null ? ""
            : Application.Formatting.BodyFormatter.Pretty(e.Response.Body, e.Response.ContentType);
        static string Status(HistoryEntry e) => e.Response?.Error ?? $"{e.StatusCode} {e.Response?.ReasonPhrase}".Trim();
        Dialogs.ShowTool(new DiffViewModel($"Compare: {first.Url}", $"{first.Timestamp.ToLocalTime():g}", $"{second.Timestamp.ToLocalTime():g}",
            Body(first), Body(second), Status(first), Status(second)));
    }

    private void ShowInfo(string message, int seconds = 4)
    {
        InfoMessage = message;
        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (InfoMessage == message)
                InfoMessage = null;
        }), TaskScheduler.Default);
    }

    // ---- Lifecycle -----------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        await SafeAsync(async () =>
        {
            await _services.Database.InitializeAsync();
            var theme = await _services.Settings.GetAsync(SettingKeys.Theme);
            IsDarkTheme = theme != "light";
            _services.Cookies.Import(await _services.Settings.GetAsync(SettingKeys.Cookies));

            await Task.WhenAll(Collections.LoadAsync(), History.LoadAsync(), Environments.LoadAsync());
        });

        if (Tabs.Count == 0)
            NewTab();
    }

    private async Task SaveCookiesAsync()
    {
        try
        {
            await _services.Settings.SetAsync(SettingKeys.Cookies, _services.Cookies.Export());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save cookies: {ex.Message}");
        }
    }

    // ---- Commands ------------------------------------------------------------------------------

    [RelayCommand]
    private void NewTab() => NewRequest(RequestKind.Http);

    [RelayCommand]
    private void NewRequest(RequestKind kind) => AddTab(new ApiRequest
    {
        Kind = kind,
        Method = HttpVerb.Get,
        Name = kind == RequestKind.Http ? "New Request" : $"New {Converters.EnumLabelConverter.Label(kind)} request",
        Headers = kind is RequestKind.Http or RequestKind.GraphQl or RequestKind.Soap ? DefaultHeaders.Create() : []
    });

    [RelayCommand]
    private void CloseSelectedTab()
    {
        if (SelectedTab is not null)
            CloseTab(SelectedTab);
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (SelectedTab?.SendCommand.CanExecute(null) == true)
            await SelectedTab.SendCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedTab is not null)
            await SafeAsync(() => SelectedTab.SaveCommand.ExecuteAsync(null));
    }

    [RelayCommand]
    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    [RelayCommand]
    private void DismissInfo() => InfoMessage = null;

    [RelayCommand]
    private void OpenPalette() => Palette.Open();

    [RelayCommand]
    private void OpenImport() => Dialogs.ShowTool(new ImportViewModel(_services.Importer, _services.Tabs.Collections,
        _services.Environments, Dialogs, async () =>
        {
            await Collections.LoadAsync();
            await Environments.LoadAsync();
        }));

    [RelayCommand]
    private void OpenCookies() => Dialogs.ShowTool(new CookiesViewModel(_services.Cookies));

    [RelayCommand]
    private void OpenRunner() => OpenCollectionTool(CollectionAction.Run);

    [RelayCommand]
    private void OpenMockServer() => OpenCollectionTool(CollectionAction.Mock);

    [RelayCommand]
    private void OpenLoadTest() => OpenCollectionTool(CollectionAction.LoadTest);

    /// <summary>Toolbar tools act on the collection of the current tab, else the first collection.</summary>
    private void OpenCollectionTool(CollectionAction action)
    {
        var node = Collections.Items.FirstOrDefault(c => c.Id == SelectedTab?.CollectionId) ?? Collections.Items.FirstOrDefault();
        if (node is null)
        {
            ErrorMessage = "Create or import a collection first.";
            return;
        }
        _ = RunCollectionActionAsync(node, action);
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
        _ = _services.Settings.SetAsync(SettingKeys.Theme, value ? "dark" : "light");
    }

    private async Task RunCollectionActionAsync(CollectionNodeViewModel node, CollectionAction action)
    {
        var collection = node.Model;
        switch (action)
        {
            case CollectionAction.Run:
                Dialogs.ShowTool(new RunnerViewModel(collection, _services.Runner, Dialogs, () => ActiveEnvironment,
                    Environments.ApplyUpdatesAsync));
                break;
            case CollectionAction.LoadTest:
                Dialogs.ShowTool(new LoadTestViewModel(collection, _services.LoadTester, () => ActiveEnvironment));
                break;
            case CollectionAction.Scan:
                Dialogs.ShowTool(new SecurityScanViewModel(collection, _services.Scanner, Dialogs, () => ActiveEnvironment));
                break;
            case CollectionAction.Flows:
                Dialogs.ShowTool(new FlowManagerViewModel(collection, _services.Flows, _services.FlowRunner, Dialogs, () => ActiveEnvironment));
                break;
            case CollectionAction.Mock:
                Dialogs.ShowTool(new MockServerViewModel(collection, _services.GrpcSchemas, _services.Tabs.Clipboard));
                break;
            case CollectionAction.Settings:
                Dialogs.ShowTool(new CollectionSettingsViewModel(collection, _services.Tabs.Collections, Dialogs, Collections.LoadAsync));
                break;
            case CollectionAction.DocsHtml or CollectionAction.DocsMarkdown:
                await SafeAsync(async () =>
                {
                    var html = action == CollectionAction.DocsHtml;
                    var slug = DispatchFormat.Slug(collection.Name);
                    var path = await Dialogs.SaveFileAsync("Save API documentation", slug + (html ? "-docs.html" : "-docs.md"),
                        html ? new FileFilter("HTML page", "*.html") : new FileFilter("Markdown", "*.md"));
                    if (path is null)
                        return;
                    await File.WriteAllTextAsync(path, html ? Application.Docs.DocsGenerator.Html(collection) : Application.Docs.DocsGenerator.MarkdownText(collection));
                    if (html)
                        ShellOpener.Open(path);
                    ShowInfo($"Documentation for {collection.Requests.Count} endpoint(s) saved to {path}");
                });
                break;
            case CollectionAction.ExportFolder:
                await SafeAsync(async () =>
                {
                    if (await Dialogs.OpenFolderAsync("Export to folder (one file per request, git-friendly)") is { } folder)
                    {
                        var target = Path.Combine(folder, DispatchFormat.Slug(collection.Name));
                        DispatchFormat.ExportFolder(collection, target);
                        ShowInfo($"Exported {collection.Requests.Count} request(s) to {target}");
                    }
                });
                break;
            default:
                await SafeAsync(async () =>
                {
                    var slug = DispatchFormat.Slug(collection.Name);
                    var (name, content, filter) = action switch
                    {
                        CollectionAction.ExportPostman => ($"{slug}.postman_collection.json", Postman.Export(collection), new FileFilter("Postman collection", "*.json")),
                        CollectionAction.ExportHttp => ($"{slug}.http", HttpFile.Export(collection), new FileFilter("HTTP file", "*.http")),
                        _ => ($"{slug}.dispatch.json", DispatchFormat.ExportCollection(collection), new FileFilter("Dispatch collection", "*.json"))
                    };
                    if (await Dialogs.SaveFileAsync("Export collection", name, filter) is { } path)
                    {
                        await File.WriteAllTextAsync(path, content);
                        ShowInfo($"Exported to {path}");
                    }
                });
                break;
        }
    }

    // ---- Command palette -----------------------------------------------------------------------

    private IEnumerable<PaletteItem> PaletteItems()
    {
        foreach (var kind in NewRequestKinds)
            yield return new PaletteItem($"New {Converters.EnumLabelConverter.Label(kind)} request", "Create", () => NewRequest(kind));
        yield return new PaletteItem("Import…", "Postman, OpenAPI, HAR, WSDL, .proto, cURL", OpenImport);
        yield return new PaletteItem("Run collection…", "Collection runner", OpenRunner);
        yield return new PaletteItem("Mock server…", "Serve saved examples", OpenMockServer);
        yield return new PaletteItem("Load test…", "Virtual users, latency percentiles", OpenLoadTest);
        yield return new PaletteItem("Test flows…", "Chain requests with conditions, loops and waits", () =>
        {
            var node = Collections.Items.FirstOrDefault(c => c.Id == SelectedTab?.CollectionId) ?? Collections.Items.FirstOrDefault();
            if (node is not null)
                _ = RunCollectionActionAsync(node, CollectionAction.Flows);
        });
        yield return new PaletteItem("Security scan…", "Passive checks and active probes", () =>
        {
            var node = Collections.Items.FirstOrDefault(c => c.Id == SelectedTab?.CollectionId) ?? Collections.Items.FirstOrDefault();
            if (node is not null)
                _ = RunCollectionActionAsync(node, CollectionAction.Scan);
        });
        yield return new PaletteItem("Cookies", "View and delete stored cookies", OpenCookies);
        foreach (var collection in Collections.Items)
            yield return new PaletteItem($"Generate API docs: {collection.Name}", "HTML reference page",
                () => _ = RunCollectionActionAsync(collection, CollectionAction.DocsHtml));
        yield return new PaletteItem("Toggle theme", "Light / dark", ToggleTheme);
        if (SelectedTab is { } tab)
        {
            yield return new PaletteItem("Generate code for this request", "cURL, Python, C#, Go, JS, grpcurl…", () => tab.ShowCodeCommand.Execute(null));
            yield return new PaletteItem("Save request", "Ctrl+S", () => tab.SaveCommand.Execute(null));
        }
        foreach (var env in Environments.Items)
            yield return new PaletteItem($"Use environment: {env.Name}", "Environment", () => Environments.Active = env);
        foreach (var collection in Collections.Items)
        {
            foreach (var request in collection.Model.Requests)
                yield return new PaletteItem(request.Name,
                    $"{collection.Name}{(request.Folder.Length > 0 ? " / " + request.Folder : "")} · {(request.Kind == RequestKind.Http ? request.Method.ToString().ToUpperInvariant() : request.Kind.ToString())}",
                    () => OpenRequest(request.Clone()));
            yield return new PaletteItem($"Run collection: {collection.Name}", "Collection runner",
                () => _ = RunCollectionActionAsync(collection, CollectionAction.Run));
        }
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private void OpenRequest(ApiRequest request)
    {
        // Saved requests are opened once; clicking again focuses the existing tab.
        var existing = request.CollectionId is null ? null : Tabs.FirstOrDefault(t => t.RequestId == request.Id);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }
        AddTab(request);
    }

    private void AddTab(ApiRequest request)
    {
        var tab = new RequestTabViewModel(request, _services.Tabs, this);
        Tabs.Add(tab);
        SelectedTab = tab;
        OnPropertyChanged(nameof(HasTabs));
    }

    private void OnRequestDeleted(Guid requestId)
    {
        // Keep the tab open (the user may still want it) but mark it as unsaved.
        foreach (var tab in Tabs.Where(t => t.RequestId == requestId))
        {
            tab.CollectionId = null;
            tab.IsDirty = true;
        }
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
