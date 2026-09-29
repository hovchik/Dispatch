using System.Collections.ObjectModel;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure.Persistence;

namespace Dispatch.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, ITabHost
{
    private readonly DatabaseInitializer _database;
    private readonly IRequestSender _sender;
    private readonly ICollectionRepository _collectionRepository;
    private readonly ISettingsRepository _settings;
    private readonly IClipboardService _clipboard;

    public MainWindowViewModel(
        DatabaseInitializer database,
        IRequestSender sender,
        ICollectionRepository collectionRepository,
        ISettingsRepository settings,
        IClipboardService clipboard,
        CollectionsViewModel collections,
        HistoryViewModel history,
        EnvironmentsViewModel environments)
    {
        _database = database;
        _sender = sender;
        _collectionRepository = collectionRepository;
        _settings = settings;
        _clipboard = clipboard;
        Collections = collections;
        History = history;
        Environments = environments;

        Collections.OpenRequested += OpenRequest;
        Collections.RequestDeleted += OnRequestDeleted;
        History.OpenRequested += OpenRequest;
    }

    public CollectionsViewModel Collections { get; }
    public HistoryViewModel History { get; }
    public EnvironmentsViewModel Environments { get; }
    public ObservableCollection<RequestTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTabs))]
    private RequestTabViewModel? _selectedTab;

    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private string? _errorMessage;

    public bool HasTabs => Tabs.Count > 0;

    // ---- ITabHost ------------------------------------------------------------------------------

    public ApiEnvironment? ActiveEnvironment => Environments.ActiveModel;
    public ObservableCollection<CollectionNodeViewModel> CollectionNodes => Collections.Items;

    public Task<CollectionNodeViewModel> CreateDefaultCollectionAsync() =>
        Collections.CreateCollectionAsync("My Collection");

    public Task OnRequestSentAsync() => SafeAsync(History.LoadAsync);
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

    // ---- Lifecycle -----------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        await SafeAsync(async () =>
        {
            await _database.InitializeAsync();
            var theme = await _settings.GetAsync(SettingKeys.Theme);
            IsDarkTheme = theme != "light";

            await Task.WhenAll(Collections.LoadAsync(), History.LoadAsync(), Environments.LoadAsync());
        });

        if (Tabs.Count == 0)
            NewTab();
    }

    // ---- Commands ------------------------------------------------------------------------------

    [RelayCommand]
    private void NewTab() => AddTab(new ApiRequest { Method = HttpVerb.Get });

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

    partial void OnIsDarkThemeChanged(bool value)
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
        _ = _settings.SetAsync(SettingKeys.Theme, value ? "dark" : "light");
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
        var tab = new RequestTabViewModel(request, _sender, _collectionRepository, _clipboard, this);
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
