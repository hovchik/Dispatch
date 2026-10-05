using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Laws;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.ClientFuzz;

public sealed class ClientFuzzOptions
{
    /// <summary>Only fuzz responses from hosts containing this (case-insensitive); empty fuzzes every host.</summary>
    public string HostFilter { get; init; } = string.Empty;

    /// <summary>How long to watch the client after a mutated response.</summary>
    public TimeSpan Observation { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Clean responses per endpoint before fuzzing it (to learn its shape and the client's normal behaviour).</summary>
    public int BaselineResponses { get; init; } = 2;

    public int MaxMutationsPerEndpoint { get; init; } = 25;
    public TimeSpan SlowDelay { get; init; } = TimeSpan.FromSeconds(3);
    public IReadOnlySet<MutationKind> Kinds { get; init; } = Enum.GetValues<MutationKind>().ToHashSet();
}

public enum FuzzVerdict
{
    Running,

    /// <summary>The client reacted badly: retry storm, broken values in later requests, an error report, or it stalled.</summary>
    Breaks,

    /// <summary>No sign of trouble while watching.</summary>
    Copes
}

/// <summary>One mutation tried on one response, and how the client behaved afterwards.</summary>
public sealed class FuzzExperiment
{
    public required string Endpoint { get; init; }
    public required string Url { get; init; }
    public required ResponseMutation Mutation { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset WatchUntil { get; init; }
    public FuzzVerdict Verdict { get; set; } = FuzzVerdict.Running;
    public List<string> Signals { get; } = [];
    public int RequestsAfter { get; set; }
    public string Summary => Verdict switch
    {
        FuzzVerdict.Breaks => $"Breaks when {Mutation.Description}: {string.Join("; ", Signals)}",
        FuzzVerdict.Copes => $"Copes with {Mutation.Description} ({RequestsAfter} request(s) afterwards, nothing unusual)",
        _ => $"Watching how the client handles {Mutation.Description}…"
    };
}

/// <summary>
/// Fuzzes a client (a web or mobile app) instead of the server: sitting in the capture proxy, it lets a few responses per
/// endpoint through untouched to learn their shape and the client's normal follow-up traffic, then changes one response
/// at a time — a null or missing field, an empty list, an unexpected enum value, a wrong type, a 500 / 401 / 429, a
/// malformed or slow body — and watches what the client does next. It reports a break when the client starts a retry
/// storm, sends broken values it got from the response (<c>/users/undefined</c>, <c>null</c>, <c>NaN</c>,
/// <c>[object Object]</c>), reports an error to an error-tracking endpoint, or goes silent where it normally continues.
/// All timing comes from the callers' timestamps, so the logic is deterministic.
/// </summary>
public sealed partial class ClientFuzzer(ClientFuzzOptions options) : IResponseInterceptor
{
    private sealed class EndpointState
    {
        public int CleanResponses;
        public Queue<ResponseMutation>? Plan;
        public List<(int Same, int Total, bool BrokenValues)> Baseline { get; } = [];
    }

    private sealed record SeenRequest(DateTimeOffset At, string Method, string Url, string Endpoint, string Body);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, EndpointState> _endpoints = new(StringComparer.Ordinal);
    private readonly List<SeenRequest> _requests = [];
    private readonly List<(string Endpoint, DateTimeOffset At)> _pendingBaselines = [];
    private readonly List<FuzzExperiment> _experiments = [];
    private FuzzExperiment? _active;

    public event Action<FuzzExperiment>? ExperimentStarted;
    public event Action<FuzzExperiment>? ExperimentFinished;

    public IReadOnlyList<FuzzExperiment> Experiments
    {
        get { lock (_gate) return _experiments.ToList(); }
    }

    /// <summary>Endpoints seen so far with how many mutations are left to try.</summary>
    public IReadOnlyList<(string Endpoint, int Remaining)> Endpoints
    {
        get { lock (_gate) return _endpoints.Select(e => (e.Key, e.Value.Plan?.Count ?? -1)).ToList(); }
    }

    public void OnRequest(string method, string url, string body, DateTimeOffset at)
    {
        FuzzExperiment? finished;
        lock (_gate)
        {
            finished = TickLocked(at);
            _requests.Add(new SeenRequest(at, method.ToUpperInvariant(), url, Endpoint(method, url), body));
            // Keep a bounded window of history.
            if (_requests.Count > 5000)
                _requests.RemoveRange(0, 1000);
        }
        if (finished is not null)
            ExperimentFinished?.Invoke(finished);
    }

    public InterceptResult? Intercept(InterceptedResponse response, DateTimeOffset at)
    {
        if (!Matches(response.Url))
            return null;
        FuzzExperiment? finished, started = null;
        InterceptResult? result = null;
        lock (_gate)
        {
            finished = TickLocked(at);
            var endpoint = Endpoint(response.Method, response.Url);
            if (!_endpoints.TryGetValue(endpoint, out var state))
                _endpoints[endpoint] = state = new EndpointState();

            var clean = response.Status is >= 200 and < 300;
            if (_active is not null || !clean)
            {
                // One experiment at a time, so the client's reaction can be attributed to it.
            }
            else if (state.CleanResponses < options.BaselineResponses || state.Plan is null)
            {
                state.CleanResponses++;
                _pendingBaselines.Add((endpoint, at));
                if (state.CleanResponses >= options.BaselineResponses)
                    state.Plan = new Queue<ResponseMutation>(Mutations.Plan(response.Body, options.Kinds, options.MaxMutationsPerEndpoint));
            }
            else
            {
                while (state.Plan.Count > 0 && result is null)
                {
                    var mutation = state.Plan.Dequeue();
                    result = Mutations.Apply(mutation, response, options.SlowDelay);
                    if (result is null)
                        continue;
                    _active = started = new FuzzExperiment
                    {
                        Endpoint = endpoint, Url = response.Url, Mutation = mutation, StartedAt = at,
                        // A slow response delays everything after it; watch for that much longer.
                        WatchUntil = at + options.Observation + result.Delay
                    };
                    _experiments.Add(started);
                }
            }
        }
        if (finished is not null)
            ExperimentFinished?.Invoke(finished);
        if (started is not null)
            ExperimentStarted?.Invoke(started);
        return result;
    }

    /// <summary>Closes experiments whose watch window is over. Call it on a timer so the last one finishes without new traffic.</summary>
    public FuzzExperiment? Tick(DateTimeOffset now)
    {
        FuzzExperiment? finished;
        lock (_gate)
            finished = TickLocked(now);
        if (finished is not null)
            ExperimentFinished?.Invoke(finished);
        return finished;
    }

    private FuzzExperiment? TickLocked(DateTimeOffset now)
    {
        // Baseline: what the client normally does in the window after a clean response.
        foreach (var (endpoint, at) in _pendingBaselines.Where(b => b.At + options.Observation <= now).ToList())
        {
            _pendingBaselines.Remove((endpoint, at));
            var after = After(at, at + options.Observation);
            _endpoints[endpoint].Baseline.Add((after.Count(r => r.Endpoint == endpoint), after.Count, after.Any(r => BrokenValue(r) is not null)));
        }

        if (_active is not { } experiment || experiment.WatchUntil > now)
            return null;
        _active = null;
        Judge(experiment);
        return experiment;
    }

    private List<SeenRequest> After(DateTimeOffset from, DateTimeOffset to) => _requests.Where(r => r.At > from && r.At <= to).ToList();

    private void Judge(FuzzExperiment experiment)
    {
        var after = After(experiment.StartedAt, experiment.WatchUntil);
        experiment.RequestsAfter = after.Count;
        var baseline = _endpoints[experiment.Endpoint].Baseline;
        var normalSame = baseline.Count == 0 ? 0 : baseline.Average(b => b.Same);
        var normalTotal = baseline.Count == 0 ? 0 : baseline.Average(b => b.Total);
        var seconds = (experiment.WatchUntil - experiment.StartedAt).TotalSeconds;

        var same = after.Count(r => r.Endpoint == experiment.Endpoint);
        if (same >= 5 && same >= 3 * Math.Max(1, normalSame))
            experiment.Signals.Add($"retry storm: {same} requests to {experiment.Endpoint} in {seconds:0.#} s (normally {normalSame:0.#})");

        // Values the client could only have produced from a broken field.
        if (!baseline.Any(b => b.BrokenValues))
            foreach (var request in after.Where(r => BrokenValue(r) is not null).Take(3))
                experiment.Signals.Add($"used a broken value: {request.Method} {Observations.PathOf(request.Url)}{QueryOf(request.Url)} contains \"{BrokenValue(request)}\"");

        foreach (var report in after.Where(r => r.Method is "POST" or "PUT" && ErrorReporting().IsMatch(r.Url)).Take(2))
            experiment.Signals.Add($"reported an error: {report.Method} {Observations.PathOf(report.Url)}");

        if (after.Count == 0 && normalTotal >= 2)
            experiment.Signals.Add($"went silent: no requests in {seconds:0.#} s (normally {normalTotal:0.#} follow-up requests)");

        experiment.Verdict = experiment.Signals.Count > 0 ? FuzzVerdict.Breaks : FuzzVerdict.Copes;
    }

    private static string? BrokenValue(SeenRequest r)
    {
        var match = BrokenToken().Match(r.Url + " " + r.Body);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string QueryOf(string url)
    {
        var q = url.IndexOf('?');
        return q < 0 ? "" : url[q..];
    }

    private bool Matches(string url) =>
        options.HostFilter.Length == 0 ||
        (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Contains(options.HostFilter, StringComparison.OrdinalIgnoreCase));

    private static string Endpoint(string method, string url) => $"{method.ToUpperInvariant()} {Observations.PathTemplate(url)}";

    /// <summary>"undefined", "null", "NaN" or "[object Object]" as a path segment, query value or JSON string value.</summary>
    [GeneratedRegex(@"(?:[/=]|"":\s*"")(undefined|null|NaN|\[object%20Object\]|\[object Object\])(?=$|[/?&#\s""])")]
    private static partial Regex BrokenToken();

    [GeneratedRegex(@"sentry|bugsnag|rollbar|honeybadger|/errors?\b|/crash|/logs?\b|/logging|/telemetry|/client-?errors?|/report", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorReporting();
}

/// <summary>Text, HTML and JSON reports of a fuzzing session.</summary>
public static class ClientFuzzReport
{
    public static string Summary(IReadOnlyList<FuzzExperiment> experiments)
    {
        var done = experiments.Where(e => e.Verdict != FuzzVerdict.Running).ToList();
        var breaks = done.Count(e => e.Verdict == FuzzVerdict.Breaks);
        return done.Count == 0
            ? "No experiments yet: use the app through the proxy so its responses can be varied."
            : $"{breaks} of {done.Count} response variation(s) broke the client, across {done.Select(e => e.Endpoint).Distinct().Count()} endpoint(s).";
    }

    public static string Text(IReadOnlyList<FuzzExperiment> experiments)
    {
        var sb = new StringBuilder(Summary(experiments)).AppendLine();
        foreach (var e in experiments.Where(e => e.Verdict == FuzzVerdict.Breaks))
            sb.AppendLine($"  ✗ {e.Endpoint}: {e.Mutation.Description}").AppendLine($"      {string.Join("; ", e.Signals)}");
        var copes = experiments.Count(e => e.Verdict == FuzzVerdict.Copes);
        if (copes > 0)
            sb.AppendLine($"  ✓ coped with {copes} other variation(s)");
        return sb.ToString().TrimEnd();
    }

    public static string Html(IReadOnlyList<FuzzExperiment> experiments, string title = "Client fuzzing")
    {
        var breaks = experiments.Where(e => e.Verdict == FuzzVerdict.Breaks).ToList();
        var sb = Begin(title, title, breaks.Count > 0 ? $"{breaks.Count} BREAK{(breaks.Count == 1 ? "" : "S")}" : "NO BREAKS", breaks.Count == 0 ? true : false);
        sb.Append($"<p><b>{E(Summary(experiments))}</b></p><div class=\"stats\">")
            .Append(Stat("Variations tried", experiments.Count(e => e.Verdict != FuzzVerdict.Running).ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("Broke the client", breaks.Count.ToString(CultureInfo.InvariantCulture), bad: breaks.Count > 0 ? true : null))
            .Append(Stat("Endpoints", experiments.Select(e => e.Endpoint).Distinct().Count().ToString(CultureInfo.InvariantCulture)))
            .Append("</div>");
        if (breaks.Count > 0)
        {
            sb.Append("<h2>Breaks</h2><ul class=\"ins\">");
            foreach (var e in breaks)
                sb.Append($"<li class=\"bad\"><b>{E(e.Endpoint)}</b>: {E(e.Mutation.Description)}<br><small>{E(string.Join("; ", e.Signals))}</small></li>");
            sb.Append("</ul>");
        }
        sb.Append("<h2>All variations</h2><div class=\"card\"><table><thead><tr><th>Endpoint</th><th>Response</th><th>Result</th></tr></thead><tbody>");
        foreach (var e in experiments)
            sb.Append($"<tr><td>{E(e.Endpoint)}</td><td>{E(e.Mutation.Description)}</td>" +
                      $"<td style=\"color:var(--{(e.Verdict == FuzzVerdict.Breaks ? "bad" : "ok")})\">{E(e.Verdict == FuzzVerdict.Breaks ? string.Join("; ", e.Signals) : e.Verdict.ToString())}</td></tr>");
        sb.Append("</tbody></table></div>").Append(End);
        return sb.ToString();
    }

    public static string Json(IReadOnlyList<FuzzExperiment> experiments) => new JsonObject
    {
        ["summary"] = Summary(experiments),
        ["experiments"] = new JsonArray(experiments.Select(e => (JsonNode)new JsonObject
        {
            ["endpoint"] = e.Endpoint, ["url"] = e.Url, ["mutation"] = e.Mutation.Kind.ToString(), ["path"] = e.Mutation.Path,
            ["description"] = e.Mutation.Description, ["verdict"] = e.Verdict.ToString(), ["requestsAfter"] = e.RequestsAfter,
            ["signals"] = new JsonArray(e.Signals.Select(s => (JsonNode)s).ToArray()), ["startedAt"] = e.StartedAt.ToString("O", CultureInfo.InvariantCulture)
        }).ToArray())
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
