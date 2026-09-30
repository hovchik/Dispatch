using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.Application.GraphQl;
using Dispatch.Application.Grpc;
using Dispatch.Application.Soap;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Editors;

public sealed record SchemaFieldItem(string Operation, GraphQlField Field)
{
    public string Display => $"{Field.Name}{(Field.Args.Count > 0 ? "(" + string.Join(", ", Field.Args.Select(a => $"{a.Name}: {a.Type}")) + ")" : "")}: {Field.Type}";
    public string? Description => Field.Description;
}

public sealed class GraphQlEditor : ModelEditor<GraphQlSettings>
{
    private readonly Func<CancellationToken, Task<GraphQlSchema>> _introspect;
    private GraphQlSchema? _schema;
    private string _schemaStatus = "Fetch the schema to browse operations.";
    private bool _isLoadingSchema;
    private string _schemaFilter = "";

    public GraphQlEditor(Func<CancellationToken, Task<GraphQlSchema>> introspect) : base(new GraphQlSettings())
    {
        _introspect = introspect;
        IntrospectCommand = new AsyncRelayCommand(IntrospectAsync);
        UseFieldCommand = new RelayCommand<SchemaFieldItem>(UseField);
    }

    public string Query { get => Model.Query; set => Set(Model.Query, value, (m, v) => m.Query = v); }
    public string Variables { get => Model.Variables; set => Set(Model.Variables, value, (m, v) => m.Variables = v); }
    public string OperationName { get => Model.OperationName; set => Set(Model.OperationName, value, (m, v) => m.OperationName = v); }
    public string SubscriptionUrl { get => Model.SubscriptionUrl; set => Set(Model.SubscriptionUrl, value, (m, v) => m.SubscriptionUrl = v); }

    public ObservableCollection<SchemaFieldItem> SchemaFields { get; } = [];

    public string SchemaStatus
    {
        get => _schemaStatus;
        private set => SetProperty(ref _schemaStatus, value);
    }

    public bool IsLoadingSchema
    {
        get => _isLoadingSchema;
        private set => SetProperty(ref _isLoadingSchema, value);
    }

    public string SchemaFilter
    {
        get => _schemaFilter;
        set
        {
            if (SetProperty(ref _schemaFilter, value))
                RebuildFields();
        }
    }

    public IAsyncRelayCommand IntrospectCommand { get; }
    public IRelayCommand<SchemaFieldItem> UseFieldCommand { get; }

    private async Task IntrospectAsync(CancellationToken ct)
    {
        IsLoadingSchema = true;
        SchemaStatus = "Loading schema…";
        try
        {
            _schema = await _introspect(ct);
            RebuildFields();
            SchemaStatus = $"{_schema.Types.Count(t => !t.Key.StartsWith("__"))} types · click a field to build an operation";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SchemaStatus = $"Introspection failed: {ex.Message}";
        }
        finally
        {
            IsLoadingSchema = false;
        }
    }

    private void RebuildFields()
    {
        SchemaFields.Clear();
        if (_schema is null)
            return;
        void Add(string operation, GraphQlType? type)
        {
            foreach (var field in type?.Fields ?? [])
                if (SchemaFilter.Length == 0 || field.Name.Contains(SchemaFilter, StringComparison.OrdinalIgnoreCase))
                    SchemaFields.Add(new SchemaFieldItem(operation, field));
        }
        Add("query", _schema.Query);
        Add("mutation", _schema.Mutation);
        Add("subscription", _schema.Subscription);
    }

    private void UseField(SchemaFieldItem? item)
    {
        if (item is null || _schema is null)
            return;
        var (query, variables) = _schema.BuildOperation(item.Operation, item.Field);
        Query = query;
        Variables = variables;
        OperationName = "";
    }
}

public sealed class GrpcEditor : ModelEditor<GrpcSettings>
{
    private readonly Func<bool, CancellationToken, Task<ProtoSchema>> _loadSchema;
    private readonly Func<Task<IReadOnlyList<string>>> _pickProtoFiles;
    private readonly Func<Task<string?>> _pickFolder;
    private ProtoSchema? _schema;
    private string _schemaStatus = "Load the schema from server reflection or .proto files.";
    private bool _isLoadingSchema;

    public GrpcEditor(Func<bool, CancellationToken, Task<ProtoSchema>> loadSchema, Func<Task<IReadOnlyList<string>>> pickProtoFiles,
        Func<Task<string?>> pickFolder) : base(new GrpcSettings())
    {
        _loadSchema = loadSchema;
        _pickProtoFiles = pickProtoFiles;
        _pickFolder = pickFolder;
        LoadSchemaCommand = new AsyncRelayCommand(ct => LoadSchemaAsync(refresh: true, ct));
        GenerateTemplateCommand = new RelayCommand(GenerateTemplate, () => _schema is not null);
        AddProtoFilesCommand = new AsyncRelayCommand(AddProtoFilesAsync);
        RemoveProtoFileCommand = new RelayCommand<string>(p =>
        {
            if (p is null)
                return;
            ProtoFiles.Remove(p);
            Model.ProtoFiles = ProtoFiles.ToList();
            RaiseChanged();
        });
        AddImportPathCommand = new AsyncRelayCommand(async () =>
        {
            if (await _pickFolder() is { } folder && !ImportPaths.Contains(folder))
            {
                ImportPaths.Add(folder);
                Model.ImportPaths = ImportPaths.ToList();
                RaiseChanged();
            }
        });
        RemoveImportPathCommand = new RelayCommand<string>(p =>
        {
            if (p is null)
                return;
            ImportPaths.Remove(p);
            Model.ImportPaths = ImportPaths.ToList();
            RaiseChanged();
        });
    }

    public static IReadOnlyList<GrpcSchemaSource> Sources { get; } = Enum.GetValues<GrpcSchemaSource>();

    public GrpcSchemaSource SchemaSource
    {
        get => Model.SchemaSource;
        set
        {
            if (Set(Model.SchemaSource, value, (m, v) => m.SchemaSource = v))
                OnPropertyChanged(nameof(UsesProtoFiles));
        }
    }

    public bool UsesProtoFiles => SchemaSource == GrpcSchemaSource.ProtoFiles;

    public string Service
    {
        get => Model.Service;
        set
        {
            if (Set(Model.Service, value ?? "", (m, v) => m.Service = v))
                RefreshMethods();
        }
    }

    public string Method
    {
        get => Model.Method;
        set
        {
            if (Set(Model.Method, value ?? "", (m, v) => m.Method = v))
                OnPropertyChanged(nameof(MethodInfo));
        }
    }

    public string Message { get => Model.Message; set => Set(Model.Message, value, (m, v) => m.Message = v); }
    public bool UseTls { get => Model.UseTls; set => Set(Model.UseTls, value, (m, v) => m.UseTls = v); }
    public decimal DeadlineSeconds { get => (decimal)Model.DeadlineSeconds; set => Set(Model.DeadlineSeconds, (double)value, (m, v) => m.DeadlineSeconds = v); }

    public ObservableCollection<string> ProtoFiles { get; } = [];
    public ObservableCollection<string> ImportPaths { get; } = [];
    public ObservableCollection<string> Services { get; } = [];
    public ObservableCollection<string> Methods { get; } = [];

    public string SchemaStatus
    {
        get => _schemaStatus;
        private set => SetProperty(ref _schemaStatus, value);
    }

    public bool IsLoadingSchema
    {
        get => _isLoadingSchema;
        private set => SetProperty(ref _isLoadingSchema, value);
    }

    /// <summary>Call type and message types of the selected method.</summary>
    public string MethodInfo
    {
        get
        {
            if (_schema is null || Service.Length == 0 || Method.Length == 0)
                return "";
            try
            {
                var (_, m) = _schema.FindMethod(Service, Method);
                var hint = m.ClientStreaming ? " · send a JSON array of messages, or type them while connected" : "";
                return $"{m.Kind} · {m.InputType} → {m.OutputType}{hint}";
            }
            catch (KeyNotFoundException ex)
            {
                return ex.Message;
            }
        }
    }

    /// <summary>True for client / bidirectional streaming methods: the composer can send messages while the call is open.</summary>
    public bool IsClientStreaming
    {
        get
        {
            if (_schema is null)
                return false;
            try
            {
                return _schema.FindMethod(Service, Method).Method.ClientStreaming;
            }
            catch (KeyNotFoundException)
            {
                return false;
            }
        }
    }

    public IAsyncRelayCommand LoadSchemaCommand { get; }
    public IRelayCommand GenerateTemplateCommand { get; }
    public IAsyncRelayCommand AddProtoFilesCommand { get; }
    public IRelayCommand<string> RemoveProtoFileCommand { get; }
    public IAsyncRelayCommand AddImportPathCommand { get; }
    public IRelayCommand<string> RemoveImportPathCommand { get; }

    protected override void OnLoaded()
    {
        ProtoFiles.Clear();
        foreach (var f in Model.ProtoFiles)
            ProtoFiles.Add(f);
        ImportPaths.Clear();
        foreach (var p in Model.ImportPaths)
            ImportPaths.Add(p);
        // Keep the saved choice visible before the schema is loaded.
        Services.Clear();
        if (Model.Service.Length > 0)
            Services.Add(Model.Service);
        Methods.Clear();
        if (Model.Method.Length > 0)
            Methods.Add(Model.Method);
    }

    public async Task LoadSchemaAsync(bool refresh, CancellationToken ct)
    {
        IsLoadingSchema = true;
        SchemaStatus = "Loading schema…";
        try
        {
            _schema = await _loadSchema(refresh, ct);
            var selectedService = Model.Service;
            var selectedMethod = Model.Method;
            Services.Clear();
            foreach (var name in _schema.Services.Keys.Order())
                Services.Add(name);
            // Setting the collections may clear the ComboBox selection; restore it without marking dirty.
            Model.Service = Services.Contains(selectedService) ? selectedService : Services.FirstOrDefault() ?? "";
            OnPropertyChanged(nameof(Service));
            RefreshMethods(selectedMethod);
            SchemaStatus = $"{_schema.Services.Count} service(s), {_schema.Services.Values.Sum(s => s.Methods.Count)} method(s)";
            GenerateTemplateCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SchemaStatus = ex.Message;
        }
        finally
        {
            IsLoadingSchema = false;
        }
    }

    private void RefreshMethods(string? keep = null)
    {
        if (_schema is null)
            return;
        keep ??= Model.Method;
        Methods.Clear();
        if (_schema.Services.TryGetValue(Service, out var svc))
            foreach (var m in svc.Methods)
                Methods.Add(m.Name);
        Model.Method = Methods.Contains(keep) ? keep : Methods.FirstOrDefault() ?? "";
        OnPropertyChanged(nameof(Method));
        OnPropertyChanged(nameof(MethodInfo));
        OnPropertyChanged(nameof(IsClientStreaming));
    }

    private void GenerateTemplate()
    {
        if (_schema is null)
            return;
        try
        {
            var (_, method) = _schema.FindMethod(Service, Method);
            var template = new ProtoJson(_schema).Template(method.InputType);
            Message = method.ClientStreaming ? $"[\n{template}\n]" : template;
        }
        catch (KeyNotFoundException ex)
        {
            SchemaStatus = ex.Message;
        }
    }

    private async Task AddProtoFilesAsync()
    {
        foreach (var path in await _pickProtoFiles())
            if (!ProtoFiles.Contains(path))
                ProtoFiles.Add(path);
        Model.ProtoFiles = ProtoFiles.ToList();
        SchemaSource = GrpcSchemaSource.ProtoFiles;
        RaiseChanged();
    }
}

public sealed class SoapEditor : ModelEditor<SoapSettings>
{
    private readonly Func<string, CancellationToken, Task<WsdlDocument>> _loadWsdl;
    private readonly Action<SoapOperation, string> _useOperation;
    private WsdlDocument? _wsdl;
    private SoapOperation? _selectedOperation;
    private string _status = "";

    public SoapEditor(Func<string, CancellationToken, Task<WsdlDocument>> loadWsdl, Action<SoapOperation, string> useOperation)
        : base(new SoapSettings())
    {
        _loadWsdl = loadWsdl;
        _useOperation = useOperation;
        LoadWsdlCommand = new AsyncRelayCommand(LoadWsdlAsync);
        GenerateEnvelopeCommand = new RelayCommand(() =>
        {
            if (_wsdl is not null && SelectedOperation is not null)
                _useOperation(SelectedOperation, _wsdl.BuildEnvelope(SelectedOperation));
        }, () => SelectedOperation is not null);
    }

    public static IReadOnlyList<SoapVersion> Versions { get; } = Enum.GetValues<SoapVersion>();
    public static IReadOnlyList<WsSecurityMode> SecurityModes { get; } = Enum.GetValues<WsSecurityMode>();

    public string WsdlUrl { get => Model.WsdlUrl; set => Set(Model.WsdlUrl, value, (m, v) => m.WsdlUrl = v); }
    public SoapVersion Version { get => Model.Version; set => Set(Model.Version, value, (m, v) => m.Version = v); }
    public string Action { get => Model.Action; set => Set(Model.Action, value, (m, v) => m.Action = v); }
    public string Operation { get => Model.Operation; set => Set(Model.Operation, value, (m, v) => m.Operation = v); }

    public WsSecurityMode WsSecurity
    {
        get => Model.WsSecurity;
        set
        {
            if (Set(Model.WsSecurity, value, (m, v) => m.WsSecurity = v))
                OnPropertyChanged(nameof(UsesUsernameToken));
        }
    }

    public bool UsesUsernameToken => WsSecurity != WsSecurityMode.None;
    public string WsUsername { get => Model.WsUsername; set => Set(Model.WsUsername, value, (m, v) => m.WsUsername = v); }
    public string WsPassword { get => Model.WsPassword; set => Set(Model.WsPassword, value, (m, v) => m.WsPassword = v); }
    public bool AddTimestamp { get => Model.AddTimestamp; set => Set(Model.AddTimestamp, value, (m, v) => m.AddTimestamp = v); }

    public ObservableCollection<SoapOperation> Operations { get; } = [];

    public SoapOperation? SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            if (!SetProperty(ref _selectedOperation, value) || value is null)
                return;
            Version = value.Version;
            Action = value.SoapAction;
            Operation = value.Name;
            GenerateEnvelopeCommand.NotifyCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public IAsyncRelayCommand LoadWsdlCommand { get; }
    public IRelayCommand GenerateEnvelopeCommand { get; }

    private async Task LoadWsdlAsync(CancellationToken ct)
    {
        Status = "Loading WSDL…";
        try
        {
            _wsdl = await _loadWsdl(WsdlUrl, ct);
            Operations.Clear();
            foreach (var op in _wsdl.Operations)
                Operations.Add(op);
            _selectedOperation = Operations.FirstOrDefault(o => o.Name == Operation && o.Version == Version);
            OnPropertyChanged(nameof(SelectedOperation));
            Status = $"{Operations.Count} operation(s). Pick one and click Generate to create the envelope.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"Could not load the WSDL: {ex.Message}";
        }
    }
}
