using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Flows;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>A choosable request for the Request step's dropdown.</summary>
public sealed record FlowRequestChoice(Guid Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One editable step node in the flow builder tree.</summary>
public sealed partial class FlowStepViewModel : ObservableObject
{
    private readonly FlowBuilderViewModel _builder;

    public FlowStepViewModel(FlowBuilderViewModel builder, FlowStep model)
    {
        _builder = builder;
        Model = model;
        foreach (var child in model.Children)
            Children.Add(new FlowStepViewModel(builder, child));
    }

    public FlowStep Model { get; }
    public ObservableCollection<FlowStepViewModel> Children { get; } = [];

    public FlowStepType Type => Model.Type;
    public bool IsContainer => Type is FlowStepType.If or FlowStepType.Repeat or FlowStepType.ForEach or FlowStepType.Until or FlowStepType.Group;
    public bool IsRequest => Type == FlowStepType.Request;
    public bool IsScript => Type == FlowStepType.Script;
    public bool IsSetVariable => Type == FlowStepType.SetVariable;
    public bool IsDelay => Type == FlowStepType.Delay;
    public bool IsRepeat => Type == FlowStepType.Repeat;
    public bool IsForEach => Type == FlowStepType.ForEach;
    public bool IsUntil => Type == FlowStepType.Until;
    public bool IsStop => Type == FlowStepType.Stop;
    public bool HasCondition => Type is FlowStepType.If or FlowStepType.Until or FlowStepType.Stop;
    public bool ShowContinueOnError => Type is FlowStepType.Request or FlowStepType.Script or FlowStepType.ForEach;
    public bool ShowDelay => Type is FlowStepType.Delay or FlowStepType.Until;

    public string TypeLabel => Type switch
    {
        FlowStepType.Request => "Request",
        FlowStepType.SetVariable => "Set variable",
        FlowStepType.ForEach => "For each",
        _ => Type.ToString()
    };

    public static IReadOnlyList<FlowComparison> Comparisons { get; } = Enum.GetValues<FlowComparison>();
    public static IReadOnlyList<VariableScope> Scopes { get; } = Enum.GetValues<VariableScope>();
    public IReadOnlyList<FlowRequestChoice> RequestChoices => _builder.RequestChoices;

    public bool Enabled
    {
        get => Model.Enabled;
        set { Model.Enabled = value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public string Name
    {
        get => Model.Name;
        set { Model.Name = value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public FlowRequestChoice? SelectedRequest
    {
        get => RequestChoices.FirstOrDefault(c => c.Id == Model.RequestId);
        set { Model.RequestId = value?.Id; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public string Variable
    {
        get => Model.Variable;
        set { Model.Variable = value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public string Value
    {
        get => Model.Value;
        set { Model.Value = value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public VariableScope Scope
    {
        get => Model.Scope;
        set { Model.Scope = value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public decimal Milliseconds
    {
        get => Model.Milliseconds;
        set { Model.Milliseconds = (int)value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public decimal Count
    {
        get => Model.Count;
        set { Model.Count = (int)value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public decimal MaxAttempts
    {
        get => Model.MaxAttempts;
        set { Model.MaxAttempts = (int)value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public bool Fail
    {
        get => Model.Fail;
        set { Model.Fail = value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public bool ContinueOnError
    {
        get => Model.ContinueOnError;
        set { Model.ContinueOnError = value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public string ConditionLeft
    {
        get => Model.Condition.Left;
        set { Model.Condition.Left = value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public FlowComparison ConditionComparison
    {
        get => Model.Condition.Comparison;
        set { Model.Condition.Comparison = value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    public string ConditionRight
    {
        get => Model.Condition.Right;
        set { Model.Condition.Right = value; OnPropertyChanged(); _builder.MarkDirty(); }
    }

    [RelayCommand] private void Remove() => _builder.Remove(this);
    [RelayCommand] private void MoveUp() => _builder.Move(this, -1);
    [RelayCommand] private void MoveDown() => _builder.Move(this, +1);
    [RelayCommand] private void AddChild(string? type) => _builder.AddStep(this, ParseType(type));

    internal static FlowStepType ParseType(string? type) => Enum.TryParse<FlowStepType>(type, out var t) ? t : FlowStepType.Request;
}

/// <summary>The visual flow builder: edit the step tree, save it, and run it with a live log.</summary>
public sealed partial class FlowBuilderViewModel : ObservableObject, ITool
{
    private readonly RequestCollection _collection;
    private readonly TestFlow _flow;
    private readonly IFlowRepository _repository;
    private readonly FlowRunner _runner;
    private readonly Func<ApiEnvironment?> _environment;
    private CancellationTokenSource? _cts;

    public FlowBuilderViewModel(RequestCollection collection, TestFlow flow, IFlowRepository repository, FlowRunner runner,
        Func<ApiEnvironment?> environment)
    {
        _collection = collection;
        _flow = flow;
        _repository = repository;
        _runner = runner;
        _environment = environment;
        Name = flow.Name;
        RequestChoices = collection.Requests.OrderBy(r => r.Folder).ThenBy(r => r.SortOrder)
            .Select(r => new FlowRequestChoice(r.Id, r.Folder.Length > 0 ? $"{r.Folder}/{r.Name}" : r.Name)).ToList();
        foreach (var step in flow.Steps)
            Steps.Add(new FlowStepViewModel(this, step));
    }

    public string Title => $"Flow · {Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.Flows;
    public double Width => 1080;
    public double Height => 760;

    public IReadOnlyList<FlowRequestChoice> RequestChoices { get; }
    public ObservableCollection<FlowStepViewModel> Steps { get; } = [];
    public ObservableCollection<FlowEvent> Log { get; } = [];

    public static IReadOnlyList<FlowStepType> StepTypes { get; } = Enum.GetValues<FlowStepType>();

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _status = "Add steps, then Run. Steps share one set of variables, so values extracted by one request feed the next.";

    partial void OnNameChanged(string value)
    {
        _flow.Name = value;
        MarkDirty();
        OnPropertyChanged(nameof(Title));
    }

    public void MarkDirty() => IsDirty = true;

    [RelayCommand]
    private void AddStep(string? type) => AddStep(null, FlowStepViewModel.ParseType(type));

    public void AddStep(FlowStepViewModel? parent, FlowStepType type)
    {
        var step = new FlowStep { Type = type };
        if (type == FlowStepType.Until)
            step.Milliseconds = 1000;
        var vm = new FlowStepViewModel(this, step);
        if (parent is not null)
        {
            parent.Model.Children.Add(step);
            parent.Children.Add(vm);
        }
        else
        {
            _flow.Steps.Add(step);
            Steps.Add(vm);
        }
        MarkDirty();
    }

    public void Remove(FlowStepViewModel step)
    {
        if (RemoveFrom(Steps, _flow.Steps, step))
        {
            MarkDirty();
            return;
        }
        foreach (var parent in Flatten(Steps))
            if (RemoveFrom(parent.Children, parent.Model.Children, step))
            {
                MarkDirty();
                return;
            }
    }

    public void Move(FlowStepViewModel step, int delta)
    {
        if (MoveIn(Steps, _flow.Steps, step, delta))
        {
            MarkDirty();
            return;
        }
        foreach (var parent in Flatten(Steps))
            if (MoveIn(parent.Children, parent.Model.Children, step, delta))
            {
                MarkDirty();
                return;
            }
    }

    private static bool RemoveFrom(ObservableCollection<FlowStepViewModel> vms, List<FlowStep> models, FlowStepViewModel step)
    {
        var index = vms.IndexOf(step);
        if (index < 0)
            return false;
        vms.RemoveAt(index);
        models.RemoveAt(index);
        return true;
    }

    private static bool MoveIn(ObservableCollection<FlowStepViewModel> vms, List<FlowStep> models, FlowStepViewModel step, int delta)
    {
        var index = vms.IndexOf(step);
        if (index < 0)
            return false;
        var target = index + delta;
        if (target < 0 || target >= vms.Count)
            return true;
        vms.Move(index, target);
        (models[index], models[target]) = (models[target], models[index]);
        return true;
    }

    private static IEnumerable<FlowStepViewModel> Flatten(IEnumerable<FlowStepViewModel> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in Flatten(step.Children))
                yield return child;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await _repository.SaveAsync(_flow);
        IsDirty = false;
        Status = $"Saved \"{_flow.Name}\".";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        Log.Clear();
        IsRunning = true;
        _cts = new CancellationTokenSource();
        Status = "Running…";
        var progress = new Progress<FlowEvent>(Log.Add);
        try
        {
            var result = await _runner.RunAsync(_flow, new FlowRunOptions
            {
                Requests = _collection.Requests,
                Environment = _environment(),
                CollectionVariables = _collection.Variables,
                CollectionSpec = _collection.SpecLocation
            }, progress, _cts.Token);
            Status = (result.Passed ? "✓ Passed" : "✗ Failed") +
                     $" · {result.StepsRun} step(s), {result.RequestsSent} request(s), {result.TestsPassed}/{result.TestsPassed + result.TestsFailed} tests · " +
                     $"{result.Duration.TotalSeconds:0.00} s" + (result.StopReason is { } r ? $" · {r}" : "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    public void OnClosed() => _cts?.Cancel();
}

/// <summary>Lists the flows of a collection and opens or creates one in the builder.</summary>
public sealed partial class FlowManagerViewModel : ObservableObject, ITool
{
    private readonly RequestCollection _collection;
    private readonly IFlowRepository _repository;
    private readonly FlowRunner _runner;
    private readonly IDialogService _dialogs;
    private readonly Func<ApiEnvironment?> _environment;

    public FlowManagerViewModel(RequestCollection collection, IFlowRepository repository, FlowRunner runner, IDialogService dialogs,
        Func<ApiEnvironment?> environment)
    {
        _collection = collection;
        _repository = repository;
        _runner = runner;
        _dialogs = dialogs;
        _environment = environment;
        _ = LoadAsync();
    }

    public string Title => $"Flows · {_collection.Name}";
    public string? HelpTopic => Dispatch.Application.Help.HelpCatalog.Flows;
    public double Width => 560;
    public double Height => 520;

    public ObservableCollection<TestFlow> Flows { get; } = [];
    [ObservableProperty] private string _hint = "Loading…";

    private async Task LoadAsync()
    {
        Flows.Clear();
        foreach (var flow in (await _repository.GetAllAsync()).Where(f => f.CollectionId == _collection.Id))
            Flows.Add(flow);
        Hint = Flows.Count == 0 ? "No flows yet. Create one to chain requests with conditions, loops and waits." : $"{Flows.Count} flow(s).";
    }

    [RelayCommand]
    private void New()
    {
        var flow = new TestFlow { Name = "New Flow", CollectionId = _collection.Id };
        Open(flow);
        _ = LoadAsync();
    }

    [RelayCommand]
    private void Open(TestFlow? flow)
    {
        if (flow is null)
            return;
        _dialogs.ShowTool(new FlowBuilderViewModel(_collection, flow.Clone(), _repository, _runner, _environment));
    }

    [RelayCommand]
    private async Task DeleteAsync(TestFlow? flow)
    {
        if (flow is null)
            return;
        await _repository.DeleteAsync(flow.Id);
        await LoadAsync();
    }
}
