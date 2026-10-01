using Dispatch.Application.Load;
using Dispatch.Application.Reporting;

namespace Dispatch.Application.Running;

/// <summary>Aggregated results of one request across all iterations of a run.</summary>
public sealed record RequestSummary(string Name, int Runs, int Passed, int Failed, int Tests, int FailedTests, LatencyStats Latency,
    IReadOnlyDictionary<string, int> Statuses)
{
    public double PassRate => Runs == 0 ? 0 : (double)Passed / Runs;
    public string StatusesText => string.Join("  ", Statuses.OrderBy(s => s.Key).Select(s => $"{s.Key}×{s.Value}"));
}

/// <summary>A failed assertion or failed request, flattened for the report.</summary>
public sealed record FailureDetail(string Request, int Iteration, string Check, string Message);

/// <summary>The "detailed report" view of a <see cref="RunReport"/>: per-request aggregates, failures, slowest calls, insights.</summary>
public sealed class RunSummary
{
    public required RunReport Report { get; init; }
    public IReadOnlyList<RequestSummary> Requests { get; init; } = [];
    public IReadOnlyList<FailureDetail> Failures { get; init; } = [];
    public IReadOnlyList<RequestRunResult> Slowest { get; init; } = [];
    public IReadOnlyDictionary<string, int> Statuses { get; init; } = new Dictionary<string, int>();
    public LatencyStats Latency { get; init; } = LatencyStats.From([]);
    public IReadOnlyList<ReportInsight> Insights { get; private set; } = [];

    public double PassRate => Report.TotalRequests == 0 ? 0 : 1 - (double)Report.FailedRequests / Report.TotalRequests;
    public double TestPassRate => Report.TotalTests == 0 ? 1 : 1 - (double)Report.FailedTests / Report.TotalTests;

    public static RunSummary From(RunReport report)
    {
        static string StatusOf(RequestRunResult r) => r.Response.HasResponse
            ? r.Response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "error";

        var requests = report.Results
            .GroupBy(r => r.Name)
            .Select(g => new RequestSummary(g.Key, g.Count(), g.Count(r => r.Passed), g.Count(r => !r.Passed),
                g.Sum(r => r.Response.TestResults.Count), g.Sum(r => r.Response.TestResults.Count(t => !t.Passed)),
                LatencyStats.From(g.Select(r => r.Response.Elapsed.TotalMilliseconds).ToList()),
                g.GroupBy(StatusOf).ToDictionary(x => x.Key, x => x.Count())))
            .ToList();

        var failures = new List<FailureDetail>();
        foreach (var r in report.Results.Where(r => !r.Passed))
        {
            var failedTests = r.Response.TestResults.Where(t => !t.Passed).ToList();
            if (!r.Response.HasResponse)
                failures.Add(new FailureDetail(r.Name, r.Iteration + 1, "Request", r.Response.Error ?? "No response"));
            else if (failedTests.Count == 0)
                failures.Add(new FailureDetail(r.Name, r.Iteration + 1, "Status", $"{r.Response.StatusCode} {r.Response.ReasonPhrase}".Trim()));
            failures.AddRange(failedTests.Select(t => new FailureDetail(r.Name, r.Iteration + 1, t.Name, t.Message ?? "failed")));
        }

        var latency = LatencyStats.From(report.Results.Select(r => r.Response.Elapsed.TotalMilliseconds).ToList());
        var summary = new RunSummary
        {
            Report = report,
            Requests = requests,
            Failures = failures,
            Slowest = report.Results.OrderByDescending(r => r.Response.Elapsed).Take(5).ToList(),
            Statuses = report.Results.GroupBy(StatusOf).ToDictionary(g => g.Key, g => g.Count()),
            Latency = latency
        };
        summary.Insights = BuildInsights(summary);
        return summary;
    }

    private static List<ReportInsight> BuildInsights(RunSummary s)
    {
        var report = s.Report;
        var list = new List<ReportInsight>();
        if (report.TotalRequests == 0)
        {
            list.Add(new(InsightLevel.Bad, "Nothing ran."));
            return list;
        }

        if (report.Passed)
            list.Add(new(InsightLevel.Good, report.TotalTests > 0
                ? $"All {report.TotalRequests} request(s) and {report.TotalTests} test(s) passed."
                : $"All {report.TotalRequests} request(s) succeeded. Add assertions to check more than the status code."));
        else
            list.Add(new(InsightLevel.Bad, $"{report.FailedRequests} of {report.TotalRequests} request(s) failed" +
                                           (report.FailedTests > 0 ? $", {report.FailedTests} of {report.TotalTests} test(s) failed." : ".")));

        if (report.Stopped)
            list.Add(new(InsightLevel.Warning, "The run stopped before every request ran."));

        if (report.Iterations > 1)
        {
            var flaky = s.Requests.Where(r => r.Passed > 0 && r.Failed > 0).ToList();
            if (flaky.Count > 0)
                list.Add(new(InsightLevel.Warning, $"Flaky across iterations: {string.Join(", ", flaky.Select(r => $"{r.Name} ({r.Passed}/{r.Runs} passed)"))}."));
        }

        var alwaysFailing = s.Requests.Where(r => r.Passed == 0).ToList();
        if (alwaysFailing.Count > 0)
            list.Add(new(InsightLevel.Bad, $"Always failing: {string.Join(", ", alwaysFailing.Select(r => r.Name))}."));

        var noResponse = report.Results.Count(r => !r.Response.HasResponse);
        if (noResponse > 0)
            list.Add(new(InsightLevel.Bad, $"{noResponse} request(s) got no response (connection, DNS or timeout errors)."));

        if (s.Requests.Count > 1)
        {
            var slowest = s.Requests.MaxBy(r => r.Latency.Mean)!;
            list.Add(new(InsightLevel.Info, $"Slowest request on average: {slowest.Name} ({slowest.Latency.Mean:0} ms)."));
        }
        var untested = s.Requests.Count(r => r.Tests == 0);
        if (untested > 0 && untested < s.Requests.Count)
            list.Add(new(InsightLevel.Info, $"{untested} request(s) have no tests; they pass on any 2xx/3xx status."));
        return list;
    }
}
