using System.Diagnostics;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Running;
using Dispatch.Domain;

namespace Dispatch.Application.Monitoring;

public sealed record MonitorContext(RequestCollection Collection, ApiEnvironment? Environment);

/// <summary>Runs a monitor once: executes its requests, records a <see cref="MonitorRun"/>, and fires due alerts.</summary>
public sealed class MonitorService(CollectionRunner runner, IAlertSender? alerts = null)
{
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
            Environment = context.Environment,
            CollectionVariables = context.Collection.Variables,
            CollectionSpec = context.Collection.SpecLocation,
            RecordHistory = false
        }, cancellationToken: ct).ConfigureAwait(false);

        var average = report.AverageResponseTime.TotalMilliseconds;
        var slow = monitor.MaxAverageMs > 0 && average > monitor.MaxAverageMs;
        var passed = report.Passed && !slow;
        var summary = report.Passed
            ? (slow ? $"All requests passed but average {average:0} ms exceeds the {monitor.MaxAverageMs} ms limit." : "All checks passed.")
            : $"{report.FailedRequests} of {report.TotalRequests} request(s) failed" +
              (report.FailedTests > 0 ? $", {report.FailedTests} test(s) failed." : ".");

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
