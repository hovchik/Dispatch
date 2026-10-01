using Dispatch.Domain;

namespace Dispatch.Application.Monitoring;

/// <summary>A message to deliver through an alert channel.</summary>
public sealed record AlertMessage(string MonitorName, bool Passed, bool Recovered, string Title, string Body, MonitorRun Run);

/// <summary>Delivers an alert to one target (webhook, Slack or email). Implemented in Infrastructure.</summary>
public interface IAlertSender
{
    Task SendAsync(AlertTarget target, AlertMessage message, CancellationToken ct);
}

public static class AlertFormatter
{
    public static AlertMessage Build(MonitorDefinition monitor, MonitorRun run, bool recovered)
    {
        var state = run.Passed ? (recovered ? "recovered" : "passed") : "failed";
        var icon = run.Passed ? (recovered ? "✅" : "✅") : "🔴";
        var title = $"{icon} Monitor \"{monitor.Name}\" {state}";
        var body = $"{run.Summary}\n" +
                   $"Requests: {run.TotalRequests - run.FailedRequests}/{run.TotalRequests} passed\n" +
                   (run.TotalTests > 0 ? $"Tests: {run.TotalTests - run.FailedTests}/{run.TotalTests} passed\n" : "") +
                   $"Average: {run.AverageMs:0} ms · duration {run.DurationMs / 1000:0.0} s\n" +
                   $"At: {run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        return new AlertMessage(monitor.Name, run.Passed, recovered, title, body, run);
    }

    /// <summary>Slack "Block Kit" JSON for the message.</summary>
    public static string SlackPayload(AlertMessage message)
    {
        var text = $"{message.Title}\n{message.Body}";
        return new System.Text.Json.Nodes.JsonObject
        {
            ["text"] = message.Title,
            ["blocks"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "section",
                    ["text"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "mrkdwn", ["text"] = text }
                })
        }.ToJsonString();
    }

    /// <summary>Generic webhook JSON with the full run detail.</summary>
    public static string WebhookPayload(AlertMessage message)
    {
        var run = message.Run;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            monitor = message.MonitorName,
            status = message.Passed ? "passed" : "failed",
            recovered = message.Recovered,
            title = message.Title,
            summary = run.Summary,
            startedAt = run.StartedAt,
            durationMs = run.DurationMs,
            averageMs = run.AverageMs,
            totalRequests = run.TotalRequests,
            failedRequests = run.FailedRequests,
            totalTests = run.TotalTests,
            failedTests = run.FailedTests
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
    }
}
