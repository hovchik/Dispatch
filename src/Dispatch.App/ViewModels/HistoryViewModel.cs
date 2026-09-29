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

    public event Action<ApiRequest>? OpenRequested;

    public async Task LoadAsync()
    {
        var entries = await repository.GetRecentAsync(PageSize);
        Items.Clear();
        foreach (var e in entries)
            Items.Add(new HistoryItemViewModel(this, e));
        IsEmpty = Items.Count == 0;
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
    public HistoryEntry Model { get; } = model;
    public HttpVerb Method => Model.Method;
    public string Url => Model.Url;
    public int StatusCode => Model.StatusCode ?? 0;
    public string StatusText => Model.StatusCode?.ToString() ?? "ERR";
    public string WhenText => Format.Ago(Model.Timestamp);
    public string ElapsedText => Format.Duration(TimeSpan.FromMilliseconds(Model.ElapsedMs));

    [RelayCommand]
    private void Open() => owner.Open(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteAsync(this);
}
