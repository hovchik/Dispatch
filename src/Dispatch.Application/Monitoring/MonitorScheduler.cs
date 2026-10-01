using Dispatch.Domain;

namespace Dispatch.Application.Monitoring;

/// <summary>Decides when each enabled monitor is next due, for a daemon loop.</summary>
public sealed class MonitorScheduler
{
    private readonly Dictionary<Guid, DateTimeOffset> _next = new();
    private readonly Dictionary<Guid, bool> _lastPassed = new();

    /// <summary>Call once per monitor before looping to seed the first due time.</summary>
    public void Prime(MonitorDefinition monitor, DateTimeOffset now)
    {
        _next[monitor.Id] = DueAfter(monitor, now, firstRun: true);
    }

    public bool? LastResult(Guid monitorId) => _lastPassed.TryGetValue(monitorId, out var v) ? v : null;

    public DateTimeOffset? NextDue(Guid monitorId) => _next.TryGetValue(monitorId, out var v) ? v : null;

    /// <summary>True when the monitor is due at <paramref name="now"/>; advances its next-due time when it is.</summary>
    public bool IsDue(MonitorDefinition monitor, DateTimeOffset now)
    {
        if (!_next.TryGetValue(monitor.Id, out var due))
        {
            Prime(monitor, now);
            return false;
        }
        if (now < due)
            return false;
        _next[monitor.Id] = DueAfter(monitor, now, firstRun: false);
        return true;
    }

    public void Record(Guid monitorId, bool passed) => _lastPassed[monitorId] = passed;

    private static DateTimeOffset DueAfter(MonitorDefinition monitor, DateTimeOffset now, bool firstRun)
    {
        if (monitor.ScheduleKind == ScheduleKind.Cron && CronSchedule.TryParse(monitor.Cron, out var cron))
            return cron!.Next(now) ?? now.AddMinutes(Math.Max(1, monitor.IntervalMinutes));
        var minutes = Math.Max(1, monitor.IntervalMinutes);
        // Interval monitors run immediately on the first tick, then every N minutes.
        return firstRun ? now : now.AddMinutes(minutes);
    }
}
