using System.Text.Json.Nodes;
using Dispatch.Application.Capture;
using Dispatch.Application.Load;
using Dispatch.Application.Reporting;
using Dispatch.Application.Running;
using Dispatch.Domain;

namespace Dispatch.Tests;

public class LoadReportTests
{
    private static LoadReport Report(int requests, int errors, IReadOnlyList<LoadSecond>? timeline = null) => new()
    {
        StartedAt = DateTimeOffset.Now,
        Duration = TimeSpan.FromSeconds(10),
        PlannedDuration = TimeSpan.FromSeconds(10),
        VirtualUsers = 5,
        TotalRequests = requests,
        Errors = errors,
        TotalBytes = 2048,
        Latency = LatencyStats.From([10, 20, 30, 40, 400]),
        Statuses = new Dictionary<string, int> { ["200"] = requests - errors, ["500"] = errors },
        PerRequest =
        [
            new LoadRequestStats("Login <x>", requests / 2, 0, LatencyStats.From([10, 20]), new Dictionary<string, int> { ["200"] = requests / 2 }),
            new LoadRequestStats("Checkout", requests - requests / 2, errors, LatencyStats.From([300, 400]),
                new Dictionary<string, int> { ["200"] = requests - requests / 2 - errors, ["500"] = errors })
        ],
        Histogram = [new LatencyBucket("≤ 10 ms", 10, 0), new LatencyBucket("≤ 25 ms", 25, 2), new LatencyBucket("≤ 50 ms", 50, 0)],
        Timeline = timeline ?? []
    };

    [Theory]
    [InlineData(100, 0, "PASSED")]
    [InlineData(100, 2, "DEGRADED")]
    [InlineData(100, 10, "FAILED")]
    [InlineData(0, 0, "NO DATA")]
    public void Verdict_follows_the_error_rate(int requests, int errors, string expected) =>
        Assert.Equal(expected, LoadReportWriter.Verdict(Report(requests, errors)));

    [Fact]
    public void Insights_name_the_slowest_and_most_failing_steps_and_server_errors()
    {
        var insights = LoadReportWriter.Insights(Report(100, 10));

        Assert.Contains(insights, i => i.Level == InsightLevel.Bad && i.Text.Contains("10 of 100"));
        Assert.Contains(insights, i => i.Text.Contains("Slowest step: Checkout"));
        Assert.Contains(insights, i => i.Text.Contains("Most errors: Checkout"));
        Assert.Contains(insights, i => i.Text.Contains("server error"));
        Assert.Contains(insights, i => i.Level == InsightLevel.Warning && i.Text.Contains("Long tail"));
    }

    [Fact]
    public void Insights_spot_latency_growing_during_the_test()
    {
        var timeline = Enumerable.Range(0, 9).Select(s => new LoadSecond(s, 10, 0, s < 3 ? 50 : s >= 6 ? 200 : 100, 5)).ToList();
        var insights = LoadReportWriter.Insights(Report(90, 0, timeline));

        Assert.Contains(insights, i => i.Level == InsightLevel.Good && i.Text.Contains("All 90"));
        Assert.Contains(insights, i => i.Text.Contains("Latency grew") && i.Text.Contains("50 ms") && i.Text.Contains("200 ms"));
    }

    [Fact]
    public void Html_json_and_csv_contain_the_details()
    {
        var report = Report(100, 10);

        var html = LoadReportWriter.Html(report, "Shop");
        Assert.Contains("Login &lt;x&gt;", html);
        Assert.Contains("FAILED", html);
        Assert.Contains("Insights", html);

        var json = JsonNode.Parse(LoadReportWriter.Json(report, "Shop"))!;
        Assert.Equal("FAILED", json["verdict"]!.GetValue<string>());
        Assert.Equal(10, json["totals"]!["errors"]!.GetValue<int>());
        Assert.Equal(2, json["perRequest"]!.AsArray().Count);
        Assert.Equal(10, json["statuses"]!["500"]!.GetValue<int>());

        var csv = LoadReportWriter.Csv(report).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("request,requests,errors", csv[0]);
        Assert.Equal(4, csv.Length); // header, two requests, "(all)"
        Assert.StartsWith("(all),100,10,0.1,", csv[3]);
    }

    [Fact]
    public void Histogram_trim_drops_empty_edges()
    {
        var trimmed = LoadReportWriter.Trim(Report(10, 0).Histogram);
        Assert.Equal("≤ 25 ms", Assert.Single(trimmed).Label);
    }
}

public class RunSummaryTests
{
    [Fact]
    public void Summary_aggregates_per_request_and_lists_failures_and_flaky_requests()
    {
        var report = new RunReport { Name = "Smoke", StartedAt = DateTimeOffset.Now, Iterations = 2, Duration = TimeSpan.FromSeconds(1) };
        var login = new ApiRequest { Name = "Login" };
        var order = new ApiRequest { Name = "Order", Folder = "Shop" };
        report.Results.Add(new RequestRunResult(0, login, new ApiResponse { StatusCode = 200, Elapsed = TimeSpan.FromMilliseconds(10) }));
        report.Results.Add(new RequestRunResult(0, order, new ApiResponse
        {
            StatusCode = 200, Elapsed = TimeSpan.FromMilliseconds(30),
            TestResults = [new TestResult("has id", false, "expected id")]
        }));
        report.Results.Add(new RequestRunResult(1, login, ApiResponse.Failed("refused", TimeSpan.FromMilliseconds(5))));
        report.Results.Add(new RequestRunResult(1, order, new ApiResponse
        {
            StatusCode = 200, Elapsed = TimeSpan.FromMilliseconds(50), TestResults = [new TestResult("has id", true)]
        }));

        var summary = RunSummary.From(report);

        Assert.Equal(2, summary.Requests.Count);
        var orderRow = summary.Requests.Single(r => r.Name == "Shop/Order");
        Assert.Equal((2, 1, 1, 2, 1), (orderRow.Runs, orderRow.Passed, orderRow.Failed, orderRow.Tests, orderRow.FailedTests));
        Assert.Equal(40, orderRow.Latency.Mean);
        Assert.Contains(summary.Failures, f => f is { Request: "Shop/Order", Iteration: 1, Check: "has id", Message: "expected id" });
        Assert.Contains(summary.Failures, f => f is { Request: "Login", Iteration: 2, Check: "Request" });
        Assert.Equal(50, summary.Slowest[0].Response.Elapsed.TotalMilliseconds);
        Assert.Equal(1, summary.Statuses["error"]);
        Assert.Equal(0.5, summary.PassRate);
        Assert.Contains(summary.Insights, i => i.Text.Contains("Flaky") && i.Text.Contains("Shop/Order"));
        Assert.Contains(summary.Insights, i => i.Text.Contains("no response"));
    }

    [Fact]
    public void Html_report_includes_the_summary_tables()
    {
        var report = new RunReport { Name = "Smoke", StartedAt = DateTimeOffset.Now, Iterations = 1 };
        report.Results.Add(new RequestRunResult(0, new ApiRequest { Name = "Ping" }, new ApiResponse { StatusCode = 503, ReasonPhrase = "Unavailable" }));

        var html = ReportWriters.Html(report);

        Assert.Contains("Per request", html);
        Assert.Contains("Failures", html);
        Assert.Contains("503 Unavailable", html);
    }
}

public class CaptureSummaryTests
{
    private static CapturedExchange Exchange(string method, string url, int status, double ms, string? error = null, long size = 100) => new()
    {
        Method = method, Url = url, Secure = url.StartsWith("https"), StatusCode = status, ElapsedMs = ms, Error = error,
        ResponseSize = size, ResponseContentType = "application/json; charset=utf-8"
    };

    [Fact]
    public void Summary_groups_by_host_endpoint_status_and_method()
    {
        var summary = CaptureSummary.From(
        [
            Exchange("GET", "https://api.example.com/users", 200, 10),
            Exchange("GET", "https://api.example.com/users", 200, 30),
            Exchange("POST", "https://api.example.com/users", 401, 5),
            Exchange("GET", "http://cdn.example.com/app.js", 500, 900, size: 5000),
            Exchange("GET", "https://down.example.com/", 0, 1, error: "connection refused")
        ]);

        Assert.Equal(5, summary.Total);
        Assert.Equal(3, summary.Errors);
        Assert.Equal(4, summary.Secure);
        Assert.Equal(5400, summary.Bytes);
        Assert.Equal("api.example.com", summary.Hosts[0].Host);
        Assert.Equal(3, summary.Hosts[0].Requests);
        var users = summary.Endpoints.First();
        Assert.Equal(("GET", "/users", 2, 20.0), (users.Method, users.Path, users.Requests, users.Latency.Mean));
        Assert.Equal(["2xx", "4xx", "5xx", "Error"], summary.StatusClasses.Select(c => c.Label));
        Assert.Equal("GET", summary.Methods[0].Label);
        Assert.Equal("application/json", summary.ContentTypes[0].Label);
        Assert.Equal(900, summary.Slowest[0].ElapsedMs);
        Assert.Contains(summary.Insights, i => i.Text.Contains("plain HTTP"));
        Assert.Contains(summary.Insights, i => i.Text.Contains("unauthorised"));
        Assert.Contains(summary.Insights, i => i.Text.Contains("server error"));
    }

    [Fact]
    public void Report_renders_html_and_json()
    {
        var summary = CaptureSummary.From([Exchange("GET", "https://api.example.com/<script>", 200, 12)]);

        var html = summary.Html();
        Assert.Contains("NO ERRORS", html);
        Assert.DoesNotContain("<script>", html);
        var json = JsonNode.Parse(summary.Json())!;
        Assert.Equal(1, json["requests"]!.GetValue<int>());
        Assert.Equal("api.example.com", json["hosts"]![0]!["host"]!.GetValue<string>());
    }

    [Fact]
    public void Empty_capture_has_a_helpful_insight()
    {
        var summary = CaptureSummary.From([]);
        Assert.Equal(0, summary.Total);
        Assert.Contains("Nothing captured", Assert.Single(summary.Insights).Text);
    }
}
