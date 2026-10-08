using System.Globalization;

namespace Dispatch.Application.Monitoring;

/// <summary>
/// A 5-field cron expression (minute hour day-of-month month day-of-week). Supports <c>*</c>, lists (<c>1,2</c>),
/// ranges (<c>1-5</c>) and steps (<c>*/5</c>, <c>10-30/5</c>). Day-of-week 0 and 7 are both Sunday.
/// </summary>
public sealed class CronSchedule
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32]; // 1..31
    private readonly bool[] _months = new bool[13];      // 1..12
    private readonly bool[] _daysOfWeek = new bool[7];   // 0..6 (Sun..Sat)

    private CronSchedule() { }

    public static CronSchedule Parse(string expression)
    {
        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new FormatException("A cron expression needs 5 fields: minute hour day-of-month month day-of-week.");
        var schedule = new CronSchedule();
        Fill(fields[0], 0, 59, schedule._minutes);
        Fill(fields[1], 0, 23, schedule._hours);
        Fill(fields[2], 1, 31, schedule._daysOfMonth);
        Fill(fields[3], 1, 12, schedule._months);
        FillDaysOfWeek(fields[4], schedule._daysOfWeek);
        return schedule;
    }

    public static bool TryParse(string expression, out CronSchedule? schedule)
    {
        try
        {
            schedule = Parse(expression);
            return true;
        }
        catch (FormatException)
        {
            schedule = null;
            return false;
        }
    }

    /// <summary>True when <paramref name="time"/> (to the minute) matches. Day-of-month and day-of-week follow cron's
    /// OR rule: when both are restricted, either matching counts.</summary>
    public bool Matches(DateTimeOffset time)
    {
        var local = time;
        if (!_minutes[local.Minute] || !_hours[local.Hour] || !_months[local.Month])
            return false;
        var dom = _daysOfMonth[local.Day];
        var dow = _daysOfWeek[(int)local.DayOfWeek];
        var domRestricted = Array.IndexOf(_daysOfMonth, false, 1, 31) >= 0;
        var dowRestricted = Array.IndexOf(_daysOfWeek, false) >= 0;
        return (domRestricted && dowRestricted) ? dom || dow : dom && dow;
    }

    /// <summary>The next minute at or after <paramref name="after"/> (exclusive) that matches, within a year.</summary>
    public DateTimeOffset? Next(DateTimeOffset after)
    {
        var t = new DateTimeOffset(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Offset).AddMinutes(1);
        var limit = after.AddYears(1);
        while (t < limit)
        {
            if (Matches(t))
                return t;
            t = t.AddMinutes(1);
        }
        return null;
    }

    private static void Fill(string field, int min, int max, bool[] set)
    {
        foreach (var part in field.Split(','))
        {
            var step = 1;
            var body = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                step = ParseInt(part[(slash + 1)..]);
                body = part[..slash];
                if (step <= 0)
                    throw new FormatException("Step must be positive.");
            }

            int from, to;
            if (body is "*" or "")
            {
                (from, to) = (min, max);
            }
            else if (body.Contains('-'))
            {
                var range = body.Split('-');
                from = ParseInt(range[0]);
                to = ParseInt(range[1]);
            }
            else
            {
                from = to = ParseInt(body);
            }

            if (from < min || to > max || from > to)
                throw new FormatException($"Value out of range ({min}-{max}): '{part}'.");
            for (var i = from; i <= to; i += step)
                set[i] = true;
        }
    }

    /// <summary>int.Parse that reports overflow as a <see cref="FormatException"/>, so TryParse rejects it instead of crashing.</summary>
    private static int ParseInt(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new FormatException($"Invalid number: '{text}'.");

    private static void FillDaysOfWeek(string field, bool[] set)
    {
        // Normalise 7 → 0 (Sunday) before parsing into a 0..6 set.
        var scratch = new bool[8];
        Fill(field, 0, 7, scratch);
        for (var i = 0; i <= 7; i++)
            if (scratch[i])
                set[i % 7] = true;
    }
}
