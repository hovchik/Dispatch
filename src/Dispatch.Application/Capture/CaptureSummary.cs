using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Load;
using Dispatch.Application.Reporting;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.Capture;

/// <summary>Traffic to one host during a capture session.</summary>
public sealed record CaptureHostStats(string Host, int Requests, int Errors, int Secure, long Bytes, LatencyStats Latency)
{
    public double ErrorRate => Requests == 0 ? 0 : (double)Errors / Requests;
}

/// <summary>An endpoint (method + host + path) seen during capture, with how often and how fast it answered.</summary>
public sealed record CaptureEndpointStats(string Method, string Host, string Path, int Requests, int Errors, LatencyStats Latency)
{
    public string Display => $"{Method} {Host}{Path}";
}

/// <summary>Counts for a distribution chart (status classes, methods, content types).</summary>
public sealed record CaptureCount(string Label, int Count);

/// <summary>A report over captured traffic: volume, hosts, endpoints, statuses, latency, failures.</summary>
public sealed class CaptureSummary
{
    public int Total { get; init; }
    public int Errors { get; init; }
    public int Secure { get; init; }
    public long Bytes { get; init; }
    public DateTimeOffset? First { get; init; }
    public DateTimeOffset? Last { get; init; }
    public LatencyStats Latency { get; init; } = LatencyStats.From([]);
    public IReadOnlyList<CaptureHostStats> Hosts { get; init; } = [];
    public IReadOnlyList<CaptureEndpointStats> Endpoints { get; init; } = [];
    public IReadOnlyList<CaptureCount> StatusClasses { get; init; } = [];
    public IReadOnlyList<CaptureCount> Methods { get; init; } = [];
    public IReadOnlyList<CaptureCount> ContentTypes { get; init; } = [];
    public IReadOnlyList<CapturedExchange> Slowest { get; init; } = [];
    public IReadOnlyList<CapturedExchange> Failed { get; init; } = [];
    public IReadOnlyList<ReportInsight> Insights { get; private set; } = [];

    public TimeSpan Span => First is { } f && Last is { } l ? l - f : TimeSpan.Zero;
    public double ErrorRate => Total == 0 ? 0 : (double)Errors / Total;

    /// <summary>A failed exchange: no response (proxy/upstream error) or a 4xx/5xx status.</summary>
    public static bool IsError(CapturedExchange e) => e.Error is not null || e.StatusCode >= 400 || e.StatusCode == 0;

    public static string StatusClass(CapturedExchange e) => e.Error is not null || e.StatusCode == 0
        ? "Error"
        : $"{e.StatusCode / 100}xx";

    public static CaptureSummary From(IEnumerable<CapturedExchange> exchanges)
    {
        var all = exchanges.ToList();
        static string ContentType(CapturedExchange e)
        {
            var ct = e.ResponseContentType ?? "";
            var semi = ct.IndexOf(';');
            ct = (semi >= 0 ? ct[..semi] : ct).Trim().ToLowerInvariant();
            return ct.Length == 0 ? "(none)" : ct;
        }

        var summary = new CaptureSummary
        {
            Total = all.Count,
            Errors = all.Count(IsError),
            Secure = all.Count(e => e.Secure),
            Bytes = all.Sum(e => e.ResponseSize),
            First = all.Count == 0 ? null : all.Min(e => e.Timestamp),
            Last = all.Count == 0 ? null : all.Max(e => e.Timestamp),
            Latency = LatencyStats.From(all.Select(e => e.ElapsedMs).ToList()),
            Hosts = all.GroupBy(e => e.Host.Length == 0 ? "(unknown)" : e.Host, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CaptureHostStats(g.Key, g.Count(), g.Count(IsError), g.Count(e => e.Secure), g.Sum(e => e.ResponseSize),
                    LatencyStats.From(g.Select(e => e.ElapsedMs).ToList())))
                .OrderByDescending(h => h.Requests).ThenBy(h => h.Host, StringComparer.OrdinalIgnoreCase).ToList(),
            Endpoints = all.GroupBy(e => (Method: e.Method.ToUpperInvariant(), e.Host, e.Path))
                .Select(g => new CaptureEndpointStats(g.Key.Method, g.Key.Host, g.Key.Path, g.Count(), g.Count(IsError),
                    LatencyStats.From(g.Select(e => e.ElapsedMs).ToList())))
                .OrderByDescending(x => x.Requests).ThenByDescending(x => x.Latency.Mean).ToList(),
            StatusClasses = all.GroupBy(StatusClass).OrderBy(g => g.Key == "Error" ? "9" : g.Key)
                .Select(g => new CaptureCount(g.Key, g.Count())).ToList(),
            Methods = all.GroupBy(e => e.Method.ToUpperInvariant()).OrderByDescending(g => g.Count())
                .Select(g => new CaptureCount(g.Key, g.Count())).ToList(),
            ContentTypes = all.GroupBy(ContentType).OrderByDescending(g => g.Count())
                .Select(g => new CaptureCount(g.Key, g.Count())).ToList(),
            Slowest = all.OrderByDescending(e => e.ElapsedMs).Take(10).ToList(),
            Failed = all.Where(IsError).OrderBy(e => e.Timestamp).ToList()
        };
        summary.Insights = BuildInsights(summary);
        return summary;
    }

    private static List<ReportInsight> BuildInsights(CaptureSummary s)
    {
        var list = new List<ReportInsight>();
        if (s.Total == 0)
        {
            list.Add(new(InsightLevel.Info, "Nothing captured yet. Start the proxy and send traffic through it."));
            return list;
        }

        list.Add(s.Errors == 0
            ? new(InsightLevel.Good, $"All {s.Total} captured request(s) succeeded.")
            : new(s.ErrorRate < 0.05 ? InsightLevel.Warning : InsightLevel.Bad,
                $"{s.Errors} of {s.Total} request(s) failed ({s.ErrorRate:P1})."));

        var plain = s.Total - s.Secure;
        if (plain > 0)
            list.Add(new(InsightLevel.Warning, $"{plain} request(s) went over plain HTTP (unencrypted)."));

        var server = s.Failed.Count(e => e.StatusCode >= 500);
        if (server > 0)
            list.Add(new(InsightLevel.Bad, $"{server} server error(s) (5xx)."));
        var auth = s.Failed.Count(e => e.StatusCode is 401 or 403);
        if (auth > 0)
            list.Add(new(InsightLevel.Warning, $"{auth} request(s) were rejected as unauthorised (401/403)."));

        if (s.Endpoints.FirstOrDefault(e => e.Requests >= 5) is { } chatty)
            list.Add(new(InsightLevel.Info, $"Most called endpoint: {chatty.Display} ({chatty.Requests}×). Repeated calls may be worth caching."));
        if (s.Slowest.Count > 0 && s.Slowest[0].ElapsedMs >= 1000)
            list.Add(new(InsightLevel.Warning, $"Slowest call: {s.Slowest[0].Method} {s.Slowest[0].Url} took {s.Slowest[0].ElapsedMs / 1000:0.0} s."));
        if (s.Hosts.Count > 1)
            list.Add(new(InsightLevel.Info, $"Traffic went to {s.Hosts.Count} host(s); {s.Hosts[0].Host} received the most ({s.Hosts[0].Requests})."));
        return list;
    }

    public string Html(string title = "Capture report")
    {
        var sb = Begin($"{title} · Dispatch", title, Errors == 0 ? "NO ERRORS" : $"{Errors} ERROR(S)", Errors == 0 ? true : ErrorRate < 0.05 ? null : false);
        if (First is { } first)
            sb.Append($"<div class=\"muted\">{first:yyyy-MM-dd HH:mm:ss} – {Last:HH:mm:ss} · {Span.TotalSeconds:0} s</div>");
        sb.Append("<div class=\"stats\">")
            .Append(Stat("Requests", Total.ToString("N0", CultureInfo.InvariantCulture)))
            .Append(Stat("Errors", $"{N(ErrorRate * 100, "0.0")}%", Errors > 0, $"{Errors} request(s)"))
            .Append(Stat("Hosts", Hosts.Count.ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("HTTPS", Total == 0 ? "–" : $"{N(100.0 * Secure / Total)}%"))
            .Append(Stat("Average", $"{N(Latency.Mean)} ms", hint: $"p95 {N(Latency.P95)} ms"))
            .Append(Stat("Received", Bytes(this.Bytes)))
            .Append("</div><h2>Insights</h2><ul class=\"ins\">");
        foreach (var i in Insights)
            sb.Append($"<li class=\"{i.Css}\">{E(i.Text)}</li>");
        sb.Append("</ul><div class=\"grid2\">");
        Distribution("Status", StatusClasses, c => c.Label == "Error" ? "#dc2626" : StatusColour(c.Label));
        Distribution("Methods", Methods, null);
        Distribution("Content types", ContentTypes.Take(8).ToList(), null);
        sb.Append("</div>");

        sb.Append("<h2>Hosts</h2><div class=\"card\"><table><tr><th>Host</th><th class=\"r\">Requests</th><th class=\"r\">Errors</th>")
            .Append("<th class=\"r\">HTTPS</th><th class=\"r\">avg</th><th class=\"r\">p95</th><th class=\"r\">Received</th></tr>");
        foreach (var h in Hosts)
            sb.Append($"<tr><td>{E(h.Host)}</td><td class=\"r\">{h.Requests}</td><td class=\"r\">{h.Errors}</td><td class=\"r\">{h.Secure}</td>")
                .Append($"<td class=\"r\">{N(h.Latency.Mean)} ms</td><td class=\"r\">{N(h.Latency.P95)} ms</td><td class=\"r\">{Bytes(h.Bytes)}</td></tr>");
        sb.Append("</table></div>");

        sb.Append("<h2>Endpoints</h2><div class=\"card\"><table><tr><th>Endpoint</th><th class=\"r\">Calls</th><th class=\"r\">Errors</th>")
            .Append("<th class=\"r\">avg</th><th class=\"r\">max</th></tr>");
        foreach (var e in Endpoints.Take(50))
            sb.Append($"<tr><td><code>{E(e.Display)}</code></td><td class=\"r\">{e.Requests}</td><td class=\"r\">{e.Errors}</td>")
                .Append($"<td class=\"r\">{N(e.Latency.Mean)} ms</td><td class=\"r\">{N(e.Latency.Max)} ms</td></tr>");
        sb.Append("</table></div>");

        if (Failed.Count > 0)
        {
            sb.Append("<h2>Failed requests</h2><div class=\"card\"><table><tr><th>Time</th><th>Request</th><th>Result</th></tr>");
            foreach (var e in Failed.Take(200))
                sb.Append($"<tr><td>{e.Timestamp:HH:mm:ss}</td><td><code>{E(e.Method)} {E(e.Url)}</code></td>")
                    .Append($"<td class=\"err\">{E(e.Error ?? $"{e.StatusCode} {e.ReasonPhrase}".Trim())}</td></tr>");
            sb.Append("</table></div>");
        }
        return sb.Append(End).ToString();

        void Distribution(string heading, IReadOnlyList<CaptureCount> counts, Func<CaptureCount, string>? colour)
        {
            sb.Append($"<div><h2>{E(heading)}</h2><div class=\"card\">");
            var max = counts.Count == 0 ? 0 : counts.Max(c => c.Count);
            foreach (var c in counts)
                sb.Append(Bar(c.Label, c.Count, max, colour?.Invoke(c)));
            sb.Append("</div></div>");
        }
    }

    public string Json()
    {
        static JsonObject Lat(LatencyStats l) => new()
        {
            ["mean"] = Math.Round(l.Mean, 1), ["p50"] = Math.Round(l.P50, 1), ["p95"] = Math.Round(l.P95, 1), ["max"] = Math.Round(l.Max, 1)
        };
        static JsonArray Counts(IEnumerable<CaptureCount> c) =>
            new(c.Select(x => (JsonNode?)new JsonObject { ["label"] = x.Label, ["count"] = x.Count }).ToArray());

        var root = new JsonObject
        {
            ["requests"] = Total,
            ["errors"] = Errors,
            ["secure"] = Secure,
            ["bytesReceived"] = Bytes,
            ["from"] = First?.ToString("O", CultureInfo.InvariantCulture),
            ["to"] = Last?.ToString("O", CultureInfo.InvariantCulture),
            ["latencyMs"] = Lat(Latency),
            ["statusClasses"] = Counts(StatusClasses),
            ["methods"] = Counts(Methods),
            ["contentTypes"] = Counts(ContentTypes),
            ["hosts"] = new JsonArray(Hosts.Select(h => (JsonNode?)new JsonObject
            {
                ["host"] = h.Host, ["requests"] = h.Requests, ["errors"] = h.Errors, ["secure"] = h.Secure, ["bytes"] = h.Bytes,
                ["latencyMs"] = Lat(h.Latency)
            }).ToArray()),
            ["endpoints"] = new JsonArray(Endpoints.Select(e => (JsonNode?)new JsonObject
            {
                ["method"] = e.Method, ["host"] = e.Host, ["path"] = e.Path, ["requests"] = e.Requests, ["errors"] = e.Errors,
                ["latencyMs"] = Lat(e.Latency)
            }).ToArray()),
            ["insights"] = new JsonArray(Insights.Select(i => (JsonNode?)new JsonObject { ["level"] = i.Css, ["text"] = i.Text }).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }
}
