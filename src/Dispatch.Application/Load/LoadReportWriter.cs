using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Reporting;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.Load;

/// <summary>Analyses a <see cref="LoadReport"/> and renders it as HTML, JSON or CSV.</summary>
public static class LoadReportWriter
{
    /// <summary>Error rate above which a run is considered failed (below it, but above zero, it is degraded).</summary>
    public const double FailedErrorRate = 0.05;

    public static string Verdict(LoadReport report) => report.TotalRequests == 0 ? "NO DATA"
        : report.Errors == 0 ? "PASSED"
        : report.ErrorRate < FailedErrorRate ? "DEGRADED"
        : "FAILED";

    /// <summary>Observations a reader would otherwise have to work out from the numbers.</summary>
    public static IReadOnlyList<ReportInsight> Insights(LoadReport report)
    {
        var list = new List<ReportInsight>();
        if (report.TotalRequests == 0)
        {
            list.Add(new(InsightLevel.Bad, "No requests completed. Check the URL, the environment and that the server is reachable."));
            return list;
        }

        if (report.Errors == 0)
            list.Add(new(InsightLevel.Good, $"All {report.TotalRequests:N0} requests succeeded."));
        else
            list.Add(new(report.ErrorRate < FailedErrorRate ? InsightLevel.Warning : InsightLevel.Bad,
                $"{report.Errors:N0} of {report.TotalRequests:N0} requests failed ({report.ErrorRate:P1})."));

        if (report.Stopped)
            list.Add(new(InsightLevel.Info, $"Stopped early after {report.Duration.TotalSeconds:0.0} s of a planned {report.PlannedDuration.TotalSeconds:0} s."));

        var l = report.Latency;
        if (l.P50 > 0 && l.P99 / l.P50 >= 4)
            list.Add(new(InsightLevel.Warning, $"Long tail: p99 ({l.P99:0} ms) is {l.P99 / l.P50:0.0}× the median ({l.P50:0} ms)."));
        else if (l.P50 > 0)
            list.Add(new(InsightLevel.Good, $"Consistent latency: p99 ({l.P99:0} ms) is within {Math.Max(1, l.P99 / l.P50):0.0}× the median."));

        // Compare the first and last third of the steady state (after ramp-up) to spot degradation under sustained load.
        var steady = report.Timeline.Where(t => t.Second >= (int)report.RampUp.TotalSeconds).ToList();
        if (steady.Count >= 6)
        {
            var third = steady.Count / 3;
            var early = steady.Take(third).ToList();
            var late = steady.TakeLast(third).ToList();
            var earlyP95 = early.Average(t => t.P95);
            var lateP95 = late.Average(t => t.P95);
            var earlyRps = early.Average(t => t.Requests);
            var lateRps = late.Average(t => t.Requests);
            if (earlyP95 > 0 && lateP95 / earlyP95 >= 1.5)
                list.Add(new(InsightLevel.Warning, $"Latency grew during the test: p95 went from {earlyP95:0} ms to {lateP95:0} ms."));
            if (earlyRps > 0 && lateRps / earlyRps <= 0.7)
                list.Add(new(InsightLevel.Warning, $"Throughput dropped from {earlyRps:0.0} to {lateRps:0.0} req/s towards the end."));
            var errorSeconds = steady.Count(t => t.Errors > 0);
            if (errorSeconds > 0 && late.Sum(t => t.Errors) > 2 * Math.Max(1, early.Sum(t => t.Errors)))
                list.Add(new(InsightLevel.Bad, "Errors became more frequent as the test went on."));
        }

        if (report.PerRequest.Count > 1)
        {
            var slowest = report.PerRequest.MaxBy(r => r.Latency.P95)!;
            list.Add(new(InsightLevel.Info, $"Slowest step: {slowest.Name} (p95 {slowest.Latency.P95:0} ms)."));
            var worst = report.PerRequest.Where(r => r.Errors > 0).MaxBy(r => (double)r.Errors / r.Count);
            if (worst is not null)
                list.Add(new(InsightLevel.Bad, $"Most errors: {worst.Name} ({worst.Errors:N0} of {worst.Count:N0}, {(double)worst.Errors / worst.Count:P1})."));
        }

        var serverErrors = report.Statuses.Where(s => s.Key.StartsWith('5')).Sum(s => s.Value);
        if (serverErrors > 0)
            list.Add(new(InsightLevel.Bad, $"{serverErrors:N0} server error response(s) (5xx)."));
        if (report.Statuses.TryGetValue("429", out var throttled))
            list.Add(new(InsightLevel.Warning, $"The server throttled {throttled:N0} request(s) (429 Too Many Requests)."));
        if (report.Statuses.TryGetValue("error", out var transport))
            list.Add(new(InsightLevel.Bad, $"{transport:N0} request(s) got no response (connection or timeout errors)."));
        return list;
    }

    public static string Html(LoadReport report, string name)
    {
        var verdict = Verdict(report);
        var sb = Begin($"{name} · Dispatch load test", $"Load test · {name}", verdict,
            verdict == "PASSED" ? true : verdict == "DEGRADED" ? null : false);
        sb.Append($"<div class=\"muted\">Started {report.StartedAt:yyyy-MM-dd HH:mm:ss} · {report.Duration.TotalSeconds:0.0} s · ")
            .Append($"{report.VirtualUsers} virtual user(s) · ramp-up {report.RampUp.TotalSeconds:0} s · think time {report.ThinkTimeMs} ms")
            .Append(report.Stopped ? " · stopped early" : "").Append("</div>");

        var l = report.Latency;
        sb.Append("<div class=\"stats\">")
            .Append(Stat("Requests", report.TotalRequests.ToString("N0", CultureInfo.InvariantCulture)))
            .Append(Stat("Throughput (req/s)", N(report.RequestsPerSecond, "0.0"), hint: $"peak {N(report.PeakRequestsPerSecond)}/s"))
            .Append(Stat("Errors", $"{N(report.ErrorRate * 100, "0.0")}%", report.Errors > 0, $"{report.Errors:N0} request(s)"))
            .Append(Stat("Average", $"{N(l.Mean)} ms"))
            .Append(Stat("p95", $"{N(l.P95)} ms"))
            .Append(Stat("p99", $"{N(l.P99)} ms"))
            .Append(Stat("Data received", Bytes(report.TotalBytes)))
            .Append("</div>");

        sb.Append("<h2>Insights</h2><ul class=\"ins\">");
        foreach (var i in Insights(report))
            sb.Append($"<li class=\"{i.Css}\">{E(i.Text)}</li>");
        sb.Append("</ul>");

        if (report.Timeline.Count > 0)
        {
            var max = Math.Max(1, report.Timeline.Max(t => t.Requests));
            sb.Append("<h2>Requests per second</h2><div class=\"chart\">");
            foreach (var t in report.Timeline)
                sb.Append($"<i{(t.Errors > 0 ? " class=\"e\"" : "")} style=\"height:{N(Math.Max(2, 100.0 * t.Requests / max), "0.#")}%\" ")
                    .Append($"title=\"{t.Second}s: {t.Requests} req, {t.Errors} errors, p95 {N(t.P95)} ms\"></i>");
            sb.Append("</div>");
        }

        sb.Append("<div class=\"grid2\"><div><h2>Latency percentiles</h2><div class=\"card\"><table><tr>")
            .Append(string.Concat(new[] { "min", "avg", "p50", "p90", "p95", "p99", "max" }.Select(h => $"<th class=\"r\">{h}</th>")))
            .Append("</tr><tr>")
            .Append(string.Concat(new[] { l.Min, l.Mean, l.P50, l.P90, l.P95, l.P99, l.Max }.Select(v => $"<td class=\"r\">{N(v)} ms</td>")))
            .Append("</tr></table></div>");
        var histogram = Trim(report.Histogram);
        if (histogram.Count > 0)
        {
            var max = histogram.Max(b => b.Count);
            sb.Append("<h2>Latency distribution</h2><div class=\"card\">");
            foreach (var b in histogram)
                sb.Append(Bar(b.Label, b.Count, max));
            sb.Append("</div>");
        }
        sb.Append("</div><div><h2>Status codes</h2><div class=\"card\">");
        var statusMax = report.Statuses.Count == 0 ? 0 : report.Statuses.Values.Max();
        foreach (var s in report.Statuses.OrderBy(s => s.Key))
            sb.Append(Bar(s.Key, s.Value, statusMax, StatusColour(s.Key)));
        sb.Append("</div></div></div>");

        sb.Append("<h2>Per request</h2><div class=\"card\"><table><tr><th>Request</th><th class=\"r\">Requests</th><th class=\"r\">Errors</th>")
            .Append("<th class=\"r\">Error %</th><th class=\"r\">min</th><th class=\"r\">avg</th><th class=\"r\">p50</th><th class=\"r\">p90</th>")
            .Append("<th class=\"r\">p95</th><th class=\"r\">p99</th><th class=\"r\">max</th><th>Statuses</th></tr>");
        foreach (var r in report.PerRequest)
        {
            var rl = r.Latency;
            sb.Append($"<tr><td>{E(r.Name)}</td><td class=\"r\">{r.Count:N0}</td><td class=\"r\">{r.Errors:N0}</td>")
                .Append($"<td class=\"r\">{N(r.Count == 0 ? 0 : 100.0 * r.Errors / r.Count, "0.0")}%</td>")
                .Append(string.Concat(new[] { rl.Min, rl.Mean, rl.P50, rl.P90, rl.P95, rl.P99, rl.Max }.Select(v => $"<td class=\"r\">{N(v)}</td>")))
                .Append($"<td>{E(string.Join(", ", r.Statuses.OrderBy(s => s.Key).Select(s => $"{s.Key}×{s.Value}")))}</td></tr>");
        }
        sb.Append("</table></div>");

        if (report.SampleErrors.Count > 0)
        {
            sb.Append("<h2>Errors (distinct samples)</h2><div class=\"card\">");
            foreach (var e in report.SampleErrors)
                sb.Append($"<div class=\"err\">{E(e)}</div>");
            sb.Append("</div>");
        }

        return sb.Append(End).ToString();
    }

    public static string Json(LoadReport report, string name)
    {
        static JsonObject Latency(LatencyStats l) => new()
        {
            ["min"] = Math.Round(l.Min, 1), ["mean"] = Math.Round(l.Mean, 1), ["p50"] = Math.Round(l.P50, 1), ["p90"] = Math.Round(l.P90, 1),
            ["p95"] = Math.Round(l.P95, 1), ["p99"] = Math.Round(l.P99, 1), ["max"] = Math.Round(l.Max, 1)
        };

        var root = new JsonObject
        {
            ["name"] = name,
            ["verdict"] = Verdict(report),
            ["startedAt"] = report.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["durationMs"] = (long)report.Duration.TotalMilliseconds,
            ["stopped"] = report.Stopped,
            ["virtualUsers"] = report.VirtualUsers,
            ["rampUpMs"] = (long)report.RampUp.TotalMilliseconds,
            ["thinkTimeMs"] = report.ThinkTimeMs,
            ["totals"] = new JsonObject
            {
                ["requests"] = report.TotalRequests,
                ["errors"] = report.Errors,
                ["errorRate"] = Math.Round(report.ErrorRate, 4),
                ["requestsPerSecond"] = Math.Round(report.RequestsPerSecond, 2),
                ["peakRequestsPerSecond"] = report.PeakRequestsPerSecond,
                ["bytesReceived"] = report.TotalBytes
            },
            ["latencyMs"] = Latency(report.Latency),
            ["histogram"] = new JsonArray(report.Histogram.Select(b => (JsonNode?)new JsonObject
            {
                ["upToMs"] = double.IsInfinity(b.UpperMs) ? null : b.UpperMs, ["count"] = b.Count
            }).ToArray()),
            ["statuses"] = new JsonObject(report.Statuses.OrderBy(s => s.Key).Select(s => KeyValuePair.Create(s.Key, (JsonNode?)s.Value))),
            ["perRequest"] = new JsonArray(report.PerRequest.Select(r => (JsonNode?)new JsonObject
            {
                ["name"] = r.Name,
                ["requests"] = r.Count,
                ["errors"] = r.Errors,
                ["latencyMs"] = Latency(r.Latency),
                ["statuses"] = new JsonObject(r.Statuses.OrderBy(s => s.Key).Select(s => KeyValuePair.Create(s.Key, (JsonNode?)s.Value)))
            }).ToArray()),
            ["timeline"] = new JsonArray(report.Timeline.Select(t => (JsonNode?)new JsonObject
            {
                ["second"] = t.Second, ["requests"] = t.Requests, ["errors"] = t.Errors, ["p95Ms"] = Math.Round(t.P95, 1)
            }).ToArray()),
            ["insights"] = new JsonArray(Insights(report).Select(i => (JsonNode?)new JsonObject
            {
                ["level"] = i.Css, ["text"] = i.Text
            }).ToArray()),
            ["errors"] = new JsonArray(report.SampleErrors.Select(e => (JsonNode?)e).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>Per-request statistics, one row per request, for spreadsheets.</summary>
    public static string Csv(LoadReport report)
    {
        static string Q(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var sb = new StringBuilder("request,requests,errors,error_rate,min_ms,avg_ms,p50_ms,p90_ms,p95_ms,p99_ms,max_ms\n");
        foreach (var r in report.PerRequest.Append(new LoadRequestStats("(all)", report.TotalRequests, report.Errors, report.Latency,
                     report.Statuses)))
        {
            var l = r.Latency;
            sb.Append(Q(r.Name)).Append(',').Append(r.Count).Append(',').Append(r.Errors).Append(',')
                .Append(N(r.Count == 0 ? 0 : (double)r.Errors / r.Count, "0.####"))
                .Append(string.Concat(new[] { l.Min, l.Mean, l.P50, l.P90, l.P95, l.P99, l.Max }.Select(v => "," + N(v, "0.#"))))
                .Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Drops empty buckets at the high end so the chart isn't mostly zeros.</summary>
    public static IReadOnlyList<LatencyBucket> Trim(IReadOnlyList<LatencyBucket> buckets)
    {
        var last = buckets.Count - 1;
        while (last >= 0 && buckets[last].Count == 0)
            last--;
        var first = 0;
        while (first <= last && buckets[first].Count == 0)
            first++;
        return first > last ? [] : buckets.Skip(first).Take(last - first + 1).ToList();
    }
}
