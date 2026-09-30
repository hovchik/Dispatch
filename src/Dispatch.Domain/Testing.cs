namespace Dispatch.Domain;

/// <summary>Where an assertion or extraction reads its value from.</summary>
public enum ValueSource
{
    Status,
    Header,
    JsonPath,
    XPath,
    Regex,
    Body,
    ResponseTime,
    Size,
    MessageCount,
    JsonSchema,
    Contract
}

public enum AssertionOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    Exists,
    NotExists,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    Matches,
    IsType,
    LengthEquals,
    IsEmpty,
    IsNotEmpty,
    OneOf,
    IsValid
}

/// <summary>A no-code check on a response: <c>source(path) operator expected</c>.</summary>
public sealed class Assertion
{
    public bool Enabled { get; set; } = true;
    public ValueSource Source { get; set; } = ValueSource.Status;

    /// <summary>Header name, JSONPath, XPath, regex, or (for JsonSchema / Contract) the schema or spec location.</summary>
    public string Path { get; set; } = string.Empty;
    public AssertionOperator Operator { get; set; } = AssertionOperator.Equals;
    public string Expected { get; set; } = string.Empty;

    public Assertion Clone() => (Assertion)MemberwiseClone();
}

public enum VariableScope
{
    /// <summary>Written to the active environment (persisted).</summary>
    Environment,

    /// <summary>Kept in memory for this session / run only.</summary>
    Runtime
}

/// <summary>Copies a value out of a response into a variable, e.g. a login token for later requests.</summary>
public sealed class ExtractionRule
{
    public bool Enabled { get; set; } = true;
    public string Variable { get; set; } = string.Empty;
    public ValueSource Source { get; set; } = ValueSource.JsonPath;
    public string Path { get; set; } = string.Empty;
    public VariableScope Scope { get; set; } = VariableScope.Environment;

    public ExtractionRule Clone() => (ExtractionRule)MemberwiseClone();
}

/// <summary>A saved response, used as documentation and served by the mock server.</summary>
public sealed class ResponseExample
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Example";
    public int StatusCode { get; set; } = 200;
    public string ContentType { get; set; } = "application/json";
    public List<KeyValueItem> Headers { get; set; } = [];
    public string Body { get; set; } = string.Empty;

    /// <summary>Mock matching: only serve this example when every enabled rule matches (e.g. query "id" = "7").</summary>
    public List<KeyValueItem> MatchQuery { get; set; } = [];
    public List<KeyValueItem> MatchHeaders { get; set; } = [];
    public string MatchBodyContains { get; set; } = string.Empty;

    public ResponseExample Clone() => DeepCopy.Of(this);
}

/// <summary>The outcome of one assertion or script test.</summary>
public sealed record TestResult(string Name, bool Passed, string? Message = null, string? Actual = null);
