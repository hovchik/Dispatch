using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Reporting;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.Probes;

/// <summary>The endpoint probes that share <see cref="ProbeReport"/>.</summary>
public enum ProbeKind
{
    Pagination,
    Idempotency,
    Caching
}

/// <summary>A headline number on a probe report ("Pages walked: 12").</summary>
public sealed record ProbeStat(string Label, string Value, string? Hint = null, bool? Bad = null);

/// <summary>One request a probe sent, as listed in its log.</summary>
public sealed record ProbeStep(double AtMs, string Label, string Method, string Url, int Status, bool Error, double LatencyMs, string? Note = null)
{
    /// <summary>The step shows a problem (highlighted in the log).</summary>
    public bool Problem { get; init; }

    public string StatusText => Error ? "error" : Status.ToString(CultureInfo.InvariantCulture);
}

/// <summary>When a probe should fail a CI run.</summary>
public enum ProbeFailOn
{
    Never,
    Warning,
    Bad
}

/// <summary>Result of an endpoint probe: headline stats, plain-language findings and the requests it sent.</summary>
public sealed class ProbeReport
{
    public ProbeKind Kind { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Duration { get; set; }
    public string RequestName { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Error { get; set; }

    /// <summary>Cancelled, or stopped at the request / time cap.</summary>
    public bool Stopped { get; set; }

    public List<ProbeStat> Stats { get; } = [];
    public List<ReportInsight> Insights { get; } = [];
    public List<ProbeStep> Steps { get; } = [];

    /// <summary>Tool-specific data for the JSON report.</summary>
    public JsonObject Details { get; } = [];

    public int RequestsSent => Steps.Count;
    public int Problems => Insights.Count(i => i.Level == InsightLevel.Bad);
    public int Warnings => Insights.Count(i => i.Level == InsightLevel.Warning);

    public string Title => Kind switch
    {
        ProbeKind.Pagination => "Pagination",
        ProbeKind.Idempotency => "Idempotency",
        _ => "Caching"
    };

    /// <summary>True when the report should fail a CI run at the given threshold.</summary>
    public bool Fails(ProbeFailOn threshold) => Error is not null || threshold switch
    {
        ProbeFailOn.Bad => Problems > 0,
        ProbeFailOn.Warning => Problems + Warnings > 0,
        _ => false
    };

    public string Verdict => Error is not null ? "ERROR" : Problems > 0 ? "PROBLEMS" : Warnings > 0 ? "WARNINGS" : "OK";
}

/// <summary>Plumbing the probes share: sending without side effects in history, and recording steps.</summary>
internal sealed class ProbeSession(IRequestSender sender, ProbeReport report, ProbeOptions options, IProgress<ProbeStep>? progress, CancellationToken ct)
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private int _reserved;

    public SendOptions SendOptions { get; } = new()
    {
        Environment = options.Environment,
        CollectionVariables = options.CollectionVariables,
        RecordHistory = false,
        CheckExpectations = false,
        RunScripts = options.RunScripts
    };

    public CancellationToken Token => ct;
    public double ElapsedMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Sends <paramref name="request"/> and logs it. Returns null once the request cap is reached.</summary>
    public async Task<(ApiResponse Response, ProbeStep Step)?> SendAsync(ApiRequest request, string label,
        Func<ApiResponse, (string? Note, bool Problem)>? describe = null)
    {
        lock (_gate)
        {
            if (_reserved >= options.MaxRequests)
            {
                // Everything after this point is skipped: the report must say it is incomplete.
                report.Stopped = true;
                return null;
            }
            _reserved++;
        }
        ct.ThrowIfCancellationRequested();
        var response = await sender.SendAsync(request, SendOptions, ct).ConfigureAwait(false);
        // The HTTP executor turns cancellation into a failed response; don't blame the endpoint for it.
        ct.ThrowIfCancellationRequested();
        var (note, problem) = describe?.Invoke(response) ?? (null, false);
        var url = response.EffectiveUrl ?? request.Url;
        var step = new ProbeStep(ElapsedMs, label, request.Method.ToString().ToUpperInvariant(), url, response.StatusCode, !response.HasResponse,
            response.Elapsed.TotalMilliseconds, note ?? response.Error) { Problem = problem || !response.HasResponse };
        lock (_gate)
        {
            report.Steps.Add(step);
            if (report.Url.Length == 0 || report.Url.Contains("{{", StringComparison.Ordinal))
                report.Url = url;
        }
        progress?.Report(step);
        return (response, step);
    }

    public void Finish()
    {
        report.Duration = _clock.Elapsed;
        if (report.Stopped)
            report.Insights.Add(new ReportInsight(InsightLevel.Info, "The probe stopped early (time limit, request cap or cancelled); results are incomplete."));
    }
}

/// <summary>Limits and context shared by every probe.</summary>
public class ProbeOptions
{
    /// <summary>Hard cap on requests sent.</summary>
    public int MaxRequests { get; init; } = 200;

    /// <summary>Hard cap on the probe's duration.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(2);

    public bool RunScripts { get; init; } = true;
    public ApiEnvironment? Environment { get; init; }
    public IReadOnlyList<KeyValueItem>? CollectionVariables { get; init; }
}

/// <summary>Text, HTML and JSON renderings of a <see cref="ProbeReport"/>.</summary>
public static class ProbeReportWriter
{
    public static string Marker(InsightLevel level) => level switch
    {
        InsightLevel.Good => "✓",
        InsightLevel.Bad => "✗",
        InsightLevel.Warning => "!",
        _ => "·"
    };

    public static string Text(ProbeReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{report.Title} · {report.RequestName}  {report.Method} {report.Url}");
        sb.AppendLine($"  {(report.Error ?? report.Summary)}");
        sb.AppendLine($"  {report.Verdict}: {report.Problems} problem(s), {report.Warnings} warning(s); {report.RequestsSent} requests in {report.Duration.TotalSeconds:0.0} s");
        var width = report.Stats.Count == 0 ? 0 : report.Stats.Max(s => s.Label.Length);
        foreach (var stat in report.Stats)
            sb.AppendLine($"  {stat.Label.PadRight(width)}  {stat.Value}" + (stat.Hint is null ? "" : $"  ({stat.Hint})"));
        if (report.Insights.Count > 0)
        {
            sb.AppendLine();
            foreach (var insight in report.Insights)
                sb.AppendLine($"  {Marker(insight.Level)} {insight.Text}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string Html(ProbeReport report)
    {
        var sb = Begin($"{report.Title} · {report.RequestName}", $"{report.Title} · {report.RequestName}", report.Verdict,
            report.Error is not null || report.Problems > 0 ? false : report.Warnings > 0 ? null : true);
        sb.Append($"<p class=\"muted\">{E(report.Method)} {E(report.Url)} · {report.StartedAt:yyyy-MM-dd HH:mm:ss} · " +
                  $"{report.RequestsSent} requests in {N(report.Duration.TotalSeconds, "0.0")} s</p>");
        sb.Append($"<p><b>{E(report.Error ?? report.Summary)}</b></p>");
        if (report.Stats.Count > 0)
        {
            sb.Append("<div class=\"stats\">");
            foreach (var stat in report.Stats)
                sb.Append(Stat(stat.Label, stat.Value, stat.Bad, stat.Hint));
            sb.Append("</div>");
        }
        if (report.Insights.Count > 0)
        {
            sb.Append("<h2>Findings</h2><ul class=\"ins\">");
            foreach (var insight in report.Insights)
                sb.Append($"<li class=\"{insight.Css}\">{E(insight.Text)}</li>");
            sb.Append("</ul>");
        }
        sb.Append("<h2>Requests</h2><div class=\"card\"><table><thead><tr><th>#</th><th>At</th><th>Step</th><th>Request</th>" +
                  "<th>Status</th><th class=\"r\">Time</th><th>Note</th></tr></thead><tbody>");
        var i = 0;
        foreach (var s in report.Steps)
            sb.Append($"<tr><td>{++i}</td><td>{N(s.AtMs / 1000, "0.000")} s</td><td>{E(s.Label)}</td><td>{E(s.Method)} {E(s.Url)}</td>" +
                      $"<td style=\"color:{(s.Problem ? "var(--bad)" : StatusColour(s.StatusText))}\">{E(s.StatusText)}</td>" +
                      $"<td class=\"r\">{N(s.LatencyMs, "0")} ms</td><td>{E(s.Note)}</td></tr>");
        sb.Append("</tbody></table></div>");
        sb.Append(End);
        return sb.ToString();
    }

    public static string Json(ProbeReport report)
    {
        var root = new JsonObject
        {
            ["tool"] = report.Kind.ToString().ToLowerInvariant(),
            ["request"] = report.RequestName,
            ["method"] = report.Method,
            ["url"] = report.Url,
            ["startedAt"] = report.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["durationMs"] = Math.Round(report.Duration.TotalMilliseconds),
            ["requestsSent"] = report.RequestsSent,
            ["verdict"] = report.Verdict,
            ["summary"] = report.Summary,
            ["error"] = report.Error,
            ["stopped"] = report.Stopped,
            ["problems"] = report.Problems,
            ["warnings"] = report.Warnings,
            ["stats"] = new JsonArray(report.Stats.Select(s => (JsonNode)new JsonObject
            {
                ["label"] = s.Label, ["value"] = s.Value, ["hint"] = s.Hint
            }).ToArray()),
            ["details"] = report.Details.DeepClone(),
            ["insights"] = new JsonArray(report.Insights.Select(i => (JsonNode)new JsonObject
            {
                ["level"] = i.Level.ToString(), ["text"] = i.Text
            }).ToArray()),
            ["steps"] = new JsonArray(report.Steps.Select(s => (JsonNode)new JsonObject
            {
                ["atMs"] = Math.Round(s.AtMs, 1), ["label"] = s.Label, ["method"] = s.Method, ["url"] = s.Url, ["status"] = s.Status,
                ["error"] = s.Error, ["latencyMs"] = Math.Round(s.LatencyMs, 1), ["note"] = s.Note, ["problem"] = s.Problem
            }).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>The three endpoint probes together, for hosts that open any of them.</summary>
public sealed class EndpointProbes(PaginationWalker pagination, IdempotencyProber idempotency, CacheChecker caching)
{
    public PaginationWalker Pagination { get; } = pagination;
    public IdempotencyProber Idempotency { get; } = idempotency;
    public CacheChecker Caching { get; } = caching;
}
