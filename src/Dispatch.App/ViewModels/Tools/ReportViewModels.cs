using System.Globalization;
using Avalonia.Media;
using Dispatch.Application.Reporting;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>A labelled number in a report's headline row.</summary>
public sealed record StatTile(string Label, string Value, string? Hint = null, IBrush? Accent = null)
{
    public bool HasHint => !string.IsNullOrEmpty(Hint);
}

/// <summary>One row of a horizontal bar chart (status codes, latency buckets, hosts…).</summary>
public sealed record ReportBar(string Label, int Count, double Max, IBrush Brush, string? Suffix = null)
{
    public string ValueText => Count.ToString("N0", CultureInfo.CurrentCulture) + Suffix;
}

/// <summary>Colours shared by the report views so verdicts and charts look the same everywhere.</summary>
public static class ReportBrushes
{
    public static readonly IBrush Good = new SolidColorBrush(Color.Parse("#22A06B"));
    public static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#D97706"));
    public static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#EF4444"));
    public static readonly IBrush Info = new SolidColorBrush(Color.Parse("#3B82F6"));
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#2F6BFF"));
    public static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#8B949E"));

    public static IBrush For(InsightLevel level) => level switch
    {
        InsightLevel.Good => Good,
        InsightLevel.Warning => Warning,
        InsightLevel.Bad => Bad,
        _ => Info
    };

    /// <summary>Status code (or "2xx", "error") → bar colour.</summary>
    public static IBrush ForStatus(string status) => status.Length == 0 ? Neutral : status[0] switch
    {
        '2' => Good,
        '3' => Info,
        '4' => Warning,
        '5' => Bad,
        'E' or 'e' => Bad,
        _ => Neutral
    };

    public static IBrush ForVerdict(string verdict) => verdict switch
    {
        "PASSED" or "NO ERRORS" => Good,
        "DEGRADED" => Warning,
        "NO DATA" => Neutral,
        _ => Bad
    };

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB"
    };
}

/// <summary>Insight level → accent brush (left stripe and icon of an insight row).</summary>
public sealed class InsightBrushConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly InsightBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is InsightLevel level ? ReportBrushes.For(level) : ReportBrushes.Neutral;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>Insight level → a one-character glyph.</summary>
public sealed class InsightGlyphConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly InsightGlyphConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        InsightLevel.Good => "✓",
        InsightLevel.Warning => "!",
        InsightLevel.Bad => "✗",
        _ => "i"
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>Byte count → "12.3 KB".</summary>
public sealed class BytesConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly BytesConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long or int ? ReportBrushes.Bytes(System.Convert.ToInt64(value, CultureInfo.InvariantCulture)) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
