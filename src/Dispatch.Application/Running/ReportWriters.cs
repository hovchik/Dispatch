using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Dispatch.Application.Running;

/// <summary>Renders a <see cref="RunReport"/> as JUnit XML (for CI), a self-contained HTML page, or JSON.</summary>
public static class ReportWriters
{
    public static string JUnit(RunReport report)
    {
        static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

        var suites = new XElement("testsuites",
            new XAttribute("name", report.Name),
            new XAttribute("tests", report.Results.Sum(r => Math.Max(1, r.Response.TestResults.Count))),
            new XAttribute("failures", report.Results.Sum(FailureCount)),
            new XAttribute("time", Seconds(report.Duration)));

        foreach (var result in report.Results)
        {
            var name = report.Iterations > 1 ? $"{result.Name} [iteration {result.Iteration + 1}]" : result.Name;
            var response = result.Response;
            var suite = new XElement("testsuite",
                new XAttribute("name", name),
                new XAttribute("tests", Math.Max(1, response.TestResults.Count)),
                new XAttribute("failures", FailureCount(result)),
                new XAttribute("time", Seconds(response.Elapsed)),
                new XAttribute("timestamp", report.StartedAt.ToString("s", CultureInfo.InvariantCulture)));

            if (response.TestResults.Count == 0)
            {
                // No tests: the request itself is the test case (it must succeed).
                var testCase = new XElement("testcase", new XAttribute("name", "Request succeeds"), new XAttribute("classname", name),
                    new XAttribute("time", Seconds(response.Elapsed)));
                if (!result.Passed)
                    testCase.Add(new XElement("failure", new XAttribute("message", Status(response)), Status(response)));
                suite.Add(testCase);
            }
            foreach (var test in response.TestResults)
            {
                var testCase = new XElement("testcase", new XAttribute("name", test.Name), new XAttribute("classname", name),
                    new XAttribute("time", "0"));
                if (!test.Passed)
                    testCase.Add(new XElement("failure", new XAttribute("message", test.Message ?? "failed"), test.Message ?? ""));
                suite.Add(testCase);
            }
            if (!response.HasResponse && response.TestResults.Count > 0)
                suite.Add(new XElement("system-err", response.Error));
            suites.Add(suite);
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), suites).ToString(SaveOptions.None);
    }

    private static int FailureCount(RequestRunResult r) =>
        r.Response.TestResults.Count == 0 ? (r.Passed ? 0 : 1) : r.Response.TestResults.Count(t => !t.Passed);

    private static string Status(Dispatch.Domain.ApiResponse r) =>
        r.HasResponse ? $"{r.StatusCode} {r.ReasonPhrase}".Trim() : $"Error: {r.Error}";

    public static string Json(RunReport report)
    {
        var root = new JsonObject
        {
            ["name"] = report.Name,
            ["startedAt"] = report.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["durationMs"] = (long)report.Duration.TotalMilliseconds,
            ["iterations"] = report.Iterations,
            ["passed"] = report.Passed,
            ["stopped"] = report.Stopped,
            ["totals"] = new JsonObject
            {
                ["requests"] = report.TotalRequests,
                ["failedRequests"] = report.FailedRequests,
                ["tests"] = report.TotalTests,
                ["failedTests"] = report.FailedTests,
                ["averageResponseTimeMs"] = Math.Round(report.AverageResponseTime.TotalMilliseconds, 1)
            },
            ["results"] = new JsonArray(report.Results.Select(r => (JsonNode?)new JsonObject
            {
                ["iteration"] = r.Iteration + 1,
                ["name"] = r.Name,
                ["kind"] = r.Request.Kind.ToString(),
                ["url"] = r.Response.EffectiveUrl ?? r.Request.Url,
                ["passed"] = r.Passed,
                ["status"] = r.Response.StatusCode,
                ["statusText"] = r.Response.ReasonPhrase,
                ["error"] = r.Response.Error,
                ["timeMs"] = Math.Round(r.Response.Elapsed.TotalMilliseconds, 1),
                ["sizeBytes"] = r.Response.SizeBytes,
                ["tests"] = new JsonArray(r.Response.TestResults.Select(t => (JsonNode?)new JsonObject
                {
                    ["name"] = t.Name,
                    ["passed"] = t.Passed,
                    ["message"] = t.Message
                }).ToArray())
            }).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public static string Html(RunReport report)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        var status = report.Passed ? "passed" : "failed";
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append($"<title>{E(report.Name)} · Dispatch report</title><style>{HtmlStyles}</style></head><body>")
            .Append($"<h1>{E(report.Name)} <span class=\"badge {status}\">{status.ToUpperInvariant()}</span></h1>")
            .Append($"<div class=\"muted\">Started {report.StartedAt:yyyy-MM-dd HH:mm:ss} · {report.Duration.TotalSeconds:0.00} s · ")
            .Append($"{report.Iterations} iteration(s){(report.Stopped ? " · stopped early" : "")}</div>")
            .Append("<div class=\"stats\">")
            .Append(Stat("Requests", report.TotalRequests.ToString(CultureInfo.InvariantCulture), null))
            .Append(Stat("Failed requests", report.FailedRequests.ToString(CultureInfo.InvariantCulture), report.FailedRequests > 0))
            .Append(Stat("Tests", report.TotalTests.ToString(CultureInfo.InvariantCulture), null))
            .Append(Stat("Failed tests", report.FailedTests.ToString(CultureInfo.InvariantCulture), report.FailedTests > 0))
            .Append(Stat("Avg. response", $"{report.AverageResponseTime.TotalMilliseconds:0} ms", null))
            .Append("</div>");
        AppendSummary(sb, RunSummary.From(report));
        sb.Append("<h2>All results</h2><div class=\"filter\"><label><input type=\"checkbox\" id=\"onlyFailed\"> Show only failures</label></div>");

        static string Stat(string label, string value, bool? bad) =>
            $"<div class=\"stat\"><span class=\"muted\">{label}</span><b{(bad is null ? "" : $" style=\"color:var(--{(bad.Value ? "bad" : "ok")})\"")}>{value}</b></div>";

        foreach (var r in report.Results)
        {
            var passed = r.Passed;
            sb.Append($"<details class=\"result{(passed ? "" : " is-failed")}\"{(passed ? "" : " open")}><summary>");
            sb.Append($"<span class=\"badge {(passed ? "passed" : "failed")}\">{(passed ? "PASS" : "FAIL")}</span>");
            sb.Append($"<span class=\"name\">{E(r.Name)}{(report.Iterations > 1 ? $" <span class=\"muted\">#{r.Iteration + 1}</span>" : "")}</span>");
            sb.Append($"<code>{E(r.Request.Kind.ToString().ToUpperInvariant())} {E(r.Response.EffectiveUrl ?? r.Request.Url)}</code>");
            sb.Append($"<span class=\"muted\">{E(Status(r.Response))} · {r.Response.Elapsed.TotalMilliseconds:0} ms</span></summary><ul>");
            foreach (var t in r.Response.TestResults)
            {
                sb.Append($"<li class=\"{(t.Passed ? "pass" : "fail")}\">{(t.Passed ? "✓" : "✗")} {E(t.Name)}");
                if (!t.Passed && t.Message is not null)
                    sb.Append($"<span class=\"msg\">{E(t.Message)}</span>");
                sb.Append("</li>");
            }
            if (r.Response.TestResults.Count == 0)
                sb.Append($"<li class=\"muted\">No tests; the request {(passed ? "succeeded" : "failed")}.</li>");
            sb.Append("</ul></details>");
        }

        sb.Append("""
            <script>
            document.getElementById('onlyFailed').addEventListener('change', function (e) {
              document.querySelectorAll('details.result').forEach(function (d) {
                d.style.display = e.target.checked && !d.classList.contains('is-failed') ? 'none' : '';
              });
            });
            </script></body></html>
            """);
        return sb.ToString();
    }

    private static void AppendSummary(StringBuilder sb, RunSummary summary)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        sb.Append("<h2>Insights</h2><ul class=\"ins\">");
        foreach (var i in summary.Insights)
            sb.Append($"<li class=\"{i.Css}\">{E(i.Text)}</li>");
        sb.Append("</ul>");

        sb.Append("<h2>Per request</h2><table><tr><th>Request</th><th class=\"r\">Runs</th><th class=\"r\">Passed</th><th class=\"r\">Failed</th>")
            .Append("<th class=\"r\">Tests</th><th class=\"r\">avg</th><th class=\"r\">min</th><th class=\"r\">max</th><th>Statuses</th></tr>");
        foreach (var r in summary.Requests)
            sb.Append($"<tr><td>{E(r.Name)}</td><td class=\"r\">{r.Runs}</td><td class=\"r ok\">{r.Passed}</td>")
                .Append($"<td class=\"r{(r.Failed > 0 ? " bad" : "")}\">{r.Failed}</td><td class=\"r\">{r.Tests - r.FailedTests}/{r.Tests}</td>")
                .Append(FormattableString.Invariant($"<td class=\"r\">{r.Latency.Mean:0} ms</td><td class=\"r\">{r.Latency.Min:0} ms</td><td class=\"r\">{r.Latency.Max:0} ms</td>"))
                .Append($"<td>{E(r.StatusesText)}</td></tr>");
        sb.Append("</table>");

        if (summary.Failures.Count > 0)
        {
            sb.Append("<h2>Failures</h2><table><tr><th>Request</th><th class=\"r\">Iteration</th><th>Check</th><th>Message</th></tr>");
            foreach (var f in summary.Failures)
                sb.Append($"<tr><td>{E(f.Request)}</td><td class=\"r\">#{f.Iteration}</td><td>{E(f.Check)}</td><td class=\"bad\">{E(f.Message)}</td></tr>");
            sb.Append("</table>");
        }
    }

    private const string HtmlStyles = """
        :root{--bg:#fff;--fg:#1d1f24;--muted:#6b7280;--card:#f6f7f9;--line:#e5e7eb;--ok:#16a34a;--bad:#dc2626}
        @media (prefers-color-scheme:dark){:root{--bg:#16181d;--fg:#e8eaed;--muted:#9aa0a6;--card:#1f2228;--line:#2d3139}}
        *{box-sizing:border-box}
        body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,-apple-system,Segoe UI,sans-serif;padding:24px}
        h1{margin:0 0 4px;font-size:22px}.muted{color:var(--muted)}
        .stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin:20px 0}
        .stat{background:var(--card);border-radius:10px;padding:14px}.stat b{display:block;font-size:22px}
        .badge{display:inline-block;padding:2px 10px;border-radius:999px;font-weight:600;font-size:12px}
        .passed{background:#16a34a22;color:var(--ok)}.failed{background:#dc262622;color:var(--bad)}
        details{background:var(--card);border-radius:10px;margin:8px 0;padding:10px 14px}
        summary{cursor:pointer;display:flex;gap:12px;align-items:center;flex-wrap:wrap}
        summary .name{font-weight:600;flex:1;min-width:200px}
        ul{margin:8px 0 0;padding-left:0;list-style:none}li{padding:4px 0;border-top:1px solid var(--line)}
        li.fail{color:var(--bad)}li.pass{color:var(--ok)}.msg{color:var(--muted);white-space:pre-wrap;display:block;margin-left:22px}
        code{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:12px;word-break:break-all}
        .filter{margin:8px 0 16px}
        h2{font-size:15px;margin:28px 0 10px;text-transform:uppercase;letter-spacing:.04em;color:var(--muted)}
        table{width:100%;border-collapse:collapse;font-size:13px;background:var(--card);border-radius:10px;overflow:hidden}
        th,td{text-align:left;padding:6px 10px;border-bottom:1px solid var(--line)}th{color:var(--muted);font-size:12px}
        td.r,th.r{text-align:right;font-variant-numeric:tabular-nums}td.ok{color:var(--ok)}td.bad{color:var(--bad)}
        ul.ins{list-style:none;padding:0;margin:0}ul.ins li{padding:8px 12px;border-radius:8px;margin:6px 0;background:var(--card);border-left:4px solid var(--muted);border-top:0}
        ul.ins li.good{border-left-color:var(--ok)}ul.ins li.warning{border-left-color:#d97706}ul.ins li.bad{border-left-color:var(--bad)}
        """;

    /// <summary>A compact console summary, one line per request.</summary>
    public static string Text(RunReport report, bool color = false)
    {
        string Paint(string text, string code) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
        var sb = new StringBuilder();
        foreach (var r in report.Results)
        {
            var mark = r.Passed ? Paint("✓", "32") : Paint("✗", "31");
            var iteration = report.Iterations > 1 ? $"[{r.Iteration + 1}] " : "";
            sb.AppendLine($"{mark} {iteration}{r.Name}  {Paint(Status(r.Response), r.Response.IsSuccess ? "90" : "33")}  {r.Response.Elapsed.TotalMilliseconds:0} ms");
            foreach (var t in r.Response.TestResults.Where(t => !t.Passed))
                sb.AppendLine($"    {Paint("✗", "31")} {t.Name}: {t.Message}");
        }
        sb.AppendLine();
        sb.AppendLine($"{report.TotalRequests} requests, {report.FailedRequests} failed · {report.TotalTests} tests, {report.FailedTests} failed · {report.Duration.TotalSeconds:0.00} s");
        return sb.ToString();
    }
}
