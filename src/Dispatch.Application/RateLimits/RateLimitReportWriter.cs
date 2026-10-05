using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Reporting;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.RateLimits;

/// <summary>Plain-language findings about a probed rate limit.</summary>
public static class RateLimitInsights
{
    public static IEnumerable<ReportInsight> For(RateLimitReport report)
    {
        if (report.Error is not null)
            yield break;

        if (!report.Throttled)
        {
            yield return new ReportInsight(InsightLevel.Warning,
                $"No throttling after {report.RequestsSent} requests at about {report.BurstRate:0.#} req/s. The endpoint may have no rate " +
                "limit, or one higher than this probe reached (raise the request cap or concurrency to push further).");
            if (report.Advertised?.Limit is { } advertised && report.BurstCapacity > advertised * 1.1)
                yield return new ReportInsight(InsightLevel.Bad,
                    $"Headers advertise a limit of {advertised}, but {report.BurstCapacity} requests were accepted without throttling.");
            yield break;
        }

        yield return report.ThrottleStatus == 429
            ? new ReportInsight(InsightLevel.Good, $"Throttles with HTTP 429 after {report.BurstCapacity} requests.")
            : new ReportInsight(InsightLevel.Info,
                $"Throttles with HTTP {report.ThrottleStatus} after {report.BurstCapacity} requests; 429 Too Many Requests is the standard status.");

        // Retry-After honesty.
        if (report.RetryAfterSeconds is not { } retryAfter)
            yield return new ReportInsight(InsightLevel.Warning,
                "Throttled responses carry no Retry-After header, so clients can only guess when to retry.");
        else if (report.Recovery is { } recovery)
        {
            var actual = recovery.TotalSeconds;
            var tolerance = Math.Max(1.5, retryAfter * 0.25);
            if (actual > retryAfter + tolerance)
                yield return new ReportInsight(InsightLevel.Bad,
                    $"Retry-After is too optimistic: it said {retryAfter:0.#} s, but requests were accepted again only after {actual:0.#} s. " +
                    "Clients that honour it will be throttled again.");
            else if (actual < retryAfter - tolerance)
                yield return new ReportInsight(InsightLevel.Info,
                    $"Retry-After is conservative: it said {retryAfter:0.#} s, but requests were accepted again after {actual:0.#} s.");
            else
                yield return new ReportInsight(InsightLevel.Good,
                    $"Retry-After is accurate: it said {retryAfter:0.#} s and the endpoint recovered after {actual:0.#} s.");
        }

        // Advertised limit vs observed.
        if (report.Advertised is null)
            yield return new ReportInsight(InsightLevel.Info,
                "No rate-limit headers (X-RateLimit-* or RateLimit-*): clients cannot see their remaining quota before they are throttled.");
        else if (report.Advertised.Limit is { } limit)
        {
            if (Math.Abs(limit - report.BurstCapacity) > Math.Max(2, limit * 0.1))
                yield return new ReportInsight(InsightLevel.Warning,
                    $"{report.Advertised.HeaderStyle} headers advertise a limit of {limit}, but {report.BurstCapacity} requests were accepted before throttling.");
            else
                yield return new ReportInsight(InsightLevel.Good,
                    $"The advertised limit ({limit}) matches what was observed ({report.BurstCapacity}).");
        }

        var lying = report.Samples.FirstOrDefault(s => s.Throttled && s.Remaining > 0);
        if (lying is not null)
            yield return new ReportInsight(InsightLevel.Warning,
                $"A throttled response still reported {lying.Remaining} remaining requests in its headers.");

        switch (report.Refill)
        {
            case RefillKind.FixedWindow:
                yield return new ReportInsight(InsightLevel.Info,
                    $"Capacity came back all at once ({report.CapacityAfterRecovery} requests available after recovery): a fixed window of " +
                    $"about {RateLimitReport.Seconds(report.WindowEstimate)} or less. Clients can burst at the start of each window.");
                break;
            case RefillKind.Gradual:
                yield return new ReportInsight(InsightLevel.Info,
                    $"Capacity comes back gradually (only {report.CapacityAfterRecovery} available right after recovery): a token bucket or " +
                    $"sliding window, refilling at {(report.RefillIsLowerBound ? "at least" : "about")} {report.RefillPerSecond:0.##} req/s.");
                if (report.RefillPerSecond is { } rate && rate > 0)
                    yield return new ReportInsight(InsightLevel.Info,
                        $"Sustained throughput is about {rate * 60:0} requests per minute, with bursts of up to {report.BurstCapacity}.");
                break;
        }

        if (report.Stopped)
            yield return new ReportInsight(InsightLevel.Info, "The probe stopped early (time limit, request cap or cancelled); some results are incomplete.");
    }
}

/// <summary>Text, HTML and JSON renderings of a <see cref="RateLimitReport"/>.</summary>
public static class RateLimitReportWriter
{
    public static string Text(RateLimitReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Rate limit · {report.RequestName}  {report.Url}");
        sb.AppendLine($"  {report.Summary}");
        sb.AppendLine($"  {report.RequestsSent} requests in {report.Duration.TotalSeconds:0.0} s");
        if (report.Throttled)
        {
            sb.AppendLine($"  burst capacity   {report.BurstCapacity} (at {report.BurstRate:0.#} req/s), throttled with HTTP {report.ThrottleStatus}");
            sb.AppendLine($"  recovery         {RateLimitReport.Seconds(report.Recovery)}" +
                          (report.RetryAfterSeconds is { } ra ? $" (Retry-After said {ra:0.#} s)" : " (no Retry-After)"));
            if (report.CapacityAfterRecovery is { } after)
                sb.AppendLine($"  after recovery   {after} request(s) available");
            sb.AppendLine($"  refill           {report.Refill}" +
                          (report.RefillPerSecond is { } r ? $", about {r:0.##} req/s" : ""));
        }
        if (report.Advertised is { } a)
            sb.AppendLine($"  advertised       {a.HeaderStyle}: limit {a.Limit?.ToString(CultureInfo.InvariantCulture) ?? "?"}, " +
                          $"remaining {a.Remaining?.ToString(CultureInfo.InvariantCulture) ?? "?"}, reset {a.ResetSeconds?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?"} s" +
                          (a.Policy is null ? "" : $", policy {a.Policy}"));
        if (report.Insights.Count > 0)
        {
            sb.AppendLine();
            foreach (var insight in report.Insights)
                sb.AppendLine($"  {Marker(insight.Level)} {insight.Text}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Marker(InsightLevel level) => level switch
    {
        InsightLevel.Good => "✓",
        InsightLevel.Bad => "✗",
        InsightLevel.Warning => "!",
        _ => "·"
    };

    public static string Html(RateLimitReport report)
    {
        var sb = Begin($"Rate limit · {report.RequestName}", $"Rate limit · {report.RequestName}",
            report.Error is not null ? "ERROR" : report.Throttled ? report.Refill.ToString() : "NO LIMIT",
            report.Error is not null ? false : report.Throttled ? true : null);
        sb.Append($"<p class=\"muted\">{E(report.Url)} · {report.StartedAt:yyyy-MM-dd HH:mm:ss} · {report.RequestsSent} requests in {N(report.Duration.TotalSeconds, "0.0")} s</p>");
        sb.Append($"<p><b>{E(report.Summary)}</b></p>");
        sb.Append("<div class=\"stats\">")
            .Append(Stat("Burst capacity", report.BurstCapacity.ToString(CultureInfo.InvariantCulture), hint: $"{N(report.BurstRate, "0.#")} req/s"))
            .Append(Stat("Recovery", RateLimitReport.Seconds(report.Recovery),
                hint: report.RetryAfterSeconds is { } ra ? $"Retry-After {N(ra, "0.#")} s" : "no Retry-After"))
            .Append(Stat("Refill", report.Refill.ToString(), hint: report.RefillPerSecond is { } r ? $"{N(r, "0.##")} req/s" : null))
            .Append(Stat("Advertised limit", report.Advertised?.Limit?.ToString(CultureInfo.InvariantCulture) ?? "—", hint: report.Advertised?.HeaderStyle))
            .Append("</div>");

        if (report.Insights.Count > 0)
        {
            sb.Append("<h2>Insights</h2><ul class=\"ins\">");
            foreach (var insight in report.Insights)
                sb.Append($"<li class=\"{insight.Css}\">{E(insight.Text)}</li>");
            sb.Append("</ul>");
        }

        sb.Append("<h2>Timeline</h2>").Append(TimelineSvg(report));
        sb.Append("<h2>Requests</h2><div class=\"card\"><table><thead><tr><th>#</th><th>At</th><th>Phase</th><th>Status</th><th>Remaining</th><th>Retry-After</th></tr></thead><tbody>");
        var i = 0;
        foreach (var s in report.Samples)
            sb.Append($"<tr><td>{++i}</td><td>{N(s.AtMs / 1000, "0.000")} s</td><td>{s.Phase}</td>" +
                      $"<td style=\"color:{(s.Throttled ? "var(--bad)" : StatusColour(s.Status.ToString(CultureInfo.InvariantCulture)))}\">{(s.Error ? "error" : s.Status)}</td>" +
                      $"<td>{s.Remaining?.ToString(CultureInfo.InvariantCulture) ?? ""}</td><td>{(s.RetryAfterSeconds is { } x ? N(x, "0.#") + " s" : "")}</td></tr>");
        sb.Append("</tbody></table></div>");
        sb.Append(End);
        return sb.ToString();
    }

    /// <summary>Each request as a dot over time: accepted on the top row, throttled on the bottom row.</summary>
    private static string TimelineSvg(RateLimitReport report)
    {
        if (report.Samples.Count == 0)
            return "<p class=\"muted\">No requests.</p>";
        const int width = 900, height = 90;
        var max = Math.Max(1, report.Samples.Max(s => s.AtMs));
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {width} {height}\" width=\"100%\" role=\"img\" aria-label=\"Accepted and throttled requests over time\">");
        sb.Append($"<text x=\"0\" y=\"22\" font-size=\"11\" fill=\"currentColor\">accepted</text><text x=\"0\" y=\"67\" font-size=\"11\" fill=\"currentColor\">throttled</text>");
        foreach (var s in report.Samples)
        {
            var x = 70 + (width - 80) * s.AtMs / max;
            var y = s.Throttled || s.Error ? 63 : 18;
            sb.Append($"<circle cx=\"{N(x, "0.#")}\" cy=\"{y}\" r=\"3\" fill=\"{(s.Throttled || s.Error ? "#dc2626" : "#16a34a")}\" fill-opacity=\"0.7\"/>");
        }
        sb.Append($"<text x=\"70\" y=\"86\" font-size=\"10\" fill=\"currentColor\">0 s</text><text x=\"{width - 10}\" y=\"86\" font-size=\"10\" text-anchor=\"end\" fill=\"currentColor\">{N(max / 1000, "0.#")} s</text></svg>");
        return sb.ToString();
    }

    public static string Json(RateLimitReport report)
    {
        var root = new JsonObject
        {
            ["request"] = report.RequestName,
            ["url"] = report.Url,
            ["startedAt"] = report.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["durationMs"] = Math.Round(report.Duration.TotalMilliseconds),
            ["requestsSent"] = report.RequestsSent,
            ["summary"] = report.Summary,
            ["error"] = report.Error,
            ["stopped"] = report.Stopped,
            ["throttled"] = report.Throttled,
            ["throttleStatus"] = report.Throttled ? report.ThrottleStatus : null,
            ["burstCapacity"] = report.BurstCapacity,
            ["burstRate"] = Math.Round(report.BurstRate, 2),
            ["recoverySeconds"] = report.Recovery is { } r ? Math.Round(r.TotalSeconds, 2) : null,
            ["windowSeconds"] = report.WindowEstimate is { } w ? Math.Round(w.TotalSeconds, 2) : null,
            ["retryAfterSeconds"] = report.RetryAfterSeconds,
            ["refill"] = report.Refill.ToString(),
            ["capacityAfterRecovery"] = report.CapacityAfterRecovery,
            ["refillPerSecond"] = report.RefillPerSecond is { } rate ? Math.Round(rate, 3) : null,
            ["refillIsLowerBound"] = report.RefillIsLowerBound,
            ["advertised"] = report.Advertised is { } a
                ? new JsonObject
                {
                    ["style"] = a.HeaderStyle, ["limit"] = a.Limit, ["remaining"] = a.Remaining, ["resetSeconds"] = a.ResetSeconds, ["policy"] = a.Policy
                }
                : null,
            ["insights"] = new JsonArray(report.Insights.Select(i => (JsonNode)new JsonObject { ["level"] = i.Level.ToString(), ["text"] = i.Text }).ToArray()),
            ["samples"] = new JsonArray(report.Samples.Select(s => (JsonNode)new JsonObject
            {
                ["atMs"] = Math.Round(s.AtMs, 1), ["phase"] = s.Phase.ToString(), ["status"] = s.Status, ["throttled"] = s.Throttled,
                ["error"] = s.Error, ["remaining"] = s.Remaining, ["retryAfterSeconds"] = s.RetryAfterSeconds
            }).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
