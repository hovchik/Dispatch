using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.Impact;

/// <summary>Text, HTML and JSON renderings of an <see cref="ImpactReport"/>.</summary>
public static class ImpactReportWriter
{
    public static string Text(ImpactReport report, bool color = false)
    {
        string Paint(string s, string code) => color ? $"\u001b[{code}m{s}\u001b[0m" : s;
        var sb = new StringBuilder();
        sb.AppendLine($"Change impact · {report.RequestName} (compared with {report.BaselineName})");
        sb.AppendLine("  " + report.Summary);

        if (report.Changes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Response shape:");
            foreach (var change in report.Changes)
                sb.AppendLine("  " + (change.Kind == ShapeChangeKind.Added ? Paint("+ ", "32") : Paint("- ", "31")) + change);
        }

        foreach (var severity in new[] { ImpactSeverity.Breaks, ImpactSeverity.Possible })
        {
            var items = report.Items.Where(i => i.Severity == severity).ToList();
            if (items.Count == 0)
                continue;
            sb.AppendLine();
            sb.AppendLine(severity == ImpactSeverity.Breaks ? Paint($"Breaks ({items.Count}):", "31") : Paint($"Worth checking ({items.Count}):", "33"));
            foreach (var item in items)
            {
                sb.AppendLine($"  {item.Owner} · {item.Location}");
                sb.AppendLine($"      {item.Detail}");
                if (item.Suggestion is not null)
                    sb.AppendLine(Paint($"      → {item.Suggestion}", "36"));
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static string Html(ImpactReport report)
    {
        var breaks = report.Items.Count(i => i.Severity == ImpactSeverity.Breaks);
        var sb = Begin($"Change impact · {report.RequestName}", $"Change impact · {report.RequestName}",
            breaks > 0 ? $"{breaks} BREAKING" : report.Items.Count > 0 ? "CHECK" : "NO IMPACT", breaks > 0 ? false : report.Items.Count > 0 ? null : true);
        sb.Append($"<p class=\"muted\">Compared with {E(report.BaselineName)} · {report.CreatedAt:yyyy-MM-dd HH:mm:ss}</p>");
        sb.Append($"<p><b>{E(report.Summary)}</b></p>");
        sb.Append("<div class=\"stats\">")
            .Append(Stat("Shape changes", report.BreakingChanges.Count().ToString(CultureInfo.InvariantCulture), hint: $"{report.Changes.Count(c => c.Kind == ShapeChangeKind.Added)} added"))
            .Append(Stat("Tests", (report.Count(ImpactKind.Assertion, ImpactSeverity.Breaks) + report.Count(ImpactKind.Snapshot, ImpactSeverity.Breaks)).ToString(CultureInfo.InvariantCulture), bad: breaks > 0 ? true : null))
            .Append(Stat("Broken variables", report.BrokenVariables.Count.ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("Requests", report.Items.Where(i => i.Kind == ImpactKind.VariableUse).Select(i => i.Owner).Distinct().Count().ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("Flows", report.Count(ImpactKind.Flow).ToString(CultureInfo.InvariantCulture)))
            .Append("</div>");

        sb.Append("<h2>Response shape</h2><div class=\"card\"><ul>");
        foreach (var change in report.Changes)
            sb.Append($"<li style=\"color:var(--{(change.Kind == ShapeChangeKind.Added ? "ok" : "bad")})\"><code>{E(change.ToString())}</code></li>");
        if (report.Changes.Count == 0)
            sb.Append("<li class=\"muted\">No structural change.</li>");
        sb.Append("</ul></div>");

        if (report.Items.Count > 0)
        {
            sb.Append("<h2>Affected</h2><div class=\"card\"><table><thead><tr><th>Impact</th><th>Where</th><th>Why</th><th>Fix</th></tr></thead><tbody>");
            foreach (var item in report.Items.OrderBy(i => i.Severity))
                sb.Append($"<tr><td style=\"color:var(--{(item.Severity == ImpactSeverity.Breaks ? "bad" : "warn")})\"><b>{item.SeverityText}</b></td>" +
                          $"<td><b>{E(item.Owner)}</b><br><span class=\"muted\">{E(item.Location)}</span></td><td>{E(item.Detail)}</td><td>{E(item.Suggestion)}</td></tr>");
            sb.Append("</tbody></table></div>");
        }
        if (report.BrokenVariables.Count > 0)
        {
            sb.Append("<h2>Variables</h2><div class=\"card\"><ul>");
            foreach (var (name, cause) in report.BrokenVariables)
                sb.Append($"<li><code>{{{{{E(name)}}}}}</code> <span class=\"muted\">{E(cause)}</span></li>");
            sb.Append("</ul></div>");
        }
        sb.Append(End);
        return sb.ToString();
    }

    public static string Json(ImpactReport report)
    {
        var root = new JsonObject
        {
            ["request"] = report.RequestName,
            ["baseline"] = report.BaselineName,
            ["createdAt"] = report.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["summary"] = report.Summary,
            ["breaking"] = report.HasBreakingImpact,
            ["changes"] = new JsonArray(report.Changes.Select(c => (JsonNode)new JsonObject
            {
                ["kind"] = c.Kind.ToString(), ["path"] = c.Path, ["newPath"] = c.NewPath, ["oldType"] = c.OldType, ["newType"] = c.NewType
            }).ToArray()),
            ["items"] = new JsonArray(report.Items.Select(i => (JsonNode)new JsonObject
            {
                ["severity"] = i.Severity.ToString(), ["kind"] = i.Kind.ToString(), ["owner"] = i.Owner, ["location"] = i.Location,
                ["detail"] = i.Detail, ["suggestion"] = i.Suggestion
            }).ToArray()),
            ["brokenVariables"] = new JsonObject(report.BrokenVariables.Select(v => KeyValuePair.Create(v.Key, (JsonNode?)v.Value)))
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
