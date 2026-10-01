using System.Net;
using System.Text;

namespace Dispatch.Application.Security;

/// <summary>Renders a scan report as a console summary, HTML or JSON.</summary>
public static class ScanReportWriter
{
    public static string Text(ScanReport report, bool color = false)
    {
        string Paint(string text, string code) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
        var sb = new StringBuilder();
        foreach (var finding in report.Ordered)
        {
            var tag = finding.Severity switch
            {
                ScanSeverity.High => Paint("HIGH  ", "31"),
                ScanSeverity.Medium => Paint("MEDIUM", "33"),
                ScanSeverity.Low => Paint("LOW   ", "36"),
                _ => Paint("INFO  ", "90")
            };
            sb.AppendLine($"{tag} [{finding.CategoryText}] {finding.RequestName}: {finding.Title}");
            sb.AppendLine($"       {finding.Detail}");
            if (finding.Evidence.Length > 0)
                sb.AppendLine(Paint($"       evidence: {finding.Evidence}", "90"));
            if (finding.Remediation.Length > 0)
                sb.AppendLine(Paint($"       fix: {finding.Remediation}", "90"));
        }
        sb.AppendLine();
        sb.AppendLine($"{report.RequestsScanned} request(s) scanned, {report.ProbesSent} probe(s) sent in {report.Duration.TotalSeconds:0.0} s.");
        sb.Append($"Findings: {report.Count(ScanSeverity.High)} high, {report.Count(ScanSeverity.Medium)} medium, " +
                  $"{report.Count(ScanSeverity.Low)} low, {report.Count(ScanSeverity.Info)} info.");
        return sb.ToString();
    }

    public static string Json(ScanReport report)
    {
        var findings = report.Ordered.Select(f => new
        {
            severity = f.SeverityText,
            category = f.CategoryText,
            request = f.RequestName,
            title = f.Title,
            detail = f.Detail,
            evidence = f.Evidence,
            remediation = f.Remediation
        });
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            startedAt = report.StartedAt,
            durationMs = report.Duration.TotalMilliseconds,
            requestsScanned = report.RequestsScanned,
            probesSent = report.ProbesSent,
            summary = new
            {
                high = report.Count(ScanSeverity.High),
                medium = report.Count(ScanSeverity.Medium),
                low = report.Count(ScanSeverity.Low),
                info = report.Count(ScanSeverity.Info)
            },
            findings
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    public static string Html(ScanReport report, string title)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append($"<title>{E(title)} · Security scan</title>\n<style>{Css}</style></head><body>\n");
        sb.Append($"<h1>Security scan — {E(title)}</h1>\n");
        sb.Append($"<p class=\"meta\">{report.StartedAt:yyyy-MM-dd HH:mm} · {report.RequestsScanned} request(s), {report.ProbesSent} probe(s), {report.Duration.TotalSeconds:0.0}s</p>\n");
        sb.Append("<div class=\"cards\">")
            .Append(Card("High", report.Count(ScanSeverity.High), "high"))
            .Append(Card("Medium", report.Count(ScanSeverity.Medium), "medium"))
            .Append(Card("Low", report.Count(ScanSeverity.Low), "low"))
            .Append(Card("Info", report.Count(ScanSeverity.Info), "info"))
            .Append("</div>\n");

        if (report.Findings.Count == 0)
            sb.Append("<p class=\"none\">No findings. Note that automated scanning is not exhaustive.</p>\n");

        foreach (var f in report.Ordered)
        {
            sb.Append($"<section class=\"finding {f.Severity.ToString().ToLowerInvariant()}\">\n")
                .Append($"<h3><span class=\"pill {f.Severity.ToString().ToLowerInvariant()}\">{f.SeverityText}</span> {E(f.Title)}</h3>\n")
                .Append($"<p class=\"meta\">{E(f.CategoryText)} · <strong>{E(f.RequestName)}</strong></p>\n")
                .Append($"<p>{E(f.Detail)}</p>\n");
            if (f.Evidence.Length > 0)
                sb.Append($"<pre>{E(f.Evidence)}</pre>\n");
            if (f.Remediation.Length > 0)
                sb.Append($"<p class=\"fix\"><strong>Fix:</strong> {E(f.Remediation)}</p>\n");
            sb.Append("</section>\n");
        }
        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    private static string Card(string label, int count, string cls) =>
        $"<div class=\"card {cls}\"><div class=\"n\">{count}</div><div class=\"l\">{label}</div></div>";

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private const string Css = """
        :root{--bg:#fff;--fg:#1f2328;--muted:#656d76;--line:#d0d7de;--card:#f6f8fa}
        @media (prefers-color-scheme:dark){:root{--bg:#0d1117;--fg:#e6edf3;--muted:#8d96a0;--line:#30363d;--card:#161b22}}
        body{font:15px/1.55 system-ui,sans-serif;color:var(--fg);background:var(--bg);margin:0;padding:24px 40px;max-width:960px}
        .cards{display:flex;gap:12px;margin:16px 0 24px}.card{flex:1;border:1px solid var(--line);border-radius:10px;padding:12px;text-align:center}
        .card .n{font-size:28px;font-weight:700}.card .l{color:var(--muted);font-size:13px}
        .card.high .n{color:#cf222e}.card.medium .n{color:#9a6700}.card.low .n{color:#0969da}
        .finding{border:1px solid var(--line);border-left-width:4px;border-radius:8px;padding:4px 18px 12px;margin:14px 0}
        .finding.high{border-left-color:#cf222e}.finding.medium{border-left-color:#9a6700}.finding.low{border-left-color:#0969da}.finding.info{border-left-color:#6e7781}
        .pill{display:inline-block;color:#fff;border-radius:5px;padding:1px 7px;font-size:12px;font-weight:600;vertical-align:middle}
        .pill.high{background:#cf222e}.pill.medium{background:#9a6700}.pill.low{background:#0969da}.pill.info{background:#6e7781}
        pre{background:var(--card);border:1px solid var(--line);border-radius:6px;padding:8px 10px;overflow:auto;font:12px ui-monospace,monospace}
        .meta{color:var(--muted);font-size:13px}.fix{color:var(--muted)}.none{color:var(--muted)}h1{margin-bottom:4px}
        """;
}
