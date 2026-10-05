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

public enum CollectionAction
{
    Run,
    LoadTest,
    Mock,
    ExportDispatch,
    ExportFolder,
    ExportPostman,
    ExportHttp,
    Settings,
    DocsHtml,
    DocsMarkdown,
    Scan,
    Laws,
    Flows,
    Monitors
}

public sealed partial class CollectionsViewModel(ICollectionRepository repository) : ObservableObject
{
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasNoMatches;

    public ObservableCollection<CollectionNodeViewModel> Items { get; } = [];

    /// <summary>Raised when the user asks to open a request in a tab.</summary>
    public event Action<ApiRequest>? OpenRequested;

    /// <summary>Raised after a request was deleted so its open tab can be detached.</summary>
    public event Action<Guid>? RequestDeleted;

    /// <summary>Collection-level actions handled by the main window (runner, mock server, export, ...).</summary>
    public event Action<CollectionNodeViewModel, CollectionAction>? ActionRequested;

    public async Task LoadAsync()
    {
        var expanded = Items.SelectMany(i => i.ExpandedKeys()).ToHashSet();
        var firstLoad = Items.Count == 0;
        var collections = await repository.GetAllAsync();

        Items.Clear();
        foreach (var c in collections)
        {
            var node = new CollectionNodeViewModel(this, c);
            node.RestoreExpansion(expanded, firstLoad);
            Items.Add(node);
        }
        ApplyFilter();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var term = Filter.Trim();
        foreach (var collection in Items)
            collection.ApplyFilter(term);
        HasNoMatches = term.Length > 0 && Items.Count > 0 && !Items.Any(i => i.IsVisible);
    }

    [RelayCommand]
    private void ClearFilter() => Filter = string.Empty;

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

    public RequestCollection? Find(Guid? id) => id is null ? null : Items.FirstOrDefault(i => i.Id == id)?.Model;

    internal async Task RenameAsync(CollectionNodeViewModel node, string name) =>
        await Guard(() => repository.RenameAsync(node.Id, name));

    internal async Task DeleteAsync(CollectionNodeViewModel node) => await Guard(async () =>
    {
        await repository.DeleteAsync(node.Id);
        Items.Remove(node);
        foreach (var r in node.Model.Requests)
            RequestDeleted?.Invoke(r.Id);
    });

    internal async Task DeleteRequestAsync(RequestNodeViewModel node) => await Guard(async () =>
    {
        await repository.DeleteRequestAsync(node.Model.Id);
        await LoadAsync();
        RequestDeleted?.Invoke(node.Model.Id);
    });

    internal async Task DuplicateRequestAsync(RequestNodeViewModel node) => await Guard(async () =>
    {
        var copy = node.Model.Clone(newIdentity: true);
        copy.CollectionId = node.Collection.Id;
        copy.Name = $"{node.Model.Name} (copy)";
        await repository.SaveRequestAsync(copy);
        await LoadAsync();
    });

    internal async Task MoveRequestAsync(RequestNodeViewModel node, string folder) => await Guard(async () =>
    {
        var model = node.Model.Clone();
        model.Folder = folder.Trim().Trim('/');
        await repository.SaveRequestAsync(model);
        await LoadAsync();
    });

    internal async Task RenameFolderAsync(CollectionNodeViewModel collection, string oldPath, string newPath) => await Guard(async () =>
    {
        foreach (var request in collection.Model.Requests.Where(r => r.Folder == oldPath || r.Folder.StartsWith(oldPath + "/", StringComparison.Ordinal)))
        {
            var model = request.Clone();
            model.Folder = newPath + model.Folder[oldPath.Length..];
            await repository.SaveRequestAsync(model);
        }
        await LoadAsync();
    });

    internal void Open(ApiRequest request) => OpenRequested?.Invoke(request.Clone());

    internal void RequestAction(CollectionNodeViewModel node, CollectionAction action) => ActionRequested?.Invoke(node, action);

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
        Model = model;
        Id = model.Id;
        _name = model.Name;
        Children = FolderNodeViewModel.BuildChildren(this, model.Requests, "");
    }

    public Guid Id { get; }
    public RequestCollection Model { get; }

    /// <summary>Folders first, then requests at the collection root.</summary>
    public ObservableCollection<TreeNodeViewModel> Children { get; }
    public int RequestCount => Model.Requests.Count;
    internal CollectionsViewModel Owner => _owner;

    internal IEnumerable<string> ExpandedKeys()
    {
        if (IsExpanded)
            yield return Id.ToString();
        foreach (var folder in AllFolders(Children).Where(f => f.IsExpanded))
            yield return $"{Id}/{folder.Path}";
    }

    internal void RestoreExpansion(HashSet<string> expanded, bool firstLoad)
    {
        IsExpanded = firstLoad || expanded.Contains(Id.ToString());
        foreach (var folder in AllFolders(Children))
            folder.IsExpanded = expanded.Contains($"{Id}/{folder.Path}");
    }

    private static IEnumerable<FolderNodeViewModel> AllFolders(IEnumerable<TreeNodeViewModel> nodes) =>
        nodes.OfType<FolderNodeViewModel>().SelectMany(f => AllFolders(f.Children).Prepend(f));

    internal void ApplyFilter(string term)
    {
        var collectionMatches = term.Length == 0 || Name.Contains(term, StringComparison.OrdinalIgnoreCase);
        var anyChild = FolderNodeViewModel.FilterChildren(Children, term, collectionMatches);
        IsVisible = collectionMatches || anyChild;
        if (term.Length > 0 && anyChild)
            IsExpanded = true;
    }

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
        Model.Name = name;
        await _owner.RenameAsync(this, name);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);

    [RelayCommand]
    private void AddRequest() => _owner.Open(new ApiRequest { CollectionId = Id, Name = "New Request", Headers = Application.Requests.DefaultHeaders.Create() });

    [RelayCommand]
    private void Action(CollectionAction action) => _owner.RequestAction(this, action);
}

public sealed partial class FolderNodeViewModel : TreeNodeViewModel
{
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _editName = string.Empty;

    private FolderNodeViewModel(CollectionNodeViewModel collection, string path, IEnumerable<ApiRequest> requests)
    {
        Collection = collection;
        Path = path;
        Name = path[(path.LastIndexOf('/') + 1)..];
        Children = BuildChildren(collection, requests, path);
    }

    public CollectionNodeViewModel Collection { get; }
    public string Path { get; }
    public string Name { get; }
    public ObservableCollection<TreeNodeViewModel> Children { get; }

    /// <summary>Groups requests by their next folder segment under <paramref name="parentPath"/>.</summary>
    internal static ObservableCollection<TreeNodeViewModel> BuildChildren(CollectionNodeViewModel collection,
        IEnumerable<ApiRequest> requests, string parentPath)
    {
        var list = requests.ToList();
        var nodes = new ObservableCollection<TreeNodeViewModel>();
        var prefix = parentPath.Length == 0 ? "" : parentPath + "/";

        var folders = list
            .Where(r => r.Folder.Length > prefix.Length && r.Folder.StartsWith(prefix, StringComparison.Ordinal))
            .Select(r => r.Folder[prefix.Length..].Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var path = prefix + folder;
            nodes.Add(new FolderNodeViewModel(collection, path,
                list.Where(r => r.Folder == path || r.Folder.StartsWith(path + "/", StringComparison.Ordinal))));
        }
        foreach (var request in list.Where(r => r.Folder == parentPath).OrderBy(r => r.SortOrder).ThenBy(r => r.Name))
            nodes.Add(new RequestNodeViewModel(collection, request));
        return nodes;
    }

    internal static bool FilterChildren(IEnumerable<TreeNodeViewModel> children, string term, bool parentMatches)
    {
        var any = false;
        foreach (var child in children)
        {
            switch (child)
            {
                case FolderNodeViewModel folder:
                    var folderMatches = parentMatches || folder.Name.Contains(term, StringComparison.OrdinalIgnoreCase);
                    var inner = FilterChildren(folder.Children, term, folderMatches);
                    folder.IsVisible = folderMatches || inner;
                    if (term.Length > 0 && inner)
                        folder.IsExpanded = true;
                    any |= folder.IsVisible;
                    break;
                case RequestNodeViewModel request:
                    request.IsVisible = parentMatches || term.Length == 0
                                        || request.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                                        || request.Model.Url.Contains(term, StringComparison.OrdinalIgnoreCase);
                    any |= request.IsVisible;
                    break;
            }
        }
        return any;
    }

    [RelayCommand]
    private void AddRequest() => Collection.Owner.Open(new ApiRequest
    {
        CollectionId = Collection.Id, Folder = Path, Name = "New Request", Headers = Application.Requests.DefaultHeaders.Create()
    });

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
        var name = EditName.Trim().Replace('/', '-');
        if (name.Length == 0 || name == Name)
            return;
        var parent = Path.Contains('/') ? Path[..Path.LastIndexOf('/')] + "/" : "";
        await Collection.Owner.RenameFolderAsync(Collection, Path, parent + name);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;
}

public sealed partial class RequestNodeViewModel(CollectionNodeViewModel collection, ApiRequest model) : TreeNodeViewModel
{
    [ObservableProperty] private bool _isMoving;
    [ObservableProperty] private string _moveTarget = model.Folder;

    public CollectionNodeViewModel Collection { get; } = collection;
    public ApiRequest Model { get; } = model;
    public string Name => Model.Name;
    public HttpVerb Method => Model.Method;
    public RequestKind Kind => Model.Kind;

    /// <summary>Badge text: the HTTP method, or the protocol for everything else.</summary>
    public object Badge => Model.Kind == RequestKind.Http ? Model.Method : Model.Kind;

    [RelayCommand]
    private void Open() => Collection.Owner.Open(Model);

    [RelayCommand]
    private Task DeleteAsync() => Collection.Owner.DeleteRequestAsync(this);

    [RelayCommand]
    private Task DuplicateAsync() => Collection.Owner.DuplicateRequestAsync(this);

    [RelayCommand]
    private void StartMove()
    {
        MoveTarget = Model.Folder;
        IsMoving = true;
    }

    [RelayCommand]
    private async Task CommitMoveAsync()
    {
        IsMoving = false;
        if (MoveTarget.Trim().Trim('/') != Model.Folder)
            await Collection.Owner.MoveRequestAsync(this, MoveTarget);
    }

    [RelayCommand]
    private void CancelMove() => IsMoving = false;
}
