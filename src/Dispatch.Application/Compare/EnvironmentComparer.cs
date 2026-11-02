using System.Diagnostics;
using System.Text.Json;
using Dispatch.Application.Diff;
using Dispatch.Application.Requests;
using Dispatch.Application.Variables;
using Dispatch.Domain;

namespace Dispatch.Application.Compare;

/// <summary>
/// One side of a comparison: an environment, a base URL, or both. A base URL replaces the scheme, host, port and path
/// prefix of every absolute http(s) request URL, and defines <c>{{baseUrl}}</c> when nothing else does, so a collection
/// can be pointed at a second deployment without a second environment.
/// </summary>
public sealed class CompareTarget
{
    public required string Name { get; init; }
    public ApiEnvironment? Environment { get; init; }
    public Uri? BaseUrl { get; init; }

    /// <summary>A target from what the user typed: an http(s) URL becomes a base URL, anything else is an environment.</summary>
    public static CompareTarget FromBaseUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"'{url}' is not an http(s) URL.");
        return new CompareTarget { Name = uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), BaseUrl = uri };
    }

    public static CompareTarget FromEnvironment(ApiEnvironment environment) => new() { Name = environment.Name, Environment = environment };

    public static bool LooksLikeUrl(string text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The environment the requests are sent with: the chosen one, plus <c>baseUrl</c> for a base-URL target.</summary>
    internal ApiEnvironment? EffectiveEnvironment()
    {
        if (BaseUrl is null)
            return Environment;
        var environment = new ApiEnvironment
        {
            Name = Environment?.Name ?? Name,
            Variables = Environment?.Variables.Select(v => v.Clone()).ToList() ?? []
        };
        if (!environment.Variables.Any(v => v.Enabled && v.Key.Trim() == "baseUrl"))
            environment.Variables.Add(new KeyValueItem { Key = "baseUrl", Value = BaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') });
        return environment;
    }

    /// <summary>The request to send on this side: a copy with its absolute URL moved onto <see cref="BaseUrl"/>.</summary>
    internal ApiRequest Prepare(ApiRequest request)
    {
        if (BaseUrl is null)
            return request;
        var copy = request.Clone();
        copy.Url = Rebase(request.Url, BaseUrl);
        return copy;
    }

    /// <summary>
    /// <c>https://api.example.com/v1/users?x=1</c> on base <c>https://staging.example.com/api</c> becomes
    /// <c>https://staging.example.com/api/v1/users?x=1</c>. URLs that start with a variable are left for the environment.
    /// </summary>
    public static string Rebase(string url, Uri baseUrl)
    {
        // Textual, not via System.Uri: the path may still hold {{placeholders}}, which Uri would percent-encode before
        // the sender gets to resolve them.
        var match = SchemeAndAuthority.Match(url.Trim());
        if (!match.Success)
            return url;
        var prefix = baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var scheme = match.Groups["scheme"].Value.ToLowerInvariant();
        if (scheme is "ws" or "wss")
            prefix = (baseUrl.Scheme == "https" ? "wss" : "ws") + prefix[baseUrl.Scheme.Length..];
        var rest = url.Trim()[match.Length..];
        return prefix + (rest.Length == 0 || rest[0] is '/' or '?' or '#' ? rest : "/" + rest);
    }

    private static readonly System.Text.RegularExpressions.Regex SchemeAndAuthority =
        new(@"^(?<scheme>https?|wss?)://[^/?#]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

public sealed class CompareOptions
{
    public required IReadOnlyList<ApiRequest> Requests { get; init; }
    public required CompareTarget Left { get; init; }
    public required CompareTarget Right { get; init; }

    /// <summary>JSONPath-like paths whose values differ on every call (<c>$.timestamp</c>, <c>$..id</c>, <c>$.items[*].etag</c>).</summary>
    public IReadOnlyList<string> IgnorePaths { get; init; } = [];
    public IReadOnlyList<KeyValueItem> CollectionVariables { get; init; } = [];

    /// <summary>Run assertions and scripts on each side (chaining via extractions always works).</summary>
    public bool RunChecks { get; init; }

    /// <summary>Response headers that legitimately differ between deployments and are never reported.</summary>
    public IReadOnlyCollection<string> IgnoreHeaders { get; init; } = DefaultIgnoredHeaders;

    public static readonly IReadOnlyCollection<string> DefaultIgnoredHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "date", "set-cookie", "etag", "last-modified", "expires", "age", "content-length", "connection", "keep-alive", "server",
        "server-timing", "via", "x-cache", "x-request-id", "x-correlation-id", "x-trace-id", "traceparent", "tracestate",
        "cf-ray", "cf-cache-status", "x-amzn-requestid", "x-amzn-trace-id", "x-amz-cf-id", "x-amz-cf-pop", "x-amz-id-2",
        "x-runtime", "x-response-time", "x-powered-by", "report-to", "nel", "alt-svc", "transfer-encoding", "vary", "x-envoy-upstream-service-time"
    };
}

public enum CompareOutcome
{
    /// <summary>Same status and (ignoring the configured paths) the same body and headers.</summary>
    Same,
    Different,

    /// <summary>At least one side got no response.</summary>
    Failed
}

/// <summary>What one side answered, reduced to what the comparison needs.</summary>
public sealed record CompareSide(int Status, string StatusText, double Milliseconds, long Bytes, string? Error, string Body, string? ContentType,
    IReadOnlyList<ResponseHeader> Headers, IReadOnlyList<TestResult>? Checks = null)
{
    public bool HasResponse => Error is null;

    /// <summary>Assertions and script tests that failed on this side (empty when checks were not run).</summary>
    public IReadOnlyList<TestResult> FailedChecks => (Checks ?? []).Where(t => !t.Passed).ToList();

    public static CompareSide From(ApiResponse response) => new(
        response.StatusCode,
        response.HasResponse ? $"{response.StatusCode} {response.ReasonPhrase}".Trim() : "error",
        response.Elapsed.TotalMilliseconds,
        response.SizeBytes,
        response.Error,
        response.Body,
        response.ContentType,
        response.Headers,
        response.TestResults);
}

public sealed record HeaderChange(string Name, string? Left, string? Right)
{
    public override string ToString() => Left is null ? $"+ {Name}: {Right}" : Right is null ? $"- {Name}: {Left}" : $"~ {Name}: {Left} → {Right}";
}

public sealed class CompareRequestResult
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required CompareSide Left { get; init; }
    public required CompareSide Right { get; init; }
    public required CompareOutcome Outcome { get; init; }
    public bool StatusDiffers { get; init; }

    /// <summary>Structural changes for JSON bodies (left → right).</summary>
    public IReadOnlyList<JsonChange> BodyChanges { get; init; } = [];

    /// <summary>For non-JSON bodies: how many lines were added or removed (0 = identical text).</summary>
    public int LineChanges { get; init; }
    public bool BodyIsJson { get; init; }
    public IReadOnlyList<HeaderChange> HeaderChanges { get; init; } = [];

    public bool BodyDiffers => BodyChanges.Count > 0 || LineChanges > 0;

    /// <summary>A check (assertion or script test) failed on at least one side; the request counts as different.</summary>
    public bool ChecksFailed => Left.FailedChecks.Count > 0 || Right.FailedChecks.Count > 0;

    /// <summary>Right minus left, in milliseconds (positive = right is slower).</summary>
    public double LatencyDeltaMs => Right.Milliseconds - Left.Milliseconds;

    /// <summary>Right relative to left, as a percentage (positive = right is slower).</summary>
    public double LatencyDeltaPercent => Left.Milliseconds <= 0 ? 0 : 100 * (Right.Milliseconds - Left.Milliseconds) / Left.Milliseconds;

    /// <summary>One line that says what differs, for lists and the CLI.</summary>
    public string Summary
    {
        get
        {
            if (Outcome == CompareOutcome.Failed)
                return Left.Error is not null && Right.Error is not null ? "both sides failed" : Left.Error is not null ? "left failed" : "right failed";
            if (Outcome == CompareOutcome.Same)
                return "identical";
            var parts = new List<string>();
            if (StatusDiffers)
                parts.Add($"status {Left.StatusText} → {Right.StatusText}");
            if (BodyChanges.Count > 0)
                parts.Add($"{BodyChanges.Count} body change(s)");
            else if (LineChanges > 0)
                parts.Add($"{LineChanges} changed line(s)");
            if (HeaderChanges.Count > 0)
                parts.Add($"{HeaderChanges.Count} header(s)");
            if (Left.FailedChecks.Count > 0)
                parts.Add($"{Left.FailedChecks.Count} check(s) failed on left");
            if (Right.FailedChecks.Count > 0)
                parts.Add($"{Right.FailedChecks.Count} check(s) failed on right");
            return string.Join(", ", parts);
        }
    }
}

public sealed class CompareReport
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Duration { get; init; }
    public required string LeftName { get; init; }
    public required string RightName { get; init; }
    public IReadOnlyList<string> IgnorePaths { get; init; } = [];
    public IReadOnlyList<CompareRequestResult> Results { get; init; } = [];

    public int Same => Results.Count(r => r.Outcome == CompareOutcome.Same);
    public int Different => Results.Count(r => r.Outcome == CompareOutcome.Different);
    public int Failed => Results.Count(r => r.Outcome == CompareOutcome.Failed);
    public bool HasDifferences => Different > 0 || Failed > 0;

    /// <summary>Median latency change of the right side relative to the left, over requests both sides answered.</summary>
    public double MedianLatencyDeltaPercent
    {
        get
        {
            var deltas = Results.Where(r => r.Left.HasResponse && r.Right.HasResponse && r.Left.Milliseconds > 0)
                .Select(r => r.LatencyDeltaPercent).Order().ToList();
            return deltas.Count == 0 ? 0 : deltas[deltas.Count / 2];
        }
    }

    public string Verdict => Results.Count == 0 ? "NO DATA" : Failed > 0 ? "FAILED" : Different > 0 ? "DIFFERENT" : "IDENTICAL";
}

/// <summary>
/// Sends a collection to two environments (or deployments) side by side and compares every response: status,
/// structure of the body with ignore paths, non-volatile headers, and latency. Each side chains its own variables, so
/// a login on the left never leaks a token into the right.
/// </summary>
public sealed class EnvironmentComparer(IRequestSender sender)
{
    public async Task<CompareReport> RunAsync(CompareOptions options, IProgress<CompareRequestResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options.Requests.Count == 0)
            throw new ArgumentException("Choose at least one request.");
        var startedAt = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        var leftVariables = VariableContext.For(options.Left.EffectiveEnvironment(), options.CollectionVariables);
        var rightVariables = VariableContext.For(options.Right.EffectiveEnvironment(), options.CollectionVariables);
        var results = new List<CompareRequestResult>(options.Requests.Count);

        foreach (var request in options.Requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Both sides at the same moment, so a latency delta is a difference between deployments, not between minutes.
            var left = Send(options.Left.Prepare(request), leftVariables);
            var right = Send(options.Right.Prepare(request), rightVariables);
            await Task.WhenAll(left, right).ConfigureAwait(false);
            var result = Analyze(request, CompareSide.From(left.Result), CompareSide.From(right.Result), options);
            results.Add(result);
            progress?.Report(result);
        }

        return new CompareReport
        {
            StartedAt = startedAt,
            Duration = clock.Elapsed,
            LeftName = options.Left.Name,
            RightName = options.Right.Name,
            IgnorePaths = options.IgnorePaths,
            Results = results
        };

        Task<ApiResponse> Send(ApiRequest request, VariableContext variables) => sender.SendAsync(request, new SendOptions
        {
            Variables = variables,
            RecordHistory = false,
            RunScripts = options.RunChecks,
            CheckExpectations = false
        }, cancellationToken);
    }

    /// <summary>Pure comparison of two answers to the same request (also used by tests and the app's history view).</summary>
    public static CompareRequestResult Analyze(ApiRequest request, CompareSide left, CompareSide right, CompareOptions options)
    {
        var label = request.Kind == RequestKind.Http ? request.Method.ToString().ToUpperInvariant() : request.Kind.ToString();
        var name = request.Folder.Length > 0 ? $"{request.Folder}/{request.Name}" : request.Name;
        if (!left.HasResponse || !right.HasResponse)
            return new CompareRequestResult { Name = name, Label = label, Left = left, Right = right, Outcome = CompareOutcome.Failed };

        var statusDiffers = left.Status != right.Status;
        var (isJson, changes, lineChanges) = CompareBodies(left, right, options.IgnorePaths);
        var headers = CompareHeaders(left.Headers, right.Headers, options.IgnoreHeaders);
        var differs = statusDiffers || changes.Count > 0 || lineChanges > 0 || headers.Count > 0
                      || left.FailedChecks.Count > 0 || right.FailedChecks.Count > 0;
        return new CompareRequestResult
        {
            Name = name,
            Label = label,
            Left = left,
            Right = right,
            Outcome = differs ? CompareOutcome.Different : CompareOutcome.Same,
            StatusDiffers = statusDiffers,
            BodyIsJson = isJson,
            BodyChanges = changes,
            LineChanges = lineChanges,
            HeaderChanges = headers
        };
    }

    private static (bool IsJson, IReadOnlyList<JsonChange> Changes, int LineChanges) CompareBodies(CompareSide left, CompareSide right,
        IReadOnlyList<string> ignorePaths)
    {
        if (LooksLikeJson(left) && LooksLikeJson(right))
        {
            try
            {
                return (true, ResponseDiff.Json(left.Body, right.Body, ignorePaths), 0);
            }
            catch (FormatException)
            {
                // Not both valid JSON after all: fall through to a text comparison.
            }
        }
        var l = left.Body.Replace("\r\n", "\n").Trim();
        var r = right.Body.Replace("\r\n", "\n").Trim();
        if (l == r)
            return (false, [], 0);
        var lines = ResponseDiff.Lines(l, r);
        return (false, [], Math.Max(1, lines.Count(d => d.Kind != DiffKind.Same)));
    }

    private static bool LooksLikeJson(CompareSide side)
    {
        if (side.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        var body = side.Body.AsSpan().TrimStart();
        if (body.Length == 0 || body[0] is not ('{' or '['))
            return false;
        try
        {
            using var _ = JsonDocument.Parse(side.Body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<HeaderChange> CompareHeaders(IReadOnlyList<ResponseHeader> left, IReadOnlyList<ResponseHeader> right,
        IReadOnlyCollection<string> ignore)
    {
        static Dictionary<string, string> Index(IReadOnlyList<ResponseHeader> headers, IReadOnlyCollection<string> ignore) =>
            headers.Where(h => !ignore.Contains(h.Name))
                .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key.ToLowerInvariant(), g => string.Join(", ", g.Select(h => h.Value)), StringComparer.OrdinalIgnoreCase);

        var l = Index(left, ignore);
        var r = Index(right, ignore);
        var changes = new List<HeaderChange>();
        foreach (var (name, value) in l.OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            if (!r.TryGetValue(name, out var other))
                changes.Add(new HeaderChange(name, value, null));
            else if (!string.Equals(value, other, StringComparison.Ordinal))
                changes.Add(new HeaderChange(name, value, other));
        }
        foreach (var (name, value) in r.OrderBy(h => h.Key, StringComparer.Ordinal))
            if (!l.ContainsKey(name))
                changes.Add(new HeaderChange(name, null, value));
        return changes;
    }
}
