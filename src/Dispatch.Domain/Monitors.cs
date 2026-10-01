namespace Dispatch.Domain;

public enum ScheduleKind
{
    /// <summary>Run every N minutes.</summary>
    Interval,

    /// <summary>Run on a cron expression (minute hour day month day-of-week).</summary>
    Cron
}

public enum AlertChannel
{
    Webhook,
    Slack,
    Email
}

public enum AlertTrigger
{
    /// <summary>Alert whenever a run finishes (good for heartbeats).</summary>
    EveryRun,

    /// <summary>Alert only when a run fails.</summary>
    OnFailure,

    /// <summary>Alert when a run fails, and once again when it recovers.</summary>
    OnChange
}

/// <summary>Where a monitor sends its alerts.</summary>
public sealed class AlertTarget
{
    public bool Enabled { get; set; } = true;
    public AlertChannel Channel { get; set; } = AlertChannel.Webhook;
    public AlertTrigger Trigger { get; set; } = AlertTrigger.OnFailure;

    /// <summary>Webhook/Slack URL, or an SMTP recipient list for email.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Email only: SMTP configuration, host:port, from address.</summary>
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public bool SmtpUseTls { get; set; } = true;

    public AlertTarget Clone() => (AlertTarget)MemberwiseClone();
}

/// <summary>A saved schedule that runs a collection (or a subset) and alerts on the result.</summary>
public sealed class MonitorDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Monitor";
    public bool Enabled { get; set; } = true;
    public Guid? CollectionId { get; set; }
    public Guid? EnvironmentId { get; set; }

    /// <summary>Requests to run (by name); empty means the whole collection.</summary>
    public List<string> RequestNames { get; set; } = [];

    public ScheduleKind ScheduleKind { get; set; } = ScheduleKind.Interval;
    public int IntervalMinutes { get; set; } = 5;
    public string Cron { get; set; } = "*/5 * * * *";

    public List<AlertTarget> Alerts { get; set; } = [];

    /// <summary>Fail the run (and alert) when the average response time exceeds this, in ms (0 = no limit).</summary>
    public int MaxAverageMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MonitorDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        CollectionId = CollectionId,
        EnvironmentId = EnvironmentId,
        RequestNames = [.. RequestNames],
        ScheduleKind = ScheduleKind,
        IntervalMinutes = IntervalMinutes,
        Cron = Cron,
        Alerts = Alerts.Select(a => a.Clone()).ToList(),
        MaxAverageMs = MaxAverageMs,
        CreatedAt = CreatedAt
    };
}

/// <summary>The outcome of one monitor run, kept as history.</summary>
public sealed class MonitorRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MonitorId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public double DurationMs { get; set; }
    public bool Passed { get; set; }
    public int TotalRequests { get; set; }
    public int FailedRequests { get; set; }
    public int TotalTests { get; set; }
    public int FailedTests { get; set; }
    public double AverageMs { get; set; }
    public string Summary { get; set; } = string.Empty;
}
