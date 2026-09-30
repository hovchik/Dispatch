using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.App.ViewModels.Editors;
using Dispatch.Application.Formatting;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels;

/// <summary>What a request tab needs from the surrounding workspace.</summary>
public interface ITabHost
{
    ApiEnvironment? ActiveEnvironment { get; }
    ObservableCollection<CollectionNodeViewModel> CollectionNodes { get; }
    RequestCollection? FindCollection(Guid? id);
    Task<CollectionNodeViewModel> CreateDefaultCollectionAsync();
    Task OnRequestSentAsync(ApiResponse response);
    Task OnRequestSavedAsync();
    void CloseTab(RequestTabViewModel tab);
    void ReportError(string message);
    void ShowCode(ApiRequest request, ApiRequest resolved);
    void ShowDiff(string title, ResponseViewModel left, ResponseViewModel right);
}

public sealed partial class MessageItemViewModel(StreamMessage message)
{
    public StreamMessage Model { get; } = message;
    public string Time => Model.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    public string Content => Model.Content;
    public string? Label => Model.Label;
    public bool HasLabel => !string.IsNullOrEmpty(Model.Label);
    public MessageDirection Direction => Model.Direction;
    public string Arrow => Model.Direction switch
    {
        MessageDirection.Sent => "↑",
        MessageDirection.Received => "↓",
        MessageDirection.Error => "!",
        _ => "•"
    };
    public bool IsInfo => Model.Direction is MessageDirection.Info or MessageDirection.Error;
}

public sealed partial class RequestTabViewModel : ObservableObject
{
    private readonly RequestTabServices _services;
    private readonly ITabHost _host;

    private readonly Guid _requestId;
    private int _sortOrder;
    private bool _loading;
    private bool _syncingQuery;
    private Channel<string>? _outgoing;

    public static IReadOnlyList<RequestKind> Kinds { get; } = Enum.GetValues<RequestKind>();
    public static IReadOnlyList<HttpVerb> Methods { get; } = Enum.GetValues<HttpVerb>();
    public static IReadOnlyList<BodyMode> BodyModes { get; } = Enum.GetValues<BodyMode>();

    public RequestTabViewModel(ApiRequest request, RequestTabServices services, ITabHost host)
    {
        _services = services;
        _host = host;
        _requestId = request.Id;

        Params = new KeyValueListViewModel("Parameter", "Value", supportsBulkEdit: true);
        Headers = new KeyValueListViewModel("Header", "Value", supportsBulkEdit: true);
        FormFields = new KeyValueListViewModel("Field", "Value", supportsBulkEdit: true, supportsFile: true)
        {
            PickFile = () => services.Dialogs.OpenFileAsync("Choose a file to upload")
        };

        Auth = new AuthEditor();
        Settings = new SettingsEditor();
        GraphQl = new GraphQlEditor(IntrospectAsync);
        Grpc = new GrpcEditor(LoadGrpcSchemaAsync,
            () => services.Dialogs.OpenFilesAsync("Choose .proto files", new FileFilter("Protocol Buffers", "*.proto")),
            () => services.Dialogs.OpenFolderAsync("Choose an import path"));
        Soap = new SoapEditor(LoadWsdlAsync, UseSoapOperation);
        Stream = new StreamEditor();
        SocketIo = new SocketIoEditor();
        Mqtt = new MqttEditor();
        Kafka = new KafkaEditor();
        Amqp = new AmqpEditor();
        Socket = new SocketEditor();
        Assertions = new AssertionsEditor();
        Extractions = new ExtractionsEditor();
        Examples = new ExamplesEditor();

        Load(request);

        Params.Changed += OnParamsChanged;
        Headers.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(BodyFormat));
            MarkDirty();
        };
        EventHandler markDirty = (_, _) => MarkDirty();
        FormFields.Changed += markDirty;
        Auth.Changed += markDirty;
        Settings.Changed += markDirty;
        GraphQl.Changed += markDirty;
        Grpc.Changed += markDirty;
        Soap.Changed += markDirty;
        Stream.Changed += markDirty;
        SocketIo.Changed += markDirty;
        Mqtt.Changed += markDirty;
        Kafka.Changed += markDirty;
        Amqp.Changed += markDirty;
        Socket.Changed += markDirty;
        Assertions.Changed += markDirty;
        Extractions.Changed += markDirty;
        Examples.Changed += markDirty;
        Grpc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GrpcEditor.IsClientStreaming) or nameof(GrpcEditor.Method))
                OnPropertyChanged(nameof(CanCompose));
        };
    }

    public Guid RequestId => _requestId;
    public KeyValueListViewModel Params { get; }
    public KeyValueListViewModel Headers { get; }
    public KeyValueListViewModel FormFields { get; }
    public AuthEditor Auth { get; }
    public SettingsEditor Settings { get; }
    public GraphQlEditor GraphQl { get; }
    public GrpcEditor Grpc { get; }
    public SoapEditor Soap { get; }
    public StreamEditor Stream { get; }
    public SocketIoEditor SocketIo { get; }
    public MqttEditor Mqtt { get; }
    public KafkaEditor Kafka { get; }
    public AmqpEditor Amqp { get; }
    public SocketEditor Socket { get; }
    public AssertionsEditor Assertions { get; }
    public ExtractionsEditor Extractions { get; }
    public ExamplesEditor Examples { get; }
    public ObservableCollection<CollectionNodeViewModel> SaveTargets => _host.CollectionNodes;

    /// <summary>Live messages of the current (or last) streaming session.</summary>
    public ObservableCollection<MessageItemViewModel> LiveMessages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaved))]
    private Guid? _collectionId;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _folder = string.Empty;
    [ObservableProperty] private string _description = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHttp), nameof(IsGraphQl), nameof(IsGrpc), nameof(IsSoap), nameof(IsWebSocket), nameof(IsSse),
        nameof(IsSocketIo), nameof(IsMqtt), nameof(IsKafka), nameof(IsAmqp), nameof(IsSocket), nameof(ShowMethod), nameof(ShowParams),
        nameof(ShowHeaders), nameof(ShowAuth), nameof(ShowHttpBody), nameof(ShowSettings), nameof(IsStreamingKind), nameof(CanCompose),
        nameof(SendLabel), nameof(UrlWatermark), nameof(HeadersLabel), nameof(IsSessionKind), nameof(BodyFormat), nameof(Badge))]
    private RequestKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge))]
    private HttpVerb _method;
    [ObservableProperty] private string _url = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBodyNone), nameof(IsBodyText), nameof(IsBodyForm), nameof(IsBodyBinary), nameof(CanBeautify),
        nameof(BodyFormat))]
    private BodyMode _bodyMode;

    [ObservableProperty] private string _bodyText = string.Empty;
    [ObservableProperty] private string _binaryFilePath = string.Empty;
    [ObservableProperty] private string _preRequestScript = string.Empty;
    [ObservableProperty] private string _testScript = string.Empty;

    [ObservableProperty] private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendLabel), nameof(CanCompose), nameof(ShowLiveSession))]
    private bool _isSending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousResponse), nameof(ShowLiveSession))]
    private ResponseViewModel? _response;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousResponse))]
    private ResponseViewModel? _previousResponse;

    [ObservableProperty] private string? _bodyError;
    [ObservableProperty] private string _outgoingText = string.Empty;

    // Save-as popup state (used when the request doesn't belong to a collection yet).
    [ObservableProperty] private bool _isSavePopupOpen;
    [ObservableProperty] private CollectionNodeViewModel? _saveTarget;

    // Inline rename of the tab header (double-click the tab title).
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = string.Empty;

    public bool IsSaved => CollectionId is not null;

    /// <summary>Tab badge: the HTTP method, or the protocol for everything else.</summary>
    public object Badge => Kind == RequestKind.Http ? Method : Kind;
    public bool IsHttp => Kind == RequestKind.Http;
    public bool IsGraphQl => Kind == RequestKind.GraphQl;
    public bool IsGrpc => Kind == RequestKind.Grpc;
    public bool IsSoap => Kind == RequestKind.Soap;
    public bool IsWebSocket => Kind == RequestKind.WebSocket;
    public bool IsSse => Kind == RequestKind.Sse;
    public bool IsSocketIo => Kind == RequestKind.SocketIo;
    public bool IsMqtt => Kind == RequestKind.Mqtt;
    public bool IsKafka => Kind == RequestKind.Kafka;
    public bool IsAmqp => Kind == RequestKind.Amqp;
    public bool IsSocket => Kind is RequestKind.Tcp or RequestKind.Udp;

    public bool ShowMethod => Kind is RequestKind.Http or RequestKind.Sse;
    public bool ShowParams => Kind is RequestKind.Http or RequestKind.Sse or RequestKind.WebSocket or RequestKind.GraphQl or RequestKind.Soap;
    public bool ShowHttpBody => Kind == RequestKind.Http;
    public bool ShowHeaders => Kind is not (RequestKind.Tcp or RequestKind.Udp);
    public bool ShowAuth => Kind is not (RequestKind.Tcp or RequestKind.Udp or RequestKind.Mqtt or RequestKind.Kafka or RequestKind.Amqp);
    public bool ShowSettings => true;

    public string HeadersLabel => Kind switch
    {
        RequestKind.Grpc => "Metadata",
        RequestKind.Mqtt => "User properties",
        RequestKind.Kafka or RequestKind.Amqp => "Headers",
        _ => "Headers"
    };

    /// <summary>Protocols whose response is a stream of messages shown live while connected.</summary>
    public bool IsStreamingKind => Kind is RequestKind.WebSocket or RequestKind.Sse or RequestKind.SocketIo or RequestKind.Mqtt
        or RequestKind.Kafka or RequestKind.Amqp or RequestKind.Tcp or RequestKind.Udp or RequestKind.Grpc or RequestKind.GraphQl;

    /// <summary>Protocols with a persistent connection, where Send reads "Connect".</summary>
    public bool IsSessionKind => Kind is RequestKind.WebSocket or RequestKind.Sse or RequestKind.SocketIo;

    /// <summary>While connected, the user can type messages to send on the open connection.</summary>
    public bool CanCompose => IsSending && (Kind is RequestKind.WebSocket or RequestKind.SocketIo or RequestKind.Tcp or RequestKind.Udp
                                            or RequestKind.Mqtt or RequestKind.Kafka or RequestKind.Amqp
                                            || (Kind == RequestKind.Grpc && Grpc.IsClientStreaming));

    public bool ShowLiveSession => IsSending && IsStreamingKind;
    public bool HasPreviousResponse => PreviousResponse is not null && Response is not null;

    public string SendLabel => IsSending ? (IsSessionKind ? "Disconnect" : "Cancel") : IsSessionKind ? "Connect" : "Send";

    public string UrlWatermark => Kind switch
    {
        RequestKind.Http => "Enter URL, e.g. {{baseUrl}}/users?page=1",
        RequestKind.GraphQl => "GraphQL endpoint, e.g. https://api.example.com/graphql",
        RequestKind.Grpc => "Server address, e.g. localhost:50051 or https://api.example.com",
        RequestKind.Soap => "Service endpoint (filled in from the WSDL)",
        RequestKind.WebSocket => "ws://localhost:8080/socket",
        RequestKind.Sse => "https://example.com/events",
        RequestKind.SocketIo => "http://localhost:3000",
        RequestKind.Mqtt => "mqtt://broker.example.com:1883 (mqtts://, ws://, wss:// also work)",
        RequestKind.Kafka => "Bootstrap servers, e.g. localhost:9092",
        RequestKind.Amqp => "amqp://guest:guest@localhost:5672/",
        RequestKind.Tcp => "tcp://localhost:7000",
        RequestKind.Udp => "udp://localhost:9000",
        _ => ""
    };

    public bool IsBodyNone => BodyMode == BodyMode.None;
    public bool IsBodyText => BodyMode is BodyMode.Json or BodyMode.Text or BodyMode.Xml;
    public bool IsBodyForm => BodyMode is BodyMode.FormUrlEncoded or BodyMode.Multipart;
    public bool IsBodyBinary => BodyMode == BodyMode.Binary;

    /// <summary>How the body editor highlights the body: from the Content-Type header, else the body mode.</summary>
    public BodyFormat BodyFormat => Kind == RequestKind.Soap
        ? BodyFormat.Xml
        : BodyFormatter.Detect(Headers.GetValue(DefaultHeaders.ContentType) ?? DefaultHeaders.ContentTypeFor(BodyMode));

    public bool CanBeautify => BodyMode is BodyMode.Json or BodyMode.Xml;
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Untitled" : Name;

    private void Load(ApiRequest r)
    {
        _loading = true;
        try
        {
            CollectionId = r.CollectionId;
            _sortOrder = r.SortOrder;
            Name = r.Name;
            Folder = r.Folder;
            Description = r.Description;
            Kind = r.Kind;
            Method = r.Method;
            Url = r.Url;
            Params.Load(r.QueryParams.Count > 0 ? r.QueryParams : QueryString.Parse(r.Url));
            Headers.Load(r.Headers);
            BodyMode = r.Body.Mode;
            BodyText = r.Body.Content;
            BinaryFilePath = r.Body.FilePath;
            FormFields.Load(r.Body.FormFields);
            Auth.Load(r.Auth);
            Settings.Load(r.Settings);
            GraphQl.Load(r.Protocol.GraphQl);
            Grpc.Load(r.Protocol.Grpc);
            Soap.Load(r.Protocol.Soap);
            Stream.Load(r.Protocol.Stream);
            SocketIo.Load(r.Protocol.SocketIo);
            Mqtt.Load(r.Protocol.Mqtt);
            Kafka.Load(r.Protocol.Kafka);
            Amqp.Load(r.Protocol.Amqp);
            Socket.Load(r.Protocol.Socket);
            Assertions.Load(r.Assertions);
            Extractions.Load(r.Extractions);
            Examples.Load(r.Examples);
            PreRequestScript = r.PreRequestScript;
            TestScript = r.TestScript;
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
        Folder = Folder.Trim().Trim('/'),
        Description = Description,
        Kind = Kind,
        Method = Method,
        Url = Url.Trim(),
        QueryParams = Params.ToItems(),
        Headers = Headers.ToItems(),
        Body = new RequestBody { Mode = BodyMode, Content = BodyText, FilePath = BinaryFilePath, FormFields = FormFields.ToItems() },
        Auth = Auth.ToModel(),
        Settings = Settings.ToModel(),
        Protocol = new ProtocolSettings
        {
            GraphQl = GraphQl.ToModel(),
            Grpc = Grpc.ToModel(),
            Soap = Soap.ToModel(),
            Stream = Stream.ToModel(),
            SocketIo = SocketIo.ToModel(),
            Mqtt = Mqtt.ToModel(),
            Kafka = Kafka.ToModel(),
            Amqp = Amqp.ToModel(),
            Socket = Socket.ToModel()
        },
        Assertions = Assertions.ToModels(),
        Extractions = Extractions.ToModels(),
        Examples = Examples.ToModels(),
        PreRequestScript = PreRequestScript,
        TestScript = TestScript
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

    partial void OnFolderChanged(string value) => MarkDirty();
    partial void OnDescriptionChanged(string value) => MarkDirty();
    partial void OnMethodChanged(HttpVerb value) => MarkDirty();

    partial void OnKindChanged(RequestKind value)
    {
        if (!_loading && value == RequestKind.Grpc && Grpc.Services.Count <= 1 && !string.IsNullOrWhiteSpace(Url))
            _ = Grpc.LoadSchemaAsync(refresh: false, CancellationToken.None);
        MarkDirty();
    }

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

    partial void OnBinaryFilePathChanged(string value) => MarkDirty();
    partial void OnPreRequestScriptChanged(string value) => MarkDirty();
    partial void OnTestScriptChanged(string value) => MarkDirty();

    private void MarkDirty()
    {
        if (!_loading)
            IsDirty = true;
    }

    // ---- Protocol helpers --------------------------------------------------------------------

    private async Task<Application.GraphQl.GraphQlSchema> IntrospectAsync(CancellationToken ct)
    {
        var resolved = Resolve(ToModel());
        return await _services.GraphQl.IntrospectAsync(resolved, ct);
    }

    private async Task<Application.Grpc.ProtoSchema> LoadGrpcSchemaAsync(bool refresh, CancellationToken ct) =>
        await _services.GrpcSchemas.GetSchemaAsync(Resolve(ToModel()), refresh, ct);

    private async Task<Application.Soap.WsdlDocument> LoadWsdlAsync(string location, CancellationToken ct)
    {
        var resolved = Application.Variables.VariableResolver.Resolve(location, Variables());
        return await _services.Wsdl.LoadAsync(resolved, Settings.ToModel(), refresh: true, ct);
    }

    private void UseSoapOperation(Application.Soap.SoapOperation operation, string envelope)
    {
        if (!string.IsNullOrWhiteSpace(operation.Endpoint))
            Url = operation.Endpoint;
        BodyText = envelope;
        if (string.IsNullOrWhiteSpace(Name) || Name == "New Request")
            Name = operation.Name;
    }

    private IReadOnlyDictionary<string, string> Variables() =>
        Application.Variables.VariableContext.For(_host.ActiveEnvironment, _host.FindCollection(CollectionId)?.Variables).Merged();

    private ApiRequest Resolve(ApiRequest request) => RequestResolver.Resolve(request, Variables());

    // ---- Commands ----------------------------------------------------------------------------

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        IsSending = true;
        LiveMessages.Clear();
        OutgoingText = string.Empty;
        _outgoing = Channel.CreateUnbounded<string>();
        var progress = new Progress<StreamMessage>(m => LiveMessages.Add(new MessageItemViewModel(m)));
        var collection = _host.FindCollection(CollectionId);
        try
        {
            var response = await _services.Sender.SendAsync(ToModel(), new SendOptions
            {
                Environment = _host.ActiveEnvironment,
                CollectionVariables = collection?.Variables,
                CollectionSpec = collection?.SpecLocation,
                Progress = progress,
                Outgoing = _outgoing.Reader,
                Interactive = true
            }, cancellationToken);

            if (Response is not null)
                PreviousResponse = Response;
            Response = await ResponseViewModel.CreateAsync(response, _services.Clipboard, _services.Dialogs);
            Assertions.ApplySnapshots(response.SnapshotUpdates);
            Assertions.ShowResults(response.TestResults);
            if (response.RefreshedAuth is { } refreshed)
                Auth.CacheToken(refreshed);
            await _host.OnRequestSentAsync(response);
        }
        finally
        {
            _outgoing.Writer.TryComplete();
            _outgoing = null;
            IsSending = false;
        }
    }

    /// <summary>Send = connect/send; while a session is open the same button disconnects.</summary>
    [RelayCommand]
    private void SendOrStop()
    {
        if (IsSending)
            SendCancelCommand.Execute(null);
        else
            SendCommand.Execute(null);
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        var text = OutgoingText;
        if (_outgoing is null || string.IsNullOrEmpty(text))
            return;
        await _outgoing.Writer.WriteAsync(text);
        OutgoingText = string.Empty;
    }

    [RelayCommand]
    private async Task SendSavedMessageAsync(KeyValueRowViewModel? row)
    {
        if (_outgoing is not null && row is { Value.Length: > 0 })
            await _outgoing.Writer.WriteAsync(row.Value);
    }

    /// <summary>Half-closes a gRPC client stream so the server can respond.</summary>
    [RelayCommand]
    private void EndStream() => _outgoing?.Writer.TryComplete();

    [RelayCommand]
    private void ClearLiveMessages() => LiveMessages.Clear();

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
            await _services.Collections.SaveRequestAsync(model);
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
    private void BeautifyEnvelope()
    {
        if (BodyFormatter.TryFormatXml(BodyText, out var xml))
            BodyText = xml;
        else
            BodyError = "The envelope is not valid XML.";
    }

    [RelayCommand]
    private async Task ChooseBinaryFileAsync()
    {
        if (await _services.Dialogs.OpenFileAsync("Choose the file to send") is { } path)
            BinaryFilePath = path;
    }

    [RelayCommand]
    private async Task ChooseClientCertificateAsync()
    {
        if (await _services.Dialogs.OpenFileAsync("Choose a client certificate",
                new FileFilter("Certificates", "*.pfx", "*.p12", "*.pem", "*.crt", "*.cer"), new FileFilter("All files", "*")) is { } path)
            Settings.ClientCertificatePath = path;
    }

    [RelayCommand]
    private void ShowCode() => _host.ShowCode(ToModel(), Resolve(ToModel()));

    [RelayCommand]
    private void CompareWithPrevious()
    {
        if (PreviousResponse is not null && Response is not null)
            _host.ShowDiff($"{DisplayName}: previous vs latest response", PreviousResponse, Response);
    }

    /// <summary>Keeps the current response as a mock/documentation example.</summary>
    [RelayCommand]
    private void SaveResponseAsExample()
    {
        if (Response?.Model is not { HasResponse: true } r)
            return;
        Examples.AddAndSelect(new ResponseExample
        {
            Name = $"{r.StatusCode} {r.ReasonPhrase}".Trim(),
            StatusCode = r.StatusCode,
            ContentType = r.ContentType ?? "",
            Body = r.Body,
            Headers = r.Headers
                .Where(h => h.Name is not ("Date" or "Content-Length" or "Transfer-Encoding" or "Connection" or "Server")
                            && !h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                .Select(h => new KeyValueItem(h.Name, h.Value))
                .ToList()
        });
        MarkDirty();
    }

    [RelayCommand]
    private void AddAssertionFromResponse(string? kind)
    {
        switch (kind)
        {
            case "status" when Response is { HasResponse: true } r:
                Assertions.AddModel(new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = r.StatusCode.ToString() });
                break;
            case "time":
                Assertions.AddModel(new Assertion { Source = ValueSource.ResponseTime, Operator = AssertionOperator.LessThan, Expected = "1000" });
                break;
            case "schema":
                Assertions.AddModel(new Assertion { Source = ValueSource.Contract, Operator = AssertionOperator.IsValid });
                break;
            case "snapshot":
                Assertions.AddModel(new Assertion
                {
                    Source = ValueSource.Snapshot,
                    Operator = AssertionOperator.IsValid,
                    Expected = Response is { HasResponse: true } last ? Application.Testing.Snapshots.Capture(last.Model) : ""
                });
                break;
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

    /// <summary>Snippets for inserting common test code (script tab menu).</summary>
    [RelayCommand]
    private void InsertSnippet(string? snippet)
    {
        var code = snippet switch
        {
            "status" => "pm.test(\"Status is 200\", () => pm.response.to.have.status(200));",
            "json" => "pm.test(\"Body has an id\", () => {\n  const json = pm.response.json();\n  pm.expect(json).to.have.property(\"id\");\n});",
            "time" => "pm.test(\"Fast enough\", () => pm.expect(pm.response.responseTime).to.be.below(500));",
            "header" => "pm.test(\"JSON content type\", () => pm.response.to.have.header(\"Content-Type\"));",
            "setvar" => "pm.environment.set(\"token\", pm.response.json().token);",
            "getvar" => "const base = pm.environment.get(\"baseUrl\");",
            "log" => "console.log(pm.response.json());",
            _ => null
        };
        if (code is not null)
            TestScript = string.IsNullOrWhiteSpace(TestScript) ? code : TestScript.TrimEnd() + "\n" + code;
    }
}
