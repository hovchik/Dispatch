using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Diff;
using Dispatch.Application.Reporting;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.Compare;

/// <summary>Renders a <see cref="CompareReport"/> for the terminal, as HTML, or as JSON.</summary>
public static class CompareReportWriter
{
    /// <summary>A latency change smaller than this (and than 20 ms) is noise, not a finding.</summary>
    public const double NotableLatencyPercent = 25;

    public static IReadOnlyList<ReportInsight> Insights(CompareReport report)
    {
        var list = new List<ReportInsight>();
        if (report.Results.Count == 0)
        {
            list.Add(new(InsightLevel.Bad, "No requests were compared."));
            return list;
        }

        if (report.Failed > 0)
            list.Add(new(InsightLevel.Bad, $"{report.Failed} request(s) got no response on at least one side: " +
                                           string.Join(", ", report.Results.Where(r => r.Outcome == CompareOutcome.Failed).Select(r => r.Name).Take(5)) + "."));
        if (report.Different == 0 && report.Failed == 0)
            list.Add(new(InsightLevel.Good, $"All {report.Results.Count} request(s) answered identically on {report.LeftName} and {report.RightName}."));
        else if (report.Same > 0)
            list.Add(new(InsightLevel.Info, $"{report.Same} of {report.Results.Count} request(s) answered identically."));

        var statuses = report.Results.Where(r => r.StatusDiffers).ToList();
        if (statuses.Count > 0)
            list.Add(new(InsightLevel.Bad, $"{statuses.Count} request(s) return a different status: " +
                                           string.Join(", ", statuses.Take(5).Select(r => $"{r.Name} ({r.Left.StatusText} → {r.Right.StatusText})")) + "."));

        var bodies = report.Results.Where(r => r.BodyDiffers && !r.StatusDiffers).ToList();
        if (bodies.Count > 0)
            list.Add(new(InsightLevel.Warning, $"{bodies.Count} request(s) return the same status but a different body."));

        // Fields missing on one side are the differences that break clients; list the most common ones.
        var missing = report.Results.SelectMany(r => r.BodyChanges)
            .Where(c => c.Kind is JsonChangeKind.Removed or JsonChangeKind.Added or JsonChangeKind.TypeChanged)
            .GroupBy(c => (c.Kind, c.Path)).OrderByDescending(g => g.Count()).Take(5).ToList();
        if (missing.Count > 0)
            list.Add(new(InsightLevel.Warning, "Structural differences: " + string.Join(", ", missing.Select(g => g.Key.Kind switch
            {
                JsonChangeKind.Removed => $"{g.Key.Path} only on {report.LeftName}",
                JsonChangeKind.Added => $"{g.Key.Path} only on {report.RightName}",
                _ => $"{g.Key.Path} changes type"
            })) + "."));

        var checks = report.Results.Where(r => r.ChecksFailed).ToList();
        if (checks.Count > 0)
            list.Add(new(InsightLevel.Bad, $"{checks.Count} request(s) failed an assertion or script test: " +
                                           string.Join(", ", checks.Take(5).Select(r => r.Name)) + "."));

        var headers = report.Results.Where(r => r.HeaderChanges.Count > 0 && !r.StatusDiffers && !r.BodyDiffers).ToList();
        if (headers.Count > 0)
            list.Add(new(InsightLevel.Info, $"{headers.Count} request(s) differ only in headers " +
                                            $"(e.g. {string.Join(", ", headers.SelectMany(r => r.HeaderChanges).Select(h => h.Name).Distinct().Take(4))})."));

        var median = report.MedianLatencyDeltaPercent;
        var answered = report.Results.Where(r => r.Left.HasResponse && r.Right.HasResponse).ToList();
        if (answered.Count > 0 && Math.Abs(median) >= NotableLatencyPercent)
            list.Add(new(InsightLevel.Warning, median > 0
                ? $"{report.RightName} is slower: median latency {median:+0}% compared with {report.LeftName}."
                : $"{report.RightName} is faster: median latency {median:0}% compared with {report.LeftName}."));
        else if (answered.Count > 0)
            list.Add(new(InsightLevel.Good, $"Latency is comparable (median {median:+0;-0;0}%)."));
        var slowest = answered.Where(r => Math.Abs(r.LatencyDeltaMs) >= 20 && Math.Abs(r.LatencyDeltaPercent) >= NotableLatencyPercent)
            .MaxBy(r => Math.Abs(r.LatencyDeltaMs));
        if (slowest is not null)
            list.Add(new(InsightLevel.Info, $"Largest latency gap: {slowest.Name} ({slowest.Left.Milliseconds:0} ms vs {slowest.Right.Milliseconds:0} ms)."));
        return list;
    }

    public static string Text(CompareReport report, bool color = false)
    {
        string Paint(string s, string code) => color ? $"\u001b[{code}m{s}\u001b[0m" : s;
        var sb = new StringBuilder();
        var verdictColor = report.Verdict switch { "IDENTICAL" => "32", "DIFFERENT" => "33", _ => "31" };
        sb.AppendLine($"{Paint(report.Verdict, verdictColor)}  {report.LeftName} vs {report.RightName}: {report.Same} identical, {report.Different} different, " +
                      $"{report.Failed} failed · {report.Duration.TotalSeconds:0.0} s");
        if (report.IgnorePaths.Count > 0)
            sb.AppendLine($"ignoring {string.Join(", ", report.IgnorePaths)}");
        sb.AppendLine();
        foreach (var r in report.Results)
        {
            var mark = r.Outcome switch
            {
                CompareOutcome.Same => Paint("=", "32"),
                CompareOutcome.Different => Paint("≠", "33"),
                _ => Paint("✗", "31")
            };
            sb.AppendLine($"{mark} {r.Label,-9} {r.Name,-40} {r.Left.StatusText,-12} {r.Right.StatusText,-12} " +
                          $"{r.Left.Milliseconds,6:0} ms {r.Right.Milliseconds,6:0} ms ({r.LatencyDeltaPercent:+0;-0;0}%)  {r.Summary}");
            if (r.Outcome == CompareOutcome.Failed)
            {
                if (r.Left.Error is not null)
                    sb.AppendLine(Paint($"    left:  {r.Left.Error}", "31"));
                if (r.Right.Error is not null)
                    sb.AppendLine(Paint($"    right: {r.Right.Error}", "31"));
            }
            foreach (var change in r.BodyChanges.Take(12))
                sb.AppendLine("    " + Paint(change.ToString(), change.Kind == JsonChangeKind.Added ? "32" : change.Kind == JsonChangeKind.Removed ? "31" : "33"));
            if (r.BodyChanges.Count > 12)
                sb.AppendLine($"    … {r.BodyChanges.Count - 12} more");
            foreach (var header in r.HeaderChanges.Take(6))
                sb.AppendLine(Paint($"    header {header}", "36"));
            foreach (var (side, check) in r.Left.FailedChecks.Select(c => ("left", c)).Concat(r.Right.FailedChecks.Select(c => ("right", c))))
                sb.AppendLine(Paint($"    check failed on {side}: {check.Name}{(check.Message is null ? "" : $" — {check.Message}")}", "31"));
        }
        sb.AppendLine();
        foreach (var insight in Insights(report))
            sb.AppendLine((insight.Level switch
            {
                InsightLevel.Good => Paint("✓ ", "32"),
                InsightLevel.Warning => Paint("! ", "33"),
                InsightLevel.Bad => Paint("✗ ", "31"),
                _ => "· "
            }) + insight.Text);
        return sb.ToString();
    }

    public static string Html(CompareReport report)
    {
        var sb = Begin($"{report.LeftName} vs {report.RightName} · Dispatch environment comparison",
            $"Compare environments · {report.LeftName} vs {report.RightName}", report.Verdict,
            report.Verdict == "IDENTICAL" ? true : report.Verdict == "DIFFERENT" ? null : false);
        sb.Append($"<div class=\"muted\">Started {report.StartedAt:yyyy-MM-dd HH:mm:ss} · {report.Duration.TotalSeconds:0.0} s · {report.Results.Count} request(s)")
            .Append(report.IgnorePaths.Count > 0 ? $" · ignoring <code>{E(string.Join(", ", report.IgnorePaths))}</code>" : "").Append("</div>");

        sb.Append("<div class=\"stats\">")
            .Append(Stat("Identical", report.Same.ToString(CultureInfo.InvariantCulture), report.Same == report.Results.Count ? false : null))
            .Append(Stat("Different", report.Different.ToString(CultureInfo.InvariantCulture), report.Different > 0))
            .Append(Stat("Failed", report.Failed.ToString(CultureInfo.InvariantCulture), report.Failed > 0))
            .Append(Stat($"Latency {E(report.RightName)} vs {E(report.LeftName)}", $"{N(report.MedianLatencyDeltaPercent, "+0;-0;0")}%", hint: "median per request"))
            .Append("</div>");

        sb.Append("<h2>Insights</h2><ul class=\"ins\">");
        foreach (var i in Insights(report))
            sb.Append($"<li class=\"{i.Css}\">{E(i.Text)}</li>");
        sb.Append("</ul>");

        sb.Append("<h2>Per request</h2><div class=\"card\"><table><tr><th></th><th>Request</th>")
            .Append($"<th>{E(report.LeftName)}</th><th>{E(report.RightName)}</th><th class=\"r\">ms left</th><th class=\"r\">ms right</th><th class=\"r\">Δ</th><th>Differences</th></tr>");
        foreach (var r in report.Results)
        {
            var mark = r.Outcome switch
            {
                CompareOutcome.Same => "<span style=\"color:var(--ok)\">=</span>",
                CompareOutcome.Different => "<span style=\"color:var(--warn)\">≠</span>",
                _ => "<span style=\"color:var(--bad)\">✗</span>"
            };
            sb.Append($"<tr><td>{mark}</td><td><code>{E(r.Label)}</code> {E(r.Name)}</td>")
                .Append($"<td style=\"color:{StatusColour(r.Left.Status.ToString(CultureInfo.InvariantCulture))}\">{E(r.Left.StatusText)}</td>")
                .Append($"<td style=\"color:{StatusColour(r.Right.Status.ToString(CultureInfo.InvariantCulture))}\">{E(r.Right.StatusText)}</td>")
                .Append($"<td class=\"r\">{N(r.Left.Milliseconds)}</td><td class=\"r\">{N(r.Right.Milliseconds)}</td>")
                .Append($"<td class=\"r\">{N(r.LatencyDeltaPercent, "+0;-0;0")}%</td><td>{E(r.Summary)}</td></tr>");
            if (r.Outcome == CompareOutcome.Failed || r.BodyChanges.Count > 0 || r.HeaderChanges.Count > 0 || r.ChecksFailed)
            {
                foreach (var (side, check) in r.Left.FailedChecks.Select(c => ("left", c)).Concat(r.Right.FailedChecks.Select(c => ("right", c))))
                    sb.Append($"<div class=\"err\">check failed on {side}: {E(check.Name)}{(check.Message is null ? "" : $" — {E(check.Message)}")}</div>");
                sb.Append("<tr><td></td><td colspan=\"7\">");
                if (r.Left.Error is not null)
                    sb.Append($"<div class=\"err\">left: {E(r.Left.Error)}</div>");
                if (r.Right.Error is not null)
                    sb.Append($"<div class=\"err\">right: {E(r.Right.Error)}</div>");
                foreach (var change in r.BodyChanges.Take(50))
                    sb.Append($"<div><code style=\"color:{ChangeColour(change.Kind)}\">{E(change.ToString())}</code></div>");
                if (r.BodyChanges.Count > 50)
                    sb.Append($"<div class=\"muted\">… {r.BodyChanges.Count - 50} more</div>");
                foreach (var header in r.HeaderChanges)
                    sb.Append($"<div><code>header {E(header.ToString())}</code></div>");
                sb.Append("</td></tr>");
            }
        }
        sb.Append("</table></div>");
        return sb.Append(End).ToString();
    }

    private static string ChangeColour(JsonChangeKind kind) => kind switch
    {
        JsonChangeKind.Added => "var(--ok)",
        JsonChangeKind.Removed => "var(--bad)",
        _ => "var(--warn)"
    };

    public static string Json(CompareReport report)
    {
        static JsonObject Side(CompareSide s) => new()
        {
            ["status"] = s.HasResponse ? s.Status : null,
            ["ms"] = Math.Round(s.Milliseconds, 1),
            ["bytes"] = s.Bytes,
            ["error"] = s.Error,
            ["failedChecks"] = new JsonArray(s.FailedChecks.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["message"] = c.Message }).ToArray())
        };

        var root = new JsonObject
        {
            ["verdict"] = report.Verdict,
            ["left"] = report.LeftName,
            ["right"] = report.RightName,
            ["startedAt"] = report.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["durationMs"] = (long)report.Duration.TotalMilliseconds,
            ["ignorePaths"] = new JsonArray(report.IgnorePaths.Select(p => (JsonNode?)p).ToArray()),
            ["totals"] = new JsonObject
            {
                ["requests"] = report.Results.Count,
                ["identical"] = report.Same,
                ["different"] = report.Different,
                ["failed"] = report.Failed,
                ["medianLatencyDeltaPercent"] = Math.Round(report.MedianLatencyDeltaPercent, 1)
            },
            ["requests"] = new JsonArray(report.Results.Select(r => (JsonNode?)new JsonObject
            {
                ["name"] = r.Name,
                ["label"] = r.Label,
                ["outcome"] = r.Outcome.ToString().ToLowerInvariant(),
                ["summary"] = r.Summary,
                ["statusDiffers"] = r.StatusDiffers,
                ["checksFailed"] = r.ChecksFailed,
                ["left"] = Side(r.Left),
                ["right"] = Side(r.Right),
                ["latencyDeltaMs"] = Math.Round(r.LatencyDeltaMs, 1),
                ["latencyDeltaPercent"] = Math.Round(r.LatencyDeltaPercent, 1),
                ["bodyChanges"] = new JsonArray(r.BodyChanges.Select(c => (JsonNode?)new JsonObject
                {
                    ["kind"] = c.Kind.ToString().ToLowerInvariant(), ["path"] = c.Path, ["left"] = c.Left, ["right"] = c.Right
                }).ToArray()),
                ["lineChanges"] = r.LineChanges,
                ["headerChanges"] = new JsonArray(r.HeaderChanges.Select(h => (JsonNode?)new JsonObject
                {
                    ["name"] = h.Name, ["left"] = h.Left, ["right"] = h.Right
                }).ToArray())
            }).ToArray()),
            ["insights"] = new JsonArray(Insights(report).Select(i => (JsonNode?)new JsonObject { ["level"] = i.Css, ["text"] = i.Text }).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
