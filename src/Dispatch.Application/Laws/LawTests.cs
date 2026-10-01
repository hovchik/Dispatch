using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Domain;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.Laws;

/// <summary>Turns laws into tests on the saved requests they describe.</summary>
public static class LawTests
{
    /// <summary>
    /// The saved request for a law's endpoint: same method and the same path template, where one may have a prefix the
    /// other lacks (a collection's <c>{{base}}</c> often contains <c>/api</c>).
    /// </summary>
    public static ApiRequest? FindRequest(string endpoint, IEnumerable<ApiRequest> requests)
    {
        var space = endpoint.IndexOf(' ');
        if (space < 0)
            return null;
        var method = endpoint[..space];
        var path = Segments(endpoint[(space + 1)..]);
        return requests
            .Where(r => r.Kind == RequestKind.Http && r.Method.ToString().Equals(method, StringComparison.OrdinalIgnoreCase))
            .Select(r => (Request: r, Template: Segments(Observations.PathTemplate(r.Url))))
            .Where(c => IsSuffix(c.Template, path) || IsSuffix(path, c.Template))
            .OrderByDescending(c => c.Template.Count)
            .Select(c => c.Request)
            .FirstOrDefault();
    }

    private static List<string> Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool IsSuffix(List<string> shorter, List<string> longer) =>
        shorter.Count <= longer.Count && shorter.Count > 0 && longer.Skip(longer.Count - shorter.Count).SequenceEqual(shorter);

    /// <summary>Whether the request already checks this law (same assertion, or the script already contains its test).</summary>
    public static bool IsApplied(ApiLaw law, ApiRequest request) =>
        law.Assertion is { } a
            ? request.Assertions.Any(x => x.Source == a.Source && x.Path == a.Path && x.Operator == a.Operator && x.Expected == a.Expected)
            : law.Script is { } s && request.TestScript.Contains(FirstLine(s), StringComparison.Ordinal);

    /// <summary>Adds the law to the request as an assertion or test-script snippet. Returns false when it can't or already has it.</summary>
    public static bool Apply(ApiLaw law, ApiRequest request)
    {
        if (!law.CanBecomeTest || IsApplied(law, request))
            return false;
        if (law.Assertion is { } assertion)
        {
            request.Assertions.Add(assertion.Clone());
            return true;
        }
        var script = request.TestScript.TrimEnd();
        request.TestScript = (script.Length == 0 ? "" : script + "\n\n") + $"// {law.Description} (seen {law.Support}×)\n" + law.Script;
        return true;
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();
}

/// <summary>Text, HTML and JSON renderings of a <see cref="LawReport"/>.</summary>
public static class LawReportWriter
{
    public static string Text(LawReport report, bool color = false)
    {
        string Paint(string s, string code) => color ? $"\u001b[{code}m{s}\u001b[0m" : s;
        var sb = new StringBuilder();
        sb.AppendLine($"API laws · {report.Source}");
        sb.AppendLine("  " + report.Summary);
        var anomalies = report.Anomalies.ToList();
        if (anomalies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Paint($"Anomalies ({anomalies.Count}): rules that held except in a few cases, likely bugs", "31"));
            foreach (var law in anomalies)
            {
                sb.AppendLine($"  {law.Endpoint}  {law.Description}");
                foreach (var c in law.Counterexamples.Take(3))
                    sb.AppendLine(Paint($"      ✗ {c}", "33"));
            }
        }
        foreach (var group in report.Laws.Where(l => !l.IsAnomaly).GroupBy(l => l.Endpoint))
        {
            sb.AppendLine();
            sb.AppendLine(group.Key);
            foreach (var law in group)
                sb.AppendLine($"  {Paint("●", "32")} {law.Description}  {Paint($"[{law.Support}×, {law.Confidence.ToString().ToLowerInvariant()}]", "90")}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string Html(LawReport report)
    {
        var anomalies = report.Anomalies.ToList();
        var sb = Begin($"API laws · {report.Source}", $"API laws · {report.Source}",
            anomalies.Count > 0 ? $"{anomalies.Count} ANOMAL{(anomalies.Count == 1 ? "Y" : "IES")}" : report.Laws.Count > 0 ? "CONSISTENT" : "NO DATA",
            anomalies.Count > 0 ? false : report.Laws.Count > 0 ? true : null);
        sb.Append($"<p class=\"muted\">{report.CreatedAt:yyyy-MM-dd HH:mm:ss}</p><p><b>{E(report.Summary)}</b></p>");
        sb.Append("<div class=\"stats\">")
            .Append(Stat("Observations", report.Observations.ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("Endpoints", report.Endpoints.Count.ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("Laws", report.Laws.Count(l => !l.IsAnomaly).ToString(CultureInfo.InvariantCulture)))
            .Append(Stat("Anomalies", anomalies.Count.ToString(CultureInfo.InvariantCulture), bad: anomalies.Count > 0 ? true : null))
            .Append("</div>");
        if (anomalies.Count > 0)
        {
            sb.Append("<h2>Anomalies</h2><ul class=\"ins\">");
            foreach (var law in anomalies)
                sb.Append($"<li class=\"bad\"><b>{E(law.Endpoint)}</b> {E(law.Description)}<br><small class=\"muted\">{E(string.Join(" · ", law.Counterexamples.Take(3)))}</small></li>");
            sb.Append("</ul>");
        }
        foreach (var group in report.Laws.Where(l => !l.IsAnomaly).GroupBy(l => l.Endpoint))
        {
            sb.Append($"<h2>{E(group.Key)}</h2><div class=\"card\"><table><thead><tr><th>Law</th><th class=\"r\">Seen</th><th>Confidence</th></tr></thead><tbody>");
            foreach (var law in group)
                sb.Append($"<tr><td>{E(law.Description)}{(law.Advice is null ? "" : $"<br><small class=\"muted\">{E(law.Advice)}</small>")}</td>" +
                          $"<td class=\"r\">{law.Support}</td><td>{law.Confidence}</td></tr>");
            sb.Append("</tbody></table></div>");
        }
        sb.Append(End);
        return sb.ToString();
    }

    public static string Json(LawReport report)
    {
        var root = new JsonObject
        {
            ["source"] = report.Source,
            ["createdAt"] = report.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["observations"] = report.Observations,
            ["summary"] = report.Summary,
            ["laws"] = new JsonArray(report.Laws.Select(l => (JsonNode)new JsonObject
            {
                ["endpoint"] = l.Endpoint, ["kind"] = l.Kind.ToString(), ["description"] = l.Description, ["support"] = l.Support,
                ["confidence"] = l.Confidence.ToString(), ["anomaly"] = l.IsAnomaly,
                ["counterexamples"] = new JsonArray(l.Counterexamples.Select(c => (JsonNode)c).ToArray()),
                ["advice"] = l.Advice, ["script"] = l.Script
            }).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
