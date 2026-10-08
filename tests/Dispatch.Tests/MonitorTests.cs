using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Monitoring;
using Dispatch.Application.Requests;
using Dispatch.Application.Running;
using Dispatch.Domain;

namespace Dispatch.Tests;

public class CronScheduleTests
{
    [Theory]
    [InlineData("*/5 * * * *", "2026-01-01T10:05:00", true)]
    [InlineData("*/5 * * * *", "2026-01-01T10:06:00", false)]
    [InlineData("0 9 * * *", "2026-01-01T09:00:00", true)]
    [InlineData("0 9 * * *", "2026-01-01T09:01:00", false)]
    [InlineData("0 9 * * 1-5", "2026-01-02T09:00:00", true)]   // Friday
    [InlineData("0 9 * * 1-5", "2026-01-03T09:00:00", false)]  // Saturday
    [InlineData("30 8,17 * * *", "2026-01-01T17:30:00", true)]
    [InlineData("0 0 1 1 *", "2026-01-01T00:00:00", true)]
    public void Matches_expected_times(string cron, string time, bool expected) =>
        Assert.Equal(expected, CronSchedule.Parse(cron).Matches(DateTimeOffset.Parse(time)));

    [Fact]
    public void Next_finds_the_following_slot()
    {
        var next = CronSchedule.Parse("*/15 * * * *").Next(DateTimeOffset.Parse("2026-01-01T10:07:00"));
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T10:15:00"), next);
    }

    [Theory]
    [InlineData("* * * *")]       // too few fields
    [InlineData("60 * * * *")]    // minute out of range
    [InlineData("* 24 * * *")]    // hour out of range
    [InlineData("*/0 * * * *")]   // bad step
    [InlineData("99999999999 * * * *")]   // overflow
    [InlineData("1-99999999999 * * * *")] // overflow in a range
    [InlineData("*/99999999999 * * * *")] // overflow in a step
    public void Rejects_invalid_expressions(string cron) =>
        Assert.False(CronSchedule.TryParse(cron, out _));
}

public class MonitorSchedulerTests
{
    [Fact]
    public void Interval_runs_immediately_then_every_n_minutes()
    {
        var monitor = new MonitorDefinition { IntervalMinutes = 5 };
        var scheduler = new MonitorScheduler();
        var t0 = DateTimeOffset.Parse("2026-01-01T10:00:00");
        scheduler.Prime(monitor, t0);

        Assert.True(scheduler.IsDue(monitor, t0));                 // first tick: due now
        Assert.False(scheduler.IsDue(monitor, t0.AddMinutes(3)));  // not yet
        Assert.True(scheduler.IsDue(monitor, t0.AddMinutes(5)));   // next window
        Assert.False(scheduler.IsDue(monitor, t0.AddMinutes(6)));
    }

    [Fact]
    public void Tracks_last_result()
    {
        var scheduler = new MonitorScheduler();
        var id = Guid.NewGuid();
        Assert.Null(scheduler.LastResult(id));
        scheduler.Record(id, false);
        Assert.False(scheduler.LastResult(id));
    }
}

internal sealed class RecordingAlertSender : IAlertSender
{
    public List<(AlertTarget Target, AlertMessage Message)> Sent { get; } = [];
    public Task SendAsync(AlertTarget target, AlertMessage message, CancellationToken ct)
    {
        Sent.Add((target, message));
        return Task.CompletedTask;
    }
}

public class MonitorServiceTests
{
    private static (MonitorService Service, RecordingAlertSender Alerts) Build(bool pass)
    {
        var sender = new RequestSender([new FakeExecutor(_ => pass
            ? new ApiResponse { StatusCode = 200, Succeeded = true }
            : new ApiResponse { StatusCode = 500, Succeeded = false })], new NullHistory());
        var alerts = new RecordingAlertSender();
        return (new MonitorService(new CollectionRunner(sender), alerts), alerts);
    }

    private static MonitorContext Context() => new(new RequestCollection
    {
        Name = "C", Requests = [new ApiRequest { Name = "Ping", Url = "http://x/ping" }]
    }, null);

    [Fact]
    public async Task OnFailure_alerts_only_when_failing()
    {
        var (service, alerts) = Build(pass: false);
        var monitor = new MonitorDefinition { Name = "M", Alerts = [new AlertTarget { Channel = AlertChannel.Slack, Trigger = AlertTrigger.OnFailure, Target = "https://hooks/x" }] };
        var run = await service.RunOnceAsync(monitor, Context(), previousPassed: true);
        Assert.False(run.Passed);
        Assert.Single(alerts.Sent);
        Assert.False(alerts.Sent[0].Message.Passed);

        var (okService, okAlerts) = Build(pass: true);
        await okService.RunOnceAsync(monitor, Context(), previousPassed: false);
        Assert.Empty(okAlerts.Sent); // passing run, OnFailure → silent
    }

    [Fact]
    public async Task OnChange_alerts_on_failure_and_once_on_recovery()
    {
        var monitor = new MonitorDefinition { Name = "M", Alerts = [new AlertTarget { Channel = AlertChannel.Webhook, Trigger = AlertTrigger.OnChange, Target = "https://hooks/x" }] };

        var (failService, failAlerts) = Build(pass: false);
        await failService.RunOnceAsync(monitor, Context(), previousPassed: true);  // was passing, now failing → alert
        Assert.Single(failAlerts.Sent);

        var (stillService, stillAlerts) = Build(pass: false);
        await stillService.RunOnceAsync(monitor, Context(), previousPassed: false); // still failing → no alert
        Assert.Empty(stillAlerts.Sent);

        var (recoverService, recoverAlerts) = Build(pass: true);
        await recoverService.RunOnceAsync(monitor, Context(), previousPassed: false); // recovered → alert
        Assert.Single(recoverAlerts.Sent);
        Assert.True(recoverAlerts.Sent[0].Message.Recovered);
    }

    [Fact]
    public async Task Slow_responses_fail_the_monitor_against_the_average_limit()
    {
        var sender = new RequestSender([new FakeExecutor(_ => new ApiResponse { StatusCode = 200, Succeeded = true, Elapsed = TimeSpan.FromMilliseconds(400) })], new NullHistory());
        var service = new MonitorService(new CollectionRunner(sender), new RecordingAlertSender());
        var monitor = new MonitorDefinition { Name = "M", MaxAverageMs = 100 };
        var run = await service.RunOnceAsync(monitor, Context(), null);
        Assert.False(run.Passed);
        Assert.Contains("exceeds", run.Summary);
    }

    [Fact]
    public void Alert_payloads_are_well_formed()
    {
        var run = new MonitorRun { Summary = "All checks passed.", Passed = true, TotalRequests = 2, AverageMs = 50 };
        var msg = AlertFormatter.Build(new MonitorDefinition { Name = "Prod health" }, run, recovered: false);
        var slack = System.Text.Json.Nodes.JsonNode.Parse(AlertFormatter.SlackPayload(msg))!;
        Assert.Contains("Prod health", slack["text"]!.GetValue<string>());
        var webhook = System.Text.Json.Nodes.JsonNode.Parse(AlertFormatter.WebhookPayload(msg))!;
        Assert.Equal("passed", webhook["status"]!.GetValue<string>());
        Assert.Equal("Prod health", webhook["monitor"]!.GetValue<string>());
    }
}

[Collection("Console")]
public class CliMonitorTests
{
    [Fact]
    public async Task Monitor_once_runs_due_monitors_and_posts_a_webhook_alert()
    {
        var alerts = new List<string>();
        var gate = new SemaphoreSlim(0);
        await using var api = await TestServer.StartAsync(app =>
        {
            app.MapGet("/down", () => Microsoft.AspNetCore.Http.Results.StatusCode(500));
            app.MapPost("/hook", async (Microsoft.AspNetCore.Http.HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                alerts.Add(await reader.ReadToEndAsync());
                gate.Release();
            });
        });

        var dir = Cli.TempDir();
        var db = Path.Combine(dir, "db");
        var services = Dispatch.Cli.Program.BuildServices(db);
        await services.GetRequiredService<Dispatch.Infrastructure.Persistence.DatabaseInitializer>().InitializeAsync();
        var collections = services.GetRequiredService<ICollectionRepository>();
        var monitors = services.GetRequiredService<IMonitorRepository>();
        var collection = new RequestCollection { Name = "Health", Requests = [] };
        await collections.AddAsync(collection);
        var req = new ApiRequest { Name = "Down", Url = $"{api.BaseUrl}/down", CollectionId = collection.Id,
            Assertions = [new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "200" }] };
        await collections.SaveRequestAsync(req);
        await monitors.SaveAsync(new MonitorDefinition
        {
            Name = "Health", CollectionId = collection.Id, IntervalMinutes = 1,
            Alerts = [new AlertTarget { Channel = AlertChannel.Webhook, Trigger = AlertTrigger.OnFailure, Target = $"{api.BaseUrl}/hook" }]
        });
        await services.DisposeAsync();

        var (exit, output) = await Cli.RunAsync("monitor", "--once", "--db", db, "--no-color");
        Assert.Equal(Dispatch.Cli.Program.ExitTestsFailed, exit); // the monitored request failed
        Assert.Contains("Health", output);
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)), "no webhook alert received");
        Assert.Contains("\"status\":\"failed\"", alerts[0]);
    }
}
