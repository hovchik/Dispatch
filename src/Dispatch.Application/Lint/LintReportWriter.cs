using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.Lint;

/// <summary>Text, HTML and JSON renderings of a <see cref="LintReport"/>.</summary>
public static class LintReportWriter
{
    public static string Text(LintReport report, bool color = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Lint · {report.CollectionName}");
        sb.AppendLine($"  {report.Summary}");
        foreach (var group in report.Problems.GroupBy(p => p.Owner))
        {
            sb.AppendLine();
            sb.AppendLine($"  {group.Key}");
            foreach (var p in group)
            {
                var severity = p.Severity switch
                {
                    LintSeverity.Error => color ? "\u001b[31merror\u001b[0m" : "error",
                    LintSeverity.Warning => color ? "\u001b[33mwarning\u001b[0m" : "warning",
                    _ => color ? "\u001b[36minfo\u001b[0m" : "info"
                };
                sb.AppendLine($"    {severity,-7} {p.RuleId} [{p.Location}] {p.Message}");
                if (p.Suggestion.Length > 0)
                    sb.AppendLine($"            fix: {p.Suggestion}");
            }
        }
        if (report.Problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Rules: " + string.Join(", ", report.Problems.GroupBy(p => p.RuleId).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key} {g.First().Title} ×{g.Count()}")));
        }
        return sb.ToString().TrimEnd();
    }

    public static string Html(LintReport report)
    {
        var sb = Begin($"Lint · {report.CollectionName}", $"Lint · {report.CollectionName}",
            report.Errors > 0 ? $"{report.Errors} ERROR" + (report.Errors > 1 ? "S" : "") : report.Warnings > 0 ? $"{report.Warnings} WARNING" + (report.Warnings > 1 ? "S" : "") : "CLEAN",
            report.Errors > 0 ? false : report.Warnings > 0 ? null : true);
        sb.Append($"<p class=\"muted\">{report.CreatedAt:yyyy-MM-dd HH:mm:ss} · {report.RulesChecked.Count} rule(s) checked</p>");
        sb.Append($"<p><b>{E(report.Summary)}</b></p>");
        sb.Append("<div class=\"stats\">")
            .Append(Stat("Errors", report.Errors.ToString(CultureInfo.InvariantCulture), report.Errors > 0))
            .Append(Stat("Warnings", report.Warnings.ToString(CultureInfo.InvariantCulture), report.Warnings > 0 ? null : false))
            .Append(Stat("Info", report.Infos.ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("Requests", report.RequestCount.ToString(CultureInfo.InvariantCulture)))
            .Append("</div>");

        if (report.Problems.Count > 0)
        {
            sb.Append("<h2>Problems</h2><div class=\"card\"><table><thead><tr><th>Severity</th><th>Rule</th><th>Where</th><th>Problem</th></tr></thead><tbody>");
            foreach (var p in report.Problems)
            {
                var css = p.Severity switch { LintSeverity.Error => "failed", LintSeverity.Warning => "warn", _ => "passed" };
                sb.Append($"<tr><td><span class=\"badge {css}\">{E(p.Severity.ToString().ToLowerInvariant())}</span></td>")
                    .Append($"<td title=\"{E(p.Title)}\">{E(p.RuleId)}</td>")
                    .Append($"<td>{E(p.Owner)}<br><small class=\"muted\">{E(p.Location)}</small></td>")
                    .Append($"<td>{E(p.Message)}{(p.Suggestion.Length > 0 ? $"<br><small class=\"muted\">Fix: {E(p.Suggestion)}</small>" : "")}</td></tr>");
            }
            sb.Append("</tbody></table></div>");
        }

        sb.Append("<h2>Rules</h2><div class=\"card\"><table><thead><tr><th>Id</th><th>Rule</th><th>Found</th></tr></thead><tbody>");
        foreach (var rule in report.RulesChecked)
            sb.Append($"<tr><td>{E(rule.Id)}</td><td>{E(rule.Title)}<br><small class=\"muted\">{E(rule.Description)}</small></td>")
                .Append($"<td>{report.Problems.Count(p => p.RuleId == rule.Id)}</td></tr>");
        sb.Append("</tbody></table></div>");
        sb.Append(End);
        return sb.ToString();
    }

    public static string Json(LintReport report)
    {
        var root = new JsonObject
        {
            ["collection"] = report.CollectionName,
            ["createdAt"] = report.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["requests"] = report.RequestCount,
            ["summary"] = report.Summary,
            ["errors"] = report.Errors,
            ["warnings"] = report.Warnings,
            ["info"] = report.Infos,
            ["rules"] = new JsonArray(report.RulesChecked.Select(r => (JsonNode?)new JsonObject
            {
                ["id"] = r.Id, ["title"] = r.Title, ["count"] = report.Problems.Count(p => p.RuleId == r.Id)
            }).ToArray()),
            ["problems"] = new JsonArray(report.Problems.Select(p => (JsonNode?)new JsonObject
            {
                ["rule"] = p.RuleId,
                ["title"] = p.Title,
                ["severity"] = p.Severity.ToString().ToLowerInvariant(),
                ["requestId"] = p.RequestId?.ToString(),
                ["request"] = p.RequestId is null ? null : p.RequestName,
                ["folder"] = p.Folder.Length > 0 ? p.Folder : null,
                ["location"] = p.Location,
                ["message"] = p.Message,
                ["suggestion"] = p.Suggestion.Length > 0 ? p.Suggestion : null
            }).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }
}
