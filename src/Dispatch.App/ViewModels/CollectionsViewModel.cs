using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.Application.Abstractions;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels;

public abstract partial class TreeNodeViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isVisible = true;
}

public sealed partial class CollectionsViewModel(ICollectionRepository repository) : ObservableObject
{
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private string? _error;

    public ObservableCollection<CollectionNodeViewModel> Items { get; } = [];

    /// <summary>Raised when the user asks to open a request in a tab.</summary>
    public event Action<ApiRequest>? OpenRequested;

    /// <summary>Raised after a request was deleted so its open tab can be detached.</summary>
    public event Action<Guid>? RequestDeleted;

    public async Task LoadAsync()
    {
        var expanded = Items.Where(i => i.IsExpanded).Select(i => i.Id).ToHashSet();
        var firstLoad = Items.Count == 0;
        var collections = await repository.GetAllAsync();

        Items.Clear();
        foreach (var c in collections)
        {
            var node = new CollectionNodeViewModel(this, c)
            {
                IsExpanded = firstLoad || expanded.Contains(c.Id)
            };
            Items.Add(node);
        }
        ApplyFilter();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var term = Filter.Trim();
        foreach (var collection in Items)
        {
            var collectionMatches = term.Length == 0 || collection.Name.Contains(term, StringComparison.OrdinalIgnoreCase);
            var anyChild = false;
            foreach (var request in collection.Requests)
            {
                request.IsVisible = collectionMatches
                                    || request.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                                    || request.Model.Url.Contains(term, StringComparison.OrdinalIgnoreCase);
                anyChild |= request.IsVisible;
            }
            collection.IsVisible = collectionMatches || anyChild;
            if (term.Length > 0 && anyChild)
                collection.IsExpanded = true;
        }
    }

    [RelayCommand]
    private async Task NewCollectionAsync()
    {
        var node = await CreateCollectionAsync("New Collection");
        node.StartRenameCommand.Execute(null);
    }

    public async Task<CollectionNodeViewModel> CreateCollectionAsync(string name)
    {
        var model = new RequestCollection { Name = name };
        await repository.AddAsync(model);
        var node = new CollectionNodeViewModel(this, model) { IsExpanded = true };
        Items.Add(node);
        return node;
    }

    internal Task RunAsync(Func<Task> action) => Guard(action);

    internal async Task RenameAsync(CollectionNodeViewModel node, string name) =>
        await Guard(() => repository.RenameAsync(node.Id, name));

    internal async Task DeleteAsync(CollectionNodeViewModel node) => await Guard(async () =>
    {
        await repository.DeleteAsync(node.Id);
        Items.Remove(node);
        foreach (var r in node.Requests)
            RequestDeleted?.Invoke(r.Model.Id);
    });

    internal async Task DeleteRequestAsync(RequestNodeViewModel node) => await Guard(async () =>
    {
        await repository.DeleteRequestAsync(node.Model.Id);
        node.Parent.Requests.Remove(node);
        RequestDeleted?.Invoke(node.Model.Id);
    });

    internal async Task DuplicateRequestAsync(RequestNodeViewModel node) => await Guard(async () =>
    {
        var copy = node.Model.Clone(newIdentity: true);
        copy.CollectionId = node.Parent.Id;
        copy.Name = $"{node.Model.Name} (copy)";
        await repository.SaveRequestAsync(copy);
        await LoadAsync();
    });

    internal void Open(ApiRequest request) => OpenRequested?.Invoke(request.Clone());

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
}

public sealed partial class CollectionNodeViewModel : TreeNodeViewModel
{
    private readonly CollectionsViewModel _owner;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _editName = string.Empty;

    public CollectionNodeViewModel(CollectionsViewModel owner, RequestCollection model)
    {
        _owner = owner;
        Id = model.Id;
        _name = model.Name;
        Requests = new ObservableCollection<RequestNodeViewModel>(model.Requests.Select(r => new RequestNodeViewModel(this, r)));
    }

    public Guid Id { get; }
    public ObservableCollection<RequestNodeViewModel> Requests { get; }
    internal CollectionsViewModel Owner => _owner;

    [RelayCommand]
    private void StartRename()
    {
        EditName = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (!IsRenaming)
            return;
        IsRenaming = false;
        var name = EditName.Trim();
        if (name.Length == 0 || name == Name)
            return;
        Name = name;
        await _owner.RenameAsync(this, name);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);

    [RelayCommand]
    private void AddRequest() => _owner.Open(new ApiRequest { CollectionId = Id, Name = "New Request" });
}

public sealed partial class RequestNodeViewModel(CollectionNodeViewModel parent, ApiRequest model) : TreeNodeViewModel
{
    public CollectionNodeViewModel Parent { get; } = parent;
    public ApiRequest Model { get; } = model;
    public string Name => Model.Name;
    public HttpVerb Method => Model.Method;

    [RelayCommand]
    private void Open() => Parent.Owner.Open(Model);

    [RelayCommand]
    private Task DeleteAsync() => Parent.Owner.DeleteRequestAsync(this);

    [RelayCommand]
    private Task DuplicateAsync() => Parent.Owner.DuplicateRequestAsync(this);
}
