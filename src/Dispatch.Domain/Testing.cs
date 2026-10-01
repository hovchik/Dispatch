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
    Contract,

    /// <summary>The body must match a stored snapshot (<see cref="Assertion.Expected"/>); Path lists ignore paths.</summary>
    Snapshot
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

    /// <summary>JSON Schema of the body (from OpenAPI); the mock server can generate fresh data from it.</summary>
    public string Schema { get; set; } = string.Empty;

    /// <summary>
    /// A recorded streaming session (WebSocket, SSE): every message with its time offset. The mock server replays it,
    /// answering client messages with the segment that followed the matching recorded one.
    /// </summary>
    public List<SessionMessage> Session { get; set; } = [];

    public ResponseExample Clone() => DeepCopy.Of(this);
}

/// <summary>One message of a recorded streaming session.</summary>
public sealed class SessionMessage
{
    /// <summary>Milliseconds since the session started.</summary>
    public long AtMs { get; set; }

    /// <summary>Received = from the server (replayed by the mock); Sent = from the client (what the mock waits for).</summary>
    public MessageDirection Direction { get; set; }
    public string Content { get; set; } = string.Empty;

    /// <summary>SSE event name (and id), MQTT topic, ….</summary>
    public string? Label { get; set; }
}

/// <summary>The outcome of one assertion or script test.</summary>
public sealed record TestResult(string Name, bool Passed, string? Message = null, string? Actual = null);

/// <summary>
/// A cross-protocol consequence check: sending this request must (or must not) cause a matching message on another
/// channel — a Kafka topic, an MQTT topic, a RabbitMQ queue, a WebSocket, SSE or Socket.IO stream — within a time limit.
/// The channel is a saved streaming request (the listener) in the same collection; it is subscribed before this request
/// is sent, so nothing published in between is missed. <see cref="Expected"/> may use variables extracted from this
/// request's response, e.g. <c>{{orderId}}</c>.
/// </summary>
public sealed class MessageExpectation
{
    public bool Enabled { get; set; } = true;

    /// <summary>The saved streaming request that subscribes to the channel.</summary>
    public Guid ListenerId { get; set; }

    /// <summary>Where to read the value in each message: JsonPath (default), Body, Regex or XPath.</summary>
    public ValueSource Source { get; set; } = ValueSource.JsonPath;
    public string Path { get; set; } = string.Empty;
    public AssertionOperator Operator { get; set; } = AssertionOperator.Equals;
    public string Expected { get; set; } = string.Empty;

    /// <summary>Only consider messages whose label (topic, event name, routing key) contains this text.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>How long after the request is sent the message may arrive.</summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>At least this many matching messages must arrive.</summary>
    public int MinCount { get; set; } = 1;

    /// <summary>Invert the check: no matching message may arrive within the time limit.</summary>
    public bool ExpectNone { get; set; }

    public MessageExpectation Clone() => (MessageExpectation)MemberwiseClone();
}
