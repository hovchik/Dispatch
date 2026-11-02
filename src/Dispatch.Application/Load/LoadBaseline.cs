using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dispatch.Application.Load;

/// <summary>Per-request numbers of a saved run.</summary>
public sealed record LoadBaselineRequest(string Name, int Count, int Errors, LatencyStats Latency, double RequestsPerSecond)
{
    public double ErrorRate => Count == 0 ? 0 : (double)Errors / Count;
}

/// <summary>
/// A saved load test to compare later runs with. A baseline file is the JSON report of a run
/// (<see cref="LoadReportWriter.Json"/>), so any exported report can serve as one.
/// </summary>
public sealed class LoadBaseline
{
    public required string Name { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public int VirtualUsers { get; init; }
    public TimeSpan Duration { get; init; }
    public int TotalRequests { get; init; }
    public int Errors { get; init; }
    public double RequestsPerSecond { get; init; }
    public LatencyStats Latency { get; init; } = LatencyStats.From([]);
    public IReadOnlyList<LoadBaselineRequest> PerRequest { get; init; } = [];

    /// <summary>Message throughput and latency, for streaming runs.</summary>
    public double? MessagesPerSecond { get; init; }
    public LatencyStats? MessageLatency { get; init; }

    public double ErrorRate => TotalRequests == 0 ? 0 : (double)Errors / TotalRequests;

    public static LoadBaseline From(LoadReport report, string name) => new()
    {
        Name = name,
        StartedAt = report.StartedAt,
        VirtualUsers = report.VirtualUsers,
        Duration = report.Duration,
        TotalRequests = report.TotalRequests,
        Errors = report.Errors,
        RequestsPerSecond = report.RequestsPerSecond,
        Latency = report.Latency,
        PerRequest = report.PerRequest.Select(r => new LoadBaselineRequest(r.Name, r.Count, r.Errors, r.Latency,
            report.Duration.TotalSeconds > 0 ? r.Count / report.Duration.TotalSeconds : 0)).ToList(),
        MessagesPerSecond = report.Streaming?.MessagesPerSecond,
        MessageLatency = report.Streaming?.MessageLatency
    };

    /// <summary>The file content to save: the run's JSON report.</summary>
    public static string Serialize(LoadReport report, string name) => LoadReportWriter.Json(report, name);

    /// <summary>Reads a baseline from a JSON report. Throws <see cref="FormatException"/> for anything else.</summary>
    public static LoadBaseline Parse(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("A load baseline is a JSON object (the JSON report of a load test).");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Not a load baseline: {ex.Message}");
        }
        if (root["totals"] is not JsonObject totals || root["latencyMs"] is not JsonObject latency)
            throw new FormatException("Not a load baseline: expected the JSON report of a load test (with \"totals\" and \"latencyMs\").");

        var durationMs = Number(root["durationMs"]);
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var streaming = root["streaming"] as JsonObject;
        return new LoadBaseline
        {
            Name = root["name"]?.GetValue<string>() ?? "baseline",
            StartedAt = DateTimeOffset.TryParse(root["startedAt"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : default,
            VirtualUsers = (int)Number(root["virtualUsers"]),
            Duration = duration,
            TotalRequests = (int)Number(totals["requests"]),
            Errors = (int)Number(totals["errors"]),
            RequestsPerSecond = Number(totals["requestsPerSecond"]),
            Latency = Latency(latency),
            PerRequest = (root["perRequest"] as JsonArray ?? []).OfType<JsonObject>().Select(r =>
            {
                var count = (int)Number(r["requests"]);
                return new LoadBaselineRequest(r["name"]?.GetValue<string>() ?? "", count, (int)Number(r["errors"]),
                    r["latencyMs"] is JsonObject l ? Latency(l) : LatencyStats.From([]), durationMs > 0 ? count / (durationMs / 1000.0) : 0);
            }).ToList(),
            MessagesPerSecond = streaming is null ? null : Number(streaming["messagesPerSecond"]),
            MessageLatency = streaming?["messageLatencyMs"] is JsonObject ml ? Latency(ml) : null
        };

        static double Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
        static LatencyStats Latency(JsonObject l) => new(Number(l["min"]), Number(l["mean"]), Number(l["p50"]), Number(l["p90"]), Number(l["p95"]),
            Number(l["p99"]), Number(l["max"]));
    }
}

/// <summary>A metric before and after. <see cref="LowerIsBetter"/> is true for latency and error rate, false for throughput.</summary>
public sealed record MetricDelta(string Metric, string Unit, double Baseline, double Current, bool LowerIsBetter, double MaxRegressionPercent)
{
    /// <summary>Below this absolute change a latency difference is noise, whatever the percentage.</summary>
    public const double LatencyNoiseMs = 5;

    /// <summary>Error rate must move by more than this many percentage points to count.</summary>
    public const double ErrorRateNoise = 0.01;

    public double Delta => Current - Baseline;

    /// <summary>Relative change; a metric that was zero and now isn't is +100 %.</summary>
    public double DeltaPercent => Baseline == 0 ? (Current == 0 ? 0 : 100) : 100 * Delta / Baseline;

    /// <summary>Worse than the baseline by more than the allowed regression (and more than the noise floor).</summary>
    public bool Regressed => Metric == "error rate"
        ? Delta > ErrorRateNoise
        : LowerIsBetter ? DeltaPercent > MaxRegressionPercent && Delta >= (Unit == "ms" ? LatencyNoiseMs : 0)
        : DeltaPercent < -MaxRegressionPercent;

    public bool Improved => Metric == "error rate"
        ? Delta < -ErrorRateNoise
        : LowerIsBetter ? DeltaPercent < -MaxRegressionPercent && -Delta >= (Unit == "ms" ? LatencyNoiseMs : 0)
        : DeltaPercent > MaxRegressionPercent;

    public string Verdict => Regressed ? "REGRESSED" : Improved ? "IMPROVED" : "UNCHANGED";

    public string Format(double value) => Metric == "error rate" ? $"{value * 100:0.0}%" : Unit == "ms" ? $"{value:0} ms" : $"{value:0.0} {Unit}";

    public override string ToString() => $"{Metric}: {Format(Baseline)} → {Format(Current)} ({DeltaPercent:+0;-0;0}%)";
}

/// <summary>One request of the journey, compared with the same request in the baseline.</summary>
public sealed record LoadRequestComparison(string Name, LoadBaselineRequest? Baseline, LoadRequestStats? Current, IReadOnlyList<MetricDelta> Metrics)
{
    public string Verdict => Baseline is null ? "NEW" : Current is null ? "MISSING" : Metrics.Any(m => m.Regressed) ? "REGRESSED"
        : Metrics.Any(m => m.Improved) ? "IMPROVED" : "UNCHANGED";

    public MetricDelta? this[string metric] => Metrics.FirstOrDefault(m => m.Metric == metric);
}

public sealed class LoadComparison
{
    public required string BaselineName { get; init; }
    public DateTimeOffset BaselineStartedAt { get; init; }
    public int BaselineVirtualUsers { get; init; }
    public int CurrentVirtualUsers { get; init; }
    public double MaxRegressionPercent { get; init; }
    public IReadOnlyList<MetricDelta> Overall { get; init; } = [];
    public IReadOnlyList<LoadRequestComparison> PerRequest { get; init; } = [];

    public IEnumerable<MetricDelta> Regressions =>
        Overall.Where(m => m.Regressed).Concat(PerRequest.SelectMany(r => r.Metrics.Where(m => m.Regressed)));

    public bool IsRegression => Regressions.Any();

    public string Verdict => IsRegression ? "REGRESSED"
        : Overall.Any(m => m.Improved) || PerRequest.Any(r => r.Verdict == "IMPROVED") ? "IMPROVED"
        : "UNCHANGED";

    /// <summary>Why the verdict is what it is, one line per finding.</summary>
    public IReadOnlyList<string> Findings
    {
        get
        {
            var list = new List<string>();
            if (BaselineVirtualUsers != CurrentVirtualUsers)
                list.Add($"Different load: baseline ran {BaselineVirtualUsers} virtual user(s), this run {CurrentVirtualUsers}. Deltas are indicative only.");
            foreach (var m in Overall.Where(m => m.Regressed))
                list.Add($"Overall {m} is worse than the baseline.");
            foreach (var r in PerRequest)
            {
                foreach (var m in r.Metrics.Where(m => m.Regressed))
                    list.Add($"{r.Name}: {m}.");
                if (r.Verdict == "MISSING")
                    list.Add($"{r.Name} is in the baseline but was not part of this run.");
                else if (r.Verdict == "NEW")
                    list.Add($"{r.Name} is new; the baseline has no numbers for it.");
            }
            foreach (var m in Overall.Where(m => m.Improved))
                list.Add($"Overall {m} improved.");
            if (list.Count == 0)
                list.Add($"Every metric is within {MaxRegressionPercent:0.#}% of the baseline.");
            return list;
        }
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Verdict} against baseline \"{BaselineName}\" ({BaselineStartedAt:yyyy-MM-dd HH:mm}, {BaselineVirtualUsers} users) · max regression {MaxRegressionPercent:0.#}%");
        foreach (var m in Overall)
            sb.AppendLine($"  {m.Metric,-12} {m.Format(m.Baseline),12} → {m.Format(m.Current),-12} {m.DeltaPercent,6:+0;-0;0}%  {m.Verdict}");
        foreach (var r in PerRequest)
        {
            var p95 = r["p95"];
            var rps = r["throughput"];
            sb.AppendLine($"  {r.Name,-40} {r.Verdict,-10}" +
                          (p95 is null ? "" : $" p95 {p95.Format(p95.Baseline)} → {p95.Format(p95.Current)} ({p95.DeltaPercent:+0;-0;0}%)") +
                          (rps is null ? "" : $"  {rps.Format(rps.Baseline)} → {rps.Format(rps.Current)} ({rps.DeltaPercent:+0;-0;0}%)"));
        }
        return sb.ToString();
    }
}

/// <summary>Compares a run with a saved baseline and decides whether it regressed.</summary>
public static class LoadBaselineComparer
{
    public const double DefaultMaxRegressionPercent = 10;

    public static LoadComparison Compare(LoadReport current, LoadBaseline baseline, double maxRegressionPercent = DefaultMaxRegressionPercent)
    {
        var max = Math.Max(0, maxRegressionPercent);
        var overall = new List<MetricDelta>
        {
            new("p50", "ms", baseline.Latency.P50, current.Latency.P50, true, max),
            new("p95", "ms", baseline.Latency.P95, current.Latency.P95, true, max),
            new("p99", "ms", baseline.Latency.P99, current.Latency.P99, true, max),
            new("throughput", "req/s", baseline.RequestsPerSecond, current.RequestsPerSecond, false, max),
            new("error rate", "", baseline.ErrorRate, current.ErrorRate, true, max)
        };
        if (baseline.MessagesPerSecond is { } baseMsgs && current.Streaming is { } streaming)
        {
            overall.Add(new MetricDelta("messages/s", "msg/s", baseMsgs, streaming.MessagesPerSecond, false, max));
            if (baseline.MessageLatency is { } ml)
                overall.Add(new MetricDelta("message p95", "ms", ml.P95, streaming.MessageLatency.P95, true, max));
        }

        var names = current.PerRequest.Select(r => r.Name).Concat(baseline.PerRequest.Select(r => r.Name)).Distinct(StringComparer.Ordinal);
        var perRequest = names.Select(name =>
        {
            var before = baseline.PerRequest.FirstOrDefault(r => r.Name == name);
            var now = current.PerRequest.FirstOrDefault(r => r.Name == name);
            var metrics = before is null || now is null ? []
                : new List<MetricDelta>
                {
                    new("p50", "ms", before.Latency.P50, now.Latency.P50, true, max),
                    new("p95", "ms", before.Latency.P95, now.Latency.P95, true, max),
                    new("p99", "ms", before.Latency.P99, now.Latency.P99, true, max),
                    new("throughput", "req/s", before.RequestsPerSecond, current.Duration.TotalSeconds > 0 ? now.Count / current.Duration.TotalSeconds : 0, false, max),
                    new("error rate", "", before.ErrorRate, now.Count == 0 ? 0 : (double)now.Errors / now.Count, true, max)
                };
            return new LoadRequestComparison(name, before, now, metrics);
        }).ToList();

        return new LoadComparison
        {
            BaselineName = baseline.Name,
            BaselineStartedAt = baseline.StartedAt,
            BaselineVirtualUsers = baseline.VirtualUsers,
            CurrentVirtualUsers = current.VirtualUsers,
            MaxRegressionPercent = max,
            Overall = overall,
            PerRequest = perRequest
        };
    }

    /// <summary><c>10</c>, <c>10%</c> or <c>0.1</c> → 10.</summary>
    public static double ParsePercent(string text)
    {
        var t = text.Trim();
        var percentSign = t.EndsWith('%');
        if (!double.TryParse(t.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new FormatException($"'{text}' is not a percentage (e.g. 10%).");
        return !percentSign && value < 1 ? value * 100 : value;
    }
}
