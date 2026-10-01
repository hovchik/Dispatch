using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Editors;

/// <summary>An editable list whose rows raise <see cref="Changed"/> on any edit.</summary>
public abstract partial class RowListEditor<TRow, TModel> : ObservableObject where TRow : ObservableObject
{
    private bool _loading;

    public ObservableCollection<TRow> Rows { get; } = [];
    public event EventHandler? Changed;

    public int Count => Rows.Count;
    public bool HasRows => Rows.Count > 0;

    protected abstract TRow CreateRow(TModel model);
    protected abstract TModel ToModel(TRow row);
    protected abstract TModel NewModel();

    public void Load(IEnumerable<TModel> items)
    {
        _loading = true;
        try
        {
            foreach (var row in Rows)
                row.PropertyChanged -= OnRowChanged;
            Rows.Clear();
            foreach (var item in items)
                AddRow(CreateRow(item));
        }
        finally
        {
            _loading = false;
        }
        RefreshCounts();
    }

    public List<TModel> ToModels() => Rows.Select(ToModel).ToList();

    [RelayCommand]
    private void Add() => AddRow(CreateRow(NewModel()));

    public void AddModel(TModel model) => AddRow(CreateRow(model));

    [RelayCommand]
    private void Remove(TRow? row)
    {
        if (row is null)
            return;
        row.PropertyChanged -= OnRowChanged;
        Rows.Remove(row);
        Raise();
    }

    private void AddRow(TRow row)
    {
        row.PropertyChanged += OnRowChanged;
        Rows.Add(row);
        Raise();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Raise();

    private void Raise()
    {
        RefreshCounts();
        if (!_loading)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasRows));
    }
}

public sealed partial class AssertionRow : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPath), nameof(NeedsExpected), nameof(PathWatermark), nameof(IsSchema), nameof(IsSnapshot),
        nameof(ShowOperator), nameof(ShowExpectedBox))]
    private ValueSource _source;

    [ObservableProperty] private string _path = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsExpected))]
    private AssertionOperator _operator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SnapshotSummary))]
    private string _expected = "";

    /// <summary>Latest outcome after a send (null before the first run).</summary>
    [ObservableProperty] private bool? _passed;

    public bool NeedsPath => Source is ValueSource.Header or ValueSource.JsonPath or ValueSource.XPath or ValueSource.Regex
        or ValueSource.JsonSchema or ValueSource.Contract or ValueSource.Snapshot;

    public bool IsSnapshot => Source == ValueSource.Snapshot;
    public bool ShowOperator => !IsSchema && !IsSnapshot;
    public bool ShowExpectedBox => !IsSnapshot;

    public string SnapshotSummary => Expected.Length == 0
        ? "Not recorded yet: the next response becomes the snapshot"
        : $"Snapshot: {Expected.Split('\n').Length} line(s), {Expected.Length:N0} chars";

    /// <summary>Clears the stored snapshot so the next response is recorded again.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetSnapshot() => Expected = "";

    public bool IsSchema => Source is ValueSource.JsonSchema or ValueSource.Contract;

    public bool NeedsExpected => !IsSchema && !IsSnapshot && Operator is not (AssertionOperator.Exists or AssertionOperator.NotExists
        or AssertionOperator.IsEmpty or AssertionOperator.IsNotEmpty or AssertionOperator.IsValid);

    public string PathWatermark => Source switch
    {
        ValueSource.Header => "Header name",
        ValueSource.JsonPath => "$.data.id",
        ValueSource.XPath => "//soap:Body/*[1]",
        ValueSource.Regex => "regex, first group is used",
        ValueSource.JsonSchema => "Inline JSON schema or path to a .json file",
        ValueSource.Contract => "OpenAPI file/URL (default: the collection's spec)",
        ValueSource.Snapshot => "Ignore paths: $.createdAt, $..id",
        _ => ""
    };
}

public sealed class AssertionsEditor : RowListEditor<AssertionRow, Assertion>
{
    public static IReadOnlyList<ValueSource> Sources { get; } = Enum.GetValues<ValueSource>();
    public static IReadOnlyList<AssertionOperator> Operators { get; } = Enum.GetValues<AssertionOperator>();

    protected override AssertionRow CreateRow(Assertion a) => new()
    {
        Enabled = a.Enabled, Source = a.Source, Path = a.Path, Operator = a.Operator, Expected = a.Expected
    };

    protected override Assertion ToModel(AssertionRow r) => new()
    {
        Enabled = r.Enabled, Source = r.Source, Path = r.Path, Operator = r.Operator, Expected = r.Expected
    };

    protected override Assertion NewModel() => new() { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "200" };

    /// <summary>Stores snapshots recorded during a send on the matching rows (by position).</summary>
    public void ApplySnapshots(IReadOnlyDictionary<int, string> snapshots)
    {
        foreach (var (index, snapshot) in snapshots)
            if (index < Rows.Count && Rows[index].IsSnapshot)
                Rows[index].Expected = snapshot;
    }

    /// <summary>Marks rows with the latest results (matched by description).</summary>
    public void ShowResults(IReadOnlyList<TestResult> results)
    {
        foreach (var row in Rows)
        {
            var name = Application.Testing.AssertionEvaluator.Describe(ToModel(row));
            row.Passed = row.Enabled ? results.FirstOrDefault(r => r.Name == name)?.Passed : null;
        }
    }
}

public sealed partial class ExtractionRow : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _variable = "";
    [ObservableProperty] private ValueSource _source = ValueSource.JsonPath;
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private VariableScope _scope = VariableScope.Environment;
}

public sealed class ExtractionsEditor : RowListEditor<ExtractionRow, ExtractionRule>
{
    public static IReadOnlyList<ValueSource> Sources { get; } =
        [ValueSource.JsonPath, ValueSource.Header, ValueSource.XPath, ValueSource.Regex, ValueSource.Body, ValueSource.Status];

    public static IReadOnlyList<VariableScope> Scopes { get; } = Enum.GetValues<VariableScope>();

    protected override ExtractionRow CreateRow(ExtractionRule e) => new()
    {
        Enabled = e.Enabled, Variable = e.Variable, Source = e.Source, Path = e.Path, Scope = e.Scope
    };

    protected override ExtractionRule ToModel(ExtractionRow r) => new()
    {
        Enabled = r.Enabled, Variable = r.Variable.Trim(), Source = r.Source, Path = r.Path, Scope = r.Scope
    };

    protected override ExtractionRule NewModel() => new() { Source = ValueSource.JsonPath, Path = "$." };
}

public sealed partial class ExampleRow : ObservableObject
{
    public ExampleRow(ResponseExample model)
    {
        Id = model.Id;
        _name = model.Name;
        _statusCode = model.StatusCode;
        _contentType = model.ContentType;
        _body = model.Body;
        _matchBodyContains = model.MatchBodyContains;
        Headers = new KeyValueListViewModel("Header", "Value");
        Headers.Load(model.Headers);
        MatchQuery = new KeyValueListViewModel("Query param", "Equals");
        MatchQuery.Load(model.MatchQuery);
        MatchHeaders = new KeyValueListViewModel("Header", "Equals");
        MatchHeaders.Load(model.MatchHeaders);
        _session = model.Session;
        Headers.Changed += (_, _) => OnPropertyChanged(nameof(Headers));
        MatchQuery.Changed += (_, _) => OnPropertyChanged(nameof(MatchQuery));
        MatchHeaders.Changed += (_, _) => OnPropertyChanged(nameof(MatchHeaders));
    }

    public Guid Id { get; }

    /// <summary>A recorded streaming session; not edited here, but kept when the example is saved.</summary>
    private readonly List<SessionMessage> _session;
    public bool HasSession => _session.Count > 0;
    public string SessionSummary => $"Recorded session: {_session.Count(m => m.Direction == MessageDirection.Received)} server message(s), " +
                                    $"{_session.Count(m => m.Direction == MessageDirection.Sent)} client message(s) over " +
                                    $"{(_session.Count == 0 ? 0 : _session[^1].AtMs / 1000.0):0.#} s. The mock server replays it on this request's path.";
    [ObservableProperty] private string _name;
    [ObservableProperty] private decimal _statusCode;
    [ObservableProperty] private string _contentType;
    [ObservableProperty] private string _body;
    [ObservableProperty] private string _matchBodyContains;
    public KeyValueListViewModel Headers { get; }
    public KeyValueListViewModel MatchQuery { get; }
    public KeyValueListViewModel MatchHeaders { get; }

    public ResponseExample ToModel() => new()
    {
        Id = Id,
        Name = Name,
        StatusCode = (int)StatusCode,
        ContentType = ContentType,
        Body = Body,
        Headers = Headers.ToItems(),
        MatchQuery = MatchQuery.ToItems(),
        MatchHeaders = MatchHeaders.ToItems(),
        MatchBodyContains = MatchBodyContains,
        Session = _session.Select(m => new SessionMessage { AtMs = m.AtMs, Direction = m.Direction, Content = m.Content, Label = m.Label }).ToList()
    };
}

public sealed partial class ExamplesEditor : RowListEditor<ExampleRow, ResponseExample>
{
    [ObservableProperty] private ExampleRow? _selected;

    protected override ExampleRow CreateRow(ResponseExample model) => new(model);
    protected override ResponseExample ToModel(ExampleRow row) => row.ToModel();
    protected override ResponseExample NewModel() => new() { Name = $"Example {Rows.Count + 1}", Body = "{}" };

    public void AddAndSelect(ResponseExample example)
    {
        AddModel(example);
        Selected = Rows[^1];
    }
}

/// <summary>A streaming request in the collection that can serve as a message listener.</summary>
public sealed record ListenerOption(Guid Id, string Name, string Kind)
{
    public override string ToString() => Kind.Length == 0 ? Name : $"{Name} · {Kind}";
}

public sealed partial class ExpectationRow : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private ListenerOption? _listener;
    [ObservableProperty] private string _path = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsExpected))]
    private AssertionOperator _operator = AssertionOperator.Equals;

    [ObservableProperty] private string _expected = "";
    [ObservableProperty] private string _channel = "";
    [ObservableProperty] private decimal _timeoutMs = 5000;
    [ObservableProperty] private bool _expectNone;
    [ObservableProperty] private bool? _passed;

    /// <summary>Count and value source aren't edited in the row, but are kept as loaded.</summary>
    public int MinCount { get; set; } = 1;
    public ValueSource Source { get; set; } = ValueSource.JsonPath;

    public bool NeedsExpected => Operator is not (AssertionOperator.Exists or AssertionOperator.NotExists
        or AssertionOperator.IsEmpty or AssertionOperator.IsNotEmpty or AssertionOperator.IsValid);
}

/// <summary>Message expectations: messages this request must (or must not) cause on Kafka, MQTT, AMQP, WebSocket, SSE or Socket.IO.</summary>
public sealed class ExpectationsEditor : RowListEditor<ExpectationRow, MessageExpectation>
{
    private static readonly HashSet<RequestKind> ListenerKinds =
        [RequestKind.Mqtt, RequestKind.Kafka, RequestKind.Amqp, RequestKind.WebSocket, RequestKind.Sse, RequestKind.SocketIo];

    public static IReadOnlyList<AssertionOperator> Operators { get; } =
        Enum.GetValues<AssertionOperator>().Where(o => o is not AssertionOperator.IsValid).ToList();

    /// <summary>Streaming requests of the owning collection.</summary>
    public ObservableCollection<ListenerOption> Listeners { get; } = [];
    public bool HasListeners => Listeners.Count > 0;

    /// <summary>Refreshes the listener choices from the collection, keeping each row's selection (by id).</summary>
    public void SetListeners(IEnumerable<ApiRequest> collectionRequests, Guid self)
    {
        var options = collectionRequests.Where(r => r.Id != self && ListenerKinds.Contains(r.Kind))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new ListenerOption(r.Id, r.Name, KindLabel(r.Kind))).ToList();
        // Keep references that are not (or no longer) in the collection, so nothing is lost silently.
        foreach (var row in Rows)
            if (row.Listener is { } current && options.All(o => o.Id != current.Id))
                options.Add(current with { Name = current.Name.EndsWith("(missing)") ? current.Name : current.Name + " (missing)", Kind = "" });
        Listeners.Clear();
        foreach (var option in options)
            Listeners.Add(option);
        foreach (var row in Rows)
            if (row.Listener is { } current)
                row.Listener = Listeners.First(o => o.Id == current.Id);
        OnPropertyChanged(nameof(HasListeners));
    }

    private static string KindLabel(RequestKind kind) => kind switch
    {
        RequestKind.Mqtt => "MQTT",
        RequestKind.Amqp => "AMQP",
        RequestKind.Sse => "SSE",
        RequestKind.SocketIo => "Socket.IO",
        _ => kind.ToString()
    };

    protected override ExpectationRow CreateRow(MessageExpectation e) => new()
    {
        Enabled = e.Enabled,
        Listener = Listeners.FirstOrDefault(o => o.Id == e.ListenerId)
                   ?? (e.ListenerId == Guid.Empty ? null : new ListenerOption(e.ListenerId, "(missing)", "")),
        Path = e.Path, Operator = e.Operator, Expected = e.Expected, Channel = e.Channel, TimeoutMs = e.TimeoutMs,
        ExpectNone = e.ExpectNone, MinCount = e.MinCount, Source = e.Source
    };

    protected override MessageExpectation ToModel(ExpectationRow r) => new()
    {
        Enabled = r.Enabled, ListenerId = r.Listener?.Id ?? Guid.Empty, Path = r.Path, Operator = r.Operator, Expected = r.Expected,
        Channel = r.Channel.Trim(), TimeoutMs = (int)Math.Max(0, r.TimeoutMs), ExpectNone = r.ExpectNone, MinCount = r.MinCount, Source = r.Source
    };

    protected override MessageExpectation NewModel() => new()
    {
        ListenerId = Listeners.FirstOrDefault()?.Id ?? Guid.Empty, Path = "$.", Operator = AssertionOperator.Equals
    };

    /// <summary>Expectation results come last in a response's test results, one per enabled row, in order.</summary>
    public void ShowResults(IReadOnlyList<TestResult> results)
    {
        var enabled = Rows.Where(r => r.Enabled).ToList();
        var offset = results.Count - enabled.Count;
        foreach (var row in Rows)
            row.Passed = null;
        if (offset < 0)
            return;
        for (var i = 0; i < enabled.Count; i++)
            enabled[i].Passed = results[offset + i].Passed;
    }
}
