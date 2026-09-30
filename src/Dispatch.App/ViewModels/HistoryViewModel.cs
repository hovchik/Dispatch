using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.Application.Abstractions;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels;

public sealed partial class HistoryViewModel(IHistoryRepository repository) : ObservableObject
{
    private const int PageSize = 200;

    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _filter = string.Empty;

    /// <summary>The first entry picked for "Compare with…".</summary>
    [ObservableProperty] private HistoryItemViewModel? _compareBase;

    public event Action<ApiRequest>? OpenRequested;
    public event Action<HistoryEntry, HistoryEntry>? CompareRequested;

    public async Task LoadAsync()
    {
        var entries = await repository.GetRecentAsync(PageSize);
        Items.Clear();
        foreach (var e in entries)
            Items.Add(new HistoryItemViewModel(this, e));
        ApplyFilter();
        IsEmpty = Items.Count == 0;
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnCompareBaseChanged(HistoryItemViewModel? oldValue, HistoryItemViewModel? newValue)
    {
        foreach (var item in Items)
            item.RefreshCompareState();
    }

    [RelayCommand]
    private void CancelCompare() => CompareBase = null;

    private void ApplyFilter()
    {
        var term = Filter.Trim();
        foreach (var item in Items)
            item.IsVisible = term.Length == 0 || item.Url.Contains(term, StringComparison.OrdinalIgnoreCase)
                                              || item.StatusText.Contains(term, StringComparison.OrdinalIgnoreCase)
                                              || item.Model.Request.Name.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty] private string? _error;

    [RelayCommand]
    private Task ClearAsync() => Guard(async () =>
    {
        await repository.ClearAsync();
        Items.Clear();
        IsEmpty = true;
    });

    internal Task DeleteAsync(HistoryItemViewModel item) => Guard(async () =>
    {
        await repository.DeleteAsync(item.Model.Id);
        Items.Remove(item);
        IsEmpty = Items.Count == 0;
    });

    internal void Compare(HistoryItemViewModel item)
    {
        if (CompareBase is null || CompareBase == item)
        {
            CompareBase = item;
            return;
        }
        var first = CompareBase;
        CompareBase = null;
        CompareRequested?.Invoke(first.Model, item.Model);
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            Error = null;
            await action();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    // History items open as new, unsaved requests so re-running them never overwrites a saved one.
    internal void Open(HistoryItemViewModel item) => OpenRequested?.Invoke(item.Model.Request.Clone(newIdentity: true));
}

public sealed partial class HistoryItemViewModel(HistoryViewModel owner, HistoryEntry model) : ObservableObject
{
    [ObservableProperty] private bool _isVisible = true;

    public HistoryEntry Model { get; } = model;
    public HttpVerb Method => Model.Method;
    public object Badge => Model.Kind == RequestKind.Http ? Model.Method : Model.Kind;
    public string Url => Model.Url;
    public int StatusCode => Model.Kind is RequestKind.Http or RequestKind.Soap or RequestKind.GraphQl or RequestKind.Sse
        ? Model.StatusCode ?? 0
        : Model.StatusCode is 0 or 101 ? 200 : 500;
    public string StatusText => Model.StatusCode?.ToString() ?? "ERR";
    public string WhenText => Format.Ago(Model.Timestamp);
    public string ElapsedText => Format.Duration(TimeSpan.FromMilliseconds(Model.ElapsedMs));
    public bool CanCompare => Model.Response is not null;
    public bool IsCompareBase => owner.CompareBase == this;
    public string CompareHeader => owner.CompareBase is null || IsCompareBase ? "Compare with…" : "Compare with selected";

    internal void RefreshCompareState()
    {
        OnPropertyChanged(nameof(IsCompareBase));
        OnPropertyChanged(nameof(CompareHeader));
    }

    [RelayCommand]
    private void Open() => owner.Open(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteAsync(this);

    [RelayCommand]
    private void Compare() => owner.Compare(this);
}
