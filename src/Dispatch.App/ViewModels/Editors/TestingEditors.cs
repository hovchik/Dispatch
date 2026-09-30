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
    [NotifyPropertyChangedFor(nameof(NeedsPath), nameof(NeedsExpected), nameof(PathWatermark), nameof(IsSchema))]
    private ValueSource _source;

    [ObservableProperty] private string _path = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsExpected))]
    private AssertionOperator _operator;

    [ObservableProperty] private string _expected = "";

    /// <summary>Latest outcome after a send (null before the first run).</summary>
    [ObservableProperty] private bool? _passed;

    public bool NeedsPath => Source is ValueSource.Header or ValueSource.JsonPath or ValueSource.XPath or ValueSource.Regex
        or ValueSource.JsonSchema or ValueSource.Contract;

    public bool IsSchema => Source is ValueSource.JsonSchema or ValueSource.Contract;

    public bool NeedsExpected => !IsSchema && Operator is not (AssertionOperator.Exists or AssertionOperator.NotExists
        or AssertionOperator.IsEmpty or AssertionOperator.IsNotEmpty or AssertionOperator.IsValid);

    public string PathWatermark => Source switch
    {
        ValueSource.Header => "Header name",
        ValueSource.JsonPath => "$.data.id",
        ValueSource.XPath => "//soap:Body/*[1]",
        ValueSource.Regex => "regex, first group is used",
        ValueSource.JsonSchema => "Inline JSON schema or path to a .json file",
        ValueSource.Contract => "OpenAPI file/URL (default: the collection's spec)",
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
        Headers.Changed += (_, _) => OnPropertyChanged(nameof(Headers));
        MatchQuery.Changed += (_, _) => OnPropertyChanged(nameof(MatchQuery));
        MatchHeaders.Changed += (_, _) => OnPropertyChanged(nameof(MatchHeaders));
    }

    public Guid Id { get; }
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
        MatchBodyContains = MatchBodyContains
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
