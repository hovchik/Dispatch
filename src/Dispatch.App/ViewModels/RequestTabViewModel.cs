using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Formatting;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels;

/// <summary>What a request tab needs from the surrounding workspace.</summary>
public interface ITabHost
{
    ApiEnvironment? ActiveEnvironment { get; }
    ObservableCollection<CollectionNodeViewModel> CollectionNodes { get; }
    Task<CollectionNodeViewModel> CreateDefaultCollectionAsync();
    Task OnRequestSentAsync();
    Task OnRequestSavedAsync();
    void CloseTab(RequestTabViewModel tab);
    void ReportError(string message);
}

public sealed partial class RequestTabViewModel : ObservableObject
{
    private readonly IRequestSender _sender;
    private readonly ICollectionRepository _collections;
    private readonly IClipboardService _clipboard;
    private readonly ITabHost _host;

    private readonly Guid _requestId;
    private int _sortOrder;
    private bool _loading;
    private bool _syncingQuery;

    public static IReadOnlyList<HttpVerb> Methods { get; } = Enum.GetValues<HttpVerb>();
    public static IReadOnlyList<BodyMode> BodyModes { get; } = Enum.GetValues<BodyMode>();
    public static IReadOnlyList<AuthMode> AuthModes { get; } = Enum.GetValues<AuthMode>();
    public static IReadOnlyList<ApiKeyLocation> ApiKeyLocations { get; } = Enum.GetValues<ApiKeyLocation>();

    public RequestTabViewModel(
        ApiRequest request,
        IRequestSender sender,
        ICollectionRepository collections,
        IClipboardService clipboard,
        ITabHost host)
    {
        _sender = sender;
        _collections = collections;
        _clipboard = clipboard;
        _host = host;
        _requestId = request.Id;

        Params = new KeyValueListViewModel("Parameter", "Value", supportsBulkEdit: true);
        Headers = new KeyValueListViewModel("Header", "Value", supportsBulkEdit: true);
        FormFields = new KeyValueListViewModel("Field", "Value", supportsBulkEdit: true);

        Load(request);

        Params.Changed += OnParamsChanged;
        Headers.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(BodyFormat));
            MarkDirty();
        };
        FormFields.Changed += (_, _) => MarkDirty();
    }

    public Guid RequestId => _requestId;
    public KeyValueListViewModel Params { get; }
    public KeyValueListViewModel Headers { get; }
    public KeyValueListViewModel FormFields { get; }
    public ObservableCollection<CollectionNodeViewModel> SaveTargets => _host.CollectionNodes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaved))]
    private Guid? _collectionId;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private HttpVerb _method;
    [ObservableProperty] private string _url = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBodyNone), nameof(IsBodyText), nameof(IsBodyForm), nameof(CanBeautify),
        nameof(BodyFormat))]
    private BodyMode _bodyMode;

    [ObservableProperty] private string _bodyText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuthNone), nameof(IsAuthBearer), nameof(IsAuthBasic), nameof(IsAuthApiKey))]
    private AuthMode _authMode;

    [ObservableProperty] private string _bearerToken = string.Empty;
    [ObservableProperty] private string _basicUsername = string.Empty;
    [ObservableProperty] private string _basicPassword = string.Empty;
    [ObservableProperty] private string _apiKeyName = string.Empty;
    [ObservableProperty] private string _apiKeyValue = string.Empty;
    [ObservableProperty] private ApiKeyLocation _apiKeyLocation;

    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private ResponseViewModel? _response;
    [ObservableProperty] private string? _bodyError;

    // Save-as popup state (used when the request doesn't belong to a collection yet). The name is
    // whatever the tab is called, so the popup only asks for the collection.
    [ObservableProperty] private bool _isSavePopupOpen;
    [ObservableProperty] private CollectionNodeViewModel? _saveTarget;

    // Inline rename of the tab header (double-click the tab title).
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = string.Empty;

    public bool IsSaved => CollectionId is not null;
    public bool IsBodyNone => BodyMode == BodyMode.None;
    public bool IsBodyText => BodyMode is BodyMode.Json or BodyMode.Text or BodyMode.Xml;
    public bool IsBodyForm => BodyMode == BodyMode.FormUrlEncoded;
    /// <summary>How the body editor highlights the body: from the Content-Type header, else the body mode.</summary>
    public BodyFormat BodyFormat =>
        BodyFormatter.Detect(Headers.GetValue(DefaultHeaders.ContentType) ?? DefaultHeaders.ContentTypeFor(BodyMode));

    public bool CanBeautify => BodyMode is BodyMode.Json or BodyMode.Xml;
    public bool IsAuthNone => AuthMode == AuthMode.None;
    public bool IsAuthBearer => AuthMode == AuthMode.Bearer;
    public bool IsAuthBasic => AuthMode == AuthMode.Basic;
    public bool IsAuthApiKey => AuthMode == AuthMode.ApiKey;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Untitled" : Name;

    private void Load(ApiRequest r)
    {
        _loading = true;
        try
        {
            CollectionId = r.CollectionId;
            _sortOrder = r.SortOrder;
            Name = r.Name;
            Method = r.Method;
            Url = r.Url;
            Params.Load(r.QueryParams.Count > 0 ? r.QueryParams : QueryString.Parse(r.Url));
            Headers.Load(r.Headers);
            BodyMode = r.Body.Mode;
            BodyText = r.Body.Content;
            FormFields.Load(r.Body.FormFields);
            AuthMode = r.Auth.Mode;
            BearerToken = r.Auth.Token;
            BasicUsername = r.Auth.Username;
            BasicPassword = r.Auth.Password;
            ApiKeyName = r.Auth.ApiKeyName;
            ApiKeyValue = r.Auth.ApiKeyValue;
            ApiKeyLocation = r.Auth.ApiKeyLocation;
            IsDirty = false;
        }
        finally
        {
            _loading = false;
        }
    }

    public ApiRequest ToModel() => new()
    {
        Id = _requestId,
        CollectionId = CollectionId,
        SortOrder = _sortOrder,
        Name = DisplayName,
        Method = Method,
        Url = Url.Trim(),
        QueryParams = Params.ToItems(),
        Headers = Headers.ToItems(),
        Body = new RequestBody { Mode = BodyMode, Content = BodyText, FormFields = FormFields.ToItems() },
        Auth = new AuthSettings
        {
            Mode = AuthMode,
            Token = BearerToken,
            Username = BasicUsername,
            Password = BasicPassword,
            ApiKeyName = ApiKeyName,
            ApiKeyValue = ApiKeyValue,
            ApiKeyLocation = ApiKeyLocation
        }
    };

    // ---- URL <-> Params two-way sync -------------------------------------------------------

    partial void OnUrlChanged(string value)
    {
        if (!_syncingQuery && !_loading)
        {
            _syncingQuery = true;
            try
            {
                // Params typed in the URL replace the enabled rows; disabled rows are kept (they aren't in the URL).
                var disabled = Params.ToItems().Where(p => !p.Enabled);
                Params.Load(QueryString.Parse(value).Concat(disabled));
            }
            finally
            {
                _syncingQuery = false;
            }
        }
        MarkDirty();
    }

    private void OnParamsChanged(object? sender, EventArgs e)
    {
        if (_syncingQuery || _loading)
            return;

        _syncingQuery = true;
        try
        {
            Url = QueryString.WithParams(Url, Params.ToItems());
        }
        finally
        {
            _syncingQuery = false;
        }
        MarkDirty();
    }

    // ---- Dirty tracking ----------------------------------------------------------------------

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        MarkDirty();
    }

    partial void OnMethodChanged(HttpVerb value) => MarkDirty();
    partial void OnBodyModeChanged(BodyMode value)
    {
        if (!_loading)
            SyncContentTypeHeader(value);
        MarkDirty();
    }

    /// <summary>
    /// Like Postman, picking a body type updates the Content-Type header, unless the user typed a custom one.
    /// </summary>
    private void SyncContentTypeHeader(BodyMode mode)
    {
        var current = Headers.GetValue(DefaultHeaders.ContentType);
        if (current is not null && !DefaultHeaders.IsGeneratedContentType(current))
            return;

        if (DefaultHeaders.ContentTypeFor(mode) is { } contentType)
            Headers.SetValue(DefaultHeaders.ContentType, contentType);
        else
            Headers.RemoveKey(DefaultHeaders.ContentType);
    }
    partial void OnBodyTextChanged(string value)
    {
        BodyError = null;
        MarkDirty();
    }
    partial void OnAuthModeChanged(AuthMode value) => MarkDirty();
    partial void OnBearerTokenChanged(string value) => MarkDirty();
    partial void OnBasicUsernameChanged(string value) => MarkDirty();
    partial void OnBasicPasswordChanged(string value) => MarkDirty();
    partial void OnApiKeyNameChanged(string value) => MarkDirty();
    partial void OnApiKeyValueChanged(string value) => MarkDirty();
    partial void OnApiKeyLocationChanged(ApiKeyLocation value) => MarkDirty();

    private void MarkDirty()
    {
        if (!_loading)
            IsDirty = true;
    }

    // ---- Commands ----------------------------------------------------------------------------

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        IsSending = true;
        try
        {
            var response = await _sender.SendAsync(ToModel(), _host.ActiveEnvironment, cancellationToken);
            Response = await ResponseViewModel.CreateAsync(response, _clipboard);
            await _host.OnRequestSentAsync();
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (CollectionId is null)
        {
            CommitRename();
            SaveTarget ??= SaveTargets.FirstOrDefault();
            IsSavePopupOpen = true;
            return;
        }
        await PersistAsync();
    }

    [RelayCommand]
    private async Task ConfirmSaveAsync()
    {
        CollectionNodeViewModel target;
        try
        {
            target = SaveTarget ?? await _host.CreateDefaultCollectionAsync();
        }
        catch (Exception ex)
        {
            _host.ReportError($"Could not create collection: {ex.Message}");
            return;
        }
        CollectionId = target.Id;
        IsSavePopupOpen = false;
        await PersistAsync();
    }

    [RelayCommand]
    private void CancelSave() => IsSavePopupOpen = false;

    private async Task PersistAsync()
    {
        try
        {
            var model = ToModel();
            await _collections.SaveRequestAsync(model);
            _sortOrder = model.SortOrder;
            IsDirty = false;
            await _host.OnRequestSavedAsync();
        }
        catch (Exception ex)
        {
            _host.ReportError($"Could not save request: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Beautify()
    {
        var ok = BodyMode switch
        {
            BodyMode.Json => BodyFormatter.TryFormatJson(BodyText, out var json) && Set(json),
            BodyMode.Xml => BodyFormatter.TryFormatXml(BodyText, out var xml) && Set(xml),
            _ => true
        };
        if (!ok)
            BodyError = $"Body is not valid {BodyMode.ToString().ToUpperInvariant()}.";

        bool Set(string formatted)
        {
            BodyText = formatted;
            return true;
        }
    }

    [RelayCommand]
    private void Close() => _host.CloseTab(this);

    [RelayCommand]
    private void BeginRename()
    {
        RenameText = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (!IsRenaming)
            return;
        if (!string.IsNullOrWhiteSpace(RenameText))
            Name = RenameText.Trim();
        IsRenaming = false;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;
}
