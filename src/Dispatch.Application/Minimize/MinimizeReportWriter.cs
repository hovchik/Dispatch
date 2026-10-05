using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Interop;
using Dispatch.Domain;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.Minimize;

/// <summary>Text, HTML and JSON renderings of a <see cref="MinimizeReport"/>.</summary>
public static class MinimizeReportWriter
{
    /// <summary>A one-line verdict, e.g. "HTTP 403 needs 2 part(s); 12 other(s) can be removed".</summary>
    public static string Summary(MinimizeReport report)
    {
        if (report.Error is not null && report.RequestsSent <= 1)
            return report.Error;
        var needed = RequiredLeaves(report).Count();
        var removed = TopRemoved(report).Count();
        return needed == 0
            ? $"{report.Outcome} happens without any of the {removed} removable parts"
            : $"{report.Outcome} needs {needed} part(s); {removed} other(s) can be removed";
    }

    public static string Text(MinimizeReport report, bool color = false)
    {
        string Paint(string s, string code) => color ? $"\u001b[{code}m{s}\u001b[0m" : s;
        var sb = new StringBuilder();
        sb.AppendLine($"Minimize · {report.Original.Name}");
        sb.AppendLine($"  outcome kept: {report.Outcome} (match: {report.Match})");
        sb.AppendLine($"  {Summary(report)} · {report.RequestsSent} requests in {report.Duration.TotalSeconds:0.0} s");
        if (report.Error is not null && report.RequestsSent > 1)
            sb.AppendLine($"  {report.Error}");
        if (report.BudgetExhausted)
            sb.AppendLine(Paint("  ! request budget ran out; the result may not be fully minimal (raise --max-requests)", "33"));
        if (report.RequestsSent > 1 && report.Error is null)
            sb.AppendLine(report.Confirmed
                ? Paint("  ✓ confirmed: the minimal request reproduces the outcome", "32")
                : Paint("  ! not confirmed: the minimal request gave a different outcome when re-sent (the endpoint may be flaky)", "33"));

        if (report.Required.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Required (removing any of these changes the outcome):");
            foreach (var unit in report.Required.Where(u => !HasRequiredChild(report, u)))
                sb.AppendLine("  " + Paint("● ", "32") + unit.Label);
        }
        if (report.Removed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Not needed ({report.Removed.Count}):");
            foreach (var unit in TopRemoved(report))
                sb.AppendLine("  " + Paint("○ ", "90") + unit.Label);
        }
        sb.AppendLine();
        sb.AppendLine("Minimal request:");
        sb.AppendLine(Curl(report.Minimal));
        return sb.ToString().TrimEnd();
    }

    /// <summary>The minimal request as a cURL command (with <c>{{placeholders}}</c> kept).</summary>
    public static string Curl(ApiRequest request) =>
        CodeGenerator.TargetsFor(request.Kind).Contains(CodeTarget.Curl) ? CodeGenerator.Generate(request, CodeTarget.Curl) : request.Url;

    /// <summary>Removed parts, without listing children of a part that was removed as a whole.</summary>
    public static IEnumerable<MinimizeUnit> TopRemoved(MinimizeReport report)
    {
        var removed = report.Removed.ToHashSet();
        var children = removed.SelectMany(u => u.Children).ToHashSet();
        return report.Removed.Where(u => !children.Contains(u));
    }

    /// <summary>True when a required part is only required because of one of its children (show the child instead).</summary>
    private static bool HasRequiredChild(MinimizeReport report, MinimizeUnit unit) => unit.Children.Any(report.Required.Contains);

    /// <summary>Required parts, most specific first (a JSON member rather than the whole body).</summary>
    public static IEnumerable<MinimizeUnit> RequiredLeaves(MinimizeReport report) => report.Required.Where(u => !HasRequiredChild(report, u));

    public static string Html(MinimizeReport report)
    {
        var sb = Begin($"Minimize · {report.Original.Name}", $"Minimize · {report.Original.Name}",
            report.Confirmed ? "CONFIRMED" : report.Error is not null ? "INCOMPLETE" : "UNCONFIRMED", report.Confirmed ? true : null);
        sb.Append($"<p class=\"muted\">{E(report.Original.Method.ToString().ToUpperInvariant())} {E(report.Original.Url)} · {report.StartedAt:yyyy-MM-dd HH:mm:ss}</p>");
        sb.Append($"<p><b>{E(Summary(report))}</b></p>");
        sb.Append("<div class=\"stats\">")
            .Append(Stat("Outcome kept", report.Outcome, hint: report.Match.ToString()))
            .Append(Stat("Required", report.Required.Count.ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("Removed", report.Removed.Count.ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("Requests sent", report.RequestsSent.ToString(CultureInfo.InvariantCulture), hint: $"{N(report.Duration.TotalSeconds, "0.0")} s"))
            .Append("</div>");
        if (report.BudgetExhausted)
            sb.Append("<ul class=\"ins\"><li class=\"warning\">The request budget ran out; the result may not be fully minimal.</li></ul>");

        sb.Append("<div class=\"grid2\"><div><h2>Required</h2><div class=\"card\"><ul>");
        foreach (var unit in RequiredLeaves(report))
            sb.Append($"<li><code>{E(unit.Label)}</code></li>");
        if (report.Required.Count == 0)
            sb.Append("<li class=\"muted\">Nothing — the outcome happens with a bare request.</li>");
        sb.Append("</ul></div></div><div><h2>Not needed</h2><div class=\"card\"><ul>");
        foreach (var unit in TopRemoved(report))
            sb.Append($"<li class=\"muted\"><code>{E(unit.Label)}</code></li>");
        sb.Append("</ul></div></div></div>");
        sb.Append($"<h2>Minimal request</h2><div class=\"card\"><pre>{E(Curl(report.Minimal))}</pre></div>");
        sb.Append(End);
        return sb.ToString();
    }

    public static string Json(MinimizeReport report)
    {
        var root = new JsonObject
        {
            ["request"] = report.Original.Name,
            ["url"] = report.Original.Url,
            ["startedAt"] = report.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["durationMs"] = Math.Round(report.Duration.TotalMilliseconds),
            ["outcome"] = report.Outcome,
            ["match"] = report.Match.ToString(),
            ["requestsSent"] = report.RequestsSent,
            ["confirmed"] = report.Confirmed,
            ["budgetExhausted"] = report.BudgetExhausted,
            ["error"] = report.Error,
            ["required"] = new JsonArray(RequiredLeaves(report).Select(u => (JsonNode)Unit(u)).ToArray()),
            ["removed"] = new JsonArray(TopRemoved(report).Select(u => (JsonNode)Unit(u)).ToArray()),
            ["curl"] = Curl(report.Minimal),
            ["minimal"] = JsonSerializer.SerializeToNode(report.Minimal, DispatchFormat.Options)
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Unit(MinimizeUnit unit) => new() { ["kind"] = unit.Kind.ToString(), ["label"] = unit.Label };
}
