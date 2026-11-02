using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.Spec;

/// <summary>Text, HTML and JSON renderings of a <see cref="CoverageReport"/>.</summary>
public static class CoverageReportWriter
{
    public static string Text(CoverageReport report, bool color = false, double? minimum = null)
    {
        string Paint(string s, string code) => color ? $"\u001b[{code}m{s}\u001b[0m" : s;
        string Grade(double percent) => Paint($"{percent,5:0.#}%", percent >= 80 ? "32" : percent >= 50 ? "33" : "31");
        var sb = new StringBuilder();
        sb.AppendLine($"Spec coverage · {report.SpecTitle} ← {report.Source} ({report.SampleCount} sample{(report.SampleCount == 1 ? "" : "s")})");
        sb.AppendLine($"  Overall {Grade(report.Overall)}" + (minimum is { } min ? (report.Passes(min) ? Paint($"  (≥ {min:0.#}% required: pass)", "32") : Paint($"  (≥ {min:0.#}% required: FAIL)", "31")) : ""));
        sb.AppendLine();
        foreach (var metric in report.Metrics)
            sb.AppendLine($"  {Grade(metric.Percent)}  {metric.Name,-24} {metric.Ratio}");

        foreach (var metric in report.Metrics.Where(m => m.Gaps.Count > 0))
        {
            sb.AppendLine();
            sb.AppendLine(Paint($"{metric.Name} · gaps ({metric.Gaps.Count}):", "33"));
            foreach (var gap in metric.Gaps.Take(40))
                sb.AppendLine($"  {gap.Operation,-40} {gap.Detail}" + (gap.Hint is null ? "" : Paint($"  ({gap.Hint})", "90")));
            if (metric.Gaps.Count > 40)
                sb.AppendLine($"  … {metric.Gaps.Count - 40} more");
        }
        return sb.ToString().TrimEnd();
    }

    public static string Html(CoverageReport report, double? minimum = null)
    {
        var good = minimum is { } min ? report.Passes(min) : report.Overall >= 80 ? true : report.Overall >= 50 ? (bool?)null : false;
        var sb = Begin($"Spec coverage · {report.SpecTitle}", $"Spec coverage · {report.SpecTitle}", $"{report.Overall:0.#}%", good);
        sb.Append($"<p class=\"muted\">Source: {E(report.Source)} · {report.SampleCount} sample(s) · {report.CreatedAt:yyyy-MM-dd HH:mm:ss}" +
                  (minimum is { } m ? $" · minimum {m:0.#}%" : "") + "</p>");
        sb.Append($"<p><b>{E(report.Summary)}</b></p>");
        sb.Append("<div class=\"stats\">");
        foreach (var metric in report.Metrics)
            sb.Append(Stat(metric.Name, metric.PercentText, metric.Total == 0 ? null : metric.Percent < 50, metric.Ratio));
        sb.Append("</div>");

        sb.Append("<h2>Metrics</h2><div class=\"card\">");
        foreach (var metric in report.Metrics)
            sb.Append(Bar(metric.Name, metric.Covered, metric.Total, metric.Percent >= 80 ? "#16a34a" : metric.Percent >= 50 ? "#d97706" : "#dc2626", $" / {metric.Total}"));
        sb.Append("</div>");

        foreach (var metric in report.Metrics.Where(m => m.Gaps.Count > 0))
        {
            sb.Append($"<h2>{E(metric.Name)} · gaps ({metric.Gaps.Count})</h2><p class=\"muted\">{E(metric.Description)}</p>");
            sb.Append("<div class=\"card\"><table><thead><tr><th>Operation</th><th>Gap</th><th>Note</th></tr></thead><tbody>");
            foreach (var gap in metric.Gaps)
                sb.Append($"<tr><td><code>{E(gap.Operation)}</code></td><td>{E(gap.Detail)}</td><td class=\"muted\">{E(gap.Hint)}</td></tr>");
            sb.Append("</tbody></table></div>");
        }

        sb.Append("<h2>Operations</h2><div class=\"card\"><table><thead><tr><th>Operation</th><th class=\"r\">Calls</th><th>Statuses seen</th><th class=\"r\">Documented</th><th class=\"r\">Params varied</th></tr></thead><tbody>");
        foreach (var row in report.Operations)
            sb.Append($"<tr><td><code>{E(row.Key)}</code>{(row.Deprecated ? " <span class=\"muted\">(deprecated)</span>" : "")}</td>" +
                      $"<td class=\"r\"{(row.Calls == 0 ? " style=\"color:var(--bad)\"" : "")}>{row.Calls}</td><td>{E(row.StatusesText)}</td>" +
                      $"<td class=\"r\">{row.StatusesDocumented}</td><td class=\"r\">{row.ParametersVaried}/{row.ParametersTotal}</td></tr>");
        sb.Append("</tbody></table></div>");
        sb.Append(End);
        return sb.ToString();
    }

    public static string Json(CoverageReport report, double? minimum = null)
    {
        var root = new JsonObject
        {
            ["spec"] = report.SpecTitle,
            ["source"] = report.Source,
            ["createdAt"] = report.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["samples"] = report.SampleCount,
            ["overall"] = report.Overall,
            ["minimum"] = minimum,
            ["passed"] = minimum is { } min ? report.Passes(min) : null,
            ["summary"] = report.Summary,
            ["metrics"] = new JsonArray(report.Metrics.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id.ToString(),
                ["name"] = m.Name,
                ["covered"] = m.Covered,
                ["total"] = m.Total,
                ["percent"] = m.Percent,
                ["gaps"] = new JsonArray(m.Gaps.Select(g => (JsonNode)new JsonObject { ["operation"] = g.Operation, ["detail"] = g.Detail, ["hint"] = g.Hint }).ToArray())
            }).ToArray()),
            ["operations"] = new JsonArray(report.Operations.Select(o => (JsonNode)new JsonObject
            {
                ["operation"] = o.Key,
                ["calls"] = o.Calls,
                ["statusesSeen"] = new JsonArray(o.StatusesSeen.OrderBy(s => s).Select(s => (JsonNode)s).ToArray()),
                ["statusesDocumented"] = o.StatusesDocumented,
                ["parametersVaried"] = o.ParametersVaried,
                ["parametersTotal"] = o.ParametersTotal,
                ["deprecated"] = o.Deprecated
            }).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
