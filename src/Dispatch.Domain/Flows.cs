namespace Dispatch.Domain;

/// <summary>The kind of a <see cref="FlowStep"/>.</summary>
public enum FlowStepType
{
    /// <summary>Send a request from the collection and run its assertions.</summary>
    Request,

    /// <summary>Run a JavaScript snippet (same pm API as test scripts) against the shared variables.</summary>
    Script,

    /// <summary>Set a variable to a value (variables and {{$dynamic}} allowed).</summary>
    SetVariable,

    /// <summary>Pause for a fixed time.</summary>
    Delay,

    /// <summary>Run the child steps only when a condition holds.</summary>
    If,

    /// <summary>Repeat the child steps a fixed number of times ({{$index}} is set each pass).</summary>
    Repeat,

    /// <summary>Repeat the child steps once per item of a JSON array ({{$item}}, {{$index}} are set).</summary>
    ForEach,

    /// <summary>Repeat the child steps until a condition holds or a maximum is reached (with a delay between tries).</summary>
    Until,

    /// <summary>Stop the flow (optionally only when a condition holds), passing or failing it.</summary>
    Stop,

    /// <summary>A labelled group of steps, for organisation.</summary>
    Group
}

public enum FlowComparison
{
    Equals,
    NotEquals,
    Contains,
    GreaterThan,
    LessThan,
    Matches,
    IsTruthy,
    IsEmpty
}

/// <summary>A condition <c>left op right</c> used by If / Until / Stop steps. Both sides may use <c>{{variables}}</c>.</summary>
public sealed class FlowCondition
{
    public string Left { get; set; } = string.Empty;
    public FlowComparison Comparison { get; set; } = FlowComparison.Equals;
    public string Right { get; set; } = string.Empty;

    public FlowCondition Clone() => (FlowCondition)MemberwiseClone();
}

/// <summary>One node in a test flow. Container steps (If, Repeat, ForEach, Until, Group) hold <see cref="Children"/>.</summary>
public sealed class FlowStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public FlowStepType Type { get; set; } = FlowStepType.Request;
    public bool Enabled { get; set; } = true;

    /// <summary>A human label; defaults are derived when empty.</summary>
    public string Name { get; set; } = string.Empty;

    // Request
    public Guid? RequestId { get; set; }

    /// <summary>Variable to read a value into or the target of SetVariable.</summary>
    public string Variable { get; set; } = string.Empty;

    /// <summary>SetVariable value, ForEach source array (JSON or a {{var}}), Script body, or Stop message.</summary>
    public string Value { get; set; } = string.Empty;
    public VariableScope Scope { get; set; } = VariableScope.Runtime;

    // Delay / Repeat / Until
    public int Milliseconds { get; set; } = 1000;
    public int Count { get; set; } = 3;
    public int MaxAttempts { get; set; } = 10;

    // If / Until / Stop
    public FlowCondition Condition { get; set; } = new();

    /// <summary>Stop step: whether reaching it fails the flow.</summary>
    public bool Fail { get; set; }

    /// <summary>If the step (typically a request) errors or its tests fail, stop the whole flow.</summary>
    public bool ContinueOnError { get; set; }

    public List<FlowStep> Children { get; set; } = [];

    public FlowStep Clone() => new()
    {
        Id = Id,
        Type = Type,
        Enabled = Enabled,
        Name = Name,
        RequestId = RequestId,
        Variable = Variable,
        Value = Value,
        Scope = Scope,
        Milliseconds = Milliseconds,
        Count = Count,
        MaxAttempts = MaxAttempts,
        Condition = Condition.Clone(),
        Fail = Fail,
        ContinueOnError = ContinueOnError,
        Children = Children.Select(c => c.Clone()).ToList()
    };
}

/// <summary>A saved, ordered sequence of steps that runs requests with control flow between them.</summary>
public sealed class TestFlow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Flow";
    public string Description { get; set; } = string.Empty;
    public Guid? CollectionId { get; set; }
    public List<FlowStep> Steps { get; set; } = [];
    public int SortOrder { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public TestFlow Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        CollectionId = CollectionId,
        Steps = Steps.Select(s => s.Clone()).ToList(),
        SortOrder = SortOrder,
        UpdatedAt = UpdatedAt
    };
}
