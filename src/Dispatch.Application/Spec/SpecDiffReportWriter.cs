using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Impact;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.Spec;

/// <summary>Text, HTML and JSON renderings of a <see cref="SpecDiffReport"/>.</summary>
public static class SpecDiffReportWriter
{
    public static string Text(SpecDiffReport report, bool color = false)
    {
        string Paint(string s, string code) => color ? $"\u001b[{code}m{s}\u001b[0m" : s;
        var sb = new StringBuilder();
        sb.AppendLine($"Spec changes · {report.OldName} → {report.NewName}");
        sb.AppendLine("  " + report.Summary);
        foreach (var severity in new[] { ChangeSeverity.Breaking, ChangeSeverity.NonBreaking, ChangeSeverity.Info })
        {
            var changes = report.Changes.Where(c => c.Severity == severity).ToList();
            if (changes.Count == 0)
                continue;
            sb.AppendLine();
            sb.AppendLine(severity switch
            {
                ChangeSeverity.Breaking => Paint($"Breaking ({changes.Count}):", "31"),
                ChangeSeverity.NonBreaking => Paint($"Non-breaking ({changes.Count}):", "32"),
                _ => Paint($"Info ({changes.Count}):", "90")
            });
            foreach (var change in changes)
                sb.AppendLine($"  {change.Operation,-36} {change.Location,-22} {change.Description}");
        }
        if (report.CollectionName is not null)
        {
            sb.AppendLine();
            if (report.Impact.Count == 0)
                sb.AppendLine(Paint($"Nothing in {report.CollectionName} is affected.", "32"));
            foreach (var severity in new[] { ImpactSeverity.Breaks, ImpactSeverity.Possible })
            {
                var items = report.Impact.Where(i => i.Severity == severity).ToList();
                if (items.Count == 0)
                    continue;
                sb.AppendLine(severity == ImpactSeverity.Breaks ? Paint($"Breaks in {report.CollectionName} ({items.Count}):", "31") : Paint($"Worth checking ({items.Count}):", "33"));
                foreach (var item in items)
                {
                    sb.AppendLine($"  {item.Owner} · {item.Location}");
                    sb.AppendLine($"      {item.Detail}");
                    if (item.Suggestion is not null)
                        sb.AppendLine(Paint($"      → {item.Suggestion}", "36"));
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static string Html(SpecDiffReport report)
    {
        var breaking = report.Count(ChangeSeverity.Breaking);
        var sb = Begin($"Spec changes · {report.NewName}", $"Spec changes · {report.OldName} → {report.NewName}",
            breaking > 0 ? $"{breaking} BREAKING" : report.Changes.Count > 0 ? "COMPATIBLE" : "NO CHANGES", breaking == 0);
        sb.Append($"<p class=\"muted\">{report.CreatedAt:yyyy-MM-dd HH:mm:ss}</p><p><b>{E(report.Summary)}</b></p>");
        sb.Append("<div class=\"stats\">")
            .Append(Stat("Breaking", breaking.ToString(CultureInfo.InvariantCulture), breaking > 0 ? true : null))
            .Append(Stat("Non-breaking", report.Count(ChangeSeverity.NonBreaking).ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("Info", report.Count(ChangeSeverity.Info).ToString(CultureInfo.InvariantCulture)));
        if (report.CollectionName is not null)
            sb.Append(Stat("Breaks in collection", report.Impact.Count(i => i.Severity == ImpactSeverity.Breaks).ToString(CultureInfo.InvariantCulture),
                report.HasBreakingImpact ? true : null, report.CollectionName));
        sb.Append("</div>");

        sb.Append("<h2>Changes</h2><div class=\"card\"><table><thead><tr><th>Severity</th><th>Operation</th><th>Where</th><th>Change</th></tr></thead><tbody>");
        foreach (var change in report.Changes)
            sb.Append($"<tr><td style=\"color:var(--{(change.Severity == ChangeSeverity.Breaking ? "bad" : change.Severity == ChangeSeverity.NonBreaking ? "ok" : "muted")})\"><b>{E(change.SeverityText)}</b></td>" +
                      $"<td><code>{E(change.Operation)}</code></td><td class=\"muted\">{E(change.Location)}</td><td>{E(change.Description)}</td></tr>");
        if (report.Changes.Count == 0)
            sb.Append("<tr><td colspan=\"4\" class=\"muted\">No differences.</td></tr>");
        sb.Append("</tbody></table></div>");

        if (report.CollectionName is not null)
        {
            sb.Append($"<h2>Impact on {E(report.CollectionName)}</h2><div class=\"card\">");
            if (report.Impact.Count == 0)
                sb.Append("<p class=\"muted\">Nothing in the collection depends on what changed.</p>");
            else
            {
                sb.Append("<table><thead><tr><th>Impact</th><th>Where</th><th>Why</th><th>Fix</th></tr></thead><tbody>");
                foreach (var item in report.Impact)
                    sb.Append($"<tr><td style=\"color:var(--{(item.Severity == ImpactSeverity.Breaks ? "bad" : "warn")})\"><b>{E(item.SeverityText)}</b></td>" +
                              $"<td><b>{E(item.Owner)}</b><br><span class=\"muted\">{E(item.Location)}</span></td><td>{E(item.Detail)}<br><span class=\"muted\">{E(item.Change)}</span></td><td>{E(item.Suggestion)}</td></tr>");
                sb.Append("</tbody></table>");
            }
            sb.Append("</div>");
        }
        sb.Append(End);
        return sb.ToString();
    }

    public static string Json(SpecDiffReport report)
    {
        var root = new JsonObject
        {
            ["old"] = report.OldName,
            ["new"] = report.NewName,
            ["createdAt"] = report.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["summary"] = report.Summary,
            ["breaking"] = report.HasBreaking,
            ["counts"] = new JsonObject
            {
                ["breaking"] = report.Count(ChangeSeverity.Breaking),
                ["nonBreaking"] = report.Count(ChangeSeverity.NonBreaking),
                ["info"] = report.Count(ChangeSeverity.Info)
            },
            ["changes"] = new JsonArray(report.Changes.Select(c => (JsonNode)new JsonObject
            {
                ["severity"] = c.Severity.ToString(),
                ["category"] = c.Category,
                ["operation"] = c.Operation,
                ["location"] = c.Location,
                ["description"] = c.Description,
                ["path"] = c.Shape?.Path,
                ["newPath"] = c.Shape?.NewPath
            }).ToArray()),
            ["collection"] = report.CollectionName,
            ["impact"] = new JsonArray(report.Impact.Select(i => (JsonNode)new JsonObject
            {
                ["severity"] = i.Severity.ToString(),
                ["kind"] = i.Kind,
                ["owner"] = i.Owner,
                ["location"] = i.Location,
                ["detail"] = i.Detail,
                ["suggestion"] = i.Suggestion,
                ["change"] = i.Change
            }).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
