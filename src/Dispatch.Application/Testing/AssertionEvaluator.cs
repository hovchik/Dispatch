using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Abstractions;
using Dispatch.Domain;

namespace Dispatch.Application.Testing;

/// <summary>Evaluates no-code <see cref="Assertion"/>s and applies <see cref="ExtractionRule"/>s.</summary>
public sealed class AssertionEvaluator(IContractValidator? contractValidator = null)
{
    public async Task<IReadOnlyList<TestResult>> EvaluateAsync(ApiRequest request, ApiResponse response,
        Func<string, string> resolve, string? collectionSpec, CancellationToken ct)
    {
        var results = new List<TestResult>();
        foreach (var assertion in request.Assertions.Where(a => a.Enabled))
            results.Add(await EvaluateAsync(assertion, request, response, resolve, collectionSpec, ct).ConfigureAwait(false));
        return results;
    }

    public async Task<TestResult> EvaluateAsync(Assertion assertion, ApiRequest request, ApiResponse response,
        Func<string, string> resolve, string? collectionSpec, CancellationToken ct)
    {
        var name = Describe(assertion);
        if (!response.HasResponse)
            return new TestResult(name, false, $"No response: {response.Error}");

        try
        {
            switch (assertion.Source)
            {
                case ValueSource.JsonSchema:
                {
                    var schema = resolve(assertion.Path.Length > 0 ? assertion.Path : assertion.Expected);
                    var errors = JsonSchemaValidator.Validate(response.Body, LoadText(schema));
                    return errors.Count == 0
                        ? new TestResult(name, true)
                        : new TestResult(name, false, string.Join(Environment.NewLine, errors.Take(20)));
                }
                case ValueSource.Contract:
                {
                    if (contractValidator is null)
                        return new TestResult(name, false, "Contract validation is not available.");
                    var spec = resolve(assertion.Path.Length > 0 ? assertion.Path : collectionSpec ?? "");
                    if (spec.Length == 0)
                        return new TestResult(name, false, "No OpenAPI document: set the collection's spec or the assertion path.");
                    var errors = await contractValidator.ValidateAsync(spec, request, response, ct).ConfigureAwait(false);
                    return errors.Count == 0
                        ? new TestResult(name, true)
                        : new TestResult(name, false, string.Join(Environment.NewLine, errors.Take(20)));
                }
            }

            var values = ResponseValues.Read(response, assertion.Source, resolve(assertion.Path));
            var expected = resolve(assertion.Expected);
            var (passed, message) = Check(values, assertion.Operator, expected);
            var actual = values.Count switch
            {
                0 => "(not found)",
                1 => Truncate(values[0]),
                _ => Truncate("[" + string.Join(", ", values) + "]")
            };
            return new TestResult(name, passed, passed ? null : message ?? $"Actual: {actual}", actual);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or RegexMatchTimeoutException
                                       or JsonException or IOException or InvalidOperationException)
        {
            return new TestResult(name, false, ex.Message);
        }
    }

    /// <summary>Loads inline JSON, or reads it from a file path.</summary>
    private static string LoadText(string schemaOrPath)
    {
        var trimmed = schemaOrPath.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return schemaOrPath;
        if (File.Exists(schemaOrPath))
            return File.ReadAllText(schemaOrPath);
        throw new FormatException("Schema must be inline JSON or a path to a .json file.");
    }

    private static (bool Passed, string? Message) Check(IReadOnlyList<string> values, AssertionOperator op, string expected)
    {
        var first = values.Count > 0 ? values[0] : null;
        switch (op)
        {
            case AssertionOperator.Exists:
                return (values.Count > 0, "Value not found");
            case AssertionOperator.NotExists:
                return (values.Count == 0, "Value exists");
            case AssertionOperator.IsEmpty:
                return (first is null || first.Length == 0 || first is "[]" or "{}" or "null", null);
            case AssertionOperator.IsNotEmpty:
                return (first is { Length: > 0 } && first is not ("[]" or "{}" or "null"), null);
        }

        if (first is null)
            return (false, "Value not found");

        switch (op)
        {
            case AssertionOperator.Equals:
                return (values.Any(v => ValuesEqual(v, expected)), null);
            case AssertionOperator.NotEquals:
                return (!values.Any(v => ValuesEqual(v, expected)), null);
            case AssertionOperator.Contains:
                return (values.Any(v => v.Contains(expected, StringComparison.Ordinal)), null);
            case AssertionOperator.NotContains:
                return (!values.Any(v => v.Contains(expected, StringComparison.Ordinal)), null);
            case AssertionOperator.Matches:
                return (values.Any(v => Regex.IsMatch(v, expected, RegexOptions.None, TimeSpan.FromSeconds(2))), null);
            case AssertionOperator.OneOf:
                var options = expected.Split(new[] { ',', '|' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return (options.Any(o => ValuesEqual(first, o)), null);
            case AssertionOperator.GreaterThan:
            case AssertionOperator.GreaterOrEqual:
            case AssertionOperator.LessThan:
            case AssertionOperator.LessOrEqual:
                if (!TryNumber(first, out var actual) || !TryNumber(expected, out var target))
                    return (false, $"'{Truncate(first)}' or '{expected}' is not a number");
                return (op switch
                {
                    AssertionOperator.GreaterThan => actual > target,
                    AssertionOperator.GreaterOrEqual => actual >= target,
                    AssertionOperator.LessThan => actual < target,
                    _ => actual <= target
                }, null);
            case AssertionOperator.IsType:
                var type = TypeOf(first);
                return (string.Equals(type, expected.Trim(), StringComparison.OrdinalIgnoreCase)
                        || (expected.Trim() == "integer" && type == "number" && !first.Contains('.')), $"Type is {type}");
            case AssertionOperator.LengthEquals:
                if (!int.TryParse(expected, out var length))
                    return (false, $"'{expected}' is not a number");
                var actualLength = LengthOf(first);
                return (actualLength == length, $"Length is {actualLength}");
            case AssertionOperator.IsValid:
                return (true, null);
            default:
                return (false, $"Unsupported operator {op}");
        }
    }

    private static bool ValuesEqual(string actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
            return true;
        if (TryNumber(actual, out var a) && TryNumber(expected, out var b))
            return Math.Abs(a - b) < 1e-9;
        // Structural JSON comparison, so {"a":1,"b":2} equals { "b": 2, "a": 1 }.
        var at = actual.TrimStart();
        if ((at.StartsWith('{') || at.StartsWith('[')) && TryParse(actual, out var x) && TryParse(expected, out var y))
            return JsonNode.DeepEquals(Normalize(x), Normalize(y));
        return false;
    }

    private static JsonNode? Normalize(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Normalize(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Normalize).ToArray()),
        _ => node?.DeepClone()
    };

    private static bool TryParse(string text, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string TypeOf(string value)
    {
        var t = value.Trim();
        if (t is "true" or "false")
            return "boolean";
        if (t == "null")
            return "null";
        if (TryNumber(t, out _))
            return "number";
        if (t.StartsWith('{') && TryParse(t, out _))
            return "object";
        if (t.StartsWith('[') && TryParse(t, out _))
            return "array";
        return "string";
    }

    private static int LengthOf(string value)
    {
        var t = value.TrimStart();
        if ((t.StartsWith('[') || t.StartsWith('{')) && TryParse(value, out var node))
            return node switch { JsonArray a => a.Count, JsonObject o => o.Count, _ => value.Length };
        return value.Length;
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..197] + "..." : s;

    public static string Describe(Assertion a)
    {
        var subject = a.Source switch
        {
            ValueSource.Status => "Status",
            ValueSource.ResponseTime => "Response time (ms)",
            ValueSource.Size => "Size (bytes)",
            ValueSource.Body => "Body",
            ValueSource.MessageCount => "Received messages",
            ValueSource.JsonSchema => "Body matches JSON schema",
            ValueSource.Contract => "Response matches OpenAPI contract",
            _ => $"{a.Source} {a.Path}"
        };
        if (a.Source is ValueSource.JsonSchema or ValueSource.Contract)
            return subject;

        var op = a.Operator switch
        {
            AssertionOperator.Equals => "==",
            AssertionOperator.NotEquals => "!=",
            AssertionOperator.GreaterThan => ">",
            AssertionOperator.GreaterOrEqual => ">=",
            AssertionOperator.LessThan => "<",
            AssertionOperator.LessOrEqual => "<=",
            AssertionOperator.Contains => "contains",
            AssertionOperator.NotContains => "does not contain",
            AssertionOperator.Exists => "exists",
            AssertionOperator.NotExists => "does not exist",
            AssertionOperator.Matches => "matches",
            AssertionOperator.IsType => "is type",
            AssertionOperator.LengthEquals => "has length",
            AssertionOperator.IsEmpty => "is empty",
            AssertionOperator.IsNotEmpty => "is not empty",
            AssertionOperator.OneOf => "is one of",
            _ => a.Operator.ToString()
        };
        return a.Operator is AssertionOperator.Exists or AssertionOperator.NotExists or AssertionOperator.IsEmpty
            or AssertionOperator.IsNotEmpty
            ? $"{subject} {op}"
            : $"{subject} {op} {a.Expected}";
    }

    /// <summary>Applies extraction rules; returns the variables that were set (rules that match nothing are reported).</summary>
    public static (Dictionary<string, (string Value, VariableScope Scope)> Values, List<TestResult> Failures) Extract(
        ApiRequest request, ApiResponse response, Func<string, string> resolve)
    {
        var values = new Dictionary<string, (string, VariableScope)>(StringComparer.Ordinal);
        var failures = new List<TestResult>();
        if (!response.HasResponse)
            return (values, failures);

        foreach (var rule in request.Extractions.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Variable)))
        {
            try
            {
                var found = ResponseValues.Read(response, rule.Source, resolve(rule.Path));
                if (found.Count > 0)
                    values[rule.Variable.Trim()] = (found[0], rule.Scope);
                else
                    failures.Add(new TestResult($"Extract {rule.Variable}", false, $"{rule.Source} '{rule.Path}' matched nothing"));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or RegexMatchTimeoutException)
            {
                failures.Add(new TestResult($"Extract {rule.Variable}", false, ex.Message));
            }
        }
        return (values, failures);
    }
}
