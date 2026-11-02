using System.Diagnostics;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Running;
using Dispatch.Application.Tls;
using Dispatch.Domain;

namespace Dispatch.Application.Monitoring;

public sealed record MonitorContext(RequestCollection Collection, ApiEnvironment? Environment);

/// <summary>Runs a monitor once: executes its requests, records a <see cref="MonitorRun"/>, and fires due alerts.</summary>
public sealed class MonitorService(CollectionRunner runner, IAlertSender? alerts = null, ITlsInspector? tls = null)
{
    private readonly ITlsInspector _tls = tls ?? new TlsInspector();

    public async Task<MonitorRun> RunOnceAsync(MonitorDefinition monitor, MonitorContext context, bool? previousPassed,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var requests = context.Collection.Requests.OrderBy(r => r.SortOrder).ToList();
        if (monitor.RequestNames.Count > 0)
            requests = requests.Where(r => monitor.RequestNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        var report = await runner.RunAsync(new RunOptions
        {
            Name = monitor.Name,
            Requests = requests,
            CollectionRequests = context.Collection.Requests,
            Environment = context.Environment,
            CollectionVariables = context.Collection.Variables,
            CollectionSpec = context.Collection.SpecLocation,
            RecordHistory = false
        }, cancellationToken: ct).ConfigureAwait(false);

        var average = report.AverageResponseTime.TotalMilliseconds;
        var slow = monitor.MaxAverageMs > 0 && average > monitor.MaxAverageMs;
        var certificateProblems = monitor.CertificateWarnDays > 0
            ? await CheckCertificatesAsync(monitor, report, ct).ConfigureAwait(false)
            : [];
        var passed = report.Passed && !slow && certificateProblems.Count == 0;
        var summary = report.Passed
            ? (slow ? $"All requests passed but average {average:0} ms exceeds the {monitor.MaxAverageMs} ms limit." : "All checks passed.")
            : $"{report.FailedRequests} of {report.TotalRequests} request(s) failed" +
              (report.FailedTests > 0 ? $", {report.FailedTests} test(s) failed." : ".");
        if (certificateProblems.Count > 0)
            summary = (report.Passed && !slow ? "" : summary + " ") + string.Join(" ", certificateProblems);

        var run = new MonitorRun
        {
            MonitorId = monitor.Id,
            DurationMs = stopwatch.Elapsed.TotalMilliseconds,
            Passed = passed,
            TotalRequests = report.TotalRequests,
            FailedRequests = report.FailedRequests,
            TotalTests = report.TotalTests,
            FailedTests = report.FailedTests,
            AverageMs = average,
            Summary = summary
        };

        if (alerts is not null)
            await DispatchAlertsAsync(monitor, run, previousPassed, ct).ConfigureAwait(false);
        return run;
    }

    /// <summary>
    /// Inspects every HTTPS host the run talked to and reports certificates that are expiring within the monitor's
    /// threshold, already expired, or otherwise unusable (untrusted chain, hostname mismatch, handshake failure).
    /// </summary>
    private async Task<List<string>> CheckCertificatesAsync(MonitorDefinition monitor, RunReport report, CancellationToken ct)
    {
        var problems = new List<string>();
        var targets = TlsTargets.FromUrls(report.Results.Select(r => (r.Request.Name, r.Response.EffectiveUrl ?? r.Request.Url)));
        foreach (var target in targets)
        {
            TlsReport tls;
            try
            {
                tls = await _tls.InspectAsync(target.Host, target.Port, new TlsInspectOptions { WarnDays = monitor.CertificateWarnDays }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"Certificate check for {target.Display} failed: {ex.Message}");
                continue;
            }
            if (tls.Error is not null)
                problems.Add($"TLS handshake with {target.Display} failed: {tls.Error}");
            else if (tls.DaysToExpiry is { } days && days < 0)
                problems.Add($"The certificate for {target.Display} expired {Math.Abs(days):0} day(s) ago.");
            else if (tls.DaysToExpiry is { } left && left <= monitor.CertificateWarnDays)
                problems.Add($"The certificate for {target.Display} expires in {left:0} day(s) (limit {monitor.CertificateWarnDays}).");
            else if (!tls.ChainTrusted || !tls.HostnameMatches)
                problems.Add($"The certificate for {target.Display} is {(tls.HostnameMatches ? "not trusted" : "not valid for the host")}.");
        }
        return problems;
    }

    private async Task DispatchAlertsAsync(MonitorDefinition monitor, MonitorRun run, bool? previousPassed, CancellationToken ct)
    {
        var recovered = run.Passed && previousPassed == false;
        foreach (var target in monitor.Alerts.Where(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Target)))
        {
            var shouldSend = target.Trigger switch
            {
                AlertTrigger.EveryRun => true,
                AlertTrigger.OnFailure => !run.Passed,
                AlertTrigger.OnChange => previousPassed != run.Passed, // first run (null) or a flip
                _ => false
            };
            if (!shouldSend)
                continue;
            var message = AlertFormatter.Build(monitor, run, recovered);
            try
            {
                await alerts!.SendAsync(target, message, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Alert to {target.Channel} failed: {ex.Message}");
            }
        }
    }
}
