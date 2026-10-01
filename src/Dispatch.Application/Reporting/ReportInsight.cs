namespace Dispatch.Application.Reporting;

public enum InsightLevel
{
    Info,
    Good,
    Warning,
    Bad
}

/// <summary>A plain-language observation about a run ("p95 grew 3× during the test", "all requests passed").</summary>
public sealed record ReportInsight(InsightLevel Level, string Text)
{
    public string Css => Level.ToString().ToLowerInvariant();
}
